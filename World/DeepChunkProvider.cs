using System;

namespace CubeApp.World
{
    /// <summary>
    /// Generates the DEEP layer (world -256..-65): solid stone carved into a labyrinth of
    /// NIGHTMARE caves, plus a bedrock floor at the very bottom (local 0..3, world -256..-253).
    ///
    /// The goal is genuine dread: huge pitch-black cathedral caverns (1.12 lighting means zero
    /// light down here), long snaking web-tunnels that cross chunk borders, sheer vertical
    /// shafts that drop you into darkness, claustrophobic crawl spaces, and walls left jagged
    /// and raw. The player should think twice about going down there.
    ///
    /// Cave systems are deterministic per world seed. They spawn from a 17x17 neighbourhood of
    /// chunk seeds (exactly like the ground layer's caves) so tunnels, shafts and caverns carry
    /// seamlessly across chunk borders instead of dying at the edge.
    /// </summary>
    public sealed class DeepChunkProvider : IChunkProvider
    {
        private readonly int seed;
        /// <summary>Coal ore in the deep layer: rare pockets at ANY depth (older buried forests).</summary>
        public CoalOreGenerator CoalOres { get; private set; }

        public DeepChunkProvider(int seed = 0)
        {
            this.seed = seed;
            CoalOres = new CoalOreGenerator(seed)
            {
                PreferSurface = false, // no surface here - any Y in the deep stone
                AttemptsPerChunk = 3,  // rare down here (older buried forests)
                BlobScale = 1.3f,      // slightly chunky pockets
            };
        }

        public Chunk GenerateChunk(int chunkX, int chunkZ, int chunkSize, int chunkHeight)
        {
            int originX = chunkX * chunkSize;
            int originZ = chunkZ * chunkSize;
            int originY = ChunkManager.DeepOriginY;
            var chunk = new Chunk(chunkSize, chunkHeight, chunkSize, originX, originY, originZ);

            int idBedrock = BlockRegistry.GetId("bedrock");
            int idStone = BlockRegistry.GetId("stone");

            // Solid stone throughout, then carve caves.
            byte[] blocks = chunk.RawBlocks;
            const int height = 192;
            for (int x = 0; x < chunkSize; x++)
            for (int z = 0; z < chunkSize; z++)
            {
                int baseIdx = (x * chunkSize + z) * height;
                for (int y = 0; y < height; y++)
                {
                    blocks[baseIdx + y] = (byte)(y < 4 ? idBedrock : idStone);
                }
            }

            GenerateNightmareCaves(chunkX, chunkZ, blocks, chunkSize, height);

            // Coal at any depth in the deep stone (rare).
            CoalOres.Generate(chunk, chunkX, chunkZ, 0, chunkSize, chunkHeight);

            return chunk;
        }

        // ------------------------------------------------------------------
        // Nightmare cave generation
        // ------------------------------------------------------------------

        private void GenerateNightmareCaves(int chunkX, int chunkZ, byte[] blocks, int chunkSize, int height)
        {
            var rand = new Random(seed);
            long var5 = rand.Next() * 2L + 1L;
            long var7 = rand.Next() * 2L + 1L;

            // Iterate the 17x17 neighbourhood of chunk seeds so systems spawn from NEIGHBOUR
            // chunks and carve across the border into this chunk - the same trick the ground
            // layer uses, so nothing dies at a chunk edge.
            for (int var9 = chunkX - 8; var9 <= chunkX + 8; var9++)
            {
                for (int var10 = chunkZ - 8; var10 <= chunkZ + 8; var10++)
                {
                    var rand2 = new Random(unchecked((int)((long)var9 * var5 + (long)var10 * var7 ^ seed)));

                    // ~1 in 3 neighbour seeds hosts a system; occasionally two.
                    if (rand2.Next(3) != 0) continue;
                    int systems = rand2.Next(2) + 1;
                    for (int i = 0; i < systems; i++)
                    {
                        double x = var9 * 16 + rand2.Next(16) + 8;
                        double y = 8 + rand2.Next(168);
                        double z = var10 * 16 + rand2.Next(16) + 8;
                        int type = rand2.Next(4); // 0 cavern, 1 web tunnel, 2 shaft, 3 crawl
                        CarveSystem(blocks, chunkX, chunkZ, chunkSize, height, rand2, x, y, z, type, 0);
                    }
                }
            }
        }

