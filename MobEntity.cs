using System;

namespace CubeApp
{
    /// <summary>
    /// A snapshot of a mob handed to the renderer each frame: position, yaw, and animation state.
    /// </summary>
    public readonly struct MobInstance
    {
        public readonly float X, Y, Z;
        public readonly float Yaw;
        public readonly float WalkPhase;
        public readonly float WalkAmount;
        public readonly float VelocityY;
        public readonly bool OnGround;
        public readonly bool IsDead;
        public readonly float DeathT, DeathRollDir, HurtTimer;
        public readonly string MobType;

        public MobInstance(float x, float y, float z, float yaw, float walkPhase, float walkAmount,
            float velocityY, bool onGround, bool isDead, float deathT, float deathRollDir, float hurtTimer, string mobType)
        {
            X = x; Y = y; Z = z; Yaw = yaw;
            WalkPhase = walkPhase; WalkAmount = walkAmount;
            VelocityY = velocityY; OnGround = onGround;
            IsDead = isDead; DeathT = deathT;
            DeathRollDir = deathRollDir; HurtTimer = hurtTimer;
            MobType = mobType;
        }
    }

    /// <summary>
    /// Base class for all mobs with full AI (idle, wander, return home, panic, death),
    /// physics, and collision handling.
    /// </summary>
    public abstract class MobEntity : IMobRenderable
    {
        // Collision bounds
        public float Width { get; protected set; } = 0.68f;
        public float Height { get; protected set; } = 1.35f;
        protected float Gravity = 22f;
        protected float MaxSpeed = 4f;
        protected float GroundAccel = 28f;
        protected float AirAccel = 7f;
        protected float JumpSpeed = 7.36f;
        protected float DragGround = 0.965f;
        protected float DragAir = 0.985f;
        protected float DragVertical = 0.992f;
        protected float StepHeight = 0.45f;
        protected double GroundProbe = 0.06;

        public Point3D Position { get; set; }
        public float Yaw { get; set; }
        public bool OnGround { get; set; }
        public bool Removed { get; protected set; }
        public bool IsDead => _dead;

        // Restores saved state after spawning (world load).
        public void RestoreState(Point3D position, float yaw, int health)
        {
            Position = position;
            Yaw = yaw;
            Health = Math.Max(1, health);
        }

        public int Health { get; protected set; } = 10;
        public int MaxHealth { get; protected set; } = 10;

        // IMobRenderable implementation
        string IMobRenderable.MobType => MobTypeName;
        float IMobRenderable.HeadYawLocal => Clamp(WrapAngle(_headYaw - Yaw), -MaxHeadYaw, MaxHeadYaw);

        /// <summary>Model lookup key used by the renderer; defaults to the lowercase class name.</summary>
        protected virtual string MobTypeName => GetType().Name.ToLowerInvariant();
        float IMobRenderable.WalkPhase => _walkPhase;
        float IMobRenderable.WalkAmount => _walkAmount;
        float IMobRenderable.FlapPhase => 0f; // Base class doesn't have flight
        float IMobRenderable.VelocityY => (float)_velY;
        float IMobRenderable.DeathT => _dead ? Math.Clamp(_deathTimer / Math.Max(0.001f, _deathDuration), 0f, 1f) : 0f;
        float IMobRenderable.DeathRollDir => _deathRollDir;
        float IMobRenderable.HurtTimer => _hurtTimer;

        protected double _velX, _velY, _velZ;
        protected bool _prevOnGround;
        protected readonly double _homeX, _homeZ;
        protected float _walkPhase, _walkAmount;
        protected float _hurtTimer;
        protected bool _dead;
        protected float _deathTimer;
        protected float _deathDuration = 0.5f;
        protected float _deathRollDir = 1f;

