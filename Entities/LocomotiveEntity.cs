namespace Cubuild
{
    /// <summary>
    /// A locomotive: a cart that hauls a train.
    ///
    /// It rides the rail network exactly like a minecart - same heading snap, same along-track
    /// gravity, same coast-to-a-stop when left alone - because it IS a CartEntity. What sets it
    /// apart is power and control:
    ///
    ///  * It cannot be sat in (<see cref="CanBeRidden"/> is false). The player drives it
    ///    INDIRECTLY, by sitting in a minecart that is coupled to it: the throttle is routed to the
    ///    locomotive (see GameWorld.StepRidingCart) and the carriage the player is sitting in is
    ///    dragged along behind as a rigid trailer.
    ///
    ///  * It is far stronger and faster than a minecart. <see cref="MaxSpeed"/> is double, and more
    ///    importantly <see cref="ThrottleAccel"/> beats the ~14.1 blocks/s^2 that gravity pulls back
    ///    down a full 45-degree grade - the exact thing a minecart's ladder-climb fails at - so a
    ///    locomotive pulls its train up a hill without slowing.
    ///
    ///  * Its model is rigged: <see cref="AnimSecondsPerBlock"/> drives the Blockbench clip's wheel
    ///    and piston joints from distance travelled, so the rods turn in proportion to actual speed.
    ///
    /// Unoccupied it simply coasts (gravity on a grade, friction on the flat), so a parked
    /// locomotive "can still run but cannot be steered" - exactly what an unmanned engine does.
    /// </summary>
    public sealed class LocomotiveEntity : CartEntity
    {
        private readonly string _mobType;

        /// <summary>
        /// The model variant. Two Blockbench exports exist - "locomotivehot" (working) and
        /// "locomotivecold" (banked) - sharing one rig, so the type name is chosen at construction.
        /// </summary>
        public override string MobTypeName => _mobType;

        /// <summary>No seat: a locomotive is driven through a coupled carriage, never directly.</summary>
        public override bool CanBeRidden => false;

        /// <summary>Roughly double a minecart's top speed (the brief asked for at least 2x).</summary>
        public override float MaxSpeed => 14f;

        /// <summary>
        /// 22 blocks/s^2 of pull. Deliberately above g*sin(45) = 20*0.707 = 14.14, which is the
        /// along-track pull of a full-block-per-cell grade: a minecart (9) is dragged back by the
        /// slope, a locomotive is not - that is the entire point of having one on a hill.
        /// </summary>
        protected override float ThrottleAccel => 22f;

        /// <summary>
        /// One full wheel revolution of the model's clip lasts 3.5s of animation time and the
        /// driving wheels have a 0.25-block radius, i.e. a 1.57-block circumference, so the clip
        /// wants to run 3.5/1.57 = ~2.23 seconds of animation per block travelled.
        /// </summary>
        protected override float AnimSecondsPerBlock => 2.23f;

        public LocomotiveEntity(Point3D position, float yaw, string mobType = "locomotivehot")
            : base(position, yaw)
        {
            _mobType = mobType;

            // A locomotive is a bigger machine than a minecart, so give it a matching collision box
            // rather than inheriting the carriage's 0.9 x 0.7 (CartEntity sets those in its ctor).
            Width = 1.1f;
            Height = 1.0f;
        }
    }
}
