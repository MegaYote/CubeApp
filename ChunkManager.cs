using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using CubeApp.World;

namespace CubeApp
{
    public sealed class ChunkManager
    {
        public const int ChunkSize = 16;
        public const int ChunkHeight = 64;
        private readonly ConcurrentDictionary<ChunkCoordinates, Chunk> loadedChunks = new();
        private readonly PriorityQueue<ChunkRequest, double> queue = new();
        private readonly object queueLock = new();
        private readonly ConcurrentDictionary<ChunkCoordinates, byte> pendingGeneration = new();
        private readonly IChunkProvider chunkProvider;

        public ChunkManager(IChunkProvider? chunkProvider = null)
        {
            this.chunkProvider = chunkProvider ?? new InfdevChunkProvider();
        }

        public Chunk GetOrCreateChunk(int chunkX, int chunkZ)
        {
            var key = new ChunkCoordinates(chunkX, chunkZ);
            bool created = false;
            var result = loadedChunks.GetOrAdd(key, _ =>
            {
                var chunk = chunkProvider.GenerateChunk(chunkX, chunkZ, ChunkSize, ChunkHeight);
                created = true;
                return chunk;
            });

            if (created)
            {
                // A newly loaded chunk can expose faces on adjacent, already-meshed chunks
                // (border faces are culled against neighbors, and an absent neighbor is treated
                // as air). Mark those neighbors dirty so their border faces get rebuilt now,
                // instead of staying wrong until some unrelated edit/unload triggers a remesh.
                if (loadedChunks.TryGetValue(new ChunkCoordinates(chunkX - 1, chunkZ), out var left))
                    left.MarkDirty();
                if (loadedChunks.TryGetValue(new ChunkCoordinates(chunkX + 1, chunkZ), out var right))
                    right.MarkDirty();
                if (loadedChunks.TryGetValue(new ChunkCoordinates(chunkX, chunkZ - 1), out var back))
                    back.MarkDirty();
                if (loadedChunks.TryGetValue(new ChunkCoordinates(chunkX, chunkZ + 1), out var front))
                    front.MarkDirty();
            }

            return result;
        }

        public bool TrySetBlock(int worldX, int worldY, int worldZ, BlockType blockType)
        {
            int chunkX = FloorDiv(worldX, ChunkSize);
            int chunkZ = FloorDiv(worldZ, ChunkSize);
            var chunk = GetOrCreateChunk(chunkX, chunkZ);
            int localX = worldX - chunk.OriginX;
            int localZ = worldZ - chunk.OriginZ;
            if (!chunk.IsInBounds(localX, worldY, localZ))
            {
                return false;
            }

            chunk[localX, worldY, localZ] = blockType;
            // mark this chunk dirty so it will be remeshed
            chunk.MarkDirty();

            // if modification touches chunk boundaries, mark neighbor chunks dirty as well
            if (localX == 0 && loadedChunks.TryGetValue(new ChunkCoordinates(chunkX - 1, chunkZ), out var left))
                left.MarkDirty();
            if (localX == ChunkSize - 1 && loadedChunks.TryGetValue(new ChunkCoordinates(chunkX + 1, chunkZ), out var right))
                right.MarkDirty();
            if (localZ == 0 && loadedChunks.TryGetValue(new ChunkCoordinates(chunkX, chunkZ - 1), out var back))
                back.MarkDirty();
            if (localZ == ChunkSize - 1 && loadedChunks.TryGetValue(new ChunkCoordinates(chunkX, chunkZ + 1), out var front))
                front.MarkDirty();

            return true;
        }

        public bool TryGetLoadedBlock(int worldX, int worldY, int worldZ, out BlockType blockType)
        {
            int chunkX = FloorDiv(worldX, ChunkSize);
            int chunkZ = FloorDiv(worldZ, ChunkSize);
            if (!loadedChunks.TryGetValue(new ChunkCoordinates(chunkX, chunkZ), out var chunk))
            {
                blockType = BlockType.Air;
                return false;
            }

            int localX = worldX - chunk.OriginX;
            int localZ = worldZ - chunk.OriginZ;
            if (!chunk.IsInBounds(localX, worldY, localZ))
            {
                blockType = BlockType.Air;
                return false;
            }

            blockType = chunk[localX, worldY, localZ];
            return true;
        }

        public bool TryGetLoadedChunk(ChunkCoordinates coords, out Chunk chunk)
        {
            return loadedChunks.TryGetValue(coords, out chunk);
        }

        private static int FloorDiv(int value, int divisor)
        {
            int result = value / divisor;
            if ((value ^ divisor) < 0 && value % divisor != 0)
            {
                result--;
            }

            return result;
        }

