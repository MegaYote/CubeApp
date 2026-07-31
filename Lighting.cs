using System;
using System.Collections.Generic;

namespace CubeApp
{
    /// <summary>
    /// Computes per-block light levels on a discrete 0..15 scale for a region of chunks using a
    /// flood-fill, modeled on the Cubuild reference's sky-light pass.
    ///
    /// Sky-exposed air (any air column open to the top of the world) is a light source at
    /// <see cref="MaxLight"/>. Light then spreads with a breadth-first flood fill, losing one level
    /// per block step. Because the BFS uses unit-cost steps across the 6 axis neighbours, a block's
    /// light equals MaxLight minus its Manhattan (taxicab) distance, through open space, to the
    /// nearest source. Opaque blocks stop the light; sky light spreads sideways and downward but
    /// never upward.
    ///
    /// The region covers the chunks handed to the mesher (a target chunk plus its loaded
    /// neighbours). Since light can travel at most 15 blocks, including the immediate neighbours is
    /// enough to light the target chunk's interior correctly.
    /// </summary>
    public sealed class ChunkLighting
    {
        public const int MaxLight = 15;

        // Sky light spreads sideways and downward, but not up (matching the Cubuild reference).
        private static readonly (int dx, int dy, int dz)[] SkyDirs =
        {
            (1, 0, 0), (-1, 0, 0),
            (0, -1, 0),
            (0, 0, 1), (0, 0, -1)
        };

        private readonly int minX;
        private readonly int minZ;
        private readonly int height;
        private readonly int dimX;
        private readonly int dimZ;
        private readonly bool[] opaque;
        private readonly byte[] light;

        // Meshing runs on a small fixed set of worker threads and each builds at most one
        // ChunkLighting at a time, so the big scratch arrays (~590k cells for a 3x3-chunk region)
        // are pooled per thread instead of being reallocated (and GC'd) on every single remesh.
        [ThreadStatic] private static bool[]? _opaquePool;
        [ThreadStatic] private static byte[]? _lightPool;
        [ThreadStatic] private static Queue<int>? _queuePool;

        public ChunkLighting(IReadOnlyDictionary<ChunkCoordinates, Chunk> chunks, int chunkSize, int chunkHeight)
        {
            if (chunks == null) throw new ArgumentNullException(nameof(chunks));

            int minChunkX = int.MaxValue, maxChunkX = int.MinValue;
            int minChunkZ = int.MaxValue, maxChunkZ = int.MinValue;
            foreach (var c in chunks.Keys)
            {
                if (c.X < minChunkX) minChunkX = c.X;
                if (c.X > maxChunkX) maxChunkX = c.X;
                if (c.Z < minChunkZ) minChunkZ = c.Z;
                if (c.Z > maxChunkZ) maxChunkZ = c.Z;
            }

            if (chunks.Count == 0)
            {
                minChunkX = maxChunkX = 0;
                minChunkZ = maxChunkZ = 0;
            }

            height = chunkHeight;
            minX = minChunkX * chunkSize;
            minZ = minChunkZ * chunkSize;
            dimX = (maxChunkX - minChunkX + 1) * chunkSize;
            dimZ = (maxChunkZ - minChunkZ + 1) * chunkSize;

            int cells = dimX * height * dimZ;
            if (_opaquePool == null || _opaquePool.Length < cells)
            {
                _opaquePool = new bool[cells];
                _lightPool = new byte[cells];
            }
            opaque = _opaquePool;
            light = _lightPool!;
            Array.Clear(opaque, 0, cells);
            Array.Clear(light, 0, cells);

            // Fill occupancy straight from each chunk's raw block bytes. Both layouts keep the
            // vertical column contiguous, so the inner copy is a tight sequential loop - no
            // delegate calls, no dictionary lookups, no per-cell coordinate math. Chunks missing
            // from the region (e.g. unloaded diagonal corners) simply stay air.
            foreach (var kv in chunks)
            {
                var c = kv.Value;
                byte[] raw = c.RawBlocks;
                int baseLX = kv.Key.X * chunkSize - minX;
                int baseLZ = kv.Key.Z * chunkSize - minZ;
                for (int x = 0; x < chunkSize; x++)
                {
                    for (int z = 0; z < chunkSize; z++)
                    {
                        int src = (x * chunkSize + z) * chunkHeight;
                        int dst = Index(baseLX + x, 0, baseLZ + z);
                        for (int y = 0; y < chunkHeight; y++)
                        {
                            // Opaque blocks skylight flood-fill (stone, dirt, planks...). Transparent-to-light blocks
                            // (water, glass, leaves) let light pass through so caves under water aren't black.
                            opaque[dst + y] = BlockRegistry.IsOpaque(raw[src + y]);
                        }
                    }
                }
            }

            var queue = _queuePool ??= new Queue<int>();
            queue.Clear();
            SeedSkyLight(queue);
            Propagate(queue);
        }

