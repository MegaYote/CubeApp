namespace CubeApp
{
    /// <summary>
    /// Unified render data for any mob. The renderer can work with this struct
    /// regardless of the underlying mob implementation.
    /// </summary>
    public readonly struct MobRenderData
    {
        public readonly string MobType;
        public readonly Point3D Position;
        public readonly float Yaw;
        public readonly float HeadYawLocal;
        public readonly float HeadPitchLocal;
        public readonly float WalkPhase;
        public readonly float WalkAmount;
        public readonly float AnimTime;
        public readonly float AnimBlend;
        public readonly float FlapPhase;
        public readonly float VelocityY;
        public readonly bool OnGround;
        public readonly bool IsDead;
        public readonly float DeathT;
        public readonly float DeathRollDir;
        public readonly float HurtTimer;

        public MobRenderData(
            string mobType,
            Point3D position,
            float yaw,
            float headYawLocal,
            float walkPhase,
            float walkAmount,
            float animTime,
            float animBlend,
            float flapPhase,
            float velocityY,
            bool onGround,
            bool isDead,
            float deathT,
            float deathRollDir,
            float hurtTimer,
            float headPitchLocal = 0f)
        {
            MobType = mobType;
            Position = position;
            Yaw = yaw;
            HeadYawLocal = headYawLocal;
            HeadPitchLocal = headPitchLocal;
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

        /// <summary>
        /// Create render data from any mob that implements IMobRenderable.
        /// </summary>
        public static MobRenderData FromMob(IMobRenderable mob)
        {
            return new MobRenderData(
                mob.MobType,
                mob.Position,
                mob.Yaw,
                mob.HeadYawLocal,
                mob.WalkPhase,
                mob.WalkAmount,
                mob.AnimTime,
                mob.AnimBlend,
                mob.FlapPhase,
                mob.VelocityY,
                mob.OnGround,
                mob.IsDead,
                mob.DeathT,
                mob.DeathRollDir,
                mob.HurtTimer,
                mob.HeadPitchLocal);
        }
    }
}
