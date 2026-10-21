using System;

namespace Cubuild
{
    /// <summary>
    /// Minecarts: setting one down on a rail, boarding it, and steering it.
    ///
    /// Same shape as the boat work: while occupied the player's own movement is suspended
    /// and their eye is pinned to the cart, so the cart - not the player - is what moves.
    /// </summary>
    public sealed partial class GameWorld : IDisposable
    {
        private static int CartItemId => ItemRegistry.TryGet("cart", out var d) ? d.NumericId : 0;

        private static int _railIdCache = -1;
        private static int RailBlockId
        {
            get { if (_railIdCache < 0) _railIdCache = BlockRegistry.GetId("rail"); return _railIdCache; }
            set => _railIdCache = value;
        }

        /// <summary>The cart the local player is currently riding, or null.</summary>
        public CartEntity? RidingCart { get; private set; }

        /// <summary>Distance from the cart floor to the rider's eyes.</summary>
        private const double CartSeatEyeHeight = 0.95;

        /// <summary>How far the crosshair may reach to grab a cart.</summary>
        private const double CartReach = 3.5;

        /// <summary>
        /// How quickly the rider's eye catches up to the cart's own height, in 1/seconds. The cart
        /// rides the rail surface EXACTLY, and on a slope that surface ramps a whole block across a
        /// cell - so at speed the body climbs at 7 (minecart) to 14 (locomotive) blocks per second,
        /// and every wobble in the frame time turns straight into vertical camera movement. Welding
        /// the eye rigidly to the body made those read as a jitter. Easing instead low-passes them:
        /// the eye trails a ramp by only Speed/rate blocks (~0.4 at full tilt, invisible from inside
        /// the seat) but a one-frame step lands spread over a few frames instead of as a snap.
        /// </summary>
        private const double RiderEyeSmoothRate = 18.0;

        /// <summary>
        /// A jump bigger than this is not jitter - it is mounting, dismounting, a teleport or a
        /// cart that just fell off a ledge. Snap to it rather than gliding, so the camera never
        /// swoops across the map trying to catch up.
        /// </summary>
        private const double RiderEyeSnapDistance = 1.5;

        /// <summary>
        /// How far the eased eye may trail BELOW the seat, in blocks. Lag is not symmetric: on a
        /// descent the eye floats a little ABOVE the seat (open air - invisible), but on a climb it
        /// trails BELOW it, and the seat is only ~0.2 above the cart's rim - so an unbounded climb
        /// lag drops the camera straight into the cart body (and into the ground, which is rising
        /// around you). Bounding the sink keeps the smoothing for small deltas while never letting
        /// the eye go far enough down to clip.
        /// </summary>
        private const double RiderEyeMaxSink = 0.2;

        private double _riderEyeY;
        private double _riderEyeTarget;
        private bool _riderEyeInit;

        public bool IsRidingCart => RidingCart != null;

        public bool HoldingCart => CartItemId > 0 && SelectedBlock == CartItemId;