        public IReadOnlyList<Chunk> GetLoadedChunks()
        {
            return new List<Chunk>(loadedChunks.Values);
        }

        public bool EnsureChunksAround(int centerChunkX, int centerChunkZ, int radius)
        {
            bool addedNewChunk = false;
            for (int dz = -radius; dz <= radius; dz++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int chunkX = centerChunkX + dx;
                    int chunkZ = centerChunkZ + dz;
                    var key = new ChunkCoordinates(chunkX, chunkZ);
                    if (!loadedChunks.ContainsKey(key))
                    {
                        addedNewChunk = true;
                    }

                    GetOrCreateChunk(chunkX, chunkZ);
                }
            }

            return addedNewChunk;
        }

        /// <summary>
        /// Queue any not-yet-loaded chunks within <paramref name="radius"/> for background
        /// generation, closest-first. Cheap to call every tick: already-loaded or already-queued
        /// chunks are skipped. Actual generation happens off the main thread via
        /// <see cref="TryGenerateNext"/>.
        /// </summary>
        public void RequestChunksAround(int centerChunkX, int centerChunkZ, int radius, Point3D cameraPosition)
        {
            for (int dz = -radius; dz <= radius; dz++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int chunkX = centerChunkX + dx;
                    int chunkZ = centerChunkZ + dz;
                    var key = new ChunkCoordinates(chunkX, chunkZ);
                    if (loadedChunks.ContainsKey(key))
                    {
                        continue;
                    }

                    // pendingGeneration dedupes so a chunk is only queued once at a time.
                    if (pendingGeneration.TryAdd(key, 0))
                    {
                        EnqueueChunk(chunkX, chunkZ, cameraPosition);
                    }
                }
            }
        }

        /// <summary>
        /// Pull the next queued chunk request and generate it (on a background worker). Returns
        /// true only when a chunk was actually created, so callers can trigger a remesh.
        /// </summary>
        public bool TryGenerateNext()
        {
            if (!TryDequeueNext(out var request))
            {
                return false;
            }

            var key = new ChunkCoordinates(request.X, request.Z);
            bool created = !loadedChunks.ContainsKey(key);

            // GetOrCreateChunk generates + inserts + dirties existing neighbors (idempotent if
            // another path loaded it in the meantime).
            GetOrCreateChunk(request.X, request.Z);
            pendingGeneration.TryRemove(key, out _);
            return created;
        }

        public List<ChunkCoordinates> UnloadChunksOutside(int centerChunkX, int centerChunkZ, int radius)
        {
            var removed = new List<ChunkCoordinates>();
            foreach (var key in loadedChunks.Keys)
            {
                int dx = Math.Abs(key.X - centerChunkX);
                int dz = Math.Abs(key.Z - centerChunkZ);
                if (dx > radius || dz > radius)
                {
                    if (loadedChunks.TryRemove(key, out var _))
                    {
                        removed.Add(key);
                        pendingGeneration.TryRemove(key, out _);

                        // A removed chunk can expose faces on adjacent loaded chunks.
                        // Mark those neighbors dirty so border faces get rebuilt.
                        if (loadedChunks.TryGetValue(new ChunkCoordinates(key.X - 1, key.Z), out var left))
                            left.MarkDirty();
                        if (loadedChunks.TryGetValue(new ChunkCoordinates(key.X + 1, key.Z), out var right))
                            right.MarkDirty();
                        if (loadedChunks.TryGetValue(new ChunkCoordinates(key.X, key.Z - 1), out var back))
                            back.MarkDirty();
                        if (loadedChunks.TryGetValue(new ChunkCoordinates(key.X, key.Z + 1), out var front))
                            front.MarkDirty();
                    }
                }
            }

            return removed;
        }

        public void EnqueueChunk(int chunkX, int chunkZ, Point3D cameraPosition)
        {
            double priority = ComputeDistancePriority(chunkX, chunkZ, cameraPosition);
            lock (queueLock)
            {
                queue.Enqueue(new ChunkRequest(chunkX, chunkZ), priority);
            }
        }

        public bool TryDequeueNext(out ChunkRequest request)
        {
            lock (queueLock)
            {
                if (queue.Count > 0)
                {
                    request = queue.Dequeue();
                    return true;
                }
            }

            request = default;
            return false;
        }

        private static double ComputeDistancePriority(int chunkX, int chunkZ, Point3D cameraPosition)
        {
            double centerX = chunkX * ChunkSize + ChunkSize / 2.0;
            double centerZ = chunkZ * ChunkSize + ChunkSize / 2.0;
            double dx = cameraPosition.X - centerX;
            double dz = cameraPosition.Z - centerZ;
            return Math.Sqrt(dx * dx + dz * dz);
        }
    }
}
