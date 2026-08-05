using System;

namespace CubeApp
{
    /// <summary>
    /// Minecraft Infdev (20100630) water simulation, ported 1:1 from BlockFlowing.java with a
    /// single water block id instead of MC's flowing/still pair. Metadata encodes the flow level:
    /// 0 = source/still, 1..7 = flowing, &gt;=8 = falling stream.
    ///
    /// MC's two block ids exist solely to give water a "still" state so stable water stops
    /// re-ticking. With one id the same behaviour falls out for free: a block whose tick doesn't
    /// change its metadata simply isn't re-scheduled, so it stays dormant until a neighbour change
    /// wakes it (the analogue of BlockStationary.onNeighborBlockChange).
    ///
    /// The algorithm: every tick a block (a) decides its own new level from the shallowest
    /// neighbour, falling-water above it, and the 2+ adjacent sources rule; (b) falls down if it
    /// can (marking itself falling, meta+8); otherwise (c) flows to the cheapest horizontal
    /// neighbours via a 4-deep recursion that prefers paths leading to a drop.
    /// </summary>
    public sealed class FluidSimulation
    {
        public const int FluidTypeWater = 1;
        public const int WaterTickRate = 5;

        private readonly ChunkManager _manager;
        private readonly BlockTickScheduler _tickScheduler;
        private readonly int _waterId;
        private int _numAdjacentSources;
        // Scratch buffers for flow costing (single-threaded: only the main loop drives the sim).
        private readonly int[] _flowCost = new int[4];
        private readonly bool[] _optimal = new bool[4];

        public FluidSimulation(ChunkManager manager, BlockTickScheduler tickScheduler)
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
            _tickScheduler = tickScheduler ?? throw new ArgumentNullException(nameof(tickScheduler));
            _waterId = BlockRegistry.GetId("water");
        }

        /// <summary>Called when any block in the world changes so nearby water wakes up and reacts
        /// (the analogue of World.notifyBlocksOfNeighborChange). A block that changed TO water also
        /// wakes itself, matching BlockFlowing.onBlockAdded scheduling the placed block.</summary>
        public void OnBlockChanged(int x, int y, int z)
        {
            if (GetBlockId(x, y, z) == _waterId)
            {
                _tickScheduler.Schedule(x, y, z, WaterTickRate);
            }

            NotifyNeighbors(x, y, z);
        }

        /// <summary>One scheduled update for a water block (BlockFlowing.updateTick).</summary>
        public void TickBlock(int x, int y, int z)
        {
            if (GetBlockId(x, y, z) != _waterId)
            {
                return;
            }

            int var6 = GetFlowDecay(x, y, z);
            bool var7 = true;
            int var9;

            if (var6 > 0)
            {
                // Flowing/falling block: recompute its level from its neighbours.
                _numAdjacentSources = 0;
                int var11 = GetSmallestFlowDecay(x - 1, y, z, -100);
                var11 = GetSmallestFlowDecay(x + 1, y, z, var11);
                var11 = GetSmallestFlowDecay(x, y, z - 1, var11);
                var11 = GetSmallestFlowDecay(x, y, z + 1, var11);
                var9 = var11 + FluidTypeWater;
                if (var9 >= 8 || var11 < 0)
                {
                    var9 = -1;
                }

                // Water above turns this into a falling stream.
                int aboveDecay = GetFlowDecay(x, y + 1, z);
                if (aboveDecay >= 0)
                {
                    var9 = aboveDecay >= 8 ? aboveDecay : aboveDecay + 8;
                }

                // Two adjacent sources + water = infinite source.
                if (_numAdjacentSources >= 2)
                {
                    var9 = 0;
                }

                // (Lava's 1-in-4 "keep level" roll and its flowCost=2 are deliberately omitted:
                // lava comes later by copying this file with FluidTypeLava.)

                if (var9 != var6)
                {
                    var6 = var9;
                    if (var9 < 0)
                    {
                        SetBlockWithNotify(x, y, z, BlockRegistry.AirId, 0);
                    }
                    else
                    {
                        SetMetaWithNotify(x, y, z, var9);
                    }
                }
                // else: equilibrium -> single-id "still": just don't re-schedule. The block stays
                // dormant until a neighbour change (NotifyNeighbors) wakes it.
            }
            // else var6 == 0 (source): MC converts to a still block; we simply don't re-schedule.
            // Sources still spread through the section below (var6 stays 0, so flow level = 1).

            if (LiquidCanDisplaceBlock(x, y - 1, z))
            {
                // Flow down: falling water keeps its level, otherwise mark the new cell falling.
                if (var6 >= 8)
                {
                    SetBlockWithNotify(x, y - 1, z, _waterId, var6);
                }
                else
                {
                    SetBlockWithNotify(x, y - 1, z, _waterId, var6 + 8);
                }
            }
            else if (var6 >= 0 && (var6 == 0 || BlockBlocksFlow(x, y - 1, z)))
            {
                // Horizontal spread along the cheapest path.
                var optimal = GetOptimalFlowDirections(x, y, z);
                var9 = var6 + FluidTypeWater;
                if (var6 >= 8)
                {
                    var9 = 1;
                }

                if (var9 >= 8)
                {
                    return;
                }

                if (optimal[0]) FlowIntoBlock(x - 1, y, z, var9);
                if (optimal[1]) FlowIntoBlock(x + 1, y, z, var9);
                if (optimal[2]) FlowIntoBlock(x, y, z - 1, var9);
                if (optimal[3]) FlowIntoBlock(x, y, z + 1, var9);
            }
        }