        // AI state
        private float _aiTimer, _actionTimer, _idleTimer;
        private float _hopCooldown, _ledgeTurnCooldown, _jumpPressCooldown;
        private float _lookRetargetTimer, _afterMoveRestTimer;
        private float _panicTimer, _panicRetargetTimer, _invulnerableTimer;
        private float _targetYaw;
        protected float _velXAI, _velZAI;
        private double _goalX, _goalZ;
        private float _goalUrgency = 0.55f;
        private float _desiredMoveForward, _currentMoveForward;
        private float _desiredSpeedScale = 0.92f, _currentSpeedScale = 0.92f;
        private double _panicSourceX, _panicSourceZ;
        private float _ctrlForward, _ctrlStrafe;
        private bool _ctrlJump;
        private bool _pendingJump;
        private float _pendingJumpSpeed;

        private const float HitInvulnDuration = 1f / 3f;
        private const float PanicDurationMin = 2.8f;
        private const float PanicDurationMax = 4.2f;
        private static readonly float MaxHeadYaw = 75f * (float)Math.PI / 180f;
        private static readonly float HeadTurnSpeed = 220f * (float)Math.PI / 180f;
        private static readonly float BodyTurnSpeedMoving = 320f * (float)Math.PI / 180f;
        private static readonly float BodyTurnSpeedIdle = 120f * (float)Math.PI / 180f;
        private const float BodyAlignWait = 0.5f;
        private const float BodyAlignFull = 1.0f;

        private enum Behavior { Idle, Look, Stroll, ReturnHome, Panic, Dead }
        private Behavior _behavior = Behavior.Idle;
        private float _bodyAlignDelay;
        private float _lastHeadYaw;
        private float _headYaw;

        public MobEntity(Point3D position, float yaw)
        {
            Position = position;
            Yaw = yaw;
            _headYaw = yaw;
            _lastHeadYaw = yaw;
            _targetYaw = yaw;
            _homeX = position.X;
            _homeZ = position.Z;
            _panicSourceX = position.X;
            _panicSourceZ = position.Z;
            _prevOnGround = false;
        }

        /// <summary>
        /// Update the mob's AI and physics.
        /// </summary>
        public virtual void Update(float dt, ChunkManager manager)
        {
            _prevOnGround = OnGround;
            OnGround = false;
            _invulnerableTimer = Math.Max(0f, _invulnerableTimer - dt);
            UpdateBrain(dt, manager);
            if (Removed) return;
            UpdatePhysics(dt, manager);
        }

        /// <summary>
        /// Apply damage from an attacker at (srcX, srcZ).
        /// </summary>
        public virtual bool Damage(int amount, double srcX, double srcZ)
        {
            if (_dead || amount <= 0) return false;
            if (_invulnerableTimer > 0f) return false;

            Health = Math.Max(0, Health - amount);
            _hurtTimer = Math.Max(_hurtTimer, 0.20f);
            _invulnerableTimer = Math.Max(_invulnerableTimer, HitInvulnDuration);

            double dx = Position.X - srcX;
            double dz = Position.Z - srcZ;
            double len = Math.Sqrt(dx * dx + dz * dz);
            if (len < 1e-6) len = 1;
            _velX += dx / len * 1.2;
            _velZ += dz / len * 1.2;

            _panicTimer = Math.Max(_panicTimer, PanicDurationMin + (float)Rng() * (PanicDurationMax - PanicDurationMin));
            _panicRetargetTimer = 0f;
            _idleTimer = 0f;
            _afterMoveRestTimer = 0f;
            _actionTimer = 0f;
            _panicSourceX = srcX;
            _panicSourceZ = srcZ;

            if (Health <= 0)
            {
                _dead = true;
                _deathTimer = 0f;
                _deathDuration = Math.Max(0.46f, _deathDuration);
                _hurtTimer = Math.Max(_hurtTimer, 0.22f);
                _invulnerableTimer = Math.Max(_invulnerableTimer, _deathDuration);
                _behavior = Behavior.Dead;
                _panicTimer = 0f;
                _panicRetargetTimer = 0f;
                _idleTimer = 0f;
                _actionTimer = 0f;
                _afterMoveRestTimer = 0f;
                _currentMoveForward = 0f;
                _desiredMoveForward = 0f;
                _deathRollDir = (Position.X - srcX) >= 0 ? 1f : -1f;
                _ctrlForward = 0f; _ctrlStrafe = 0f; _ctrlJump = false;
            }
            return true;
        }

