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
    /// Falling: a gravity block falls straight down through air AND water until it finds a solid
    /// support (matching Minecraft, where sand drops through water). It moves in ONE step (teleport
    /// to the landing spot) for deterministic cheap simulation. After landing, the cell it vacated
    /// triggers another OnBlockChanged, which cascades the check upward - so a whole column of
    /// dirt collapses together, one scheduling pass at a time.
    ///
    /// NOTE: this only ever reads/writes via ChunkManager.TrySetBlock / TryGetLoadedBlock, so it
    /// is safe to call from the main thread (the tick loop runs there, inside BlockTickScheduler).
    /// </summary>
    public sealed class GravitySimulation
    {
        private const int FallDelayTicks = 1;

        private readonly ChunkManager _manager;
        private readonly BlockTickScheduler _tickScheduler;
        private readonly int _waterId;

        public GravitySimulation(ChunkManager manager, BlockTickScheduler tickScheduler)
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
            _tickScheduler = tickScheduler ?? throw new ArgumentNullException(nameof(tickScheduler));
            _waterId = BlockRegistry.GetId("water");
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

        /// <summary>One scheduled tick for a gravity block: if still unsupported, drop it to the
        /// first supported cell below (falling through air and water).</summary>
        public void TickBlock(int x, int y, int z)
        {
            if (!_manager.TryGetLoadedBlock(x, y, z, out var id) || !BlockRegistry.IsGravity(id)) return;
            if (!IsUnsupported(x, y, z)) return;

            // Find the landing Y: the first cell (scanning down) whose below is solid support.
            int landingY = y - 1;
            while (landingY > ChunkManager.WorldOriginY)
            {
                if (_manager.TryGetLoadedBlock(x, landingY - 1, z, out var below) && IsSupport(below))
                {
                    break;
                }
                landingY--;
            }

            if (landingY == y) return; // can't move (already at floor)

            // Move the block. Use LoadedOnly so we never force-generate distant chunks just
            // because a column of dirt fell into unloaded territory.
            if (!_manager.TrySetBlockLoadedOnly(x, y, z, BlockRegistry.AirId, 0)) return;
            _manager.TrySetBlockLoadedOnly(x, landingY, z, id, 0);

            // The vacated cell (and the new landing cell) can wake neighbors: water displaced by
            // the fall, and any gravity block now hanging where this one used to be.
            _tickScheduler.OnBlockChanged(x, y, z);
            _tickScheduler.OnBlockChanged(x, landingY, z);
        }

        /// <summary>Solid enough to hold up a gravity block: any non-air solid block, including
        /// slabs/stairs (their partial geometry still supports).</summary>
        private static bool IsSupport(int id)
            => id != BlockRegistry.AirId && (BlockRegistry.IsSolid(id) || BlockRegistry.IsPartialShape(id));
    }
}
