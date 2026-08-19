using System;

namespace Cubuild.World
{
    /// <summary>
    /// The ALPHA biome generator: a byte-faithful port of Minecraft's "old terrain generator"
    /// (Infdev 20100415 -> Alpha v1.1.2_01, the last release to use it). Replaces the engine's
    /// density field with the real 684.412 field: two 16-octave body noises blended by an
    /// 8-octave selector, the y*4-64 height ramp (x3 below 0), a ±10 density clamp, plain
    /// trilinear interpolation at 4-block resolution (the chunky alpha look), the surface
    /// pass (grass only near sea level, sand/gravel dice, noise-driven dirt depth), alpha's
    /// ore distribution (coal x20 / iron x10 / gold x1/2 / diamond x1/8 per chunk), and the
    /// WorldGenBigTree forest whose density comes from the 5-octave mobSpawnerNoise.
    ///
    /// Alpha world y == our local Y (alpha y=0 sits at world -64, alpha sea 64 at world 0),
    /// so the entire 128-tall alpha world maps perfectly onto the bottom of our terrain band.
    /// </summary>
    public sealed class AlphaBiomeGenerator
    {
        private const int AlphaBandBlocks = 128;   // alpha y 0..127 == our local Y 0..127
        private const int SeaLevel = 64;           // alpha sea level == our local Y 64
        private const double BodyDivisor = 512.0;  // the famous /512.0
        private const double SelectorDivisor = 2.0;

        private readonly AlphaTerrainParams _p;
        private readonly JavaOctaves _low, _high, _selector, _sandGravel, _dirtDepth, _trees;
        private readonly JavaRandom _rand = new JavaRandom(0); // re-seeded per chunk

        public AlphaBiomeGenerator(long worldSeed, AlphaTerrainParams p)
        {
            _p = p;
            // Faithful init order from the 20100415 decompile: 16/16/8/4/4, an anonymous
            // 5-octave stack is CREATED AND DISCARDED (consuming its draws), then 5 more for
            // mobSpawnerNoise. Every JavaPerlin consumes 3 nextDouble draws.
            var r = new JavaRandom(worldSeed);
            _low = new JavaOctaves(r, p.LowNoiseOctaves);
            _high = new JavaOctaves(r, p.HighNoiseOctaves);
            _selector = new JavaOctaves(r, p.SelectorNoiseOctaves);
            _sandGravel = new JavaOctaves(r, p.SandGravelNoiseOctaves);
            _dirtDepth = new JavaOctaves(r, p.DirtDepthNoiseOctaves);
            _ = new JavaOctaves(r, 5); // discarded, but its draws keep the stream aligned
            _trees = new JavaOctaves(r, p.TreeNoiseOctaves);
        }

        // ---- the 2010 density field sample (initializeNoiseField) ----
        // fx/fz are field coords in 4-block units, fy is the field Y sample (0..32).
        private double FieldSample(double fx, double fy, double fz)
        {
            double ramp = fy * _p.HeightStretch - 64.0;
            if (ramp < 0.0) ramp *= 3.0;

            double sel = _selector.Noise3D(
                fx * _p.MainNoiseScaleXZ, fy * _p.MainNoiseScaleY, fz * _p.MainNoiseScaleXZ) / SelectorDivisor;

            double low = _low.Noise3D(fx * _p.CoordinateScale, fy * _p.HeightScale, fz * _p.CoordinateScale) / BodyDivisor - ramp;
            double high = _high.Noise3D(fx * _p.CoordinateScale, fy * _p.HeightScale, fz * _p.CoordinateScale) / BodyDivisor - ramp;

            if (sel < -1.0) return Clamp(low, -10.0, 10.0);
            if (sel > 1.0) return Clamp(high, -10.0, 10.0);
            double a = Clamp(low, -10.0, 10.0);
            double b = Clamp(high, -10.0, 10.0);
            return a + (b - a) * ((sel + 1.0) / 2.0);
        }

