using System;

namespace CubeApp.World
{
    public sealed class InfdevChunkProvider : IChunkProvider
    {
        private readonly int seed;

        public InfdevChunkProvider(int seed = 341873128)
        {
            this.seed = seed;
        }

public Chunk GenerateChunk(int chunkX, int chunkZ, int chunkSize, int chunkHeight)
        {
            int originX = chunkX * chunkSize;
            int originZ = chunkZ * chunkSize;
// World Y spans -64..191. Local Y 0..255 with OriginY = -64 (see ChunkManager.WorldOriginY).
            const int originY = ChunkManager.WorldOriginY;
            var chunk = new Chunk(chunkSize, chunkHeight, chunkSize, originX, originY, originZ);

            // Sea level in world space. Any air column sitting at or below this Y fills with water,
            // so terrain needs to actually dip below it for oceans to generate.
            const int seaLevelWorldY = 0;

            // Resolve block ids once from the data-driven registry (air=0 reserved).
            int idBedrock = BlockRegistry.GetId("bedrock");
            int idWater = BlockRegistry.GetId("water");
            int idSand = BlockRegistry.GetId("sand");
            int idAir = BlockRegistry.AirId;
            int idGrass = BlockRegistry.GetId("grass");
            int idDirt = BlockRegistry.GetId("dirt");
            int idStone = BlockRegistry.GetId("stone");

            for (int lx = 0; lx < chunk.Width; lx++)
            {
                for (int lz = 0; lz < chunk.Depth; lz++)
                {
                    int worldX = originX + lx;
                    int worldZ = originZ + lz;

                    // Heightfield in the Infdev spirit: the terrain surface sits AT sea level
                    // (world Y 0), shaped by a very broad "continent" mask that carves large
                    // ocean basins and landmasses, plus gentle rolling hills. Amplitudes stay
                    // modest so most terrain hugs the shoreline and real oceans form - while the
                    // tall world Y range (-64..191) stays available for building and digging.
                    double continent = Fbm2D(worldX * 0.0025, worldZ * 0.0025, 3, 0.5);
                    double hills = Fbm2D(worldX * 0.012, worldZ * 0.012, 3, 0.5);
                    int surfaceWorldY = (int)Math.Round(continent * 16.0 + hills * 8.0 + 1.0);
                    int surfaceLocalY = surfaceWorldY - originY;  // local Y = world Y - originY
                    surfaceLocalY = Math.Clamp(surfaceLocalY, 0, chunkHeight - 1);

                    // Surface/sub-surface blocks pick by depth relative to sea level: underwater
                    // and the immediate shoreline use sand (no grass grows beneath water), while
                    // dry land keeps the grass-on-dirt-on-stone stratigraphy.
                    bool submerged = surfaceWorldY <= seaLevelWorldY;
                    int surfaceBlock = submerged ? idSand : idGrass;
                    int subSurfaceBlock = submerged ? idSand : idDirt;

                    for (int y = 0; y < chunk.Height; y++)
                    {
                        int worldY = y + originY; // convert local Y to world Y

                        if (y == 0)
                        {
                            chunk[lx, y, lz] = idBedrock;
                            continue;
                        }

                        if (y > surfaceLocalY)
                        {
                            // Below sea level fills with water; above stays air.
                            if (worldY <= seaLevelWorldY)
                            {
                                chunk[lx, y, lz] = idWater;
                            }
                            else
                            {
                                chunk[lx, y, lz] = idAir;
                            }
                            continue;
                        }

                        bool carveCave = y < surfaceLocalY - 3 && SampleCave(worldX, worldY, worldZ) > 0.63;
                        if (carveCave)
                        {
                            chunk[lx, y, lz] = idAir;
                            continue;
                        }

                        if (y == surfaceLocalY)
                        {
                            chunk[lx, y, lz] = surfaceBlock;
                        }
                        else if (y >= surfaceLocalY - 3)
                        {
                            chunk[lx, y, lz] = subSurfaceBlock;
                        }
                        else
                        {
                            chunk[lx, y, lz] = idStone;
                        }
                    }
                }
            }

            chunk.NeedsRemesh = true;
            return chunk;
        }

