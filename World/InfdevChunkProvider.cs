using System;

namespace CubeApp.World
{
    /// <summary>
    /// Faithful port of Infdev 20100630's ChunkProviderGenerate: the 5x17x5 noise field
    /// (initializeNoiseField) with Infdev's exact generator scales/octave composition,
    /// trilinear-interpolated into a 16x16x128 block column (generateTerrain), then the
    /// replaceBlocks surface pass (grass/dirt/sand/gravel/bedrock). The noise primitive is
    /// simplex (user preference) but the frequency/amplitude composition is Infdev's:
    /// octave i samples at baseScale*2^-i and accumulates noise*2^i, so the LOW-frequency
    /// octaves dominate. startIndex skips the negligible high-frequency tail.
    /// </summary>
    public sealed class InfdevChunkProvider : IChunkProvider
    {
        private readonly int seed;
        // Chunks whose deep zone has already been filled (world -252..-65). The fill is a one-shot:
        // tracking it here (instead of inferring from a block) means a cave tunnel or the player
        // digging through the probe cell can never cause a re-fill loop that flashes and wipes edits.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<ChunkCoordinates, byte> _deepFilled = new();
        // Proposal A (lazy deep fill): when true, GenerateChunk also fills the deep zone (world
        // -252..-65) with stone + caves at generation time. Program sets this when the player is
        // deep underground so NEW chunks are born with their deep terrain - no separate fill pass
        // racing the mesh worker. Idempotent: DeepFillChunk skips already-filled zones.
        public bool AutoDeepFill { get; set; }

        /// <summary>Controllable monolith feature (see MonolithSculptor). The classic Infdev
        /// "glitch" made explicit: tunable frequency/size/height/carve, seed-driven.</summary>
        public MonolithSculptor Monoliths { get; private set; }

        /// <summary>Hidden sky islands (see SkyIslandSculptor): rare floating landmasses far
        /// above the clouds, only discoverable by building up.</summary>
        public SkyIslandSculptor SkyIslands { get; private set; }
        // Infdev's seven octave generators, in the same construction order as the Java:
        // noiseGen1/2 = 16 octaves (terrain body), noiseGen3 = 8 (upper/lower selector),
        // noiseGen4/5 = 4 (replaceBlocks biomes/dirt depth), noiseGen6 = 10 (continent),
        // noiseGen7 = 16 (relief/cliff factor).
        private readonly InfdevOctaves _gen1;
        private readonly InfdevOctaves _gen2;
        private readonly InfdevOctaves _gen3;
        private readonly InfdevOctaves _gen4;
        private readonly InfdevOctaves _gen5;
        private readonly InfdevOctaves _gen6;
        private readonly InfdevOctaves _gen7;

        public InfdevChunkProvider(int seed = 341873128)
        {
            this.seed = seed;
            var rand = new Random(seed);
            // startIndex trims the tiny-amplitude high-frequency octaves (2^0..2^7 = 255 of
            // 65535 total = 0.4% of the signal) for the 16-octave generators.
            _gen1 = new InfdevOctaves(rand, 8, 8);
            _gen2 = new InfdevOctaves(rand, 8, 8);
            _gen3 = new InfdevOctaves(rand, 8, 0);
            _gen4 = new InfdevOctaves(rand, 4, 0);
            _gen5 = new InfdevOctaves(rand, 4, 0);
            _gen6 = new InfdevOctaves(rand, 8, 2);
            _gen7 = new InfdevOctaves(rand, 8, 8);
            Monoliths = new MonolithSculptor(seed);
            SkyIslands = new SkyIslandSculptor(seed);
        }