        private static double Clamp(double v, double lo, double hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        // ---- chunk fill: 4x4 cells of alpha terrain overwrite the engine's fill ----
        // Border cells do NOT switch hard: each field node blends alpha density with the
        // engine's own density by a smoothstep weight derived from the 4 cells around the
        // node (neighbor-aware across chunk edges). Fully-alpha nodes stay byte-faithful;
        // the ramp kills the cliff where alpha meets other biomes.
        public void FillOverride(Chunk chunk, int chunkX, int chunkZ, bool[,] alphaCell,
            Func<int, int, string> biomeIdAt, double[] engineField, int idStone, int idWater)
        {
            byte[] blocks = chunk.RawBlocks;
            const int height = ChunkManager.ChunkHeight;

            // Neighbor-aware 6x6 cell mask (chunk cells -1..4): border weights ramp
            // smoothly across chunk boundaries instead of snapping at them.
            var cellMask = new bool[6, 6];
            for (int gx = 0; gx < 6; gx++)
                for (int gz = 0; gz < 6; gz++)
                    cellMask[gx, gz] = string.Equals(
                        biomeIdAt(chunkX * 4 + gx - 1, chunkZ * 4 + gz - 1), "alpha", StringComparison.OrdinalIgnoreCase);

            // Smooth blend weight of a field node: alpha share of its 4 surrounding cells,
            // S-curved so the transition is flat at both ends (alpha inside, engine outside).
            double NodeWeight(int fx, int fz)
            {
                int a = (cellMask[fx, fz] ? 1 : 0) + (cellMask[fx + 1, fz] ? 1 : 0)
                      + (cellMask[fx, fz + 1] ? 1 : 0) + (cellMask[fx + 1, fz + 1] ? 1 : 0);
                double s = a / 4.0;
                return s * s * (3.0 - 2.0 * s);
            }

            // The engine's density at an alpha node: its field y rows are 8 blocks apart,
            // alpha's are 4, so sample halfway (alpha fy -> engine fy/2).
            double EngineAt(int fx, int fy, int fz)
            {
                double yp = fy / 2.0;
                int fy0 = (int)yp;
                if (fy0 > 31) fy0 = 31;
                double t = yp - fy0;
                int baseIdx = (fx * 5 + fz) * 33 + fy0;
                return engineField[baseIdx] + (engineField[baseIdx + 1] - engineField[baseIdx]) * t;
            }

            for (int fx = 0; fx < 4; fx++)
            {
                for (int fz = 0; fz < 4; fz++)
                {
                    if (!alphaCell[fx, fz]) continue;

                    // The 4 corner field nodes of this cell (node coords 0..4 in the chunk).
                    int[,] node = { { fx, fz }, { fx, fz + 1 }, { fx + 1, fz }, { fx + 1, fz + 1 } };
                    var c = new double[4, 33];
                    var cw = new double[4];
                    bool fullAlpha = true;
                    for (int n = 0; n < 4; n++)
                    {
                        int nfx = node[n, 0], nfz = node[n, 1];
                        double wn = NodeWeight(nfx, nfz);
                        cw[n] = wn;
                        if (wn < 1.0) fullAlpha = false;
                        for (int y = 0; y < 33; y++)
                        {
                            double alpha = FieldSample(chunkX * 4 + nfx, y, chunkZ * 4 + nfz);
                            double eng = EngineAt(nfx, y, nfz);
                            c[n, y] = eng + (alpha - eng) * wn;
                        }
                    }

                    int ox = fx * 4, oz = fz * 4;

                    // Only a fully-alpha cell tops out at 128: everything above its band is
                    // air. Blended cells keep the engine's terrain above 128 untouched.
                    if (fullAlpha)
                    {
                        for (int x = ox; x < ox + 4; x++)
                            for (int z = oz; z < oz + 4; z++)
                                for (int ly = AlphaBandBlocks; ly < TerrainChunkProvider.TerrainBandBlocks; ly++)
                                    blocks[(x * 16 + z) * height + ly] = 0;
                    }

                    // Plain trilinear over the 4x4x4 sub-cells, exactly like the original.
                    for (int seg = 0; seg < 32; seg++)
                    {
                        double v00 = c[0, seg], v01 = c[1, seg], v10 = c[2, seg], v11 = c[3, seg];
                        double w00 = c[0, seg + 1], w01 = c[1, seg + 1], w10 = c[2, seg + 1], w11 = c[3, seg + 1];

                        for (int dy = 0; dy < 4; dy++)
                        {
                            double t = dy / 4.0;
                            double i00 = v00 + (w00 - v00) * t;
                            double i01 = v01 + (w01 - v01) * t;
                            double i10 = v10 + (w10 - v10) * t;
                            double i11 = v11 + (w11 - v11) * t;

                            for (int dx = 0; dx < 4; dx++)
                            {
                                double u = dx / 4.0;
                                double ix0 = i00 + (i10 - i00) * u;
                                double ix1 = i01 + (i11 - i01) * u;

                                for (int dz = 0; dz < 4; dz++)
                                {
                                    double s = dz / 4.0;
                                    double density = ix0 + (ix1 - ix0) * s;

                                    int ly = seg * 4 + dy;
                                    int id = 0;
                                    if (ly < SeaLevel) id = idWater;
                                    if (density > 0.0) id = idStone;
                                    blocks[((ox + dx) * 16 + (oz + dz)) * height + ly] = (byte)id;
                                }
                            }
                        }
                    }
                }
            }
        }

        // ---- the 2010 surface pass ----
        // Every column rolls the three random dice (faithful draw order), but only alpha
        // columns are re-dressed: grass/dirt only where the surface sits between alpha y 60
        // and 65, sand/gravel from the noise4 dice, dirt depth from noise5, water below 64.
        public void SurfacePass(Chunk chunk, int chunkX, int chunkZ,
            Func<int, int, bool> isAlphaColumn, int idStone, int idGrass, int idDirt,
            int idSand, int idGravel, int idWater)
        {
            byte[] blocks = chunk.RawBlocks;
            const int height = ChunkManager.ChunkHeight;

            _rand.SetSeed((long)chunkX * 341873128712L + (long)chunkZ * 132897987541L);

            for (int x = 0; x < 16; x++)
            {
                for (int z = 0; z < 16; z++)
                {
                    double wx = chunkX * 16 + x;
                    double wz = chunkZ * 16 + z;

                    // Faithful dice for EVERY column (the original never skipped any).
                    bool sand = _sandGravel.Noise3D(wx / 32.0, wz / 32.0, 0.0) + _rand.NextDouble() * 0.2 > 0.0;
                    bool gravel = _sandGravel.Noise3D(wz / 32.0, 109.0134, wx / 32.0) + _rand.NextDouble() * 0.2 > 3.0;
                    int depth = (int)(_dirtDepth.Noise2D(wx / 16.0, wz / 16.0) / 3.0 + 3.0 + _rand.NextDouble() * 0.25);

                    if (!isAlphaColumn((int)wx, (int)wz)) continue;

                    int topBlock = idGrass;
                    int fillBlock = idDirt;
                    int remaining = -1;

                    for (int ly = AlphaBandBlocks - 1; ly >= 0; ly--)
                    {
                        int idx = (x * 16 + z) * height + ly;
                        byte id = blocks[idx];

                        if (id == 0)
                        {
                            remaining = -1;
                        }
                        else if (id == idStone)
                        {
                            if (remaining == -1)
                            {
                                if (depth <= 0)
                                {
                                    topBlock = 0;
                                    fillBlock = idStone;
                                }
                                else if (ly >= 60 && ly <= 65)
                                {
                                    topBlock = idGrass;
                                    fillBlock = idDirt;
                                    if (gravel) { topBlock = 0; fillBlock = idGravel; }
                                    if (sand) { topBlock = idSand; fillBlock = idSand; }
                                }
                                if (ly < SeaLevel && topBlock == 0) topBlock = idWater;
                                remaining = depth;
                                blocks[idx] = (byte)(ly >= 63 ? topBlock : fillBlock);
                            }
                            else if (remaining > 0)
                            {
                                remaining--;
                                blocks[idx] = (byte)fillBlock;
                            }
                        }
                    }
                }
            }
        }

        // ---- alpha ores ----
        // The 2010 distribution per chunk: coal x20 (any y<128), iron x10 (y<64), gold 50%
        // (y<32), diamond 12.5% (y<16); every blob is the original sin-bulge ellipsoid
        // (WorldGenMinable), replacing only stone. Draws stay faithful to the original
        // sequence; placement is limited to alpha cells (border blobs clip at biome edges).
        public void GenerateOres(Chunk chunk, int chunkX, int chunkZ, bool[,] alphaCell,
            int idStone, int idCoal, int idIron, int idGold, int idDiamond)
        {
            byte[] blocks = chunk.RawBlocks;
            _rand.SetSeed((long)chunkX * 318279123L + (long)chunkZ * 919871212L);

            for (int i = 0; i < 20; i++)
                Minable(blocks, chunkX, chunkZ, alphaCell, idStone, _rand.NextInt(16), _rand.NextInt(128), _rand.NextInt(16), idCoal);
            for (int i = 0; i < 10; i++)
                Minable(blocks, chunkX, chunkZ, alphaCell, idStone, _rand.NextInt(16), _rand.NextInt(64), _rand.NextInt(16), idIron);
            if (_rand.NextInt(2) == 0)
                Minable(blocks, chunkX, chunkZ, alphaCell, idStone, _rand.NextInt(16), _rand.NextInt(32), _rand.NextInt(16), idGold);
            if (_rand.NextInt(8) == 0)
                Minable(blocks, chunkX, chunkZ, alphaCell, idStone, _rand.NextInt(16), _rand.NextInt(16), _rand.NextInt(16), idDiamond);
        }

        private void Minable(byte[] blocks, int chunkX, int chunkZ, bool[,] alphaCell,
            int idStone, int lx, int ly, int lz, int oreId)
        {
            const int height = ChunkManager.ChunkHeight;

            double angle = _rand.NextFloat() * Math.PI;
            double x0 = lx + 8 + Math.Sin(angle) * 2.0;
            double x1 = lx + 8 - Math.Sin(angle) * 2.0;
            double z0 = lz + 8 + Math.Cos(angle) * 2.0;
            double z1 = lz + 8 - Math.Cos(angle) * 2.0;
            double y0 = ly + _rand.NextInt(3) + 2;
            double y1 = ly + _rand.NextInt(3) + 2;

            for (int i = 0; i <= 16; i++)
            {
                double cx = x0 + (x1 - x0) * i / 16.0;
                double cy = y0 + (y1 - y0) * i / 16.0;
                double cz = z0 + (z1 - z0) * i / 16.0;
                double r = _rand.NextDouble();
                double rx = (Math.Sin(i / 16.0 * Math.PI) + 1.0) * r + 1.0;
                double ry = (Math.Sin(i / 16.0 * Math.PI) + 1.0) * r + 1.0;

                for (int bx = (int)(cx - rx / 2.0); bx <= (int)(cx + rx / 2.0); bx++)
                {
                    for (int by = (int)(cy - ry / 2.0); by <= (int)(cy + ry / 2.0); by++)
                    {
                        for (int bz = (int)(cz - rx / 2.0); bz <= (int)(cz + rx / 2.0); bz++)
                        {
                            if (bx < 0 || bx >= 16 || bz < 0 || bz >= 16 || by < 0 || by >= AlphaBandBlocks) continue;
                            if (!alphaCell[bx >> 2, bz >> 2]) continue;

                            double dx = (bx + 0.5 - cx) / (rx / 2.0);
                            double dy = (by + 0.5 - cy) / (ry / 2.0);
                            double dz = (bz + 0.5 - cz) / (rx / 2.0);
                            if (dx * dx + dy * dy + dz * dz < 1.0)
                            {
                                int idx = (bx * 16 + bz) * height + by;
                                if (blocks[idx] == idStone) blocks[idx] = (byte)oreId;
                            }
                        }
                    }
                }
            }
        }

        // ---- alpha forests ----
        // Tree count per chunk: (int)mobSpawnerNoise(x*0.25, z*0.25) << 3 - a dense, loud
        // forest wherever the noise says so. All trees are WorldGenBigTree (the 0.618 golden
        // tree: trunk, branch rings, leaf blobs), seeded from the outer rand exactly like the
        // original. Only alpha columns grow them.
        public void GenerateTrees(Chunk chunk, int chunkX, int chunkZ,
            Func<int, int, bool> isAlphaColumn, int idWood, int idLeaves, int idGrass, int idDirt)
        {
            byte[] blocks = chunk.RawBlocks;
            _rand.SetSeed((long)chunkX * 318279123L + (long)chunkZ * 919871212L);

            int count = (int)_trees.Noise2D(chunkX * 4.0, chunkZ * 4.0) << 3; // x*0.25 == (chunkX*16)*0.25
            if (count <= 0) return;

            var tree = new BigTree(blocks, idWood, idLeaves, idGrass, idDirt);
            for (int i = 0; i < count; i++)
            {
                int lx = _rand.NextInt(16) + 8; // 8..23 like the original
                int lz = _rand.NextInt(16) + 8;
                if (lx > 15 || lz > 15) continue;              // border tree: the neighbour owns it
                if (!isAlphaColumn(chunkX * 16 + lx, chunkZ * 16 + lz)) continue;

                int surfaceY = -1;
                for (int y = AlphaBandBlocks - 1; y >= 0; y--)
                {
                    if (blocks[(lx * 16 + lz) * ChunkManager.ChunkHeight + y] != 0)
                    {
                        surfaceY = y;
                        break;
                    }
                }
                if (surfaceY <= 0) continue;

                // The original roots AT the topmost block: the trunk replaces the grass
                // block itself (base - 1 must be grass/dirt, checked inside Generate).
                tree.Generate(lx, surfaceY, lz, _rand);
            }
        }

        /// <summary>
        /// Byte-faithful port of the 2010 WorldGenBigTree: trunk of golden-ratio height,
        /// branch rings radiating from the upper trunk (0.618 attenuation), round leaf blobs
        /// at each branch node, and log branch lines down to the trunk base. The instance
        /// keeps heightLimit between trees within a chunk (an original quirk: every tree in
        /// a chunk shares the first tree's height).
        /// </summary>
        private sealed class BigTree
        {
            private static readonly byte[] CoordPairs = { 2, 0, 0, 1, 2, 1 };

            private readonly byte[] _blocks;
            private readonly byte _wood, _leaves, _grass, _dirt;
            private readonly JavaRandom _rand = new JavaRandom(0);
            private readonly int[] _base = new int[3];
            private int _heightLimit;
            private int _height;
            private const double HeightAttenuation = 0.618D;
            private const double BranchSlope = 0.381D;
            private const int LeafDistanceLimit = 5;  // setScale(1,1,1) sets this to 5
            private const int HeightLimitLimit = 12;

            public BigTree(byte[] blocks, int wood, int leaves, int grass, int dirt)
            {
                _blocks = blocks;
                _wood = (byte)wood;
                _leaves = (byte)leaves;
                _grass = (byte)grass;
                _dirt = (byte)dirt;
            }

            private int Get(int x, int y, int z)
            {
                if (x < 0 || x >= 16 || z < 0 || z >= 16 || y < 0 || y >= AlphaBandBlocks) return 0; // air
                return _blocks[(x * 16 + z) * ChunkManager.ChunkHeight + y];
            }

            private void Set(int x, int y, int z, int id)
            {
                if (x < 0 || x >= 16 || z < 0 || z >= 16 || y < 0 || y >= AlphaBandBlocks) return;
                _blocks[(x * 16 + z) * ChunkManager.ChunkHeight + y] = (byte)id;
            }

            // Walks from a toward b along the dominant axis; returns -1 when the whole line
            // is air/leaves, otherwise the distance at which a solid block stopped the walk.
            private int CheckBlockLine(int[] a, int[] b)
            {
                int[] d = { b[0] - a[0], b[1] - a[1], b[2] - a[2] };
                int dominant = 0;
                for (int i = 1; i < 3; i++)
                    if (Math.Abs(d[i]) > Math.Abs(d[dominant])) dominant = i;

                if (d[dominant] == 0) return -1;
                int pairA = CoordPairs[dominant];
                int pairB = CoordPairs[dominant + 3];
                int step = d[dominant] > 0 ? 1 : -1;
                double slopeA = (double)d[pairA] / (double)d[dominant];
                double slopeB = (double)d[pairB] / (double)d[dominant];
                int[] p = new int[3];
                int walk = 0;

                for (int end = d[dominant] + step; walk != end; walk += step)
                {
                    p[dominant] = a[dominant] + walk;
                    p[pairA] = (int)((double)a[pairA] + (double)walk * slopeA);
                    p[pairB] = (int)((double)a[pairB] + (double)walk * slopeB);
                    int id = Get(p[0], p[1], p[2]);
                    if (id != 0 && id != _leaves) break;
                }
                return walk == d[dominant] + step ? -1 : Math.Abs(walk);
            }

            // Unconditionally lays log blocks along the line a->b (dominant-axis walk).
            private void PlaceBlockLine(int[] a, int[] b)
            {
                int[] d = { b[0] - a[0], b[1] - a[1], b[2] - a[2] };
                int dominant = 0;
                for (int i = 1; i < 3; i++)
                    if (Math.Abs(d[i]) > Math.Abs(d[dominant])) dominant = i;

                if (d[dominant] == 0) return;
                int pairA = CoordPairs[dominant];
                int pairB = CoordPairs[dominant + 3];
                int step = d[dominant] > 0 ? 1 : -1;
                double slopeA = (double)d[pairA] / (double)d[dominant];
                double slopeB = (double)d[pairB] / (double)d[dominant];
                int[] p = new int[3];
                int walk = 0;

                for (int end = d[dominant] + step; walk != end; walk += step)
                {
                    p[dominant] = (int)Math.Floor((double)(a[dominant] + walk) + 0.5);
                    p[pairA] = (int)Math.Floor((double)a[pairA] + (double)walk * slopeA + 0.5);
                    p[pairB] = (int)Math.Floor((double)a[pairB] + (double)walk * slopeB + 0.5);
                    Set(p[0], p[1], p[2], _wood);
                }
            }

            public void Generate(int x, int y, int z, JavaRandom outer)
            {
                _rand.SetSeed(outer.NextLong());
                _base[0] = x; _base[1] = y; _base[2] = z;
                if (_heightLimit == 0) _heightLimit = 5 + _rand.NextInt(HeightLimitLimit);

                // Clearance: the trunk column must be clear; grass/dirt must sit under the base.
                int[] trunkBase = { x, y, z };
                int[] trunkTop = { x, y + _heightLimit - 1, z };
                int ground = Get(x, y - 1, z);
                bool ok;
                if (ground == _grass || ground == _dirt)
                {
                    int dist = CheckBlockLine(trunkBase, trunkTop);
                    if (dist == -1) ok = true;
                    else if (dist < 6) ok = false;
                    else { _heightLimit = dist; ok = true; }
                }
                else ok = false;
                if (!ok) return;

                _height = (int)(_heightLimit * HeightAttenuation);
                if (_height >= _heightLimit) _height = _heightLimit - 1;

                int ringCount = (int)(1.382 + Math.Pow(1.0 * _heightLimit / 13.0, 2.0));
                if (ringCount <= 0) ringCount = 1;
                int[,] nodes = new int[ringCount * _heightLimit, 4];

                int nodeY = y + _heightLimit - LeafDistanceLimit;
                int topY = y + _height;
                int dy = nodeY - y;
                int nodeCount = 1;
                nodes[0, 0] = x; nodes[0, 1] = nodeY; nodes[0, 2] = z; nodes[0, 3] = topY;
                nodeY--;

                // Branch rings: walk down from the leaf layer, laying out leaf nodes.
                while (dy >= 0)
                {
                    float ringRadius;
                    if ((double)dy < _heightLimit * 0.3D)
                    {
                        ringRadius = -1.618F;
                    }
                    else
                    {
                        float half = _heightLimit / 2.0F;
                        float off = _heightLimit / 2.0F - dy;
                        float r;
                        if (off == 0.0F) r = half;
                        else if (Math.Abs(off) >= half) r = 0.0F;
                        else r = (float)Math.Sqrt(Math.Pow((double)Math.Abs(half), 2.0D) - Math.Pow((double)Math.Abs(off), 2.0D));
                        r *= 0.5F;
                        ringRadius = r;
                    }

                    if (ringRadius < 0.0F)
                    {
                        nodeY--;
                        dy--;
                    }
                    else
                    {
                        for (int ring = 0; ring < ringCount; ring++)
                        {
                            double w = 1.0 * ringRadius * (_rand.NextFloat() + 0.328D);
                            double angle = _rand.NextFloat() * 2.0D * Math.PI;
                            int lx = (int)(w * Math.Sin(angle) + x + 0.5D);
                            int lz = (int)(w * Math.Cos(angle) + z + 0.5D);
                            int[] node = { lx, nodeY, lz };
                            int[] above = { lx, nodeY + LeafDistanceLimit, lz };

                            if (CheckBlockLine(node, above) == -1)
                            {
                                int[] branchBase = { x, y, z };
                                double dist = Math.Sqrt(
                                    Math.Pow((double)Math.Abs(x - lx), 2.0D) + Math.Pow((double)Math.Abs(z - lz), 2.0D));
                                double drop = dist * BranchSlope;
                                if ((double)nodeY - drop > (double)topY) branchBase[1] = topY;
                                else branchBase[1] = (int)((double)nodeY - drop);

                                if (CheckBlockLine(branchBase, node) == -1)
                                {
                                    nodes[nodeCount, 0] = lx;
                                    nodes[nodeCount, 1] = nodeY;
                                    nodes[nodeCount, 2] = lz;
                                    nodes[nodeCount, 3] = branchBase[1];
                                    nodeCount++;
                                }
                            }
                        }
                        nodeY--;
                        dy--;
                    }
                }

                // Leaf blobs: 5 rows up from each node, radius 3 (2 at the ends), rounded.
                for (int n = 0; n < nodeCount; n++)
                {
                    int nx = nodes[n, 0];
                    int ny = nodes[n, 1];
                    int nz = nodes[n, 2];
                    for (int ly = ny; ly < ny + LeafDistanceLimit; ly++)
                    {
                        int row = ly - ny;
                        float radius = (row == 0 || row == LeafDistanceLimit - 1) ? 2.0F : 3.0F;
                        int span = (int)(radius + 0.618D);
                        for (int dx = -span; dx <= span; dx++)
                        {
                            for (int dz = -span; dz <= span; dz++)
                            {
                                double dist = Math.Sqrt(Math.Pow((double)Math.Abs(dx) + 0.5D, 2.0D) + Math.Pow((double)Math.Abs(dz) + 0.5D, 2.0D));
                                if (dist <= radius)
                                {
                                    int id = Get(nx + dx, ly, nz + dz);
                                    if (id == 0 || id == _leaves) Set(nx + dx, ly, nz + dz, _leaves);
                                }
                            }
                        }
                    }
                }

                // Trunk.
                int[] t0 = { x, y, z };
                int[] t1 = { x, y + _height, z };
                PlaceBlockLine(t0, t1);

                // Branch lines from the leaf nodes down to their branch bases (trunkSize == 1).
                int[] fromBase = { x, y, z };
                for (int n = 0; n < nodeCount; n++)
                {
                    int[] node = { nodes[n, 0], nodes[n, 1], nodes[n, 2] };
                    fromBase[1] = nodes[n, 3];
                    if (fromBase[1] - y >= _heightLimit * 0.2D)
                    {
                        PlaceBlockLine(fromBase, node);
                    }
                }
            }
        }
    }
}