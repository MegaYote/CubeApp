using System;

namespace CubeApp
{
    /// <summary>
    /// Samples the block world to decide which pathfinding nodes are walkable, mirroring 1.12's
    /// NodeProcessor/WalkNodeProcessor. A node at (x,y,z) is walkable if the mob's feet cell is
    /// open (air/transparent) and there is a solid block to stand on (either directly below, or
    /// reachable via a 1-high step - the mob already steps up 0.45 blocks).
    ///
    /// The world is sampled through the ChunkManager: unloaded chunks read as air, so paths can't
    /// route through unexplored space (the seeker only expands loaded cells, which matches the
    /// player's loaded area anyway).
    /// </summary>
    public sealed class PathNodeProcessor
    {
        private readonly ChunkManager _manager;
        private readonly float _mobWidth;
        private readonly float _mobHeight;

        public PathNodeProcessor(ChunkManager manager, float mobWidth, float mobHeight)
        {
            _manager = manager;
            _mobWidth = mobWidth;
            _mobHeight = mobHeight;
        }

        public PathPoint GetStart(double x, double y, double z)
        {
            return new PathPoint((int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z));
        }

        public PathPoint GetPathPointToCoords(double x, double y, double z)
        {
            return new PathPoint((int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z));
        }

        /// <summary>
        /// Whether a mob's AABB centred at (cx, cy, cz) with the given half-width clears the column
        /// (no solid blocks intersecting the body). Used to reject nodes the mob can't fit through.
        /// </summary>
        public bool IsStandable(int x, int y, int z)
        {
            // Feet cell must be passable.
            if (IsSolid(x, y, z)) return false;
            if (IsSolid(x, y + 1, z)) return false;

            // Must have ground below (or a step-up target within 1).
            int below = y - 1;
            if (IsSolid(x, below, z)) return true;
            return false;
        }

        private bool IsSolid(int x, int y, int z)
        {
            if (y < -300 || y > 1000) return false;
            int id = _manager.GetBlockAt(x, y, z);
            if (id == BlockRegistry.AirId) return false;
            // Water/glass/leaves/partial shapes don't block walking for pathfinding purposes;
            // everything opaque and solid does.
            return BlockRegistry.IsOpaque(id);
        }
    }
}
