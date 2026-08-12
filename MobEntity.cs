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
        public readonly float AnimTime;
        public readonly float AnimBlend;
        public readonly float VelocityY;
        public readonly bool OnGround;
        public readonly bool IsDead;
        public readonly float DeathT, DeathRollDir, HurtTimer;
        public readonly string MobType;

        public MobInstance(float x, float y, float z, float yaw, float walkPhase, float walkAmount,
            float animTime, float animBlend, float velocityY, bool onGround, bool isDead, float deathT, float deathRollDir, float hurtTimer, string mobType)
        {
            X = x; Y = y; Z = z; Yaw = yaw;
            WalkPhase = walkPhase; WalkAmount = walkAmount;
            AnimTime = animTime; AnimBlend = animBlend;
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
        protected float MaxFallSpeed = 36f; // ground-mob terminal velocity (matches the player)
        protected float MaxSpeed = 4f;
        protected float GroundAccel = 28f;
        protected float AirAccel = 7f;
        protected float JumpSpeed = 7.36f;
        protected float DragGround = 0.86f;   // strong ground traction: stops quickly, no ice feel
        protected float DragAir = 0.985f;
        protected float DragVertical = 0.992f;
        protected float StepHeight = 0.45f;
        protected const double FallDamageThreshold = 3.0;  // first 3 blocks free (MC convention)
        private double _fallDistance;  // accumulates downward movement while airborne
        protected double GroundProbe = 0.06;

        public Point3D Position { get; set; }
        public float Yaw { get; set; }
        public bool OnGround { get; set; }
        public bool Removed { get; protected set; }
        public bool IsDead => _dead;

        // A* pathfinding. Lazily built per-mob when a path goal is requested; a fresh PathFinder
        // and NodeProcessor are cheap (they hold no world state).
        private PathFinder? _pathFinder;
        private PathEntity? _path;
        private double _pathGoalX, _pathGoalZ;
        private int _pathRecalcCooldown;
        private bool _pathGoalActive;

        /// <summary>Whether a pathfinding route is currently active.</summary>
        public bool HasPath => _path != null && !_path.IsDone;

        /// <summary>
        /// Supplies the current night-dim level (0..11) so environmental behaviors (zombie
        /// sunburn) can tell day from night. Wired by the EntityManager from the world clock.
        /// </summary>
        public Func<int>? SkylightSource { get; set; }

        // ---- Hostile AI (zombies) ----

        /// <summary>True when this mob hunts humans (zombies). Wired from the mob config.</summary>
        public bool Hostile { get; protected set; }

        /// <summary>Damage dealt per attack.</summary>
        public int AttackDamage { get; protected set; } = 5;

        /// <summary>Horizontal range at which the mob starts chasing its target (blocks).</summary>
        public float AggroRange { get; protected set; } = 24f;

        /// <summary>Horizontal range at which the mob can land a hit (blocks).</summary>
        public float AttackRange { get; protected set; } = 1.5f;

        /// <summary>Seconds between attacks.</summary>
        public float AttackCooldown { get; protected set; } = 1.0f;

        /// <summary>
        /// Set each frame by the EntityManager to the nearest human's feet position (the local
        /// player or a Steve NPC). The chase re-paths via A* so zombies route AROUND cliffs and
        /// walls instead of beelining over a drop.
        /// </summary>
        public void SetChaseTarget(Point3D? target)
        {
            _chaseTarget = target;
            if (!target.HasValue)
            {
                ClearPathGoal();
            }
        }

        /// <summary>Fired when this mob lands a melee hit on its target (wired to the EntityManager
        /// to damage a Steve NPC or mark the player as hit).</summary>
        public Action? OnAttack { get; set; }

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
        public virtual string MobTypeName => GetType().Name.ToLowerInvariant();
        float IMobRenderable.WalkPhase => _walkPhase;
        float IMobRenderable.WalkAmount => _walkAmount;
        float IMobRenderable.AnimTime => _animClock;
        float IMobRenderable.AnimBlend => _animBlend;
        float IMobRenderable.FlapPhase => _flapPhase;
        float IMobRenderable.VelocityY => (float)_velY;
        float IMobRenderable.DeathT => _dead ? Math.Clamp(_deathTimer / Math.Max(0.001f, _deathDuration), 0f, 1f) : 0f;
        float IMobRenderable.DeathRollDir => _deathRollDir;
        float IMobRenderable.HurtTimer => _hurtTimer;

        (int, int, int, int, float)? IMobRenderable.MiningBlock =>
            _isMining && _miningBlockId > 0
                ? (_miningBlockX, _miningBlockY, _miningBlockZ, _miningBlockId, _miningProgress)
                : null;

        float IMobRenderable.HeadPitchLocal => _headPitch;
        float IMobRenderable.RenderScale => 1f;
        private float _headPitch;

        protected double _velX, _velY, _velZ;
        protected bool _prevOnGround;
        protected readonly double _homeX, _homeZ;
        protected float _walkPhase, _walkAmount, _flapPhase;
        /// <summary>Accumulated animation time (seconds). Only advances while the mob is actually
        /// moving (scaled by walkAmount), so GLB walk cycles play while walking and hold pose when
        /// idle - unlike _walkPhase which also advances at an idle-bob rate.</summary>
        protected float _animClock;
        /// <summary>Smoothed 0..1 blend between the rest pose and the walk pose. Eases to 1 while
        /// moving and back to 0 when idle, so a stopped mob returns to its neutral stance instead
        /// of freezing mid-stride.</summary>
        protected float _animBlend;
        /// <summary>How fast the GLB walk animation plays relative to real time (1.0 = one cycle
        /// per second of full-speed walking). Tuned per mob so leg motion matches ground speed.</summary>
        protected float AnimSpeedScale = 1.0f;
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

        // Block-breaking: when a zombie is blocked by a breakable wall it starts mining it.
        private int _miningBlockX, _miningBlockY, _miningBlockZ;
        private int _miningBlockId;
        private float _miningProgress;
        private float _miningDuration;
        private bool _isMining;

        // Hostile AI (zombies): a chase target position (set by the EntityManager from the nearest
        // human) plus attack timing. Hostiles path toward the target and lunge when in range.
        private Point3D? _chaseTarget;
        private float _attackCooldown;

        private const float HitInvulnDuration = 1f / 3f;
        private const float PanicDurationMin = 2.8f;
        private const float PanicDurationMax = 4.2f;
        private static readonly float MaxHeadYaw = 75f * (float)Math.PI / 180f;
        private static readonly float HeadTurnSpeed = 220f * (float)Math.PI / 180f;
        private static readonly float BodyTurnSpeedMoving = 320f * (float)Math.PI / 180f;
        private static readonly float BodyTurnSpeedIdle = 120f * (float)Math.PI / 180f;
        private const float BodyAlignWait = 0.5f;
        private const float BodyAlignFull = 1.0f;

        private enum Behavior { Idle, Look, Stroll, ReturnHome, Panic, Chase, Dead }
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
            UpdateEnvironment(dt, manager);
            UpdatePhysics(dt, manager);
        }

        /// <summary>
        /// Environmental damage hook (zombies burn in daylight). Virtual so subclasses can
        /// opt in without touching the base simulation.
        /// </summary>
        protected virtual void UpdateEnvironment(float dt, ChunkManager manager) { }

        /// <summary>
        /// Apply damage from an attacker at (srcX, srcZ).
        /// </summary>
        public virtual bool Damage(int amount, double srcX, double srcZ) => Damage(amount, srcX, srcZ, true);

        /// <summary>
        /// Apply damage from an attacker at (srcX, srcZ). Triggers hurt flash, knockback, panic, and
        /// death when health hits zero. <paramref name="hasSource"/> lets knockback/panic-direction be
        /// skipped when there's no real attacker (e.g. environmental damage) - the mob then panics
        /// away from its current facing instead.
        /// </summary>
        public virtual bool Damage(int amount, double srcX, double srcZ, bool hasSource)
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
                // MC-style knockback: horizontal push away from attacker + a small upward lift.
                _velX += dx / len * 4.5;
                _velZ += dz / len * 4.5;
                _velY += 2.5;
            }

            // Hostile mobs never flee — they stand their ground and fight back.
            if (!Hostile)
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

        /// <summary>Adds velocity for entity-collision pushing (MC-style mob separation).</summary>
        public void AddVelocity(double vx, double vy, double vz)
        {
            _velX += vx; _velY += vy; _velZ += vz;
        }

        private void CancelMining()
        {
            _isMining = false; _miningProgress = 0f; _miningBlockId = 0;
        }

        private bool UpdateMining(float dt, ChunkManager manager)
        {
            if (!_isMining || _miningBlockId <= 0) return false;
            _miningProgress += dt / _miningDuration;
            if (_miningProgress >= 1f)
            {
                manager.TrySetBlock(_miningBlockX, _miningBlockY, _miningBlockZ, BlockRegistry.AirId, 0);
                CancelMining();
                return true;
            }
            return false;
        }

        private void StartMiningBlock(int x, int y, int z, int blockId)
        {
            _miningBlockX = x; _miningBlockY = y; _miningBlockZ = z;
            _miningBlockId = blockId; _miningProgress = 0f; _isMining = true;
            var speed = BlockRegistry.ZombieBreakSpeedOf(blockId);
            _miningDuration = speed switch { ZombieBreakSpeed.Fast => 0.5f, ZombieBreakSpeed.Slow => 3.0f, _ => 1.5f };
        }

        /// <summary>Whether this mob animates wing flaps (ducks). Default false.</summary>
        protected virtual bool HasFlap => false;

        /// <summary>
        /// Snapshot handed to the renderer each frame. Uses <see cref="MobTypeName"/> as the model
        /// key, so subclasses only override <see cref="MobTypeName"/> (and optionally <see cref="HasFlap"/>).
        /// </summary>
        public virtual MobInstance ToInstance()
        {
            return new MobInstance(
                (float)Position.X, (float)Position.Y, (float)Position.Z,
                Yaw, _walkPhase, _walkAmount, _animClock, _animBlend,
                (float)_velY, OnGround, _dead,
                _dead ? Math.Clamp(_deathTimer / Math.Max(0.001f, _deathDuration), 0f, 1f) : 0f,
                _deathRollDir, _hurtTimer, MobTypeName);
        }

        /// <summary>
        /// Ask the navigator to path to a world position. The mob steers along the computed
        /// waypoints (A*) until the goal is reached or the path is exhausted.
        /// Set <paramref name="maxDistance"/> to bound how far the search will look.
        /// </summary>
        public void SetPathGoal(double goalX, double goalZ, double maxDistance = 64.0)
        {
            _pathGoalX = goalX;
            _pathGoalZ = goalZ;
            _pathRecalcCooldown = 0;
            _path = null;
            _pathGoalActive = true;
        }

        /// <summary>Cancel any active path goal and let the wander brain resume.</summary>
        public void ClearPathGoal()
        {
            _pathGoalActive = false;
            _path = null;
        }

        private void UpdatePath(float dt, ChunkManager manager)
        {
            if (_path == null)
            {
                // No route yet: compute one to the stored goal.
                _pathFinder ??= new PathFinder(new PathNodeProcessor(manager, Width, Height));
                _path = _pathFinder.FindPath(Position.X, Position.Y, Position.Z, _pathGoalX, Position.Y, _pathGoalZ, 64.0f);
                if (_path == null)
                {
                    _pathRecalcCooldown = 20;
                    return;
                }
            }

            if (_path.IsDone)
            {
                _path = null;
                return;
            }

            // Re-request the path when the goal moves far from the current path end (or every so
            // often in case terrain changed).
            _pathRecalcCooldown--;
            if (_pathRecalcCooldown <= 0)
            {
                double gx = _pathGoalX - Position.X, gz = _pathGoalZ - Position.Z;
                if (gx * gx + gz * gz < 2.25) // within 1.5 blocks of goal: actually arrived
                {
                    _path = null;
                    return;
                }
                var final = _path.GetFinal();
                double fdx = _pathGoalX - final.X, fdz = _pathGoalZ - final.Z;
                if (fdx * fdx + fdz * fdz > 9.0)
                {
                    _pathFinder ??= new PathFinder(new PathNodeProcessor(manager, Width, Height));
                    var fresh = _pathFinder.FindPath(Position.X, Position.Y, Position.Z, _pathGoalX, Position.Y, _pathGoalZ, 64.0f);
                    if (fresh != null) _path = fresh;
                }
                _pathRecalcCooldown = 20;
            }

            var wp = _path.GetNext();
            if (wp == null)
            {
                _path = null;
                return;
            }

            // Steer toward the current waypoint; advance when we're close to it.
            double dx = wp.X + 0.5 - Position.X;
            double dz = wp.Z + 0.5 - Position.Z;
            double distSq = dx * dx + dz * dz;
            if (distSq < 0.7 * 0.7)
            {
                _path.Advance();
                wp = _path.GetNext();
                if (wp == null)
                {
                    _path = null;
                    return;
                }
                dx = wp.X + 0.5 - Position.X;
                dz = wp.Z + 0.5 - Position.Z;
            }

            _targetYaw = (float)Math.Atan2(dx, dz);
            _desiredMoveForward = 0.9f;
            _desiredSpeedScale = 1.0f;
            _idleTimer = 0f;

            // Let the shared steering code handle the actual turn + movement.
        }

        // ---- AI brain -------------------------------------------------------

        private void UpdateBrain(float dt, ChunkManager manager)
        {
            _ctrlForward = 0f; _ctrlStrafe = 0f; _ctrlJump = false;
            if (_dead) { UpdateDead(dt); return; }

            // Tick block-breaking progress and hurt flash — must run regardless of AI branch.
            if (_isMining) UpdateMining(dt, manager);
            _hurtTimer = Math.Max(0f, _hurtTimer - dt);

            // Tick timers that matter regardless of which AI branch runs.
            _hopCooldown = Math.Max(0f, _hopCooldown - dt);
            _jumpPressCooldown = Math.Max(0f, _jumpPressCooldown - dt);

            bool grounded = _prevOnGround || OnGround;

            // Hostile chase (zombies): when a human target is in aggro range, re-path toward it via
            // A* (routing AROUND cliffs/walls) and lunge when in melee range. Panic still wins (a
            // fleeing mob never stops to fight). When the chase only sets a path goal it returns
            // false so the A* path override below steers along the route.
            if (_panicTimer <= 0f && Hostile && _chaseTarget.HasValue)
            {
                if (UpdateChase(dt, manager)) return;
            }

            _aiTimer = Math.Max(0f, _aiTimer - dt);
            _actionTimer = Math.Max(0f, _actionTimer - dt);
            _idleTimer = Math.Max(0f, _idleTimer - dt);
            // _hopCooldown / _jumpPressCooldown / _hurtTimer ticked in the top block so the
            // chase branch (which returns early) still drains them - do NOT re-tick here.
            _ledgeTurnCooldown = Math.Max(0f, _ledgeTurnCooldown - dt);
            _lookRetargetTimer = Math.Max(0f, _lookRetargetTimer - dt);
            _afterMoveRestTimer = Math.Max(0f, _afterMoveRestTimer - dt);
            _panicTimer = Math.Max(0f, _panicTimer - dt);
            _panicRetargetTimer = Math.Max(0f, _panicRetargetTimer - dt);
            // _walkPhase advances in ApplyMoveControls (once per frame, all AI branches).
            if (HasFlap) _flapPhase += dt * (grounded ? (4.5f + _walkAmount * 4.0f) : 18.0f);

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

        // Hostile chase (MC-style): walk toward the player, attack while moving, jump obstacles,
        // break blocks when stuck. Lightweight — just direct movement, no A* pathfinding.
        private bool UpdateChase(float dt, ChunkManager manager)
        {
            var target = _chaseTarget.Value;
            double dx = target.X - Position.X, dz = target.Z - Position.Z;
            double distSq = dx * dx + dz * dz;
            if (distSq > AggroRange * AggroRange) { ClearPathGoal(); return false; }

            _attackCooldown = Math.Max(0f, _attackCooldown - dt);
            _behavior = Behavior.Chase; _idleTimer = 0f; _afterMoveRestTimer = 0f;

            // Face and walk toward the player.
            float toPlayer = (float)Math.Atan2(dx, dz);
            _targetYaw = toPlayer;
            _desiredMoveForward = distSq <= AttackRange * AttackRange ? 0.65f : 0.92f;
            _desiredSpeedScale = 1.0f;
            _currentMoveForward = Lerp(_currentMoveForward, _desiredMoveForward, 1f - (float)Math.Exp(-dt * 5.0));
            _currentSpeedScale = Lerp(_currentSpeedScale, _desiredSpeedScale, 1f - (float)Math.Exp(-dt * 3.9));

            _headYaw = TurnToward(_headYaw, toPlayer, HeadTurnSpeed * 1.4f * dt);
            // Head pitch: look up/down at the player. Eye height is roughly 80% of body height.
            // Negative pitch = looking up (matches the renderer convention from player camera).
            double eyeY = Position.Y + Height * 0.8;
            double hDist = Math.Sqrt(distSq);
            float targetPitch = (float)Math.Atan2(target.Y - eyeY, hDist);
            _headPitch += (targetPitch - _headPitch) * Math.Min(1f, HeadTurnSpeed * 1.4f * dt);
            _headPitch = Clamp(_headPitch, -1.05f, 1.05f); // ±60° like the player
            UpdateBodyYawFromHead(dt, _currentMoveForward > 0.04f);

            float yawError = WrapAngle(_targetYaw - Yaw);
            float turnSlow = Math.Abs(yawError) > 1.2f ? 0.22f : (Math.Abs(yawError) > 0.75f ? 0.72f : 1.0f);
            _ctrlForward = _currentMoveForward * turnSlow;
            _ctrlStrafe = _currentMoveForward > 0.04f ? Clamp(yawError * 0.14f, -0.18f, 0.18f) : 0f;

            // Melee attack while pressing into the player.
            if (distSq <= AttackRange * AttackRange && _attackCooldown <= 0f)
            {
                _attackCooldown = AttackCooldown;
                OnAttack?.Invoke();
            }

            // Jump over 1-block obstacles.
            if ((_prevOnGround || OnGround) && _hopCooldown <= 0f && NeedsStepJump(manager, 0.6))
            {
                _ctrlJump = true; _pendingJump = true;
                _pendingJumpSpeed = 7.42f; _hopCooldown = 0.26f;
            }

            // Break blocks when stuck. Simple tick counter: if the zombie hasn't moved for ~15 ticks,
            // try to break whatever's in front of it.
            if ((_prevOnGround || OnGround))
            {
                double moved = Math.Abs(Position.X - _lastPathX) + Math.Abs(Position.Z - _lastPathZ);
                if (moved < 0.02) _pathRecalcTick++;
                else { _pathRecalcTick = 0; _lastPathX = Position.X; _lastPathZ = Position.Z; }

                if (_pathRecalcTick > 15 && !_isMining)
                    TryBreakBlockAhead(manager);
            }
            else _pathRecalcTick = 0;

            if (_isMining) _ctrlForward = 0f;
            return true;
        }

        private double _lastPathX, _lastPathZ;
        private int _pathRecalcTick;

        private void TryBreakBlockAhead(ChunkManager manager)
        {
            double dirX = Math.Sin(Yaw), dirZ = Math.Cos(Yaw);
            int bx = (int)Math.Floor(Position.X + dirX * (Width * 0.5 + 0.2));
            int bz = (int)Math.Floor(Position.Z + dirZ * (Width * 0.5 + 0.2));
            int by = (int)Math.Floor(Position.Y + 0.05);
            for (int dy = 0; dy < 2; dy++)
            {
                if (manager.TryGetLoadedBlock(bx, by + dy, bz, out int bid)
                    && bid != BlockRegistry.AirId && BlockRegistry.IsSolid(bid)
                    && BlockRegistry.ZombieCanBreakOf(bid))
                {
                    manager.TryGetLoadedBlock(bx, by + dy + 1, bz, out int above1);
                    manager.TryGetLoadedBlock(bx, by + dy + 2, bz, out int above2);
                    if (!BlockRegistry.IsSolid(above1) && !BlockRegistry.IsSolid(above2))
                    {
                        StartMiningBlock(bx, by + dy, bz, bid);
                        return;
                    }
                }
            }
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

            // Swimming: in water the mob floats, rides up to the surface, and paddles while
            // moving - it never walks on water and doesn't sink to the bottom. Ducks are natural
            // swimmers: they ride high, barely any struggle, and cruise across the water.
            bool feetInWater = IsWaterAt(manager, Position.X, Position.Y + 0.15, Position.Z);
            bool bodyInWater = InWater(manager);
            if (feetInWater || bodyInWater)
            {
                bool duck = HasFlap;
                double waterDrag = Math.Pow(duck ? 0.90 : 0.82, dt * 60);
                _velX *= waterDrag; _velZ *= waterDrag;

                // Weak water gravity; buoyancy lifts while submerged.
                _velY -= Gravity * (duck ? 0.12 : 0.25) * dt;
                if (bodyInWater && _velY < (duck ? 2.5 : 2.0))
                {
                    _velY += (duck ? 9.0 : 7.0) * dt;
                }
                // Ride until the TORSO clears the surface, then flatten - the mob sits ON the
                // water instead of bobbing just below the surface.
                if (!bodyInWater && _velY > 0)
                {
                    _velY *= Math.Pow(0.35, dt * 60);
                    if (_velY > 0.12) _velY = 0.12;
                }
                if (_velY < -2.5) _velY = -2.5;
                if (_velY > 3.2) _velY = 3.2;

                // Ducks paddle with real speed in water (their wings push them along).
                if (duck)
                {
                    float forward = Clamp(_ctrlForward, -1f, 1f);
                    float strafe = Clamp(_ctrlStrafe, -1f, 1f);
                    if (Math.Abs(forward) > 0.05f || Math.Abs(strafe) > 0.05f)
                    {
                        double sinYaw = Math.Sin(Yaw), cosYaw = Math.Cos(Yaw);
                        double wishX = sinYaw * forward + cosYaw * strafe;
                        double wishZ = cosYaw * forward - sinYaw * strafe;
                        _velX += wishX * 6.0 * dt;
                        _velZ += wishZ * 6.0 * dt;
                    }
                }

                MoveAxis(manager, Axis.X, _velX * dt);
                MoveAxis(manager, Axis.Z, _velZ * dt);
                MoveAxis(manager, Axis.Y, _velY * dt);

                _pendingJump = false;
                OnGround = false;
                return;
            }

            _velY -= Gravity * dt;
            if (_velY < 0)
            {
                if (HasFlap)
                {
                    // Ducks glide on their wings: strong air resistance caps the fall at a gentle
                    // ~2 blocks/s float-down. Ground mobs fall normally (no feather-drop).
                    _velY *= Math.Pow(0.82, dt * 60);
                    if (_velY < -2.15) _velY = -2.15;
                }
                else if (_velY < -MaxFallSpeed)
                {
                    _velY = -MaxFallSpeed; // matches the player's terminal velocity (24g / 36)
                }
            }

            bool wasAirborne = !OnGround && !_dead;
            double prevY = Position.Y;
            MoveAxis(manager, Axis.X, _velX * dt);
            MoveAxis(manager, Axis.Z, _velZ * dt);
            MoveAxis(manager, Axis.Y, _velY * dt);

            // Ground probe: catch landings that MoveAxis missed (mob ended just above surface).
            if (!OnGround && _velY <= 0 && IntersectsSolid(manager, Position.X, Position.Y - GroundProbe, Position.Z))
            {
                OnGround = true; _velY = 0;
            }

            // MC-style fall damage: accumulated downward distance. Ducks glide so exempt.
            if (wasAirborne)
            {
                double dy = Position.Y - prevY;
                if (dy < 0) _fallDistance -= dy;
                if (OnGround && !HasFlap && _fallDistance > FallDamageThreshold)
                {
                    int damage = (int)Math.Ceiling(_fallDistance - FallDamageThreshold);
                    damage = Math.Max(1, damage);
                    Health = Math.Max(0, Health - damage);
                    _hurtTimer = Math.Max(_hurtTimer, 0.22f);
                    if (Health <= 0)
                    {
                        _dead = true; _deathTimer = 0f; _behavior = Behavior.Dead;
                        _deathRollDir = Rng() < 0.5 ? -1f : 1f;
                        _ctrlForward = 0f; _ctrlStrafe = 0f; _ctrlJump = false;
                    }
                }
                if (OnGround) _fallDistance = 0;
            }
            else
            {
                _fallDistance = 0;
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
            else if (_prevOnGround || OnGround)
            {
                // No push this frame (idle / turning): brake hard so the mob stops in place
                // instead of coasting. The base drag in UpdatePhysics handles the rest.
                double brakeSpeedSq = _velX * _velX + _velZ * _velZ;
                if (brakeSpeedSq > 0.001)
                {
                    double brake = Math.Pow(0.62, dt * 60); // ~0.62/frame: quick stop
                    _velX *= brake; _velZ *= brake;
                }
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
            // Animation clock: advance in step with real walking; freeze when idle. Scaled by
            // AnimSpeedScale so leg cycles match the mob's actual ground speed.
            _animClock += dt * _walkAmount * (_prevOnGround || OnGround ? 1f : 0.2f) * AnimSpeedScale;
            // Walk blend eases toward the movement amount so stopping returns the mob to rest pose.
            _animBlend += (_walkAmount - _animBlend) * (1f - (float)Math.Exp(-dt * 5.0f));
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
            => manager.TryGetLoadedBlock(x, y, z, out var block) && BlockRegistry.IsSolid(block);

        private static int _waterId = -1;
        private static int WaterId
        {
            get
            {
                if (_waterId < 0) _waterId = BlockRegistry.GetId("water");
                return _waterId;
            }
        }

        // Body center is submerged -> the mob is in water (feet can wade while the torso is dry).
        private bool InWater(ChunkManager manager)
        {
            int x = (int)Math.Floor(Position.X);
            int y = (int)Math.Floor(Position.Y + Height * 0.4);
            int z = (int)Math.Floor(Position.Z);
            return manager.TryGetLoadedBlock(x, y, z, out var b) && b == WaterId;
        }

        // True when the block at a world point is water.
        private bool IsWaterAt(ChunkManager manager, double x, double y, double z)
        {
            return manager.TryGetLoadedBlock((int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z), out var b) && b == WaterId;
        }

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

        protected static double Rng() => Random.Shared.NextDouble();
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
            Height = 0.95f;
            MaxHealth = 10;
            Health = MaxHealth;
            // Legs should swing about one full stride per ~2.5 blocks covered; at 4 blocks/s and a
            // 1.33s cycle that's ~2.2x real time.
            AnimSpeedScale = 2.2f;
        }

        public override string MobTypeName => "coyote";
    }
}
