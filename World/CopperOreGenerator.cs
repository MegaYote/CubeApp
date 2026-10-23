using System;
using System.Collections.Generic;

namespace Cubuild.World
{
    /// <summary>
    /// Small, shallow copper veins for Frontier Survival worlds. Most veins sit 1-8 blocks
    /// beneath the terrain surface; a smaller share follows exposed cave and cliff strata.
    /// The per-chunk random stream is independent of all other generation features.
    /// </summary>
    public sealed class CopperOreGenerator
    {
        /// <summary>Disabled unless the owning world opts into Frontier progression.</summary>
        public bool Enabled { get; set; }

        /// <summary>Small vein attempts per chunk; tuned for a find within a few explored chunks.</summary>
        public int VeinsPerChunk { get; set; } = 4;

        /// <summary>Share of vein attempts that follow a shallow exposed cave/cliff wall.</summary>
        public double CaveStrataChance { get; set; } = 0.2;

        private const int MaxSurfaceDepth = 8;
        private const int CaveStrataSearchDepth = 32;
        private readonly int _seed;

        private readonly struct Target
        {
            public readonly int X;
            public readonly int Y;
            public readonly int Z;

            public Target(int x, int y, int z)
            {
                X = x;
                Y = y;
                Z = z;
            }
        }

        public CopperOreGenerator(int seed)
        {
            _seed = seed;
        }

        public void Generate(Chunk chunk, int chunkX, int chunkZ, int terrainBandStart, int chunkSize, int chunkHeight)
        {
            if (!Enabled || VeinsPerChunk <= 0 || chunkSize <= 0 || chunkHeight <= 0) return;

            byte[] blocks = chunk.RawBlocks;
            byte idCopper = (byte)BlockRegistry.GetId("copperore");
            byte idStone = (byte)BlockRegistry.GetId("stone");
            byte idDirt = (byte)BlockRegistry.GetId("dirt");
            byte idWater = (byte)BlockRegistry.GetId("water");
            byte idBedrock = (byte)BlockRegistry.GetId("bedrock");
            byte idLog = (byte)BlockRegistry.GetId("log");
            byte idLeaves = (byte)BlockRegistry.GetId("leaves");

            // Stable seed mixing gives each world/chunk its own stream without consuming any
            // random values from terrain, caves, trees, or the other ore generators.
            int chunkSeed = unchecked(((_seed * 397) ^ chunkX) * 397 ^ chunkZ ^ 0x43555052);
            var rand = new Random(chunkSeed);

            int bandBottom = Math.Max(0, terrainBandStart);
            int bandTop = Math.Min(chunkHeight - 1,
                terrainBandStart + TerrainChunkProvider.TerrainBandBlocks - 1);
            if (bandBottom > bandTop) return;

            var surfaces = new int[chunkSize * chunkSize];
            var strataTargets = new List<Target>();

            for (int x = 0; x < chunkSize; x++)
            {
                for (int z = 0; z < chunkSize; z++)
                {
                    int surfaceY = FindTerrainSurface(blocks, x, z, chunkSize, chunkHeight,
                        bandBottom, bandTop, idWater, idBedrock, idLog, idLeaves);
                    surfaces[x * chunkSize + z] = surfaceY;
                    if (surfaceY < bandBottom) continue;

                    // Find stone/dirt exposed to air in shallow caves or on cliff faces. Veins
                    // spawned here sit just inside the wall, rather than filling the open space.
                    int lowestY = Math.Max(bandBottom, surfaceY - CaveStrataSearchDepth);
                    for (int y = surfaceY - 1; y >= lowestY; y--)
                    {
                        byte block = blocks[Index(x, y, z, chunkSize, chunkHeight)];
                        if ((block == idStone || block == idDirt)
                            && TouchesAir(blocks, x, y, z, chunkSize, chunkHeight))
                        {
                            strataTargets.Add(new Target(x, y, z));
                        }
                    }
                }
            }

            for (int vein = 0; vein < VeinsPerChunk; vein++)
            {
                Target target;
                if (strataTargets.Count > 0 && rand.NextDouble() < CaveStrataChance)
                {
                    target = strataTargets[rand.Next(strataTargets.Count)];
                }
                else
                {
                    int x = rand.Next(chunkSize);
                    int z = rand.Next(chunkSize);
                    int surfaceY = surfaces[x * chunkSize + z];
                    if (surfaceY < bandBottom) continue;

                    int availableDepth = Math.Min(MaxSurfaceDepth, surfaceY - bandBottom);
                    if (availableDepth <= 0) continue;

                    // A power curve favors the top few blocks while retaining targets all the
                    // way down to the 8-block limit.
                    int depth = 1 + (int)(Math.Pow(rand.NextDouble(), 1.5) * availableDepth);
                    target = new Target(x, surfaceY - depth, z);
                }

                PlaceVein(blocks, chunkSize, chunkHeight, bandBottom, bandTop,
                    target, rand, idCopper, idStone, idDirt);
            }
        }

