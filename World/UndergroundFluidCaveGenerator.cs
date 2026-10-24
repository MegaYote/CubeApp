using System;

namespace Cubuild.World
{
    /// <summary>
    /// Creates a small, sealed underground fluid chamber from otherwise solid stone. Each
    /// successful chamber is its own ellipsoidal cave: the lower half is still fluid and the
    /// upper half is an air crown. No fluid simulation is scheduled; it remains still unless
    /// a player later breaches the stone shell.
    /// </summary>
    public static class UndergroundFluidCaveGenerator
    {
        private const int CandidateAttempts = 12;
        private static readonly (int X, int Y, int Z)[] FaceNeighbors =
        {
            (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)
        };

        /// <summary>
        /// Makes one deterministic per-chunk chance check, then tries a bounded set of chamber
        /// positions. minLocalY/maxLocalY are inclusive and apply to the chamber and its full
        /// one-block shell. The chamber interior and every face-adjacent shell block must be
        /// stone, so this carves a separate cave instead of filling or connecting to an existing
        /// cave. All writes stay in this chunk; no neighboring chunk data or terrain RNG is used.
        /// </summary>
        public static bool TryGenerate(
            Chunk chunk,
            int chunkX,
            int chunkZ,
            int worldSeed,
            int fluidId,
            int minLocalY,
            int maxLocalY,
            int chanceDenominator,
            int seedSalt)
        {
            if (chunk == null || chanceDenominator <= 0 || fluidId <= 0 || fluidId > byte.MaxValue)
                return false;

            int minY = Math.Max(0, minLocalY);
            int maxY = Math.Min(chunk.Height - 1, maxLocalY);
            if (minY > maxY || chunk.Width <= 0 || chunk.Depth <= 0)
                return false;

            int stoneId = BlockRegistry.GetId("stone");
            if (stoneId <= 0 || stoneId > byte.MaxValue || fluidId == BlockRegistry.AirId || fluidId == stoneId)
                return false;

            var random = new DeterministicRandom(MakeSeed(worldSeed, chunkX, chunkZ, seedSalt));
            if (random.NextInt(chanceDenominator) != 0)
                return false;

            byte[] blocks = chunk.RawBlocks;
            byte[] metadata = chunk.RawMeta;
            byte idStone = (byte)stoneId;
            byte idFluid = (byte)fluidId;

            for (int attempt = 0; attempt < CandidateAttempts; attempt++)
            {
                // Diameters are 5-9 blocks across and 5-7 blocks high, including the chamber.
                int radiusX = 2 + random.NextInt(3);
                int radiusY = 2 + random.NextInt(2);
                int radiusZ = 2 + random.NextInt(3);

                int minCenterX = radiusX + 1;
                int maxCenterX = chunk.Width - radiusX - 2;
                int minCenterZ = radiusZ + 1;
                int maxCenterZ = chunk.Depth - radiusZ - 2;
                int minCenterY = minY + radiusY + 1;
                int maxCenterY = maxY - radiusY - 1;
                if (minCenterX > maxCenterX || minCenterZ > maxCenterZ || minCenterY > maxCenterY)
                    continue;

                int centerX = minCenterX + random.NextInt(maxCenterX - minCenterX + 1);
                int centerY = minCenterY + random.NextInt(maxCenterY - minCenterY + 1);
                int centerZ = minCenterZ + random.NextInt(maxCenterZ - minCenterZ + 1);

                int spanY = radiusY * 2 + 1;
                int spanZ = radiusZ * 2 + 1;
                var chamber = new bool[(radiusX * 2 + 1) * spanY * spanZ];
                int MaskIndex(int dx, int dy, int dz)
                    => ((dx + radiusX) * spanY + (dy + radiusY)) * spanZ + dz + radiusZ;

                // Mark the ellipsoid and require its entire interior to be natural stone. Then
                // validate the six face-neighbors of every carved cell, which is the complete
                // one-block shell needed to keep cellular fluids sealed without rejecting nearby
                // caves that only touch the chamber diagonally.
                bool allStone = true;
                for (int dx = -radiusX; dx <= radiusX && allStone; dx++)
                {
                    double nx = dx / (double)radiusX;
                    for (int dy = -radiusY; dy <= radiusY && allStone; dy++)
                    {
                        double ny = dy / (double)radiusY;
                        for (int dz = -radiusZ; dz <= radiusZ; dz++)
                        {
                            double nz = dz / (double)radiusZ;
                            if (nx * nx + ny * ny + nz * nz > 1.0)
                                continue;

                            chamber[MaskIndex(dx, dy, dz)] = true;
                            if (blocks[chunk.Index(centerX + dx, centerY + dy, centerZ + dz)] != idStone)
                            {
                                allStone = false;
                                break;
                            }
                        }
                    }
                }
                if (!allStone)
                    continue;

                for (int dx = -radiusX; dx <= radiusX && allStone; dx++)
                {
                    for (int dy = -radiusY; dy <= radiusY && allStone; dy++)
                    {
                        for (int dz = -radiusZ; dz <= radiusZ; dz++)
                        {
                            if (!chamber[MaskIndex(dx, dy, dz)])
                                continue;

                            foreach (var offset in FaceNeighbors)
                            {
                                int nx = dx + offset.X;
                                int ny = dy + offset.Y;
                                int nz = dz + offset.Z;
                                bool neighborInside = nx >= -radiusX && nx <= radiusX
                                    && ny >= -radiusY && ny <= radiusY
                                    && nz >= -radiusZ && nz <= radiusZ
                                    && chamber[MaskIndex(nx, ny, nz)];
                                if (neighborInside)
                                    continue;

                                if (blocks[chunk.Index(centerX + nx, centerY + ny, centerZ + nz)] != idStone)
                                {
                                    allStone = false;
                                    break;
                                }
                            }
                            if (!allStone)
                                break;
                        }
                    }
                }
                if (!allStone)
                    continue;

                bool placed = false;
                for (int x = centerX - radiusX; x <= centerX + radiusX; x++)
                {
                    double nx = (x - centerX) / (double)radiusX;
                    for (int y = centerY - radiusY; y <= centerY + radiusY; y++)
                    {
                        double ny = (y - centerY) / (double)radiusY;
                        for (int z = centerZ - radiusZ; z <= centerZ + radiusZ; z++)
                        {
                            double nz = (z - centerZ) / (double)radiusZ;
                            if (nx * nx + ny * ny + nz * nz > 1.0)
                                continue;

                            int index = chunk.Index(x, y, z);
                            // The center-height layer and everything below it form the lower
                            // fluid half; everything above forms the enclosed air crown.
                            blocks[index] = y <= centerY ? idFluid : (byte)BlockRegistry.AirId;
                            metadata[index] = 0; // Explicit still/source fluid metadata; clear air too.
                            placed = true;
                        }
                    }
                }

                if (placed)
                {
                    chunk.NeedsRemesh = true;
                    return true;
                }
            }

            return false;
        }

