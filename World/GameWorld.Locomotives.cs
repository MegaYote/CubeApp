using System;

namespace Cubuild
{
    /// <summary>
    /// Locomotives: setting one down on a rail. The same placement as the ore cart (see
    /// GameWorld.OreCarts.cs) - it is a cart type, not a spawn - but there is no boarding, because
    /// the player drives it by coupling a carriage to it and sitting in that instead.
    /// </summary>
    public sealed partial class GameWorld
    {
        private static int LocomotiveItemId => ItemRegistry.TryGet("locomotive", out var d) ? d.NumericId : 0;

        public bool HoldingLocomotive => LocomotiveItemId > 0 && SelectedBlock == LocomotiveItemId;

        /// <summary>
        /// Right-click while holding a locomotive sets it down on the rail under the crosshair
        /// (or on the aimed face, if that face is a rail). Returns true when the click was consumed,
        /// so the caller skips normal block placement.
        /// </summary>
        public bool TryPlaceLocomotive(Point3D origin, Point3D direction)
        {
            if (!HoldingLocomotive) return false;

            // Prefer the face we're aiming at, if that face belongs to a rail.
            var pick = TryPickBlock(origin, direction);
            if (pick.HasValue)
            {
                var rem = pick.Value.Remove;
                if (Chunks.TryGetLoadedBlock(rem.x, rem.y, rem.z, out int hitId) && hitId == RailBlockId)
                {
                    if (Entities.AddMobAt("locomotivehot", new Point3D(rem.x + 0.5, rem.y + 0.55, rem.z + 0.5), LocalPlayer.Yaw))
                    {
                        TryConsumeFromInventory(LocomotiveItemId, 1);
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