        public Chunk GenerateChunk(int chunkX, int chunkZ, int chunkSize, int chunkHeight)
        {
            int originX = chunkX * chunkSize;
            int originZ = chunkZ * chunkSize;
            const int originY = ChunkManager.WorldOriginY; // -256; local Y 0..447 = world -256..191
            var chunk = new Chunk(chunkSize, chunkHeight, chunkSize, originX, originY, originZ);

            // Proposal A (tall chunk, lazy deep fill):
            //   - Infdev's terrain band occupies local Y TerrainBandStart..TerrainBandStart+127
            //     (world -64..63). Their world height is 128 with sea at 64, so sea is at
            //     TerrainBandStart+64 == worldY 0 == our sea level.
            //   - Local 0..DeepFloor (world -256..-65) is the DEEP ZONE. For now it is only a
            //     bedrock floor; the air between fills lazily when the player descends (so surface
            //     chunk gen stays cheap, but a surface-dug hole still has a visible bottom).
            const int terrainBandStart = 192; // local Y where the Infdev band begins (world -64)
            const int seaLevelLocalY = terrainBandStart + 64;
            const int deepFloor = 4;          // bedrock floor thickness at the very bottom

            int idBedrock = BlockRegistry.GetId("bedrock");
            int idWater = BlockRegistry.GetId("water");
            int idStone = BlockRegistry.GetId("stone");
            int idGrass = BlockRegistry.GetId("grass");
            int idDirt = BlockRegistry.GetId("dirt");
            int idSand = BlockRegistry.GetId("sand");
            int idGravel = BlockRegistry.GetId("gravel");

            // ---- initializeNoiseField: build the 5 x 17 x 5 density field ----
            // Field x/z are in 4-block units (5 samples cover the chunk's 16 blocks), field y
            // in 8-block units (17 samples cover 128). Exact Infdev scales.
            const int fxCount = 5, fyCount = 17, fzCount = 5;
            const double scaleBase = 684.412;
            double[] field = new double[fxCount * fyCount * fzCount];

            for (int fx = 0; fx < fxCount; fx++)
            {
                double xq = (chunkX * 4 + fx); // x field coord = worldX/4
                for (int fz = 0; fz < fzCount; fz++)
                {
                    double zq = (chunkZ * 4 + fz); // z field coord = worldZ/4
                    int col = (fx * fzCount + fz) * fyCount;

                    // noise6 (continent) scale 1.0, noise7 (relief) scale 100 - both 2D.
                    double n6 = _gen6.Noise2D(xq, zq);
                    double n7 = _gen7.Noise2D(xq * 100.0, zq * 100.0);

                    // Infdev's continent + cliff/plateau shaping (var16/var20 chain).
                    double var16 = (n6 + 256.0) / 512.0;
                    if (var16 > 1.0) var16 = 1.0;
                    double var20 = n7 / 8000.0;
                    if (var20 < 0.0) var20 = -var20;
                    var20 = var20 * 3.0 - 3.0;
                    if (var20 < 0.0)
                    {
                        var20 /= 2.0;
                        if (var20 < -1.0) var20 = -1.0;
                        var20 /= 1.4;
                        var20 /= 2.0;
                        var16 = 0.0;
                    }
                    else
                    {
                        if (var20 > 1.0) var20 = 1.0;
                        var20 /= 6.0;
                    }
                    var16 += 0.5;
                    var20 = var20 * fyCount / 16.0;
                    double var22 = fyCount / 2.0 + var20 * 4.0; // center height in field-y units

                    for (int fy = 0; fy < fyCount; fy++)
                    {
                        int idx = col + fy;
                        double yq = fy; // y field coord = worldY/8

                        // noise1/noise2 (terrain body, scale 684.412), noise3 (upper/lower
                        // selector, scale 684.412/80 in x/z and /160 in y).
                        double var29 = _gen1.Noise3D(xq * scaleBase, yq * scaleBase, zq * scaleBase) / 512.0;
                        double var31 = _gen2.Noise3D(xq * scaleBase, yq * scaleBase, zq * scaleBase) / 512.0;
                        double var33 = (_gen3.Noise3D(xq * (scaleBase / 80.0), yq * (scaleBase / 160.0), zq * (scaleBase / 80.0)) / 10.0 + 1.0) / 2.0;

                        double var25;
                        if (var33 < 0.0) var25 = var29;
                        else if (var33 > 1.0) var25 = var31;
                        else var25 = var29 + (var31 - var29) * var33;

                        // Height falloff: pushes density solid below the surface line, air above.
                        double var27 = ((double)fy - var22) * 12.0 / var16;
                        if (var27 < 0.0) var27 *= 4.0;
                        var25 -= var27;

                        // Top-of-world clamp (force air in the top field rows).
                        if (fy > fyCount - 4)
                        {
                            double var35 = (double)(fy - (fyCount - 4)) / 3.0;
                            var25 = var25 * (1.0 - var35) + -10.0 * var35;
                        }

                        field[idx] = var25;
                    }
                }
            }

            // ---- generateTerrain: trilinear-interpolate the field into blocks ----
            // The Infdev band occupies local Y terrainBandStart..terrainBandStart+127; above it is
            // open sky. Water below the band's sea level.
            for (int ly = 0; ly < 128; ly++)
            {
                int localY = terrainBandStart + ly;
                double gy = ly / 8.0;
                int fy0 = (int)gy;
                int fy1 = fy0 + 1;
                double ty = gy - fy0;
                if (fy0 > 15) { fy0 = 15; fy1 = 16; ty = 1.0; }

                for (int lx = 0; lx < chunkSize; lx++)
                {
                    double gx = lx / 4.0;
                    int fx0 = (int)gx;
                    int fx1 = fx0 + 1;
                    double tx = gx - fx0;
                    if (fx0 > 3) { fx0 = 3; fx1 = 4; tx = 1.0; }

                    for (int lz = 0; lz < chunkSize; lz++)
                    {
                        double gz = lz / 4.0;
                        int fz0 = (int)gz;
                        int fz1 = fz0 + 1;
                        double tz = gz - fz0;
                        if (fz0 > 3) { fz0 = 3; fz1 = 4; tz = 1.0; }

                        double density = Trilinear(field, fx0, fy0, fz0, fx1, fy1, fz1, fxCount, fyCount, fzCount, tx, ty, tz);

                        int block;
                        if (density > 0.0) block = idStone;
                        else if (localY < seaLevelLocalY) block = idWater;
                        else block = 0;
                        chunk[lx, localY, lz] = block;
                    }
                }
            }

            // Deep zone bedrock floor at the very bottom (local 0..deepFloor-1). Everything between
            // the floor and the terrain band (local deepFloor..terrainBandStart-1) stays AIR - the
            // lazy deep fill (see DeepFillChunk) carves real terrain there when the player descends.
            for (int lx = 0; lx < chunkSize; lx++)
            {
                for (int lz = 0; lz < chunkSize; lz++)
                {
                    for (int ly = 0; ly < deepFloor; ly++)
                    {
                        chunk[lx, ly, lz] = idBedrock;
                    }
                }
            }

            // ---- replaceBlocks: surface materials + bedrock ----
            ReplaceBlocks(chunkX, chunkZ, chunk, idBedrock, idWater, idStone, idGrass, idDirt, idSand, idGravel, terrainBandStart);

            // ---- caves and trees ----
            GenerateCaves(chunkX, chunkZ, chunk, terrainBandStart);
            GenerateTrees(chunkX, chunkZ, chunk);

            // ---- monoliths (controllable feature; runs after caves so towers stand on ground) ----
            Monoliths.Sculpt(chunk, terrainBandStart, chunkSize, chunkHeight);

            // ---- hidden sky islands (far above the clouds; only found by building up) ----
            SkyIslands.Sculpt(chunk, terrainBandStart, chunkSize, chunkHeight);

            // When the player is already deep, fill the deep zone at generation time so newly
            // loaded chunks ahead are born with terrain instead of an empty void.
            if (AutoDeepFill)
            {
                DeepFillChunk(chunkX, chunkZ, chunk);
            }

            chunk.NeedsRemesh = true;
            return chunk;
        }

