using System;

namespace Cubuild
{
    /// <summary>
    /// Samples the block world to decide which pathfinding nodes are walkable. A node at (x,y,z)
    /// is walkable if the mob's body clears collidable blocks and there is a solid block to
    /// stand on (either directly below, or reachable via a 1-high step - the mob already steps up
    /// 0.45 blocks).
    ///
    /// The world is sampled through the ChunkManager. External-goal agents can require known cells:
    /// unloaded body cells are blocked and unloaded floor cells cannot support a route.
    /// </summary>
    public sealed class PathNodeProcessor
    {
        private readonly ChunkManager _manager;
        private readonly float _mobWidth;
        private readonly float _mobHeight;
        private readonly bool _requireLoadedCells;
        private readonly bool _canBreakBlocks;
        private readonly bool _requireMineableBreakBlocks;
        private readonly Func<int, bool>? _canBreakBlock;

        public PathNodeProcessor(ChunkManager manager, float mobWidth, float mobHeight,
            bool requireLoadedCells = false, bool canBreakBlocks = false,
            bool requireMineableBreakBlocks = false, Func<int, bool>? canBreakBlock = null)
        {
            _manager = manager;
            _mobWidth = mobWidth;
            _mobHeight = mobHeight;
            _requireLoadedCells = requireLoadedCells;
            _canBreakBlocks = canBreakBlocks;
            _requireMineableBreakBlocks = requireMineableBreakBlocks;
            _canBreakBlock = canBreakBlock;
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
            if (!IsBodyClearForStep(x, y, y, z)) return false;

            GetFootprint(x, z, out int minX, out int maxX, out int minZ, out int maxZ);

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
            // Upright door panels cannot support a mob's feet; their collision is handled
            // against the thin metadata-aware panel below, not as a solid floor voxel.
            return TryGetBlock(x, y, z, out int id) && BlockRegistry.IsSolid(id)
                && !BlockRegistry.IsDoor(id);
        }

        private bool IsBlocked(int x, int y, int z)
        {
            if (!TryGetBlock(x, y, z, out int id)) return true;
            return BlockRegistry.IsSolid(id)
                && !(_canBreakBlocks && IsPermittedBreakBlock(id));
        }

        /// <summary>
        /// Checks body clearance over a horizontal edge. The inclusive feet range covers both
        /// endpoints of a step (used for diagonal corner checks); ordinary nodes pass one Y value.
        /// </summary>
        internal bool IsBodyClearForStep(int x, int minFeetY, int maxFeetY, int z)
        {
            int bodyCells = Math.Max(1, (int)Math.Ceiling(_mobHeight));
            double halfWidth = _mobWidth * 0.5;
            double centerX = x + 0.5, centerZ = z + 0.5;
            return IsBodyClearInBounds(centerX - halfWidth, centerX + halfWidth,
                Math.Min(minFeetY, maxFeetY), Math.Max(minFeetY, maxFeetY) + bodyCells,
                centerZ - halfWidth, centerZ + halfWidth);
        }

