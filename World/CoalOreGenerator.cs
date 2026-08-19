using System;

namespace Cubuild.World
{
    /// <summary>
    /// Coal ore: blob veins (Cubuild-style minable clusters) that can appear at ANY depth but
    /// strongly prefer sitting just under the "living layer" — the surface grass/dirt where
    /// biomass would decompose and compress into coal in nature. Deep pockets are rarer (older,
    /// buried forests). Only replaces stone and dirt (never grass/bedrock/water/air), so veins
    /// never punch through the surface or a cave.
    ///
    /// Two modes:
    ///  - Ground chunks (PreferSurface=true): shallow coal just below the surface most of the
    ///    time (depth biased small, like Cubuild's pickOreY), plus occasional random-Y pockets
    ///    anywhere in the band.
    ///  - Deep chunks (PreferSurface=false): always a random Y in the deep stone (rare, sparse).
    /// </summary>
    public sealed class CoalOreGenerator
    {
        public bool Enabled = true;
        /// <summary>Blob attempts per chunk (Cubuild's coalAttempts ≈ 20 * ore scale).</summary>
        public int AttemptsPerChunk = 6;
        /// <summary>Probability an attempt picks the shallow "just under the living layer" spot.</summary>
        public float ShallowBias = 0.85f;
        /// <summary>How hard shallow picks hug the surface (1.0 = uniform, &gt;1 = tighter).</summary>
        public float DepthBiasPower = 2.2f;
        /// <summary>Shallow coal sits this far below the surface (blocks).</summary>
        public int ShallowMinDepth = 2;
        public int ShallowMaxDepth = 14;
        /// <summary>Blob size multiplier (1.0 = Cubuild default).</summary>
        public float BlobScale = 1.0f;
        /// <summary>When false, every attempt picks a random Y in the band (deep-layer mode).</summary>
        public bool PreferSurface = true;

        private readonly int _seed;

        public CoalOreGenerator(int seed)
        {
            _seed = seed;
        }

        public void Generate(Chunk chunk, int chunkX, int chunkZ, int terrainBandStart, int chunkSize, int chunkHeight)
        {
            if (!Enabled) return;

            byte[] blocks = chunk.RawBlocks;
            int height = chunkHeight;
            byte idCoal = (byte)BlockRegistry.GetId("coalore");
            byte idStone = (byte)BlockRegistry.GetId("stone");
            byte idDirt = (byte)BlockRegistry.GetId("dirt");

            var rand = new Random(unchecked(chunkX * 401719 + chunkZ * 811543 ^ _seed) ^ 0x51ED270B);

            int bandTopLocal = terrainBandStart + TerrainChunkProvider.TerrainBandBlocks - 1;
            if (bandTopLocal >= chunkHeight) bandTopLocal = chunkHeight - 1;

            for (int i = 0; i < AttemptsPerChunk; i++)
            {
                int lx = rand.Next(chunkSize);
                int lz = rand.Next(chunkSize);
                int surfaceLocalY = -999;
                if (PreferSurface)
                    surfaceLocalY = FindSurfaceLocalY(blocks, lx, lz, chunkSize, height, terrainBandStart);

                int localY; // LOCAL chunk Y of the blob center (what PlaceBlob needs)
                if (PreferSurface && surfaceLocalY >= terrainBandStart && rand.NextDouble() < ShallowBias)
                {
                    // Just under the living layer: small depths are much more likely.
                    double t = Math.Pow(rand.NextDouble(), DepthBiasPower);
                    int depth = ShallowMinDepth + (int)(t * (ShallowMaxDepth - ShallowMinDepth));
                    localY = surfaceLocalY - depth;
                    if (localY < 0) localY = 0;
                }
                else
                {
                    // Any Y in the band (deep pocket from an old buried forest / deep layer).
                    localY = terrainBandStart + rand.Next(bandTopLocal - terrainBandStart + 1);
                }

                PlaceBlob(blocks, chunkSize, height, lx, localY, lz, rand, idCoal, idStone, idDirt);
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

        // Cubuild's generateExperimentalMinable: a short line segment of elliptical blobs that
        // swells in the middle. Only replaces stone/dirt. Clamped to this chunk's block array.
        private static void PlaceBlob(byte[] blocks, int chunkSize, int height, int lx, int localY, int lz,
            Random rand, byte idCoal, byte idStone, byte idDirt)
        {
            double angle = rand.NextDouble() * Math.PI;
            double startX = lx + Math.Sin(angle) * 2.0;
            double endX = lx - Math.Sin(angle) * 2.0;
            double startZ = lz + Math.Cos(angle) * 2.0;
            double endZ = lz - Math.Cos(angle) * 2.0;
            double startY = localY + rand.Next(5) - 2;
            double endY = localY + rand.Next(5) - 2;

            for (int step = 0; step <= 16; step++)
            {
                double px = startX + (endX - startX) * (step / 16.0);
                double py = startY + (endY - startY) * (step / 16.0);
                double pz = startZ + (endZ - startZ) * (step / 16.0);
                double size = rand.NextDouble();
                double radius = ((Math.Sin((step / 16.0) * Math.PI) + 1.0) * size + 1.0);

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
                            if (b == idStone || b == idDirt) blocks[idx] = idCoal;
                        }
                    }
                }
            }
        }
    }
}