        /// <summary>
        /// Right-click while holding a minecart sets it down on the rail under the crosshair
        /// (or on the block face you're pointing at, if it's a rail). Returns true when the
        /// click was consumed so the caller skips normal block placement.
        /// </summary>
        public bool TryPlaceCart(Point3D origin, Point3D direction)
        {
            if (!HoldingCart) return false;

            // Prefer the face we're aiming at, if that face belongs to a rail.
            var pick = TryPickBlock(origin, direction);
            if (pick.HasValue)
            {
                var rem = pick.Value.Remove;
                if (Chunks.TryGetLoadedBlock(rem.x, rem.y, rem.z, out int hitId) && hitId == RailBlockId)
                {
                    if (Entities.AddMobAt("cart", new Point3D(rem.x + 0.5, rem.y + 0.55, rem.z + 0.5), LocalPlayer.Yaw))
                    {
                        TryConsumeFromInventory(CartItemId, 1);
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>Boards the cart under the crosshair, or steps out of the current one.</summary>
        public bool ToggleCartUnderCrosshair(Point3D origin, Point3D direction)
        {
            if (RidingCart != null) { DismountCart(); return true; }

            var cart = FindCartUnderCrosshair(origin, direction);
            if (cart == null) return false;
            MountCart(cart);
            return true;
        }

        private CartEntity? FindCartUnderCrosshair(Point3D origin, Point3D direction)
        {
            CartEntity? best = null;
            double bestDist = CartReach;
            foreach (var mob in Entities.Mobs)
            {
                // Only boardable carts: the passive ore cart is a CartEntity too, but says
                // CanBeRidden = false, and right-clicking one must not seat the player.
                if (mob is not CartEntity cart || !cart.CanBeRidden) continue;
                double dx = cart.Position.X - origin.X;
                double dy = cart.Position.Y + cart.Height * 0.5 - origin.Y;
                double dz = cart.Position.Z - origin.Z;
                double along = dx * direction.X + dy * direction.Y + dz * direction.Z;
                if (along < 0 || along > CartReach) continue;
                double perp = Math.Sqrt(Math.Max(0, dx * dx + dy * dy + dz * dz - along * along));
                if (perp < 1.2 && along < bestDist) { bestDist = along; best = cart; }
            }
            return best;
        }

        public void MountCart(CartEntity cart)
        {
            DismountCart();
            RidingCart = cart;
            // Start the eye ease fresh so the first pin snaps onto the seat instead of gliding
            // across from wherever the player was standing.
            _riderEyeInit = false;
            Entities.LocallyDrivenMob = cart;
            cart.Occupied = true;
            cart._driveFwd = 0f;
            // CartEntity keeps its yaw in RADIANS (it feeds Math.Sin/Math.Atan2 directly),
            // but the player's yaw is in DEGREES - Simulation.cs converts with *PI/180. Copying
            // one into the other verbatim left the rider facing a nonsense heading after boarding.
            if (Math.Abs(cart.Yaw) > 0.001f)
            {
                float deg = cart.Yaw * 180f / (float)Math.PI;
                LocalPlayer.Yaw = (deg % 360f + 360f) % 360f;
            }
        }

        public void DismountCart()
        {
            if (RidingCart is { } c)
            {
                c.Occupied = false;
                c._driveFwd = 0f;
                // The train's engine was the one actually being driven, not the carriage the player
                // sat in (see StepRidingCart), so it has to be released too - otherwise a locomotive
                // would keep the last throttle input forever after its driver stepped off.
                if (c.FindTrainLocomotive() is { } loco)
                {
                    loco.Occupied = false;
                    loco._driveFwd = 0f;
                }
                else if (Entities.LocallyDrivenMob is LocomotiveEntity drivenLoco)
                {
                    // A destroyed cart may already have had its links severed. Clear the locomotive
                    // that was driving this rider before the coupling disappeared.
                    drivenLoco.Occupied = false;
                    drivenLoco._driveFwd = 0f;
                }
                double sx = c.Position.X + Math.Cos(c.Yaw) * 1.1;
                double sz = c.Position.Z - Math.Sin(c.Yaw) * 1.1;
                LocalPlayer.Position = new Point3D(sx, c.Position.Y + 0.2 + EyeHeight, sz);
                // Anything the player accumulated while carried (mob shoves, a stale fall) must
                // not be applied the instant they step off, or they rocket away from the track.
                LocalPlayer.Velocity = new Point3D(0, 0, 0);
                RidingCart = null;
                // Drop the eased eye so the next mount starts clean.
                _riderEyeInit = false;
                Entities.LocallyDrivenMob = null;
            }
        }

        /// <summary>
        /// Moves the ridden cart and pins the player's eye to it. Runs instead of StepPlayer
        /// while mounted.
        /// </summary>
        public void StepRidingCart(TickInputState tickInput, float deltaSeconds)
        {
            if (RidingCart is not { } cart) { StepPlayer(LocalPlayer, tickInput, deltaSeconds); return; }
            if (cart.Removed || cart.IsDead)
            {
                DismountCart();
                StepPlayer(LocalPlayer, tickInput, deltaSeconds);
                return;
            }

            float drive = (tickInput.MoveForward ? 1f : 0f) - (tickInput.MoveBackward ? 1f : 0f);

            // A carriage coupled to a LOCOMOTIVE is not a vehicle any more, it is freight: it is
            // placed rigidly behind the engine by the train pass (EntityManager.ResolveTrainLinks)
            // and its own Update would bail out anyway (Leader != null). So hand the throttle to the
            // locomotive and tick THAT instead - it is the train's only mover. With no locomotive in
            // the chain this is the plain minecart behaviour.
            var loco = cart.FindTrainLocomotive();
            if (loco != null && !ReferenceEquals(loco, cart))
            {
                cart._driveFwd = 0f;
                cart.Occupied = false;

                loco.Occupied = true;
                loco._driveFwd = drive;
                loco.Update(deltaSeconds, Chunks);

                // Entities.Update skips LocallyDrivenMob, so it must point at whatever was ticked.
                Entities.LocallyDrivenMob = loco;
            }
            else
            {
                cart._driveFwd = drive;
                cart.Update(deltaSeconds, Chunks);
                Entities.LocallyDrivenMob = cart;
            }

            PinRiderEye(deltaSeconds);
            // The rider is carried, not falling or walking, so their own velocity is meaningless
            // while mounted. Clearing it every tick means nothing can build up behind the scenes
            // and ambush them on the next dismount.
            LocalPlayer.Velocity = new Point3D(0, 0, 0);
        }

        /// <summary>
        /// Places the rider's eye on the cart they are sitting in. Called again after
        /// EntityManager.Update: a carriage hauled by a locomotive is a rigid trailer, and the train
        /// pass that places it only runs at the END of the entity tick (after StepRidingCart), so
        /// without this second pin the camera would trail the carriage by a frame.
        ///
        /// Only the HEIGHT is eased (see RiderEyeSmoothRate): the horizontal pin stays exact so
        /// steering keeps its feel, and a slope is the one place the body's motion is fast enough
        /// and vertical enough for frame jitter to be visible.
        ///
        /// Because both pins usually see the SAME target (a solo cart has not moved again by the
        /// end of the tick), the ease is skipped when the target is unchanged - otherwise each tick
        /// would ease twice, roughly doubling the rate and halving the smoothing.
        /// </summary>
        public void PinRiderEye(double dt)
        {
            if (RidingCart is not { } cart) return;

            double targetY = cart.Position.Y + CartSeatEyeHeight;
            if (!_riderEyeInit || Math.Abs(targetY - _riderEyeY) > RiderEyeSnapDistance)
            {
                // First pin of the ride, or a jump too big to be jitter: snap.
                _riderEyeY = targetY;
                _riderEyeInit = true;
                _riderEyeTarget = targetY;
            }
            else if (targetY != _riderEyeTarget)
            {
                if (dt > 0.0)
                {
                    double k = 1.0 - Math.Exp(-RiderEyeSmoothRate * dt);
                    _riderEyeY += (targetY - _riderEyeY) * k;
                }
                _riderEyeTarget = targetY;
            }

            // Never let the ease drop the eye far enough below the seat to sit inside the cart.
            if (_riderEyeY < targetY - RiderEyeMaxSink) _riderEyeY = targetY - RiderEyeMaxSink;

            LocalPlayer.Position = new Point3D(cart.Position.X, _riderEyeY, cart.Position.Z);
        }
    }
}
