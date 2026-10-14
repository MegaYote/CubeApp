using System;
using Cubuild.World;

namespace Cubuild
{
    /// <summary>
    /// A player-rideable boat. Unlike every other mob it has no AI: it sits where it was
    /// launched, floats on the water surface with real buoyancy, and only moves when a player
    /// is steering it. Rendering goes through the ordinary MobModel path (a hand-authored
    /// Blockbench hull in MobEntities/BoatMob), so no renderer changes were needed.
    ///
    /// Coordinates follow MobEntity: <see cref="MobEntity.Position"/> is the FEET position and
    /// +Y is up.
    /// </summary>
    public sealed class BoatEntity : MobEntity
    {
        public override string MobTypeName => "boat";

        /// <summary>Set by GameWorld while the local player is riding this boat.</summary>
        public bool Occupied { get; set; }

        /// <summary>
        /// Steering input, written by GameWorld each tick while ridden (-1..1). The base class
        /// keeps its own brain-driven control fields private, so a vehicle reads its own.
        /// </summary>
        public float SteerForward;
        public float SteerStrafe;

        /// <summary>True once the player has steered at least once, so a placed boat is inert.</summary>
        private bool _launched;

        private static readonly int _waterId = BlockRegistry.GetId("water");

        public BoatEntity(Point3D position, float yaw) : base(position, yaw)
        {
            Width = 1.2f;
            Height = 0.6f;
            MaxSpeed = 7.0f;
            // Boats are immortal props; nothing should be able to kill one mid-ocean.
            Health = 1000;
            Hostile = false;
            // No wandering: the brain is skipped in Update below anyway.
            Gravity = 9f;
            MaxFallSpeed = 6f;
        }

        /// <summary>World-space Y of the water surface the hull should ride on, or null off-water.</summary>
        private static double? WaterSurfaceY(ChunkManager cm, double x, double z, double aroundY)
        {
            // Water is a full cube in storage; the RENDERED surface sits a little below the
            // block top (Mesher.GetFluidHeight returns ~0.889 for a lone source block). Scan a
            // small vertical window around the boat for the topmost water cell so the hull
            // settles just under the surface instead of hovering or sinking.
            int bx = (int)Math.Floor(x), bz = (int)Math.Floor(z);
            int from = (int)Math.Floor(aroundY) - 2;
            int to = (int)Math.Floor(aroundY) + 2;
            for (int y = to; y >= from; y--)
            {
                if (cm.TryGetLoadedBlock(bx, y, bz, out int id) && id == _waterId)
                    return y + 0.89;
            }
            return null;
        }

        private bool InWater(ChunkManager cm, double x, double y, double z)
            => cm.TryGetLoadedBlock((int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z), out int id)
               && id == _waterId;

        /// <summary>
        /// Replaces the whole mob AI+physics pipeline. A boat only floats, damps and (while
        /// occupied) steers; it never walks, chases, attacks or takes fall damage.
        /// </summary>
        public override void Update(float dt, ChunkManager manager)
        {
            if (IsDead) return;

            var surface = WaterSurfaceY(manager, Position.X, Position.Z, Position.Y);
            bool afloat = surface.HasValue;

            if (afloat)
            {
                // Buoyancy: push the hull toward the waterline, and let wave-scale bob happen
                // naturally because the target moves when the boat crosses a block boundary.
                double targetY = surface.Value;
                double dy = targetY - Position.Y;
                // Heavier-looking hull: correct fast near the line, gently overshoot for bob.
                _velY += Math.Clamp(dy * 9.0, -6.0, 6.0) * dt;
                _velY *= Math.Pow(0.12, dt);          // strong vertical damping => floats steady
                Position = new Point3D(Position.X, Position.Y + _velY * dt, Position.Z);

                // Water drag on the horizontal axes, weaker than gravity so you keep momentum.
                double drag = Math.Pow(Occupied ? 0.93 : 0.70, dt * 60);
                _velX *= drag;
                _velZ *= drag;
            }
            else
            {
                // Aground or beached: normal gravity, and damp hard so it doesn't slide off.
                _velY -= Gravity * dt;
                if (_velY < -MaxFallSpeed) _velY = -MaxFallSpeed;
                Position = new Point3D(Position.X, Position.Y + _velY * dt, Position.Z);
                _velX *= Math.Pow(0.02, dt);
                _velZ *= Math.Pow(0.02, dt);
            }

            // Steer (only meaningful while ridden).
            if (Occupied)
            {
                _launched = true;
                double sinYaw = Math.Sin(Yaw), cosYaw = Math.Cos(Yaw);
                double wishX = sinYaw * SteerForward + cosYaw * SteerStrafe;
                double wishZ = cosYaw * SteerForward - sinYaw * SteerStrafe;
                double accel = afloat ? 9.0 : 3.0;    // sluggish on land, quick on water
                _velX += wishX * accel * dt;
                _velZ += wishZ * accel * dt;
            }
            else if (_launched)
            {
                // Empty boat coasts to a stop rather than drifting forever.
                _velX *= Math.Pow(0.05, dt);
                _velZ *= Math.Pow(0.05, dt);
            }

            // Clamp horizontal speed.
            double speed = Math.Sqrt(_velX * _velX + _velZ * _velZ);
            if (speed > MaxSpeed)
            {
                double k = MaxSpeed / speed;
                _velX *= k;
                _velZ *= k;
            }

            // Move with collision, axis by axis, using the base class's protected solid test so the
            // hull can't be pushed through terrain. Deliberately no step-up: a boat should
            // stop at a one-block ledge rather than hopping onto the dock.
            MoveWithCollision(manager, Axis.X, _velX * dt);
            MoveWithCollision(manager, Axis.Z, _velZ * dt);
            MoveWithCollision(manager, Axis.Y, _velY * dt);

            // Never let the boat fall out of the world if it is somehow unloaded under it.
            if (Position.Y < -32) { Removed = true; return; }

            OnGround = afloat;
        }

        /// <summary>
        /// Sub-stepped axis move that stops at the first solid overlap. The base class keeps its
        /// own MoveAxis private, so a vehicle re-implements the same idea on top of the
        /// protected IntersectsSolid helper.
        /// </summary>
        private void MoveWithCollision(ChunkManager cm, Axis axis, double amount)
        {
            if (amount == 0) return;
            const double baseStep = 0.05;
            int steps = (int)Math.Ceiling(Math.Abs(amount) / baseStep);
            if (steps < 1) steps = 1;
            if (steps > 96) steps = 96;
            double inc = amount / steps;
            for (int s = 0; s < steps; s++)
            {
                double nx = Position.X, ny = Position.Y, nz = Position.Z;
                if (axis == Axis.X) nx += inc;
                else if (axis == Axis.Y) ny += inc;
                else nz += inc;
                if (IntersectsSolid(cm, nx, ny, nz)) return;   // stop this axis, keep the rest
                Position = new Point3D(nx, ny, nz);
            }
        }

        /// <summary>A boat is a vehicle: mob separation must never shove it off its heading.</summary>
        public override bool ParticipatesInSeparation => false;
    }
}