        public abstract MobInstance ToInstance();

        // ---- AI brain -------------------------------------------------------

        private void UpdateBrain(float dt, ChunkManager manager)
        {
            _ctrlForward = 0f; _ctrlStrafe = 0f; _ctrlJump = false;
            if (_dead) { UpdateDead(dt); return; }

            bool grounded = _prevOnGround || OnGround;
            _aiTimer = Math.Max(0f, _aiTimer - dt);
            _actionTimer = Math.Max(0f, _actionTimer - dt);
            _idleTimer = Math.Max(0f, _idleTimer - dt);
            _hopCooldown = Math.Max(0f, _hopCooldown - dt);
            _ledgeTurnCooldown = Math.Max(0f, _ledgeTurnCooldown - dt);
            _jumpPressCooldown = Math.Max(0f, _jumpPressCooldown - dt);
            _lookRetargetTimer = Math.Max(0f, _lookRetargetTimer - dt);
            _afterMoveRestTimer = Math.Max(0f, _afterMoveRestTimer - dt);
            _hurtTimer = Math.Max(0f, _hurtTimer - dt);
            _panicTimer = Math.Max(0f, _panicTimer - dt);
            _panicRetargetTimer = Math.Max(0f, _panicRetargetTimer - dt);
            _walkPhase += dt * (grounded ? (4.5f + _walkAmount * 4.0f) : 14.0f);

            double homeDx = _homeX - Position.X;
            double homeDz = _homeZ - Position.Z;
            double homeDistSq = homeDx * homeDx + homeDz * homeDz;

            if (_panicTimer <= 0f && _behavior == Behavior.Panic)
            {
                _behavior = Behavior.Idle;
                _actionTimer = 0f;
                _afterMoveRestTimer = Math.Max(_afterMoveRestTimer, 0.8f + (float)Rng() * 1.1f);
            }

            if (_panicTimer > 0f)
            {
                if (_panicRetargetTimer <= 0f || _actionTimer <= 0f)
                    ChoosePanicGoal();
            }
            else if (_actionTimer <= 0f)
            {
                _aiTimer = 1.1f + (float)Rng() * 1.7f;
                ChooseWanderGoal(homeDistSq > 196);
            }

            double goalDx = _goalX - Position.X;
            double goalDz = _goalZ - Position.Z;
            double goalDist = Math.Sqrt(goalDx * goalDx + goalDz * goalDz);

            if (_idleTimer > 0f)
            {
                if (_lookRetargetTimer <= 0f)
                {
                    float lookYaw = Yaw + ((float)Rng() - 0.5f) * (_behavior == Behavior.Look ? (0.85f + (float)Rng() * 0.95f) : (0.22f + (float)Rng() * 0.32f));
                    _targetYaw = lookYaw;
                    _lookRetargetTimer = _behavior == Behavior.Look ? (0.65f + (float)Rng() * 1.2f) : (1.0f + (float)Rng() * 1.3f);
                }
                _desiredMoveForward = 0f; _desiredSpeedScale = 0.9f;
            }
            else
            {
                if (goalDist > 0.001) _targetYaw = (float)Math.Atan2(goalDx, goalDz);
                float urgency = Clamp(_goalUrgency, 0f, 1f);
                float distFactor = Clamp((float)goalDist / 3.8f, 0f, 1f);
                float desiredForward = Clamp(0.34f + urgency * 0.22f + distFactor * 0.28f, 0f, 1.0f);
                if (goalDist < 0.9) desiredForward *= Clamp(((float)goalDist - 0.16f) / 0.74f, 0f, 1f);
                if (goalDist < 0.26)
                {
                    if (_behavior == Behavior.Stroll) _afterMoveRestTimer = Math.Max(_afterMoveRestTimer, 1.4f + (float)Rng() * 2.4f);
                    if (_behavior == Behavior.ReturnHome) _afterMoveRestTimer = Math.Max(_afterMoveRestTimer, 0.9f + (float)Rng() * 1.5f);
                    _actionTimer = 0f; desiredForward = 0f;
                }
                _desiredMoveForward = desiredForward;
                _desiredSpeedScale = Clamp(0.92f + urgency * 0.05f + distFactor * 0.08f, 0.92f, 1.06f);
            }

            bool needsStepJump = grounded && NeedsStepJump(manager, 0.6);
            bool facingObstacle = SolidAhead(manager, 0.6);
            bool blockedByWall = facingObstacle && !needsStepJump;
            bool facingDrop = grounded && DangerousDropAhead(manager, 0.82);

            if (_idleTimer <= 0f && (blockedByWall || facingDrop) && _ledgeTurnCooldown <= 0f)
            {
                _behavior = Behavior.Stroll;
                _targetYaw = Yaw + (Rng() < 0.5 ? -1f : 1f) * (0.9f + (float)Rng() * 1.2f);
                double sidestepDist = facingDrop ? (0.9 + Rng() * 0.45) : (0.7 + Rng() * 0.65);
                _goalX = Position.X + Math.Sin(_targetYaw) * sidestepDist;
                _goalZ = Position.Z + Math.Cos(_targetYaw) * sidestepDist;
                _goalUrgency = facingDrop ? 0.65f : 0.5f;
                _desiredMoveForward = facingDrop ? (0.40f + (float)Rng() * 0.10f) : (0.52f + (float)Rng() * 0.18f);
                _desiredSpeedScale = facingDrop ? 0.96f : 1.0f;
                _actionTimer = 0.45f + (float)Rng() * 0.55f;
                _idleTimer = 0f;
                _ledgeTurnCooldown = 0.35f + (float)Rng() * 0.25f;
            }
            else if (_idleTimer <= 0f && needsStepJump && _hopCooldown <= 0f)
            {
                _ctrlJump = true; _pendingJump = true; _pendingJumpSpeed = 7.42f; _hopCooldown = 0.26f;
            }

            _currentMoveForward = Lerp(_currentMoveForward, _idleTimer > 0f ? 0f : _desiredMoveForward, 1f - (float)Math.Exp(-dt * 4.6));
            _currentSpeedScale = Lerp(_currentSpeedScale, _idleTimer > 0f ? 0.9f : _desiredSpeedScale, 1f - (float)Math.Exp(-dt * 3.9));

            _headYaw = TurnToward(_headYaw, _targetYaw, HeadTurnSpeed * dt);
            float moveIntent = _currentMoveForward;
            UpdateBodyYawFromHead(dt, moveIntent > 0.04f);

            float yawError = WrapAngle(_targetYaw - Yaw);
            float turnSlow = Math.Abs(yawError) > 1.2f ? 0.22f : (Math.Abs(yawError) > 0.75f ? 0.72f : 1.0f);
            _ctrlForward = moveIntent * turnSlow;
            _ctrlStrafe = moveIntent > 0.04f ? Clamp(yawError * 0.14f, -0.18f, 0.18f) : 0f;
        }

