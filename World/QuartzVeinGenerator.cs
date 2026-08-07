using System;

namespace CubeApp.World
{
    /// <summary>
    /// Sedimentary quartz veins: huge underground quartz strata that follow the terrain surface,
    /// like visible rock bands in a real cliff face. For each column the vein sits a few blocks
    /// below the surface, rises and falls with the hills (same broad noise scale as the terrain),
    /// has noise-driven thickness and waviness, and only fills stone (caves/air pockets stay,
    /// dirt/grass untouched).
    ///
    /// Where veins appear is controlled by a smooth low-frequency "host" field (not a per-chunk
    /// roll), so veins form large organic regions that span chunk borders and peter out
    /// naturally, like real strata.
    /// </summary>
    public sealed class QuartzVeinGenerator
    {
        public bool Enabled = true;
        /// <summary>Fraction of the terrain that hosts veins (0..1).</summary>
        public float Frequency = 0.48f;
        /// <summary>Average depth of the vein's center below the surface (blocks).</summary>
        public float BaseDepth = 18f;
        /// <summary>Average vein thickness (blocks).</summary>
        public float BaseThickness = 6f;
        /// <summary>How much the vein's depth undulates (blocks).</summary>
        public float DepthWaviness = 8f;
        /// <summary>Fraction of stone blocks converted (0..1); less than 1 leaves natural gaps.</summary>
        public float Fill = 0.8f;

        private readonly InfdevOctaves _hostNoise;      // low freq: where veins exist at all
        private readonly InfdevOctaves _depthNoise;     // follows terrain: depth of the band
        private readonly InfdevOctaves _thicknessNoise; // medium freq: band thickness
        private readonly InfdevOctaves _presenceNoise;  // high freq: per-block scatter

        public QuartzVeinGenerator(int seed)
        {
            var rand = new Random(unchecked((int)(seed * 13 + 0x5A41B3)));
            _hostNoise = new InfdevOctaves(rand, 2, 0);
            _depthNoise = new InfdevOctaves(rand, 3, 0);
            _thicknessNoise = new InfdevOctaves(rand, 2, 0);
            _presenceNoise = new InfdevOctaves(rand, 4, 0);
        }

        public void Generate(Chunk chunk, int chunkX, int chunkZ, int terrainBandStart, int chunkSize, int chunkHeight)
        {
            if (!Enabled) return;

            int idStone = BlockRegistry.GetId("stone");
            int idQuartz = BlockRegistry.GetId("quartz");
            const int originY = ChunkManager.GroundOriginY;

            // Maps Frequency (0..1) to a threshold on the -1..1 host field: higher Frequency =>
            // lower threshold => more columns host veins. Default 0.4 => threshold 0.2.
            double hostThreshold = 1.0 - 2.0 * Frequency;

            for (int lx = 0; lx < chunkSize; lx++)
            {
                for (int lz = 0; lz < chunkSize; lz++)
                {
                    int wx = chunk.OriginX + lx;
                    int wz = chunk.OriginZ + lz;

                    // Smooth region mask: veins exist in broad organic patches.
                    if (_hostNoise.Noise2DNormalized(wx * 0.012, wz * 0.012) < hostThreshold)
                        continue;

                    // Surface = topmost non-air block in the band (world Y).
                    int surfaceY = FindSurfaceY(chunk, lx, lz, terrainBandStart, chunkHeight);
                    if (surfaceY <= originY + 2) continue; // column has no real ground

                    // Vein center's depth below the surface, undulating on a broad scale so the
                    // band rides the hills (rises and falls with the terrain).
                    double depthNoise = _depthNoise.Noise2DNormalized(wx * 0.008, wz * 0.008);
                    double centerDepth = BaseDepth + depthNoise * DepthWaviness;
                    if (centerDepth < 2.0) centerDepth = 2.0;

                    double thickNoise = _thicknessNoise.Noise2DNormalized(wx * 0.02, wz * 0.02);
                    double thickness = BaseThickness * (0.7 + 0.3 * thickNoise);
                    if (thickness < 1.5) thickness = 1.5;

                    int veinTopY = surfaceY - (int)(centerDepth - thickness * 0.5);
                    int veinBottomY = surfaceY - (int)(centerDepth + thickness * 0.5);
                    if (veinTopY > surfaceY) veinTopY = surfaceY;

                    for (int wy = veinTopY; wy >= veinBottomY; wy--)
                    {
                        int localY = wy - originY;
                        if (localY < 0 || localY >= chunkHeight) continue;

                        // Only replace stone (caves/air stay as pockets; dirt/grass untouched).
                        int id = chunk[lx, localY, lz];
                        if (id != idStone) continue;

                        // High-frequency scatter: leave some stone for a natural look.
                        double p = _presenceNoise.Noise3DNormalized(wx * 0.15, wy * 0.15, wz * 0.15);
                        if (p < 1.0 - 2.0 * Fill) continue;

                        chunk[lx, localY, lz] = idQuartz;
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
