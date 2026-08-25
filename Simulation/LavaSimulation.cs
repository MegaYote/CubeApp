using System;

namespace Cubuild
{
    /// <summary>
    /// Cellular liquid spreading for lava blocks.
    ///
    /// Similar to water but:
    /// - Flows slower (tick rate 10 instead of 5)
    /// - Flows shorter distance (max level 4 instead of 7)
    /// - Doesn't create infinite sources from adjacent sources
    /// - Emits light (handled by block lightEmission)
    /// - Higher viscosity - spreads less aggressively
    /// </summary>
    public sealed class LavaSimulation
    {
        public const int FluidTypeLava = 2;
        public const int LavaTickRate = 10; // Slower than water (5)

        // Lava-specific limits - shorter flow distance, higher viscosity
        private const int MaxLevel = 4;           // Lava only flows 4 blocks out instead of 7
        private const int FallingFlag = 8;        // Same flag for falling streams
        private const int MultiSource = 3;        // Lava doesn't easily create infinite sources
        private const int FlowSearchDepth = 3;    // Shorter search depth for sideways flow
        private const int InfiniteCost = 1000;    // Sentinel for "no path this way"

        private readonly ChunkManager _manager;
        private readonly BlockTickScheduler _tickScheduler;
        private readonly int _lavaId;

        // Reusable scratch for the four horizontal directions
        private readonly int[] _cost = new int[4];
        private readonly bool[] _best = new bool[4];

        public LavaSimulation(ChunkManager manager, BlockTickScheduler tickScheduler)
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
            _tickScheduler = tickScheduler ?? throw new ArgumentNullException(nameof(tickScheduler));
            _lavaId = BlockRegistry.GetId("lava");
        }

        /// <summary>Wakes nearby lava whenever any block changes.</summary>
        public void OnBlockChanged(int x, int y, int z)
        {
            if (AtIsLava(x, y, z))
            {
                _tickScheduler.Schedule(x, y, z, LavaTickRate);
            }

            PokeNeighbours(x, y, z);
        }

        /// <summary>One scheduled update of a lava cell.</summary>
        public void TickBlock(int x, int y, int z)
        {
            if (!AtIsLava(x, y, z))
            {
                return;
            }

            int currentLevel = LevelAt(x, y, z);

            // --- Recompute this cell's desired level from its surroundings ---
            int desired = currentLevel;
            if (currentLevel > 0)
            {
                ClearSourceCount();
                int weakest = WeakestNeighbourLevel(x, y, z);
                desired = weakest + FluidTypeLava;
                if (desired > MaxLevel || weakest < 0)
                {
                    desired = -1; // too weak / cut off -> the cell dries up
                }

                // Falling lava directly above forces this cell to become a falling stream.
                int aboveLevel = LevelAt(x, y + 1, z);
                if (aboveLevel >= 0)
                {
                    desired = aboveLevel >= FallingFlag ? aboveLevel : aboveLevel + FallingFlag;
                }

                // Three adjacent sources sustain an infinite source here (lava is harder to make infinite).
                if (AdjacentSourceCount() >= MultiSource)
                {
                    desired = 0;
                }

                ApplyNewLevel(x, y, z, desired, ref currentLevel);
            }

            // --- Gravity: fall into the cell below when open ---
            if (CanFallInto(x, y - 1, z))
            {
                int fallLevel = currentLevel >= FallingFlag ? currentLevel : currentLevel + FallingFlag;
                WriteLava(x, y - 1, z, fallLevel);
                return;
            }

            // --- Sideways spread along the cheapest route ---
            // Lava only spreads when on solid ground or when it's a source
            if (currentLevel >= 0 && (currentLevel == 0 || BelowIsSolid(x, y - 1, z)))
            {
                int spreadLevel = currentLevel + FluidTypeLava;
                if (currentLevel >= FallingFlag)
                {
                    spreadLevel = 1;
                }
                if (spreadLevel > MaxLevel)
                {
                    return;
                }

                var dirs = BestFlowDirections(x, y, z);
                if (dirs[0]) FlowInto(x - 1, y, z, spreadLevel);
                if (dirs[1]) FlowInto(x + 1, y, z, spreadLevel);
                if (dirs[2]) FlowInto(x, y, z - 1, spreadLevel);
                if (dirs[3]) FlowInto(x, y, z + 1, spreadLevel);
            }
        }

        private void ApplyNewLevel(int x, int y, int z, int desired, ref int currentLevel)
        {
            if (desired == currentLevel)
            {
                return; // equilibrium: stay dormant until a neighbour changes
            }

            currentLevel = desired;
            if (desired < 0)
            {
                RemoveLavaCell(x, y, z);
            }
            else
            {
                SetMetaOnly(x, y, z, desired);
            }
        }

        private void FlowInto(int x, int y, int z, int level)
        {
            if (CanFallInto(x, y, z))
            {
                WriteLava(x, y, z, level);
            }
        }

        private void WriteLava(int x, int y, int z, int level)
        {
            if (!_manager.TrySetBlockLoadedOnly(x, y, z, _lavaId, level))
            {
                return;
            }

            _tickScheduler.Schedule(x, y, z, LavaTickRate);
            PokeNeighbours(x, y, z);
        }

