using System;

namespace Cubuild.World
{
    /// <summary>
    /// Builds the surface terrain for a ground-layer chunk. A low-resolution density field (5x17x5
    /// samples covering a 16x16x128 block column) is filled with layered simplex noise, then
    /// trilinearly interpolated into blocks. A continent signal sets the base elevation and a
    /// relief signal shapes hills/mountains/valleys; a body-noise pair is blended by a vertical
    /// selector to give the strata organic variation. A surface pass replaces the top materials
    /// (grass/dirt/sand/gravel/bedrock), then caves, trees and ore features are carved in.
    /// </summary>
    public sealed class TerrainChunkProvider : IChunkProvider
    {
        // Frequency of the relief noise. Higher = tighter-packed hills/mountains (smaller, more
        // broken-up regions); lower = broad, sweeping mountain ranges.
        private const double ReliefFrequency = 130.0;
        // Amplified block-level 3D carving: a mid-frequency 3D density field sampled AT BLOCK
        // resolution near the surface line of amplified biomes (the 8-block field grid would
        // wash features out in interpolation, so this runs in the block fill loop instead).
        // Tuned for the SHATTERED HIGHLANDS look: tight frequency (features ~3-5 blocks),
        // heavy Y-stretch (tall narrow rock columns -> spires and near-vertical cliffs) and
        // savage strength (~10 blocks of flip range) so surfaces erode into ragged, broken
        // shards instead of smooth slopes. Non-amplified columns are untouched.
        private const double CarveFrequency = 0.32;
        private const double CarveStrength = 34.0;
        private const double CarveYStretch = 0.15; // vertical wavelength = 1/(freq*stretch)
        private readonly int seed;
        private readonly BiomeMap _biomeMap;
        private readonly NoiseOctaves _amplifiedCarve;

        // Byte-faithful 2010 old-terrain-generator port (the Alpha biome). Lazy: the biome
        // registry must be loaded first so custom knobs from biomes.json can be applied.
        private AlphaBiomeGenerator? _alphaGen;
        private AlphaBiomeGenerator AlphaGen => _alphaGen ??= new AlphaBiomeGenerator(seed,
            BiomeRegistry.Get("alpha").AlphaParams ?? new AlphaTerrainParams());

        /// <summary>Controllable monolith feature (see MonolithSculptor). A tunable tower/column
        /// feature: frequency/size/height/carve, seed-driven.</summary>
        public MonolithSculptor Monoliths { get; private set; }

        /// <summary>Sedimentary quartz veins (see QuartzVeinGenerator): layered underground veins
        /// that follow the terrain surface like real cliff strata.</summary>
        public QuartzVeinGenerator QuartzVeins { get; private set; }

        /// <summary>Serpentine vein bands (see SerpentineGenerator): like quartz but biome-weighted
        /// (very common in paradise); contact zones with quartz veins turn into gold ore.</summary>
        public SerpentineGenerator Serpentines { get; private set; }

        /// <summary>Coal ore blobs (see CoalOreGenerator): prefer just under the living layer
        /// (decomposed biomass), rare deep pockets anywhere.</summary>
        public CoalOreGenerator CoalOres { get; private set; }

        /// <summary>Occasional underground gravel pockets (see GravelSplotchGenerator): sparse
        /// buried splotches in the stone, a nice find while digging - never everywhere.</summary>
        public GravelSplotchGenerator GravelSplotches { get; private set; }

        /// <summary>One colossal solid-brick pyramid per world (see PyramidGenerator). A rare,
        /// seed-fixed landmark with no purpose but to confuse and mystify.</summary>
        public PyramidGenerator Pyramids { get; private set; }

        /// <summary>Red clay discs on ocean/lake floors (see ClayDiscGenerator): small
        /// occasional flat clay patches on the bottom of any water body.</summary>
        public ClayDiscGenerator ClayDiscs { get; private set; }

        /// <summary>Underground red clay blobs (see RedClayBlobGenerator): sparse stone-replacement pockets.</summary>
        public RedClayBlobGenerator RedClayBlobs { get; private set; }

        /// <summary>A fixed seed-derived set of small, geometrically-perfect brick pyramids
        /// (see RegularPyramidGenerator). Each exists exactly once per world.</summary>
        public RegularPyramidGenerator RegularPyramids { get; private set; }

        // Octave noise layers for each role:
        //  _bodyA/_bodyB = two terrain-body generators (16 octaves), blended by _upperSelector
        //  _upperSelector = vertical upper/lower selector (8 octaves)
        //  _surfaceA = surface sand/gravel patches
        //  _continent = large-scale continent field, _relief = hills/cliffs factor
        private readonly NoiseOctaves _bodyA;
        private readonly NoiseOctaves _bodyB;
        private readonly NoiseOctaves _upperSelector;
        private readonly NoiseOctaves _surfaceA;
        private readonly NoiseOctaves _continent;
        private readonly NoiseOctaves _relief;
        // Classic-biome heightmap: the Classic biome replaces the surface line with a pure 2D
        // height noise (classic Minecraft had NO rolling 3D hills - just flat plateaus that
        // step up in sudden cliffs). Quantized so terrain rises in blocky steps.
        private readonly NoiseOctaves _classicHeight;
        private const double ClassicHeightFrequency = 0.08;  // ~100-block features
        private const double ClassicStep = 0.75;             // step height in field-y (6 blocks)
        // 4D density field for the experimental Anomaly biome: sampled on a curved slice
        // through 4D space (w follows y with a slow sine fold) -> folded, weaving strata
        // that plain 3D noise cannot express.
        private readonly NoiseOctaves4D _anomalyNoise;
        // Forest density field: low-frequency 2D noise that makes some regions DENSE
        // woodland and others sparse clearings. Sampled per tree candidate (continuous
        // across chunk borders); neutral areas keep the biome's normal tree rate.
        private readonly NoiseOctaves _forestDensity;
        private const double ForestFrequency = 0.015; // ~250-block woodland patches
        private const double ForestGain = 5.0;        // -1..1 noise -> 0.1x .. 6x tree rate

        /// <summary>Height of the terrain band in blocks (world -64 .. +191). Taller than the
        /// original 128 so amplified biomes can grow REAL mountains; sea level stays at world 0.
        /// Surface scans across the engine must use this instead of a hardcoded 127.</summary>
        public const int TerrainBandBlocks = 256;

        public TerrainChunkProvider(int seed = 20260809)
        {
            this.seed = seed;
            _biomeMap = new BiomeMap(seed);
            var rand = new Random(seed);
            // Octave layers: low-frequency-dominant FBM. The octave counts / start indices are
            // this engine's own tuning (not a copy of any specific generator's recipe).
            _bodyA = new NoiseOctaves(rand, 9, 7);
            _bodyB = new NoiseOctaves(rand, 9, 7);
            _upperSelector = new NoiseOctaves(rand, 7, 1);
            _surfaceA = new NoiseOctaves(rand, 5, 1);
            _continent = new NoiseOctaves(rand, 9, 3);
            _relief = new NoiseOctaves(rand, 7, 9);
            _classicHeight = new NoiseOctaves(rand, 3, 1);
            _anomalyNoise = new NoiseOctaves4D(rand, 9, 7);
            _amplifiedCarve = new NoiseOctaves(rand, 4, 3);
            _forestDensity = new NoiseOctaves(rand, 3, 0);
            Monoliths = new MonolithSculptor(seed);
            QuartzVeins = new QuartzVeinGenerator(seed);
            Serpentines = new SerpentineGenerator(seed);
            CoalOres = new CoalOreGenerator(seed);
            GravelSplotches = new GravelSplotchGenerator(seed);
            Pyramids = new PyramidGenerator(seed);
            RegularPyramids = new RegularPyramidGenerator(seed);
            ClayDiscs = new ClayDiscGenerator(seed);
            RedClayBlobs = new RedClayBlobGenerator(seed);
        }