        // Recursively carve one cave system. Each type has its own shape; branches spawn new
        // systems of a random type so caverns, tunnels and shafts all link together into one
        // sprawling nightmare web.
        private void CarveSystem(byte[] blocks, int chunkX, int chunkZ, int chunkSize, int height,
            Random rand, double x, double y, double z, int type, int depth)
        {
            if (depth > 3) return; // cap recursion so a system can't explode

            double yaw, pitch, size, length;
            int branches;
            switch (type)
            {
                case 0: // MEGA CAVERN: a short, fat, cathedral-scale chamber
                    size = 6.0 + rand.NextDouble() * 9.0;      // radius 6-15
                    yaw = rand.NextDouble() * Math.PI * 2.0;
                    pitch = (rand.NextDouble() - 0.5) * 0.5;
                    length = 10 + rand.Next(18);
                    branches = 2 + rand.Next(3);
                    break;
                case 1: // WEB TUNNEL: long wandering corridor that branches
                    size = 2.5 + rand.NextDouble() * 2.5;
                    yaw = rand.NextDouble() * Math.PI * 2.0;
                    pitch = (rand.NextDouble() - 0.5) * 0.7;
                    length = 30 + rand.Next(55);
                    branches = 2 + rand.Next(4);
                    break;
                case 2: // VERTICAL SHAFT: a near-straight pit or chimney
                    size = 2.0 + rand.NextDouble() * 3.5;
                    yaw = rand.NextDouble() * Math.PI * 2.0;
                    pitch = (rand.Next(2) == 0 ? 1 : -1) * (0.88 + rand.NextDouble() * 0.11);
                    length = 25 + rand.Next(45);
                    branches = 1 + rand.Next(2);
                    break;
                default: // CRAWL: thin, winding, claustrophobic
                    size = 1.0 + rand.NextDouble() * 1.5;
                    yaw = rand.NextDouble() * Math.PI * 2.0;
                    pitch = (rand.NextDouble() - 0.5) * 0.35;
                    length = 40 + rand.Next(70);
                    branches = 1 + rand.Next(3);
                    break;
            }

            CarveNode(blocks, chunkX, chunkZ, chunkSize, height, rand,
                x, y, z, (float)size, (float)yaw, (float)pitch, -1, (int)length, 1.0, type == 0);

            for (int b = 0; b < branches; b++)
            {
                double bx = x + (rand.NextDouble() - 0.5) * 14.0;
                double by = y + (rand.NextDouble() - 0.5) * 10.0;
                double bz = z + (rand.NextDouble() - 0.5) * 14.0;
                CarveSystem(blocks, chunkX, chunkZ, chunkSize, height, rand, bx, by, bz, rand.Next(4), depth + 1);
            }
        }

