using System;
using System.Collections.Generic;

namespace CubeApp.World
{
    /// <summary>
    /// Regular pyramids: small, geometrically-perfect brick pyramids. Like the Great Pyramid,
    /// each one exists exactly ONCE per world - a fixed, seed-derived set of sites (typically
    /// 2..5 pyramids, each a unique location and random brick color). No scattering, no
    /// repetition: every pyramid is a deliberate monument.
    ///
    /// Each base sits on a perfectly level plane anchored to the terrain at its center, and
    /// every face is an exact planar slope, so the geometry is crisp and flawless.
    /// </summary>
    public sealed class RegularPyramidGenerator
    {
        private readonly List<PyramidSpec> _pyramids = new();

        /// <summary>The fixed set of regular pyramids in this world (seed-derived, once per world).</summary>
        public IReadOnlyList<PyramidSpec> Pyramids => _pyramids;

        public RegularPyramidGenerator(int seed)
        {
            var rand = new Random(unchecked((int)(seed ^ 0x51ED270B)));

            // A world has a small fixed set of regular pyramids - each one unique and singular.
            int count = 2 + rand.Next(4); // 2..5
            string[] bricks =
            {
                "bricks", "bluebrick", "greenbrick", "yellowbrick",
                "pinkbrick", "cyanbrick", "blackbrick", "whitebrick",
                "purple_bricks", "orange_bricks", "gray_bricks", "cobble_bricks"
            };

            for (int i = 0; i < count; i++)
            {
                // Scattered at a moderate range so they're real discoveries, not skyline clutter.
                double distance = 900.0 + rand.NextDouble() * 2500.0; // 900..3400 blocks out
                double angle = rand.NextDouble() * Math.PI * 2.0;
                int cx = (int)Math.Round(Math.Cos(angle) * distance);
                int cz = (int)Math.Round(Math.Sin(angle) * distance);

                int halfWidth = 18 + rand.Next(24);   // 18..41
                int height = (int)(halfWidth * (0.9 + rand.NextDouble() * 0.4)); // ~0.9..1.3x
                int brickId = BlockRegistry.GetId(bricks[rand.Next(bricks.Length)]);

                _pyramids.Add(new PyramidSpec(cx, cz, halfWidth, height, brickId));
            }
        }

        public void Generate(Chunk chunk, int chunkX, int chunkZ, int terrainBandStart, int chunkSize,
            int chunkHeight, Func<int, int, int> surfaceEstimator)
        {
            int chunkWorldX = chunkX * chunkSize;
            int chunkWorldZ = chunkZ * chunkSize;
            int worldTop = ChunkManager.WorldOriginY + chunkHeight - 1;

            foreach (var spec in _pyramids)
            {
                int minWX = spec.CenterX - spec.HalfWidth;
                int maxWX = spec.CenterX + spec.HalfWidth;
                int minWZ = spec.CenterZ - spec.HalfWidth;
                int maxWZ = spec.CenterZ + spec.HalfWidth;
                if (chunkWorldX + chunkSize <= minWX) continue;
                if (chunkWorldX > maxWX) continue;
                if (chunkWorldZ + chunkSize <= minWZ) continue;
                if (chunkWorldZ > maxWZ) continue;

                // Perfectly level base: anchored to the terrain estimate at the center column.
                int baseWy = surfaceEstimator(spec.CenterX, spec.CenterZ) + 1;

                for (int lx = 0; lx < chunkSize; lx++)
                {
                    for (int lz = 0; lz < chunkSize; lz++)
                    {
                        int wx = chunkWorldX + lx;
                        int wz = chunkWorldZ + lz;
                        int r = Math.Max(Math.Abs(wx - spec.CenterX), Math.Abs(wz - spec.CenterZ));
                        if (r >= spec.HalfWidth) continue;

                        // Exact square-pyramid surface: full height at the center, linear to
                        // zero at the base edge - planar faces, sharp edges.
                        int surfaceWy = baseWy + (int)Math.Round(spec.Height * (1.0 - (double)r / spec.HalfWidth));
                        if (surfaceWy > worldTop) surfaceWy = worldTop;
                        if (surfaceWy <= baseWy) continue;

                        for (int wy = baseWy; wy <= surfaceWy; wy++)
                        {
                            int localY = wy - ChunkManager.WorldOriginY;
                            if (localY < 0 || localY >= chunkHeight) break;
                            chunk[lx, localY, lz] = spec.BrickId;
                        }
                    }
                }
            }
        }
    }

    /// <summary>A single regular pyramid's fixed world placement.</summary>
    public sealed class PyramidSpec
    {
        public int CenterX { get; }
        public int CenterZ { get; }
        public int HalfWidth { get; }
        public int Height { get; }
        public int BrickId { get; }

        internal PyramidSpec(int centerX, int centerZ, int halfWidth, int height, int brickId)
        {
            CenterX = centerX;
            CenterZ = centerZ;
            HalfWidth = halfWidth;
            Height = height;
            BrickId = brickId;
        }
    }
}
