using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Cubuild
{
    /// <summary>How the spawner picks the Y coordinate for a spawn attempt.</summary>
    public enum SpawnYMode
    {
        /// <summary>Walk the terrain top (animals).</summary>
        Surface,
        /// <summary>Triple-nested random biased toward the world bottom - monsters rise from caves and shafts.</summary>
        DepthBias,
    }

    /// <summary>Light requirements for a spawn cell (checked only when a light probe is wired in).</summary>
    public enum SpawnLightGate
    {
        /// <summary>No light requirement.</summary>
        Any,
        /// <summary>Block light >= 9 (Minecraft's animal floor - daylit surface only).</summary>
        Bright,
        /// <summary>Block light <= rand(8) (monsters: caves all day, surface only at night).</summary>
        Dark,
    }

    /// <summary>
    /// One spawnable mob entry in a category's weighted table. A rule only participates when
    /// the sampled chunk's biome matches <see cref="Biomes"/> ("*" matches everything).
    /// </summary>
    public sealed class MobSpawnRule
    {
        public string MobId { get; set; } = "";
        public int Weight { get; set; } = 1;
        public int PackMin { get; set; } = 1;
        public int PackMax { get; set; } = 1;
        /// <summary>Biome ids this mob may spawn in; empty or "*" = any biome.</summary>
        public HashSet<string> Biomes { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Require a water block within ~4 blocks of the spawn cell (duck ponds).</summary>
        public bool NearWater { get; set; }
        /// <summary>Height clamp for depth-bias spawns (monsters stay underground).</summary>
        public int? MinY { get; set; }
        public int? MaxY { get; set; }

        public bool MatchesBiome(string biomeId) =>
            Biomes.Count == 0 || Biomes.Contains("*") || Biomes.Contains(biomeId);
    }

    /// <summary>An independent spawn table: caps, Y mode, light gate, cadence, and its rules.</summary>
    public sealed class SpawnCategory
    {
        public string Id { get; set; } = "";
        public int MaxTotal { get; set; } = 20;
        public int MaxPerType { get; set; } = 12;
        public int MaxPerChunk { get; set; } = 6;
        public SpawnYMode YMode { get; set; } = SpawnYMode.Surface;
        public SpawnLightGate LightGate { get; set; } = SpawnLightGate.Any;
        public double SpawnInterval { get; set; } = 2.0;
        public List<MobSpawnRule> Rules { get; } = new();
    }

    /// <summary>Loads spawn categories from spawns.json (embedded resource or loose file).</summary>
    public static class SpawnTable
    {
        public static List<SpawnCategory> LoadDefault()
        {
            byte[]? bytes = LoadResourceBytes("spawns.json");
            if (bytes == null) throw new FileNotFoundException("spawns.json not found as an embedded resource or loose file.");
            return LoadFromJson(System.Text.Encoding.UTF8.GetString(bytes));
        }

        public static List<SpawnCategory> LoadFromJson(string json)
        {
            var categories = new List<SpawnCategory>();
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (!doc.RootElement.TryGetProperty("categories", out var cats)) return categories;

            foreach (var c in cats.EnumerateArray())
            {
                var cat = new SpawnCategory
                {
                    Id = c.TryGetProperty("id", out var id) ? id.GetString() ?? "category" : "category",
                    MaxTotal = c.TryGetProperty("maxTotal", out var mt) ? mt.GetInt32() : 20,
                    MaxPerType = c.TryGetProperty("maxPerType", out var mpt) ? mpt.GetInt32() : 12,
                    MaxPerChunk = c.TryGetProperty("maxPerChunk", out var mpc) ? mpc.GetInt32() : 6,
                    SpawnInterval = c.TryGetProperty("spawnInterval", out var si) ? si.GetDouble() : 2.0,
                };
                string yMode = c.TryGetProperty("yMode", out var ym) ? ym.GetString() ?? "surface" : "surface";
                cat.YMode = string.Equals(yMode, "depthBias", StringComparison.OrdinalIgnoreCase)
                    ? SpawnYMode.DepthBias : SpawnYMode.Surface;
                string gate = c.TryGetProperty("lightGate", out var lg) ? lg.GetString() ?? "any" : "any";
                cat.LightGate = string.Equals(gate, "bright", StringComparison.OrdinalIgnoreCase)
                    ? SpawnLightGate.Bright
                    : string.Equals(gate, "dark", StringComparison.OrdinalIgnoreCase)
                        ? SpawnLightGate.Dark : SpawnLightGate.Any;

                if (c.TryGetProperty("entries", out var entries))
                {
                    foreach (var e in entries.EnumerateArray())
                    {
                        var rule = new MobSpawnRule
                        {
                            MobId = e.TryGetProperty("mobId", out var mid) ? mid.GetString() ?? "" : "",
                            Weight = e.TryGetProperty("weight", out var w) ? w.GetInt32() : 1,
                            PackMin = e.TryGetProperty("packMin", out var pmin) ? pmin.GetInt32() : 1,
                            PackMax = e.TryGetProperty("packMax", out var pmax) ? pmax.GetInt32() : 1,
                            NearWater = e.TryGetProperty("nearWater", out var nw) && nw.GetBoolean(),
                        };
                        if (e.TryGetProperty("minY", out var minY)) rule.MinY = minY.GetInt32();
                        if (e.TryGetProperty("maxY", out var maxY)) rule.MaxY = maxY.GetInt32();
                        if (e.TryGetProperty("biomes", out var biomes))
                        {
                            foreach (var b in biomes.EnumerateArray())
                            {
                                var biomeId = b.GetString();
                                if (!string.IsNullOrEmpty(biomeId)) rule.Biomes.Add(biomeId);
                            }
                        }
                        if (!string.IsNullOrEmpty(rule.MobId)) cat.Rules.Add(rule);
                    }
                }
                if (cat.Rules.Count > 0) categories.Add(cat);
            }
            return categories;
        }

        private static byte[]? LoadResourceBytes(string fileName)
        {
            var asm = typeof(SpawnTable).Assembly;
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

    /// <summary>
    /// Natural mob spawning for one category from spawns.json.
    ///
    /// Selection pipeline per pass:
    ///  - Under the category's global/per-type caps? Skip otherwise.
    ///  - Pick a LOADED ground-layer chunk 2-7 chunks out (32-112 blocks).
    ///  - Sample the chunk's biome, filter the rule table to matching entries, weight-roll one.
    ///  - Enforce the category's per-chunk density budget (mobs already in that column).
    ///  - Roll Y per the category's mode (surface walk or depth-biased, clamped by rule MinY/MaxY).
    ///  - Jitter a few cells: solid cube below, air at and above, >= 32 from the player,
    ///    near-water requirement, and the light gate.
    /// </summary>
    public sealed class MobSpawner
    {
        private readonly SpawnCategory _category;
        private readonly Func<string, Point3D, float, bool> _spawnFn;
        private readonly Func<int> _totalCountFn;
        private readonly Func<string, int> _typeCountFn;
        private readonly Func<int, int, int> _chunkCountFn;
        private Func<int, int, BiomeDefinition>? _biomeAt;
        private const double SpawnMinDistanceSq = 32.0 * 32.0;
        private const int MaxPackMembers = 3;
        private const int SpawnPasses = 10;
        private const int WaterScanRadius = 4;
        private static readonly int WaterId = BlockRegistry.GetId("water");

        public SpawnCategory Category => _category;
        public double SpawnInterval => _category.SpawnInterval;

        public MobSpawner(SpawnCategory category,
            Func<string, Point3D, float, bool> spawnFn,
            Func<int> totalCountFn,
            Func<string, int> typeCountFn,
            Func<int, int, int> chunkCountFn,
            Func<int, int, BiomeDefinition>? biomeAt = null)
        {
            _category = category;
            _spawnFn = spawnFn;
            _totalCountFn = totalCountFn;
            _typeCountFn = typeCountFn;
            _chunkCountFn = chunkCountFn;
            _biomeAt = biomeAt;
        }

        public void SetBiomeSource(Func<int, int, BiomeDefinition>? biomeAt) => _biomeAt = biomeAt;

        /// <summary>Try to spawn a pack somewhere near the player. Returns true when something spawned.</summary>
        public bool TrySpawn(ChunkManager manager, Point3D playerPosition, Random rand, Func<int,int,int,int>? getLight = null)
        {
            if (_category.Rules.Count == 0) return false;
            if (_totalCountFn() >= _category.MaxTotal) return false;

            var rule = PickRuleForChunk(manager, playerPosition, rand, out int chunkX, out int chunkZ);
            if (rule == null) return false;
            if (_typeCountFn(rule.MobId) >= _category.MaxPerType) return false;
            if (_chunkCountFn(chunkX, chunkZ) >= _category.MaxPerChunk) return false;

            int pack = rule.PackMin + rand.Next(Math.Max(1, rule.PackMax - rule.PackMin + 1));
            pack = Math.Min(pack, MaxPackMembers);
            int room = _category.MaxPerType - _typeCountFn(rule.MobId);
            pack = Math.Min(pack, Math.Max(0, room));

            int px = chunkX * 16 + rand.Next(16);
            int pz = chunkZ * 16 + rand.Next(16);

            int py = _category.YMode == SpawnYMode.DepthBias
                ? DepthBiasY(rand, rule)
                : SurfaceY(manager, px, pz);
            if (py < 0) return false;

            // Jitter a few cells from the base point.
            int spawned = 0;
            for (int attempt = 0; attempt < 3 && spawned < pack; attempt++)
            {
                int x = px + rand.Next(6) - rand.Next(6);
                int y = py + rand.Next(1) - rand.Next(1);
                int z = pz + rand.Next(6) - rand.Next(6);

                // Valid: solid cube below, air/non-liquid at the cell and above, >= 32 from player.
                if (!IsNormalCube(manager, x, y - 1, z)) continue;
                if (manager.GetBlockAt(x, y, z) != BlockRegistry.AirId) continue;
                if (manager.GetBlockAt(x, y + 1, z) != BlockRegistry.AirId) continue;

                if (rule.NearWater && !IsNearWater(manager, x, z, y)) continue;

                if (_category.LightGate != SpawnLightGate.Any && getLight != null)
                {
                    int light = getLight(x, y, z);
                    if (_category.LightGate == SpawnLightGate.Bright)
                    {
                        if (light < 9) continue;
                    }
                    else // Dark: monsters need light <= rand(8) - caves always pass, the surface
                    {    // only passes at night when the subtracted skylight drags it down.
                        if (light > rand.Next(8)) continue;
                    }
                }

                double dx = (x + 0.5) - playerPosition.X;
                double dy = (y + 1.0) - playerPosition.Y;
                double dz = (z + 0.5) - playerPosition.Z;
                if (dx * dx + dy * dy + dz * dz < 1024.0) continue;

                if (_spawnFn(rule.MobId, new Point3D(x + 0.5, y + 1.0, z + 0.5),
                    (float)(rand.NextDouble() * Math.PI * 2.0)))
                {
                    spawned++;
                }
            }
            return spawned > 0;
        }

        /// <summary>
        /// Picks a loaded ground-layer chunk, samples its biome, and weight-rolls a rule whose
        /// biome list matches. Returns null when the table has nothing for that biome (or no
        /// loaded chunk was found).
        /// </summary>
        private MobSpawnRule? PickRuleForChunk(ChunkManager manager, Point3D playerPosition, Random rand,
            out int chunkX, out int chunkZ)
        {
            chunkX = 0; chunkZ = 0;
            if (!TryPickLoadedChunk(manager, playerPosition, rand, out chunkX, out chunkZ)) return null;

            string biomeId = "plains";
            if (_biomeAt != null)
            {
                var biome = _biomeAt(chunkX * 16 + 8, chunkZ * 16 + 8);
                biomeId = string.IsNullOrEmpty(biome.Id) ? "plains" : biome.Id;
            }

            // Build the eligible table for this biome, then weight-roll.
            int eligibleWeight = 0;
            foreach (var r in _category.Rules)
                if (r.MatchesBiome(biomeId)) eligibleWeight += r.Weight;
            if (eligibleWeight <= 0) return null;

            int roll = rand.Next(eligibleWeight);
            foreach (var r in _category.Rules)
            {
                if (!r.MatchesBiome(biomeId)) continue;
                roll -= r.Weight;
                if (roll < 0) return r;
            }
            return null;
        }

        // Picks a loaded ground-layer chunk at least 2 chunks from the player (so the 32-block
        // spawn-distance gate has a chance), and within a few chunks so mobs appear near the world.
        private static bool TryPickLoadedChunk(ChunkManager manager, Point3D playerPosition, Random rand,
            out int chunkX, out int chunkZ)
        {
            chunkX = 0; chunkZ = 0;
            int pcx = (int)Math.Floor(playerPosition.X / 16.0);
            int pcz = (int)Math.Floor(playerPosition.Z / 16.0);

            for (int attempt = 0; attempt < 24; attempt++)
            {
                int radius = 2 + rand.Next(6); // 2..7 chunks out (32..112 blocks)
                double ang = rand.NextDouble() * Math.PI * 2.0;
                int cx = pcx + (int)Math.Round(Math.Cos(ang) * radius);
                int cz = pcz + (int)Math.Round(Math.Sin(ang) * radius);
                if (manager.TryGetLoadedChunk(new ChunkCoordinates(ChunkManager.GroundLayer, cx, cz), out _))
                {
                    chunkX = cx; chunkZ = cz;
                    return true;
                }
            }
            return false;
        }

        /// <summary>Depth-biased Y: triple-nested random toward the world bottom, clamped into the rule's range.</summary>
        private static int DepthBiasY(Random rand, MobSpawnRule rule)
        {
            int py = rand.Next(rand.Next(rand.Next(112) + 8) + 8);
            if (rule.MinY.HasValue && py < rule.MinY.Value) py = rule.MinY.Value;
            if (rule.MaxY.HasValue && py > rule.MaxY.Value) py = rule.MaxY.Value;
            return py;
        }

        // Highest solid block in the column (the ground surface the mob would stand on); -1 if none.
        private static int SurfaceY(ChunkManager manager, int x, int z)
        {
            for (int y = 150; y >= 0; y--)
            {
                if (manager.GetBlockAt(x, y, z) != BlockRegistry.AirId) return y;
            }
            return -1;
        }

        // True when the block is a solid, non-transparent cube (grass/dirt/stone/cobble...).
        private static bool IsNormalCube(ChunkManager manager, int x, int y, int z)
        {
            int id = manager.GetBlockAt(x, y, z);
            return id != BlockRegistry.AirId && BlockRegistry.IsSolid(id) && BlockRegistry.IsOpaque(id);
        }

        // Water within a small radius of the spawn cell (duck ponds). Only scans the ground layer
        // around the cell - the chunk is loaded by construction so this is cheap.
        private static bool IsNearWater(ChunkManager manager, int x, int z, int y)
        {
            for (int dx = -WaterScanRadius; dx <= WaterScanRadius; dx++)
            {
                for (int dz = -WaterScanRadius; dz <= WaterScanRadius; dz++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        if (manager.GetBlockAt(x + dx, y + dy, z + dz) == WaterId) return true;
                    }
                }
            }
            return false;
        }
    }
}