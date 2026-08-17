using System;

namespace Cubuild
{
    /// <summary>
    /// Grass spread (MC BlockGrass.updateTick port, adapted to our two-grass design).
    ///
    /// Two grass blocks:
    ///   grass            - the full grass that generates in the world (all-grass faces).
    ///   grass_spreading  - the transitional "half-grass": grass top, dirt sides + bottom.
    ///
    /// Spread order (per user design):
    ///   1. A FULL grass block adjacent to plain dirt converts that dirt to grass_spreading.
    ///   2. grass_spreading that is lit (sky above) and adjacent to a full grass block grows
    ///      into full grass.
    ///
    /// Both revert to dirt if the block above them is opaque (blocks sky light).
    ///
    /// This runs on the BlockTickScheduler's 20 TPS tick: any grass block scheduled by a
    /// neighbor change ticks, picks random nearby cells like MC, and converts them.
    /// </summary>
    public sealed class GrassSpreadSimulation
    {
        private const int TickDelay = 1024;

        private readonly ChunkManager _manager;
        private readonly BlockTickScheduler _tickScheduler;
        private readonly int _grassId;
        private readonly int _spreadingId;
        private readonly int _dirtId;

        public GrassSpreadSimulation(ChunkManager manager, BlockTickScheduler tickScheduler)
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
            _tickScheduler = tickScheduler ?? throw new ArgumentNullException(nameof(tickScheduler));
            _grassId = BlockRegistry.GetId("grass");
            _spreadingId = BlockRegistry.GetId("grass_spreading");
            _dirtId = BlockRegistry.GetId("dirt");
        }

        /// <summary>Called when any block changes: wake nearby grass so it spreads/reverts.</summary>
        public void OnBlockChanged(int x, int y, int z)
        {
            // Wake grass in the 3x3x3 neighbourhood (MC uses random tick updates, but waking on
            // change is cheaper and makes spread feel immediate when you place dirt next to grass).
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                CheckAndSchedule(x + dx, y + dy, z + dz);
            }
        }

        private void CheckAndSchedule(int x, int y, int z)
        {
            if (_manager.TryGetLoadedBlock(x, y, z, out var id)
                && (id == _grassId || id == _spreadingId))
            {
                _tickScheduler.Schedule(x, y, z, TickDelay);
            }
        }

        /// <summary>One scheduled tick for a grass block (MC BlockGrass.updateTick).</summary>
        public void TickBlock(int x, int y, int z)
        {
            if (!_manager.TryGetLoadedBlock(x, y, z, out var id)) return;
            if (id != _grassId && id != _spreadingId) return;

            // Grass keeps ticking (MC random-tick style) so it keeps trying to spread even if a
            // given tick's random picks miss the dirt. Without this, a grass block woken once by
            // a neighbor change would try once and go dormant forever if it rolled badly.
            _tickScheduler.Schedule(x, y, z, TickDelay);

            // If the block above is opaque (or too dark), grass dies back to dirt.
            if (!IsSkyLit(x, y, z))
            {
                if (id == _grassId || id == _spreadingId)
                {
                    _manager.TrySetBlockLoadedOnly(x, y, z, _dirtId, 0);
                }
                return;
            }

            // Spread: pick a few random cells in a small box, like MC's 4 attempts.
            var rand = Random.Shared;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                int nx = x + rand.Next(-1, 2);
                int ny = y + rand.Next(-1, 3) - 1;
                int nz = z + rand.Next(-1, 2);
                if (!_manager.TryGetLoadedBlock(nx, ny, nz, out var nid)) continue;

                if (nid == _dirtId && IsSkyLit(nx, ny, nz))
                {
                    // Dirt next to FULL grass becomes the transitional grass_spreading.
                    if (id == _grassId)
                    {
                        if (_manager.TrySetBlockLoadedOnly(nx, ny, nz, _spreadingId, 0))
                        {
                            // Keep propagating: schedule the new spreading block too.
                            _tickScheduler.Schedule(nx, ny, nz, TickDelay);
                        }
                    }
                    else if (id == _spreadingId)
                    {
                        // Spreading grass grows to full grass when it has dirt spread available
                        // (it's already lit by the top check) - this is the "eventually grows"
                        // transition. Only if a full grass neighbour exists.
                        if (HasFullGrassNeighbour(x, y, z))
                        {
                            _manager.TrySetBlockLoadedOnly(nx, ny, nz, _spreadingId, 0);
                        }
                    }
                }
            }

            // grass_spreading with a full-grass neighbour grows into full grass.
            if (id == _spreadingId && HasFullGrassNeighbour(x, y, z))
            {
                _manager.TrySetBlockLoadedOnly(x, y, z, _grassId, 0);
            }
        }

        // True when the cell above is air or transparent enough that sky light reaches it.
        private bool IsSkyLit(int x, int y, int z)
        {
            return _manager.TryGetLoadedBlock(x, y + 1, z, out var above)
                && !BlockRegistry.IsOpaque(above);
        }

        // True when any of the 4 horizontal neighbours is full grass (not just spreading).
        private bool HasFullGrassNeighbour(int x, int y, int z)
        {
            return (_manager.TryGetLoadedBlock(x + 1, y, z, out var e) && e == _grassId)
                || (_manager.TryGetLoadedBlock(x - 1, y, z, out var w) && w == _grassId)
                || (_manager.TryGetLoadedBlock(x, y, z + 1, out var s) && s == _grassId)
                || (_manager.TryGetLoadedBlock(x, y, z - 1, out var n) && n == _grassId);
        }
    }
}
