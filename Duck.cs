using System;

namespace CubeApp
{
    /// <summary>
    /// A snapshot of a duck handed to the renderer each frame: where it is, which way it faces and
    /// the animation state needed to pose its bones (walk cycle, wing flap, head turn, hurt/death).
    /// Kept separate from <see cref="Duck"/> so rendering never touches sim state.
    /// </summary>
    public readonly struct DuckInstance
    {
        public readonly Point3D Position;   // feet position (model origin) in world space
        public readonly float Yaw;          // radians, body rotation about +Y
        public readonly float HeadYawLocal; // radians, head yaw relative to body (already clamped)
        public readonly float WalkPhase;    // radians, drives leg/wing swing
        public readonly float WalkAmount;   // 0..1, how briskly it's walking
        public readonly float FlapPhase;    // radians, drives in-air wing flap
        public readonly float VelocityY;    // vertical velocity (for in-air tilt)
        public readonly bool OnGround;
        public readonly bool IsDead;
        public readonly float DeathT;       // 0..1 death animation progress
        public readonly float DeathRollDir; // +1/-1 roll direction on death
        public readonly float HurtTimer;    // seconds of remaining hurt flash

        public DuckInstance(
            Point3D position, float yaw, float headYawLocal,
            float walkPhase, float walkAmount, float flapPhase,
            float velocityY, bool onGround,
            bool isDead, float deathT, float deathRollDir, float hurtTimer)
        {
            Position = position;
            Yaw = yaw;
            HeadYawLocal = headYawLocal;
            WalkPhase = walkPhase;
            WalkAmount = walkAmount;
            FlapPhase = flapPhase;
            VelocityY = velocityY;
            OnGround = onGround;
            IsDead = isDead;
            DeathT = deathT;
            DeathRollDir = deathRollDir;
            HurtTimer = hurtTimer;
        }
    }

    /// <summary>
    /// The duck test-mob, ported from Cubuild: it wanders (idle / look / stroll / return-home),
    /// panics and flees when hit, avoids walking off ledges, hops small steps, and plays a walk /
    /// idle / flap / hurt / death animation. Water behaviour from Cubuild is omitted because this
    /// world has no fluid blocks. Position is the feet point; the collision box is centred on that
    /// point horizontally and rises <see cref="Height"/> above it.
    /// </summary>
    public sealed class Duck
    {
        public const float Width = 0.68f;   // full width/depth of the collision box (blocks)
        public const float Height = 1.35f;
        public const float Gravity = 22f;   // blocks / s^2 (matches Cubuild's test mob)
        private const float StepHeight = 0.45f;
        private const double GroundProbe = 0.06; // ground-contact tolerance below the feet (blocks)

        // Movement / drag (Cubuild test-mob data block).
        private const float MaxSpeed = 4f;
        private const float GroundAccel = 28f;
        private const float AirAccel = 7f;
        private const float JumpSpeed = 7.36f;
        private const float DragGround = 0.965f;
        private const float DragAir = 0.985f;
        private const float DragVertical = 0.992f;

        // Health / combat.
        public const int MaxHealth = 8;
        private const float HitInvulnDuration = 1f / 3f;
        private const float PanicDurationMin = 2.8f;
        private const float PanicDurationMax = 4.2f;

        // Head / body turning (Cubuild constants, radians).
        private static readonly float MaxHeadYaw = Deg(75f);
        private static readonly float HeadTurnSpeed = Deg(220f);
        private static readonly float BodyTurnSpeedMoving = Deg(320f);
        private static readonly float BodyTurnSpeedIdle = Deg(120f);
        private const float BodyAlignWait = 0.5f;
        private const float BodyAlignFull = 1.0f;

        private enum Behavior { Idle, Look, Stroll, ReturnHome, Panic, Dead }

        public Point3D Position;
        public float Yaw;
        public float HeadYaw;
        public bool OnGround;
        public bool Removed { get; private set; }

        public int Health { get; private set; } = MaxHealth;
        public bool IsDead => _dead;

        private double _velX, _velY, _velZ;
        private bool _prevOnGround;

