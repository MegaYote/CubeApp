using System;

namespace CubeApp
{
    /// <summary>
    /// A spawnable NPC that uses the Minecraft-style player model (<see cref="PlayerModel"/>) and
    /// the player skin. Inherits the standard wander/panic AI and physics from
    /// <see cref="MobEntity"/>; player-sized collision box (0.6 x 1.8).
    /// </summary>
    public sealed class SteveMob : MobEntity
    {
        public SteveMob(Point3D position, float yaw) : base(position, yaw)
        {
            Width = 0.75f;
            Height = 2.25f;
            MaxHealth = 20;
            Health = MaxHealth;
            StepHeight = 0.55f;
        }

        public override string MobTypeName => "player";
    }
}
