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

        /// <summary>
        /// How hard the player can shove it when boarding and holding a direction. Virtual so the
        /// locomotive can run far faster than a hand-pushed minecart (~2x, see LocomotiveEntity).
        /// Only read from within this class, so exposing an instance value changes nothing else.
        /// </summary>
        public virtual float MaxSpeed => 7.0f;

        /// <summary>
        /// Blocks/second of thrust added per tick while a rider holds forward. A minecart's 9 is
        /// LESS than the ~14.1 that gravity pulls back down a 45-degree grade, which is exactly why
        /// a lone minecart can never climb a slope - the locomotive overrides this to overcome it.
        /// </summary>
        protected virtual float ThrottleAccel => 9f;

        /// <summary>
        /// Seconds of model-animation time advanced per block travelled, driving the rig's joint
        /// tracks (the locomotive's wheels and piston). 0 for the base rig-less minecart, whose
        /// model has no animation channels to feed.
        /// </summary>
        protected virtual float AnimSecondsPerBlock => 0f;

        /// <summary>Speed lost per second with nothing driving it.</summary>
        public const float CoastDecay = 2.2f;

        /// <summary>
        /// Fraction of gravity that acts ALONG the track rather than into it. A slope climbs a
        /// full block across one cell, i.e. a 45-degree grade, so sin(45 degrees). This is what
        /// drives the cart downhill on its own.
        /// </summary>
        private const double SlopeGrade = 0.7071067811865476;

        /// <summary>
        /// Ceiling on the drawn body lean, in radians (~69 degrees). A built slope only ever needs
        /// 45 (a full block across a cell), but a sagging span's gradient is whatever the solver
        /// settled on, so a hard clamp keeps the model from tipping past vertical on a violent sag.
        /// </summary>
        private const float MaxBodyPitch = 1.2f;

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
        /// Cart this one is rigidly coupled BEHIND, or null when it leads its own train (or is
        /// unattached). Set by the linkage flow (GameWorld.LinkCarts) and cleared ONLY when a cart
        /// is removed - no normal run (shoves, coasting, rail sag, despawn checks) ever severs a link.
        /// </summary>
        public CartEntity? Leader;

        /// <summary>Cart rigidly coupled behind this one, or null. The other half of a <see cref="Leader"/> link.</summary>
        public CartEntity? Follower;

        /// <summary>True while this cart is towed by a <see cref="Leader"/> (so <see cref="Update"/> is skipped).</summary>
        public bool IsTowed => Leader != null;

        /// <summary>
        /// The locomotive hauling this cart's train, or null when the train has none. Walks back
        /// through <see cref="Leader"/> to the head, then forward along the chain - the coupling code
        /// puts the locomotive at the head (see GameWorld.LinkCarts), but scanning the whole chain
        /// keeps this correct even if a save was written by an older build.
        /// </summary>
        public LocomotiveEntity? FindTrainLocomotive()
        {
            var head = this;
            for (int i = 0; i < 256 && head.Leader != null; i++) head = head.Leader;

            int guard = 0;
            for (CartEntity? c = head; c != null && guard++ < 256; c = c.Follower)
            {
                if (c is LocomotiveEntity loco) return loco;
            }
            return null;
        }

        /// <summary>
        /// Longitudinal gap a coupled trailer keeps behind its leader, in rail cells. A little over
        /// one cell leaves a clear coupler gap between the (0.9-wide) bodies, so the drawn link beam
        /// is actually visible between them - a true one-cell spacing would bury it in the carts.
        /// </summary>
        public const double LinkCellSpacing = 1.5;

        /// <summary>
        /// Fractions of <see cref="LinkCellSpacing"/> a trailer tries, in order, straight BEHIND the
        /// leader before falling back to walking the track cell by cell. The full gap wins normally;
        /// the tighter fraction catches a bend the straight-line offset cannot reach, so the trailer
        /// stays CLOSE to the head instead of leaving a long unsupported span to snap across.
        /// </summary>
        private static readonly double[] LinkOffsetScan = { 1.0, 0.7 };

        /// <summary>
        /// Vertical search window, in blocks, used when sampling the rail behind the head at the
        /// coupler gap. A slope climbs a full block per cell, so the full 1.5-block gap can sit 1.5
        /// blocks below (or above) the head's own layer - well outside the one-block window a cart
        /// uses for itself. Three blocks clears a 45-degree climb at the full gap with margin.
        /// </summary>
        private const int TrailerRailYRange = 3;

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
        ///
        /// <paramref name="yRange"/> widens that vertical window. The default of one block is
        /// right when the query point IS the cart, but a caller sampling a point offset ALONG the
        /// track (the train's coupler gap) has to allow for the climb: a slope rises a full block
        /// per cell, so 1.5 blocks back is 1.5 blocks down and a one-block window misses it -
        /// which silently dropped the towed car onto a cell centre instead (see FollowLeader).
        /// Rails cannot stack, so searching a column further is safe.
        /// </summary>
        private static bool TryFindRail(ChunkManager cm, double px, double py, double pz, out int rx, out int ry, out int rz, int yRange = 1)
        {
            int bx = (int)Math.Floor(px);
            int cy = (int)Math.Floor(py + 0.25);
            int bz = (int)Math.Floor(pz);
            // Search outward from the expected layer, lower first - the original order was
            // cy, cy-1, cy+1, so a range of 1 reproduces it exactly.
            if (IsRail(cm, bx, cy, bz)) { rx = bx; ry = cy; rz = bz; return true; }
            for (int d = 1; d <= yRange; d++)
            {
                if (IsRail(cm, bx, cy - d, bz)) { rx = bx; ry = cy - d; rz = bz; return true; }
                if (IsRail(cm, bx, cy + d, bz)) { rx = bx; ry = cy + d; rz = bz; return true; }
            }
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
        ///
        /// On a SAGGING straight span the height is sampled from the SAME two edge heights the
        /// mesher draws the plank from (<see cref="MeshEdges"/> mirrors Mesher.EmitRailFlat): each
        /// edge sits at the midpoint of this cell's droop and the rail neighbour's. Using the
        /// cell's own droop here instead made the ride height a per-cell step function - a smooth
        /// tilt over a choppy Y, most obvious across a long hanging span where neighbouring droops
        /// differ by a block or more. Sharing the edge height across the boundary makes the sampled
        /// surface continuous, so the cart (and the rider's camera pinned above it) glide.
        /// </summary>
        private static bool TryRailTopAt(ChunkManager cm, double px, double py, double pz, out double railTop, out int rbx, out int rby, out int rbz, int yRange = 1)
        {
            railTop = 0;
            if (!TryFindRail(cm, px, py, pz, out int bx, out int by, out int bz, yRange)) { rbx = rby = rbz = 0; return false; }
            rbx = bx; rby = by; rbz = bz;

            double top = by + BlockRegistry.RailHeight;
            cm.TryGetLoadedBlockAndMeta(bx, by, bz, out _, out byte meta);
            double ownDrop = DroopAt(cm, bx, by, bz);

            if (RailShape.IsSlope(meta))
            {
                // A ramp climbs a full block across its cell and, when it droops, sinks as a whole
                // (mirrors Mesher.EmitRailSlope): height ramps from the low edge up to the high one.
                railTop = top - ownDrop;
                switch (RailShape.AscendDirection(meta))
                {
                    case 0: railTop += px - bx; break;          // ascends East
                    case 2: railTop += 1.0 - (px - bx); break;  // ascends West
                    case 1: railTop += pz - bz; break;          // ascends South
                    case 3: railTop += 1.0 - (pz - bz); break;  // ascends North
                }
            }
            else if (MeshEdges(meta, out int posDir, out int negDir))
            {
                // Straight track: the drawn plank runs from the edge toward negDir up to the edge
                // toward posDir (+X / +Z), so the surface is the linear blend between the two edge
                // heights - continuous across cells because a shared edge is the same midpoint
                // seen from both sides. (Drooped rails sit over a gap, so following the drop down
                // never puts the cart inside a block.)
                bool axisZ = posDir == 1;
                double posTop = RailEdgeTop(cm, bx, by, bz, meta, ownDrop, posDir);
                double negTop = RailEdgeTop(cm, bx, by, bz, meta, ownDrop, negDir);
                double t = axisZ ? (pz - bz) : (px - bx);
                if (t < 0.0) t = 0.0; else if (t > 1.0) t = 1.0;
                railTop = negTop + (posTop - negTop) * t;
            }
            else
            {
                // Curve (or a degenerate straight): no single axis to ramp along - the drawn tile
                // sits level at the cell's own height.
                railTop = top - ownDrop;
            }
            return true;
        }

        /// <summary>
        /// The +X/+Z and -X/-Z ends a straight (or slope) rail's drawn surface runs between, or
        /// false for a curve - which has no single axis. <paramref name="posDir"/> is the +X/+Z end
        /// and <paramref name="negDir"/> the opposite, matching how Mesher.EmitRailFlat picks its
        /// two edge heights, so a caller blending between them samples the plank it actually sees.
        /// </summary>
        private static bool MeshEdges(int meta, out int posDir, out int negDir)
        {
            var (a, b) = RailShape.Connections(meta);
            if (!RailShape.IsCurve(meta) && a != b && ((a + 2) & 3) == b)
            {
                posDir = (a == 0 || a == 1) ? a : b;
                negDir = (a == 0 || a == 1) ? b : a;
                return true;
            }
            posDir = negDir = 0;
            return false;
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

        // ---- render-only smoothing ---------------------------------------------------------------
        // A coupled cart's rigid spot is recomputed from scratch each tick, so through a bend it
        // updates in whole-cell steps - the drawn model would teleport a cell sideways, and its yaw
        // would snap a full 90 degrees in one frame. These ease the DRAWN transform toward the rigid
        // one so a train swings round a corner instead. Physics (Position/Yaw) is untouched: the
        // coupling placement, the collision, and the beam PICKING all still read the exact rigid
        // spots; only what the eye sees is softened. Reset to the rigid transform whenever the cart
        // is not towed (see Update), so an uncoupled cart never renders at a stale eased spot.
        private double _visX, _visY, _visZ;
        private float _visYaw;
        private bool _visInit;

        /// <summary>
        /// Base rate (per second) the drawn cart chases its rigid spot, plus a speed term so a fast
        /// train rounds a bend in roughly the time it takes to cross the cell (a slow one is never
        /// sluggish). Large enough that the steady trailing offset on a straight is only a fraction
        /// of a block, so the coupler gap still reads as fixed.
        /// </summary>
        private const double RenderFollowRate = 10.0;

        /// <summary>Eased, render-only position of the drawn cart (see <see cref="RenderFollowRate"/>).</summary>
        public override Point3D RenderPosition => _visInit ? new Point3D(_visX, _visY, _visZ) : Position;

        /// <summary>Eased, render-only yaw of the drawn cart.</summary>
        public override float RenderYaw => _visInit ? _visYaw : Yaw;

        /// <summary>
        /// Eases the drawn transform toward the rigid one. Called every tick while this cart is towed
        /// (from <see cref="FollowLeader"/>); the first tick after a coupling snaps so a freshly
        /// linked cart never flies in from wherever it happened to be.
        /// </summary>
        private void SmoothRenderPose(float dt)
        {
            if (!_visInit)
            {
                _visX = Position.X; _visY = Position.Y; _visZ = Position.Z; _visYaw = Yaw;
                _visInit = true;
                return;
            }

            double rate = RenderFollowRate + Math.Abs(Speed) * 2.0;
            double k = 1.0 - Math.Exp(-rate * dt);
            _visX += (Position.X - _visX) * k;
            _visY += (Position.Y - _visY) * k;
            _visZ += (Position.Z - _visZ) * k;
            // Same angle-aware easing the driven cart uses for its body, so the swing matches.
            _visYaw = StepHeadingToward(_visYaw, Yaw, dt, Math.Abs(Speed));
        }

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

            // A towed cart is placed RIGIDLY by the train pass (EntityManager.ResolveTrainLinks ->
            // FollowLeader), so its own throttle/coast/gravity must never also run - that would
            // fight the coupling and let the cars drift apart. It is still drawn/updated as a body.
            if (Leader != null) return;

            // Not towed: the rigid transform IS the drawn transform, so drop any eased pose left over
            // from a coupling (otherwise an uncoupled cart would keep rendering at its last eased spot).
            _visInit = false;

            bool onRail = TryFindRail(manager, this, out int bx, out int ry, out int bz);

            // The lean the drawn body should settle to this tick, in radians (0 = level). Set from
            // the rail surface below; stays 0 off-track, on a curve, or on a level straight.
            float pitchTarget = 0f;

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
                        Speed += _driveFwd * ThrottleAccel * dt;
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
                    pitchTarget = RailSurfacePitch(manager, cbx, cby, cbz, cmeta);

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

            // Lean the drawn body onto the rail surface (or level it out again off-track). Cosmetic.
            UpdateVisualPitch(dt, pitchTarget);

            // Bleed off any leftover external shove so an off-rail nudge glides to a stop instead
            // of sliding forever - the base off-rail path has no friction of its own. On the rails
            // the shove was already folded into Speed and cleared above, so this is a no-op there.
            // The minecart never has one to bleed (AcceptsExternalPush is false).
            if (AcceptsExternalPush)
            {
                _velX *= Math.Pow(0.05, dt);
                _velZ *= Math.Pow(0.05, dt);
            }

            // Drive the model rig from DISTANCE travelled rather than wall-clock time, so the
            // locomotive's wheels and piston stay tied to how fast it is actually rolling (and spin
            // backwards when it reverses). A stopped locomotive freezes on its last pose because the
            // clock stops advancing; a rig-less minecart has no tracks to feed and skips this.
            if (AnimSecondsPerBlock > 0f)
            {
                _animClock += Speed * AnimSecondsPerBlock * dt;
                _animBlend = 1f;
            }

            OnGround = onRail;
            if (Position.Y < -32) { Removed = true; return; }
        }

        /// <summary>
        /// Cardinal direction from this cart to <paramref name="other"/> (0=+X, 1=+Z, 2=-X, 3=-Z),
        /// chosen on whichever horizontal axis dominates.
        /// </summary>
        private int DirectionTo(CartEntity other)
        {
            double dx = other.Position.X - Position.X;
            double dz = other.Position.Z - Position.Z;
            if (Math.Abs(dx) >= Math.Abs(dz)) return dx >= 0 ? 0 : 2;
            return dz >= 0 ? 1 : 3;
        }

        /// <summary>
        /// Turns the body so it faces away from <paramref name="other"/> - i.e. the other cart ends up
        /// directly BEHIND this one. Called once when a coupling is made, so the first rigid placement
        /// (one cell opposite the head's forward axis) lands the trailer on the back of the train
        /// rather than possibly in front of it.
        /// </summary>
        internal void FaceAwayFrom(CartEntity other)
        {
            int away = (DirectionTo(other) + 2) & 3;
            _facing = away;
            Yaw = (float)Math.Atan2(DirX[away], DirZ[away]);
        }

        /// <summary>
        /// Places this cart rigidly behind its <see cref="Leader"/>, so a coupled pair rolls as one
        /// train. Runs AFTER the leader has updated (see EntityManager.ResolveTrainLinks) and
        /// re-centres / height-samples through the SAME rail helpers the leader uses, so the train
        /// follows straights, curves, slopes and sag exactly as the leader does.
        ///
        /// The "behind" side is derived from the leader's rail SHAPE (<see cref="BackConnection"/>),
        /// NOT from its body axis. On a bend the leader's forward axis has already snapped onto the
        /// exit, so the naive opposite points ACROSS the rails - the candidate cell then holds no
        /// track, the trailer sits still until the gap grows wide enough to snap across, and a fast
        /// train reads as a stutter and can fling a car off the rails. Following the topology keeps
        /// the trailer on the track behind the head no matter which way the bend turns.
        ///
        /// If there is no rail behind the leader (a gap, or a dead end) the towed cart is simply left
        /// where it is; the coupling is NEVER broken here. Only a left-click on the beam, or a cart
        /// ceasing to exist, ends a link.
        /// </summary>
        internal void FollowLeader(ChunkManager cm, float dt)
        {
            if (Leader is not { } lead || lead.Removed || lead.IsDead) return;

            // The leader's ACTUAL rail cell and shape - the source of truth for "behind".
            if (!TryFindRail(cm, lead, out int lbx, out int lby, out int lbz)) return;
            int leadMeta = cm.TryGetLoadedBlockAndMeta(lbx, lby, lbz, out _, out byte lm) ? lm : 0;

            int leadFacing = lead._facing;
            if (leadFacing < 0) leadFacing = DirFromYaw(lead.Yaw);
            int back = BackConnection(leadMeta, leadFacing);
            double backX = DirX[back], backZ = DirZ[back];

            int bx = 0, by = 0, bz = 0;
            double px = 0, pz = 0;   // world point to re-centre through CentreOnRail
            bool haveCell = false;

            // 1) Rigid offset: the normal coupler gap, then a tighter one, taking the first that
            //    lands on rail. Keeping the trailer CLOSE means a bend never leaves a long span.
            //
            //    The vertical window is widened (TrailerRailYRange) because this point is offset
            //    ALONG the track rather than being the leader itself: on a slope the rail behind
            //    the head is a whole block lower per cell travelled, so the leader's Y plus a
            //    one-block window cannot reach it. When it failed, the trailer skipped the rigid
            //    placement entirely and got dropped on a CELL CENTRE by WalkBackOntoRail - which
            //    is why a towed car (and the camera of whoever was sitting in it) stepped a block
            //    at a time up a slope. Rails cannot stack, so the wider search is unambiguous.
            for (int i = 0; i < LinkOffsetScan.Length; i++)
            {
                double d = LinkCellSpacing * LinkOffsetScan[i];
                double wx = lead.Position.X + backX * d;
                double wz = lead.Position.Z + backZ * d;
                if (TryRailTopAt(cm, wx, lead.Position.Y, wz, out _, out int cx, out int cy, out int cz, TrailerRailYRange))
                {
                    bx = cx; by = cy; bz = cz; px = wx; pz = wz; haveCell = true; break;
                }
            }

            // 2) Walk the track: step back through connected rail cells so a tight bend no straight
            //    offset could reach still leaves the trailer sitting on a real rail.
            if (!haveCell)
            {
                if (!WalkBackOntoRail(cm, lbx, lby, lbz, back, out bx, out by, out bz)) return;
                px = bx + 0.5; pz = bz + 0.5;
            }

            int meta = cm.TryGetLoadedBlockAndMeta(bx, by, bz, out _, out byte m2) ? m2 : 0;
            Point3D centred = CentreOnRail(new Point3D(px, 0, pz), bx, bz, meta);
            if (!TryRailTopAt(cm, centred.X, by, centred.Z, out double top, out int fx, out int fy, out int fz, TrailerRailYRange))
                return;
            Position = new Point3D(centred.X, top + 0.02, centred.Z);

            // Face the heading of the train: from the trailer's cell toward the head's. When the two
            // share a cell (a very tight bend shrinks the gap into the leader's own cell) fall back
            // to the leader's own heading.
            int face = CardinalFromTo(fx, fz, lbx, lbz);
            _facing = face >= 0 ? face : leadFacing;
            Yaw = (float)Math.Atan2(DirX[_facing], DirZ[_facing]);
            Speed = lead.Speed;

            UpdateVisualPitch(dt, RailSurfacePitch(cm, fx, fy, fz, meta));
            OnGround = true;

            // Soften what the eye sees: ease the DRAWN transform toward the rigid one just set.
            SmoothRenderPose(dt);
        }

        /// <summary>
        /// The rail connection sitting BEHIND a cart whose forward axis is <paramref name="facing"/>.
        /// A straight/slope runs between an opposite pair, so this is simply the other end; a curve
        /// joins two perpendicular directions, so "behind" is the connection the cart did NOT exit
        /// through - the one it arrived on. Deriving it from the SHAPE (not from facing+2) is what
        /// stops a bend from pointing the trailer across the rails.
        /// </summary>
        private static int BackConnection(int meta, int facing)
        {
            var (a, b) = RailShape.Connections(meta);
            int want = (facing + 2) & 3;
            if (a == want) return a;
            if (b == want) return b;
            if (a == facing && b != a) return b;   // curve: arrived on b, leaving through a
            if (b == facing && a != b) return a;
            // facing is off-axis (or a degenerate shape): take whichever end is most nearly behind.
            int best = a;
            double bestDot = DirX[a] * DirX[want] + DirZ[a] * DirZ[want];
            double dotB = DirX[b] * DirX[want] + DirZ[b] * DirZ[want];
            if (dotB > bestDot) best = b;
            return best;
        }

        /// <summary>
        /// The connection of a rail shape OTHER than <paramref name="conn"/>. For a straight/slope
        /// pair this is the opposite end; for a curve it is the perpendicular exit on the far side of
        /// the bend - which is what lets a walk step across a corner cell and keep going instead of
        /// doubling back.
        /// </summary>
        private static int OtherConnection(int meta, int conn)
        {
            var (a, b) = RailShape.Connections(meta);
            if (conn == a && b != a) return b;
            if (conn == b && a != b) return a;
            return (conn + 2) & 3;
        }

        /// <summary>
        /// Steps back from the leader's rail cell along the TRACK (not straight through the air) up to
        /// the coupler gap, reporting the furthest rail cell reached. Stops at the first gap or dead
        /// end, so a trailer at the end of a line tucks into the last real rail cell behind the head
        /// rather than being placed on empty space. False when there is no rail at all behind.
        /// </summary>
        private static bool WalkBackOntoRail(ChunkManager cm, int cx, int cy, int cz, int firstBack, out int rx, out int ry, out int rz)
        {
            rx = cx; ry = cy; rz = cz;
            int steps = Math.Max(1, (int)Math.Round(LinkCellSpacing));
            int dir = firstBack;
            bool moved = false;
            for (int i = 0; i < steps; i++)
            {
                int nx = rx + DirX[dir], nz = rz + DirZ[dir];
                if (!TryFindRailY(cm, nx, ry, nz, out int ny)) break;
                rx = nx; ry = ny; rz = nz;
                moved = true;
                if (!cm.TryGetLoadedBlockAndMeta(rx, ry, rz, out _, out byte nm)) break;
                // Continue through the cell along its far connection (the one not entered from).
                dir = OtherConnection(nm, (dir + 2) & 3);
            }
            return moved;
        }

        /// <summary>
        /// Finds the rail block in column (x,z) nearest <paramref name="baseY"/> - this level, then one
        /// down, then one up - or false when the column holds no rail. Same vertical tolerance as
        /// <see cref="TryFindRail"/> so a coupled cart can follow its leader up a rise or down a ledge.
        /// </summary>
        private static bool TryFindRailY(ChunkManager cm, int x, int baseY, int z, out int y)
        {
            if (IsRail(cm, x, baseY, z)) { y = baseY; return true; }
            if (IsRail(cm, x, baseY - 1, z)) { y = baseY - 1; return true; }
            if (IsRail(cm, x, baseY + 1, z)) { y = baseY + 1; return true; }
            y = baseY; return false;
        }

        /// <summary>Cardinal direction (0=+X, 1=+Z, 2=-X, 3=-Z) from cell (fx,fz) to cell (tx,tz), or -1 when the two coincide.</summary>
        private static int CardinalFromTo(int fx, int fz, int tx, int tz)
        {
            int dx = tx - fx, dz = tz - fz;
            if (dx == 0 && dz == 0) return -1;
            if (Math.Abs(dx) >= Math.Abs(dz)) return dx >= 0 ? 0 : 2;
            return dz >= 0 ? 1 : 3;
        }

        /// <summary>
        /// Eases <see cref="_visualPitch"/> toward the angle the rail surface actually sits at,
        /// passed in as <paramref name="target"/> (see <see cref="RailSurfacePitch"/>, which folds in
        /// both a built slope AND a hanging span's droop). Eased exponentially instead of snapped
        /// because the entity ticks at 20Hz with no render interpolation: crossing a flat cell onto
        /// a ramp would otherwise pop the model to a hard angle in a single frame, and the same
        /// easing levels the cart off again once the surface is flat (target 0).
        /// </summary>
        private void UpdateVisualPitch(float dt, float target)
        {
            _visualPitch += (target - _visualPitch) * (1f - (float)Math.Exp(-dt * 14.0));
        }

        /// <summary>
        /// Angle the drawn body should lean to lie flush on the rail under it, in radians, positive
        /// = nose up along the BODY'S FORWARD AXIS (<see cref="_facing"/>). Measured from the SAME
        /// two edge heights the mesher draws the plank with - the rail's own ramp for a built slope,
        /// offset by the droop where the track meets each neighbour - so a cart tips down a hanging
        /// span exactly as the plank does, not just on a 45-degree ramp. The target is projected onto
        /// the body's forward axis rather than the direction of travel: what gets rotated is the
        /// body, and the two only agree while rolling forward - reversing down a hill would otherwise
        /// flip the sign and dig the nose in instead of lifting it. Zero on a curve (no single axis
        /// to lean on) and off-track.
        /// </summary>
        private float RailSurfacePitch(ChunkManager cm, int bx, int by, int bz, int meta)
        {
            if (_facing < 0 || RailShape.IsCurve(meta)) return 0f;

            var (a, b) = RailShape.Connections(meta);
            if (a == b || ((a + 2) & 3) != b) return 0f;   // not an opposite pair -> no single axis
            if (_facing != a && _facing != b) return 0f;    // facing across the track (mid-curve)

            double ownDrop = DroopAt(cm, bx, by, bz);
            int back = (_facing + 2) & 3;
            double dh = RailEdgeTop(cm, bx, by, bz, meta, ownDrop, _facing)
                      - RailEdgeTop(cm, bx, by, bz, meta, ownDrop, back);
            // The track runs one block across this cell, so the rise over it is the tangent directly.
            float pitch = (float)Math.Atan2(dh, 1.0);
            return Math.Clamp(pitch, -MaxBodyPitch, MaxBodyPitch);
        }

        /// <summary>
        /// Surface height where the track leaves this cell toward <paramref name="dir"/>: the rail
        /// top, plus the block the surface climbs if <paramref name="dir"/> is a slope's high end,
        /// minus the droop at that edge - the midpoint with a rail neighbour, or this cell's own
        /// droop at a free end. Mirrors Mesher.EmitRailFlat's RailEdgeDrop so the body and the drawn
        /// plank agree.
        /// </summary>
        private static double RailEdgeTop(ChunkManager cm, int bx, int by, int bz, int meta, double ownDrop, int dir)
        {
            double ramp = RailShape.AscendDirection(meta) == dir ? 1.0 : 0.0;
            int nx = bx + RailShape.DirX[dir], nz = bz + RailShape.DirZ[dir];
            double drop = ownDrop;
            if (cm.TryGetLoadedBlock(nx, by, nz, out int id) && id == RailId)
                drop = (ownDrop + DroopAt(cm, nx, by, nz)) * 0.5;
            return by + BlockRegistry.RailHeight + ramp - drop;
        }

        /// <summary>Current animated droop of a cell, in blocks (0 when it has none or is unloaded).</summary>
        private static double DroopAt(ChunkManager cm, int x, int y, int z)
            => cm.TryGetLoadedChunkAndDrop(x, y, z, out byte d) ? RailSag.Dequantize(d) : 0.0;

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