        public Chunk GenerateChunk(int chunkX, int chunkZ, int chunkSize, int chunkHeight)
        {
            int originX = chunkX * chunkSize;
            int originZ = chunkZ * chunkSize;
            // Ground layer: OriginY=-64, local Y 0..447 = world -64..383.
            const int originY = ChunkManager.GroundOriginY;
            var chunk = new Chunk(chunkSize, chunkHeight, chunkSize, originX, originY, originZ);

            // The terrain band occupies local Y 0..127 (world -64..63) at the TOP of the ground
            // layer. Sea level is local 64 (world 0). Above the band (world 64..383) is open sky.
            // The DEEP world (-256..-65) is a separate layer generated by DeepChunkProvider.
            const int terrainBandStart = 0; // local Y where the terrain band begins (world -64)
            const int seaLevelLocalY = terrainBandStart + 64;

            int idBedrock = BlockRegistry.GetId("bedrock");
            int idWater = BlockRegistry.GetId("water");
            int idStone = BlockRegistry.GetId("stone");
            int idGrass = BlockRegistry.GetId("grass");
            int idDirt = BlockRegistry.GetId("dirt");
            int idSand = BlockRegistry.GetId("sand");
            int idGravel = BlockRegistry.GetId("gravel");
            int idLog = BlockRegistry.GetId("log");
            int idLeaves = BlockRegistry.GetId("leaves");
            int idCoal = BlockRegistry.GetId("coalore");
            int idIron = BlockRegistry.GetId("ironore");
            int idGold = BlockRegistry.GetId("goldore");
            int idDiamond = BlockRegistry.GetId("diamondore");

            // ---- ALPHA BIOME cell map ----
            // The Alpha biome replaces whole 4x4 cells with the byte-faithful 2010 generator:
            // its own density field, surface pass, ores and big trees. Cells are sampled at
            // their center; a chunk is "fullyAlpha" when all 16 cells are alpha (then every
            // engine-only feature is skipped for purity - no monoliths/pyramids in Alpha).
            var alphaCell = new bool[4, 4];
            bool hasAlpha = false, fullyAlpha = true;
            for (int fx = 0; fx < 4; fx++)
            {
                for (int fz = 0; fz < 4; fz++)
                {
                    alphaCell[fx, fz] = _biomeMap.BiomeAt(chunkX * 16 + fx * 4 + 2, chunkZ * 16 + fz * 4 + 2).Id == "alpha";
                    if (alphaCell[fx, fz]) hasAlpha = true;
                    else fullyAlpha = false;
                }
            }
            bool[,] cellMask = alphaCell; // captured for the per-column lookup below
            Func<int, int, bool> isAlphaColumn = (wx, wz) => cellMask[((wx - chunkX * 16) & 15) >> 2, ((wz - chunkZ * 16) & 15) >> 2];

            // ---- Build the 5 x 17 x 5 density field ----
            // Field x/z are in 4-block units (5 samples cover the chunk's 16 blocks), field y
            // in 8-block units (17 samples cover 128). All frequency/amplitude constants below
            // are this engine's own tuning.
            const int fxCount = 5, fyCount = TerrainBandBlocks / 8 + 1, fzCount = 5; // 33 field-y rows cover 256 blocks
            const double baseFreq = 592.0;
            double[] field = new double[fxCount * fyCount * fzCount];
            // Per-column surface line + amplified blend (4-block grid), needed by the
            // block-level carve band in the fill loop below.
            double[] colHeights = new double[fxCount * fzCount];
            double[] colAmp = new double[fxCount * fzCount];

            for (int fx = 0; fx < fxCount; fx++)
            {
                double xq = (chunkX * 4 + fx); // x field coord = worldX/4
                for (int fz = 0; fz < fzCount; fz++)
                {
                    double zq = (chunkZ * 4 + fz); // z field coord = worldZ/4
                    int col = (fx * fzCount + fz) * fyCount;

                    // Large-scale elevation: continent field + a sharpened relief signal.
                    double continent = _continent.Noise2D(xq, zq);
                    // Relief is normalized to ~[-1,1] so the shaping offset actually produces
                    // valleys (negative) vs hills (positive) instead of always being dominated.
                    double relief = _relief.Noise2DNormalized(xq * ReliefFrequency, zq * ReliefFrequency);

                    // Elevation baseline. The continent field is a weighted octave sum with a wide
                    // range (~+-2000+), so the bias decides where the zero-crossing sits. When the
                    // continent dips below -(bias*2) the elevation goes NEGATIVE, which inverts the
                    // falloff and produces a rare floating slab (flat top, hollow underside) - a
                    // deliberate "bug monolith". Raising the bias pushes that crossing into deeper,
                    // less common valleys, so those monoliths appear less often.
                    const double continentBias = 380.0;
                    double elevation = (continent + continentBias) / 480.0;
                    if (elevation > 1.0) elevation = 1.0;

                    double reliefShaped = relief / 1.0;
                    if (reliefShaped < 0.0) reliefShaped = -reliefShaped;
                    reliefShaped = reliefShaped * 2.6 - 2.6;
                    if (reliefShaped < 0.0)
                    {
                        reliefShaped /= 2.4;
                        if (reliefShaped < -1.0) reliefShaped = -1.0;
                        reliefShaped /= 1.7;
                        reliefShaped /= 2.4;
                        elevation = 0.0;
                    }
                    else
                    {
                        if (reliefShaped > 1.0) reliefShaped = 1.0;
                        reliefShaped /= 5.2;
                    }
                    elevation += 0.5;
                    // ANCHORED scale: keep the ORIGINAL 17/16 magnitude regardless of the taller
                    // band, so non-amplified biomes generate byte-identical terrain to before.
                    // Amplified biomes reach the new headroom via baseHeight instead.
                    reliefShaped = reliefShaped * 17.0 / 16.0;

                    // ---- BIOME-DRIVEN SURFACE LINE ----
                    // The authoritative biome at this column sets the target surface height.
                    // Field-y units: the terrain band is 16 field-y tall = 128 blocks, and sea
                    // level sits at field-y 8 (fraction 0.5). baseHeight is a band fraction, so:
                    //   centerHeight = baseHeight * 16 + relief * (variation * 16)
                    // Ocean biomes use a low baseHeight (< 0.5) so the surface sits below sea
                    // level -> water fills. Terrain height and biome label can never desync
                    // because both come from the same BiomeMap.
                    var biome = _biomeMap.BiomeAt(xq * 4.0, zq * 4.0);
                    // The Anomaly biome replaces the 3D body field with a 4D one (see below).
                    bool anomalyColumn = string.Equals(biome.Id, "anomaly", StringComparison.OrdinalIgnoreCase);
                    // The Classic biome replaces the surface line with a quantized 2D heightmap.
                    bool classicColumn = string.Equals(biome.Id, "classic", StringComparison.OrdinalIgnoreCase);
                    // ---- SMOOTHED BIOME SURFACE LINE ----
                    // A raw biome.BaseHeight is a step function: the instant you cross a biome
                    // border the target surface snaps from ocean-low (~0.28) to land-high (~0.56+),
                    // which is why every coast became a sheer cliff. To bring back beaches, sample
                    // the biome map around the column and blend the base heights with a Gaussian
                    // falloff. Near a shoreline the blend drags the surface down toward sea level
                    // gradually, so the land slopes down into the water instead of dropping off.
                    double blendedBase = 0.0;
                    double blendedVariation = 0.0;
                    double blendedAmplified = 0.0;   // 0..1; how strongly amplified terrain is mixed in
                    double weightSum = 0.0;
                    const int blendCells = 4;            // look 4 field cells out (= 16 blocks)
                    const double blendSigma = 12.0;      // falloff width in world blocks
                    for (int by = -blendCells; by <= blendCells; by++)
                    {
                        for (int bx = -blendCells; bx <= blendCells; bx++)
                        {
                            double dx = bx * 4.0; // field cell -> world blocks
                            double dz = by * 4.0;
                            double w = Math.Exp(-(dx * dx + dz * dz) / (2.0 * blendSigma * blendSigma));
                            var b = _biomeMap.BiomeAt(xq * 4.0 + dx, zq * 4.0 + dz);
                            blendedBase += b.BaseHeight * w;
                            blendedVariation += b.HeightVariation * w;
                            blendedAmplified += (b.Amplified ? 1.0 : 0.0) * w;
                            weightSum += w;
                        }
                    }
                    blendedBase /= weightSum;
                    blendedVariation /= weightSum;
                    blendedAmplified /= weightSum;

                    // ---- AMPLIFIED RELIEF ----
                    // Normal terrain relief only carves downward (reliefShaped <= 0) AND its
                    // shaping saturates near its maximum almost everywhere - remapping that
                    // saturated signal's sign produced flat mesa plateaus with cliff edges.
                    // Amplified biomes instead use the RAW signed relief noise: bell-distributed
                    // and continuous, so terrain rolls smoothly between real peaks and deep
                    // valleys. Non-amplified biomes keep the original shaping unchanged
                    // (blendedAmplified ~ 0 => identical behavior).
                    const double amplifiedGain = 1.2; // field-y of swing per relief unit (valleys plunge ~50 below sea)
                    double reliefMagnitude = -reliefShaped; // 0..~0.26
                    reliefShaped = reliefMagnitude * (1.0 - blendedAmplified)
                        + relief * amplifiedGain * blendedAmplified;

                    double centerHeight = blendedBase * 16.0
                        + reliefShaped * (blendedVariation * 16.0);

                    colHeights[fx * fzCount + fz] = centerHeight;
                    colAmp[fx * fzCount + fz] = blendedAmplified;

                    // ---- BEACH SHELF ----
                    // Sea level is field-y 8 (world 64). Land biome targets sit only ~1 field-y
                    // (~8 blocks) above it while ocean floors drop ~28 blocks below, so even a
                    // smooth blend makes a steep little bank instead of a beach. Compress the
                    // first few field-y above sea level with an easing curve whose slope is
                    // exactly zero at the waterline: the shore starts perfectly flat and rises
                    // gradually inland, giving a wide flat beach.
                    const double seaLevel = 8.0;
                    double aboveSea = centerHeight - seaLevel;
                    if (aboveSea > 0.0)
                    {
                        centerHeight = seaLevel + aboveSea * Math.Sqrt(aboveSea / (aboveSea + 0.6));
                    }

                    // ---- CLASSIC BIOME: 2D HEIGHTMAP SURFACE ----
                    // Overrides the surface line with pure 2D height noise, QUANTIZED into
                    // blocky steps: flat plateaus that suddenly cliff up to the next level -
                    // classic Minecraft terrain, which never had rolling smooth hills. The band
                    // hugs the sea like real classic (world -4..~28, sea = 0): flat blocky
                    // shores with the odd 6-block step inland instead of 96-block coastal
                    // cliffs. Where the shore meets a water biome, ease toward the
                    // beach-smoothed line so classic coasts slope into the ocean.
                    if (classicColumn)
                    {
                        double easedLine = centerHeight; // the beach-smoothed line, pre-classic
                        double h = _classicHeight.Noise2DNormalized(xq * ClassicHeightFrequency, zq * ClassicHeightFrequency);
                        double hf = 7.5 + (h * 0.5 + 0.5) * 4.0; // world -4 .. ~28
                        double classicH = Math.Floor(hf / ClassicStep) * ClassicStep;
                        centerHeight = classicH;
                        double wx = xq * 4.0, wz = zq * 4.0;
                        if (_biomeMap.IsNearWater((int)wx, (int)wz, 6, 2))
                        {
                            double beachBlend = _biomeMap.IsNearWater((int)wx, (int)wz, 2, 1) ? 1.0 : 0.4;
                            centerHeight = classicH + (easedLine - classicH) * beachBlend;
                        }
                    }

                    for (int fy = 0; fy < fyCount; fy++)
                    {
                        int idx = col + fy;
                        double yq = fy; // y field coord = worldY/8

                        double density;
                        if (anomalyColumn)
                        {
                            // ---- ANOMALY: 4D DENSITY FIELD ----
                            // Sample 4D simplex on a curved slice through 4D space: w follows y
                            // (twisting the vertical axis) plus a slow sine fold through the
                            // hyperplane. The resulting density folds and weaves into twisted
                            // strata, wrapped bands and alien structures - shapes 3D noise
                            // physically cannot produce. Same weight envelope as the 3D body
                            // (/480), so the surface line and falloff behave identically.
                            double w4 = yq * baseFreq * 1.3 + (xq + zq) * baseFreq * 0.2
                                + Math.Sin(xq * 0.5 + zq * 0.5 + yq * 0.4) * 3.0;
                            density = _anomalyNoise.Noise4D(xq * baseFreq, yq * baseFreq, zq * baseFreq, w4) / 480.0;
                        }
                        else
                        {
                            // Terrain body + vertical selector blend.
                            double bodyA = _bodyA.Noise3D(xq * baseFreq, yq * baseFreq, zq * baseFreq) / 480.0;
                            double bodyB = _bodyB.Noise3D(xq * baseFreq, yq * baseFreq, zq * baseFreq) / 480.0;
                            double selector = (_upperSelector.Noise3D(xq * (baseFreq / 96.0), yq * (baseFreq / 192.0), zq * (baseFreq / 96.0)) / 11.0 + 1.0) / 2.0;

                            if (selector < 0.0) density = bodyA;
                            else if (selector > 1.0) density = bodyB;
                            else density = bodyA + (bodyB - bodyA) * selector;
                        }

                        // Falloff: push density solid below the surface line, air above.
                        double falloff = ((double)fy - centerHeight) * 13.0 / elevation;
                        if (falloff < 0.0) falloff *= 3.6;
                        density -= falloff;

                        // Force air in the top field rows.
                        if (fy > fyCount - 4)
                        {
                            double clamp = (double)(fy - (fyCount - 4)) / 3.0;
                            density = density * (1.0 - clamp) + -9.0 * clamp;
                        }

                        field[idx] = density;
                    }
                }
            }

            // ---- generateTerrain: trilinear-interpolate the field into blocks ----
            // The terrain band occupies local Y terrainBandStart..terrainBandStart+255
            // (world -64..191); above it is open sky. Water below the band's sea level.
            for (int ly = 0; ly < TerrainBandBlocks; ly++)
            {
                int localY = terrainBandStart + ly;
                double gy = ly / 8.0;
                int fy0 = (int)gy;
                int fy1 = fy0 + 1;
                double ty = gy - fy0;
                if (fy0 > fyCount - 2) { fy0 = fyCount - 2; fy1 = fyCount - 1; ty = 1.0; }

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

                        // ---- AMPLIFIED BLOCK-LEVEL 3D CARVING ----
                        // Carve at 1-block resolution around the surface line: a mid-frequency
                        // 3D noise flips individual cells (density sits within ~3-4 blocks of
                        // the solid/air threshold here), producing real overhangs, arches,
                        // ledges and rocky pockets - the alpha-style carved surface. Deep below
                        // the surface stays solid and far above stays air (the carve never
                        // reaches). Amp/surface are bilinear-interpolated from the 4-block field
                        // grid so biome borders blend smoothly.
                        double amp = Lerp(
                            Lerp(colAmp[fx0 * fzCount + fz0], colAmp[fx1 * fzCount + fz0], tx),
                            Lerp(colAmp[fx0 * fzCount + fz1], colAmp[fx1 * fzCount + fz1], tx), tz);
                        if (amp > 0.01)
                        {
                            double surf = Lerp(
                                Lerp(colHeights[fx0 * fzCount + fz0], colHeights[fx1 * fzCount + fz0], tx),
                                Lerp(colHeights[fx0 * fzCount + fz1], colHeights[fx1 * fzCount + fz1], tx), tz);
                            int surfaceLY = (int)(surf * 8.0);
                            if (ly >= surfaceLY - 8 && ly <= surfaceLY + 8)
                            {
                                int wx = chunk.OriginX + lx;
                                int wz = chunk.OriginZ + lz;
                                int wy = chunk.OriginY + localY;
                                double carve = _amplifiedCarve.Noise3DNormalized(
                                    wx * CarveFrequency, wy * CarveFrequency * CarveYStretch, wz * CarveFrequency);
                                density += carve * CarveStrength * amp;
                            }
                        }

                        int block;
                        if (density > 0.0) block = idStone;
                        else if (localY < seaLevelLocalY) block = idWater;
                        else block = 0;
                        chunk[lx, localY, lz] = block;
                    }
                }
            }