        private void SetMetaOnly(int x, int y, int z, int level)
        {
            if (!_manager.TrySetBlock(x, y, z, _lavaId, level))
            {
                return;
            }

            _tickScheduler.Schedule(x, y, z, LavaTickRate);
            PokeNeighbours(x, y, z);
        }

        private void RemoveLavaCell(int x, int y, int z)
        {
            if (!_manager.TrySetBlock(x, y, z, BlockRegistry.AirId, 0))
            {
                return;
            }

            PokeNeighbours(x, y, z);
        }

        private void PokeNeighbours(int x, int y, int z)
        {
            WakeIfLava(x + 1, y, z);
            WakeIfLava(x - 1, y, z);
            WakeIfLava(x, y + 1, z);
            WakeIfLava(x, y - 1, z);
            WakeIfLava(x, y, z + 1);
            WakeIfLava(x, y, z - 1);
        }

        private void WakeIfLava(int x, int y, int z)
        {
            if (AtIsLava(x, y, z))
            {
                _tickScheduler.Schedule(x, y, z, LavaTickRate);
            }
        }

        private int BlockIdAt(int x, int y, int z) => _manager.GetBlockAt(x, y, z);
        private int MetaAt(int x, int y, int z) => _manager.GetMetaAt(x, y, z);
        private bool AtIsLava(int x, int y, int z) => BlockIdAt(x, y, z) == _lavaId;

        private int LevelAt(int x, int y, int z)
        {
            return AtIsLava(x, y, z) ? MetaAt(x, y, z) : -1;
        }

        private bool BelowIsSolid(int x, int y, int z)
        {
            int id = BlockIdAt(x, y, z);
            return id != BlockRegistry.AirId && BlockRegistry.IsSolid(id);
        }

        private bool CanFallInto(int x, int y, int z)
        {
            if (AtIsLava(x, y, z))
            {
                return false;
            }
            int id = BlockIdAt(x, y, z);
            return id == BlockRegistry.AirId || !BlockRegistry.IsSolid(id);
        }

        private int _sourceCount;

        private void ClearSourceCount() => _sourceCount = 0;
        private int AdjacentSourceCount() => _sourceCount;

        private int WeakestNeighbourLevel(int x, int y, int z)
        {
            int best = -100;
            best = FoldNeighbour(best, LevelAt(x - 1, y, z));
            best = FoldNeighbour(best, LevelAt(x + 1, y, z));
            best = FoldNeighbour(best, LevelAt(x, y, z - 1));
            best = FoldNeighbour(best, LevelAt(x, y, z + 1));
            return best;
        }

        private int FoldNeighbour(int current, int level)
        {
            if (level < 0)
            {
                return current;
            }
            if (level == 0)
            {
                _sourceCount++;
            }
            if (level >= FallingFlag)
            {
                level = 0;
            }
            return current >= 0 && level >= current ? current : level;
        }

        private bool[] BestFlowDirections(int x, int y, int z)
        {
            for (int d = 0; d < 4; d++)
            {
                _cost[d] = InfiniteCost;
                (int nx, int nz) = Offset(d, x, z);

                if (IsHorizontalOpen(nx, y, nz))
                {
                    _cost[d] = BelowIsSolid(nx, y - 1, nz)
                        ? RouteCost(nx, y, nz, 1, d)
                        : 0; // direct drop -> cheapest
                }
            }

            int cheapest = _cost[0];
            for (int i = 1; i < 4; i++)
            {
                if (_cost[i] < cheapest) cheapest = _cost[i];
            }
            for (int i = 0; i < 4; i++)
            {
                _best[i] = _cost[i] == cheapest;
            }
            return _best;
        }

        private int RouteCost(int x, int y, int z, int depth, int cameFrom)
        {
            int best = InfiniteCost;
            for (int d = 0; d < 4; d++)
            {
                if (IsReverse(d, cameFrom)) continue; // don't backtrack

                (int nx, int nz) = Offset(d, x, z);
                if (!IsHorizontalOpen(nx, y, nz)) continue;

                if (!BelowIsSolid(nx, y - 1, nz))
                {
                    return depth; // found a drop
                }

                if (depth < FlowSearchDepth)
                {
                    int result = RouteCost(nx, y, nz, depth + 1, d);
                    if (result < best) best = result;
                }
            }
            return best;
        }

        private bool IsHorizontalOpen(int x, int y, int z)
        {
            if (BlockIdAt(x, y, z) == _lavaId)
            {
                return MetaAt(x, y, z) != 0; // sources aren't flow targets
            }
            int id = BlockIdAt(x, y, z);
            return id == BlockRegistry.AirId || !BlockRegistry.IsSolid(id);
        }

        private static bool IsReverse(int a, int from) =>
            (a == 0 && from == 1) || (a == 1 && from == 0) ||
            (a == 2 && from == 3) || (a == 3 && from == 2);

        private static (int, int) Offset(int dir, int x, int z) => dir switch
        {
            0 => (x - 1, z),
            1 => (x + 1, z),
            2 => (x, z - 1),
            _ => (x, z + 1),
        };
    }
}