        private void UpdateDead(float dt)
        {
            _hurtTimer = Math.Max(0f, _hurtTimer - dt);
            _deathTimer += dt;
            _currentMoveForward = Lerp(_currentMoveForward, 0f, 1f - (float)Math.Exp(-dt * 10.0));
            _currentSpeedScale = Lerp(_currentSpeedScale, 1f, 1f - (float)Math.Exp(-dt * 10.0));
            _headYaw = Yaw;
            _velX *= Math.Pow(0.60, dt * 60);
            _velZ *= Math.Pow(0.60, dt * 60);
            if (_deathTimer >= _deathDuration) Removed = true;
        }

        private void ChooseWanderGoal(bool forceHome)
        {
            double homeDx = _homeX - Position.X, homeDz = _homeZ - Position.Z;
            double homeDistSq = homeDx * homeDx + homeDz * homeDz;

            if (forceHome || homeDistSq > 196)
            {
                double homeDist = Math.Sqrt(homeDistSq);
                double targetDist = Math.Min(6.25, Math.Max(2.4, homeDist));
                _behavior = Behavior.ReturnHome;
                _afterMoveRestTimer = 0f; _idleTimer = 0f;
                _actionTimer = 1.5f + (float)Rng() * 1.4f + (float)targetDist * 0.26f;
                _goalUrgency = 1.0f;
                _targetYaw = (float)Math.Atan2(homeDx, homeDz) + ((float)Rng() - 0.5f) * 0.18f;
                _goalX = Position.X + Math.Sin(_targetYaw) * targetDist;
                _goalZ = Position.Z + Math.Cos(_targetYaw) * targetDist;
                _desiredMoveForward = 0.68f; _desiredSpeedScale = 1.06f;
                return;
            }

            if (_afterMoveRestTimer > 0f) { _afterMoveRestTimer = 0f; SetIdleGoal(true); return; }
            if (Rng() < 0.64) { SetIdleGoal(false); return; }

            double wanderDist = 0.85 + Rng() * 3.4;
            float urgency = 0.26f + (float)Rng() * 0.34f;
            float yaw = _headYaw + ((float)Rng() - 0.5f) * (1.05f + (float)Rng() * 0.9f);
            if (Rng() < 0.12) yaw = (float)(Rng() * Math.PI * 2);
            _behavior = Behavior.Stroll; _idleTimer = 0f;
            _actionTimer = 1.0f + (float)Rng() * 1.55f + (float)wanderDist * 0.34f;
            _goalUrgency = urgency;
            _goalX = Position.X + Math.Sin(yaw) * wanderDist;
            _goalZ = Position.Z + Math.Cos(yaw) * wanderDist;
            _targetYaw = yaw;
            _desiredMoveForward = Clamp(0.22f + urgency * 0.18f + (float)Math.Min(1, wanderDist / 4.5) * 0.14f, 0.2f, 0.56f);
            _desiredSpeedScale = Clamp(0.93f + urgency * 0.06f + (float)Math.Min(1, wanderDist / 5.0) * 0.08f, 0.93f, 1.05f);
            _lookRetargetTimer = 0.45f + (float)Rng() * 0.75f;
        }