            // ---- ALPHA terrain override (runs BEFORE the surface passes so the alpha cells
            // carry the true 2010 terrain, and the alpha surface pass dresses them first -
            // the engine's caves then tunnel through alpha grass exactly like 1.1.2_01).
            // Border cells blend the alpha density field with the engine's own field for
            // smooth transitions instead of cliffs. ----
            if (hasAlpha)
            {
                AlphaGen.FillOverride(chunk, chunkX, chunkZ, alphaCell,
                    (cx, cz) => _biomeMap.BiomeAt(cx * 4 + 2, cz * 4 + 2).Id,
                    field, idStone, idWater);
                AlphaGen.SurfacePass(chunk, chunkX, chunkZ, isAlphaColumn, idStone, idGrass, idDirt, idSand, idGravel, idWater);
            }

            // ---- Surface materials pass ----
            ReplaceBlocks(chunkX, chunkZ, chunk, idBedrock, idWater, idStone, idGrass, idDirt, idSand, idGravel, terrainBandStart, alphaCell);

            // ---- water first, caves second ----
            // The oceans are flooded BEFORE carving so the walkers only ever tunnel through
            // stone: caves below sea level stay DRY (the refill already ran), and the water
            // body itself is never cut into - no air pockets carved inside the ocean.
            RefillWaterBelowSeaLevel(chunk, terrainBandStart, idWater, seaLevelLocalY);
            GenerateCaves(chunkX, chunkZ, chunk, terrainBandStart);
            GenerateTrees(chunkX, chunkZ, chunk);

            // ---- alpha forests & ores (the 2010 populate step; runs after caves like the
            // original pipeline) ----
            if (hasAlpha)
            {
                AlphaGen.GenerateTrees(chunk, chunkX, chunkZ, isAlphaColumn, idLog, idLeaves, idGrass, idDirt);
                AlphaGen.GenerateOres(chunk, chunkX, chunkZ, alphaCell, idStone, idCoal, idIron, idGold, idDiamond);
            }

