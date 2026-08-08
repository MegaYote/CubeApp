namespace CubeApp
{
    /// <summary>
    /// Interface for mobs that can be rendered. Allows the renderer to work with
    /// different mob implementations (hardcoded ducks, GLB-based mobs, etc.) generically.
    /// </summary>
    public interface IMobRenderable
    {
        /// <summary>
        /// The type identifier for this mob (e.g., "duck", "coyote"). Used for model lookup.
        /// </summary>
        string MobType { get; }

        /// <summary>
        /// World position of the mob's feet.
        /// </summary>
        Point3D Position { get; }

        /// <summary>
        /// Yaw rotation in radians.
        /// </summary>
        float Yaw { get; }

        /// <summary>
        /// Whether the mob is currently on the ground.
        /// </summary>
        bool OnGround { get; }

        /// <summary>
        /// Whether the mob is dead.
        /// </summary>
        bool IsDead { get; }

        /// <summary>
        /// Death animation progress (0.0 to 1.0).
        /// </summary>
        float DeathT { get; }

        /// <summary>
        /// Death roll direction (+1 or -1).
        /// </summary>
        float DeathRollDir { get; }

        /// <summary>
        /// Seconds of remaining hurt flash.
        /// </summary>
        float HurtTimer { get; }

        /// <summary>
        /// Walk cycle phase in radians for animation.
        /// </summary>
        float WalkPhase { get; }

        /// <summary>
        /// How briskly the mob is walking (0.0 to 1.0).
        /// </summary>
        float WalkAmount { get; }

        /// <summary>
        /// Accumulated animation time (seconds) that advances only while the mob is moving -
        /// drives GLB walk cycles so they play while walking and hold pose when idle.
        /// </summary>
        float AnimTime { get; }

        /// <summary>
        /// Vertical velocity for in-air tilt animation.
        /// </summary>
        float VelocityY { get; }

        /// <summary>
        /// Head yaw relative to body (for mobs with head animation).
        /// </summary>
        float HeadYawLocal { get; }

        /// <summary>
        /// Wing flap phase for flying mobs (radians).
        /// </summary>
        float FlapPhase { get; }
    }
}