        private void ChoosePanicGoal()
        {
            float awayYaw = Yaw + (float)Math.PI;
            double awayX = Position.X - _panicSourceX, awayZ = Position.Z - _panicSourceZ;
            if ((awayX * awayX + awayZ * awayZ) > 1e-4) awayYaw = (float)Math.Atan2(awayX, awayZ);
            float yaw = awayYaw + ((float)Rng() - 0.5f) * (0.65f + (float)Rng() * 0.7f);
            double panicDist = 2.8 + Rng() * 3.0;
            _behavior = Behavior.Panic; _idleTimer = 0f; _afterMoveRestTimer = 0f;
            _goalUrgency = 1.0f; _targetYaw = yaw;
            _goalX = Position.X + Math.Sin(yaw) * panicDist;
            _goalZ = Position.Z + Math.Cos(yaw) * panicDist;
            _desiredMoveForward = 0.76f + (float)Rng() * 0.12f; _desiredSpeedScale = 1.0f;
            _actionTimer = 0.40f + (float)Rng() * 0.38f;
            _lookRetargetTimer = 0.18f + (float)Rng() * 0.22f;
            _panicRetargetTimer = 0.28f + (float)Rng() * 0.28f;
        }

        private void SetIdleGoal(bool strongRest)
        {
            bool glance = Rng() < (strongRest ? 0.8 : 0.68);
            float duration = (strongRest ? 1.9f : 1.1f) + (float)Rng() * (strongRest ? 3.2f : 2.7f);
            float lookYaw = Yaw + ((float)Rng() - 0.5f) * (glance ? (0.9f + (float)Rng() * 0.9f) : (0.24f + (float)Rng() * 0.34f));
            _behavior = glance ? Behavior.Look : Behavior.Idle;
            _idleTimer = duration; _actionTimer = duration;
            _goalX = Position.X; _goalZ = Position.Z;
            _goalUrgency = 0f; _desiredMoveForward = 0f; _desiredSpeedScale = 0.9f;
            _targetYaw = lookYaw;
            _lookRetargetTimer = glance ? (0.7f + (float)Rng() * 1.35f) : (1.2f + (float)Rng() * 1.4f);
        }

