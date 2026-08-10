namespace CubeApp
{
    /// <summary>
    /// Mutable state for one simulated player. The local player is one instance; networked
    /// players (host-simulated) are more instances. All movement physics is written against
    /// this so the exact same code runs for local and remote players.
    /// </summary>
    public sealed class PlayerState
    {
        public Point3D Position; // eye position
        public float Yaw;
        public float Pitch;
        public Point3D Velocity;
        public bool Grounded;
        public bool FlyMode;
        public float WalkPhase;
        public float WalkAmount;

        /// <summary>Latest input received from the network (host-side). The local player's own
        /// input is applied directly via StepSimulation.</summary>
        public TickInputState PendingInput;

        /// <summary>Player health, 0..10. Each point is one slice of the HUD heart (death at 0).</summary>
        public int Health = 10;
        /// <summary>How the player last died. Set when health reaches 0; the death screen picks its
        /// message from this. Falls back to generic when no specific cause is known.</summary>
        public DeathCause DeathCause;
        /// <summary>Time since death began (seconds); drives the death roll animation. 0 = alive.</summary>
        public float DeathTimer;
        /// <summary>Direction the corpse rolls on death (+1/-1), matching mob death rolls.</summary>
        public float DeathRollDir = 1f;
        /// <summary>Seconds since the player last took damage. Healing waits for the delay, then
        /// restores one heart slice per regen interval.</summary>
        public float TimeSinceDamage;
        /// <summary>Accumulator toward the next heart-slice heal.</summary>
        public float RegenAccumulator;
        /// <summary>Seconds until the next heart slice restores (8.5 + random 1..2 fluctuation).</summary>
        public float NextRegenInterval = 8.5f;
    }

    /// <summary>
    /// The manner of a player's death. The respawn screen reads this to pick a fitting message;
    /// new death methods just set a new cause when they kill the player.
    /// </summary>
    public enum DeathCause
    {
        /// <summary>Not dead / no cause recorded.</summary>
        Unknown,
        /// <summary>Killed by the debug "O" key (manual damage test).</summary>
        DebugSelf,
        /// <summary>Landed too hard after a fall (survival fall damage).</summary>
        Fall,
    }
}