        // One random-walker tube. Radius bulges in the middle, wobbles with per-step noise for
        // jagged, raw walls, and optionally leaves a stone pillar running down the centre
        // (mega caverns keep a core so the chamber reads as a grand hall with columns).
        private void CarveNode(byte[] blocks, int chunkX, int chunkZ, int chunkSize, int height,
            Random rand, double x, double y, double z, float size, float yaw, float pitch,
            int start, int maxLength, double scale, bool leavePillar)
        {
            double cx = chunkX * 16 + 8;
            double cz = chunkZ * 16 + 8;
            var rng = new Random(rand.Next());
            float wobbleYaw = 0f;
            float wobblePitch = 0f;
            double pillarRadius = leavePillar ? (size * 0.35 + 1.0) : 0.0;
            double wobbleSeed = rng.NextDouble() * Math.PI * 2.0;

            if (maxLength <= 0) maxLength = 80 - rng.Next(30);
            bool branch = false;
            if (start == -1)
            {
                start = maxLength / 2;
                branch = true;
            }
            int branchAt = rng.Next(maxLength / 2) + maxLength / 4;

            for (int len = start; len < maxLength; len++)
            {
                // Radius: sine bulge in the middle + per-step wobble for jagged edges.
                double radius = (1.5 + Math.Sin(len * Math.PI / maxLength) * size) * scale;
                radius *= 0.7 + 0.5 * Math.Sin(len * 0.55 + wobbleSeed);
                if (radius < 1.2) radius = 1.2;
                double vRadius = radius * scale;

                x += Math.Cos(yaw) * Math.Cos(pitch);
                y += Math.Sin(pitch);
                z += Math.Sin(yaw) * Math.Cos(pitch);

                if (rng.Next(6) == 0) pitch *= 0.92f;
                else pitch *= 0.7f;
                pitch += wobblePitch * 0.1f;
                yaw += wobbleYaw * 0.1f;
                wobblePitch *= 0.9f;
                wobbleYaw *= 12f / 16f;
                wobblePitch += (float)((rng.NextDouble() - rng.NextDouble()) * rng.NextDouble() * 2.0);
                wobbleYaw += (float)((rng.NextDouble() - rng.NextDouble()) * rng.NextDouble() * 4.0);

                // Midpoint branching (only for tunnels - the top-level branches handle the rest).
                if (!branch && len == branchAt && size > 1.0f)
                {
                    CarveNode(blocks, chunkX, chunkZ, chunkSize, height, rng, x, y, z,
                        (float)(rng.NextDouble() * 0.5 + 0.5), yaw - (float)Math.PI * 0.5f, pitch / 3f, len, maxLength, 1.0, false);
                    CarveNode(blocks, chunkX, chunkZ, chunkSize, height, rng, x, y, z,
                        (float)(rng.NextDouble() * 0.5 + 0.5), yaw + (float)Math.PI * 0.5f, pitch / 3f, len, maxLength, 1.0, false);
                    return;
                }

                // Stop when the walker wanders far from this chunk's neighbourhood.
                double dx = x - cx;
                double dz = z - cz;
                double remaining = maxLength - len;
                double bound = size + 2.0 + 16.0;
                if (dx * dx + dz * dz - remaining * remaining > bound * bound) return;

                int minX = (int)Math.Floor(x - radius) - chunkX * 16 - 1;
                int maxX = (int)Math.Floor(x + radius) - chunkX * 16 + 1;
                int minY = (int)Math.Floor(y - vRadius) - 1;
                int maxY = (int)Math.Floor(y + vRadius) + 1;
                int minZ = (int)Math.Floor(z - radius) - chunkZ * 16 - 1;
                int maxZ = (int)Math.Floor(z + radius) - chunkZ * 16 + 1;
                if (minX < 0) minX = 0;
                if (maxX > chunkSize) maxX = chunkSize;
                if (minY < 4) minY = 4;                 // never touch the bedrock floor
                if (maxY > height - 1) maxY = height - 1;
                if (minZ < 0) minZ = 0;
                if (maxZ > chunkSize) maxZ = chunkSize;

                for (int lx = minX; lx < maxX; lx++)
                {
                    double ndx = (lx + chunkX * 16 + 0.5 - x) / radius;
                    for (int lz = minZ; lz < maxZ; lz++)
                    {
                        double ndz = (lz + chunkZ * 16 + 0.5 - z) / radius;
                        for (int ly = maxY; ly >= minY; ly--)
                        {
                            double ndy = (ly + 0.5 - y) / vRadius;
                            double dist = ndx * ndx + ndy * ndy + ndz * ndz;
                            if (dist >= 1.0) continue;

                            // Mega caverns leave a stone pillar core (stalactite feel).
                            if (leavePillar)
                            {
                                double px = lx - (int)(x - chunkX * 16);
                                double pz = lz - (int)(z - chunkZ * 16);
                                if (px * px + pz * pz < pillarRadius * pillarRadius) continue;
                            }

                            int idx = (lx * chunkSize + lz) * height + ly;
                            if (blocks[idx] != 0) blocks[idx] = 0;
                        }
                    }
                }
            }
        }
    }
}