        private void UpdateBodyYawFromHead(float dt, bool moving)
        {
            float headDelta = Math.Abs(WrapAngle(_headYaw - _lastHeadYaw));
            if (moving)
            {
                _bodyAlignDelay = 0f; _lastHeadYaw = _headYaw;
                Yaw = TurnToward(Yaw, _headYaw, BodyTurnSpeedMoving * dt);
            }
            else
            {
                float diff = WrapAngle(_headYaw - Yaw);
                if (Math.Abs(diff) > MaxHeadYaw) { Yaw = _headYaw - Math.Sign(diff) * MaxHeadYaw; _bodyAlignDelay = 0f; _lastHeadYaw = _headYaw; }
                else if (headDelta > 0.5f * (float)Math.PI / 180f) { _bodyAlignDelay = 0f; _lastHeadYaw = _headYaw; }
                else
                {
                    _bodyAlignDelay += dt;
                    if (_bodyAlignDelay > BodyAlignWait)
                    {
                        float t = Math.Min(1f, (_bodyAlignDelay - BodyAlignWait) / Math.Max(0.0001f, BodyAlignFull - BodyAlignWait));
                        Yaw = TurnToward(Yaw, _headYaw, Lerp(0f, BodyTurnSpeedIdle, t) * dt);
                    }
                }
            }
            float clampedDiff = Clamp(WrapAngle(_headYaw - Yaw), -MaxHeadYaw, MaxHeadYaw);
            _headYaw = Yaw + clampedDiff;
        }

        // ---- Physics + collision -------------------------------------------

        private void UpdatePhysics(float dt, ChunkManager manager)
        {
            ApplyMoveControls(dt);
            _velY -= Gravity * dt;
            if (_velY < 0) { _velY *= Math.Pow(0.82, dt * 60); if (_velY < -2.15) _velY = -2.15; }

            MoveAxis(manager, Axis.X, _velX * dt);
            MoveAxis(manager, Axis.Z, _velZ * dt);
            MoveAxis(manager, Axis.Y, _velY * dt);

            if (!OnGround && _velY <= 0 && IntersectsSolid(manager, Position.X, Position.Y - GroundProbe, Position.Z))
            {
                OnGround = true; _velY = 0;
            }

            double hDrag = Math.Pow(OnGround ? DragGround : DragAir, dt * 60);
            double vDrag = Math.Pow(DragVertical, dt * 60);
            _velX *= hDrag; _velZ *= hDrag; _velY *= vDrag;
        }

