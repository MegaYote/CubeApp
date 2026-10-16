using System;

namespace Cubuild
{
    /// <summary>
    /// Ore carts: setting one down on a rail. Exactly the minecart's placement (see
    /// GameWorld.Carts.cs) minus the boarding - an ore cart has no seat and no throttle, so
    /// there is nothing to mount. Its only control is physical: walk into it to shove it.
    /// </summary>
    public sealed partial class GameWorld : IDisposable
    {
        private static int _oreCartItemIdCache = -1;
        private static int OreCartItemId
        {
            get { if (_oreCartItemIdCache < 0) _oreCartItemIdCache = ItemRegistry.TryGet("orecart", out var d) ? d.NumericId : 0; return _oreCartItemIdCache; }
            set => _oreCartItemIdCache = value;
        }

        public bool HoldingOreCart => OreCartItemId > 0 && SelectedBlock == OreCartItemId;

        /// <summary>
        /// Right-click while holding an ore cart sets it down on the rail under the crosshair
        /// (or on the block face you're pointing at, if it's a rail). Returns true when the
        /// click was consumed so the caller skips normal block placement. Unlike
        /// <see cref="TryPlaceCart"/> there is no matching board toggle: ore carts stay put.
        /// </summary>
        public bool TryPlaceOreCart(Point3D origin, Point3D direction)
        {
            if (!HoldingOreCart) return false;

            // Prefer the face we're aiming at, if that face belongs to a rail.
            var pick = TryPickBlock(origin, direction);
            if (pick.HasValue)
            {
                var rem = pick.Value.Remove;
                if (Chunks.TryGetLoadedBlock(rem.x, rem.y, rem.z, out int hitId) && hitId == RailBlockId)
                {
                    if (Entities.AddMobAt("orecart", new Point3D(rem.x + 0.5, rem.y + 0.55, rem.z + 0.5), LocalPlayer.Yaw))
                    {
                        TryConsumeFromInventory(OreCartItemId, 1);
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