        // AI / behaviour state.
        private Behavior _behavior = Behavior.Idle;
        private float _aiTimer, _actionTimer, _idleTimer;
        private float _hopCooldown, _ledgeTurnCooldown, _jumpPressCooldown;
        private float _lookRetargetTimer, _afterMoveRestTimer;
        private float _hurtTimer, _panicTimer, _panicRetargetTimer, _invulnerableTimer;
        private float _targetYaw;
        private double _goalX, _goalZ;
        private float _goalUrgency = 0.55f;
        private float _desiredMoveForward, _currentMoveForward;
        private float _desiredSpeedScale = 0.92f, _currentSpeedScale = 0.92f;
        private readonly double _homeX, _homeZ;
        private double _panicSourceX, _panicSourceZ;
        private float _bodyAlignDelay, _lastHeadYaw;
        private bool _pendingJump;
        private float _pendingJumpSpeed;

        // Animation state.
        private float _walkPhase, _walkAmount, _flapPhase;

        // Death state.
        private bool _dead;
        private float _deathTimer;
        private float _deathDuration = 0.5f;
        private float _deathRollDir = 1f;

        // Movement controls chosen by the brain each tick.
        private float _ctrlForward, _ctrlStrafe;
        private bool _ctrlJump;

        public Duck(Point3D position, float yaw)
        {
            Position = position;
            Yaw = yaw;
            HeadYaw = yaw;
            _targetYaw = yaw;
            _lastHeadYaw = yaw;
            _goalX = position.X;
            _goalZ = position.Z;
            _homeX = position.X;
            _homeZ = position.Z;
            _panicSourceX = position.X;
            _panicSourceZ = position.Z;
            _flapPhase = (float)(Rng() * Math.PI * 2);
        }

        public DuckInstance ToInstance()
        {
            float headLocal = Clamp(WrapAngle(HeadYaw - Yaw), -MaxHeadYaw, MaxHeadYaw);
            float deathT = _dead ? Clamp(_deathTimer / Math.Max(0.001f, _deathDuration), 0f, 1f) : 0f;
            return new DuckInstance(
                Position, Yaw, headLocal,
                _walkPhase, _walkAmount, _flapPhase,
                (float)_velY, OnGround,
                _dead, deathT, _deathRollDir, _hurtTimer);
        }

        /// <summary>Advance the duck one tick: AI brain, then physics + collision.</summary>
        public void Update(float dt, ChunkManager manager)
        {
            _prevOnGround = OnGround;
            OnGround = false;
            _invulnerableTimer = Math.Max(0f, _invulnerableTimer - dt);

            UpdateBrain(dt, manager);
            if (Removed) return;
            UpdatePhysics(dt, manager);
        }

        /// <summary>
        /// Apply damage from an attacker at (srcX, srcZ). Triggers hurt flash, knockback, panic, and
        /// death when health hits zero. Returns whether the hit landed (false if invulnerable/dead).
        /// </summary>
        public bool Damage(int amount, double srcX, double srcZ, bool hasSource)
        {
            if (_dead || amount <= 0) return false;
            if (_invulnerableTimer > 0f) return false;

            Health = Math.Max(0, Health - amount);
            _hurtTimer = Math.Max(_hurtTimer, 0.20f);
            _invulnerableTimer = Math.Max(_invulnerableTimer, HitInvulnDuration);

            if (hasSource)
            {
                double dx = Position.X - srcX;
                double dz = Position.Z - srcZ;
                double len = Math.Sqrt(dx * dx + dz * dz);
                if (len < 1e-6) len = 1;
                _velX += dx / len * 1.2;
                _velZ += dz / len * 1.2;
            }

            _panicTimer = Math.Max(_panicTimer, PanicDurationMin + (float)Rng() * (PanicDurationMax - PanicDurationMin));
            _panicRetargetTimer = 0f;
            _idleTimer = 0f;
            _afterMoveRestTimer = 0f;
            _actionTimer = 0f;
            if (hasSource)
            {
                _panicSourceX = srcX;
                _panicSourceZ = srcZ;
            }
            else
            {
                _panicSourceX = Position.X - Math.Sin(Yaw);
                _panicSourceZ = Position.Z - Math.Cos(Yaw);
            }

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
                _deathRollDir = hasSource ? ((Position.X - srcX) >= 0 ? 1f : -1f) : (Rng() < 0.5 ? -1f : 1f);
                _ctrlForward = 0f; _ctrlStrafe = 0f; _ctrlJump = false;
            }