        private void ApplyMoveControls(float dt)
        {
            float forward = Clamp(_ctrlForward, -1f, 1f);
            float strafe = Clamp(_ctrlStrafe, -1f, 1f);
            double inputLen = Math.Sqrt(forward * forward + strafe * strafe);
            if (inputLen > 1e-5)
            {
                double inv = inputLen > 1 ? 1 / inputLen : 1;
                forward = (float)(forward * inv); strafe = (float)(strafe * inv);
                double sinYaw = Math.Sin(Yaw), cosYaw = Math.Cos(Yaw);
                double wishX = sinYaw * forward + cosYaw * strafe;
                double wishZ = cosYaw * forward - sinYaw * strafe;
                bool grounded = _prevOnGround || OnGround;
                float accelBase = grounded ? GroundAccel : AirAccel;
                float accel = accelBase * Lerp(0.9f, 1.12f, Clamp((_currentSpeedScale - 0.85f) / 0.3f, 0f, 1f));
                _velX += wishX * accel * dt; _velZ += wishZ * accel * dt;
            }

            if (_ctrlJump && (_prevOnGround || OnGround) && _jumpPressCooldown <= 0f)
            {
                _velY = Math.Max(_velY, _pendingJump ? _pendingJumpSpeed : JumpSpeed);
                _pendingJump = false; _jumpPressCooldown = 0.12f;
            }

            float maxSpeed = MaxSpeed * _currentSpeedScale;
            double speedSq = _velX * _velX + _velZ * _velZ;
            if (speedSq > maxSpeed * maxSpeed)
            {
                double inv = maxSpeed / Math.Sqrt(speedSq);
                _velX *= inv; _velZ *= inv;
            }

            double horizontalSpeed = Math.Sqrt(_velX * _velX + _velZ * _velZ);
            _walkAmount = (float)Math.Min(1, horizontalSpeed / Math.Max(0.001, maxSpeed));
            _walkPhase += dt * ((_prevOnGround || OnGround) ? (_walkAmount * 8.5f + 1.9f) : 14.0f);
        }

        protected enum Axis { X, Y, Z }

        private void MoveAxis(ChunkManager manager, Axis axis, double amount)
        {
            const double baseStep = 0.05;
            double remaining = amount;
            int safety = 0;
            while (Math.Abs(remaining) > 0.0001)
            {
                if (++safety > 96) { SetVel(axis, 0); return; }
                double delta = Math.Sign(remaining) * Math.Min(Math.Abs(remaining), baseStep);
                double nx = Position.X, ny = Position.Y, nz = Position.Z;
                switch (axis) { case Axis.X: nx += delta; break; case Axis.Y: ny += delta; break; case Axis.Z: nz += delta; break; }
                if (!IntersectsSolid(manager, nx, ny, nz))
                {
                    Position = new Point3D(nx, ny, nz);
                    remaining -= delta; continue;
                }
                if (axis != Axis.Y && TryStep(manager, axis, delta)) { remaining -= delta; continue; }
                if (axis == Axis.Y && delta < 0) OnGround = true;
                SetVel(axis, 0); return;
            }
        }

        private bool TryStep(ChunkManager manager, Axis axis, double delta)
        {
            if (StepHeight <= 0 || !OnGround) return false;
            double bx = Position.X, by = Position.Y + StepHeight, bz = Position.Z;
            switch (axis) { case Axis.X: bx += delta; break; case Axis.Z: bz += delta; break; }
            if (IntersectsSolid(manager, bx, by, bz)) return false;
            Position = new Point3D(bx, by, bz); OnGround = true; return true;
        }

        private void SetVel(Axis axis, double value)
        {
            switch (axis) { case Axis.X: _velX = value; break; case Axis.Y: _velY = value; break; case Axis.Z: _velZ = value; break; }
        }

