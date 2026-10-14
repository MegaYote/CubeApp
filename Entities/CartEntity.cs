using System;

namespace Cubuild
{
    /// <summary>
    /// A minecart that rides the rail network. Prototype scope: it snaps onto a rail, holds a
    /// single scalar <see cref="Speed"/>, derives its heading purely from which rails it is
    /// standing on, and is driven by a seated player (or a shunt from being hit).
    ///
    /// Deliberately NOT here yet: linkage/coupling, cargo mass, the locomotive's traction
    /// limit and cruise. Those are layered on after the base loop is proven.
    ///
    /// Everything below is derived locally each tick - there is no cached rail graph - so
    /// editing the track while a cart is moving can never leave it following a stale path.
    /// </summary>
    public sealed class CartEntity : MobEntity
    {
        public override string MobTypeName => "cart";

        private static int _railIdCache = -1;
        private static int RailId
        {
            get { if (_railIdCache < 0) _railIdCache = BlockRegistry.GetId("rail"); return _railIdCache; }
            set => _railIdCache = value;
        }

        /// <summary>Signed speed along the cart's current heading, in blocks/second.</summary>
        public float Speed;

        /// <summary>How hard the player can shove it when boarding and holding a direction.</summary>
        public const float MaxSpeed = 7.0f;

        /// <summary>Speed lost per second with nothing driving it.</summary>
        public const float CoastDecay = 2.2f;

        /// <summary>Set by GameWorld while the local player is riding this cart.</summary>
        public bool Occupied { get; set; }

        public CartEntity(Point3D position, float yaw) : base(position, yaw)
        {
            Width = 0.9f;
            Height = 0.7f;
            MaxFallSpeed = 6f;
            Gravity = 20f;
            Health = 100;
            Hostile = false;
        }

        /// <summary>True when the cell holds a rail.</summary>
        private static bool IsRail(ChunkManager cm, double x, double y, double z)
            => cm.TryGetLoadedBlock((int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z), out int id)
               && id == RailId;

        /// <summary>
        /// The rail cell this cart belongs to, or null when it is off-track. A cart is "on"
        /// the rail layer at its own Y, so it slides along regardless of terrain.
        /// </summary>
        private static bool TryFindRail(ChunkManager cm, CartEntity c, out int rx, out int rz)
        {
            int bx = (int)Math.Floor(c.Position.X);
            int by = (int)Math.Floor(c.Position.Y + 0.25);
            int bz = (int)Math.Floor(c.Position.Z);
            // A cart sits slightly above the rail block, so accept the cell below too.
            if (IsRail(cm, bx, by, bz) || IsRail(cm, bx, by - 1, bz))
            {
                rx = bx; rz = bz; return true;
            }
            rx = rz = 0; return false;
        }

        /// <summary>
        /// Heading, derived from which of the four cardinal neighbours also hold a rail. With
        /// exactly one connection it points that way (dead end); with two it bisects them;
        /// with none it keeps its current heading so a nudged cart doesn't spin wildly.
        /// </summary>
        private static float ComputeHeading(ChunkManager cm, int bx, int by, int bz, float current)
        {
            bool hasRail(int dx, int dz) =>
                IsRail(cm, bx + dx + 0.5, by, bz + dz + 0.5) || IsRail(cm, bx + dx + 0.5, by - 1, bz + dz + 0.5);

            bool nx = hasRail(-1, 0), px = hasRail(1, 0);
            bool nz = hasRail(0, -1), pz = hasRail(0, 1);

            double dx = (px ? 1 : 0) - (nx ? 1 : 0);
            double dz = (pz ? 1 : 0) - (nz ? 1 : 0);
            if (dx == 0 && dz == 0) return current;               // isolated or a closed loop
            return (float)Math.Atan2(dx, dz);                       // matches the engine's yaw sense
        }

        public override void Update(float dt, ChunkManager manager)
        {
            if (IsDead) return;

            bool onRail = TryFindRail(manager, this, out int bx, out int bz);

            if (onRail)
            {
                int by = (int)Math.Floor(Position.Y + 0.25);
                Yaw = ComputeHeading(manager, bx, by, bz, Yaw);

                // Drive: W/S throttle forward/back along the current heading.
                if (Occupied)
                {
                    if (_driveFwd != 0f)
                    {
                        Speed += _driveFwd * 9f * dt;
                        Speed = Math.Clamp(Speed, -MaxSpeed * 0.45f, MaxSpeed);
                    }
                    else
                    {
                        Speed -= Math.Sign(Speed) * Math.Min(Math.Abs(Speed), CoastDecay * dt);
                    }
                }
                else
                {
                    // Unridden carts coast to a stop so nothing runs away from its owner.
                    Speed -= Math.Sign(Speed) * Math.Min(Math.Abs(Speed), CoastDecay * dt);
                }

                // Advance along the heading, axis by axis so walls stop the cart cleanly.
                if (Math.Abs(Speed) > 0.001f)
                {
                    double mx = Math.Sin(Yaw) * Speed * dt;
                    double mz = Math.Cos(Yaw) * Speed * dt;
                    if (!IntersectsSolid(manager, Position.X + mx, Position.Y, Position.Z))
                        Position = new Point3D(Position.X + mx, Position.Y, Position.Z);
                    else
                        Speed = 0f;
                    if (!IntersectsSolid(manager, Position.X, Position.Y, Position.Z + mz))
                        Position = new Point3D(Position.X, Position.Y, Position.Z + mz);
                    else
                        Speed = 0f;
                }

                // Sit on top of the rail surface rather than sinking through it.
                double railTop = by + 0.0;
                if (Position.Y < railTop + 0.05)
                {
                    Position = new Point3D(Position.X, railTop + 0.05, Position.Z);
                    _velY = 0;
                }
                else
                {
                    _velY -= Gravity * dt;
                    Position = new Point3D(Position.X, Position.Y + _velY * dt, Position.Z);
                    if (Position.Y < railTop + 0.05)
                    {
                        Position = new Point3D(Position.X, railTop + 0.05, Position.Z);
                        _velY = 0;
                    }
                }
            }
            else
            {
                // Off-track: behave like a dropped item rather than a vehicle.
                _velY -= Gravity * dt;
                if (_velY < -MaxFallSpeed) _velY = -MaxFallSpeed;
                MoveWithCollision(manager, Axis.X, _velX * dt);
                MoveWithCollision(manager, Axis.Z, _velZ * dt);
                MoveWithCollision(manager, Axis.Y, _velY * dt);
                Speed = 0f;
            }

            OnGround = onRail;
            if (Position.Y < -32) { Removed = true; return; }
        }

        // Throttle, written by GameWorld while ridden.
        public float _driveFwd;

        /// <summary>Sub-stepped axis move that stops at the first solid overlap.</summary>
        private void MoveWithCollision(ChunkManager cm, Axis axis, double amount)
        {
            if (amount == 0) return;
            const double baseStep = 0.05;
            int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(amount) / baseStep));
            if (steps > 96) steps = 96;
            double inc = amount / steps;
            for (int s = 0; s < steps; s++)
            {
                double nx = Position.X, ny = Position.Y, nz = Position.Z;
                if (axis == Axis.X) nx += inc; else if (axis == Axis.Y) ny += inc; else nz += inc;
                if (IntersectsSolid(cm, nx, ny, nz)) return;
                Position = new Point3D(nx, ny, nz);
            }
        }
    }
}