        private static int FindTerrainSurface(byte[] blocks, int x, int z, int chunkSize, int chunkHeight,
            int bandBottom, int bandTop, byte idWater, byte idBedrock, byte idLog, byte idLeaves)
        {
            for (int y = bandTop; y >= bandBottom; y--)
            {
                byte block = blocks[Index(x, y, z, chunkSize, chunkHeight)];
                // Water and bedrock are not terrain surfaces. Tree canopies/logs are also skipped
                // so the anchor remains the ground even when generation runs after tree placement.
                if (block == BlockRegistry.AirId || block == idWater || block == idBedrock
                    || block == idLog || block == idLeaves)
                    continue;
                return y;
            }
            return -1;
        }

        private static bool TouchesAir(byte[] blocks, int x, int y, int z, int chunkSize, int chunkHeight)
        {
            if (y > 0 && blocks[Index(x, y - 1, z, chunkSize, chunkHeight)] == BlockRegistry.AirId) return true;
            if (y + 1 < chunkHeight && blocks[Index(x, y + 1, z, chunkSize, chunkHeight)] == BlockRegistry.AirId) return true;
            if (x > 0 && blocks[Index(x - 1, y, z, chunkSize, chunkHeight)] == BlockRegistry.AirId) return true;
            if (x + 1 < chunkSize && blocks[Index(x + 1, y, z, chunkSize, chunkHeight)] == BlockRegistry.AirId) return true;
            if (z > 0 && blocks[Index(x, y, z - 1, chunkSize, chunkHeight)] == BlockRegistry.AirId) return true;
            if (z + 1 < chunkSize && blocks[Index(x, y, z + 1, chunkSize, chunkHeight)] == BlockRegistry.AirId) return true;
            return false;
        }

        private static void PlaceVein(byte[] blocks, int chunkSize, int chunkHeight, int bandBottom, int bandTop,
            Target target, Random rand, byte idCopper, byte idStone, byte idDirt)
        {
            double angle = rand.NextDouble() * Math.PI;
            double startX = target.X + Math.Sin(angle) * 2.0;
            double endX = target.X - Math.Sin(angle) * 2.0;
            double startZ = target.Z + Math.Cos(angle) * 2.0;
            double endZ = target.Z - Math.Cos(angle) * 2.0;
            double startY = target.Y + rand.Next(3) - 1;
            double endY = target.Y + rand.Next(3) - 1;

            const int steps = 7;
            for (int step = 0; step <= steps; step++)
            {
                double t = step / (double)steps;
                double px = startX + (endX - startX) * t;
                double py = startY + (endY - startY) * t;
                double pz = startZ + (endZ - startZ) * t;
                double radius = 0.9 + rand.NextDouble() * 0.55;

                int minX = Math.Max(0, (int)Math.Floor(px - radius));
                int maxX = Math.Min(chunkSize - 1, (int)Math.Floor(px + radius));
                int minY = Math.Max(bandBottom, (int)Math.Floor(py - radius));
                int maxY = Math.Min(bandTop, (int)Math.Floor(py + radius));
                int minZ = Math.Max(0, (int)Math.Floor(pz - radius));
                int maxZ = Math.Min(chunkSize - 1, (int)Math.Floor(pz + radius));

                for (int x = minX; x <= maxX; x++)
                {
                    double dx = ((x + 0.5) - px) / radius;
                    for (int y = minY; y <= maxY; y++)
                    {
                        double dy = ((y + 0.5) - py) / radius;
                        for (int z = minZ; z <= maxZ; z++)
                        {
                            double dz = ((z + 0.5) - pz) / radius;
                            if (dx * dx + dy * dy + dz * dz >= 1.0) continue;

                            int index = Index(x, y, z, chunkSize, chunkHeight);
                            byte block = blocks[index];
                            if (block == idStone || block == idDirt) blocks[index] = idCopper;
                        }
                    }
                }
            }
        }

        private static int Index(int x, int y, int z, int chunkSize, int chunkHeight)
            => (x * chunkSize + z) * chunkHeight + y;
    }
}