        // y is the fastest-varying index so vertical columns are contiguous (matches Chunk's
        // internal layout, which makes the occupancy fill and sky-light seeding sequential).
        private int Index(int lx, int y, int lz)
        {
            return (lx * dimZ + lz) * height + y;
        }

        private void SeedSkyLight(Queue<int> queue)
        {
            for (int lx = 0; lx < dimX; lx++)
            {
                for (int lz = 0; lz < dimZ; lz++)
                {
                    // Walk down from the sky; every air block is fully sky-lit until the first
                    // opaque block, which casts everything below it into shadow (to be filled in by
                    // the flood fill from the sides). Columns are contiguous in the new layout.
                    int colBase = Index(lx, 0, lz);
                    for (int y = height - 1; y >= 0; y--)
                    {
                        int idx = colBase + y;
                        if (opaque[idx])
                        {
                            break;
                        }

                        light[idx] = MaxLight;
                        queue.Enqueue(idx);
                    }
                }
            }
        }

        private void Propagate(Queue<int> queue)
        {
            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                int level = light[idx];
                if (level <= 1)
                {
                    continue;
                }

                Decode(idx, out int lx, out int y, out int lz);
                byte next = (byte)(level - 1);

                foreach (var (dx, dy, dz) in SkyDirs)
                {
                    int nlx = lx + dx;
                    int ny = y + dy;
                    int nlz = lz + dz;
                    if (nlx < 0 || nlx >= dimX || nlz < 0 || nlz >= dimZ || ny < 0 || ny >= height)
                    {
                        continue;
                    }

                    int nidx = Index(nlx, ny, nlz);
                    if (opaque[nidx] || light[nidx] >= next)
                    {
                        continue;
                    }

                    light[nidx] = next;
                    queue.Enqueue(nidx);
                }
            }
        }

        private void Decode(int idx, out int lx, out int y, out int lz)
        {
            y = idx % height;
            int t = idx / height;
            lz = t % dimZ;
            lx = t / dimZ;
        }

        /// <summary>
        /// Light level (0..15) of the block at the given world coordinates. Blocks outside the
        /// computed region return a low ambient light level to avoid harsh lighting artifacts
        /// at chunk borders, especially when underground.
        /// </summary>
        public int GetLight(int worldX, int y, int worldZ)
        {
            int lx = worldX - minX;
            int lz = worldZ - minZ;
            if (lx < 0 || lx >= dimX || lz < 0 || lz >= dimZ || y < 0 || y >= height)
            {
                // Return low ambient light instead of MaxLight to avoid harsh lighting
                // artifacts at chunk borders, particularly when underground
                return 5;
            }

            return light[Index(lx, y, lz)];
        }

        /// <summary>
        /// Maps a discrete light level to a brightness multiplier, matching Minecraft's classic
        /// lightBrightnessTable (Alpha 1.1.2_01 / Infdev 20100630 World.java):
        ///     v = 1 - light/15
        ///     table[light] = (1 - v) / (v*3 + 1) * (1 - 0.05) + 0.05
        /// A gamma curve with a 0.05 ambient minimum at level 0. It darkens low light levels far
        /// more aggressively than a linear ramp, which gives classic Minecraft its deep-cave
        /// contrast and torch falloff.
        /// </summary>
        public static float Brightness(int lightLevel)
        {
            int clamped = Math.Clamp(lightLevel, 0, MaxLight);
            float inverted = 1f - clamped / (float)MaxLight;
            const float ambient = 0.05f;
            return (1f - inverted) / (inverted * 3f + 1f) * (1f - ambient) + ambient;
        }
    }
}
