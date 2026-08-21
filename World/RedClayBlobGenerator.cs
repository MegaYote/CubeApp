using System;

namespace Cubuild.World
{
    /// <summary>
    /// Underground red clay blobs: small, sparse pockets of red clay buried in stone, giving
    /// players a way to obtain red clay when they can't find the paradise foothills biome.
    /// Much smaller and rarer than gravel splotches - meant to be a lucky find while mining,
    /// not a common resource. Only replaces stone (never ores, dirt, grass, bedrock, water,
    /// or air).
    /// </summary>
    public sealed class RedClayBlobGenerator
    {
        public bool Enabled = true;
        /// <summary>Independent blob attempts per chunk, each gated by ChancePerAttempt.</summary>
        public int AttemptsPerChunk = 2;
        /// <summary>Probability one attempt places a blob (~1 blob per 7 chunks).</summary>
        public float ChancePerAttempt = 0.14f;
        /// <summary>How deep below the surface blobs sit (blocks).</summary>
        public int MinDepth = 6;
        public int MaxDepth = 45;
        /// <summary>Blob scale multiplier (1.0 = default small pocket).</summary>
        public float BlobScale = 1.0f;

        private readonly int _seed;

        public RedClayBlobGenerator(int seed)
        {
            _seed = seed;
        }

        public void Generate(Chunk chunk, int chunkX, int chunkZ, int terrainBandStart, int chunkSize, int chunkHeight)
        {
            if (!Enabled) return;

            byte[] blocks = chunk.RawBlocks;
            int height = chunkHeight;
            byte idRedClay = (byte)BlockRegistry.GetId("redclay");
            byte idStone = (byte)BlockRegistry.GetId("stone");

            var rand = new Random(unchecked(chunkX * 131119 + chunkZ * 271817 ^ _seed) ^ 0x3A7B4C2D);

            int bandTopLocal = terrainBandStart + TerrainChunkProvider.TerrainBandBlocks - 1;
            if (bandTopLocal >= chunkHeight) bandTopLocal = chunkHeight - 1;

            for (int i = 0; i < AttemptsPerChunk; i++)
            {
                if (rand.NextDouble() >= ChancePerAttempt) continue;

                int lx = rand.Next(chunkSize);
                int lz = rand.Next(chunkSize);
                int surfaceLocalY = FindSurfaceLocalY(blocks, lx, lz, chunkSize, height, terrainBandStart);
                if (surfaceLocalY < terrainBandStart) continue;

                double t = Math.Pow(rand.NextDouble(), 1.6);
                int depth = MinDepth + (int)(t * (MaxDepth - MinDepth));
                int localY = surfaceLocalY - depth;
                if (localY < terrainBandStart) localY = terrainBandStart;

                PlaceBlob(blocks, chunkSize, height, lx, localY, lz, rand, idRedClay, idStone, BlobScale);
            }
        }

        private static int FindSurfaceLocalY(byte[] blocks, int lx, int lz, int chunkSize, int height, int terrainBandStart)
        {
            for (int ly = terrainBandStart + TerrainChunkProvider.TerrainBandBlocks - 1; ly >= terrainBandStart; ly--)
            {
                if (ly >= height) continue;
                if (blocks[(lx * chunkSize + lz) * height + ly] != 0) return ly;
            }
            return -999;
        }

        // Very small blob (~1-1.5 block radius): a short line of 3-5 ellipsoidal nodes.
        // Only replaces stone so it never eats ores, dirt, or opens into caves.
        private static void PlaceBlob(byte[] blocks, int chunkSize, int height, int lx, int localY, int lz,
            Random rand, byte idRedClay, byte idStone, float scale)
        {
            double angle = rand.NextDouble() * Math.PI;
            double reach = (0.8 + rand.NextDouble() * 0.7) * scale;
            double startX = lx + Math.Sin(angle) * reach;
            double endX = lx - Math.Sin(angle) * reach;
            double startZ = lz + Math.Cos(angle) * reach;
            double endZ = lz - Math.Cos(angle) * reach;
            double startY = localY + rand.Next(3) - 1;
            double endY = localY + rand.Next(3) - 1;

            int steps = 3 + rand.Next(3); // 3..5 nodes
            for (int step = 0; step <= steps; step++)
            {
                double px = startX + (endX - startX) * (step / (double)steps);
                double py = startY + (endY - startY) * (step / (double)steps);
                double pz = startZ + (endZ - startZ) * (step / (double)steps);
                double size = (0.5 + rand.NextDouble() * 0.5) * scale;
                double radius = ((Math.Sin((step / (double)steps) * Math.PI) + 1.0) * size + 0.5) * 0.7;

                int minX = Math.Max(0, (int)Math.Floor(px - radius));
                int maxX = Math.Min(chunkSize, (int)Math.Floor(px + radius) + 1);
                int minY = Math.Max(0, (int)Math.Floor(py - radius));
                int maxY = Math.Min(height, (int)Math.Floor(py + radius) + 1);
                int minZ = Math.Max(0, (int)Math.Floor(pz - radius));
                int maxZ = Math.Min(chunkSize, (int)Math.Floor(pz + radius) + 1);

                for (int ox = minX; ox < maxX; ox++)
                {
                    double ndx = ((ox + 0.5) - px) / radius;
                    for (int oy = minY; oy < maxY; oy++)
                    {
                        double ndy = ((oy + 0.5) - py) / radius;
                        for (int oz = minZ; oz < maxZ; oz++)
                        {
                            double ndz = ((oz + 0.5) - pz) / radius;
                            if (ndx * ndx + ndy * ndy + ndz * ndz >= 1.0) continue;
                            int idx = (ox * chunkSize + oz) * height + oy;
                            if (blocks[idx] == idStone) blocks[idx] = idRedClay;
                        }
                    }
                }
            }
        }
    }
}
