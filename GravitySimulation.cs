using System;
using System.Collections.Generic;

namespace CubeApp
{
    /// <summary>
    /// Gravity for sand/gravel/dirt/red_clay (anything flagged "gravity" in blocks.json).
    ///
    /// The rule is deliberately lazy, per the design decision: blocks only fall when UPDATED.
    /// The world may generate floating overhangs and they stay perfectly still until the player
    /// (or water, or a cave-in) changes a block near them - then gravity remembers them and they
    /// drop. This makes cave-ins "waiting to happen" without making world-gen collapse on itself.
    ///
    /// PERFORMANCE (hundreds falling at once): when a block loses support the ENTIRE contiguous
    /// gravity column above it is popped into falling entities in a SINGLE tick (not one block
    /// per tick), so an avalanche starts moving immediately. The entity list uses swap-remove
    /// (O(1) removal, no List.RemoveAt shifts) and the landing pass marks landed blocks in place
    /// (no per-step allocations, no Contains() scans) - a 500-block collapse costs O(n) per step.
    ///
    /// Triggering: BlockTickScheduler.OnBlockChanged calls us with every changed cell. We check
    /// that cell and the cell directly above it; an unsupported gravity block is scheduled to
    /// tick, and TickBlock pops the whole column.
    ///
    /// Falling: blocks pop out of the world grid and spawn FallingBlockData entities rendered as
    /// 3D cubes. They accelerate downward (Minecraft-style), falling through air AND water, until
    /// the bottom reaches a solid support, then land back into the grid (displacing water).
    ///
    /// NOTE: only ever touches the world on the main thread (the tick loop and UpdateFalling
    /// both run inside the game loop), so it is safe alongside the networking main-thread rule.
    /// </summary>
    public sealed class GravitySimulation
    {
        private const int FallDelayTicks = 1;
        private const float FallGravity = 24.0f;   // matches player gravity; snappy cave-ins
        private const float MaxFallSpeed = 32.0f;
        private const float StepRate = 1f / 20f;   // gravity integration step

        private readonly ChunkManager _manager;
        private readonly BlockTickScheduler _tickScheduler;
        private readonly int _waterId;
        private readonly int _worldTopY; // exclusive: one past the highest block row

        // Active falling blocks (entity list; NOT in the world grid). Parallel speeds.
        private readonly List<FallingBlockData> _falling = new();
        private readonly List<float> _fallSpeeds = new();

        private float _fallAccumulator;

        public GravitySimulation(ChunkManager manager, BlockTickScheduler tickScheduler)
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
            _tickScheduler = tickScheduler ?? throw new ArgumentNullException(nameof(tickScheduler));
            _waterId = BlockRegistry.GetId("water");
            _worldTopY = ChunkManager.WorldOriginY + ChunkManager.ChunkHeight;
        }

        /// <summary>Read-only view of the blocks currently falling (for the renderer).</summary>
        public IReadOnlyList<FallingBlockData> FallingBlocks => _falling;

