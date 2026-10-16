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
    public class CartEntity : MobEntity
    {
        public override string MobTypeName => "cart";

        /// <summary>
        /// Whether a player may board this cart. The minecart says yes; the passive ore cart
        /// (<see cref="OreCartEntity"/>) says no, and GameWorld skips it when picking a cart to
        /// mount so right-clicking one never seats the player.
        /// </summary>
        public virtual bool CanBeRidden => true;

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

        /// <summary>
        /// Fraction of gravity that acts ALONG the track rather than into it. A slope climbs a
        /// full block across one cell, i.e. a 45-degree grade, so sin(45 degrees). This is what
        /// drives the cart downhill on its own.
        /// </summary>
        private const double SlopeGrade = 0.7071067811865476;

        /// <summary>
        /// How far the rendered body leans to lie along a slope, in radians. A slope rises a full
        /// block across one cell, so it is a real 45-degree incline - the same angle SlopeGrade
        /// takes the sine of for the physics.
        /// </summary>
        private const float SlopeTilt = MathF.PI / 4f;

        /// <summary>
        /// Floor on how fast the drawn heading chases the rail's direction, in radians per second.
        /// The speed-proportional term already covers a moving cart; this only matters when one
        /// stops with its model still trailing the bend, which would otherwise leave it parked
        /// askew on a straight with no way to finish the turn.
        /// </summary>
        private const float TurnSettleRate = 1.5f;

        /// <summary>Set by GameWorld while the local player is riding this cart.</summary>
        public bool Occupied { get; set; }

        /// <summary>
        /// A cart never separates against the local player. GameWorld pins the rider's body
        /// EXACTLY onto the cart each tick, so any separation pass sees a permanent overlap and
        /// shoves the player every frame - and because StepPlayer (the only thing that applies
        /// walk friction) is skipped while riding, that shove never decays. It just accumulates
        /// and fires the moment you dismount, flinging you away from the track. BoatEntity does
        /// the same; see its override for the same reasoning.
        /// </summary>
        public override bool ParticipatesInSeparation => false;

        /// <summary>
        /// Whether an external world-space shove (the separation pass writing _velX/_velZ) should
        /// be folded into <see cref="Speed"/> while the cart rides a rail. False for the ridden
        /// minecart - see <see cref="ParticipatesInSeparation"/> for why the separation pass must
        /// never touch it. A passive cart overrides this so walking into it nudges it along the
        /// track instead of doing nothing (the base on-rail loop reads only Speed, so a raw shove
        /// would otherwise be silently dropped).
        /// </summary>
        protected virtual bool AcceptsExternalPush => false;

        /// <summary>
        /// Fraction of a shove's along-rail component that becomes cart speed each tick. One would
        /// be a straight 1:1 match to the shove velocity; a fraction keeps a body-check from
        /// launching the cart down the line.
        /// </summary>
        private const float RailPushFactor = 0.5f;

        /// <summary>
        /// How far a PUSHED cart may be nudged out of a cart-cart overlap per 60fps-equivalent
        /// tick. Capped so a cart pinned against a wall - with something still driving into it -
        /// gets shoved rather than teleported, and so two carts moving apart can never leapfrog
        /// each other in a single frame.
        /// </summary>
        internal const double CartPushStepCap = 0.35;

        /// <summary>
        /// True when this cart is meant to be PUSHED rather than driven (the ore cart). Exposed to
        /// EntityManager's cart-shove pass, which may only ever write to a cart that says yes
        /// here: that is what keeps a driven minecart an immovable pusher, and keeps the rider
        /// pinned inside it out of the shove channel entirely.
        /// </summary>
        internal bool AcceptsCartPush => AcceptsExternalPush;

        /// <summary>
        /// The cart's world-space horizontal velocity. While it rides a rail that is its scalar
        /// <see cref="Speed"/> along the tracked heading; off the rails Speed is zeroed and the
        /// motion lives in the raw _velX/_velZ the fall path integrates. EntityManager's
        /// cart-shove pass reads this to work out how fast two carts are closing on each other.
        /// </summary>
        internal void GetWorldVelocity(out double vx, out double vz)
        {
            if (_facing >= 0)
            {
                vx = DirX[_facing] * Speed;
                vz = DirZ[_facing] * Speed;
            }
            else
            {
                vx = _velX;
                vz = _velZ;
            }
        }

        /// <summary>
        /// Moves a PUSHED cart out of a cart-cart overlap, respecting the world: the nudge runs
        /// through the same sub-stepped MoveWithCollision the cart itself uses, so a shove can
        /// never push it through a wall or into the ground. Off the rails that is the whole move;
        /// on them the cart's next step re-centres it on the track, so the shove reads as sliding
        /// along the rail rather than a sideways teleport.
        /// </summary>
        internal void ShoveBy(double dx, double dz, ChunkManager manager)
        {
            if (dx != 0) MoveWithCollision(manager, Axis.X, dx);
            if (dz != 0) MoveWithCollision(manager, Axis.Z, dz);
        }


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
        /// The rail cell this cart belongs to, or false when it is off-track. A cart is "on"
        /// the rail layer at its own Y, so it slides along regardless of terrain. Reports the
        /// rail's ACTUAL Y, not the cart's: the two differ whenever the cart is sitting on top
        /// of the rail slab, and using the cart's Y put the ride height a full block too high.
        /// One block up and one block down are both accepted so a cart can climb a rise or
        /// drop off a ledge instead of grinding to a halt at every step in the ground.
        /// </summary>
        private static bool TryFindRail(ChunkManager cm, double px, double py, double pz, out int rx, out int ry, out int rz)
        {
            int bx = (int)Math.Floor(px);
            int cy = (int)Math.Floor(py + 0.25);
            int bz = (int)Math.Floor(pz);
            for (int dy = 0; dy >= -1; dy--)
            {
                if (IsRail(cm, bx, cy + dy, bz)) { rx = bx; ry = cy + dy; rz = bz; return true; }
            }
            if (IsRail(cm, bx, cy + 1, bz)) { rx = bx; ry = cy + 1; rz = bz; return true; }
            rx = ry = rz = 0; return false;
        }

        private static bool TryFindRail(ChunkManager cm, CartEntity c, out int rx, out int ry, out int rz)
            => TryFindRail(cm, c.Position.X, c.Position.Y, c.Position.Z, out rx, out ry, out rz);

        /// <summary>
        /// Height of the rail surface at a world position, or false when there is no rail there.
        /// On a slope the surface ramps a full block across the cell, so the height depends on
        /// where in the cell the position falls: low edge at the climb's start, high edge at its
        /// end. Callers that test a candidate position must use the height returned here rather
        /// than the cart's own Y - on a climb the body lags a tick behind the surface, and testing
        /// the lower height collides with the hill the track is laid over.
        /// </summary>
        private static bool TryRailTopAt(ChunkManager cm, double px, double py, double pz, out double railTop, out int rbx, out int rby, out int rbz)
        {
            railTop = 0;
            if (!TryFindRail(cm, px, py, pz, out int bx, out int by, out int bz)) { rbx = rby = rbz = 0; return false; }
            rbx = bx; rby = by; rbz = bz;
            railTop = by + BlockRegistry.RailHeight;
            if (cm.TryGetLoadedBlockAndMeta(bx, by, bz, out _, out byte meta))
            {
                switch (RailShape.AscendDirection(meta))
                {
                    case 0: railTop += px - bx; break;          // ascends East
                    case 2: railTop += 1.0 - (px - bx); break;  // ascends West
                    case 1: railTop += pz - bz; break;          // ascends South
                    case 3: railTop += 1.0 - (pz - bz); break;  // ascends North
                }
            }
            return true;
        }

        /// <summary>
        /// Snaps the cart laterally onto the middle of its rail cell. A cart travels along one
        /// axis and nothing re-centred the other, so it sat wherever it was first placed and
        /// looked like it was riding off the side of the track. Curves are left alone because a
        /// curve genuinely runs diagonally through its cell.
        /// </summary>
        private static Point3D CentreOnRail(Point3D pos, int bx, int bz, int meta)
        {
            var (a, b) = RailShape.Connections(meta);
            // Straight and slope links are an opposite pair; that pair names the travel axis.
            if (a != b && ((a + 2) & 3) == b)
            {
                bool alongX = a == 0 || a == 2;

                // Only snap if the cart is actually inside this cell on BOTH axes. A cart that
                // has crossed into the neighbouring cell mid-tick (its coordinate on that axis
                // landing outside [cellOrigin, cellOrigin+1)) must not be yanked back - that is
                // what made reversing lurch: x sitting exactly on the cell boundary got
                // teleported to the cell centre, the cart never left, and it crept along the
                // corner instead of traversing it.
                double cx = pos.X - bx, cz = pos.Z - bz;
                bool insideX = cx > 1e-9 && cx < 1.0 - 1e-9;
                bool insideZ = cz > 1e-9 && cz < 1.0 - 1e-9;

                if (alongX && insideZ) return new Point3D(pos.X, pos.Y, bz + 0.5);
                if (!alongX && insideX) return new Point3D(bx + 0.5, pos.Y, pos.Z);
            }
            return pos;
        }

        // Travel directions. The opposite of direction i is (i + 2) % 4, so a straight pair is
        // i and i+2. Yaw is the engine's sense - movement is (sin(yaw), cos(yaw)) - so the yaw
        // that points along direction i is atan2(DirX[i], DirZ[i]).
        private static readonly int[] DirX = { 1, 0, -1, 0 };
        private static readonly int[] DirZ = { 0, 1, 0, -1 };

        /// <summary>Cardinal direction index the current yaw is pointing along.</summary>
        private static int DirFromYaw(float yaw)
        {
            double x = Math.Sin(yaw), z = Math.Cos(yaw);
            if (Math.Abs(x) >= Math.Abs(z)) return x >= 0 ? 0 : 2;
            return z >= 0 ? 1 : 3;
        }

        /// <summary>
        /// Snaps the cart's heading onto a real rail connection and returns the direction it
        /// settled on, or -1 when this cell has no rail neighbours at all.
        ///
        /// The previous version averaged the connected neighbours into one vector, which was
        /// wrong in both interesting cases. A straight track cancels to zero (rails at -X and
        /// +X give dx = 1-1 = 0), so the cart kept whatever heading it happened to hold and
        /// would drive perpendicular straight off the rails; and a corner produced a 45-degree
        /// diagonal that cut through empty space where there is no track. Treating the
        /// connected set as discrete exits fixes both: the cart takes the exit closest to
        /// where it is actually travelling - straight on when the track continues, otherwise
        /// the 90-degree turn.
        /// </summary>
        /// <summary>
        /// The body's forward AXIS as a cardinal direction index (0=+X, 1=+Z, 2=-X, 3=-Z), or -1
        /// until it is first resolved from yaw. Driving forward moves along _facing; reversing
        /// moves along _facing+2. It is kept as its own state rather than read back from yaw
        /// because on a curve the two decouple: backing through a bend must rotate the body by the
        /// SAME turn the travel direction took (a single 90-degree step) so the cart keeps rolling
        /// backward around the corner, and the next cell's exit - resolved off the rotated axis -
        /// points the way it is actually going. If the axis were left frozen (or re-derived from a
        /// frozen yaw), backing out of a corner would aim at the wrong exit, double back, and stall.
        /// It is never advanced by 180, so reversing on a straight does not spin the cart around.
        /// </summary>
        private int _facing = -1;

        /// <summary>
        /// The angle the body is currently LEANING at, in radians, positive nose-up. Purely
        /// cosmetic: the physics stays on the rail surface, this just rotates the drawn model so it
        /// lies along the ramp instead of sliding through it level. Settled toward its target each
        /// tick rather than snapped - see <see cref="UpdateVisualPitch"/>.
        /// </summary>
        private float _visualPitch;

        /// <summary>
        /// <see cref="IMobRenderable.BodyPitch"/>: the current lean angle, handed to the renderer
        /// (and to the seated rider, so a player tips with the cart instead of sitting upright in
        /// it). The rest of the game's mobs inherit the default of 0 and never change.
        /// </summary>
        public override float BodyPitch => _visualPitch;

        private int SnapHeadingToRail(ChunkManager cm, int bx, int by, int bz, ref float yaw, float travelSign)
        {
            int mask = 0;
            for (int i = 0; i < 4; i++)
            {
                int nx = bx + DirX[i], nz = bz + DirZ[i];
                if (IsRail(cm, nx, by, nz) || IsRail(cm, nx, by - 1, nz) || IsRail(cm, nx, by + 1, nz))
                    mask |= 1 << i;
            }
            if (mask == 0) return -1;

            // Resolve the body's forward axis the first time it is needed (or after a remount).
            if (_facing < 0) _facing = DirFromYaw(yaw);

            int facing = _facing;
            int intended = travelSign < 0f ? (facing + 2) & 3 : facing;

            int travel = intended;
            if ((mask & (1 << travel)) == 0)
            {
                // The intended exit is not connected, so this is a curve: take the connected exit
                // that keeps us going. +1 = straight on, 0 = a 90-degree turn, -1 = doubling back.
                // A real curve offers a turn (0); only a dead end offers nothing but the way we
                // came (-1), and doubling back is exactly what makes a cart bounce and stall, so
                // report a dead end and let the caller stop instead.
                int best = -1;
                double bestDot = -2.0;
                for (int i = 0; i < 4; i++)
                {
                    if ((mask & (1 << i)) == 0) continue;
                    double dot = DirX[i] * DirX[intended] + DirZ[i] * DirZ[intended];
                    if (dot > bestDot) { bestDot = dot; best = i; }
                }
                if (best < 0 || bestDot < 0.0) return -1;
                travel = best;
            }

            // Advance the body's forward axis by the SAME turn the travel direction just took. On
            // a straight that turn is zero, so reversing never spins the cart; through a curve it
            // is one 90-degree step, so the body follows the bend in the same sense its travel
            // did. Because `intended` is defined relative to `facing` both ways, this keeps the
            // body pointing opposite its travel while reversing, all the way around the corner.
            int turn = (travel - intended) & 3;
            _facing = (facing + turn) & 3;

            yaw = (float)Math.Atan2(DirX[_facing], DirZ[_facing]);
            return travel;
        }

        /// <summary>
        /// Eases the drawn heading onto <paramref name="target"/> along the shortest arc rather
        /// than cutting to it. A bend used to be a hard snap: the instant the cart entered a curve
        /// cell the model jumped a full 90 degrees in one frame while its position kept travelling,
        /// so the turn read as a glitch instead of a turn. The rate is tied to travel - pi/2 per
        /// block travelled is exactly one 90-degree swing per cell crossed - so the model finishes
        /// turning at about the moment it finishes crossing the bend, which lines the sweep up with
        /// CentreOnRail dragging it onto the rail's centreline through the same cell. The floor
        /// only ever matters at a standstill. Nothing physical reads Yaw (travel runs off _facing),
        /// so this is purely what the eye sees.
        /// </summary>
        private static float StepHeadingToward(float current, float target, float dt, float speed)
        {
            float delta = target - current;
            while (delta > (float)Math.PI) delta -= 2f * (float)Math.PI;
            while (delta < -(float)Math.PI) delta += 2f * (float)Math.PI;
            if (delta == 0f) return target;

            float rate = MathF.Max(TurnSettleRate, MathF.Abs(speed) * ((float)Math.PI * 0.5f));
            float step = rate * dt;
            if (step >= MathF.Abs(delta)) return target;
            return current + (delta > 0f ? step : -step);
        }

        public override void Update(float dt, ChunkManager manager)
        {
            if (IsDead) return;

            bool onRail = TryFindRail(manager, this, out int bx, out int ry, out int bz);

            // The rail cell we should lean to this tick; -1 means "no slope", i.e. level.
            int tiltAscend = -1;

            if (onRail)
            {
                int by = ry;   // the RAIL's Y, not the cart's
                manager.TryGetLoadedBlockAndMeta(bx, by, bz, out _, out byte railMeta);
                int railAscend = RailShape.AscendDirection(railMeta);

                // Drive: W/S throttle forward/back along the current heading. This runs BEFORE
                // the heading snap so the snap can see this tick's signed speed and know whether
                // the cart is rolling forwards or being shoved backwards.
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

                // Lock the heading onto a real rail connection. Rails are NOT solid, so the
                // wall test cannot by itself keep a cart on the track - before this existed the
                // cart only noticed it had left the rails on the FOLLOWING tick, by which
                // point it was already flying off in whatever direction it was pointing.
                float snappedYaw = Yaw;
                int dir = SnapHeadingToRail(manager, bx, by, bz, ref snappedYaw, Speed >= 0f ? 1f : -1f);
                // Ease onto the rail's direction instead of cutting to it, so a bend sweeps round
                // over the cell instead of snapping 90 degrees in a single frame. SnapHeadingToRail
                // wrote the TARGET into snappedYaw; the model is what catches up with it.
                Yaw = StepHeadingToward(Yaw, snappedYaw, dt, Speed);

                // A shove from the world (the separation pass writing _velX/_velZ) reaches the
                // cart only off the rails otherwise, where the fall path below integrates it. On a
                // rail the cart's motion is the scalar Speed along its heading, so fold the
                // along-track part of the shove into that and discard the rest: the rails hold the
                // cart on the line, and an unconsumed velocity would just sit and pile up tick
                // after tick. Skipped entirely by the minecart (see AcceptsExternalPush).
                if (AcceptsExternalPush)
                {
                    if (_facing >= 0 && (_velX != 0 || _velZ != 0))
                    {
                        double along = _velX * DirX[_facing] + _velZ * DirZ[_facing];
                        Speed += (float)(along * RailPushFactor);
                        Speed = Math.Clamp(Speed, -MaxSpeed, MaxSpeed);
                    }
                    _velX = 0;
                    _velZ = 0;
                }

                // Gravity along the track. A slope rises a full block across a single cell, so it
                // is a real 45-degree grade and g*sin(45) of its pull acts along the rails rather
                // than straight down. That is what makes the cart behave like a cart: park it on
                // a hill and it rolls off downhill, and riding up drains the speed you gained
                // coming down. Applied after the heading snap so the sign is resolved against
                // the direction actually travelled this tick.
                if (dir >= 0 && railAscend >= 0)
                {
                    int downhill = (railAscend + 2) & 3;

                    // Measure the pull along the body's FORWARD axis (_facing), NOT along `dir`.
                    // Speed is a signed scalar on that axis (positive = driving the way the cart
                    // faces), so gravity must accelerate it in axis terms. Using `dir` here is
                    // `_facing` when rolling forward but `_facing+2` when rolling backward, which
                    // inverts the sign exactly while reversing: a cart rolling backward downhill
                    // got pushed back uphill instead, so its Speed oscillated around zero and it
                    // hung stuck on the slope. Projected onto _facing, downhill is +1 when the cart
                    // faces downhill (roll on down) and -1 when it faces uphill (roll back down).
                    double along = DirX[downhill] * DirX[_facing] + DirZ[downhill] * DirZ[_facing];
                    Speed += (float)(along * Gravity * SlopeGrade * dt);
                    // Gravity must never outrun the throttle.
                    Speed = Math.Clamp(Speed, -MaxSpeed, MaxSpeed);
                }

                if (dir < 0)
                {
                    // Dead end: nothing continues from this cell, so stop rather than drift off.
                    Speed = 0f;
                }
                else if (Math.Abs(Speed) > 0.001f)
                {
                    // Sub-step so a fast cart cannot skip clean over a corner cell in one tick,
                    // and so the rail test below applies to every step and not just the end.
                    //
                    // dir already encodes the direction of TRAVEL: reversing picks the backward
                    // exit. The step is the MAGNITUDE of Speed (the sign is carried by dir).
                    double total = Math.Abs(Speed) * dt;
                    int steps = Math.Max(1, (int)Math.Ceiling(total / 0.05));
                    if (steps > 96) steps = 96;
                    double inc = total / steps;
                    for (int s = 0; s < steps; s++)
                    {
                        double nx = Position.X + DirX[dir] * inc;
                        double nz = Position.Z + DirZ[dir] * inc;
                        if (!TryRailTopAt(manager, nx, Position.Y, nz, out _, out _, out _, out _)) { Speed = 0f; break; }
                        Position = new Point3D(nx, Position.Y, nz);
                    }
                }

                // Sit on top of the rail surface rather than sinking through it. Re-find the cell
                // at the post-movement position: a fast cart can cross into the next cell
                // mid-tick, and the ramp must be measured across the cell it is actually over
                // now, not the one the tick started in.
                if (TryRailTopAt(manager, Position.X, Position.Y, Position.Z, out double railTop, out int cbx, out int cby, out int cbz))
                {
                    // Lock onto the rail centreline before settling the height.
                    int cmeta = manager.TryGetLoadedBlockAndMeta(cbx, cby, cbz, out _, out byte cm) ? cm : 0;
                    Position = CentreOnRail(Position, cbx, cbz, cmeta);
                    tiltAscend = RailShape.AscendDirection(cmeta);

                    // The rail surface IS the cart's height while it rides. Always settle onto it
                    // exactly and zero the vertical velocity.
                    //
                    // Letting gravity fly here instead is what made a long slope stutter: a ramp
                    // drops one block per block travelled, so at Speed 7 the cart must descend
                    // 7 blocks/s, but _velY only accelerates from zero at 20/s^2 and needs ~0.35s
                    // (~2.4 blocks) to reach that rate. Until it does the cart hangs ABOVE the
                    // surface; once it overshoots it plunges BELOW, snaps back, zeroes, and repeats
                    // - a downward hiccup every couple of blocks. Descent is already driven by the
                    // along-track gravity applied to Speed, so the surface can simply be obeyed.
                    Position = new Point3D(Position.X, railTop + 0.02, Position.Z);
                    _velY = 0;
                }
                else
                {
                    // Clean off the end of the track: fall and stop.
                    _facing = -1;
                    _velY -= Gravity * dt;
                    if (_velY < -MaxFallSpeed) _velY = -MaxFallSpeed;
                    MoveWithCollision(manager, Axis.X, _velX * dt);
                    MoveWithCollision(manager, Axis.Z, _velZ * dt);
                    MoveWithCollision(manager, Axis.Y, _velY * dt);
                    Speed = 0f;
                }
            }
            else
            {
                // Off-track: behave like a dropped item rather than a vehicle.
                _facing = -1;
                _velY -= Gravity * dt;
                if (_velY < -MaxFallSpeed) _velY = -MaxFallSpeed;
                MoveWithCollision(manager, Axis.X, _velX * dt);
                MoveWithCollision(manager, Axis.Z, _velZ * dt);
                MoveWithCollision(manager, Axis.Y, _velY * dt);
                Speed = 0f;
            }

            // Lean the drawn body onto the ramp (or level it out again off-track). Cosmetic only.
            UpdateVisualPitch(dt, tiltAscend);

            // Bleed off any leftover external shove so an off-rail nudge glides to a stop instead
            // of sliding forever - the base off-rail path has no friction of its own. On the rails
            // the shove was already folded into Speed and cleared above, so this is a no-op there.
            // The minecart never has one to bleed (AcceptsExternalPush is false).
            if (AcceptsExternalPush)
            {
                _velX *= Math.Pow(0.05, dt);
                _velZ *= Math.Pow(0.05, dt);
            }

            OnGround = onRail;
            if (Position.Y < -32) { Removed = true; return; }
        }

        /// <summary>
        /// Eases <see cref="_visualPitch"/> toward the angle of the rail under the cart. The target
        /// is the ramp's ascend direction projected onto the BODY'S FORWARD AXIS (_facing) rather
        /// than onto the direction of travel: what gets rotated is the body, and the two only agree
        /// while rolling forward - reversing down a hill would otherwise flip the sign and dig the
        /// nose in instead of lifting it. Independent of travel direction, so up and down look the
        /// same either way round. Eased exponentially instead of snapped because the entity ticks at
        /// 20Hz with no render interpolation: crossing a flat cell onto a ramp would otherwise pop
        /// the model to a hard 45 degrees in a single frame, and the same easing levels the cart off
        /// again once it is back on flat rail (ascend &lt; 0).
        /// </summary>
        private void UpdateVisualPitch(float dt, int ascend)
        {
            float target = 0f;
            if (ascend >= 0 && _facing >= 0)
            {
                int along = DirX[ascend] * DirX[_facing] + DirZ[ascend] * DirZ[_facing];
                if (along != 0) target = along > 0 ? SlopeTilt : -SlopeTilt;
            }
            _visualPitch += (target - _visualPitch) * (1f - (float)Math.Exp(-dt * 14.0));
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