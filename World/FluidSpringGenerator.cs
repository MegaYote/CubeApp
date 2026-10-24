using System;
using System.Collections.Generic;

namespace Cubuild.World
{
    /// <summary>
    /// Adds sparse water and lava springs to natural stone cliff faces. A spring is a stone
    /// source block beside one sky-open air outlet, with a short clear drop below the outlet.
    /// </summary>
    public static class FluidSpringGenerator
    {
        // Keep both the source and its four horizontal neighbors off the chunk edge. This makes
        // the cliff test self-contained and leaves a one-block margin around the outlet.
        private const int HorizontalMargin = 2;
        private const int CascadeAirBlocks = 3;

        private static readonly int[] HorizontalDx = { -1, 1, 0, 0 };
        private static readonly int[] HorizontalDz = { 0, 0, -1, 1 };

        /// <summary>
        /// Places naturally occurring cliff springs and returns their absolute world positions.
        /// Each chunk can add at most one spring: water occurs at 1/12, lava at 1/24, and the
        /// remaining rolls place none. Results are deterministic and independent of other RNGs.
        /// </summary>
        public static List<(int X, int Y, int Z)> Generate(
            Chunk chunk,
            int chunkX,
            int chunkZ,
            int worldSeed,
            int waterId,
            int lavaId,
            int minLocalY,
            int maxLocalY)
        {
            var placedSources = new List<(int X, int Y, int Z)>();
            if (chunk == null || chunk.Width < HorizontalMargin * 2 + 1 ||
                chunk.Depth < HorizontalMargin * 2 + 1 || waterId <= 0 || lavaId <= 0 ||
                waterId > byte.MaxValue || lavaId > byte.MaxValue || waterId == lavaId)
                return placedSources;

            int stoneId = BlockRegistry.GetId("stone");
            if (stoneId <= 0 || stoneId > byte.MaxValue ||
                waterId == BlockRegistry.AirId || lavaId == BlockRegistry.AirId ||
                waterId == stoneId || lavaId == stoneId)
                return placedSources;

            int minY = Math.Max(Math.Max(0, minLocalY), CascadeAirBlocks);
            int maxY = Math.Min(chunk.Height - 1, maxLocalY);
            if (minY > maxY || chunk.Height <= 0)
                return placedSources;

            byte[] blocks = chunk.RawBlocks;
            byte[] metadata = chunk.RawMeta;
            byte idStone = (byte)stoneId;
            byte idWater = (byte)waterId;
            byte idLava = (byte)lavaId;
            byte idAir = (byte)BlockRegistry.AirId;

            // Cache each column's highest non-air block once. A candidate outlet is open to the
            // sky precisely when no non-air block reaches its height or anything above it.
            int[] columnTop = new int[chunk.Width * chunk.Depth];
            for (int x = 0; x < chunk.Width; x++)
            {
                for (int z = 0; z < chunk.Depth; z++)
                {
                    int top = -1;
                    for (int y = chunk.Height - 1; y >= 0; y--)
                    {
                        if (blocks[chunk.Index(x, y, z)] == idAir)
                            continue;
                        top = y;
                        break;
                    }
                    columnTop[x * chunk.Depth + z] = top;
                }
            }

            var candidates = new List<Candidate>();
            for (int x = HorizontalMargin; x < chunk.Width - HorizontalMargin; x++)
            {
                for (int z = HorizontalMargin; z < chunk.Depth - HorizontalMargin; z++)
                {
                    for (int y = minY; y <= maxY; y++)
                    {
                        if (TryFindOutlet(chunk, blocks, columnTop, x, y, z, idStone, idAir,
                                out int outletX, out int outletZ))
                            candidates.Add(new Candidate(x, y, z, outletX, outletZ));
                    }
                }
            }

            var random = new DeterministicRandom(MakeSeed(worldSeed, chunkX, chunkZ));

            if (candidates.Count == 0)
                return placedSources;

            // One roll per chunk keeps springs genuinely sparse regardless of how many exposed
            // cliff blocks happen to qualify. Of 24 equally likely values, two select water and
            // one selects lava: water is exactly twice as common, with at most one spring here.
            int roll = random.NextInt(24);
            byte fluidId = roll < 2 ? idWater : roll == 2 ? idLava : (byte)0;
            if (fluidId == 0)
                return placedSources;

            // Select uniformly among valid sites, starting at a seeded offset. The recheck keeps
            // the choice safe if a nearby candidate has already been modified by this generator.
            int start = random.NextInt(candidates.Count);
            for (int offset = 0; offset < candidates.Count; offset++)
            {
                Candidate candidate = candidates[(start + offset) % candidates.Count];
                if (!TryFindOutlet(chunk, blocks, columnTop, candidate.X, candidate.Y, candidate.Z,
                        idStone, idAir, out int outletX, out int outletZ) ||
                    outletX != candidate.OutletX || outletZ != candidate.OutletZ ||
                    HasNearbyFluid(chunk, blocks, candidate, idWater, idLava))
                    continue;

                int sourceIndex = chunk.Index(candidate.X, candidate.Y, candidate.Z);
                blocks[sourceIndex] = fluidId;
                metadata[sourceIndex] = 0; // Source/still metadata, not a flowing level.
                placedSources.Add((
                    chunk.OriginX + candidate.X,
                    chunk.OriginY + candidate.Y,
                    chunk.OriginZ + candidate.Z));
                break;
            }

            if (placedSources.Count > 0)
                chunk.NeedsRemesh = true;

            return placedSources;
        }

