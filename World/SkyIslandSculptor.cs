using System;
using System.Collections.Concurrent;

namespace CubeApp.World
{
    /// <summary>
    /// Hidden sky islands: rare floating landmasses placed FAR above the cloud deck, invisible
    /// from the ground. The player would only ever find them by building up out of curiosity -
    /// which is exactly the point. Each island is a flat-topped floating chunk with a
    /// grass/dirt/stone sandwich and a tapered underside, like a piece of terrain ripped from
    /// the ground and suspended in the sky.
    ///
    /// LAZY (mirror of the deep-fill trick): islands live in the stratosphere (world
    /// HighMin..HighMax, default 512..1000 - far above even the cloud deck). At generation time
    /// the upper region is pure air, so chunk gen stays cheap. <see cref="HighFillChunk"/> fills
    /// a chunk's upper zone with islands only when the player gets close (Program calls it from
    /// UpdateHighFill), and each chunk is filled exactly once (tracked in _filled).
    ///
    /// Knobs (all seed-driven, deterministic per world):
    ///   Frequency - raw placement-noise threshold; higher = rarer (noise max ~2.3-2.6).
    ///   Size      - island radius in blocks.
    ///   Thickness - vertical height of the island body (grass + dirt + stone).
    ///   HighMin/HighMax - stratosphere altitude band where islands live.
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
        /// <summary>Stratosphere altitude band: islands only exist in this world-Y range.</summary>
        public int HighMin = 512;
        public int HighMax = 1000;

        /// <summary>True when HighFillChunk should also run at generation time (player is high).
        /// Program sets this like AutoDeepFill so new chunks are born with their islands.</summary>
        public bool AutoHighFill { get; set; }

        private readonly ConcurrentDictionary<ChunkCoordinates, byte> _filled = new();
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

        /// <summary>Fills the stratosphere of a chunk (world HighMin..HighMax) with islands.
        /// One-shot per chunk, like DeepFillChunk. Cheap: only columns whose placement noise is
        /// above threshold build anything, and most are far below it.</summary>
        public void HighFillChunk(int chunkX, int chunkZ, Chunk chunk, int chunkSize, int chunkHeight)
        {
            if (!Enabled) return;
            var key = new ChunkCoordinates(chunkX, chunkZ);
            if (!_filled.TryAdd(key, 0)) return; // already filled once

            const int originY = ChunkManager.WorldOriginY;
            bool wroteAnything = false;
            for (int lx = 0; lx < chunkSize; lx++)
            {
                for (int lz = 0; lz < chunkSize; lz++)
                {
                    int wx = chunk.OriginX + lx;
                    int wz = chunk.OriginZ + lz;

                    double p = _placement.Noise2D(wx * 0.02, wz * 0.02);
                    double strength = (p - Frequency) / (2.7 - Frequency);
                    if (strength <= 0.0) continue;
                    if (strength > 1.0) strength = 1.0;

                    double yNoise = _shape.Noise2D(wx * 0.02 + 37.1, wz * 0.02 - 11.7);
                    double altFrac = 0.5 + 0.5 * yNoise;
                    int islandTopY = HighMin + (int)(altFrac * (HighMax - HighMin));

                    double rNoise = _shape.Noise2D(wx * 0.05, wz * 0.05);
                    double radius = Size * (0.7 + 0.6 * rNoise) * (0.4 + 0.6 * strength);
                    if (radius < 3.0) radius = 3.0;

                    if (BuildIsland(chunk, chunkSize, chunkHeight, originY,
                        lx, lz, wx, wz, islandTopY, (float)radius, strength))
                    {
                        wroteAnything = true;
                    }
                }
            }
            if (wroteAnything) chunk.NeedsRemesh = true;
        }

        // Draws one island disk centered on this column: a flat grassy top, a few dirt blocks,
        // then stone down to a tapered (rounded) underside. Underside hangs free - no connection
        // to terrain, so it reads as a true floating island. Returns true if it wrote any block.
        private bool BuildIsland(Chunk chunk, int chunkSize, int chunkHeight, int originY,
            int lx, int lz, int wx, int wz, int topY, float radius, double strength)
        {
            int idGrass = BlockRegistry.GetId("grass");
            int idDirt = BlockRegistry.GetId("dirt");
            int idStone = BlockRegistry.GetId("stone");
            bool wrote = false;

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
                        if (wy < HighMin - 8 || wy > HighMax) continue;
                        int localY = wy - originY;
                        if (localY < 0 || localY >= chunkHeight) continue;

                        double depthFrac = (double)dy / Math.Max(1, thickness);
                        double underCut = Math.Max(0.0, depthFrac - 0.5) * 2.0;
                        double radAtDepth = radius * (1.0 - 0.45 * underCut);
                        if (dist > radAtDepth) continue;

                        int block = dy == 0 ? idGrass : (dy <= 4 ? idDirt : idStone);
                        if (depthFrac > 0.7)
                        {
                            double n = _detail.Noise3D(wx * 0.08, wy * 0.08, wz * 0.08);
                            if (n > 0.55 && depthFrac > 0.85 && dist > radius * 0.5) continue;
                        }
                        chunk[nx, localY, nz] = block;
                        wrote = true;
                    }
                }
            }
            return wrote;
        }
    }
}