        /// <summary>True when a falling block currently occupies the given world cell. Placement
        /// refuses cells a falling block is passing through (Minecraft's "wait for it to fall out
        /// of the way" behaviour - you can't stack a new block into a moving one).</summary>
        public bool IsCellOccupiedByFalling(int x, int y, int z)
        {
            // n is small (a handful during normal play); a big cave-in rarely sees simultaneous
            // placements, so a linear scan is fine and avoids a per-frame occupancy structure.
            for (int i = 0; i < _falling.Count; i++)
            {
                var f = _falling[i];
                if ((int)Math.Floor(f.X) == x && (int)Math.Floor(f.Y) == y && (int)Math.Floor(f.Z) == z)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Called when any block in the world changes. If the changed cell (or the cell
        /// above it) is now an unsupported gravity block, schedule it to fall.</summary>
        public void OnBlockChanged(int x, int y, int z)
        {
            CheckCell(x, y, z);
            CheckCell(x, y + 1, z);
        }

        private void CheckCell(int x, int y, int z)
        {
            if (_manager.TryGetLoadedBlock(x, y, z, out var id) && BlockRegistry.IsGravity(id) && IsUnsupported(x, y, z))
            {
                _tickScheduler.Schedule(x, y, z, FallDelayTicks);
            }
        }

        /// <summary>True when the block at (x,y,z) has no solid support directly below (air or
        /// water underneath = unsupported).</summary>
        private bool IsUnsupported(int x, int y, int z)
        {
            if (!_manager.TryGetLoadedBlock(x, y - 1, z, out var belowId)) return true;
            return belowId == BlockRegistry.AirId || belowId == _waterId;
        }

        /// <summary>One scheduled tick for a gravity block: if still unsupported, pop the ENTIRE
        /// contiguous gravity column above it into falling entities in a single operation.</summary>
        public void TickBlock(int x, int y, int z)
        {
            if (!_manager.TryGetLoadedBlock(x, y, z, out var id) || !BlockRegistry.IsGravity(id)) return;
            if (!IsUnsupported(x, y, z)) return;

            // Walk up the column while the block above is also an (now-unsupported) gravity
            // block, popping each into a falling entity. O(column height) total - a 100-block
            // tower starts falling in ONE tick, not 100.
            int top = y;
            while (top < _worldTopY)
            {
                if (!_manager.TryGetLoadedBlock(x, top, z, out var upId) || !BlockRegistry.IsGravity(upId)) break;
                if (!_manager.TrySetBlockLoadedOnly(x, top, z, BlockRegistry.AirId, 0)) break;
                lock (_falling)
                {
                    _falling.Add(new FallingBlockData(upId, x + 0f, top, z + 0f));
                    _fallSpeeds.Add(0f);
                }
                top++;
            }

            // The vacated column wakes what's above it (a gravity block resting on this column's
            // top) and the water below (displaced air lets it flow in).
            _tickScheduler.OnBlockChanged(x, top, z);
            _tickScheduler.OnBlockChanged(x, y - 1, z);
        }

        /// <summary>Advance all falling blocks by one game-loop frame (main thread). Uses a fixed
        /// integration step so fall speed is frame-rate independent.</summary>
        public void UpdateFalling(float deltaSeconds)
        {
            if (_falling.Count == 0) return;

            _fallAccumulator += Math.Min(deltaSeconds, 0.1f);
            while (_fallAccumulator >= StepRate && _falling.Count > 0)
            {
                StepFalling();
                _fallAccumulator -= StepRate;
            }
            if (_fallAccumulator > StepRate * 2) _fallAccumulator = 0;
        }

        private void StepFalling()
        {
            // Integrate velocities (O(n)).
            for (int i = 0; i < _falling.Count; i++)
            {
                float vel = _fallSpeeds[i] + FallGravity * StepRate;
                if (vel > MaxFallSpeed) vel = MaxFallSpeed;
                _fallSpeeds[i] = vel;
            }

            // Landing + position pass (O(n), in place, no allocations). Iterate forward; when a
            // block lands, swap-remove it into the current slot (O(1)) and re-check the slot.
            for (int i = 0; i < _falling.Count;)
            {
                var f = _falling[i];
                float newY = f.Y - _fallSpeeds[i] * StepRate;
                int floorX = (int)Math.Floor(f.X);
                int floorZ = (int)Math.Floor(f.Z);
                int cellY = (int)Math.Floor(newY);

                if (HasSupportBelow(floorX, cellY - 1, floorZ))
                {
                    // Landing cell; if it's already occupied (another block settled there this
                    // pass), push up onto it so columns restack.
                    int finalY = cellY;
                    if (_manager.TryGetLoadedBlock(floorX, finalY, floorZ, out var occ) && IsSupport(occ))
                    {
                        finalY = cellY + 1;
                    }
                    _manager.TrySetBlockLoadedOnly(floorX, finalY, floorZ, f.BlockId, 0);
                    _tickScheduler.OnBlockChanged(floorX, finalY, floorZ);

                    // Swap-remove: overwrite this slot with the last element, drop the tail.
                    int last = _falling.Count - 1;
                    _falling[i] = _falling[last];
                    _fallSpeeds[i] = _fallSpeeds[last];
                    _falling.RemoveAt(last);
                    _fallSpeeds.RemoveAt(last);
                    // Do NOT increment i: the swapped-in element must be re-examined.
                }
                else
                {
                    _falling[i] = new FallingBlockData(f.BlockId, f.X, newY, f.Z);
                    i++;
                }
            }
        }

        private bool HasSupportBelow(int x, int y, int z)
        {
            if (!_manager.TryGetLoadedBlock(x, y, z, out var below)) return false;
            return IsSupport(below);
        }

        /// <summary>Solid enough to hold up a gravity block: any non-air solid block, including
        /// slabs/stairs (their partial geometry still supports).</summary>
        private static bool IsSupport(int id)
            => id != BlockRegistry.AirId && (BlockRegistry.IsSolid(id) || BlockRegistry.IsPartialShape(id));
    }
}
