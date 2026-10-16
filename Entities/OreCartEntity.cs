namespace Cubuild
{
    /// <summary>
    /// A passive ore cart: a minecart that nobody rides and nobody drives. It rides the rail
    /// network exactly like <see cref="CartEntity"/> - the same heading snap, the same
    /// along-track gravity, the same coast-to-a-stop - because it IS a CartEntity; only the two
    /// controls are taken away.
    ///
    /// No rider: <see cref="CanBeRidden"/> is false, so right-clicking one never seats the player
    /// and it has no throttle. Its one control is physical. It takes part in the usual
    /// mob/player separation pass (<see cref="ParticipatesInSeparation"/>), which is what lets a
    /// walking player shove it; <see cref="AcceptsExternalPush"/> then turns that shove into
    /// motion along the rails. The minecart opts out of both, so it is completely unaffected.
    /// </summary>
    public sealed class OreCartEntity : CartEntity
    {
        public override string MobTypeName => "orecart";

        /// <summary>No seat, no throttle - you cannot ride or drive an ore cart.</summary>
        public override bool CanBeRidden => false;

        /// <summary>
        /// Unlike the minecart, an ore cart has no rider pinned inside it, so it can safely take
        /// part in separation. That is the mechanism behind "walk into it to push it": the pass
        /// writes a world-space shove into _velX/_velZ, which the base loop folds into Speed.
        /// </summary>
        public override bool ParticipatesInSeparation => true;

        /// <summary>Turns a shove from the separation pass into motion along the rails.</summary>
        protected override bool AcceptsExternalPush => true;

        public OreCartEntity(Point3D position, float yaw) : base(position, yaw) { }
    }
}