            return true;
        }

        // ---- AI brain -------------------------------------------------------

        private void UpdateBrain(float dt, ChunkManager manager)
        {
            _ctrlForward = 0f; _ctrlStrafe = 0f; _ctrlJump = false;

            if (_dead)
            {
                UpdateDead(dt);
                return;
            }

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
            _flapPhase += dt * (grounded ? (4.5f + _walkAmount * 4.0f) : 18.0f);

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
                {
                    ChoosePanicGoal();
                }
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
                    _lookRetargetTimer = _behavior == Behavior.Look
                        ? (0.65f + (float)Rng() * 1.2f)
                        : (1.0f + (float)Rng() * 1.3f);
                }
                _desiredMoveForward = 0f;
                _desiredSpeedScale = 0.9f;
            }
            else
            {
                if (goalDist > 0.001)
                {
                    _targetYaw = (float)Math.Atan2(goalDx, goalDz);
                }
                float urgency = Clamp(_goalUrgency, 0f, 1f);
                float distFactor = Clamp((float)goalDist / 3.8f, 0f, 1f);
                float desiredForward = Clamp(0.34f + urgency * 0.22f + distFactor * 0.28f, 0f, 1.0f);
                if (goalDist < 0.9) desiredForward *= Clamp(((float)goalDist - 0.16f) / 0.74f, 0f, 1f);
                if (goalDist < 0.26)
                {
                    if (_behavior == Behavior.Stroll) _afterMoveRestTimer = Math.Max(_afterMoveRestTimer, 1.4f + (float)Rng() * 2.4f);
                    if (_behavior == Behavior.ReturnHome) _afterMoveRestTimer = Math.Max(_afterMoveRestTimer, 0.9f + (float)Rng() * 1.5f);
                    _actionTimer = 0f;
                    desiredForward = 0f;
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
                _ctrlJump = true;
                _pendingJump = true;
                _pendingJumpSpeed = 7.42f;
                _hopCooldown = 0.26f;
            }

            _currentMoveForward = Lerp(_currentMoveForward, _idleTimer > 0f ? 0f : _desiredMoveForward, 1f - (float)Math.Exp(-dt * 4.6));
            _currentSpeedScale = Lerp(_currentSpeedScale, _idleTimer > 0f ? 0.9f : _desiredSpeedScale, 1f - (float)Math.Exp(-dt * 3.9));

            HeadYaw = TurnToward(HeadYaw, _targetYaw, HeadTurnSpeed * dt);
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
            HeadYaw = Yaw;
            _velX *= Math.Pow(0.60, dt * 60);
            _velZ *= Math.Pow(0.60, dt * 60);
            if (_deathTimer >= _deathDuration)
            {
                Removed = true;
            }
        }

        private void ChooseWanderGoal(bool forceHome)
        {
            double homeDx = _homeX - Position.X;
            double homeDz = _homeZ - Position.Z;
            double homeDistSq = homeDx * homeDx + homeDz * homeDz;

            if (forceHome || homeDistSq > 196)
            {
                double homeDist = Math.Sqrt(homeDistSq);
                double targetDist = Math.Min(6.25, Math.Max(2.4, homeDist));
                _behavior = Behavior.ReturnHome;
                _afterMoveRestTimer = 0f;
                _idleTimer = 0f;
                _actionTimer = 1.5f + (float)Rng() * 1.4f + (float)targetDist * 0.26f;
                _goalUrgency = 1.0f;
                _targetYaw = (float)Math.Atan2(homeDx, homeDz) + ((float)Rng() - 0.5f) * 0.18f;
                _goalX = Position.X + Math.Sin(_targetYaw) * targetDist;
                _goalZ = Position.Z + Math.Cos(_targetYaw) * targetDist;
                _desiredMoveForward = 0.68f;
                _desiredSpeedScale = 1.06f;
                return;
            }

            if (_afterMoveRestTimer > 0f)
            {
                _afterMoveRestTimer = 0f;
                SetIdleGoal(true);
                return;
            }

            if (Rng() < 0.64)
            {
                SetIdleGoal(false);
                return;
            }

            double wanderDist = 0.85 + Rng() * 3.4;
            float urgency = 0.26f + (float)Rng() * 0.34f;
            float yaw = HeadYaw + ((float)Rng() - 0.5f) * (1.05f + (float)Rng() * 0.9f);
            if (Rng() < 0.12) yaw = (float)(Rng() * Math.PI * 2);

            _behavior = Behavior.Stroll;
            _idleTimer = 0f;
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
            double awayX = Position.X - _panicSourceX;
            double awayZ = Position.Z - _panicSourceZ;
            if ((awayX * awayX + awayZ * awayZ) > 1e-4) awayYaw = (float)Math.Atan2(awayX, awayZ);

            float yaw = awayYaw + ((float)Rng() - 0.5f) * (0.65f + (float)Rng() * 0.7f);
            double panicDist = 2.8 + Rng() * 3.0;
            _behavior = Behavior.Panic;
            _idleTimer = 0f;
            _afterMoveRestTimer = 0f;
            _goalUrgency = 1.0f;
            _targetYaw = yaw;
            _goalX = Position.X + Math.Sin(yaw) * panicDist;
            _goalZ = Position.Z + Math.Cos(yaw) * panicDist;
            _desiredMoveForward = 0.76f + (float)Rng() * 0.12f;
            _desiredSpeedScale = 1.0f;
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
            _idleTimer = duration;
            _actionTimer = duration;
            _goalX = Position.X;
            _goalZ = Position.Z;
            _goalUrgency = 0f;
            _desiredMoveForward = 0f;
            _desiredSpeedScale = 0.9f;
            _targetYaw = lookYaw;
            _lookRetargetTimer = glance ? (0.7f + (float)Rng() * 1.35f) : (1.2f + (float)Rng() * 1.4f);
        }

        private void UpdateBodyYawFromHead(float dt, bool moving)
        {
            float headYaw = HeadYaw;
            float headDelta = Math.Abs(WrapAngle(headYaw - _lastHeadYaw));

            if (moving)
            {
                _bodyAlignDelay = 0f;
                _lastHeadYaw = headYaw;
                Yaw = TurnToward(Yaw, headYaw, BodyTurnSpeedMoving * dt);
            }
            else
            {
                float diff = WrapAngle(headYaw - Yaw);
                if (Math.Abs(diff) > MaxHeadYaw)
                {
                    Yaw = headYaw - Math.Sign(diff) * MaxHeadYaw;
                    _bodyAlignDelay = 0f;
                    _lastHeadYaw = headYaw;
                }
                else if (headDelta > Deg(0.5f))
                {
                    _bodyAlignDelay = 0f;
                    _lastHeadYaw = headYaw;
                }
                else
                {
                    _bodyAlignDelay += dt;
                    if (_bodyAlignDelay > BodyAlignWait)
                    {
                        float t = Math.Min(1f, (_bodyAlignDelay - BodyAlignWait) / Math.Max(0.0001f, BodyAlignFull - BodyAlignWait));
                        float speed = Lerp(0f, BodyTurnSpeedIdle, t);
                        Yaw = TurnToward(Yaw, headYaw, speed * dt);
                    }
                }
            }

            float clampedDiff = Clamp(WrapAngle(HeadYaw - Yaw), -MaxHeadYaw, MaxHeadYaw);
            HeadYaw = Yaw + clampedDiff;
        }

        // ---- Physics + collision -------------------------------------------

        private void UpdatePhysics(float dt, ChunkManager manager)
        {
            ApplyMoveControls(dt);

            _velY -= Gravity * dt;
            if (_velY < 0)
            {
                _velY *= Math.Pow(0.82, dt * 60);
                if (_velY < -2.15) _velY = -2.15;
            }

            MoveAxis(manager, Axis.X, _velX * dt);
            MoveAxis(manager, Axis.Z, _velZ * dt);
            MoveAxis(manager, Axis.Y, _velY * dt);

            // Frame-rate-independent ground contact: a slow (high-FPS) descent settles a hair above
            // the block, so the per-tick fall never re-touches it and OnGround would stay false
            // (making the duck flap/bob as if airborne). Probe a small distance below the feet.
            if (!OnGround && _velY <= 0
                && IntersectsSolid(manager, Position.X, Position.Y - GroundProbe, Position.Z))
            {
                OnGround = true;
                _velY = 0;
            }

            double horizontalDrag = Math.Pow(OnGround ? DragGround : DragAir, dt * 60);
            double verticalDrag = Math.Pow(DragVertical, dt * 60);
            _velX *= horizontalDrag;
            _velZ *= horizontalDrag;
            _velY *= verticalDrag;
        }

        private void ApplyMoveControls(float dt)
        {
            float speedScale = _currentSpeedScale;
            float forward = Clamp(_ctrlForward, -1f, 1f);
            float strafe = Clamp(_ctrlStrafe, -1f, 1f);
            double inputLen = Math.Sqrt(forward * forward + strafe * strafe);
            if (inputLen > 1e-5)
            {
                double inv = inputLen > 1 ? 1 / inputLen : 1;
                forward = (float)(forward * inv);
                strafe = (float)(strafe * inv);
                double sinYaw = Math.Sin(Yaw);
                double cosYaw = Math.Cos(Yaw);
                double wishX = sinYaw * forward + cosYaw * strafe;
                double wishZ = cosYaw * forward - sinYaw * strafe;
                bool grounded = _prevOnGround || OnGround;
                float accelBase = grounded ? GroundAccel : AirAccel;
                float accel = accelBase * Lerp(0.9f, 1.12f, Clamp((speedScale - 0.85f) / 0.3f, 0f, 1f));
                _velX += wishX * accel * dt;
                _velZ += wishZ * accel * dt;
            }

            if (_ctrlJump && (_prevOnGround || OnGround) && _jumpPressCooldown <= 0f)
            {
                float jumpSpeed = _pendingJump ? _pendingJumpSpeed : JumpSpeed;
                _velY = Math.Max(_velY, jumpSpeed);
                _pendingJump = false;
                _jumpPressCooldown = 0.12f;
            }

            float maxSpeed = MaxSpeed * speedScale;
            double speedSq = _velX * _velX + _velZ * _velZ;
            if (speedSq > maxSpeed * maxSpeed)
            {
                double inv = maxSpeed / Math.Sqrt(speedSq);
                _velX *= inv;
                _velZ *= inv;
            }

            double horizontalSpeed = Math.Sqrt(_velX * _velX + _velZ * _velZ);
            _walkAmount = (float)Math.Min(1, horizontalSpeed / Math.Max(0.001, maxSpeed));
            _walkPhase += dt * ((_prevOnGround || OnGround) ? (_walkAmount * 8.5f + 1.9f) : 14.0f);
        }

        private enum Axis { X, Y, Z }

        private void MoveAxis(ChunkManager manager, Axis axis, double amount)
        {
            const double baseStep = 0.05;
            double remaining = amount;
            int safety = 0;
            while (Math.Abs(remaining) > 0.0001)
            {
                if (++safety > 96)
                {
                    SetVel(axis, 0);
                    return;
                }
                double delta = Math.Sign(remaining) * Math.Min(Math.Abs(remaining), baseStep);
                double nx = Position.X, ny = Position.Y, nz = Position.Z;
                switch (axis)
                {
                    case Axis.X: nx += delta; break;
                    case Axis.Y: ny += delta; break;
                    case Axis.Z: nz += delta; break;
                }

                if (!IntersectsSolid(manager, nx, ny, nz))
                {
                    Position = new Point3D(nx, ny, nz);
                    remaining -= delta;
                    continue;
                }

                if (axis != Axis.Y && TryStep(manager, axis, delta))
                {
                    remaining -= delta;
                    continue;
                }

                if (axis == Axis.Y && delta < 0) OnGround = true;
                SetVel(axis, 0);
                return;
            }
        }

        private bool TryStep(ChunkManager manager, Axis axis, double delta)
        {
            if (StepHeight <= 0 || !OnGround) return false;
            double bx = Position.X, by = Position.Y + StepHeight, bz = Position.Z;
            switch (axis)
            {
                case Axis.X: bx += delta; break;
                case Axis.Z: bz += delta; break;
            }
            if (IntersectsSolid(manager, bx, by, bz)) return false;
            Position = new Point3D(bx, by, bz);
            OnGround = true;
            return true;
        }

        private void SetVel(Axis axis, double value)
        {
            switch (axis)
            {
                case Axis.X: _velX = value; break;
                case Axis.Y: _velY = value; break;
                case Axis.Z: _velZ = value; break;
            }
        }

        private static bool IntersectsSolid(ChunkManager manager, double px, double py, double pz)
        {
            double halfW = Width * 0.5;
            double minX = px - halfW, maxX = px + halfW;
            double minY = py, maxY = py + Height;
            double minZ = pz - halfW, maxZ = pz + halfW;

            int x0 = (int)Math.Floor(minX);
            int x1 = (int)Math.Floor(maxX);
            int y0 = (int)Math.Floor(minY);
            int y1 = (int)Math.Floor(maxY - 0.001);
            int z0 = (int)Math.Floor(minZ);
            int z1 = (int)Math.Floor(maxZ);

            for (int x = x0; x <= x1; x++)
            {
                for (int y = y0; y <= y1; y++)
                {
                    for (int z = z0; z <= z1; z++)
                    {
                        if (IsSolid(manager, x, y, z)) return true;
                    }
                }
            }
            return false;
        }

        // ---- Terrain probes -------------------------------------------------

        private static bool IsSolid(ChunkManager manager, int x, int y, int z)
        {
            return manager.TryGetLoadedBlock(x, y, z, out var block) && block != BlockType.Air;
        }

        private bool SolidAhead(ChunkManager manager, double distance)
        {
            double dirX = Math.Sin(Yaw);
            double dirZ = Math.Cos(Yaw);
            int probeX = (int)Math.Floor(Position.X + dirX * distance);
            int probeZ = (int)Math.Floor(Position.Z + dirZ * distance);
            int baseY = (int)Math.Floor(Position.Y + 0.05);
            return IsSolid(manager, probeX, baseY, probeZ) || IsSolid(manager, probeX, baseY + 1, probeZ);
        }

        private bool NeedsStepJump(ChunkManager manager, double distance)
        {
            double dirX = Math.Sin(Yaw);
            double dirZ = Math.Cos(Yaw);
            int probeX = (int)Math.Floor(Position.X + dirX * distance);
            int probeZ = (int)Math.Floor(Position.Z + dirZ * distance);
            int baseY = (int)Math.Floor(Position.Y + 0.05);
            return IsSolid(manager, probeX, baseY, probeZ)
                && !IsSolid(manager, probeX, baseY + 1, probeZ)
                && !IsSolid(manager, probeX, baseY + 2, probeZ);
        }

        private bool DangerousDropAhead(ChunkManager manager, double distance)
        {
            double dirX = Math.Sin(Yaw);
            double dirZ = Math.Cos(Yaw);
            int probeX = (int)Math.Floor(Position.X + dirX * distance);
            int probeZ = (int)Math.Floor(Position.Z + dirZ * distance);
            int baseY = (int)Math.Floor(Position.Y + 0.05);

            int aheadGroundY = FindGroundYAt(manager, probeX, probeZ, baseY);
            if (aheadGroundY < 0) return true;
            return (aheadGroundY + 1) < (Position.Y - 1.05);
        }

        // Highest solid block at or below startY in the column; -1 if none within scan range.
        private static int FindGroundYAt(ChunkManager manager, int x, int z, int startY)
        {
            int bottom = Math.Max(0, startY - 24);
            for (int y = startY; y >= bottom; y--)
            {
                if (IsSolid(manager, x, y, z)) return y;
            }
            return -1;
        }

        // ---- Math helpers ---------------------------------------------------

        private static double Rng() => Random.Shared.NextDouble();
        private static float Deg(float degrees) => degrees * (float)Math.PI / 180f;
        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        private static float WrapAngle(float angle)
        {
            while (angle > Math.PI) angle -= (float)(Math.PI * 2);
            while (angle < -Math.PI) angle += (float)(Math.PI * 2);
            return angle;
        }

        private static float TurnToward(float current, float target, float maxStep)
        {
            float delta = WrapAngle(target - current);
            if (Math.Abs(delta) <= maxStep) return target;
            return current + Math.Sign(delta) * maxStep;
        }
    }
}
