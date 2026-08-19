using System;

namespace Cubuild.World
{
    /// <summary>
    /// Serpentine vein bands: quartz-style strata of dark serpentine stone, but biome-weighted -
    /// much more frequent inside the paradise biome. Gold ore forms at the contact zones: wherever
    /// a serpentine cell overlaps a quartz-vein cell (same deterministic field logic, no chunk
    /// border seams), the touching cell becomes GOLD ORE instead - quartz and serpentine meet,
    /// and the meeting point is gilded.
    /// </summary>
    public sealed class SerpentineGenerator
    {
        public bool Enabled = true;
        /// <summary>Fraction of the terrain that hosts veins outside paradise (0..1).</summary>
        public float Frequency = 0.34f;
        /// <summary>Frequency multiplier inside the paradise biome.</summary>
        public float ParadiseFrequencyMultiplier = 2.5f;
        /// <summary>Average depth of the vein's center below the surface (blocks).</summary>
        public float BaseDepth = 14f;
        /// <summary>Average vein thickness (blocks).</summary>
        public float BaseThickness = 5f;
        /// <summary>How much the vein's depth undulates (blocks).</summary>
        public float DepthWaviness = 7f;
        /// <summary>Fraction of stone blocks converted (0..1); less than 1 leaves natural gaps.</summary>
        public float Fill = 0.75f;
        /// <summary>Biome id that gets the boosted serpentine frequency.</summary>
        public string ParadiseBiomeId = "paradise";

        private readonly NoiseOctaves _hostNoise;      // low freq: where veins exist at all
        private readonly NoiseOctaves _depthNoise;     // follows terrain: depth of the band
        private readonly NoiseOctaves _thicknessNoise; // medium freq: band thickness
        private readonly NoiseOctaves _presenceNoise;  // high freq: per-block scatter

        public SerpentineGenerator(int seed)
        {
            var rand = new Random(unchecked((int)(seed * 17 + 0xB16B00B5)));
            _hostNoise = new NoiseOctaves(rand, 2, 0);
            _depthNoise = new NoiseOctaves(rand, 3, 0);
            _thicknessNoise = new NoiseOctaves(rand, 2, 0);
            _presenceNoise = new NoiseOctaves(rand, 4, 0);
        }

        public void Generate(Chunk chunk, int chunkX, int chunkZ, int terrainBandStart, int chunkSize, int chunkHeight,
            Func<int, int, string> biomeIdAt, QuartzVeinGenerator quartz)
        {
            if (!Enabled) return;

            int idStone = BlockRegistry.GetId("stone");
            int idSerpentine = BlockRegistry.GetId("serpentine");
            int idGold = BlockRegistry.GetId("goldore");
            const int originY = ChunkManager.GroundOriginY;

            for (int lx = 0; lx < chunkSize; lx++)
            {
                for (int lz = 0; lz < chunkSize; lz++)
                {
                    int wx = chunk.OriginX + lx;
                    int wz = chunk.OriginZ + lz;

                    // Biome-weighted host mask: paradise columns host veins far more often.
                    bool paradise = string.Equals(biomeIdAt(wx, wz), ParadiseBiomeId, StringComparison.OrdinalIgnoreCase);
                    double hostThreshold = 1.0 - 2.0 * Frequency * (paradise ? ParadiseFrequencyMultiplier : 1.0);
                    if (_hostNoise.Noise2DNormalized(wx * 0.012, wz * 0.012) < hostThreshold)
                        continue;

                    // Surface = topmost non-air block in the band (world Y).
                    int surfaceY = FindSurfaceY(chunk, lx, lz, terrainBandStart, chunkHeight);
                    if (surfaceY <= originY + 2) continue; // column has no real ground

                    double depthNoise = _depthNoise.Noise2DNormalized(wx * 0.008, wz * 0.008);
                    double centerDepth = BaseDepth + depthNoise * DepthWaviness;
                    if (centerDepth < 2.0) centerDepth = 2.0;

                    double thickNoise = _thicknessNoise.Noise2DNormalized(wx * 0.02, wz * 0.02);
                    double thickness = BaseThickness * (0.7 + 0.3 * thickNoise);
                    if (paradise) thickness *= 1.6; // paradise veins run thicker too
                    if (thickness < 1.5) thickness = 1.5;

                    int veinTopY = surfaceY - (int)(centerDepth - thickness * 0.5);
                    int veinBottomY = surfaceY - (int)(centerDepth + thickness * 0.5);
                    if (veinTopY > surfaceY) veinTopY = surfaceY;

                    for (int wy = veinTopY; wy >= veinBottomY; wy--)
                    {
                        int localY = wy - originY;
                        if (localY < 0 || localY >= chunkHeight) continue;

                        // Only replace stone (caves/air stay as pockets; dirt/grass untouched).
                        if (chunk[lx, localY, lz] != idStone) continue;

                        // High-frequency scatter: leave some stone for a natural look.
                        double p = _presenceNoise.Noise3DNormalized(wx * 0.15, wy * 0.15, wz * 0.15);
                        if (p < 1.0 - 2.0 * Fill) continue;

                        // Contact rule: wherever serpentine meets quartz, the touching cell is
                        // gilded. The quartz test is deterministic (pure noise + column shape),
                        // so contact zones are seamless across chunk borders.
                        if (quartz.WouldPlaceQuartz(chunk, lx, lz, wx, wy, wz, terrainBandStart, chunkSize, chunkHeight))
                            chunk[lx, localY, lz] = idGold;
                        else
                            chunk[lx, localY, lz] = idSerpentine;
                    }
                }
            }
        }

        private static int FindSurfaceY(Chunk chunk, int lx, int lz, int terrainBandStart, int chunkHeight)
        {
            for (int ly = terrainBandStart + 127; ly >= terrainBandStart; ly--)
            {
                if (chunk[lx, ly, lz] != BlockRegistry.AirId) return ChunkManager.GroundOriginY + ly;
            }
            return ChunkManager.GroundOriginY - 1;
        }
    }
}