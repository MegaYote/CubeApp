using CubeApp.World;

namespace CubeApp
{
    /// <summary>
    /// The authoritative biome map. Two large low-frequency noise fields (temperature and
    /// humidity, both normalized ~[-1,1]) are sampled per world column; the (temperature,
    /// humidity) pair is then looked up in <see cref="BiomeRegistry"/> to pick the biome. Terrain
    /// height and surface materials derive FROM the resulting biome, so biome labels and terrain
    /// can never desync (one source of truth).
    /// </summary>
    public sealed class BiomeMap
    {
        private readonly NoiseOctaves _temperature;
        private readonly NoiseOctaves _humidity;

        public BiomeMap(int seed)
        {
            var rand = new System.Random(seed);
            // Low-frequency so biomes form broad, contiguous regions. Adjust frequencies to
            // control biome size (lower = larger regions).
            _temperature = new NoiseOctaves(rand, 4, 0);
            _humidity = new NoiseOctaves(rand, 4, 0);
        }

        public BiomeDefinition BiomeAt(int worldX, int worldZ)
        {
            float temp = (float)_temperature.Noise2DNormalized(worldX * 0.008, worldZ * 0.008);
            float hum = (float)_humidity.Noise2DNormalized(worldX * 0.008, worldZ * 0.008);
            return BiomeRegistry.Match(temp, hum);
        }

        /// <summary>
        /// True when any column within <paramref name="radius"/> blocks (sampled every few blocks)
        /// is a water biome - used to make beaches at ocean shores. The sample step keeps it cheap:
        /// a beach radius of ~4 blocks with step 2 checks only a handful of cells.
        /// </summary>
        public bool IsNearWater(int worldX, int worldZ, int radius = 4, int step = 2)
        {
            for (int dx = -radius; dx <= radius; dx += step)
            {
                for (int dz = -radius; dz <= radius; dz += step)
                {
                    if (_biomeMap_IsWater(worldX + dx, worldZ + dz)) return true;
                }
            }
            return false;
        }

        private bool _biomeMap_IsWater(int worldX, int worldZ) => BiomeAt(worldX, worldZ).IsWater;

        public BiomeDefinition BiomeAt(double worldX, double worldZ) => BiomeAt((int)worldX, (int)worldZ);
    }
}
