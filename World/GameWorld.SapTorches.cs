using System;
using System.Collections.Generic;

namespace Cubuild
{
    public sealed partial class GameWorld
    {
        public const long SapTorchGracePeriodTicks = 24000;
        private const int SapTorchMinAdditionalLifetimeTicks = 12000;
        private const int SapTorchMaxAdditionalLifetimeTicks = 48000;

        // A zero BurnAtTick means the grace period has not elapsed yet. The additional lifetime
        // is rolled only on the first simulation step at/after GraceEndTick.
        private readonly Dictionary<(int X, int Y, int Z), (long GraceEndTick, long BurnAtTick)> _sapTorchBurnTimers = new();
        private readonly Random _sapTorchRandom = new();

        private void StartSapTorchBurnTimer(int x, int y, int z)
        {
            _sapTorchBurnTimers[(x, y, z)] = (WorldTime + SapTorchGracePeriodTicks, 0);
        }

        /// <summary>Updates/removes the timer whenever an edit changes a block at this cell.</summary>
        private void OnSapTorchBlockChanged(int x, int y, int z, int blockId, int previousBlockId = -1)
        {
            var position = (x, y, z);
            if (blockId == _idSapTorch)
            {
                // Preserve an existing countdown for metadata/orientation-only updates. A newly
                // placed or relit torch always receives a fresh grace period.
                if (previousBlockId != _idSapTorch || !_sapTorchBurnTimers.ContainsKey(position))
                    StartSapTorchBurnTimer(x, y, z);
            }
            else
            {
                _sapTorchBurnTimers.Remove(position);
            }
        }

        private void StepSapTorchBurnTimers()
        {
            if (_sapTorchBurnTimers.Count == 0) return;

            var positions = new List<(int X, int Y, int Z)>(_sapTorchBurnTimers.Keys);
            foreach (var position in positions)
            {
                if (!_sapTorchBurnTimers.TryGetValue(position, out var timer)) continue;

                // Start the randomized lifetime at the absolute grace-end tick even if the
                // torch's chunk is streamed out. Defer only the block conversion; when the chunk
                // returns an already-expired torch burns immediately.
                if (timer.BurnAtTick == 0 && WorldTime >= timer.GraceEndTick)
                {
                    int additionalLifetime = _sapTorchRandom.Next(
                        SapTorchMinAdditionalLifetimeTicks,
                        SapTorchMaxAdditionalLifetimeTicks + 1);
                    timer.BurnAtTick = WorldTime + additionalLifetime;
                    _sapTorchBurnTimers[position] = timer;
                }

                if (!Chunks.TryGetLoadedBlockAndMeta(position.X, position.Y, position.Z, out int blockId, out byte meta))
                    continue;

                if (blockId != _idSapTorch)
                {
                    _sapTorchBurnTimers.Remove(position);
                    continue;
                }

                if (timer.BurnAtTick == 0 || WorldTime < timer.BurnAtTick) continue;
                if (!Chunks.TrySetBlockLoadedOnly(position.X, position.Y, position.Z, _idBurntTorch, meta))
                    continue;

                BlockTicks?.OnBlockChanged(position.X, position.Y, position.Z);
                Mesher.RequestImmediateRemesh(new ChunkCoordinates(
                    ChunkManager.LayerForWorldY(position.Y),
                    WorldToChunkCoord(position.X),
                    WorldToChunkCoord(position.Z)));
                BlockEdited?.Invoke(position.X, position.Y, position.Z, _idBurntTorch, meta);
                _sapTorchBurnTimers.Remove(position);
            }
        }

        /// <summary>Restores the v11 absolute schedules captured by a save.</summary>
        public void RestoreSapTorchBurnTimers(IEnumerable<SavedSapTorchTimer> timers)
        {
            _sapTorchBurnTimers.Clear();
            foreach (var timer in timers)
            {
                _sapTorchBurnTimers[(timer.X, timer.Y, timer.Z)] =
                    (timer.GraceEndTick, timer.BurnAtTick);
            }
        }

        /// <summary>Copies timer state on the simulation thread for a save snapshot.</summary>
        public List<SavedSapTorchTimer> CaptureSapTorchBurnTimers()
        {
            var result = new List<SavedSapTorchTimer>(_sapTorchBurnTimers.Count);
            foreach (var entry in _sapTorchBurnTimers)
            {
                result.Add(new SavedSapTorchTimer
                {
                    X = entry.Key.X,
                    Y = entry.Key.Y,
                    Z = entry.Key.Z,
                    GraceEndTick = entry.Value.GraceEndTick,
                    BurnAtTick = entry.Value.BurnAtTick,
                });
            }
            return result;
        }

        /// <summary>
        /// v1-v10 saves had no lifecycle state. Seed every sap torch in the restored loaded chunks
        /// with a new full grace period before normal streaming begins.
        /// </summary>
        public void InitializeSapTorchBurnTimersForLoadedTorches()
        {
            _sapTorchBurnTimers.Clear();
            foreach (var chunk in Chunks.GetLoadedChunks())
            {
                byte[] blocks = chunk.RawBlocks;
                int columnSize = chunk.Depth * chunk.Height;
                int length = blocks.Length;
                for (int index = 0; index < length; index++)
                {
                    if (blocks[index] != (byte)_idSapTorch) continue;

                    int localX = index / columnSize;
                    int withinColumn = index % columnSize;
                    int localZ = withinColumn / chunk.Height;
                    int localY = withinColumn % chunk.Height;
                    StartSapTorchBurnTimer(
                        chunk.OriginX + localX,
                        chunk.OriginY + localY,
                        chunk.OriginZ + localZ);
                }
            }
        }
    }
}