        /// <summary>Checks only door panels along the movement between adjacent path nodes.
        /// Other block traversal keeps its existing node/corner checks; this catches thin door
        /// panels that neither endpoint's centered footprint overlaps.</summary>
        internal bool IsBodyClearAlongStep(int fromX, int fromFeetY, int fromZ,
            int toX, int toFeetY, int toZ)
        {
            int bodyCells = Math.Max(1, (int)Math.Ceiling(_mobHeight));
            double halfWidth = _mobWidth * 0.5;
            double fromCenterX = fromX + 0.5, toCenterX = toX + 0.5;
            double fromCenterZ = fromZ + 0.5, toCenterZ = toZ + 0.5;
            double minX = Math.Min(fromCenterX, toCenterX) - halfWidth;
            double maxX = Math.Max(fromCenterX, toCenterX) + halfWidth;
            double minY = Math.Min(fromFeetY, toFeetY);
            double maxY = Math.Max(fromFeetY, toFeetY) + bodyCells;
            double minZ = Math.Min(fromCenterZ, toCenterZ) - halfWidth;
            double maxZ = Math.Max(fromCenterZ, toCenterZ) + halfWidth;

            int x0 = (int)Math.Floor(minX), x1 = (int)Math.Floor(maxX - 1e-9);
            int y0 = (int)Math.Floor(minY), y1 = (int)Math.Floor(maxY - 1e-9);
            int z0 = (int)Math.Floor(minZ), z1 = (int)Math.Floor(maxZ - 1e-9);
            for (int bx = x0; bx <= x1; bx++)
            for (int by = y0; by <= y1; by++)
            for (int bz = z0; bz <= z1; bz++)
            {
                if (!TryGetBlockAndMeta(bx, by, bz, out int id, out byte meta)
                    || !BlockRegistry.IsSolid(id) || !BlockRegistry.IsDoor(id)) continue;
                foreach (var box in GameWorld.GetBlockCollisionBoxes(id, meta))
                {
                    if (GameWorld.CollisionBoxOverlapVolume(box, bx, by, bz,
                        minX, maxX, minY, maxY, minZ, maxZ) <= 0) continue;

                    // The ordinary zombie route may plan through a breakable obstruction and
                    // let its existing mining behavior handle it. The explicit emergency route
                    // instead emits a break waypoint for the panel it hit.
                    if (_canBreakBlocks && !_requireMineableBreakBlocks
                        && IsPermittedBreakBlock(id)) continue;
                    return false;
                }
            }
            return true;
        }

        internal bool TryGetEmergencyBreakBlockForStep(int fromX, int fromFeetY, int fromZ,
            int toX, int toFeetY, int toZ, out int blockX, out int blockY, out int blockZ,
            out int blockId, out float hardness)
        {
            blockX = blockY = blockZ = blockId = 0;
            hardness = 0f;
            if (!_requireMineableBreakBlocks) return false;

            int bodyCells = Math.Max(1, (int)Math.Ceiling(_mobHeight));
            double halfWidth = _mobWidth * 0.5;
            double fromCenterX = fromX + 0.5, toCenterX = toX + 0.5;
            double fromCenterZ = fromZ + 0.5, toCenterZ = toZ + 0.5;
            double minX = Math.Min(fromCenterX, toCenterX) - halfWidth;
            double maxX = Math.Max(fromCenterX, toCenterX) + halfWidth;
            double minY = Math.Min(fromFeetY, toFeetY);
            double maxY = Math.Max(fromFeetY, toFeetY) + bodyCells;
            double minZ = Math.Min(fromCenterZ, toCenterZ) - halfWidth;
            double maxZ = Math.Max(fromCenterZ, toCenterZ) + halfWidth;
            int x0 = (int)Math.Floor(minX), x1 = (int)Math.Floor(maxX - 1e-9);
            int y0 = (int)Math.Floor(minY), y1 = (int)Math.Floor(maxY - 1e-9);
            int z0 = (int)Math.Floor(minZ), z1 = (int)Math.Floor(maxZ - 1e-9);
            for (int bx = x0; bx <= x1; bx++)
            for (int by = y0; by <= y1; by++)
            for (int bz = z0; bz <= z1; bz++)
            {
                if (!TryGetBlockAndMeta(bx, by, bz, out int id, out byte meta)
                    || !BlockRegistry.IsDoor(id) || !BlockRegistry.IsSolid(id)
                    || !IsPermittedBreakBlock(id)) continue;
                foreach (var box in GameWorld.GetBlockCollisionBoxes(id, meta))
                {
                    if (GameWorld.CollisionBoxOverlapVolume(box, bx, by, bz,
                        minX, maxX, minY, maxY, minZ, maxZ) <= 0) continue;
                    blockX = bx;
                    blockY = by;
                    blockZ = bz;
                    blockId = id;
                    hardness = BlockRegistry.HardnessOf(id);
                    return true;
                }
            }
            return false;
        }