        // Trilinear interpolation over the density field. Field layout is column-major:
        // ((fx * fzCount) + fz) * fyCount + fy (matches Java's ((x*zSize + z)*ySize + y)).
        private static double Trilinear(double[] f, int fx0, int fy0, int fz0, int fx1, int fy1, int fz1,
            int fxCount, int fyCount, int fzCount, double tx, double ty, double tz)
        {
            int zStride = fyCount;
            int xStride = fzCount * fyCount;
            double c000 = f[fx0 * xStride + fz0 * zStride + fy0];
            double c100 = f[fx1 * xStride + fz0 * zStride + fy0];
            double c010 = f[fx0 * xStride + fz1 * zStride + fy0];
            double c110 = f[fx1 * xStride + fz1 * zStride + fy0];
            double c001 = f[fx0 * xStride + fz0 * zStride + fy1];
            double c101 = f[fx1 * xStride + fz0 * zStride + fy1];
            double c011 = f[fx0 * xStride + fz1 * zStride + fy1];
            double c111 = f[fx1 * xStride + fz1 * zStride + fy1];

            double x00 = Lerp(c000, c100, tx);
            double x10 = Lerp(c010, c110, tx);
            double x01 = Lerp(c001, c101, tx);
            double x11 = Lerp(c011, c111, tx);

            double z0 = Lerp(x00, x10, tz);
            double z1 = Lerp(x01, x11, tz);
            return Lerp(z0, z1, ty);
        }

