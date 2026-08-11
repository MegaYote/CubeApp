using System;

namespace CubeApp
{
    /// <summary>
    /// Samples the block world to decide which pathfinding nodes are walkable. A node at (x,y,z)
    /// is walkable if the mob's feet cell is open (air/transparent) and there is a solid block to
    /// stand on (either directly below, or reachable via a 1-high step - the mob already steps up
    /// 0.45 blocks).
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
            // Check every block the mob's body occupies at this position (not just the center cell).
            double cx = x + 0.5, cz = z + 0.5;
            int minX = (int)Math.Floor(cx - _mobWidth * 0.5);
            int maxX = (int)Math.Floor(cx + _mobWidth * 0.5 - 0.001);
            int minZ = (int)Math.Floor(cz - _mobWidth * 0.5);
            int maxZ = (int)Math.Floor(cz + _mobWidth * 0.5 - 0.001);
            for (int bx = minX; bx <= maxX; bx++)
            {
                for (int bz = minZ; bz <= maxZ; bz++)
                {
                    // Body cells (feet + headroom) must be passable (or breakable).
                    for (int by = y; by <= y + 1 && by < y + (int)_mobHeight + 1; by++)
                    {
                        if (IsSolid(bx, by, bz)) return false;
                    }
                }
            }

            // Must have at least one solid block below across the footprint to stand on.
            int below = y - 1;
            for (int bx = minX; bx <= maxX; bx++)
            {
                for (int bz = minZ; bz <= maxZ; bz++)
                {
                    if (IsSolid(bx, below, bz)) return true;
                }
            }
            return false;
        }

        private bool IsSolid(int x, int y, int z)
        {
            if (y < -300 || y > 1000) return false;
            int id = _manager.GetBlockAt(x, y, z);
            if (id == BlockRegistry.AirId) return false;
            if (BlockRegistry.IsOpaque(id))
            {
                // Breakable blocks are NOT solid for pathfinding — they're passable with a
                // cost penalty applied by the pathfinder via BlockCostMalus.
                if (BlockRegistry.ZombieCanBreakOf(id)) return false;
                return true;
            }
            return false;
        }

        /// <summary>Cost penalty for pathing through a breakable block. 0 = free, >0 = added cost.</summary>
        public float BlockCostMalus(int x, int y, int z)
        {
            int id = _manager.GetBlockAt(x, y, z);
            if (id == BlockRegistry.AirId || !BlockRegistry.IsOpaque(id)) return 0f;
            if (!BlockRegistry.ZombieCanBreakOf(id)) return 0f;
            var speed = BlockRegistry.ZombieBreakSpeedOf(id);
            return speed switch
            {
                ZombieBreakSpeed.Fast => 3f,
                ZombieBreakSpeed.Medium => 8f,
                ZombieBreakSpeed.Slow => 16f,
                _ => 0f,
            };
        }
    }
}
