using System;

namespace Cubuild.World
{
    /// <summary>
    /// Clay discs on the floors of oceans and lakes: small, OCCASIONAL flat circular patches
    /// of red clay that sit on the bottom of any water body (ocean floors, lake beds, flooded
    /// valleys). Only spawns where the column actually holds water, and only replaces the
    /// natural floor material (sand/gravel/dirt/stone) - never ores, bedrock, grass, water or
    /// air, and never inside another feature.
    /// </summary>
    public sealed class ClayDiscGenerator
    {
        public bool Enabled = true;
        /// <summary>Independent disc attempts per chunk, each gated by ChancePerAttempt.</summary>
        public int AttemptsPerChunk = 2;
        /// <summary>Probability one attempt places a disc (~1 disc per 3.5 chunks).</summary>
        public float ChancePerAttempt = 0.14f;
        /// <summary>Disc radius in blocks (radius + random up to +RadiusExtra).</summary>
        public int Radius = 2;
        public int RadiusExtra = 3;

        private readonly int _seed;

        public ClayDiscGenerator(int seed)
        {
            _seed = seed;
        }

        public void Generate(Chunk chunk, int chunkX, int chunkZ, int terrainBandStart, int chunkSize, int chunkHeight)
        {
            if (!Enabled) return;

            byte[] blocks = chunk.RawBlocks;
            int height = chunkHeight;
            byte idRedClay = (byte)BlockRegistry.GetId("redclay");
            byte idWater = (byte)BlockRegistry.GetId("water");
            byte idSand = (byte)BlockRegistry.GetId("sand");
            byte idGravel = (byte)BlockRegistry.GetId("gravel");
            byte idDirt = (byte)BlockRegistry.GetId("dirt");
            byte idStone = (byte)BlockRegistry.GetId("stone");

            var rand = new Random(unchecked(chunkX * 71777 + chunkZ * 53357 ^ _seed) ^ 0x5C19E7A1);

            int bandTopLocal = terrainBandStart + TerrainChunkProvider.TerrainBandBlocks - 1;
            if (bandTopLocal >= chunkHeight) bandTopLocal = chunkHeight - 1;

            for (int i = 0; i < AttemptsPerChunk; i++)
            {
                if (rand.NextDouble() >= ChancePerAttempt) continue;

                int lx = rand.Next(chunkSize);
                int lz = rand.Next(chunkSize);

                // Only water columns qualify: the topmost non-air block must be water
                // (a lake, an ocean, a flooded valley), and the floor is the first solid
                // block underneath it. Land columns and bare cave openings are skipped.
                int floorY = -1;
                bool sawWater = false;
                for (int ly = bandTopLocal; ly >= terrainBandStart; ly--)
                {
                    byte id = blocks[(lx * chunkSize + lz) * height + ly];
                    if (id == 0) continue;
                    if (id == idWater) { sawWater = true; continue; }
                    if (sawWater) { floorY = ly; }
                    break;
                }
                if (floorY < terrainBandStart) continue;

                // The disc: a flat, round patch 1 block thick (occasionally 2) on the floor.
                int radius = Radius + rand.Next(RadiusExtra + 1);
                int thickness = rand.Next(3) == 0 ? 2 : 1;
                int centerX = lx, centerZ = lz;

                for (int dx = -radius; dx <= radius; dx++)
                {
                    int ox = centerX + dx;
                    if (ox < 0 || ox >= chunkSize) continue;
                    for (int dz = -radius; dz <= radius; dz++)
                    {
                        int oz = centerZ + dz;
                        if (oz < 0 || oz >= chunkSize) continue;
                        if (dx * dx + dz * dz > radius * radius + radius * 0.25) continue; // roundish

                        for (int t = 0; t < thickness; t++)
                        {
                            int ly = floorY - t;
                            if (ly < terrainBandStart) break;
                            int idx = (ox * chunkSize + oz) * height + ly;
                            byte b = blocks[idx];
                            if (b == idRedClay) continue;
                            if (b == idSand || b == idGravel || b == idDirt || b == idStone)
                            {
                                blocks[idx] = idRedClay;
                            }
                        }
                    }
                }
            }
        }
    }
}