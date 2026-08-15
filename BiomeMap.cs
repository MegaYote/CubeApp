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
        // Macro "continent" climate fields: very low frequency heat + dryness that bias the
        // fine climate, so whole regions settle into one dominant climate (hot/dry, cool/wet,
        // ...) instead of every biome scattering into small patches. Deserts become enormous
        // because their (hot, dry) window covers nearly an entire macro-region.
        private readonly NoiseOctaves _heatRegion;
        private readonly NoiseOctaves _dryRegion;

        /// <summary>Base sampling frequency of the macro climate fields (lower = larger continents).</summary>
        private const double RegionFrequency = 0.003;
        /// <summary>How strongly a macro-region shifts the fine climate toward its extreme (0.5 = up to half a band).</summary>
        private const float RegionGain = 0.55f;

        public BiomeMap(int seed)
        {
            var rand = new System.Random(seed);
            // Low-frequency so biomes form broad, contiguous regions. Adjust frequencies to
            // control biome size (lower = larger regions).
            _temperature = new NoiseOctaves(rand, 4, 0);
            _humidity = new NoiseOctaves(rand, 4, 0);
            // Even lower frequency than the fine fields -> multi-thousand-block climate continents.
            _heatRegion = new NoiseOctaves(rand, 4, 0);
            _dryRegion = new NoiseOctaves(rand, 4, 0);
        }

        public BiomeDefinition BiomeAt(int worldX, int worldZ)
        {
            float heat = (float)_heatRegion.Noise2DNormalized(worldX * RegionFrequency, worldZ * RegionFrequency);
            float dry = (float)_dryRegion.Noise2DNormalized(worldX * RegionFrequency, worldZ * RegionFrequency);
            // Macro bias: hot regions push temperature up, dry regions push humidity down.
            float temp = (float)_temperature.Noise2DNormalized(worldX * 0.008, worldZ * 0.008) + heat * RegionGain;
            float hum = (float)_humidity.Noise2DNormalized(worldX * 0.008, worldZ * 0.008) - dry * RegionGain;
            temp = System.Math.Clamp(temp, -1f, 1f);
            hum = System.Math.Clamp(hum, -1f, 1f);
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
