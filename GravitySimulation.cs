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
    /// Triggering: BlockTickScheduler.OnBlockChanged calls us with every changed cell. We check
    /// that cell and the cell directly above it. If either is an unsupported gravity block we
    /// schedule it to tick (delay 1) so it falls on the next 20 TPS step.
    ///
    /// Falling: an unsupported gravity block is removed from the world grid and spawns a
    /// <see cref="FallingBlockData"/> entity that the renderer draws as a 3D cube. The entity
    /// accelerates downward under gravity each frame (Minecraft-style), falling through air AND
    /// water, until its bottom reaches a solid support. Then it lands back into the world grid
    /// (displacing water, like MC) and wakes neighbours so the cascade continues upward.
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

        // Active falling blocks (entity list; NOT in the world grid).
        private readonly List<FallingBlockData> _falling = new();
        private readonly List<int> _fallingSpeeds = new(); // parallel: current velocity per block

        private float _fallAccumulator;

        public GravitySimulation(ChunkManager manager, BlockTickScheduler tickScheduler)
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
            _tickScheduler = tickScheduler ?? throw new ArgumentNullException(nameof(tickScheduler));
            _waterId = BlockRegistry.GetId("water");
        }

        /// <summary>Read-only view of the blocks currently falling (for the renderer).</summary>
        public IReadOnlyList<FallingBlockData> FallingBlocks => _falling;

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

        /// <summary>One scheduled tick for a gravity block: if still unsupported, pop it out of
        /// the grid and start it falling as an entity.</summary>
        public void TickBlock(int x, int y, int z)
        {
            if (!_manager.TryGetLoadedBlock(x, y, z, out var id) || !BlockRegistry.IsGravity(id)) return;
            if (!IsUnsupported(x, y, z)) return;

            // Remove from the world and spawn the falling entity at the exact cell.
            if (!_manager.TrySetBlockLoadedOnly(x, y, z, BlockRegistry.AirId, 0)) return;
            lock (_falling)
            {
                _falling.Add(new FallingBlockData(id, x + 0f, y, z + 0f));
                _fallingSpeeds.Add(0);
            }
            // The cell we just vacated wakes the block ABOVE it (and water below), so a column
            // of gravity blocks cascades: each loses support one tick after the one below it.
            _tickScheduler.OnBlockChanged(x, y, z);
        }

        /// <summary>Advance all falling blocks by one game-loop frame (called from the main
        /// thread). Uses a fixed integration step so the fall speed is frame-rate independent.</summary>
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
            // Integrate velocities + positions, then resolve landings in a second pass so
            // multiple blocks in one column stack correctly (lower lands first).
            for (int i = 0; i < _falling.Count; i++)
            {
                int vel = _fallingSpeeds[i];
                vel = (int)(vel + FallGravity * StepRate);
                if (vel > MaxFallSpeed) vel = (int)MaxFallSpeed;
                _fallingSpeeds[i] = vel;
            }

            // Landing pass: collect which blocks have solid ground under their bottom now.
            var landed = new List<int>();
            for (int i = 0; i < _falling.Count; i++)
            {
                var f = _falling[i];
                float y = f.Y - _fallingSpeeds[i] * StepRate; // new bottom this step
                int cellY = (int)Math.Floor(y);
                // Land when the cell BELOW the cube is a solid support (air/water = keep falling).
                if (HasSupportBelow((int)Math.Floor(f.X), cellY - 1, (int)Math.Floor(f.Z)))
                {
                    // Round to the cell grid; if the target cell is occupied (e.g. by another
                    // falling block's future spot), keep going - the lower one lands first.
                    int finalY = cellY;
                    if (_manager.TryGetLoadedBlock((int)Math.Floor(f.X), finalY, (int)Math.Floor(f.Z), out var occ) && IsSupport(occ))
                    {
                        finalY = cellY + 1; // push up onto it
                    }
                    landed.Add(i);
                    _manager.TrySetBlockLoadedOnly((int)Math.Floor(f.X), finalY, (int)Math.Floor(f.Z), f.BlockId, 0);
                    _tickScheduler.OnBlockChanged((int)Math.Floor(f.X), finalY, (int)Math.Floor(f.Z));
                }
            }

            // Apply positions (only for blocks that didn't land) and remove landed in reverse.
            for (int i = _falling.Count - 1; i >= 0; i--)
            {
                var f = _falling[i];
                bool didLand = landed.Contains(i);
                if (didLand)
                {
                    _falling.RemoveAt(i);
                    _fallingSpeeds.RemoveAt(i);
                    continue;
                }
                int vel = _fallingSpeeds[i];
                _falling[i] = new FallingBlockData(f.BlockId, f.X, f.Y - vel * StepRate, f.Z);
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
