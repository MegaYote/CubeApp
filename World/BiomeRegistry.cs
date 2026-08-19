using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Cubuild
{
    /// <summary>
    /// A data-driven biome definition, loaded from biomes.json. Each biome is identified on the
    /// world by a 2D noise field of temperature and humidity: a column whose (temperature,
    /// humidity) falls inside a biome's ranges selects that biome. The biome then drives terrain
    /// height and surface materials, so anyone can add a biome by appending an entry to biomes.json.
    /// </summary>
    public sealed class BiomeDefinition
    {
        public string Id { get; }
        public string DisplayName { get; }
        /// <summary>Which (temperature, humidity) noise cells this biome claims.</summary>
        public (float Min, float Max) Temperature;
        public (float Min, float Max) Humidity;
        /// <summary>True when this biome is mostly water (ocean).</summary>
        public bool IsWater { get; }
        /// <summary>Base terrain height as a fraction of the terrain band (0..1).</summary>
        public float BaseHeight { get; }
        /// <summary>How much the height varies (amplitude of the noise bump).</summary>
        public float HeightVariation { get; }
        /// <summary>True = amplified terrain: the relief swings BOTH ways (real peaks AND deep
        /// valleys, like amplified Minecraft worlds) instead of only carving downward.</summary>
        public bool Amplified { get; }
        /// <summary>Block id name for the surface (topmost) block.</summary>
        public string SurfaceBlock { get; }
        /// <summary>Block id name for the fill below the surface.</summary>
        public string FillBlock { get; }
        /// <summary>How many layers deep the fill block goes.</summary>
        public int FillDepth { get; }
        /// <summary>Approximate trees per chunk (0 = none).</summary>
        public int TreeDensity { get; }
        /// <summary>Which tree generator this biome uses ("oak" or "pine").</summary>
        public string TreeType { get; }
        /// <summary>Optional block id mixed into the fill layers (e.g. red clay mixed with dirt).</summary>
        public string FillMixBlock { get; }
        /// <summary>Chance (0..1) that a fill layer becomes FillMixBlock instead of FillBlock.</summary>
        public float FillMixChance { get; }

        public BiomeDefinition(string id, string displayName,
            (float, float) temperature, (float, float) humidity,
            bool isWater, float baseHeight, float heightVariation,
            string surfaceBlock, string fillBlock, int fillDepth, int treeDensity,
            string treeType = "oak", string fillMixBlock = "", float fillMixChance = 0.5f,
            bool amplified = false)
        {
            Id = id;
            DisplayName = displayName;
            Temperature = temperature;
            Humidity = humidity;
            IsWater = isWater;
            BaseHeight = baseHeight;
            HeightVariation = heightVariation;
            Amplified = amplified;
            SurfaceBlock = surfaceBlock;
            FillBlock = fillBlock;
            FillDepth = fillDepth;
            TreeDensity = treeDensity;
            TreeType = treeType;
            FillMixBlock = fillMixBlock;
            FillMixChance = fillMixChance;
        }
    }

    /// <summary>
    /// Registry of all biomes loaded from biomes.json. Lookup is by a (temperature, humidity)
    /// pair from the BiomeMap noise - the first biome whose ranges contain the values wins.
    /// </summary>
    public static class BiomeRegistry
    {
        private static readonly List<BiomeDefinition> _biomes = new();

        public static bool Loaded { get; private set; }

        public static IReadOnlyList<BiomeDefinition> All => _biomes;

        public static void LoadDefault()
        {
            byte[]? bytes = LoadResourceBytes("biomes.json");
            if (bytes == null) throw new FileNotFoundException("biomes.json not found as an embedded resource or loose file.");
            LoadFromJson(System.Text.Encoding.UTF8.GetString(bytes));
        }

        public static void LoadFromJson(string json)
        {
            _biomes.Clear();
            // biomes.json is hand-edited: allow // and /* */ comments + trailing commas.
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (!doc.RootElement.TryGetProperty("biomes", out var biomes)) return;

            foreach (var b in biomes.EnumerateArray())
            {
                var id = b.GetProperty("id").GetString() ?? "";
                var display = b.TryGetProperty("displayName", out var d) ? d.GetString() ?? id : id;
                var temp = ReadRange(b, "temperature");
                var hum = ReadRange(b, "humidity");
                bool water = b.TryGetProperty("water", out var w) && w.GetBoolean();
                float baseH = b.TryGetProperty("baseHeight", out var bh) ? bh.GetSingle() : 0.4f;
                float varH = b.TryGetProperty("heightVariation", out var hv) ? hv.GetSingle() : 0.1f;
                string surface = b.TryGetProperty("surfaceBlock", out var sb) ? sb.GetString() ?? "grass" : "grass";
                string fill = b.TryGetProperty("fillBlock", out var fb) ? fb.GetString() ?? "dirt" : "dirt";
                int depth = b.TryGetProperty("fillDepth", out var fd) ? fd.GetInt32() : 3;
                int trees = b.TryGetProperty("treeDensity", out var td) ? td.GetInt32() : 0;
                string treeType = b.TryGetProperty("treeType", out var tt) ? tt.GetString() ?? "oak" : "oak";
                string fillMix = b.TryGetProperty("fillMixBlock", out var fm) ? fm.GetString() ?? "" : "";
                float mixChance = b.TryGetProperty("fillMixChance", out var mc) ? mc.GetSingle() : 0.5f;
                bool amplified = b.TryGetProperty("amplified", out var amp) && amp.GetBoolean();
                _biomes.Add(new BiomeDefinition(id, display, temp, hum, water, baseH, varH, surface, fill, depth, trees, treeType, fillMix, mixChance, amplified));
            }
            Loaded = true;
        }

        private static (float, float) ReadRange(JsonElement b, string name)
        {
            float min = -1f, max = 1f;
            if (b.TryGetProperty(name, out var r))
            {
                if (r.TryGetProperty("min", out var mn)) min = mn.GetSingle();
                if (r.TryGetProperty("max", out var mx)) max = mx.GetSingle();
            }
            return (min, max);
        }

        /// <summary>Returns the biome matching a (temperature, humidity) pair, or the first biome
        /// as a fallback (so an uncovered cell still maps to something sane).</summary>
        public static BiomeDefinition Match(float temperature, float humidity)
        {
            foreach (var b in _biomes)
            {
                if (temperature >= b.Temperature.Min && temperature <= b.Temperature.Max
                    && humidity >= b.Humidity.Min && humidity <= b.Humidity.Max)
                {
                    return b;
                }
            }
            return _biomes.Count > 0 ? _biomes[0] : Fallback;
        }

        public static BiomeDefinition Get(string id)
        {
            foreach (var b in _biomes)
                if (string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase)) return b;
            return Fallback;
        }

        private static BiomeDefinition Fallback => new("plains", "Plains", (-1f, 1f), (-1f, 1f),
            false, 0.42f, 0.10f, "grass", "dirt", 3, 1);

        private static byte[]? LoadResourceBytes(string fileName)
        {
            var asm = typeof(BiomeRegistry).Assembly;
            foreach (var name in asm.GetManifestResourceNames())
            {
                if (name.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
                {
                    using var stream = asm.GetManifestResourceStream(name);
                    if (stream != null)
                    {
                        using var ms = new MemoryStream();
                        stream.CopyTo(ms);
                        return ms.ToArray();
                    }
                }
            }
            string path = File.Exists(fileName) ? fileName : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
    }
}