        // Infdev's replaceBlocks: scans each column top-down, replaces the surface stone with
        // grass/dirt (or sand/gravel in their biomes), fills bedrock at the bottom.
        private void ReplaceBlocks(int chunkX, int chunkZ, Chunk chunk,
            int idBedrock, int idWater, int idStone, int idGrass, int idDirt, int idSand, int idGravel,
            int terrainBandStart)
        {
            byte[] blocks = chunk.RawBlocks;
            const int height = ChunkManager.ChunkHeight; // 448
            const int width = 16;
            const int seaLevel = 64; // relative to the terrain band start (world 0)
            var rand = new Random(unchecked(chunkX * 341873128 + chunkZ * 132897987 ^ seed));
            const double inv32 = 1.0 / 32.0;

            for (int x = 0; x < width; x++)
            {
                for (int z = 0; z < width; z++)
                {
                    double wx = chunkX * width + x;
                    double wz = chunkZ * width + z;

                    // Sand biome: gen4 at (x/32, z/32, 0) > 0. Gravel biome: gen4 at a rotated
                    // offset > 3. Dirt depth: gen5 2D octaves /3 + 3.
                    bool sandy = _gen4.Noise3D(wx * inv32, wz * inv32, 0.0) + rand.NextDouble() * 0.2 > 0.0;
                    bool gravelly = _gen4.Noise3D(wz * inv32, 109.0134, wx * inv32) + rand.NextDouble() * 0.2 > 3.0;
                    int dirtDepth = (int)(_gen5.Noise2D(wx * inv32 * 2.0, wz * inv32 * 2.0) / 3.0 + 3.0 + rand.NextDouble() * 0.25);

                    int depthRemaining = -1; // -1 = haven't found the surface yet
                    int topBlock = idGrass;
                    int fillBlock = idDirt;

                    // Scan the terrain band (band-relative 127..0, actual local terrainBandStart..+127).
                    for (int bandLy = 127; bandLy >= 0; bandLy--)
                    {
                        int ly = terrainBandStart + bandLy;
                        int idx = (x * width + z) * height + ly;

                        if (bandLy <= rand.Next(6) - 1)
                        {
                            blocks[idx] = (byte)idBedrock;
                        }
                        else if (blocks[idx] == 0)
                        {
                            depthRemaining = -1;
                        }
                        else if (blocks[idx] == idStone)
                        {
                            if (depthRemaining == -1)
                            {
                                if (dirtDepth <= 0)
                                {
                                    topBlock = 0;
                                    fillBlock = idStone;
                                }
                                else if (bandLy >= seaLevel - 4 && bandLy <= seaLevel + 1)
                                {
                                    topBlock = idGrass;
                                    fillBlock = idDirt;
                                    if (gravelly)
                                    {
                                        topBlock = 0;
                                        fillBlock = idGravel;
                                    }
                                    if (sandy)
                                    {
                                        topBlock = idSand;
                                        fillBlock = idSand;
                                    }
                                }
                                if (bandLy < seaLevel && topBlock == 0) topBlock = idWater;
                                depthRemaining = dirtDepth;
                                blocks[idx] = (byte)(bandLy >= seaLevel - 1 ? topBlock : fillBlock);
                            }
                            else if (depthRemaining > 0)
                            {
                                depthRemaining--;
                                blocks[idx] = (byte)fillBlock;
                            }
                        }
                    }
                }
            }
        }

