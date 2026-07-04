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

        public ChunkLighting(IReadOnlyCollection<ChunkCoordinates> chunkCoords, int chunkSize, int chunkHeight, Func<int, int, int, BlockType> getBlock)
        {
            if (getBlock == null) throw new ArgumentNullException(nameof(getBlock));

            int minChunkX = int.MaxValue, maxChunkX = int.MinValue;
            int minChunkZ = int.MaxValue, maxChunkZ = int.MinValue;
            foreach (var c in chunkCoords)
            {
                if (c.X < minChunkX) minChunkX = c.X;
                if (c.X > maxChunkX) maxChunkX = c.X;
                if (c.Z < minChunkZ) minChunkZ = c.Z;
                if (c.Z > maxChunkZ) maxChunkZ = c.Z;
            }

            if (chunkCoords.Count == 0)
            {
                minChunkX = maxChunkX = 0;
                minChunkZ = maxChunkZ = 0;
            }

            height = chunkHeight;
            minX = minChunkX * chunkSize;
            minZ = minChunkZ * chunkSize;
            dimX = (maxChunkX - minChunkX + 1) * chunkSize;
            dimZ = (maxChunkZ - minChunkZ + 1) * chunkSize;

            opaque = new bool[dimX * height * dimZ];
            light = new byte[dimX * height * dimZ];

            for (int lx = 0; lx < dimX; lx++)
            {
                for (int lz = 0; lz < dimZ; lz++)
                {
                    for (int y = 0; y < height; y++)
                    {
                        opaque[Index(lx, y, lz)] = getBlock(minX + lx, y, minZ + lz) != BlockType.Air;
                    }
                }
            }

            var queue = new Queue<int>();
            SeedSkyLight(queue);
            Propagate(queue);
        }

        private int Index(int lx, int y, int lz)
        {
            return (lx * height + y) * dimZ + lz;
        }

        private void SeedSkyLight(Queue<int> queue)
        {
            for (int lx = 0; lx < dimX; lx++)
            {
                for (int lz = 0; lz < dimZ; lz++)
                {
                    // Walk down from the sky; every air block is fully sky-lit until the first
                    // opaque block, which casts everything below it into shadow (to be filled in by
                    // the flood fill from the sides).
                    for (int y = height - 1; y >= 0; y--)
                    {
                        int idx = Index(lx, y, lz);
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
            lz = idx % dimZ;
            int t = idx / dimZ;
            y = t % height;
            lx = t / height;
        }

        /// <summary>
        /// Light level (0..15) of the block at the given world coordinates. Blocks outside the
        /// computed region are assumed fully lit so unmeshed frontier faces don't render black.
        /// </summary>
        public int GetLight(int worldX, int y, int worldZ)
        {
            int lx = worldX - minX;
            int lz = worldZ - minZ;
            if (lx < 0 || lx >= dimX || lz < 0 || lz >= dimZ || y < 0 || y >= height)
            {
                return MaxLight;
            }

            return light[Index(lx, y, lz)];
        }

        /// <summary>
        /// Maps a discrete light level to a brightness multiplier, matching the Cubuild reference:
        /// a linear ramp from a small ambient minimum at level 0 up to full brightness at level 15.
        /// </summary>
        public static float Brightness(int lightLevel)
        {
            int clamped = Math.Clamp(lightLevel, 0, MaxLight);
            const float minBrightness = 0.04f;
            return minBrightness + (1f - minBrightness) * (clamped / (float)MaxLight);
        }
    }
}
