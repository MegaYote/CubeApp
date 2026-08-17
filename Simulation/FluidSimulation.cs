using System;

namespace Cubuild
{
    /// <summary>
    /// Cellular liquid spreading for water blocks.
    ///
    /// Design (independent implementation, same observable behavior):
    ///  - Water exists as a single block id; metadata stores its "level" (0..7) plus an 8 flag
    ///    that marks a falling stream.
    ///  - A source (level 0) is generated when two or more neighbouring sources surround a cell.
    ///  - A cell's own strength is derived from the weakest (most exhausted) horizontal neighbour,
    ///    then decays by one per hop as it spreads outward.
    ///  - Gravity dominates: water always tries to fall into the cell below; only when the drop is
    ///    blocked does it spread sideways along the cheapest path (preferring any route that
    ///    eventually falls).
    ///
    /// Propagation is driven through the shared block-tick scheduler: every level change
    /// re-schedules the cell and pokes its six neighbours so they react and settle. A cell that
    /// reaches equilibrium simply stops re-scheduling and stays dormant until a neighbour changes.
    /// </summary>
    public sealed class FluidSimulation
    {
        public const int FluidTypeWater = 1;
        public const int WaterTickRate = 5;

        // Flags / limits used by the flow rules.
        private const int MaxLevel = 7;          // highest spread level before a cell dries up
        private const int FallingFlag = 8;       // added to a level to mark a falling stream
        private const int MultiSource = 2;       // neighbours needed to keep a source alive
        private const int FlowSearchDepth = 4;   // how far the sideways cost walk looks ahead
        private const int InfiniteCost = 1000;   // sentinel for "no path this way"

        private readonly ChunkManager _manager;
        private readonly BlockTickScheduler _tickScheduler;
        private readonly int _waterId;

        // Reusable scratch for the four horizontal directions (the sim runs on one thread).
        private readonly int[] _cost = new int[4];
        private readonly bool[] _best = new bool[4];

        public FluidSimulation(ChunkManager manager, BlockTickScheduler tickScheduler)
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
            _tickScheduler = tickScheduler ?? throw new ArgumentNullException(nameof(tickScheduler));
            _waterId = BlockRegistry.GetId("water");
        }

        /// <summary>Wakes nearby water whenever any block changes, so it re-evaluates. A cell that
        /// changed to water also schedules itself.</summary>
        public void OnBlockChanged(int x, int y, int z)
        {
            if (AtIsWater(x, y, z))
            {
                _tickScheduler.Schedule(x, y, z, WaterTickRate);
            }

            PokeNeighbours(x, y, z);
        }

        /// <summary>One scheduled update of a water cell.</summary>
        public void TickBlock(int x, int y, int z)
        {
            if (!AtIsWater(x, y, z))
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
                desired = weakest + FluidTypeWater;
                if (desired > MaxLevel || weakest < 0)
                {
                    desired = -1; // too weak / cut off -> the cell dries up
                }

                // Falling water directly above forces this cell to become a falling stream.
                int aboveLevel = LevelAt(x, y + 1, z);
                if (aboveLevel >= 0)
                {
                    desired = aboveLevel >= FallingFlag ? aboveLevel : aboveLevel + FallingFlag;
                }

                // Two adjacent sources sustain an infinite source here.
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
                WriteWater(x, y - 1, z, fallLevel);
                return;
            }

            // --- Sideways spread along the cheapest route ---
            if (currentLevel >= 0 && (currentLevel == 0 || BelowIsSolid(x, y - 1, z)))
            {
                int spreadLevel = currentLevel + FluidTypeWater;
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

        // Writes a newly computed level back, clearing the cell if it dried up.
        private void ApplyNewLevel(int x, int y, int z, int desired, ref int currentLevel)
        {
            if (desired == currentLevel)
            {
                return; // equilibrium: stay dormant until a neighbour changes
            }

            currentLevel = desired;
            if (desired < 0)
            {
                RemoveWaterCell(x, y, z);
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
                WriteWater(x, y, z, level);
            }
        }

        // ---------------------------------------------------------------------
        // World writes
        // ---------------------------------------------------------------------

        private void WriteWater(int x, int y, int z, int level)
        {
            // Water only ever spreads into already-loaded territory - never force gen.
            if (!_manager.TrySetBlockLoadedOnly(x, y, z, _waterId, level))
            {
                return;
            }

            _tickScheduler.Schedule(x, y, z, WaterTickRate);
            PokeNeighbours(x, y, z);
        }

        private void SetMetaOnly(int x, int y, int z, int level)
        {
            if (!_manager.TrySetBlock(x, y, z, _waterId, level))
            {
                return;
            }

            _tickScheduler.Schedule(x, y, z, WaterTickRate);
            PokeNeighbours(x, y, z);
        }

        private void RemoveWaterCell(int x, int y, int z)
        {
            if (!_manager.TrySetBlock(x, y, z, BlockRegistry.AirId, 0))
            {
                return;
            }

            PokeNeighbours(x, y, z);
        }

        private void PokeNeighbours(int x, int y, int z)
        {
            WakeIfWater(x + 1, y, z);
            WakeIfWater(x - 1, y, z);
            WakeIfWater(x, y + 1, z);
            WakeIfWater(x, y - 1, z);
            WakeIfWater(x, y, z + 1);
            WakeIfWater(x, y, z - 1);
        }

        private void WakeIfWater(int x, int y, int z)
        {
            if (AtIsWater(x, y, z))
            {
                _tickScheduler.Schedule(x, y, z, WaterTickRate);
            }
        }

        // ---------------------------------------------------------------------
        // Level / neighbour queries
        // ---------------------------------------------------------------------

        private int BlockIdAt(int x, int y, int z) => _manager.GetBlockAt(x, y, z);
        private int MetaAt(int x, int y, int z) => _manager.GetMetaAt(x, y, z);
        private bool AtIsWater(int x, int y, int z) => BlockIdAt(x, y, z) == _waterId;

        /// <summary>Returns the flow level (with the falling flag) of a water cell, or -1 for air/solid.</summary>
        private int LevelAt(int x, int y, int z)
        {
            return AtIsWater(x, y, z) ? MetaAt(x, y, z) : -1;
        }

        private bool BelowIsSolid(int x, int y, int z)
        {
            int id = BlockIdAt(x, y, z);
            return id != BlockRegistry.AirId && BlockRegistry.IsSolid(id);
        }

        private bool CanFallInto(int x, int y, int z)
        {
            // Water can't flow into another water cell, but flows into anything non-solid.
            if (AtIsWater(x, y, z))
            {
                return false;
            }
            int id = BlockIdAt(x, y, z);
            return id == BlockRegistry.AirId || !BlockRegistry.IsSolid(id);
        }

        // ---------------------------------------------------------------------
        // Horizontal flow costing
        // ---------------------------------------------------------------------

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
            // A falling stream counts as a level-0 flow source.
            if (level >= FallingFlag)
            {
                level = 0;
            }
            return current >= 0 && level >= current ? current : level;
        }

        // ---------------------------------------------------------------------
        // Cheapest sideways path (prefers routes that eventually fall)
        // ---------------------------------------------------------------------

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
            if (BlockIdAt(x, y, z) == _waterId)
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