        // Infdev-style cave generation: a per-chunk deterministic chance of spawning cave walkers
        // that carve winding, branching tubes through the stone.
        private void GenerateCaves(int chunkX, int chunkZ, Chunk chunk, int terrainBandStart)
        {
            byte[] blocks = chunk.RawBlocks;

            // Faithful port of Infdev's generateCaves: iterate a 17x17 region of chunk seeds
            // (var9/var10 from -8..+8) and spawn walkers at the NEIGHBOR chunk's coordinates,
            // carving into THIS chunk's block array. That way a cave that starts in a neighboring
            // chunk crosses the border and continues here - without this, every tube dies at the
            // chunk edge because the walker can only carve the current chunk's blocks.
            var rand = new Random(seed);
            long var5 = rand.Next() * 2L + 1L;
            long var7 = rand.Next() * 2L + 1L;

            for (int var9 = chunkX - 8; var9 <= chunkX + 8; var9++)
            {
                for (int var10 = chunkZ - 8; var10 <= chunkZ + 8; var10++)
                {
                    var rand2 = new Random(unchecked((int)((long)var9 * var5 + (long)var10 * var7 ^ seed)));

                    int numCaves = rand2.Next(rand2.Next(rand2.Next(40) + 1) + 1);
                    if (rand2.Next(15) != 0) numCaves = 0;

                    for (int i = 0; i < numCaves; i++)
                    {
                        // Walker starts in the NEIGHBOR chunk (var9/var10), so its tube can reach
                        // across the border into this chunk.
                        double x = var9 * 16 + rand2.Next(16);
                        // Y is confined to the terrain band (local terrainBandStart..+127).
                        double y = terrainBandStart + rand2.Next(rand2.Next(120) + 8);
                        double z = var10 * 16 + rand2.Next(16);

                        int nodeCount = 1;
                        if (rand2.Next(4) == 0)
                        {
                            GenerateCaveNode(chunk, blocks, chunkX, chunkZ, rand2,
                                x, y, z, (float)(rand2.NextDouble() * 2.0 + rand2.NextDouble()), 0f, 0f, -1, -1, 1.0);
                            nodeCount += rand2.Next(4);
                        }

                        for (int n = 0; n < nodeCount; n++)
                        {
                            float yaw = (float)(rand2.NextDouble() * Math.PI * 2.0);
                            float pitch = (float)((rand2.NextDouble() - 0.5) * 2.0 / 8.0);
                            float size = (float)(rand2.NextDouble() * 2.0 + rand2.NextDouble());

                            // Yours truly: deep caves have a small chance to spawn 5x as fat - the
                            // walker's size drives the tube radius (1.5 + sin(...)*size), so x5 turns
                            // a ~2-4 wide tunnel into a ~20-wide chamber. Only fires in the lower
                            // terrain band (below world Y -4) and only on ~1 in 8 nodes.
                            if (y < terrainBandStart + 60 && rand2.Next(8) == 0)
                            {
                                size *= 5f;
                            }

                            GenerateCaveNode(chunk, blocks, chunkX, chunkZ, rand2, x, y, z, size, yaw, pitch, 0, 0, 1.0);
                        }
                    }
                }
            }
        }