        private void FlowIntoBlock(int x, int y, int z, int level)
        {
            if (LiquidCanDisplaceBlock(x, y, z))
            {
                // (No item drops or lava-mix effects in this engine yet.)
                SetBlockWithNotify(x, y, z, _waterId, level);
            }
        }

        private void SetBlockWithNotify(int x, int y, int z, int id, int meta)
        {
            if (id == _waterId)
            {
                // A water write is usually a spread target: never force terrain generation.
                if (!_manager.TrySetBlockLoadedOnly(x, y, z, id, meta))
                {
                    return;
                }
            }
            else
            {
                if (!_manager.TrySetBlock(x, y, z, id, meta))
                {
                    return;
                }
            }

            if (id == _waterId)
            {
                _tickScheduler.Schedule(x, y, z, WaterTickRate);
            }

            NotifyNeighbors(x, y, z);
        }

        private void SetMetaWithNotify(int x, int y, int z, int meta)
        {
            if (!_manager.TrySetBlock(x, y, z, _waterId, meta))
            {
                return;
            }

            _tickScheduler.Schedule(x, y, z, WaterTickRate);
            NotifyNeighbors(x, y, z);
        }

        private void NotifyNeighbors(int x, int y, int z)
        {
            ScheduleWaterNeighbor(x + 1, y, z);
            ScheduleWaterNeighbor(x - 1, y, z);
            ScheduleWaterNeighbor(x, y + 1, z);
            ScheduleWaterNeighbor(x, y - 1, z);
            ScheduleWaterNeighbor(x, y, z + 1);
            ScheduleWaterNeighbor(x, y, z - 1);
        }

        private void ScheduleWaterNeighbor(int x, int y, int z)
        {
            if (GetBlockId(x, y, z) == _waterId)
            {
                _tickScheduler.Schedule(x, y, z, WaterTickRate);
            }
        }

        // ---- World queries ----------------------------------------------------------

        private int GetBlockId(int x, int y, int z) => _manager.GetBlockAt(x, y, z);
        private int GetMeta(int x, int y, int z) => _manager.GetMetaAt(x, y, z);

        private int GetFlowDecay(int x, int y, int z)
        {
            return GetBlockId(x, y, z) == _waterId ? GetMeta(x, y, z) : -1;
        }

        private int GetSmallestFlowDecay(int x, int y, int z, int currentBest)
        {
            int decay = GetFlowDecay(x, y, z);
            if (decay < 0)
            {
                return currentBest;
            }

            if (decay == 0)
            {
                _numAdjacentSources++;
            }

            if (decay >= 8)
            {
                decay = 0; // falling water counts as level 0 for flow purposes
            }

            return currentBest >= 0 && decay >= currentBest ? currentBest : decay;
        }

        private bool BlockBlocksFlow(int x, int y, int z)
        {
            int id = GetBlockId(x, y, z);
            if (id == BlockRegistry.AirId)
            {
                return false;
            }

            return BlockRegistry.IsSolid(id);
        }

        private bool LiquidCanDisplaceBlock(int x, int y, int z)
        {
            if (GetBlockId(x, y, z) == _waterId)
            {
                return false;
            }

            // (MC also refuses to displace lava - no lava in this engine yet.)
            return !BlockBlocksFlow(x, y, z);
        }

        // ---- Flow direction costing (BlockFlowing.getOptimalFlowDirections/calculateFlowCost) ----

        private bool[] GetOptimalFlowDirections(int x, int y, int z)
        {
            for (int dir = 0; dir < 4; dir++)
            {
                _flowCost[dir] = 1000;
                int nx = x, nz = z;
                if (dir == 0) nx = x - 1;
                else if (dir == 1) nx = x + 1;
                else if (dir == 2) nz = z - 1;
                else nz = z + 1;

                // A water source (meta 0) is not a flow target.
                if (!BlockBlocksFlow(nx, y, nz) && (GetBlockId(nx, y, nz) != _waterId || GetMeta(nx, y, nz) != 0))
                {
                    if (!BlockBlocksFlow(nx, y - 1, nz))
                    {
                        _flowCost[dir] = 0; // direct drop: cheapest path
                    }
                    else
                    {
                        _flowCost[dir] = CalculateFlowCost(nx, y, nz, 1, dir);
                    }
                }
            }

            int min = _flowCost[0];
            for (int i = 1; i < 4; i++)
            {
                if (_flowCost[i] < min)
                {
                    min = _flowCost[i];
                }
            }

            for (int i = 0; i < 4; i++)
            {
                _optimal[i] = _flowCost[i] == min;
            }

            return _optimal;
        }

        private int CalculateFlowCost(int x, int y, int z, int cost, int fromDir)
        {
            int best = 1000;
            for (int dir = 0; dir < 4; dir++)
            {
                // Don't flow straight back the way we came.
                if ((dir == 0 && fromDir == 1) || (dir == 1 && fromDir == 0) ||
                    (dir == 2 && fromDir == 3) || (dir == 3 && fromDir == 2))
                {
                    continue;
                }

                int nx = x, nz = z;
                if (dir == 0) nx = x - 1;
                else if (dir == 1) nx = x + 1;
                else if (dir == 2) nz = z - 1;
                else nz = z + 1;

                if (!BlockBlocksFlow(nx, y, nz) && (GetBlockId(nx, y, nz) != _waterId || GetMeta(nx, y, nz) != 0))
                {
                    if (!BlockBlocksFlow(nx, y - 1, nz))
                    {
                        return cost; // a path that falls is the cheapest
                    }

                    if (cost < 4)
                    {
                        int result = CalculateFlowCost(nx, y, nz, cost + 1, dir);
                        if (result < best)
                        {
                            best = result;
                        }
                    }
                }
            }

            return best;
        }
    }
}
