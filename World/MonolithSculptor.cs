using System;

namespace CubeApp.World
{
    /// <summary>
    /// Controllable monolith feature. Infdev's real terrain noise accidentally produces
    /// "monoliths" (tall stone towers) when the relief noise spikes while the 3D body noise is
    /// positive high up, and the same noise's valleys hollow their bases into floating islands.
    /// Rather than rely on that accident, this post-pass sculpts them deliberately from a
    /// dedicated seeded noise field with tunable knobs:
    ///
    ///   Frequency  - how many monolith seed points exist (a 2D noise threshold).
    ///   Size       - base radius of each monolith in blocks.
    ///   Height     - how tall the towers reach above the local terrain.
    ///   SlabTaper  - 1 = columns are fat all the way up; lower values taper to a point.
    ///   Carve      - how aggressively the base is hollowed out (floating-island look).
    ///   CarveNoise - the base hollow follows a high-frequency noise so it reads organic.
    ///
    /// Everything is derived from the world seed, so a given world always produces the same
    /// monoliths. Runs AFTER terrain + caves so the towers stand on/around real ground.
    /// </summary>
    public sealed class MonolithSculptor
    {
        public bool Enabled = true;
        /// <summary>Raw placement-noise threshold (noise range ~-2.6..2.65). Higher = rarer.
        /// ~1.4 common, ~1.75 rare, ~2.0 very rare, ~2.55 = only the tallest few peaks in a
        /// thousand-block area (legendary). Values above ~2.66 disable monoliths entirely.</summary>
        public float Frequency = 2.55f;

        /// <summary>Raw placement noise at a world column (debug / tuning helper).</summary>
        public double PlacementValue(int wx, int wz) => _placement.Noise2D(wx * 0.02, wz * 0.02);        /// <summary>Base radius in blocks (scaled smoothly by the noise field).</summary>
        public float Size = 7.0f;
        /// <summary>Maximum height above local terrain in blocks.</summary>
        public float Height = 60f;
        /// <summary>1 = full width to the top; ~0.3 = towers taper to a point.</summary>
        public float SlabTaper = 0.7f;
        /// <summary>0 = no carving; 1 = aggressive floating-island hollowing.</summary>
        public float Carve = 0.6f;
        /// <summary>How noisy the carve surface is (organic vs clean).</summary>
        public float CarveNoise = 1.0f;

        private readonly int _seed;
        private readonly InfdevOctaves _placement;
        private readonly InfdevOctaves _carve;
        private readonly InfdevOctaves _shade;

        public MonolithSculptor(int seed)
        {
            _seed = seed;
            var rand = new Random(unchecked((int)(seed ^ 0x9E3779B9)));
            _placement = new InfdevOctaves(rand, 2, 0); // broad 2D placement
            _carve = new InfdevOctaves(rand, 4, 0);     // medium-frequency carve noise
            _shade = new InfdevOctaves(rand, 4, 0);     // radius/height shaping noise
        }