        private bool IsBodyClearInBounds(double minX, double maxX, double minY, double maxY,
            double minZ, double maxZ)
        {
            int x0 = (int)Math.Floor(minX), x1 = (int)Math.Floor(maxX - 1e-9);
            int y0 = (int)Math.Floor(minY), y1 = (int)Math.Floor(maxY - 1e-9);
            int z0 = (int)Math.Floor(minZ), z1 = (int)Math.Floor(maxZ - 1e-9);
            for (int bx = x0; bx <= x1; bx++)
            for (int by = y0; by <= y1; by++)
            for (int bz = z0; bz <= z1; bz++)
            {
                if (!TryGetBlockAndMeta(bx, by, bz, out int id, out byte meta)) return false;
                if (!BlockRegistry.IsSolid(id)) continue;

                if (BlockRegistry.IsDoor(id))
                {
                    foreach (var box in GameWorld.GetBlockCollisionBoxes(id, meta))
                    {
                        if (GameWorld.CollisionBoxOverlapVolume(box, bx, by, bz,
                            minX, maxX, minY, maxY, minZ, maxZ) <= 0) continue;

                        // Keep the existing emergency-break route semantics: a permitted
                        // breakable blocker can still be planned as a mining waypoint.
                        if (_canBreakBlocks && IsPermittedBreakBlock(id)) continue;
                        return false;
                    }
                }
                else if (IsBlocked(bx, by, bz))
                {
                    return false;
                }
            }
            return true;
        }

        private void GetFootprint(int x, int z, out int minX, out int maxX, out int minZ, out int maxZ)
        {
            double cx = x + 0.5, cz = z + 0.5;
            minX = (int)Math.Floor(cx - _mobWidth * 0.5);
            maxX = (int)Math.Floor(cx + _mobWidth * 0.5 - 0.001);
            minZ = (int)Math.Floor(cz - _mobWidth * 0.5);
            maxZ = (int)Math.Floor(cz + _mobWidth * 0.5 - 0.001);
        }

        private bool TryGetBlock(int x, int y, int z, out int id)
        {
            if (y < -300 || y > 1000)
            {
                id = BlockRegistry.AirId;
                return true;
            }

            if (_requireLoadedCells)
                return _manager.TryGetLoadedBlock(x, y, z, out id);

            id = _manager.GetBlockAt(x, y, z);
            return true;
        }

        private bool TryGetBlockAndMeta(int x, int y, int z, out int id, out byte meta)
        {
            if (y < -300 || y > 1000)
            {
                id = BlockRegistry.AirId;
                meta = 0;
                return true;
            }

            if (_requireLoadedCells)
                return _manager.TryGetLoadedBlockAndMeta(x, y, z, out id, out meta);

            id = _manager.GetBlockAt(x, y, z);
            meta = _manager.GetMetaAt(x, y, z);
            return true;
        }

        /// <summary>Cost penalty for pathing through a breakable block. 0 = free, >0 = added cost.</summary>
        public float BlockCostMalus(int x, int y, int z)
        {
            if (!_canBreakBlocks) return 0f;

            GetFootprint(x, z, out int minX, out int maxX, out int minZ, out int maxZ);
            int bodyCells = Math.Max(1, (int)Math.Ceiling(_mobHeight));
            float malus = 0f;
            for (int bx = minX; bx <= maxX; bx++)
            {
                for (int bz = minZ; bz <= maxZ; bz++)
                {
                    for (int by = y; by < y + bodyCells; by++)
                    {
                        if (!TryGetBlock(bx, by, bz, out int id)
                            || !BlockRegistry.IsSolid(id) || !IsPermittedBreakBlock(id)) continue;
                        if (BlockRegistry.IsDoor(id)
                            && (!TryGetBlockAndMeta(bx, by, bz, out _, out byte doorMeta)
                                || !DoorPanelOverlapsNode(x, y, z, bx, by, bz, id, doorMeta))) continue;

                        if (_requireMineableBreakBlocks)
                        {
                            // This route is only considered after ordinary routing failed. Keep
                            // every break waypoint overwhelmingly expensive, independent of the
                            // legacy zombie speed ranking.
                            float hardness = BlockRegistry.HardnessOf(id);
                            malus += 1000f + Math.Min(hardness, 100000f) * 10f;
                        }
                        else
                        {
                            malus += BlockRegistry.ZombieBreakSpeedOf(id) switch
                            {
                                ZombieBreakSpeed.Fast => 3f,
                                ZombieBreakSpeed.Medium => 8f,
                                ZombieBreakSpeed.Slow => 16f,
                                _ => 0f,
                            };
                        }
                    }
                }
            }
            return malus;
        }