            // ---- engine-only features ----
            // Skipped ENTIRELY in fully-alpha chunks: the alpha biome is a faithful slice of
            // 1.1.2_01, which had no monoliths, pyramids or the engine's ore styles.
            if (!fullyAlpha)
            {
                // ---- monoliths (controllable feature; runs after caves so towers stand on ground) ----
                Monoliths.Sculpt(chunk, terrainBandStart, chunkSize, chunkHeight);

                // ---- sedimentary quartz veins (follows terrain, like cliff strata) ----
                QuartzVeins.Generate(chunk, chunkX, chunkZ, terrainBandStart, chunkSize, chunkHeight);

                // ---- serpentine veins (biome-weighted; paradise gets lots) ----
                // Contact zones with quartz veins are gilded into gold ore automatically.
                Serpentines.Generate(chunk, chunkX, chunkZ, terrainBandStart, chunkSize, chunkHeight,
                    (wx, wz) => _biomeMap.BiomeAt(wx, wz).Id, QuartzVeins);

                // ---- underground gravel splotches (occasional pockets, easy to stumble on) ----
                GravelSplotches.Generate(chunk, chunkX, chunkZ, terrainBandStart, chunkSize, chunkHeight);

                // ---- coal ore (biomass coal just under the living layer, rare deep pockets) ----
                CoalOres.Generate(chunk, chunkX, chunkZ, terrainBandStart, chunkSize, chunkHeight);

                // ---- red clay discs on ocean and lake floors (small flat clay patches) ----
                ClayDiscs.Generate(chunk, chunkX, chunkZ, terrainBandStart, chunkSize, chunkHeight);

                // ---- underground red clay blobs (sparse stone pockets, backup source) ----
                RedClayBlobs.Generate(chunk, chunkX, chunkZ, terrainBandStart, chunkSize, chunkHeight);

                // ---- regular pyramids (once-per-world monuments, geometrically perfect) ----
                RegularPyramids.Generate(chunk, chunkX, chunkZ, terrainBandStart, chunkSize, chunkHeight, EstimateSurfaceHeightAt);

                // ---- the Great Pyramid (runs LAST so its volume is pure solid brick) ----
                Pyramids.Generate(chunk, chunkX, chunkZ, terrainBandStart, chunkSize, chunkHeight);
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

        // Surface materials pass: scans each column top-down, replaces the surface stone with
        // grass/dirt (or sand/gravel in their biomes), fills bedrock at the bottom.
        private void ReplaceBlocks(int chunkX, int chunkZ, Chunk chunk,
            int idBedrock, int idWater, int idStone, int idGrass, int idDirt, int idSand, int idGravel,
            int terrainBandStart, bool[,] alphaCell)
        {
            byte[] blocks = chunk.RawBlocks;
            const int height = ChunkManager.ChunkHeight; // 448
            const int width = 16;
            const int seaLevel = 64; // relative to the terrain band start (world 0)
            var rand = new Random(unchecked(chunkX * 401719 + chunkZ * 811543 ^ seed));
            const double surfaceScale = 1.0 / 29.0;

            for (int x = 0; x < width; x++)
            {
                for (int z = 0; z < width; z++)
                {
                    double wx = chunkX * width + x;
                    double wz = chunkZ * width + z;
                    // Alpha columns keep the alpha surface pass' dressing: only bedrock here.
                    bool isAlpha = alphaCell[x >> 2, z >> 2];

                    // The biome at this column drives the surface materials (surface/fill blocks
                    // and fill depth come straight from the data-driven biome definition).
                    var biome = _biomeMap.BiomeAt(wx, wz);
                    int biomeSurface = BlockRegistry.GetId(biome.SurfaceBlock);
                    int biomeFill = BlockRegistry.GetId(biome.FillBlock);
                    int biomeFillDepth = Math.Max(0, biome.FillDepth);
                    // Optional fill mix: some fill layers turn into this block (e.g. dirt mixed
                    // into the red clay) so the ground isn't one uniform slab.
                    int biomeFillMix = string.IsNullOrEmpty(biome.FillMixBlock) ? 0 : BlockRegistry.GetId(biome.FillMixBlock);
                    float fillMixChance = biome.FillMixChance;

                    // Beach: when this column is near a water biome (ocean), give it a beach 50% of
                    // the time - so some shores are sandy and some keep their native surface. The
                    // beach roll also picks between sand and gravel, so there are sandy beaches AND
                    // gravelly beaches.
                    bool nearWater = !biome.IsWater && _biomeMap.IsNearWater((int)wx, (int)wz, 5, 2);
                    bool beach = nearWater && (rand.Next(2) == 0);
                    if (beach)
                    {
                        if (rand.Next(2) == 0)
                        {
                            biomeSurface = BlockRegistry.GetId("sand");
                            biomeFill = BlockRegistry.GetId("sand");
                        }
                        else
                        {
                            biomeSurface = BlockRegistry.GetId("gravel");
                            biomeFill = BlockRegistry.GetId("gravel");
                        }
                        biomeFillDepth = 3;
                        biomeFillMix = 0; // beaches are pure sand/gravel, no dirt mix
                    }

                    // Sand biome: surfaceA at (x/29, z/29, 0) > 0. Gravel biome: surfaceA at a
                    // rotated offset > 2.8.
                    bool gravelly = _surfaceA.Noise3D(wz * surfaceScale, 121.037, wx * surfaceScale) + rand.NextDouble() * 0.2 > 2.8;

                    int depthRemaining = -1; // -1 = haven't found the surface yet
                    int topBlock = biomeSurface;
                    int fillBlock = biomeFill;

                    // Scan the terrain band (band-relative 127..0, actual local terrainBandStart..+127).
                    for (int bandLy = TerrainBandBlocks - 1; bandLy >= 0; bandLy--)
                    {
                        int ly = terrainBandStart + bandLy;
                        int idx = (x * width + z) * height + ly;

                        if (bandLy <= rand.Next(6) - 1)
                        {
                            blocks[idx] = (byte)idBedrock;
                        }
                        else if (!isAlpha)
                        {
                            if (blocks[idx] == 0)
                            {
                                depthRemaining = -1;
                            }
                            else if (blocks[idx] == idStone)
                        {
                            if (depthRemaining == -1)
                            {
                                if (biomeFillDepth <= 0)
                                {
                                    topBlock = 0;
                                    fillBlock = idStone;
                                }
                                else if (bandLy >= seaLevel - 4 && bandLy <= seaLevel + 1)
                                {
                                    // Biome surface blocks; grass/dirt fallback for land biomes,
                                    // sand for ocean/desert (from the biome definition).
                                    topBlock = biomeSurface;
                                    fillBlock = biomeFill;
                                    // Extra gravel noise patches only on non-beach shores (a beach is
                                    // already sand or gravel from the beach roll).
                                    if (gravelly && !beach && biomeFillDepth <= 3)
                                    {
                                        topBlock = 0;
                                        fillBlock = idGravel;
                                    }
                                }
                                if (bandLy < seaLevel && topBlock == 0) topBlock = idWater;
                                depthRemaining = biomeFillDepth;
                                int layerFill = (biomeFillMix != 0 && rand.NextDouble() < fillMixChance) ? biomeFillMix : fillBlock;
                                blocks[idx] = (byte)(bandLy >= seaLevel - 1 ? topBlock : layerFill);
                            }
                            else if (depthRemaining > 0)
                            {
                                depthRemaining--;
                                int layerFill = (biomeFillMix != 0 && rand.NextDouble() < fillMixChance) ? biomeFillMix : fillBlock;
                                blocks[idx] = (byte)layerFill;
                            }
                        }
                        }
                    }
                }
            }
        }

        // Cave generation: a per-chunk deterministic chance of spawning random-walker tunnels that
        // carve winding, branching tubes through the stone.
        private void GenerateCaves(int chunkX, int chunkZ, Chunk chunk, int terrainBandStart)
        {
            byte[] blocks = chunk.RawBlocks;

            // Iterate a 17x17 region of neighbour-chunk seeds and spawn walkers at the NEIGHBOR
            // chunk's coordinates, carving into THIS chunk's block array. That way a cave that
            // starts in a neighbouring chunk crosses the border and continues here - without this,
            // every tunnel dies at the chunk edge because the walker can only carve the current
            // chunk's blocks.
            var rand = new Random(seed);
            long seedA = rand.Next() * 2L + 1L;
            long seedB = rand.Next() * 2L + 1L;

            for (int nx = chunkX - 8; nx <= chunkX + 8; nx++)
            {
                for (int nz = chunkZ - 8; nz <= chunkZ + 8; nz++)
                {
                    var rand2 = new Random(unchecked((int)((long)nx * seedA + (long)nz * seedB ^ seed)));

                    int caveCount = rand2.Next(rand2.Next(rand2.Next(36) + 1) + 1);
                    if (rand2.Next(13) != 0) caveCount = 0;

                    for (int c = 0; c < caveCount; c++)
                    {
                        // Walker starts in the NEIGHBOR chunk (nx/nz), so its tunnel can reach
                        // across the border into this chunk.
                        double x = nx * 16 + rand2.Next(16);
                        // Y is confined to the terrain band. Some caves spawn LOW so they can
                        // descend through the bedrock floor and open a passage down into the deep
                        // layer (the deep chunk below mirrors these openings via
                        // ChunkManager.SyncDeepAccess).
                        double y = (rand2.Next(3) == 0)
                            ? terrainBandStart + rand2.Next(16)      // deep diver: digs toward the floor
                            : terrainBandStart + rand2.Next(rand2.Next(116) + 8);
                        double z = nz * 16 + rand2.Next(16);

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
                            float pitch = (float)((rand2.NextDouble() - 0.5) * 2.0 / 7.0);
                            float size = (float)(rand2.NextDouble() * 2.0 + rand2.NextDouble());

                            // Deep caves have a small chance to spawn 5x as fat - the walker's
                            // size drives the tunnel radius, so x5 turns a ~2-4 wide tube into a
                            // ~20-wide chamber. Only in the lower terrain band and ~1 in 8 nodes.
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

            if (maxLength <= 0) maxLength = 104 - rng.Next(104 / 4);
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
                double radius = 1.4 + Math.Sin(len * Math.PI / maxLength) * size;
                double vRadius = radius * scale;

                x += Math.Cos(yaw) * Math.Cos(pitch);
                y += Math.Sin(pitch);
                z += Math.Sin(yaw) * Math.Cos(pitch);

                if (rng.Next(6) == 0) pitch *= 0.9f;
                else pitch *= 0.66f;
                pitch += wobblePitch * 0.1f;
                yaw += wobbleYaw * 0.1f;
                wobblePitch *= 0.88f;
                wobbleYaw *= 11f / 15f;
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
                if (minY < 0) minY = 0; // allow caves to punch through the bedrock floor (local 0)
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
                                if (id == idWater) continue; // never carve the water itself
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

        // Re-floods below-sea-level air in the terrain band (local Y < seaLevelLocalY) with water.
        // Runs BEFORE cave carving (see GenerateChunk): the walkers then tunnel only through
        // stone, so underwater caves stay dry while the ocean body is never carved into.
        // Only touches the terrain band; deeper layers handle their own water.
        private void RefillWaterBelowSeaLevel(Chunk chunk, int terrainBandStart, int idWater, int seaLevelLocalY)
        {
            byte[] blocks = chunk.RawBlocks;
            const int width = 16;
            const int height = ChunkManager.ChunkHeight;
            for (int x = 0; x < width; x++)
            {
                for (int z = 0; z < width; z++)
                {
                    int col = (x * width + z) * height;
                    int maxLy = Math.Min(seaLevelLocalY, terrainBandStart + TerrainBandBlocks);
                    for (int ly = terrainBandStart; ly < maxLy; ly++)
                    {
                        int idx = col + ly;
                        if (blocks[idx] == 0) blocks[idx] = (byte)idWater;
                    }
                }
            }
        }

        // Trees: a few per chunk on grass, each a 4-6 tall trunk with a rounded leaf canopy (top
        // corners cut for the plus shape). Trees that would cross the chunk edge fail their
        // clearance check and don't spawn.
        private void GenerateTrees(int chunkX, int chunkZ, Chunk chunk)
        {
            byte[] blocks = chunk.RawBlocks;
            var rand = new Random(unchecked(chunkX * 401719 + chunkZ * 811543 ^ seed) ^ 0x51AB7F);
            byte idWood = (byte)BlockRegistry.GetId("log");
            byte idLeaves = (byte)BlockRegistry.GetId("leaves");
            byte idGrass = (byte)BlockRegistry.GetId("grass");
            byte idDirt = (byte)BlockRegistry.GetId("dirt");
            byte idRedClay = (byte)BlockRegistry.GetId("redclay");
            byte idSand = (byte)BlockRegistry.GetId("sand");
            const int height = ChunkManager.ChunkHeight;

            int treeCount = 0;
            // Chunk-wide forest density (for the per-chunk tree cap): patches of dense
            // woodland allow many more trees than the normal 8 before thinning back out.
            double chunkForest = 1.0 + _forestDensity.Noise2DNormalized(
                (chunkX * 16 + 8) * ForestFrequency, (chunkZ * 16 + 8) * ForestFrequency) * ForestGain;
            if (chunkForest < 0.1) chunkForest = 0.1;
            if (chunkForest > 6.0) chunkForest = 6.0;
            int maxTrees = 8 + (int)(chunkForest * 2.0);

            for (int t = 0; t < 16; t++)
            {
                int lx = rand.Next(16);
                int lz = rand.Next(16);
                var biome = _biomeMap.BiomeAt(chunkX * 16 + lx, chunkZ * 16 + lz);
                if (biome.TreeDensity <= 0) continue;
                // Per-candidate forest density: smooth across chunk borders, so woodland
                // thins into clearings gradually instead of in chunk-sized steps.
                double density = 1.0 + _forestDensity.Noise2DNormalized(
                    (chunkX * 16 + lx) * ForestFrequency, (chunkZ * 16 + lz) * ForestFrequency) * ForestGain;
                if (density < 0.1) density = 0.1;
                if (density > 6.0) density = 6.0;
                if (rand.Next(16) >= biome.TreeDensity * density) continue;

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
                // The trunk starts one block ABOVE the surface block (which must be grass/dirt/redclay).
                string style = string.IsNullOrEmpty(biome.TreeType) ? "oak" : biome.TreeType.ToLowerInvariant();
                // A biome can mix a SECOND tree style into its forests (e.g. desert grows
                // dead snags with the occasional oasis palm).
                if (biome.TreeSecondaryChance > 0f && !string.IsNullOrEmpty(biome.TreeTypeSecondary)
                    && rand.NextDouble() < biome.TreeSecondaryChance)
                {
                    style = biome.TreeTypeSecondary.ToLowerInvariant();
                }
                switch (style)
                {
                    case "pine":
                        GeneratePineTree(blocks, lx, surfaceY + 1, lz, rand, idWood, idLeaves);
                        break;
                    case "round":
                        // The classic Alpha-era round tree: short trunk, flat rounded crown.
                        GenerateRoundTree(blocks, lx, surfaceY + 1, lz, rand, idWood, idLeaves, idGrass, idDirt);
                        break;
                    case "tall":
                        // A towering forest oak: the cathedral pillars of the dense Anomaly woodlands.
                        GenerateTallTree(blocks, lx, surfaceY + 1, lz, rand, idWood, idLeaves, idGrass, idDirt);
                        break;
                    case "dead":
                        // A bare snag: trunk with branch stubs and almost no leaves.
                        GenerateDeadTree(blocks, lx, surfaceY + 1, lz, rand, idWood, idLeaves, idGrass, idDirt, idSand);
                        break;
                    case "willow":
                        // A squat umbrella-crowned tree for the open plains.
                        GenerateWillowTree(blocks, lx, surfaceY + 1, lz, rand, idWood, idLeaves, idGrass, idDirt);
                        break;
                    case "gnarled":
                        // An ancient, gnarled old oak with hanging branch blobs (the hill
                        // forests are ALL old trees - no sapling oaks up there).
                        GenerateBigOakTree(blocks, lx, surfaceY + 1, lz, rand, idWood, idLeaves, idGrass, idDirt, idRedClay);
                        break;
                    case "palm":
                        // A leaning desert palm with a fan of fronds (roots in sand).
                        GeneratePalmTree(blocks, lx, surfaceY + 1, lz, rand, idWood, idLeaves, idGrass, idDirt, idSand);
                        break;
                    case "cypress":
                        // A slim swamp cypress: flared skirt, narrow tiers, a pointed tip.
                        GenerateCypressTree(blocks, lx, surfaceY + 1, lz, rand, idWood, idLeaves, idGrass, idDirt);
                        break;
                    default:
                        // Oak: small chance a sapling grows into a big, gnarled old tree.
                        if (rand.Next(10) == 0)
                        {
                            GenerateBigOakTree(blocks, lx, surfaceY + 1, lz, rand, idWood, idLeaves, idGrass, idDirt, idRedClay);
                        }
                        else
                        {
                            GenerateTree(blocks, lx, surfaceY + 1, lz, rand, idWood, idLeaves, idGrass, idDirt, idRedClay);
                        }
                        break;
                }
                if (++treeCount >= maxTrees) break;
            }
        }

        // One tree rooted with its trunk base at (x, baseY, z) - baseY is the first trunk cell,
        // the ground (grass/dirt) sits at baseY-1.
        private void GenerateTree(byte[] blocks, int x, int baseY, int z, Random rand,
            byte idWood, byte idLeaves, byte idGrass, byte idDirt, byte idRedClay)
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

            // The ground must be grass, dirt, or red clay (Paradise foothills).
            if (baseY < 1) return;
            byte ground = blocks[(x * 16 + z) * height + (baseY - 1)];
            if (ground != idGrass && ground != idDirt && ground != idRedClay) return;

            // Shade the ground under the tree with dirt (grass only - red clay stays red).
            if (ground == idGrass) blocks[(x * 16 + z) * height + (baseY - 1)] = idDirt;

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

        // The classic pre-Beta "round tree": a short trunk (4-6) crowned with a flat, rounded
        // blob of leaves - two 5x5 layers with trimmed corners and a 3x3 cap. The kind of
        // tree Classic and early Alpha worlds were made of. Grows on grass or dirt.
        private void GenerateRoundTree(byte[] blocks, int x, int baseY, int z, Random rand,
            byte idWood, byte idLeaves, byte idGrass, byte idDirt)
        {
            const int height = ChunkManager.ChunkHeight;
            int trunkHeight = rand.Next(3) + 4;
            int topY = baseY + trunkHeight;

            // Clearance: trunk column plus the 2-block canopy footprint.
            for (int y = baseY; y <= topY + 1 && y < height; y++)
            {
                int radius = y == baseY ? 0 : 2;
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

            if (baseY < 1) return;
            byte ground = blocks[(x * 16 + z) * height + (baseY - 1)];
            if (ground != idGrass && ground != idDirt) return;
            if (ground == idGrass) blocks[(x * 16 + z) * height + (baseY - 1)] = idDirt;

            // The flat rounded crown: loose 5x5 layer, plus-shaped 5x5 layer, full 3x3 cap.
            for (int i = 0; i < 3; i++)
            {
                int y = topY - 1 + i;
                if (y < 0 || y >= height) continue;
                int radius = i < 2 ? 2 : 1;
                for (int dx = -radius; dx <= radius; dx++)
                {
                    for (int dz = -radius; dz <= radius; dz++)
                    {
                        bool isCorner = Math.Abs(dx) == radius && Math.Abs(dz) == radius;
                        if (isCorner && i != 2 && (i == 1 || rand.Next(3) != 0)) continue;
                        int lx = x + dx;
                        int lz = z + dz;
                        if (lx < 0 || lx >= 16 || lz < 0 || lz >= 16) continue;
                        int idx = (lx * 16 + lz) * height + y;
                        byte b = blocks[idx];
                        if (b == 0 || b == idLeaves) blocks[idx] = idLeaves;
                    }
                }
            }

            // Trunk: only replace air or leaves so it doesn't punch through terrain.
            for (int i = 0; i < trunkHeight; i++)
            {
                int y = baseY + i;
                if (y < 0 || y >= height) break;
                int idx = (x * 16 + z) * height + y;
                byte b = blocks[idx];
                if (b == 0 || b == idLeaves) blocks[idx] = idWood;
            }
        }

        // A towering forest oak (8-12 tall): one straight trunk and a big ragged crown at
        // the top with stray leaves spiking out - cathedral pillars for dense woodlands.
        // Grows on grass or dirt.
        private void GenerateTallTree(byte[] blocks, int x, int baseY, int z, Random rand,
            byte idWood, byte idLeaves, byte idGrass, byte idDirt)
        {
            const int height = ChunkManager.ChunkHeight;
            int trunkHeight = rand.Next(5) + 8;
            int topY = baseY + trunkHeight;

            // Clearance: trunk column plus the 3-wide crown footprint near the top.
            for (int y = baseY; y <= topY + 1 && y < height; y++)
            {
                int radius = y == baseY ? 0 : y >= topY - 1 ? 3 : 1;
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

            if (baseY < 1) return;
            byte ground = blocks[(x * 16 + z) * height + (baseY - 1)];
            if (ground != idGrass && ground != idDirt) return;
            if (ground == idGrass) blocks[(x * 16 + z) * height + (baseY - 1)] = idDirt;

            // The crown: wide 5x5 layers with smaller tiers above, all filled full.
            for (int y = topY - 2; y <= topY + 1 && y < height; y++)
            {
                if (y < 0) continue;
                int radius = y >= topY ? 1 : 2;
                for (int dx = -radius; dx <= radius; dx++)
                {
                    for (int dz = -radius; dz <= radius; dz++)
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
            // Stray spikes: a few single leaves at radius 2-3, for the ragged canopy edge.
            for (int i = 0; i < 5; i++)
            {
                int sx = x + (rand.Next(2) == 0 ? -1 : 1) * (2 + rand.Next(2));
                int sz = z + (rand.Next(2) == 0 ? -1 : 1) * (2 + rand.Next(2));
                int sy = topY - rand.Next(2);
                if (sx < 0 || sx >= 16 || sz < 0 || sz >= 16 || sy < 0 || sy >= height) continue;
                int idx = (sx * 16 + sz) * height + sy;
                byte b = blocks[idx];
                if (b == 0 || b == idLeaves) blocks[idx] = idLeaves;
            }

            for (int i = 0; i < trunkHeight; i++)
            {
                int y = baseY + i;
                if (y < 0 || y >= height) break;
                int idx = (x * 16 + z) * height + y;
                byte b = blocks[idx];
                if (b == 0 || b == idLeaves) blocks[idx] = idWood;
            }
        }

        // A dead snag: a bare trunk (3-6) with a couple of branch stubs poking out at odd
        // angles and only a tiny tuft of leaves left at the tip - haunted-looking. Grows
        // on grass or dirt.
        private void GenerateDeadTree(byte[] blocks, int x, int baseY, int z, Random rand,
            byte idWood, byte idLeaves, byte idGrass, byte idDirt, byte idSand)
        {
            const int height = ChunkManager.ChunkHeight;
            int trunkHeight = rand.Next(4) + 3;
            int topY = baseY + trunkHeight;

            // Clearance: trunk column plus the stub footprint (2 wide).
            for (int y = baseY; y <= topY + 1 && y < height; y++)
            {
                int radius = y == baseY ? 0 : 2;
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

            if (baseY < 1) return;
            byte ground = blocks[(x * 16 + z) * height + (baseY - 1)];
            if (ground != idGrass && ground != idDirt && ground != idSand) return;
            if (ground == idGrass) blocks[(x * 16 + z) * height + (baseY - 1)] = idDirt;

            for (int i = 0; i < trunkHeight; i++)
            {
                int y = baseY + i;
                if (y < 0 || y >= height) break;
                int idx = (x * 16 + z) * height + y;
                byte b = blocks[idx];
                if (b == 0 || b == idLeaves) blocks[idx] = idWood;
            }

            // Branch stubs: 1-2 short log arms at odd heights.
            int stubs = rand.Next(2) + 1;
            for (int i = 0; i < stubs; i++)
            {
                int by = baseY + rand.Next(1, Math.Max(2, trunkHeight));
                int dir = rand.Next(4);
                int bx = x + (dir == 0 ? 1 : dir == 1 ? -1 : 0);
                int bz = z + (dir == 2 ? 1 : dir == 3 ? -1 : 0);
                if (bx < 0 || bx >= 16 || bz < 0 || bz >= 16) continue;
                int idx = (bx * 16 + bz) * height + by;
                byte b = blocks[idx];
                if (b == 0 || b == idLeaves) blocks[idx] = idWood;
            }
            // A tiny last tuft of leaves at the tip, most of the time.
            if (rand.Next(4) != 0 && topY + 1 < height)
            {
                int idx = (x * 16 + z) * height + (topY + 1);
                byte b = blocks[idx];
                if (b == 0 || b == idLeaves) blocks[idx] = idLeaves;
            }
        }

        // A squat willow for the open plains: a short trunk (3-4) under a wide flat crown -
        // a 7x7 top with trimmed corners, a 5x5 under it, and the rim hanging one block
        // lower all around, like a green umbrella. Grows on grass or dirt.
        private void GenerateWillowTree(byte[] blocks, int x, int baseY, int z, Random rand,
            byte idWood, byte idLeaves, byte idGrass, byte idDirt)
        {
            const int height = ChunkManager.ChunkHeight;
            int trunkHeight = rand.Next(2) + 3;
            int topY = baseY + trunkHeight;

            // Clearance: the full 3-block umbrella footprint.
            for (int y = baseY; y <= topY + 1 && y < height; y++)
            {
                int radius = y == baseY ? 0 : 3;
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

            if (baseY < 1) return;
            byte ground = blocks[(x * 16 + z) * height + (baseY - 1)];
            if (ground != idGrass && ground != idDirt) return;
            if (ground == idGrass) blocks[(x * 16 + z) * height + (baseY - 1)] = idDirt;

            // The umbrella top: a wide 7x7 layer with trimmed corners.
            for (int dx = -3; dx <= 3; dx++)
            {
                for (int dz = -3; dz <= 3; dz++)
                {
                    if (Math.Abs(dx) == 3 && Math.Abs(dz) == 3 && rand.Next(3) != 0) continue;
                    int lx = x + dx;
                    int lz = z + dz;
                    if (lx < 0 || lx >= 16 || lz < 0 || lz >= 16) continue;
                    int idx = (lx * 16 + lz) * height + topY;
                    byte b = blocks[idx];
                    if (b == 0 || b == idLeaves) blocks[idx] = idLeaves;
                }
            }
            // A full 5x5 layer just under it.
            for (int dx = -2; dx <= 2; dx++)
            {
                for (int dz = -2; dz <= 2; dz++)
                {
                    int lx = x + dx;
                    int lz = z + dz;
                    if (lx < 0 || lx >= 16 || lz < 0 || lz >= 16) continue;
                    int idx = (lx * 16 + lz) * height + (topY - 1);
                    byte b = blocks[idx];
                    if (b == 0 || b == idLeaves) blocks[idx] = idLeaves;
                }
            }
            // Drooping rim: the outer ring of the crown hangs one block lower all around.
            for (int dx = -3; dx <= 3; dx++)
            {
                for (int dz = -3; dz <= 3; dz++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dz)) < 2) continue;
                    if (Math.Abs(dx) == 3 && Math.Abs(dz) == 3 && rand.Next(3) != 0) continue;
                    int lx = x + dx;
                    int lz = z + dz;
                    if (lx < 0 || lx >= 16 || lz < 0 || lz >= 16) continue;
                    int idx = (lx * 16 + lz) * height + (topY - 2);
                    byte b = blocks[idx];
                    if (b == 0 || b == idLeaves) blocks[idx] = idLeaves;
                }
            }
            // A tiny cap leaf on top.
            if (topY + 1 < height)
            {
                int idx = (x * 16 + z) * height + (topY + 1);
                byte b = blocks[idx];
                if (b == 0 || b == idLeaves) blocks[idx] = idLeaves;
            }

            for (int i = 0; i < trunkHeight; i++)
            {
                int y = baseY + i;
                if (y < 0 || y >= height) break;
                int idx = (x * 16 + z) * height + y;
                byte b = blocks[idx];
                if (b == 0 || b == idLeaves) blocks[idx] = idWood;
            }
        }

        // A leaning desert palm with a fan of fronds (roots in sand).
        private void GeneratePalmTree(byte[] blocks, int x, int baseY, int z, Random rand,
            byte idWood, byte idLeaves, byte idGrass, byte idDirt, byte idSand)
        {
            const int height = ChunkManager.ChunkHeight;
            int trunkHeight = rand.Next(4) + 4;   // 4..7
            int offFactor = (rand.Next(4) + 1) / 2; // 1..2 (lean amount)
            int dxSign = rand.Next(4) switch { 0 => 1, 1 => -1, 2 => 0, _ => 0 };
            int dzSign = rand.Next(4) switch { 0 => 0, 1 => 0, 2 => 1, _ => -1 };

            // Clearance: trunk column (radius 1 at each level) plus canopy footprint.
            for (int i = 0; i <= trunkHeight; i++)
            {
                int ly = baseY + i;
                if (ly < 0 || ly >= height) continue;
                int off = i / offFactor;
                int tx = x + dxSign * off;
                int tz = z + dzSign * off;
                if (tx < 0 || tx >= 16 || tz < 0 || tz >= 16) return;
                int radius = (i >= trunkHeight - 1) ? 2 : 1;
                for (int dx = -radius; dx <= radius; dx++)
                {
                    for (int dz = -radius; dz <= radius; dz++)
                    {
                        int lx = tx + dx;
                        int lz = tz + dz;
                        if (lx < 0 || lx >= 16 || lz < 0 || lz >= 16 || ly < 0 || ly >= height) return;
                        byte b = blocks[(lx * 16 + lz) * height + ly];
                        if (b != 0 && b != idLeaves) return;
                    }
                }
            }

            if (baseY < 1) return;
            byte ground = blocks[(x * 16 + z) * height + (baseY - 1)];
            if (ground != idGrass && ground != idDirt && ground != idSand) return;
            if (ground == idGrass) blocks[(x * 16 + z) * height + (baseY - 1)] = idDirt;

            // Trunk: lean with step every level.
            for (int i = 0; i < trunkHeight; i++)
            {
                int y = baseY + i;
                if (y < 0 || y >= height) break;
                int off = i / offFactor;
                int tx = x + dxSign * off;
                int tz = z + dzSign * off;
                if (tx < 0 || tx >= 16 || tz < 0 || tz >= 16) break;
                int idx = (tx * 16 + tz) * height + y;
                byte b = blocks[idx];
                if (b == 0 || b == idLeaves) blocks[idx] = idWood;
            }

            // Frond crown at the top.
            int topY = baseY + trunkHeight;
            // Tip: plus shape of radius 2.
            for (int dx = -2; dx <= 2; dx++)
            {
                for (int dz = -2; dz <= 2; dz++)
                {
                    if (Math.Abs(dx) == 2 && Math.Abs(dz) == 2) continue;
                    int lx = x + dx;
                    int lz = z + dz;
                    if (lx < 0 || lx >= 16 || lz < 0 || lz >= 16) continue;
                    int idx = (lx * 16 + lz) * height + topY;
                    byte b = blocks[idx];
                    if (b == 0 || b == idLeaves) blocks[idx] = idLeaves;
                }
            }
            // Ring at top-1: radius 2 with corners cut.
            for (int dx = -2; dx <= 2; dx++)
            {
                for (int dz = -2; dz <= 2; dz++)
                {
                    bool isCorner = Math.Abs(dx) == 2 && Math.Abs(dz) == 2;
                    if (isCorner && rand.Next(3) != 0) continue;
                    int lx = x + dx;
                    int lz = z + dz;
                    if (lx < 0 || lx >= 16 || lz < 0 || lz >= 16) continue;
                    int idx = (lx * 16 + lz) * height + (topY - 1);
                    byte b = blocks[idx];
                    if (b == 0 || b == idLeaves) blocks[idx] = idLeaves;
                }
            }
// Tiny cap on top.
            if (topY + 1 < height)
            {
                int idx = (x * 16 + z) * height + (topY + 1);
                byte b = blocks[idx];
                if (b == 0 || b == idLeaves) blocks[idx] = idLeaves;
            }
        }

        // A slim swamp cypress: flared skirt, narrow tiers, a pointed tip.
        private void GenerateCypressTree(byte[] blocks, int x, int baseY, int z, Random rand,
            byte idWood, byte idLeaves, byte idGrass, byte idDirt)
        {
            const int height = ChunkManager.ChunkHeight;
            int trunkHeight = rand.Next(4) + 7;   // 7..10
            int topY = baseY + trunkHeight;

            // Clearance: radius 2 through the whole height (skirt + tiers).
            for (int y = baseY; y <= topY + 1 && y < height; y++)
            {
                int radius = y <= baseY + 1 ? 2 : (y >= topY - 1 ? 1 : (y - baseY) % 2 == 0 ? 1 : 0);
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

            if (baseY < 1) return;
            byte ground = blocks[(x * 16 + z) * height + (baseY - 1)];
            if (ground != idGrass && ground != idDirt) return;
            if (ground == idGrass) blocks[(x * 16 + z) * height + (baseY - 1)] = idDirt;

            // Skirt: baseY and baseY+1 with radius 2 corners cut.
            for (int dx = -2; dx <= 2; dx++)
            {
                for (int dz = -2; dz <= 2; dz++)
                {
                    if (Math.Abs(dx) == 2 && Math.Abs(dz) == 2 && rand.Next(3) != 0) continue;
                    int lx = x + dx;
                    int lz = z + dz;
                    if (lx < 0 || lx >= 16 || lz < 0 || lz >= 16) continue;
                    // baseY
                    int idx = (lx * 16 + lz) * height + baseY;
                    byte b = blocks[idx];
                    if (b == 0 || b == idLeaves) blocks[idx] = idLeaves;
                    // baseY+1
                    idx = (lx * 16 + lz) * height + (baseY + 1);
                    b = blocks[idx];
                    if (b == 0 || b == idLeaves) blocks[idx] = idLeaves;
                }
            }
            // Tiers: from baseY+2 to topY-1, alternating full 3x3 and plus-shape.
            for (int y = baseY + 2; y < topY; y++)
            {
                int dy = y - baseY;
                if (dy % 2 == 0)
                {
                    // full 3x3
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        for (int dz = -1; dz <= 1; dz++)
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
                else
                {
                    // plus-shape: centre + cardinals.
                    int[] dirs = { 0, 1, -1 };
                    foreach (int dz in dirs)
                    {
                        foreach (int dx in dirs)
                        {
                            if (dx == 0 && dz == 0) continue;
                            int lx = x + dx;
                            int lz = z + dz;
                            if (lx < 0 || lx >= 16 || lz < 0 || lz >= 16) continue;
                            int idx = (lx * 16 + lz) * height + y;
                            byte b = blocks[idx];
                            if (b == 0 || b == idLeaves) blocks[idx] = idLeaves;
                        }
                    }
                    // centre
                    {
                        int idx = (x * 16 + z) * height + y;
                        byte b = blocks[idx];
                        if (b == 0 || b == idLeaves) blocks[idx] = idLeaves;
                    }
                }
            }
            // Tip: single leaf at top.
            if (topY < height)
            {
                int idx = (x * 16 + z) * height + topY;
                byte b = blocks[idx];
                if (b == 0 || b == idLeaves) blocks[idx] = idLeaves;
            }
        }

        // A big, gnarled old oak: a tall trunk with a wide rounded dome canopy plus a few
        // hanging branch blobs. Grows anywhere a normal oak would (grass, dirt, red clay).
// A big, gnarled old oak: a tall trunk with a wide rounded dome canopy plus a few
        // hanging branch blobs. Grows anywhere a normal oak would (grass, dirt, red clay).
        private void GenerateBigOakTree(byte[] blocks, int x, int baseY, int z, Random rand,
            byte idWood, byte idLeaves, byte idGrass, byte idDirt, byte idRedClay)
        {
            const int height = ChunkManager.ChunkHeight;
            int trunkHeight = rand.Next(5) + 7;   // 7..11 - tall and old
            int topY = baseY + trunkHeight;

            // Clearance: wider canopy footprint near the top.
            for (int y = baseY; y <= topY + 1 && y < height; y++)
            {
                int radius = 1;
                if (y == baseY) radius = 0;
                else if (y >= topY - 2) radius = 3;
                else if (y >= topY - 5) radius = 2;
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

            // The ground must be grass, dirt, or red clay.
            if (baseY < 1) return;
            byte ground = blocks[(x * 16 + z) * height + (baseY - 1)];
            if (ground != idGrass && ground != idDirt && ground != idRedClay) return;
            if (ground == idGrass) blocks[(x * 16 + z) * height + (baseY - 1)] = idDirt;

            // Trunk.
            for (int i = 0; i < trunkHeight; i++)
            {
                int y = baseY + i;
                if (y < 0 || y >= height) break;
                int idx = (x * 16 + z) * height + y;
                byte b = blocks[idx];
                if (b == 0 || b == idLeaves) blocks[idx] = idWood;
            }

            // Wide rounded canopy: lower dome (radius 3), an upper tier (radius 2), and a cap.
            for (int y = topY - 3; y <= topY + 1 && y < height; y++)
            {
                if (y < 0) continue;
                int dy = y - topY;               // 0 at the top, negative below
                int radius = dy >= 0 ? 1 : dy == -1 ? 2 : 3;
                for (int dx = -radius; dx <= radius; dx++)
                {
                    for (int dz = -radius; dz <= radius; dz++)
                    {
                        // Round the corners so the dome isn't a square slab.
                        if (Math.Abs(dx) == radius && Math.Abs(dz) == radius && rand.Next(3) != 0) continue;
                        int lx = x + dx;
                        int lz = z + dz;
                        if (lx < 0 || lx >= 16 || lz < 0 || lz >= 16) continue;
                        int idx = (lx * 16 + lz) * height + y;
                        byte b = blocks[idx];
                        if (b == 0 || b == idLeaves) blocks[idx] = idLeaves;
                    }
                }
            }

            // Hanging branch blobs: small leaf clusters poking out partway down the trunk
            // for the classic gnarly silhouette.
            for (int i = 0; i < 3; i++)
            {
                int by = baseY + rand.Next(3, Math.Max(4, trunkHeight - 1));
                int bdx = (rand.Next(2) == 0 ? -1 : 1) * (2 + rand.Next(2));
                int bdz = (rand.Next(2) == 0 ? -1 : 1) * (2 + rand.Next(2));
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        int lx = x + bdx + dx;
                        int lz = z + bdz + dz;
                        if (lx < 0 || lx >= 16 || lz < 0 || lz >= 16) continue;
                        int idx = (lx * 16 + lz) * height + by;
                        byte b = blocks[idx];
                        if (b == 0 || b == idLeaves) blocks[idx] = idLeaves;
                    }
                }
            }
        }

        // A conifer for the Paradise foothills: tall trunk crowned with a layered cone of
        // leaves. Cone shape varies per tree — some have tight 1-block steps, others have
        // wider 2-block tiers. Grows on grass, dirt, or red clay.
        private void GeneratePineTree(byte[] blocks, int x, int baseY, int z, Random rand,
            byte idWood, byte idLeaves)
        {
            const int height = ChunkManager.ChunkHeight;
            int trunkHeight = rand.Next(12) + 8;   // 8..19 — no short pines

            // Each tree picks a step style: 1-block increments (dense) or 2-block tiers (layered).
            int stepSize = rand.Next(3) == 0 ? 2 : 1;

            // Slender pine profile — narrow base, gentle taper.
            int baseRadius = 1 + trunkHeight / 5;   // 2..4
            if (baseRadius > 4) baseRadius = 4;

            int topY = baseY + trunkHeight;

            // Bare trunk at the bottom (no branches), canopy covers the rest to the tip.
            int bareTrunk = Math.Max(2, trunkHeight / 3);
            int canopyStart = baseY + bareTrunk;
            int canopyH = topY - canopyStart;   // always reaches the tip

            // Clearance check: trunk + full canopy footprint must be air or leaves.
            for (int y = canopyStart; y <= topY && y < height; y++)
            {
                int dy = y - canopyStart;
                int radius = CanopyRadius(dy, canopyH, baseRadius, stepSize);
                for (int dx = -radius; dx <= radius; dx++)
                for (int dz = -radius; dz <= radius; dz++)
                {
                    if (dx * dx + dz * dz > radius * radius) continue;
                    int lx = x + dx, lz = z + dz;
                    if (lx < 0 || lx >= 16 || lz < 0 || lz >= 16 || y < 0 || y >= height) return;
                    byte b = blocks[(lx * 16 + lz) * height + y];
                    if (b != 0 && b != idLeaves) return;
                }
            }

            // Ground must be plantable.
            if (baseY < 1) return;
            byte ground = blocks[(x * 16 + z) * height + (baseY - 1)];
            if (ground != (byte)BlockRegistry.GetId("grass")
                && ground != (byte)BlockRegistry.GetId("dirt")
                && ground != (byte)BlockRegistry.GetId("redclay")) return;

            // Trunk — stops 2 blocks below the tip so the canopy covers the top.
            int trunkLogHeight = trunkHeight - 2;
            for (int i = 0; i < trunkLogHeight; i++)
            {
                int y = baseY + i;
                if (y >= height) break;
                int idx = (x * 16 + z) * height + y;
                if (blocks[idx] == 0 || blocks[idx] == idLeaves) blocks[idx] = idWood;
            }

            // Cone canopy — fully covers the trunk to the tip (no exposed log).
            // The bottom ~1/3 of the trunk stays bare like a real pine.
            for (int y = canopyStart; y <= topY && y < height; y++)
            {
                int dy = y - canopyStart;
                int radius = CanopyRadius(dy, canopyH, baseRadius, stepSize);
                if (radius < 0) radius = 0;

                for (int dx = -radius; dx <= radius; dx++)
                for (int dz = -radius; dz <= radius; dz++)
                {
                    if (dx * dx + dz * dz > radius * radius) continue;
                    if (Math.Abs(dx) == radius && Math.Abs(dz) == radius && rand.Next(3) != 0) continue;
                    int lx = x + dx, lz = z + dz;
                    if (lx < 0 || lx >= 16 || lz < 0 || lz >= 16) continue;
                    int idx = (lx * 16 + lz) * height + y;
                    if (blocks[idx] == 0) blocks[idx] = idLeaves;
                }
            }
        }

        /// <summary>Radius of the pine cone at a given height within the canopy.</summary>
        private static int CanopyRadius(int dy, int canopyH, int baseR, int step)
        {
            if (step == 1)
            {
                // Smooth cone: radius = baseR at bottom, shrinks linearly to 0 at top.
                return (int)(baseR * (canopyH - 1 - dy) / (double)(canopyH - 1));
            }
            else
            {
                // Stepped cone: each tier is 2 blocks tall, radius drops by 1 per tier.
                int tier = dy / 2;
                int maxTier = (canopyH - 1) / 2 + 1;
                return Math.Max(0, baseR - tier);
            }
        }

        public string BiomeNameAt(int worldX, int worldZ)
        {
            // Authoritative source: the biome map drives both terrain and label, so they agree.
            return _biomeMap.BiomeAt(worldX, worldZ).DisplayName;
        }

        /// <summary>The biome definition at a world column (spawn rules key on biome id).</summary>
        public BiomeDefinition BiomeAt(int worldX, int worldZ) => _biomeMap.BiomeAt(worldX, worldZ);

        private static double Lerp(double a, double b, double t)
        {
            return a + (b - a) * t;
        }

        /// <summary>
        /// Estimates the surface height (block Y, world coords) at a world column WITHOUT generating
        /// the chunk - cheap, used for teleport/preview where a fully-generated block scan is too
        /// slow. Uses the same biome-driven surface math as the density field (base height +
        /// relief variation). Slightly imprecise (the real surface is a noise-perturbed band around
        /// this line) but fine for dropping the player in nearby.
        /// </summary>
        public int EstimateSurfaceHeightAt(int worldX, int worldZ)
        {
            double xq = worldX / 4.0;
            double zq = worldZ / 4.0;
            double relief = _relief.Noise2DNormalized(xq * ReliefFrequency, zq * ReliefFrequency);

            // Must match the density field's biome-driven centerHeight exactly.
            double reliefShaped = relief / 1.0;
            if (reliefShaped < 0.0) reliefShaped = -reliefShaped;
            reliefShaped = reliefShaped * 2.6 - 2.6;
            if (reliefShaped < 0.0)
            {
                reliefShaped /= 2.4;
                if (reliefShaped < -1.0) reliefShaped = -1.0;
                reliefShaped /= 1.7;
                reliefShaped /= 2.4;
            }
            else
            {
                if (reliefShaped > 1.0) reliefShaped = 1.0;
                reliefShaped /= 5.2;
            }
            reliefShaped = reliefShaped * 17.0 / 16.0;

            var biome = _biomeMap.BiomeAt(worldX, worldZ);
            double centerHeight = biome.BaseHeight * 16.0 + reliefShaped * (biome.HeightVariation * 16.0);

            // Field-y -> block Y: field y = 1 unit per 8 blocks, so block Y = centerHeight * 8.
            // Terrain band local Y 0 maps to world -64.
            return ChunkManager.GroundOriginY + (int)Math.Round(centerHeight * 8.0);
        }
    }
}
