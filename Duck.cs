using System;

namespace CubeApp
{
    /// <summary>
    /// A snapshot of a duck handed to the renderer each frame: where it is, which way it faces and
    /// the animation state needed to pose its bones (walk cycle, wing flap, head turn, hurt/death).
    /// Kept separate from the mob sim so rendering never touches sim state.
    /// </summary>
    public readonly struct DuckInstance
    {
        public readonly Point3D Position;   // feet position (model origin) in world space
        public readonly float Yaw;          // radians, body rotation about +Y
        public readonly float HeadYawLocal; // radians, head yaw relative to body (already clamped)
        public readonly float WalkPhase;    // radians, drives leg/wing swing
        public readonly float WalkAmount;   // 0..1, how briskly it's walking
        public readonly float AnimTime;     // seconds, advances only while moving (GLB walk cycles)
        public readonly float AnimBlend;    // 0..1 rest<->walk blend
        public readonly float FlapPhase;    // radians, drives in-air wing flap
        public readonly float VelocityY;    // vertical velocity (for in-air tilt)
        public readonly bool OnGround;
        public readonly bool IsDead;
        public readonly float DeathT;       // 0..1 death animation progress
        public readonly float DeathRollDir; // +1/-1 roll direction on death
        public readonly float HurtTimer;    // seconds of remaining hurt flash

        public DuckInstance(
            Point3D position, float yaw, float headYawLocal,
            float walkPhase, float walkAmount, float animTime, float animBlend, float flapPhase,
            float velocityY, bool onGround,
            bool isDead, float deathT, float deathRollDir, float hurtTimer)
        {
            Position = position;
            Yaw = yaw;
            HeadYawLocal = headYawLocal;
            WalkPhase = walkPhase;
            WalkAmount = walkAmount;
            AnimTime = animTime;
            AnimBlend = animBlend;
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
    /// The duck mob. Now just ONE ENTRY in the universal <see cref="MobEntity"/> system - it only
    /// sets its dimensions / stats and enables the wing-flap animation. The wander / panic / death
    /// AI, physics and collision all come from the shared base class.
    /// </summary>
    public sealed class Duck : MobEntity
    {
        public const float DuckWidth = 0.68f;
        public const float DuckHeight = 1.35f;

        public Duck(Point3D position, float yaw) : base(position, yaw)
        {
            Width = DuckWidth;
            Height = DuckHeight;
            MaxHealth = 8;
            Health = MaxHealth;
            _flapPhase = (float)(Rng() * Math.PI * 2);
        }

        public override string MobTypeName => "duck";
        protected override bool HasFlap => true;
    }
}