        // One random-walker cave tube: advances in the yaw/pitch direction, carving a round tube
        // whose radius bulges in the middle, wobbling as it goes and branching at the midpoint.
        private void GenerateCaveNode(Chunk chunk, byte[] blocks, int chunkX, int chunkZ, Random rand,
            double x, double y, double z, float size, float yaw, float pitch, int start, int maxLength, double scale)
        {
            double cx = chunkX * 16 + 8;
            double cz = chunkZ * 16 + 8;
            var rng = new Random(rand.Next());
            float wobbleYaw = 0f;
            float wobblePitch = 0f;
            byte idWater = (byte)BlockRegistry.GetId("water");
            byte idGrass = (byte)BlockRegistry.GetId("grass");
            byte idDirt = (byte)BlockRegistry.GetId("dirt");

            if (maxLength <= 0) maxLength = 112 - rng.Next(112 / 4);
            bool branch = false;
            if (start == -1)
            {
                start = maxLength / 2;
                branch = true;
            }
            int branchAt = rng.Next(maxLength / 2) + maxLength / 4;

            const int height = ChunkManager.ChunkHeight; // 256 (local Y 0..255)

            for (int len = start; len < maxLength; len++)
            {
                double radius = 1.5 + Math.Sin(len * Math.PI / maxLength) * size;
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

                if (!branch && len == branchAt && size > 1.0f)
                {
                    GenerateCaveNode(chunk, blocks, chunkX, chunkZ, rng, x, y, z,
                        (float)(rng.NextDouble() * 0.5 + 0.5), yaw - (float)Math.PI * 0.5f, pitch / 3f, len, maxLength, 1.0);
                    GenerateCaveNode(chunk, blocks, chunkX, chunkZ, rng, x, y, z,
                        (float)(rng.NextDouble() * 0.5 + 0.5), yaw + (float)Math.PI * 0.5f, pitch / 3f, len, maxLength, 1.0);
                    return;
                }

                // Stop when the walker leaves the chunk's neighbourhood.
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
                if (maxX > 16) maxX = 16;
                if (minY < 1) minY = 1;
                if (maxY > height - 1) maxY = height - 1;
                if (minZ < 0) minZ = 0;
                if (maxZ > 16) maxZ = 16;

                for (int lx = minX; lx < maxX; lx++)
                {
                    double ndx = (lx + chunkX * 16 + 0.5 - x) / radius;
                    for (int lz = minZ; lz < maxZ; lz++)
                    {
                        double ndz = (lz + chunkZ * 16 + 0.5 - z) / radius;
                        for (int ly = maxY; ly >= minY; ly--)
                        {
                            double ndy = (ly + 0.5 - y) / vRadius;
                            if (ndx * ndx + ndy * ndy + ndz * ndz < 1.0)
                            {
                                int idx = (lx * 16 + lz) * height + ly;
                                byte id = blocks[idx];
                                if (id == idWater) continue; // don't carve water
                                if (id == 0) continue;        // already air
                                blocks[idx] = 0;
                                // If the cave opened through a surface grass block, let the grass
                                // settle one block down so no floating grass is left over a mouth.
                                if (id == idGrass && ly > 1 && blocks[idx - 1] == idDirt)
                                {
                                    blocks[idx - 1] = idGrass;
                                }
                            }
                        }
                    }
                }
            }
        }

