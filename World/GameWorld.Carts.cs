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
        private static int _cartItemIdCache = -1;
        private static int CartItemId
        {
            get { if (_cartItemIdCache < 0) _cartItemIdCache = ItemRegistry.TryGet("cart", out var d) ? d.NumericId : 0; return _cartItemIdCache; }
            set => _cartItemIdCache = value;
        }

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
                if (mob is not CartEntity cart) continue;
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
            cart.Occupied = true;
            cart._driveFwd = 0f;
            if (Math.Abs(cart.Yaw) > 0.001f) LocalPlayer.Yaw = cart.Yaw;
        }

        public void DismountCart()
        {
            if (RidingCart is { } c)
            {
                c.Occupied = false;
                c._driveFwd = 0f;
                double sx = c.Position.X + Math.Cos(c.Yaw) * 1.1;
                double sz = c.Position.Z - Math.Sin(c.Yaw) * 1.1;
                LocalPlayer.Position = new Point3D(sx, c.Position.Y + 0.2 + EyeHeight, sz);
                RidingCart = null;
            }
        }

        /// <summary>
        /// Moves the ridden cart and pins the player's eye to it. Runs instead of StepPlayer
        /// while mounted.
        /// </summary>
        public void StepRidingCart(TickInputState tickInput, float deltaSeconds)
        {
            if (RidingCart is not { } cart) { StepPlayer(LocalPlayer, tickInput, deltaSeconds); return; }

            cart._driveFwd = (tickInput.MoveForward ? 1f : 0f) - (tickInput.MoveBackward ? 1f : 0f);
            cart.Update(deltaSeconds, Chunks);

            LocalPlayer.Position = new Point3D(
                cart.Position.X,
                cart.Position.Y + CartSeatEyeHeight,
                cart.Position.Z);
        }
    }
}