        private double SampleCave(int x, int y, int z)
        {
            double a = Fbm3D(x * 0.08, y * 0.14, z * 0.08, 3, 0.5);
            double b = Fbm3D((x + 1337) * 0.11, (y + 73) * 0.12, (z - 991) * 0.11, 2, 0.5);
            return a * 0.7 + b * 0.3;
        }

        private double Fbm2D(double x, double z, int octaves, double persistence)
        {
            double amplitude = 1.0;
            double frequency = 1.0;
            double sum = 0.0;
            double norm = 0.0;

            for (int i = 0; i < octaves; i++)
            {
                sum += SmoothValueNoise2D(x * frequency, z * frequency) * amplitude;
                norm += amplitude;
                amplitude *= persistence;
                frequency *= 2.0;
            }

            return norm > 0.0 ? sum / norm : 0.0;
        }

        private double Fbm3D(double x, double y, double z, int octaves, double persistence)
        {
            double amplitude = 1.0;
            double frequency = 1.0;
            double sum = 0.0;
            double norm = 0.0;

            for (int i = 0; i < octaves; i++)
            {
                sum += SmoothValueNoise3D(x * frequency, y * frequency, z * frequency) * amplitude;
                norm += amplitude;
                amplitude *= persistence;
                frequency *= 2.0;
            }

            return norm > 0.0 ? sum / norm : 0.0;
        }

        private double SmoothValueNoise2D(double x, double z)
        {
            int x0 = FastFloor(x);
            int z0 = FastFloor(z);
            int x1 = x0 + 1;
            int z1 = z0 + 1;

            double tx = x - x0;
            double tz = z - z0;
            double sx = SmoothStep(tx);
            double sz = SmoothStep(tz);

            double n00 = HashToUnit(x0, 0, z0);
            double n10 = HashToUnit(x1, 0, z0);
            double n01 = HashToUnit(x0, 0, z1);
            double n11 = HashToUnit(x1, 0, z1);

            double nx0 = Lerp(n00, n10, sx);
            double nx1 = Lerp(n01, n11, sx);
            return Lerp(nx0, nx1, sz);
        }

        private double SmoothValueNoise3D(double x, double y, double z)
        {
            int x0 = FastFloor(x);
            int y0 = FastFloor(y);
            int z0 = FastFloor(z);
            int x1 = x0 + 1;
            int y1 = y0 + 1;
            int z1 = z0 + 1;

            double tx = SmoothStep(x - x0);
            double ty = SmoothStep(y - y0);
            double tz = SmoothStep(z - z0);

            double c000 = HashToUnit(x0, y0, z0);
            double c100 = HashToUnit(x1, y0, z0);
            double c010 = HashToUnit(x0, y1, z0);
            double c110 = HashToUnit(x1, y1, z0);
            double c001 = HashToUnit(x0, y0, z1);
            double c101 = HashToUnit(x1, y0, z1);
            double c011 = HashToUnit(x0, y1, z1);
            double c111 = HashToUnit(x1, y1, z1);

            double x00 = Lerp(c000, c100, tx);
            double x10 = Lerp(c010, c110, tx);
            double x01 = Lerp(c001, c101, tx);
            double x11 = Lerp(c011, c111, tx);
            double y0v = Lerp(x00, x10, ty);
            double y1v = Lerp(x01, x11, ty);
            return Lerp(y0v, y1v, tz);
        }

        private double HashToUnit(int x, int y, int z)
        {
            unchecked
            {
                uint h = (uint)seed;
                h ^= (uint)(x * 374761393);
                h = (h << 13) | (h >> 19);
                h ^= (uint)(y * 668265263);
                h = (h << 11) | (h >> 21);
                h ^= (uint)(z * 2246822519);
                h ^= h >> 15;
                h *= 2246822519;
                h ^= h >> 13;
                h *= 3266489917;
                h ^= h >> 16;
                return (h / (double)uint.MaxValue) * 2.0 - 1.0;
            }
        }

        private static int FastFloor(double v)
        {
            int i = (int)v;
            return v < i ? i - 1 : i;
        }

        private static double SmoothStep(double t)
        {
            return t * t * (3.0 - 2.0 * t);
        }

        private static double Lerp(double a, double b, double t)
        {
            return a + (b - a) * t;
        }
    }
}