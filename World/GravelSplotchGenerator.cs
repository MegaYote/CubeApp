using System;

namespace Cubuild.World
{
    /// <summary>
    /// Underground gravel splotches: small, OCCASIONAL pockets of gravel buried in the stone
    /// of the ground band. Deliberately sparse - the point is that digging down eventually
    /// turns one up (a nice little find), not that the ground is full of it. Only replaces
    /// stone/dirt (never ores, grass, bedrock, water, or air), so splotches never punch
    /// through the surface or into a cave opening.
    /// </summary>
    public sealed class GravelSplotchGenerator
    {
        public bool Enabled = true;
        /// <summary>Independent blob attempts per chunk, each gated by ChancePerAttempt.</summary>
        public int AttemptsPerChunk = 2;
        /// <summary>Probability one attempt actually places a splotch (~1 splotch per 1.5 chunks).</summary>
        public float ChancePerAttempt = 0.35f;
        /// <summary>How deep below the surface splotches sit (blocks).</summary>
        public int MinDepth = 4;
        public int MaxDepth = 40;
        /// <summary>Blob size multiplier (1.0 = default small pocket).</summary>
        public float BlobScale = 1.0f;

        private readonly int _seed;

        public GravelSplotchGenerator(int seed)
        {
            _seed = seed;
        }

        public void Generate(Chunk chunk, int chunkX, int chunkZ, int terrainBandStart, int chunkSize, int chunkHeight)
        {
            if (!Enabled) return;

            byte[] blocks = chunk.RawBlocks;
            int height = chunkHeight;
            byte idGravel = (byte)BlockRegistry.GetId("gravel");
            byte idStone = (byte)BlockRegistry.GetId("stone");
            byte idDirt = (byte)BlockRegistry.GetId("dirt");

            var rand = new Random(unchecked(chunkX * 171119 + chunkZ * 351817 ^ _seed) ^ 0x2A93D15C);

            int bandTopLocal = terrainBandStart + TerrainChunkProvider.TerrainBandBlocks - 1;
            if (bandTopLocal >= chunkHeight) bandTopLocal = chunkHeight - 1;

            for (int i = 0; i < AttemptsPerChunk; i++)
            {
                if (rand.NextDouble() >= ChancePerAttempt) continue;

                int lx = rand.Next(chunkSize);
                int lz = rand.Next(chunkSize);
                int surfaceLocalY = FindSurfaceLocalY(blocks, lx, lz, chunkSize, height, terrainBandStart);
                if (surfaceLocalY < terrainBandStart) continue;

                // Underground: a band of depths below the surface column (never on it).
                double t = Math.Pow(rand.NextDouble(), 1.6);
                int depth = MinDepth + (int)(t * (MaxDepth - MinDepth));
                int localY = surfaceLocalY - depth;
                if (localY < terrainBandStart) localY = terrainBandStart;

                PlaceSplotch(blocks, chunkSize, height, lx, localY, lz, rand, idGravel, idStone, idDirt);
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

        // Small short-line blob (MC gravel-pocket scale, ~1.5-2.5 block radius). Only replaces
        // stone/dirt so it never eats ores, bedrock, or opens into caves. Clamped to the chunk.
        private static void PlaceSplotch(byte[] blocks, int chunkSize, int height, int lx, int localY, int lz,
            Random rand, byte idGravel, byte idStone, byte idDirt)
        {
            double angle = rand.NextDouble() * Math.PI;
            double startX = lx + Math.Sin(angle) * 1.5;
            double endX = lx - Math.Sin(angle) * 1.5;
            double startZ = lz + Math.Cos(angle) * 1.5;
            double endZ = lz - Math.Cos(angle) * 1.5;
            double startY = localY + rand.Next(3) - 1;
            double endY = localY + rand.Next(3) - 1;

            int steps = 6 + rand.Next(4); // 6..9
            for (int step = 0; step <= steps; step++)
            {
                double px = startX + (endX - startX) * (step / (double)steps);
                double py = startY + (endY - startY) * (step / (double)steps);
                double pz = startZ + (endZ - startZ) * (step / (double)steps);
                double size = 0.6 + rand.NextDouble() * 0.8;
                double radius = ((Math.Sin((step / (double)steps) * Math.PI) + 1.0) * size + 0.8) * 0.85;

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
                            byte b = blocks[idx];
                            if (b == idStone || b == idDirt) blocks[idx] = idGravel;
                        }
                    }
                }
            }
        }
    }
}