        private static bool TryFindOutlet(
            Chunk chunk,
            byte[] blocks,
            int[] columnTop,
            int x,
            int y,
            int z,
            byte idStone,
            byte idAir,
            out int outletX,
            out int outletZ)
        {
            outletX = -1;
            outletZ = -1;
            if (blocks[chunk.Index(x, y, z)] != idStone)
                return false;

            int airNeighbors = 0;
            for (int i = 0; i < HorizontalDx.Length; i++)
            {
                int neighborX = x + HorizontalDx[i];
                int neighborZ = z + HorizontalDz[i];
                byte neighbor = blocks[chunk.Index(neighborX, y, neighborZ)];
                if (neighbor == idAir)
                {
                    airNeighbors++;
                    outletX = neighborX;
                    outletZ = neighborZ;
                }
                else if (neighbor != idStone)
                {
                    return false;
                }

                if (airNeighbors > 1)
                    return false;
            }

            if (airNeighbors != 1 || columnTop[outletX * chunk.Depth + outletZ] >= y)
                return false;

            // The outlet must have a clear three-block drop, all contained in this chunk.
            for (int drop = 1; drop <= CascadeAirBlocks; drop++)
            {
                if (blocks[chunk.Index(outletX, y - drop, outletZ)] != idAir)
                    return false;
            }

            return true;
        }

        private static bool HasNearbyFluid(
            Chunk chunk,
            byte[] blocks,
            Candidate candidate,
            byte idWater,
            byte idLava)
        {
            // Reject springs within two blocks of either the source or outlet. The bounding box
            // is intentionally conservative and avoids placing sources that immediately mix.
            int minX = Math.Max(0, Math.Min(candidate.X, candidate.OutletX) - 2);
            int maxX = Math.Min(chunk.Width - 1, Math.Max(candidate.X, candidate.OutletX) + 2);
            int minY = Math.Max(0, candidate.Y - 2);
            int maxY = Math.Min(chunk.Height - 1, candidate.Y + 2);
            int minZ = Math.Max(0, Math.Min(candidate.Z, candidate.OutletZ) - 2);
            int maxZ = Math.Min(chunk.Depth - 1, Math.Max(candidate.Z, candidate.OutletZ) + 2);

            for (int x = minX; x <= maxX; x++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    for (int y = minY; y <= maxY; y++)
                    {
                        byte block = blocks[chunk.Index(x, y, z)];
                        if (block == idWater || block == idLava)
                            return true;
                    }
                }
            }

            return false;
        }

        private static ulong MakeSeed(int worldSeed, int chunkX, int chunkZ)
        {
            unchecked
            {
                ulong seed = Mix((uint)worldSeed ^ 0x9E3779B97F4A7C15UL);
                seed = Mix(seed ^ ((ulong)(uint)chunkX * 0xBF58476D1CE4E5B9UL));
                seed = Mix(seed ^ ((ulong)(uint)chunkZ * 0x94D049BB133111EBUL));
                return Mix(seed ^ 0xD6E8FEB86659FD93UL);
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

        private readonly struct Candidate
        {
            public readonly int X;
            public readonly int Y;
            public readonly int Z;
            public readonly int OutletX;
            public readonly int OutletZ;

            public Candidate(int x, int y, int z, int outletX, int outletZ)
            {
                X = x;
                Y = y;
                Z = z;
                OutletX = outletX;
                OutletZ = outletZ;
            }
        }

        /// <summary>A fixed-algorithm PRNG prevents generator results from depending on runtime RNG details.</summary>
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
