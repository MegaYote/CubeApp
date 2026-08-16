namespace CubeApp
{
    /// <summary>
    /// Sim state for one dropped item (a stack the player mined or threw that didn't go
    /// straight into inventory). Falls with gravity, tumbles like a physical object, settles on
    /// the ground, and despawns after a while. The player collects it by walking into it.
    /// </summary>
    public sealed class DroppedItem
    {
        public int ItemId;
        public int Count;
        /// <summary>World position of the drop's base (bottom corner).</summary>
        public Point3D Position;
        public Point3D Velocity;
        /// <summary>Seconds since spawn; drives the pickup grace period and the despawn timer.</summary>
        public float Age;
        /// <summary>Orientation as a unit quaternion (tumbles while airborne).</summary>
        public float RotX = 1f, RotY, RotZ, RotW; // identity
        /// <summary>Normalized tumble axis.</summary>
        public float SpinAxisX, SpinAxisY, SpinAxisZ;
        /// <summary>Tumble speed in radians per second (decays with drag, stops on landing).</summary>
        public float SpinSpeed;
        /// <summary>When &gt; 0, the drop is magnetized toward the player for pickup (seconds
        /// remaining). During this flight it ignores gravity and homes to the player.</summary>
        public float FlyTime;
    }
}