        protected bool IntersectsSolid(ChunkManager manager, double px, double py, double pz)
        {
            double halfW = Width * 0.5;
            double minX = px - halfW, maxX = px + halfW;
            double minY = py, maxY = py + Height;
            double minZ = pz - halfW, maxZ = pz + halfW;
            int x0 = (int)Math.Floor(minX), x1 = (int)Math.Floor(maxX);
            int y0 = (int)Math.Floor(minY), y1 = (int)Math.Floor(maxY - 0.001);
            int z0 = (int)Math.Floor(minZ), z1 = (int)Math.Floor(maxZ);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    for (int z = z0; z <= z1; z++)
                        if (IsSolid(manager, x, y, z)) return true;
            return false;
        }

        private static bool IsSolid(ChunkManager manager, int x, int y, int z)
            => manager.TryGetLoadedBlock(x, y, z, out var block) && block != BlockRegistry.AirId;

        private bool SolidAhead(ChunkManager manager, double distance)
        {
            double dirX = Math.Sin(Yaw), dirZ = Math.Cos(Yaw);
            int probeX = (int)Math.Floor(Position.X + dirX * distance);
            int probeZ = (int)Math.Floor(Position.Z + dirZ * distance);
            int baseY = (int)Math.Floor(Position.Y + 0.05);
            return IsSolid(manager, probeX, baseY, probeZ) || IsSolid(manager, probeX, baseY + 1, probeZ);
        }

        private bool NeedsStepJump(ChunkManager manager, double distance)
        {
            double dirX = Math.Sin(Yaw), dirZ = Math.Cos(Yaw);
            int probeX = (int)Math.Floor(Position.X + dirX * distance);
            int probeZ = (int)Math.Floor(Position.Z + dirZ * distance);
            int baseY = (int)Math.Floor(Position.Y + 0.05);
            return IsSolid(manager, probeX, baseY, probeZ) && !IsSolid(manager, probeX, baseY + 1, probeZ) && !IsSolid(manager, probeX, baseY + 2, probeZ);
        }

        private bool DangerousDropAhead(ChunkManager manager, double distance)
        {
            double dirX = Math.Sin(Yaw), dirZ = Math.Cos(Yaw);
            int probeX = (int)Math.Floor(Position.X + dirX * distance);
            int probeZ = (int)Math.Floor(Position.Z + dirZ * distance);
            int baseY = (int)Math.Floor(Position.Y + 0.05);
            int aheadGroundY = FindGroundYAt(manager, probeX, probeZ, baseY);
            return aheadGroundY < 0 || (aheadGroundY + 1) < (Position.Y - 1.05);
        }

        private static int FindGroundYAt(ChunkManager manager, int x, int z, int startY)
        {
            int bottom = Math.Max(0, startY - 24);
            for (int y = startY; y >= bottom; y--) if (IsSolid(manager, x, y, z)) return y;
            return -1;
        }

        // ---- Math helpers ---------------------------------------------------

        private static double Rng() => Random.Shared.NextDouble();
        protected static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
        protected static float Lerp(float a, float b, float t) => a + (b - a) * t;
        protected static float WrapAngle(float angle)
        {
            while (angle > Math.PI) angle -= (float)(Math.PI * 2);
            while (angle < -Math.PI) angle += (float)(Math.PI * 2);
            return angle;
        }
        private static float TurnToward(float current, float target, float maxStep)
        {
            float delta = WrapAngle(target - current);
            return Math.Abs(delta) <= maxStep ? target : current + Math.Sign(delta) * maxStep;
        }
    }

    // Coyote inherits MobEntity with default wander AI
    public sealed class Coyote : MobEntity
    {
        public Coyote(Point3D position, float yaw) : base(position, yaw)
        {
            Width = 0.9f;
            Height = 1.2f;
            MaxHealth = 10;
            Health = MaxHealth;
        }

        public override MobInstance ToInstance()
        {
            return new MobInstance(
                (float)Position.X, (float)Position.Y, (float)Position.Z,
                Yaw, _walkPhase, _walkAmount,
                (float)_velY, OnGround, _dead,
                _deathTimer / Math.Max(0.001f, _deathDuration), _deathRollDir, _hurtTimer,
                "coyote");
        }
    }
}