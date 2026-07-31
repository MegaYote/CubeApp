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
            Width = 0.6f;
            Height = 1.8f;
            MaxHealth = 20;
            Health = MaxHealth;
            StepHeight = 0.55f;
        }

        protected override string MobTypeName => "player";

        public override MobInstance ToInstance()
        {
            return new MobInstance(
                (float)Position.X, (float)Position.Y, (float)Position.Z,
                Yaw, _walkPhase, _walkAmount,
                (float)_velY, OnGround, _dead,
                _dead ? Math.Clamp(_deathTimer / Math.Max(0.001f, _deathDuration), 0f, 1f) : 0f,
                _deathRollDir, _hurtTimer, "player");
        }
    }
}
