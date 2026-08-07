using System;

namespace CubeApp.World
{
    /// <summary>
    /// Hidden sky islands: rare floating landmasses placed FAR above the cloud deck (world Y
    /// SkyMin..SkyMax), invisible from the ground. The player would only ever find them by
    /// building up out of curiosity - which is exactly the point. Each island is a flat-topped
    /// floating chunk with a grass/dirt/stone sandwich and a tapered underside, like a piece of
    /// terrain ripped from the ground and suspended in the sky.
    ///
    /// Knobs (all seed-driven, deterministic per world):
    ///   Frequency - raw placement-noise threshold; higher = rarer (noise max ~2.3-2.6).
    ///   Size      - island radius in blocks.
    ///   Thickness - vertical height of the island body (grass + dirt + stone).
    ///   SkyMin/SkyMax - altitude band where islands live (default well above clouds at 128).
    ///
    /// Placement is a pure noise threshold: the noise scale is broad enough that each strong
    /// peak is a distinct region, so each qualifying column builds its own island disk. Raising
    /// the threshold toward the noise max (2.2+) leaves only a handful of islands per map.
    /// </summary>
    public sealed class SkyIslandSculptor
    {
        public bool Enabled = true;
        /// <summary>Raw placement-noise threshold; higher = rarer (noise max ~2.3-2.6).
        /// 2.0 = a few per map; 2.2 = only the rarest peaks.</summary>
        public float Frequency = 2.15f;
        /// <summary>Island radius in blocks.</summary>
        public float Size = 16f;
        /// <summary>Vertical thickness of the island body (grass + dirt + stone).</summary>
        public float Thickness = 14f;
        /// <summary>Altitude band: islands only exist in this world-Y range.</summary>
        public int SkyMin = 220;
        public int SkyMax = 350;

        private readonly InfdevOctaves _placement;
        private readonly InfdevOctaves _shape;
        private readonly InfdevOctaves _detail;

        public SkyIslandSculptor(int seed)
        {
            var rand = new Random(unchecked((int)(seed * 31 + 0x51AB3D)));
            _placement = new InfdevOctaves(rand, 2, 0); // broad 2D placement
            _shape = new InfdevOctaves(rand, 3, 0);     // island outline shaping
            _detail = new InfdevOctaves(rand, 4, 0);    // underside detail
        }

        public double DebugPlacement(int wx, int wz) => _placement.Noise2D(wx * 0.02, wz * 0.02);

        public void Sculpt(Chunk chunk, int terrainBandStart, int chunkSize, int chunkHeight)
        {
            if (!Enabled) return;
            const int originY = ChunkManager.WorldOriginY;

            for (int lx = 0; lx < chunkSize; lx++)
            {
                for (int lz = 0; lz < chunkSize; lz++)
                {
                    int wx = chunk.OriginX + lx;
                    int wz = chunk.OriginZ + lz;

                    // Placement: a strong noise peak spawns an island. Threshold toward the noise
                    // max keeps them rare and separated (broad scale = one island per peak region).
                    double p = _placement.Noise2D(wx * 0.02, wz * 0.02);
                    double strength = (p - Frequency) / (2.7 - Frequency);
                    if (strength <= 0.0) continue;
                    if (strength > 1.0) strength = 1.0;

                    // Altitude within the band, jittered per region so islands float at varied
                    // heights (never a uniform shelf).
                    double yNoise = _shape.Noise2D(wx * 0.02 + 37.1, wz * 0.02 - 11.7);
                    double altFrac = 0.5 + 0.5 * yNoise;
                    int islandTopY = SkyMin + (int)(altFrac * (SkyMax - SkyMin));

                    // Island size: noise-shaped, scaled by how far above threshold the peak is.
                    double rNoise = _shape.Noise2D(wx * 0.05, wz * 0.05);
                    double radius = Size * (0.7 + 0.6 * rNoise) * (0.4 + 0.6 * strength);
                    if (radius < 3.0) radius = 3.0;

                    BuildIsland(chunk, chunkSize, chunkHeight, originY,
                        lx, lz, wx, wz, islandTopY, (float)radius, strength);
                }
            }
        }

        // Draws one island disk centered on this column: a flat grassy top, a few dirt blocks,
        // then stone down to a tapered (rounded) underside. Underside hangs free - no connection
        // to terrain, so it reads as a true floating island.
        private void BuildIsland(Chunk chunk, int chunkSize, int chunkHeight, int originY,
            int lx, int lz, int wx, int wz, int topY, float radius, double strength)
        {
            int idGrass = BlockRegistry.GetId("grass");
            int idDirt = BlockRegistry.GetId("dirt");
            int idStone = BlockRegistry.GetId("stone");

            int r = (int)Math.Ceiling(radius);
            for (int oy = -r; oy <= r; oy++)
            {
                for (int ox = -r; ox <= r; ox++)
                {
                    int nx = lx + ox;
                    int nz = lz + oy;
                    if (nx < 0 || nx >= chunkSize || nz < 0 || nz >= chunkSize) continue;

                    double dist = Math.Sqrt(ox * ox + oy * oy);
                    if (dist > radius) continue;

                    // Horizontal profile: flat out to ~85% radius, then falls off quickly at the rim.
                    double edge = dist / radius;
                    if (edge > 0.85)
                    {
                        double rim = (edge - 0.85) / 0.15;
                        double n = _detail.Noise3D(wx * 0.1, topY * 0.1, wz * 0.1);
                        if (rim > 0.55 && n < 0.4) continue;
                    }

                    int thickness = (int)(Thickness * (0.7 + 0.6 * strength));
                    for (int dy = 0; dy <= thickness; dy++)
                    {
                        int wy = topY - dy;
                        if (wy < SkyMin - 8 || wy > SkyMax) continue;
                        int localY = wy - originY;
                        if (localY < 0 || localY >= chunkHeight) continue;

                        // Vertical profile: the underside tapers in with depth, so the bottom is
                        // narrower than the top (floating-island silhouette).
                        double depthFrac = (double)dy / Math.Max(1, thickness);
                        double underCut = Math.Max(0.0, depthFrac - 0.5) * 2.0;
                        double radAtDepth = radius * (1.0 - 0.45 * underCut);
                        if (dist > radAtDepth) continue;

                        // Materials: grass top, dirt for the next 3-4, stone below.
                        int block = dy == 0 ? idGrass : (dy <= 4 ? idDirt : idStone);
                        // Underside detail: occasional notch so it's not a perfect dome.
                        if (depthFrac > 0.7)
                        {
                            double n = _detail.Noise3D(wx * 0.08, wy * 0.08, wz * 0.08);
                            if (n > 0.55 && depthFrac > 0.85 && dist > radius * 0.5) continue;
                        }
                        chunk[nx, localY, nz] = block;
                    }
                }
            }
        }
    }
}