        public void Sculpt(Chunk chunk, int terrainBandStart, int chunkSize, int chunkHeight)
        {
            if (!Enabled) return;

            int idStone = BlockRegistry.GetId("stone");
            const int originY = ChunkManager.WorldOriginY;
            // Monoliths may rise far above the terrain band (world -64..63) into the sky region
            // (world 64..191) - that's the whole point. Cap at the world top, not the band top.
            int worldTop = originY + ChunkManager.ChunkHeight - 1;

            for (int lx = 0; lx < chunkSize; lx++)
            {
                for (int lz = 0; lz < chunkSize; lz++)
                {
                    int wx = chunk.OriginX + lx;
                    int wz = chunk.OriginZ + lz;

                    // Placement: a smooth 2D noise decides whether this column hosts a monolith.
                    // The noise ranges roughly -2..+1.5, so normalize to 0..1 before comparing
                    // against the Frequency threshold (which is 0..1: lower = more monoliths).
                    // Placement: a smooth 2D noise decides whether this column hosts a monolith.
                    // Compare the RAW noise to the Frequency threshold (noise spans ~-2.6..2.5;
                    // higher threshold = rarer peaks). strength 0..1 scales size/height.
                    double p = _placement.Noise2D(wx * 0.02, wz * 0.02);
                    double strength = (p - Frequency) / (2.6 - Frequency);
                    if (strength <= 0.0) continue;
                    if (strength > 1.0) strength = 1.0;

                    // Find local terrain top (world Y) below the band top.
                    int surfaceY = FindSurfaceY(chunk, lx, lz, terrainBandStart, chunkSize, chunkHeight);
                    if (surfaceY <= originY) continue;

                    // Size + height shaped by noise so monoliths aren't identical.
                    double size = Size * (0.5 + 0.5 * _shade.Noise2D(wx * 0.05, wz * 0.05)) * strength;
                    if (size < 0.5) size = 0.5;
                    double height = Height * (0.6 + 0.8 * _shade.Noise2D(wx * 0.11 + 13.7, wz * 0.11 + 7.3)) * strength;
                    if (height < 4.0) height = 4.0;

                    int topY = surfaceY + (int)height;
                    if (topY > worldTop) topY = worldTop;

                    // Carve the base BEFORE building so the hollow reads as floating-island.
                    if (Carve > 0.01f)
                    {
                        CarveBase(chunk, chunkSize, chunkHeight, terrainBandStart,
                            lx, lz, wx, wz, surfaceY, (float)size, strength);
                    }

                    // Build the tower: each column gets a radius from the center; the shape falls
                    // off with distance from the monolith center column, and (optionally) tapers
                    // near the top. The monolith is a smooth 2D blob cross-section extruded up.
                    for (int oy = -8; oy <= 8; oy++)
                    for (int ox = -8; ox <= 8; ox++)
                    {
                        int nx = lx + ox;
                        int nz = lz + oy;
                        if (nx < 0 || nx >= chunkSize || nz < 0 || nz >= chunkSize) continue;

                        double dist = Math.Sqrt(ox * ox + oy * oy);
                        if (dist > size) continue;

                        double frac = 1.0 - dist / size; // 1 at center, 0 at edge
                        for (int wy = surfaceY; wy <= topY; wy++)
                        {
                            // Taper near the top: radius shrinks upward when SlabTaper<1.
                            double hf = (double)(wy - surfaceY) / Math.Max(1.0, topY - surfaceY);
                            double radAtH = size * (SlabTaper + (1.0 - SlabTaper) * (1.0 - hf));
                            if (dist <= radAtH)
                            {
                                int localY = wy - originY;
                                if (localY >= 0 && localY < chunkHeight)
                                {
                                    chunk[nx, localY, nz] = idStone;
                                }
                            }
                        }
                    }
                }
            }
        }

        // Finds the highest non-air block in a column within the terrain band, world Y.
        private static int FindSurfaceY(Chunk chunk, int lx, int lz, int terrainBandStart, int chunkSize, int chunkHeight)
        {
            for (int ly = terrainBandStart + 127; ly >= terrainBandStart; ly--)
            {
                int id = chunk[lx, ly, lz];
                if (id != BlockRegistry.AirId) return ChunkManager.WorldOriginY + ly;
            }
            return ChunkManager.WorldOriginY - 1;
        }

        // Hollows the base of the monolith like the "carved out" floating-island look: removes a
        // wide gap of stone AROUND the tower base (radius grows with Carve), so the monolith
        // stands on a narrowing stalk or floats above a hollow - the Infdev noise-valley effect.
        private void CarveBase(Chunk chunk, int chunkSize, int chunkHeight, int terrainBandStart,
            int lx, int lz, int wx, int wz, int surfaceY, float size, double strength)
        {
            int idAir = BlockRegistry.AirId;
            const int originY = ChunkManager.WorldOriginY;
            // Carve a ring around the tower: inner radius just outside the base, outer grows with
            // Carve. Depth is deepest at the ring center and tapers toward the edges.
            float innerR = size * 0.9f;
            float outerR = size * (1.6f + Carve * 2.5f);
            int carveTop = surfaceY - 1;

            for (int oy = -24; oy <= 24; oy++)
            for (int ox = -24; ox <= 24; ox++)
            {
                int nx = lx + ox;
                int nz = lz + oy;
                if (nx < 0 || nx >= chunkSize || nz < 0 || nz >= chunkSize) continue;

                double dist = Math.Sqrt(ox * ox + oy * oy);
                if (dist < innerR || dist > outerR) continue;

                // Depth profile: deepest in the middle of the ring, shallow at both edges.
                double ringFrac = (dist - innerR) / Math.Max(1e-5, outerR - innerR);
                double depth = (1.0 - Math.Abs(ringFrac - 0.5) * 2.0) * (6.0 + 14.0 * Carve) * strength;
                depth += 2.0;
                if (depth < 3.0) depth = 3.0;

                for (double dy = 1; dy <= depth; dy++)
                {
                    int wy = carveTop - (int)dy;
                    if (wy < originY) break;
                    int localY = wy - originY;
                    if (localY < 0 || localY >= chunkHeight) break;
                    // Organic edge: high-freq noise can leave fingers/pillars, like the real
                    // noise-valley carving.
                    double n = _carve.Noise3D(wx * 0.15, wy * 0.15, wz * 0.15) * CarveNoise;
                    if (n < -0.25) continue; // noise keeps a pillar here
                    chunk[nx, localY, nz] = idAir;
                }
            }
        }
    }
}