        // Infdev-style trees: a few per chunk on grass, each a 4-6 tall trunk with a rounded
        // leaf canopy (top corners cut for the plus shape). Trees that would cross the chunk
        // edge fail their clearance check and don't spawn.
        private void GenerateTrees(int chunkX, int chunkZ, Chunk chunk)
        {
            byte[] blocks = chunk.RawBlocks;
            var rand = new Random(unchecked(chunkX * 341873128 + chunkZ * 132897987 ^ seed) ^ 0x9E3779);
            byte idWood = (byte)BlockRegistry.GetId("log");
            byte idLeaves = (byte)BlockRegistry.GetId("leaves");
            byte idGrass = (byte)BlockRegistry.GetId("grass");
            byte idDirt = (byte)BlockRegistry.GetId("dirt");
            const int height = ChunkManager.ChunkHeight;

            int treeCount = rand.Next(6);
            if (rand.Next(10) == 0) treeCount++;

            for (int t = 0; t < treeCount; t++)
            {
                int lx = rand.Next(16);
                int lz = rand.Next(16);
                int surfaceY = -1;
                for (int y = height - 1; y >= 0; y--)
                {
                    if (blocks[(lx * 16 + lz) * height + y] != 0)
                    {
                        surfaceY = y;
                        break;
                    }
                }
                if (surfaceY <= 0) continue;
                // The trunk starts one block ABOVE the surface block (which must be grass/dirt).
                GenerateTree(blocks, lx, surfaceY + 1, lz, rand, idWood, idLeaves, idGrass, idDirt);
            }
        }

        // One tree rooted with its trunk base at (x, baseY, z) - baseY is the first trunk cell,
        // the ground (grass/dirt) sits at baseY-1. Faithful port of WorldGenTrees.generate.
        private void GenerateTree(byte[] blocks, int x, int baseY, int z, Random rand,
            byte idWood, byte idLeaves, byte idGrass, byte idDirt)
        {
            const int height = ChunkManager.ChunkHeight;
            int trunkHeight = rand.Next(3) + 4;

            // Clearance: the trunk column and canopy footprint must be air or leaves.
            for (int y = baseY; y <= baseY + 1 + trunkHeight && y < height; y++)
            {
                int radius = 1;
                if (y == baseY) radius = 0;
                if (y >= baseY + 1 + trunkHeight - 2) radius = 2;
                for (int dx = -radius; dx <= radius; dx++)
                {
                    for (int dz = -radius; dz <= radius; dz++)
                    {
                        int lx = x + dx;
                        int lz = z + dz;
                        if (lx < 0 || lx >= 16 || lz < 0 || lz >= 16 || y < 0 || y >= height) return;
                        byte b = blocks[(lx * 16 + lz) * height + y];
                        if (b != 0 && b != idLeaves) return;
                    }
                }
            }

            // The ground must be grass or dirt.
            if (baseY < 1) return;
            byte ground = blocks[(x * 16 + z) * height + (baseY - 1)];
            if (ground != idGrass && ground != idDirt) return;

            // Shade the ground under the tree with dirt.
            blocks[(x * 16 + z) * height + (baseY - 1)] = idDirt;

            // Canopy: radius grows downward; the top row corners are always cut (plus shape),
            // lower-row corners are randomly trimmed.
            for (int y = baseY - 3 + trunkHeight; y <= baseY + trunkHeight && y < height; y++)
            {
                if (y < 0) continue;
                int dy = y - (baseY + trunkHeight);
                int radius = 1 - dy / 2;
                for (int dx = -radius; dx <= radius; dx++)
                {
                    for (int dz = -radius; dz <= radius; dz++)
                    {
                        int ax = Math.Abs(dx);
                        int az = Math.Abs(dz);
                        if (ax != radius || az != radius || (rand.Next(2) != 0 && dy != 0))
                        {
                            int lx = x + dx;
                            int lz = z + dz;
                            if (lx < 0 || lx >= 16 || lz < 0 || lz >= 16) continue;
                            int idx = (lx * 16 + lz) * height + y;
                            byte b = blocks[idx];
                            if (b == 0 || b == idLeaves) blocks[idx] = idLeaves;
                        }
                    }
                }
            }

            // Trunk: only replace air or leaves so the trunk doesn't punch through terrain.
            for (int i = 0; i < trunkHeight; i++)
            {
                int y = baseY + i;
                if (y < 0 || y >= height) break;
                int idx = (x * 16 + z) * height + y;
                byte b = blocks[idx];
                if (b == 0 || b == idLeaves) blocks[idx] = idWood;
            }
        }

