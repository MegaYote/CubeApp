using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace CubeApp
{
    /// <summary>
    /// Central, data-driven catalogue of every block, loaded once at startup from blocks.json.
    /// Blocks are addressed by string id in code/data (e.g. <c>BlockRegistry.GetId("grass")</c>)
    /// and by numeric id in hot storage (the chunk's <c>byte[]</c>); air is reserved numeric id 0.
    /// Hot-path lookups are array-indexed by numeric id so the mesher/lighting/collision code never
    /// pays for a dictionary or an enum switch.
    /// </summary>
    public static class BlockRegistry
    {
        public const int AirId = 0;
        public const int TileSize = 16; // atlas tiles are 16x16 (terrain.png is a 16x16 tile grid)

        private static Dictionary<string, BlockDefinition> _byName = new();
        private static BlockDefinition[] _defs = Array.Empty<BlockDefinition>();
        // Array-indexed fast flags keyed by numeric id, sized to Count (so any valid id is in range).
        private static bool[] _solid = Array.Empty<bool>();
        private static bool[] _opaque = Array.Empty<bool>();
        private static bool[] _transparent = Array.Empty<bool>();
        private static bool[] _cross = Array.Empty<bool>();
        private static bool[] _cutout = Array.Empty<bool>();
        private static bool[] _glass = Array.Empty<bool>();
        private static bool[] _translucent = Array.Empty<bool>();
        private static bool[] _slab = Array.Empty<bool>();
        private static bool[] _slabTop = Array.Empty<bool>();
        private static bool[] _stair = Array.Empty<bool>();
        private static bool[] _inventory = Array.Empty<bool>();
        private static bool[] _placeable = Array.Empty<bool>();
        private static bool[] _gravity = Array.Empty<bool>();
        private static int[] _lightEmission = Array.Empty<int>();
        private static float[] _alpha = Array.Empty<float>();
        private static float[] _hardness = Array.Empty<float>();
        private static string[] _toolType = Array.Empty<string>();
        private static bool[] _toolRequired = Array.Empty<bool>();
        private static bool[] _zombieCanBreak = Array.Empty<bool>();
        private static ZombieBreakSpeed[] _zombieBreakSpeed = Array.Empty<ZombieBreakSpeed>();
        private static uint[] _mapColor = Array.Empty<uint>();
        private static int[] _hotbar = Array.Empty<int>();
        public static bool Loaded { get; private set; }
        public static int Count { get; private set; }

        // ---- Deserialization DTOs ------------------------------------------------

        private sealed class BlocksFile
        {
            public List<string> Hotbar { get; set; } = new();
            public List<BlockDefDto> Blocks { get; set; } = new();
        }

        private sealed class BlockDefDto
        {
            public string Id { get; set; } = "";
            public string? DisplayName { get; set; }
            public string? Texture { get; set; }
            public string? Top { get; set; }
            public string? Bottom { get; set; }
            public string? Side { get; set; }
            public string? ItemTile { get; set; }
            public bool Solid { get; set; } = true;
            public bool Opaque { get; set; } = true;
            public bool Transparent { get; set; } = false;
            public double Alpha { get; set; } = 1.0;
            public string? Shape { get; set; }
            public bool Inventory { get; set; } = true;
            public bool Placeable { get; set; } = true;
            public bool Translucent { get; set; } = false;
            public int LightEmission { get; set; } = 0;
            public bool Gravity { get; set; } = false;
            public string? MapColor { get; set; } = "#000000";
            public double Hardness { get; set; } = 0; // 0 => code default
            public string? ToolType { get; set; } = ""; // "pickaxe"/"axe"/"shovel"/... empty = none
            public bool ToolRequired { get; set; } = false; // true = needs the tool to drop items
            public bool ZombieCanBreak { get; set; } = false;
            public string? ZombieBreakSpeed { get; set; } = "Medium"; // Slow / Medium / Fast
        }

        /// <summary>Default survival-mining hardness (Cubuild C++ port). Blocks that share an id
        /// family (slabs/stairs/_top) inherit the base block's value. Bedrock is unbreakable.</summary>
        private static float DefaultHardness(string id)
        {
            string baseId = id
                .Replace("_slab_top", "").Replace("_slab", "").Replace("_stairs", "")
                .Replace("_top", "");
            return baseId switch
            {
                "air" => 0f,
                "bedrock" => float.PositiveInfinity,
                "water" => 0f,
                "grass" or "grass_spreading" or "full_grass" => 0.6f,
                "dirt" or "gravel" or "redclay" or "sand" => 0.5f,
                "leaves" or "sapling" or "red_flower" or "yellow_flower" or "blue_flower"
                    or "red_mushroom" or "white_mushroom" or "poison_mushroom" or "spikes" or "sap" => 0.2f,
                "planks" or "treated_planks" or "log" or "bookshelf" or "workbench" or "rottingwood"
                    or "rottingwood_2" or "rottingwood_3" or "rottingwood_4" or "rottingwood_5"
                    or "rubberblock" or "cage" or "corn_block" => 2f,
                "stone" or "cobblestone" or "mossycobblestone" or "smoothstone" or "shimmerrock"
                    or "quartz" or "tiledstone" or "darkstone" => 4f,
                "bricks" or "bluebrick" or "greenbrick" or "yellowbrick" or "pinkbrick" or "cyanbrick"
                    or "blackbrick" or "whitebrick" => 4f,
                "coalore" => 3f,
                "ironore" => 3f,
                "goldore" => 3f,
                "diamondore" or "bluestoneore" or "copperore" => 3f,
                "iron" or "gold" or "diamond" or "copper" => 5f,
                "obsidian" => 8f,
                "glass" => 0.3f,
                "bomb" => 0f,
                "sponge" => 0.6f,
                _ => 1f,
            };
        }

        /// <summary>Loads the catalogue from a JSON string. Throws on the first malformed block
        /// (unknown tile reference, missing air, duplicate id) so bad data fails loudly at startup.</summary>
        public static void LoadFromJson(string json)
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var file = JsonSerializer.Deserialize<BlocksFile>(json, opts)
                ?? throw new InvalidDataException("blocks.json is empty or malformed.");

            if (file.Blocks.Count == 0)
                throw new InvalidDataException("blocks.json defines no blocks.");
            if (file.Blocks.Count > 256)
                throw new InvalidDataException($"blocks.json defines {file.Blocks.Count} blocks; max is 256 (byte storage).");
            if (!string.Equals(file.Blocks[0].Id, "air", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The first block in blocks.json must be \"air\" (numeric id 0).");

            var defs = new BlockDefinition[file.Blocks.Count];
            _byName = new Dictionary<string, BlockDefinition>(file.Blocks.Count, StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < file.Blocks.Count; i++)
            {
                var dto = file.Blocks[i];
                if (string.IsNullOrWhiteSpace(dto.Id))
                    throw new InvalidDataException($"Block at index {i} has no id.");
                if (_byName.ContainsKey(dto.Id))
                    throw new InvalidDataException($"Duplicate block id \"{dto.Id}\".");

                var def = new BlockDefinition
                {
                    Id = dto.Id,
                    NumericId = i,
                    DisplayName = dto.DisplayName ?? dto.Id,
                    Solid = dto.Solid,
                    Opaque = dto.Opaque,
                    Transparent = dto.Transparent,
                    Alpha = (float)dto.Alpha,
                    Shape = dto.Shape ?? "",
                    Inventory = dto.Inventory,
                    Placeable = dto.Placeable,
                    Translucent = dto.Translucent,
                    LightEmission = dto.LightEmission,
                    Gravity = dto.Gravity,
                    MapColor = ParseMapColor(dto.MapColor ?? "#000000"),
                    Hardness = dto.Hardness > 0 ? (float)dto.Hardness : DefaultHardness(dto.Id),
                    ToolType = dto.ToolType ?? "",
                    ToolRequired = dto.ToolRequired,
                    ZombieCanBreak = dto.ZombieCanBreak,
                    ZombieBreakSpeed = ParseZombieBreakSpeed(dto.ZombieBreakSpeed),
                };

                // air has no faces; everything else must define at least an "all" tile.
                if (i != 0)
                {
                    def.AllTexture = dto.Texture != null ? ParseTile(dto.Texture) : throw new InvalidDataException($"Block \"{dto.Id}\" has no texture.");
                    if (dto.Top != null) def.TopTexture = ParseTile(dto.Top);
                    if (dto.Bottom != null) def.BottomTexture = ParseTile(dto.Bottom);
                    if (dto.Side != null) def.SideTexture = ParseTile(dto.Side);
                    if (dto.ItemTile != null) def.ItemTile = ParseTile(dto.ItemTile);
                }

                defs[i] = def;
                _byName[dto.Id] = def;
            }

            _defs = defs;
            Count = defs.Length;
            _solid = new bool[Count];
            _opaque = new bool[Count];
            _transparent = new bool[Count];
            _alpha = new float[Count];
            _hardness = new float[Count];
            _toolType = new string[Count];
            _toolRequired = new bool[Count];
            _zombieCanBreak = new bool[Count];
            _zombieBreakSpeed = new ZombieBreakSpeed[Count];
            _mapColor = new uint[Count];
            _cross = new bool[Count];
            _cutout = new bool[Count];
            _glass = new bool[Count];
            _translucent = new bool[Count];
            _slab = new bool[Count];
            _slabTop = new bool[Count];
            _stair = new bool[Count];
            _inventory = new bool[Count];
            _placeable = new bool[Count];
            _gravity = new bool[Count];
            _lightEmission = new int[Count];
            for (int i = 0; i < Count; i++)
            {
                _solid[i] = defs[i].Solid;
                _opaque[i] = defs[i].Opaque;
                _transparent[i] = defs[i].Transparent;
                _alpha[i] = defs[i].Alpha;
                _hardness[i] = defs[i].Hardness;
                _toolType[i] = defs[i].ToolType ?? "";
                _toolRequired[i] = defs[i].ToolRequired;
                _zombieCanBreak[i] = defs[i].ZombieCanBreak;
                _zombieBreakSpeed[i] = defs[i].ZombieBreakSpeed;
                _mapColor[i] = defs[i].MapColor;
                _inventory[i] = defs[i].Inventory;
                _placeable[i] = defs[i].Placeable;
                _lightEmission[i] = defs[i].LightEmission;
                _cross[i] = string.Equals(defs[i].Shape, "cross", StringComparison.OrdinalIgnoreCase);
                _cutout[i] = _cross[i] || string.Equals(defs[i].Shape, "cutout", StringComparison.OrdinalIgnoreCase);
                _glass[i] = string.Equals(defs[i].Shape, "glass", StringComparison.OrdinalIgnoreCase);
                _translucent[i] = defs[i].Translucent;
                _slab[i] = string.Equals(defs[i].Shape, "slab", StringComparison.OrdinalIgnoreCase);
                _slabTop[i] = string.Equals(defs[i].Shape, "slab_top", StringComparison.OrdinalIgnoreCase);
                _stair[i] = string.Equals(defs[i].Shape, "stairs", StringComparison.OrdinalIgnoreCase);
                _gravity[i] = defs[i].Gravity;
            }

            _hotbar = new int[Math.Min(file.Hotbar.Count, 10)];
            for (int i = 0; i < _hotbar.Length; i++)
            {
                if (!_byName.TryGetValue(file.Hotbar[i], out var def))
                    throw new InvalidDataException($"Hotbar references unknown block \"{file.Hotbar[i]}\".");
                _hotbar[i] = def.NumericId;
            }

            Loaded = true;
        }

        /// <summary>Loads blocks.json the same way other resources load: embedded resource first
        /// (so the self-contained .exe carries it), then a loose file next to the executable.</summary>
        public static void LoadDefault()
        {
            byte[]? bytes = LoadResourceBytes("blocks.json");
            if (bytes == null)
                throw new FileNotFoundException("blocks.json not found as an embedded resource or loose file.");
            LoadFromJson(System.Text.Encoding.UTF8.GetString(bytes));
        }

        private static byte[]? LoadResourceBytes(string fileName)
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
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

        // ---- Lookups -------------------------------------------------------------

        public static BlockDefinition Get(string name) => _byName[name];
        public static bool TryGet(string name, out BlockDefinition def) => _byName.TryGetValue(name, out def!);
        public static int GetId(string name) => _byName[name].NumericId;
        public static BlockDefinition GetById(int id) => _defs[id];
        public static string GetName(int id) => _defs[id].Id;

        // Array-indexed hot lookups (NUMERIC id in range 0..Count-1).
        public static bool IsSolid(int id) => id > 0 && id < Count && _solid[id];
        public static bool IsOpaque(int id) => id >= 0 && id < Count && _opaque[id];
        public static bool IsTransparent(int id) => id >= 0 && id < Count && _transparent[id];
        public static bool IsCross(int id) => id > 0 && id < Count && _cross[id];
        public static bool IsCutout(int id) => id > 0 && id < Count && _cutout[id];
        public static bool IsGlass(int id) => id > 0 && id < Count && _glass[id];
        public static bool IsTranslucent(int id) => id > 0 && id < Count && _translucent[id];
        public static bool IsSlab(int id) => id > 0 && id < Count && _slab[id];
        public static bool IsSlabTop(int id) => id > 0 && id < Count && _slabTop[id];
        public static bool IsStair(int id) => id > 0 && id < Count && _stair[id];
        public static bool IsPartialShape(int id) => id > 0 && id < Count && (_slab[id] || _slabTop[id] || _stair[id]);
        public static bool IsInInventory(int id) => id >= 0 && id < Count && _inventory[id];
        public static bool IsPlaceable(int id) => id > 0 && id < Count && _placeable[id];
        public static bool IsGravity(int id) => id > 0 && id < Count && _gravity[id];
        public static int LightEmissionOf(int id) => id > 0 && id < Count ? _lightEmission[id] : 0;
        public static float Alpha(int id) => _alpha[id];
        public static float HardnessOf(int id) => id >= 0 && id < Count ? _hardness[id] : 1f;
        /// <summary>Tool type that mines this block most efficiently ("" = none/any tool).</summary>
        public static string ToolTypeOf(int id) => id > 0 && id < Count ? _toolType[id] : "";
        /// <summary>True when the matching tool is required for this block to drop an item.</summary>
        public static bool ToolRequiredOf(int id) => id > 0 && id < Count && _toolRequired[id];

        public static bool ZombieCanBreakOf(int id) => id > 0 && id < Count && _zombieCanBreak[id];

        public static ZombieBreakSpeed ZombieBreakSpeedOf(int id)
            => id > 0 && id < Count ? _zombieBreakSpeed[id] : ZombieBreakSpeed.Medium;
        public static uint MapColorOf(int id) => _mapColor[id];
        public static TextureRect FaceTexture(int id, Point3D normal) => _defs[id].FaceTexture(normal);

        public static IReadOnlyList<int> Hotbar => _hotbar;

        // ---- Parsers -------------------------------------------------------------

        /// <summary>Parses a "col,row" atlas tile string into a TextureRect at (col*16, row*16, 16, 16).</summary>
        private static TextureRect ParseTile(string s)
        {
            var parts = s.Split(',');
            if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int col)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int row))
            {
                throw new InvalidDataException($"Bad tile \"{s}\" (expected \"col,row\").");
            }
            return new TextureRect(col * TileSize, row * TileSize, TileSize, TileSize);
        }

        /// <summary>Parses a "#RRGGBB" hex colour into an ImGui-packed U32 (0xAABBGGRR, full alpha).</summary>
        private static uint ParseMapColor(string s)
        {
            if (s.StartsWith("#", StringComparison.Ordinal)) s = s.Substring(1);
            // Accept RRGGBB or RRGGBBAA; default alpha to 255.
            int r, g, b, a = 255;
            if (s.Length == 6)
            {
                r = Convert.ToInt32(s.Substring(0, 2), 16);
                g = Convert.ToInt32(s.Substring(2, 2), 16);
                b = Convert.ToInt32(s.Substring(4, 2), 16);
            }
            else if (s.Length == 8)
            {
                r = Convert.ToInt32(s.Substring(0, 2), 16);
                g = Convert.ToInt32(s.Substring(2, 2), 16);
                b = Convert.ToInt32(s.Substring(4, 2), 16);
                a = Convert.ToInt32(s.Substring(6, 2), 16);
            }
            else
            {
                throw new InvalidDataException($"Bad mapColor \"{s}\" (expected #RRGGBB or #RRGGBBAA).");
            }
            return ((uint)a << 24) | ((uint)b << 16) | ((uint)g << 8) | (uint)r;
        }

        private static ZombieBreakSpeed ParseZombieBreakSpeed(string? s)
        {
            if (string.IsNullOrEmpty(s)) return ZombieBreakSpeed.Medium;
            if (s.StartsWith("F", StringComparison.OrdinalIgnoreCase)) return ZombieBreakSpeed.Fast;
            if (s.StartsWith("S", StringComparison.OrdinalIgnoreCase)) return ZombieBreakSpeed.Slow;
            return ZombieBreakSpeed.Medium;
        }
    }
}