        internal bool TryGetEmergencyBreakBlockForNode(int x, int y, int z,
            out int blockX, out int blockY, out int blockZ, out int blockId, out float hardness)
        {
            blockX = blockY = blockZ = blockId = 0;
            hardness = 0f;
            if (!_requireMineableBreakBlocks) return false;

            GetFootprint(x, z, out int minX, out int maxX, out int minZ, out int maxZ);
            int bodyCells = Math.Max(1, (int)Math.Ceiling(_mobHeight));
            for (int by = y; by < y + bodyCells; by++)
            {
                for (int bx = minX; bx <= maxX; bx++)
                {
                    for (int bz = minZ; bz <= maxZ; bz++)
                    {
                        if (!TryGetBlock(bx, by, bz, out int id)
                            || !BlockRegistry.IsSolid(id) || !IsPermittedBreakBlock(id)) continue;
                        if (BlockRegistry.IsDoor(id)
                            && (!TryGetBlockAndMeta(bx, by, bz, out _, out byte doorMeta)
                                || !DoorPanelOverlapsNode(x, y, z, bx, by, bz, id, doorMeta))) continue;
                        blockX = bx; blockY = by; blockZ = bz; blockId = id;
                        hardness = BlockRegistry.HardnessOf(id);
                        return true;
                    }
                }
            }
            return false;
        }

        private bool DoorPanelOverlapsNode(int nodeX, int feetY, int nodeZ,
            int blockX, int blockY, int blockZ, int blockId, byte meta)
        {
            double halfWidth = _mobWidth * 0.5;
            double centerX = nodeX + 0.5, centerZ = nodeZ + 0.5;
            double minX = centerX - halfWidth, maxX = centerX + halfWidth;
            double minY = feetY, maxY = feetY + Math.Max(1, (int)Math.Ceiling(_mobHeight));
            double minZ = centerZ - halfWidth, maxZ = centerZ + halfWidth;
            foreach (var box in GameWorld.GetBlockCollisionBoxes(blockId, meta))
            {
                if (GameWorld.CollisionBoxOverlapVolume(box, blockX, blockY, blockZ,
                    minX, maxX, minY, maxY, minZ, maxZ) > 0) return true;
            }
            return false;
        }

        internal bool HasEmergencyBreakBlockForStep(int x, int minFeetY, int maxFeetY, int z)
        {
            if (!_requireMineableBreakBlocks) return false;
            for (int feetY = Math.Min(minFeetY, maxFeetY); feetY <= Math.Max(minFeetY, maxFeetY); feetY++)
            {
                if (TryGetEmergencyBreakBlockForNode(x, feetY, z,
                    out _, out _, out _, out _, out _)) return true;
            }
            return false;
        }

        private bool IsPermittedBreakBlock(int id)
        {
            if (!BlockRegistry.ZombieCanBreakOf(id) || (_canBreakBlock != null && !_canBreakBlock(id))) return false;
            if (!_requireMineableBreakBlocks) return true;
            float hardness = BlockRegistry.HardnessOf(id);
            return hardness > 0f && !float.IsNaN(hardness) && !float.IsInfinity(hardness);
        }
    }
}
