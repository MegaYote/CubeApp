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
    }
}