        // Lazy deep-fill (Proposal A): fills the empty deep zone (local deepFloor..terrainBandStart-1,
        // world -256..-65) with solid stone plus a few large random caves. Called only when the
        // player descends near this chunk, so surface chunk gen stays cheap. One-shot: the chunk's
        // coordinate is recorded in _deepFilled, so a cave or the player digging through the probe
        // cell can never cause a re-fill loop.
        public void DeepFillChunk(int chunkX, int chunkZ, Chunk chunk)
        {
            var key = new ChunkCoordinates(chunkX, chunkZ);
            if (!_deepFilled.TryAdd(key, 0))
            {
                return; // already filled once
            }

            const int terrainBandStart = 192;
            const int deepFloor = 4;
            int idStone = BlockRegistry.GetId("stone");
            byte[] blocks = chunk.RawBlocks;
            const int height = ChunkManager.ChunkHeight;
            const int width = 16;

            for (int x = 0; x < width; x++)
            {
                for (int z = 0; z < width; z++)
                {
                    for (int y = deepFloor; y < terrainBandStart; y++)
                    {
                        blocks[(x * width + z) * height + y] = (byte)idStone;
                    }
                }
            }

            // Carve a few large caves into the deep zone so it isn't boring solid rock.
            var rand = new Random(unchecked(chunkX * 341873128 + chunkZ * 132897987 ^ seed));
            int caveCount = 2 + rand.Next(4);
            for (int i = 0; i < caveCount; i++)
            {
                double x = chunkX * 16 + rand.Next(16);
                double y = deepFloor + rand.Next(terrainBandStart - deepFloor);
                double z = chunkZ * 16 + rand.Next(16);
                float yaw = (float)(rand.NextDouble() * Math.PI * 2.0);
                float pitch = (float)((rand.NextDouble() - 0.5) * 2.0 / 8.0);
                float size = 2f + (float)(rand.NextDouble() * 4.0);
                GenerateCaveNode(chunk, blocks, chunkX, chunkZ, rand, x, y, z, size, yaw, pitch, -1, 0, 0.9);
            }

            chunk.NeedsRemesh = true;
        }

        public string BiomeNameAt(int worldX, int worldZ)
        {
            // Same continent/relief sampling as the terrain; classify by expected land height.
            double xq = worldX / 4.0;
            double zq = worldZ / 4.0;
            double n6 = _gen6.Noise2D(xq, zq);
            double n7 = _gen7.Noise2D(xq * 100.0, zq * 100.0);

            double var16 = (n6 + 256.0) / 512.0;
            if (var16 > 1.0) var16 = 1.0;
            double var20 = n7 / 8000.0;
            if (var20 < 0.0) var20 = -var20;
            var20 = var20 * 3.0 - 3.0;
            if (var20 < 0.0)
            {
                var20 /= 2.0;
                if (var20 < -1.0) var20 = -1.0;
                var20 /= 1.4;
                var20 /= 2.0;
                var16 = 0.0;
            }
            else
            {
                if (var20 > 1.0) var20 = 1.0;
                var20 /= 6.0;
            }
            var16 += 0.5;
            double centerY = (17.0 / 2.0 + (var20 * 17.0 / 16.0) * 4.0) * 8.0; // block Y of the surface line

            if (centerY < 56) return "Ocean";
            if (var20 > 0.12) return "Mountains";
            if (var20 > 0.0) return "Hills";
            return "Plains";
        }

        private static double Lerp(double a, double b, double t)
        {
            return a + (b - a) * t;
        }
    }
}
