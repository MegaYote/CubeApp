using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using CubeApp.World;

namespace CubeApp
{
    public sealed class ChunkManager
    {
        public const int ChunkSize = 16;
        // World Y origin: local y=0 maps to this world Y. World spans WorldOriginY..(WorldOriginY + ChunkHeight).
        // Proposal A (tall chunk, lazy deep fill): the chunk is 448 tall so the world reaches down to
        // Y=-256. The Infdev terrain band occupies local Y 192..319 (world -64..63); local 0..191
        // (world -256..-65) is the DEEP ZONE - filled lazily when the player descends, with a bedrock
        // floor at local 0 so a surface-dug hole always has a visible bottom.
public const int WorldOriginY = -256;
    public const int ChunkHeight = 640;
        private readonly ConcurrentDictionary<ChunkCoordinates, Chunk> loadedChunks = new();
        // Chunk coords that have been modified (player edits / fluid flow) since load; these are
        // the only chunks a world save needs to serialize.
        private readonly HashSet<ChunkCoordinates> _modifiedChunks = new();
        public IReadOnlyCollection<ChunkCoordinates> ModifiedChunks => _modifiedChunks;
        private readonly PriorityQueue<ChunkRequest, double> queue = new();
        private readonly object queueLock = new();
        private readonly ConcurrentDictionary<ChunkCoordinates, byte> pendingGeneration = new();
        private readonly IChunkProvider chunkProvider;

        public ChunkManager(IChunkProvider? chunkProvider = null)
        {
            this.chunkProvider = chunkProvider ?? new InfdevChunkProvider();
        }