        private static ulong MakeSeed(int worldSeed, int chunkX, int chunkZ, int seedSalt)
        {
            unchecked
            {
                ulong seed = Mix((uint)worldSeed ^ 0x9E3779B97F4A7C15UL);
                seed = Mix(seed ^ ((ulong)(uint)chunkX * 0xBF58476D1CE4E5B9UL));
                seed = Mix(seed ^ ((ulong)(uint)chunkZ * 0x94D049BB133111EBUL));
                return Mix(seed ^ ((ulong)(uint)seedSalt * 0xD6E8FEB86659FD93UL));
            }
        }

        private static ulong Mix(ulong value)
        {
            unchecked
            {
                value ^= value >> 30;
                value *= 0xBF58476D1CE4E5B9UL;
                value ^= value >> 27;
                value *= 0x94D049BB133111EBUL;
                value ^= value >> 31;
                return value;
            }
        }

        /// <summary>A small fixed-algorithm PRNG keeps results independent of terrain RNG.</summary>
        private struct DeterministicRandom
        {
            private ulong _state;

            public DeterministicRandom(ulong seed)
            {
                _state = seed;
            }

            public int NextInt(int exclusiveMax)
            {
                unchecked
                {
                    _state += 0x9E3779B97F4A7C15UL;
                    ulong value = _state;
                    value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
                    value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
                    value ^= value >> 31;
                    return (int)(((value >> 32) * (uint)exclusiveMax) >> 32);
                }
            }
        }
    }
}
