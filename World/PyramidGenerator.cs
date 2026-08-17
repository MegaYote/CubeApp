using System;

namespace Cubuild.World
{
    /// <summary>
    /// The Great Pyramid: a single colossal solid-brick pyramid that exists exactly once per
    /// world. Its location and brick color are derived purely from the world seed, so a given
    /// world always has one - always in the same spot, always far from spawn. It is hundreds of
    /// blocks tall (bigger than any mountain) and contains nothing but bricks. It has no
    /// purpose other than to confuse and mystify.
    ///
    /// Runs LAST in chunk generation so its volume is pure solid brick - any trees, caves, or
    /// terrain inside the footprint get swallowed. The pyramid base anchors to the real ground,
    /// so it rises out of whatever terrain it lands on (including the sea floor).
    /// </summary>
    public sealed class PyramidGenerator
    {
        private readonly int _centerWorldX;
        private readonly int _centerWorldZ;
        private readonly int _halfWidth; // half the base width in blocks
        private readonly int _height;    // blocks above the ground at the peak
        private readonly int _brickId;
        private readonly bool _exists;   // most worlds simply don't get a Great Pyramid

        public PyramidGenerator(int seed)
        {
            var rand = new Random(unchecked((int)(seed ^ 0x9E3779B9)));

            // The Great Pyramid is the rarest thing in the world: only ~15% of seeds ever get
            // one. Most worlds have none at all.
            _exists = rand.NextDouble() < 0.15;

            // Place it well outside spawn range so it's a genuine rare discovery, but not so far
            // that a determined explorer can never reach it.
            double distance = 2500.0 + rand.NextDouble() * 3500.0; // 2.5k..6k blocks out
            double angle = rand.NextDouble() * Math.PI * 2.0;
            _centerWorldX = (int)Math.Round(Math.Cos(angle) * distance);
            _centerWorldZ = (int)Math.Round(Math.Sin(angle) * distance);

            _halfWidth = 190; // 380-block base - wider than a mountain
            _height = 320;    // towers above the 128-block terrain band and the skyline

            // A random brick color for this world's pyramid.
            string[] bricks =
            {
                "bricks", "bluebrick", "greenbrick", "yellowbrick",
                "pinkbrick", "cyanbrick", "blackbrick", "whitebrick",
                "purple_bricks", "orange_bricks", "gray_bricks", "cobble_bricks"
            };
            _brickId = BlockRegistry.GetId(bricks[rand.Next(bricks.Length)]);
        }

        /// <summary>Whether this world has a Great Pyramid at all (most don't).</summary>
        public bool Exists => _exists;

        /// <summary>The world position of the pyramid's center column (a finding aid).</summary>
        public (int X, int Z) Center => (_centerWorldX, _centerWorldZ);

        /// <summary>Half the base width in blocks (base spans Center ± HalfWidth).</summary>
        public int HalfWidth => _halfWidth;

        /// <summary>Height of the peak above the terrain it stands on, in blocks.</summary>
        public int Height => _height;

        public void Generate(Chunk chunk, int chunkX, int chunkZ, int terrainBandStart, int chunkSize, int chunkHeight)
        {
            if (!_exists) return;

            // Quick bounding-box reject for chunks outside the pyramid footprint.
            int chunkWorldX = chunkX * chunkSize;
            int chunkWorldZ = chunkZ * chunkSize;
            if (chunkWorldX + chunkSize <= _centerWorldX - _halfWidth) return;
            if (chunkWorldX >= _centerWorldX + _halfWidth) return;
            if (chunkWorldZ + chunkSize <= _centerWorldZ - _halfWidth) return;
            if (chunkWorldZ >= _centerWorldZ + _halfWidth) return;

            const int originY = ChunkManager.WorldOriginY; // -64
            int worldTop = originY + chunkHeight - 1;

            for (int lx = 0; lx < chunkSize; lx++)
            {
                for (int lz = 0; lz < chunkSize; lz++)
                {
                    int wx = chunkWorldX + lx;
                    int wz = chunkWorldZ + lz;
                    int r = Math.Max(Math.Abs(wx - _centerWorldX), Math.Abs(wz - _centerWorldZ));
                    if (r >= _halfWidth) continue;

                    // Anchor the base to the real terrain surface under this column so the
                    // pyramid grows out of the ground instead of floating.
                    int baseWy = originY - 1;
                    for (int ly = terrainBandStart + 127; ly >= terrainBandStart; ly--)
                    {
                        if (chunk[lx, ly, lz] != BlockRegistry.AirId)
                        {
                            baseWy = originY + ly;
                            break;
                        }
                    }

                    // Square-pyramid surface at this column: full height at the center, zero at
                    // the base edge.
                    int surfaceWy = baseWy + (int)(_height * (1.0 - (double)r / _halfWidth));
                    if (surfaceWy > worldTop) surfaceWy = worldTop;
                    if (surfaceWy <= baseWy) continue;

                    for (int wy = baseWy + 1; wy <= surfaceWy; wy++)
                    {
                        int localY = wy - originY;
                        if (localY < 0 || localY >= chunkHeight) break;
                        chunk[lx, localY, lz] = _brickId;
                    }
                }
            }
        }
    }
}