        public Chunk GetOrCreateChunk(int chunkX, int chunkZ)
        {            var key = new ChunkCoordinates(chunkX, chunkZ);
            bool created = false;
            var result = loadedChunks.GetOrAdd(key, _ =>
            {
                var chunk = chunkProvider.GenerateChunk(chunkX, chunkZ, ChunkSize, ChunkHeight);
                chunk.NeedsRemesh = true;
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
                    left.NeedsRemesh = true;
                if (loadedChunks.TryGetValue(new ChunkCoordinates(chunkX + 1, chunkZ), out var right))
                    right.NeedsRemesh = true;
                if (loadedChunks.TryGetValue(new ChunkCoordinates(chunkX, chunkZ - 1), out var back))
                    back.NeedsRemesh = true;
                if (loadedChunks.TryGetValue(new ChunkCoordinates(chunkX, chunkZ + 1), out var front))
                    front.NeedsRemesh = true;
            }

            return result;
        }

        // Stamps a saved chunk's block+meta data over the (re)generated chunk on world load.
        public void ApplySavedChunk(int chunkX, int chunkZ, byte[] blocks, byte[] meta)
        {
            var chunk = GetOrCreateChunk(chunkX, chunkZ);
            if (blocks != null) Array.Copy(blocks, chunk.RawBlocks, Math.Min(blocks.Length, chunk.RawBlocks.Length));
            if (meta != null) Array.Copy(meta, chunk.RawMeta, Math.Min(meta.Length, chunk.RawMeta.Length));
            chunk.NeedsRemesh = true;
            _modifiedChunks.Add(new ChunkCoordinates(chunkX, chunkZ));
        }

public bool TrySetBlock(int worldX, int worldY, int worldZ, int blockId)
        {
            return TrySetBlock(worldX, worldY, worldZ, blockId, 0);
        }

        public bool TrySetBlock(int worldX, int worldY, int worldZ, int blockId, int meta)
        {
            int chunkX = FloorDiv(worldX, ChunkSize);
            int chunkZ = FloorDiv(worldZ, ChunkSize);
            var chunk = GetOrCreateChunk(chunkX, chunkZ);
            int localX = worldX - chunk.OriginX;
            int localZ = worldZ - chunk.OriginZ;
            int localY = chunk.WorldYToLocal(worldY);
            if (!chunk.IsInBounds(localX, localY, localZ))
            {
                return false;
            }

            chunk[localX, localY, localZ] = blockId;
            chunk.SetMeta(localX, localY, localZ, (byte)meta);
            MarkDirty(chunkX, chunkZ, localX, localZ);
            return true;
        }

        /// <summary>
        /// Like <see cref="TrySetBlock(int,int,int,int,int)"/> but refuses to generate the target
        /// chunk. Fluid simulation uses this so a spreading water edge can never force terrain
        /// generation into unloaded territory; it simply doesn't flow there.
        /// </summary>
        public bool TrySetBlockLoadedOnly(int worldX, int worldY, int worldZ, int blockId, int meta)
        {
            int chunkX = FloorDiv(worldX, ChunkSize);
            int chunkZ = FloorDiv(worldZ, ChunkSize);
            if (!loadedChunks.TryGetValue(new ChunkCoordinates(chunkX, chunkZ), out var chunk))
            {
                return false;
            }

            int localX = worldX - chunk.OriginX;
            int localZ = worldZ - chunk.OriginZ;
            int localY = chunk.WorldYToLocal(worldY);
            if (!chunk.IsInBounds(localX, localY, localZ))
            {
                return false;
            }

            chunk[localX, localY, localZ] = blockId;
            chunk.SetMeta(localX, localY, localZ, (byte)meta);
            MarkDirty(chunkX, chunkZ, localX, localZ);
            return true;
        }

        private void MarkDirty(int chunkX, int chunkZ, int localX, int localZ)
        {
            if (!loadedChunks.TryGetValue(new ChunkCoordinates(chunkX, chunkZ), out var chunk))
            {
                return;
            }

            // Remember this chunk for world saving (only modified chunks get serialized; the
            // rest regenerate from the seed).
            _modifiedChunks.Add(new ChunkCoordinates(chunkX, chunkZ));

            // mark this chunk dirty so it will be remeshed
            chunk.NeedsRemesh = true;

            // if modification touches chunk boundaries, mark neighbor chunks dirty as well
            if (localX == 0 && loadedChunks.TryGetValue(new ChunkCoordinates(chunkX - 1, chunkZ), out var left))
                left.NeedsRemesh = true;
            if (localX == ChunkSize - 1 && loadedChunks.TryGetValue(new ChunkCoordinates(chunkX + 1, chunkZ), out var right))
                right.NeedsRemesh = true;
            if (localZ == 0 && loadedChunks.TryGetValue(new ChunkCoordinates(chunkX, chunkZ - 1), out var back))
                back.NeedsRemesh = true;
            if (localZ == ChunkSize - 1 && loadedChunks.TryGetValue(new ChunkCoordinates(chunkX, chunkZ + 1), out var front))
                front.NeedsRemesh = true;

            // A change on a chunk corner also affects the diagonal neighbour: the water pass
            // samples the 2x2 block neighbourhood around each corner, so the corner cell of a
            // diagonal chunk feeds the surface height of the four-chunk junction. Without this,
            // an edit/flow at a corner would leave the diagonal chunk's junction water stale.
            if (localX == 0 && localZ == 0 && loadedChunks.TryGetValue(new ChunkCoordinates(chunkX - 1, chunkZ - 1), out var diagNW))
                diagNW.NeedsRemesh = true;
            if (localX == ChunkSize - 1 && localZ == 0 && loadedChunks.TryGetValue(new ChunkCoordinates(chunkX + 1, chunkZ - 1), out var diagNE))
                diagNE.NeedsRemesh = true;
            if (localX == 0 && localZ == ChunkSize - 1 && loadedChunks.TryGetValue(new ChunkCoordinates(chunkX - 1, chunkZ + 1), out var diagSW))
                diagSW.NeedsRemesh = true;
            if (localX == ChunkSize - 1 && localZ == ChunkSize - 1 && loadedChunks.TryGetValue(new ChunkCoordinates(chunkX + 1, chunkZ + 1), out var diagSE))
                diagSE.NeedsRemesh = true;
        }

        public bool TryGetLoadedBlock(int worldX, int worldY, int worldZ, out int blockId)
        {
            int chunkX = FloorDiv(worldX, ChunkSize);
            int chunkZ = FloorDiv(worldZ, ChunkSize);
            if (!loadedChunks.TryGetValue(new ChunkCoordinates(chunkX, chunkZ), out var chunk))
            {
                blockId = BlockRegistry.AirId;
                return false;
            }

            int localX = worldX - chunk.OriginX;
            int localZ = worldZ - chunk.OriginZ;
            int localY = chunk.WorldYToLocal(worldY);
            if (!chunk.IsInBounds(localX, localY, localZ))
            {
                blockId = BlockRegistry.AirId;
                return false;
            }

            blockId = chunk[localX, localY, localZ];
            return true;
        }

        public bool TryGetLoadedBlockAndMeta(int worldX, int worldY, int worldZ, out int blockId, out byte meta)
        {
            int chunkX = FloorDiv(worldX, ChunkSize);
            int chunkZ = FloorDiv(worldZ, ChunkSize);
            if (!loadedChunks.TryGetValue(new ChunkCoordinates(chunkX, chunkZ), out var chunk))
            {
                blockId = BlockRegistry.AirId;
                meta = 0;
                return false;
            }

            int localX = worldX - chunk.OriginX;
            int localZ = worldZ - chunk.OriginZ;
            int localY = chunk.WorldYToLocal(worldY);
            if (!chunk.IsInBounds(localX, localY, localZ))
            {
                blockId = BlockRegistry.AirId;
                meta = 0;
                return false;
            }

            blockId = chunk[localX, localY, localZ];
            meta = chunk.GetMeta(localX, localY, localZ);
            return true;
        }

        /// <summary>Block id at world coords, or AirId when the chunk isn't loaded / coords out of bounds.</summary>
        public int GetBlockAt(int worldX, int worldY, int worldZ)
        {
            return TryGetLoadedBlock(worldX, worldY, worldZ, out var id) ? id : BlockRegistry.AirId;
        }

        /// <summary>Block metadata at world coords, or 0 when the chunk isn't loaded / coords out of bounds.</summary>
        public byte GetMetaAt(int worldX, int worldY, int worldZ)
        {
            return TryGetLoadedBlockAndMeta(worldX, worldY, worldZ, out _, out var meta) ? meta : (byte)0;
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

        /// <summary>
        /// Live view of all loaded chunks. No defensive copy: ConcurrentDictionary.Values
        /// enumerates over an internal snapshot, so iterating is safe against chunks being
        /// generated/removed by worker threads while the render thread scans.
        /// </summary>
        public ICollection<Chunk> GetLoadedChunks() => loadedChunks.Values;

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
                            left.NeedsRemesh = true;
                        if (loadedChunks.TryGetValue(new ChunkCoordinates(key.X + 1, key.Z), out var right))
                            right.NeedsRemesh = true;
                        if (loadedChunks.TryGetValue(new ChunkCoordinates(key.X, key.Z - 1), out var back))
                            back.NeedsRemesh = true;
                        if (loadedChunks.TryGetValue(new ChunkCoordinates(key.X, key.Z + 1), out var front))
                            front.NeedsRemesh = true;
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
