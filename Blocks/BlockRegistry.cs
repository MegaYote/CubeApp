using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Cubuild
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
        private static bool[] _door = Array.Empty<bool>();
    private static bool[] _rail = Array.Empty<bool>();
        private static bool[] _inventory = Array.Empty<bool>();
        private static bool[] _placeable = Array.Empty<bool>();
        private static bool[] _gravity = Array.Empty<bool>();
        private static int[] _lightEmission = Array.Empty<int>();
        private static float[] _alpha = Array.Empty<float>();
        private static float[] _hardness = Array.Empty<float>();
        private static string[] _toolType = Array.Empty<string>();
        private static string[] _material = Array.Empty<string>();
        private static string[] _wrongToolDrop = Array.Empty<string>();
        private static int[] _dropCountMin = Array.Empty<int>();
        private static int[] _dropCountMax = Array.Empty<int>();
        private static int[] _weight = Array.Empty<int>();
        private static bool[] _toolRequired = Array.Empty<bool>();
        private static int[] _harvestLevel = Array.Empty<int>();
        private static bool[] _zombieCanBreak = Array.Empty<bool>();
        private static ZombieBreakSpeed[] _zombieBreakSpeed = Array.Empty<ZombieBreakSpeed>();
        private static uint[] _mapColor = Array.Empty<uint>();
        private static int[] _hotbar = Array.Empty<int>();
        public static bool Loaded { get; private set; }
        public static int Count { get; private set; }
        public static bool IsMinecraftMode { get; private set; }

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
            // Nullable on purpose: when a field is omitted the value comes from smart
            // per-shape defaults (see ApplyShapeDefaults), so entries can stay tiny.
            // An explicitly written value ALWAYS wins over the shape default.
            public bool? Solid { get; set; }
            public bool? Opaque { get; set; }
            public bool? Transparent { get; set; }
            public double? Alpha { get; set; }
            public string? Shape { get; set; }
            public bool? Inventory { get; set; }
            public bool Placeable { get; set; } = true;
            public bool? Translucent { get; set; }
            public int LightEmission { get; set; } = 0;
            public bool Gravity { get; set; } = false;
            public string? MapColor { get; set; }
            public double Hardness { get; set; } = 0; // 0 => code default
            public string? ToolType { get; set; } = ""; // "pickaxe"/"axe"/"shovel"/... empty = none
            public string? Material { get; set; }
            public string? WrongToolDrop { get; set; }
            public bool ToolRequired { get; set; } = false; // true = needs the tool to drop items
            public int HarvestLevel { get; set; } = 0; // minimum matching tool level needed for drops
            public string? DropItem { get; set; } // optional item id dropped instead of this block
            public int DropCountMin { get; set; } = 0; // smallest number of items a break drops (0 = one)
            public int DropCountMax { get; set; } = 0; // largest number of items a break drops (0 = one)
            public int Weight { get; set; } = 0; // carry weight of this block's item, in weight units
            public bool ZombieCanBreak { get; set; } = false;
            public string? ZombieBreakSpeed { get; set; } = "Medium"; // Slow / Medium / Fast
            public string? Base { get; set; } // inherit texture/top/bottom/side/colour from another block id

            // --- Door properties ---
            public string? DoorHalf { get; set; } = "bottom"; // "bottom" or "top"
            public string? DoorHinge { get; set; } = "left";   // "left" or "right"
            public bool? DoorOpen { get; set; } = false;
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
                "dirt" or "gravel" or "redclay" or "sand" => 0.7f,
                "leaves" or "sapling" or "red_flower" or "yellow_flower" or "blue_flower"
                    or "red_mushroom" or "white_mushroom" or "poison_mushroom" or "spikes" or "sap" => 0.2f,
                "planks" or "treated_planks" or "log" or "bookshelf" or "workbench" or "rottingwood"
                    or "rottingwood_2" or "rottingwood_3" or "rottingwood_4" or "rottingwood_5"
                    or "rubberblock" or "corn_block" or "carpenters_workbench" or "masons_workbench" => 2.8f,
                "stone" or "cobblestone" or "mossycobblestone" or "darkstone" or "serpentine" => 4.8f,
                "smoothstone" or "tiledstone" => 5.0f,
                "ceramic" or "bricks" or "bluebrick" or "greenbrick" or "yellowbrick" or "pinkbrick" or "cyanbrick"
                    or "blackbrick" or "whitebrick" or "purple_bricks" or "orange_bricks" or "cobble_bricks" or "gray_bricks" or "kiln" or "forge" => 6.5f,
                "quartz" or "shimmerrock" or "cobbled_quartz" => 7.0f,
                "coalore" or "ironore" or "goldore" or "diamondore" or "bluestoneore" or "copperore" => 5.0f,
                "iron" or "gold" or "diamond" or "copper" => 8.0f,
                "obsidian" => 9f,
                "glass" => 0.3f,
                "bomb" => 0f,
                "sponge" => 0.6f,
                _ => 1f,
            };
        }

        /// <summary>
        /// Fills omitted fields from the entry's "shape" so common blocks stay one line:
        /// cross plants just say <c>{"id": "sapling", "texture": "15,0", "shape": "cross"}</c>.
        /// Fields written explicitly in the JSON always win over these defaults.
        /// </summary>
        private static void ApplyShapeDefaults(BlockDefDto dto)
        {
            string shape = dto.Shape ?? "";
            bool isCross = string.Equals(shape, "cross", StringComparison.OrdinalIgnoreCase);
            bool isCutout = string.Equals(shape, "cutout", StringComparison.OrdinalIgnoreCase);
            bool isGlass = string.Equals(shape, "glass", StringComparison.OrdinalIgnoreCase);
            bool isPartial = string.Equals(shape, "slab", StringComparison.OrdinalIgnoreCase)
                || string.Equals(shape, "slab_top", StringComparison.OrdinalIgnoreCase)
                || string.Equals(shape, "stairs", StringComparison.OrdinalIgnoreCase)
                || string.Equals(shape, "door", StringComparison.OrdinalIgnoreCase);

            bool isDoor = string.Equals(shape, "door", StringComparison.OrdinalIgnoreCase);

            dto.Solid ??= !isCross;                                    // cross plants don't collide
            dto.Opaque ??= !(isCross || isCutout || isGlass || isPartial); // anything see-through lets light pass
            dto.Transparent ??= isCross || isCutout || isGlass || isDoor; // hide internal faces between like blocks
            dto.Alpha ??= isCross ? 0.95 : 1.0;                        // cross atlas tiles have baked-in background
            dto.Translucent ??= false;
            dto.Inventory ??= !string.Equals(shape, "slab_top", StringComparison.OrdinalIgnoreCase)
                && !isDoor; // _top halves and door tops are placement-only

            // Door-specific defaults
            if (isDoor)
            {
                dto.DoorHalf ??= "bottom";
                dto.DoorHinge ??= "left";
                dto.DoorOpen ??= false;
            }
        }

        /// <summary>Recursively copies tiles + map colour from a "base" block so variants like
        /// slabs/stairs inherit their material. Chain-safe (base of base), cycle-checked.</summary>
        private static void ResolveBase(BlockDefinition def, Dictionary<string, string?> baseOf, HashSet<string> seen)
        {
            if (!baseOf.TryGetValue(def.Id, out var baseId) || baseId == null) return;
            if (!seen.Add(def.Id))
                throw new InvalidDataException($"Circular \"base\" chain involving \"{def.Id}\".");
            if (!_byName.TryGetValue(baseId, out var baseDef))
                throw new InvalidDataException($"Block \"{def.Id}\" references unknown base \"{baseId}\".");

            // The base inherits FIRST so a grandchild gets the deepest material's tiles.
            ResolveBase(baseDef, baseOf, seen);

            def.AllTexture ??= baseDef.AllTexture;
            def.TopTexture ??= baseDef.TopTexture;
            def.BottomTexture ??= baseDef.BottomTexture;
            def.SideTexture ??= baseDef.SideTexture;
            def.ItemTile ??= baseDef.ItemTile;
            if (def.MapColor == 0xFF000000u) def.MapColor = baseDef.MapColor; // unset (default black) => inherit
        }

        /// <summary>Loads the catalogue from a JSON string. Throws on the first malformed block
        /// (unknown tile reference, missing air, duplicate id) so bad data fails loudly at startup.</summary>
        public static void LoadFromJson(string json)
        {
            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                // blocks.json is hand-edited: allow // and /* */ comments (docs, examples,
                // section headers) and trailing commas so a new entry can be pasted after the
                // last one without fixing up the comma. Both are simply skipped by the parser.
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };
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
            var baseOf = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < file.Blocks.Count; i++)
            {
                var dto = file.Blocks[i];
                if (string.IsNullOrWhiteSpace(dto.Id))
                    throw new InvalidDataException($"Block at index {i} has no id.");
                if (_byName.ContainsKey(dto.Id))
                    throw new InvalidDataException($"Duplicate block id \"{dto.Id}\".");

                ApplyShapeDefaults(dto); // fill omitted fields from the entry's "shape"

                string material = (dto.Material ?? "").Trim().ToLowerInvariant();
                if (material.Length > 0 && material is not ("rock" or "soil" or "wood" or "plant" or "misc" or "fluid" or "nothing"))
                    throw new InvalidDataException($"Block \"{dto.Id}\" has unknown material \"{dto.Material}\".");
                string wrongToolDrop = (dto.WrongToolDrop ?? "").Trim().ToLowerInvariant();
                if (wrongToolDrop.Length > 0 && wrongToolDrop is not ("self" or "nothing"))
                    throw new InvalidDataException($"Block \"{dto.Id}\" has invalid wrongToolDrop \"{dto.WrongToolDrop}\".");
                if (material == "rock" && wrongToolDrop.Length == 0)
                    throw new InvalidDataException($"Rock block \"{dto.Id}\" must define wrongToolDrop as \"self\" or \"nothing\".");

                var def = new BlockDefinition
                {
                    Id = dto.Id,
                    NumericId = i,
                    DisplayName = dto.DisplayName ?? dto.Id,
                    Solid = dto.Solid ?? true,
                    Opaque = dto.Opaque ?? true,
                    Transparent = dto.Transparent ?? false,
                    Alpha = (float)(dto.Alpha ?? 1.0),
                    Shape = dto.Shape ?? "",
                    Inventory = dto.Inventory ?? true,
                    Placeable = dto.Placeable,
                    Translucent = dto.Translucent ?? false,
                    LightEmission = dto.LightEmission,
                    Gravity = dto.Gravity,
                    MapColor = dto.MapColor != null ? ParseMapColor(dto.MapColor) : 0xFF000000u,
                    Hardness = dto.Hardness > 0 ? (float)dto.Hardness : DefaultHardness(dto.Id),
                    Material = material,
                    WrongToolDrop = wrongToolDrop,
                    ToolType = dto.ToolType ?? "",
                    ToolRequired = dto.ToolRequired,
                    HarvestLevel = Math.Max(0, dto.HarvestLevel),
                    DropItem = dto.DropItem ?? "",
                    DropCountMin = Math.Max(0, dto.DropCountMin),
                    DropCountMax = Math.Max(0, dto.DropCountMax),
                    Weight = Math.Max(0, dto.Weight),
                    ZombieCanBreak = dto.ZombieCanBreak,
                    ZombieBreakSpeed = ParseZombieBreakSpeed(dto.ZombieBreakSpeed),
                    DoorHalf = dto.DoorHalf ?? "bottom",
                    DoorHinge = dto.DoorHinge ?? "left",
                    DoorOpen = dto.DoorOpen ?? false,
                };

                // Tiles are parsed here when present; entries with only a "base" get their
                // tiles filled in the resolution pass below, so the "no texture" check for
                // non-air blocks happens after that.
                if (dto.Texture != null) def.AllTexture = ParseTile(dto.Texture);
                if (dto.Top != null) def.TopTexture = ParseTile(dto.Top);
                if (dto.Bottom != null) def.BottomTexture = ParseTile(dto.Bottom);
                if (dto.Side != null) def.SideTexture = ParseTile(dto.Side);
                if (dto.ItemTile != null) def.ItemTile = ParseTile(dto.ItemTile);

                if (dto.Base != null) baseOf[dto.Id] = dto.Base;
                defs[i] = def;
                _byName[dto.Id] = def;
            }

            // Second pass: resolve "base" inheritance (tiles + map colour), deepest first.
            foreach (var def in defs)
                ResolveBase(def, baseOf, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            for (int i = 1; i < defs.Length; i++)
            {
                if (defs[i].AllTexture == null)
                    throw new InvalidDataException($"Block \"{defs[i].Id}\" has no texture (add \"texture\": \"col,row\", or inherit one via \"base\").");
            }

            _defs = defs;
            Count = defs.Length;
            _solid = new bool[Count];
            _opaque = new bool[Count];
            _transparent = new bool[Count];
            _alpha = new float[Count];
            _hardness = new float[Count];
            _toolType = new string[Count];
            _material = new string[Count];
            _wrongToolDrop = new string[Count];
            _dropCountMin = new int[Count];
            _dropCountMax = new int[Count];
            _weight = new int[Count];
            _toolRequired = new bool[Count];
            _harvestLevel = new int[Count];
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
            _door = new bool[Count];
        _rail = new bool[Count];
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
                _material[i] = defs[i].Material;
                _wrongToolDrop[i] = defs[i].WrongToolDrop;
                _dropCountMin[i] = defs[i].DropCountMin;
                _dropCountMax[i] = defs[i].DropCountMax;
                _weight[i] = defs[i].Weight;
                _toolRequired[i] = defs[i].ToolRequired;
                _harvestLevel[i] = defs[i].HarvestLevel;
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
            _door[i] = string.Equals(defs[i].Shape, "door", StringComparison.OrdinalIgnoreCase);
        _rail[i] = string.Equals(defs[i].Shape, "rail", StringComparison.OrdinalIgnoreCase);
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
            // Bumped on every successful load. Switching mode swaps the whole block table
            // (the two packs disagree at most numeric ids), so anything that CACHES data
            // derived from block ids - notably the software-rendered inventory icon atlas -
            // must compare this to know its copy is stale.
            LoadGeneration++;
        }

        /// <summary>Increments each time the registry is (re)loaded. See <see cref="LoadGeneration"/>.</summary>
        public static int LoadGeneration { get; private set; }

        /// <summary>Loads blocks.json the same way other resources load: embedded resource first
        /// (so the self-contained .exe carries it), then a loose file next to the executable.</summary>
        public static void LoadDefault() => LoadFromFolder("Assets/Data");

        public static void LoadFromFolder(string folder)
        {
            bool wantsMinecraft = folder.IndexOf("Minecraft", StringComparison.OrdinalIgnoreCase) >= 0;
            byte[]? bytes = LoadResourceBytes(folder, "blocks.json");
            bool foundMinecraft = bytes != null && wantsMinecraft;
            if (bytes == null && wantsMinecraft)
                bytes = LoadResourceBytes("Assets/Data", "blocks.json");
            if (bytes == null)
                throw new FileNotFoundException("blocks.json not found as an embedded resource or loose file.");
            LoadFromJson(System.Text.Encoding.UTF8.GetString(bytes));
            IsMinecraftMode = foundMinecraft;
        }

        private static byte[]? LoadResourceBytes(string fileName) => LoadResourceBytes("Assets/Data", fileName);

        private static byte[]? LoadResourceBytes(string folder, string fileName)
        {
            string suffix = folder.Replace("/", ".").Replace("\\", ".") + "." + fileName;
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            foreach (var name in asm.GetManifestResourceNames())
            {
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
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
            // loose file fallback
            string folderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, folder, fileName);
            if (File.Exists(folderPath)) return File.ReadAllBytes(folderPath);
            string direct = Path.Combine(folder, fileName);
            if (File.Exists(direct)) return File.ReadAllBytes(direct);
            string baseFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
            if (File.Exists(baseFile)) return File.ReadAllBytes(baseFile);
            if (File.Exists(fileName)) return File.ReadAllBytes(fileName);
            return null;
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
        public static bool IsDoor(int id) => id > 0 && id < Count && _door[id];

        /// <summary>Rail: a flat slab lying on the floor of its cell, one sixteenth high.</summary>
        /// <summary>
        /// How tall a rail block stands, in blocks. One sixteenth, like Minecraft. This is the
        /// RENDER height: the mesh is a wafer-thin quad lying on the floor of its cell.
        /// </summary>
        public const double RailHeight = 1.0 / 16.0;

        /// <summary>
        /// Height of a rail's clickable volume, in blocks. A rail renders as almost nothing, and
        /// aiming at a 1/16 sliver is miserable - so the selectable box is a shallow quarter
        /// block, half a slab's height. Deep enough to hit reliably, shallow enough that the air
        /// above a track stays selectable for building.
        /// </summary>
        public const double RailPickHeight = 0.25;
        public static bool IsRail(int id) => id > 0 && id < Count && _rail[id];
        public static bool IsPartialShape(int id) => id > 0 && id < Count && (_slab[id] || _slabTop[id] || _stair[id] || _door[id] || _rail[id]);
        public static bool IsInInventory(int id) => id >= 0 && id < Count && _inventory[id];
        public static bool IsPlaceable(int id) => id > 0 && id < Count && _placeable[id];
        public static bool IsGravity(int id) => id > 0 && id < Count && _gravity[id];
        public static int LightEmissionOf(int id) => id > 0 && id < Count ? _lightEmission[id] : 0;
        public static float Alpha(int id) => _alpha[id];
        public static float HardnessOf(int id) => id >= 0 && id < Count ? _hardness[id] : 1f;
        /// <summary>Material class used for tool-family behavior; empty for legacy definitions.</summary>
        public static string MaterialOf(int id) => id >= 0 && id < Count ? _material[id] : "";
        public static bool HasMaterialOf(int id) => id >= 0 && id < Count && _material[id].Length > 0;
        /// <summary>Drop policy for a rock mined without a pickaxe ("self" or "nothing").</summary>
        public static string WrongToolDropOf(int id) => id > 0 && id < Count ? _wrongToolDrop[id] : "";
        /// <summary>Tool type that mines this block most efficiently ("" = none/any tool).
        /// Material classes take precedence; old toolType data is used only when material is absent.</summary>
        public static string ToolTypeOf(int id)
        {
            if (id < 0 || id >= Count) return "";
            return _material[id] switch
            {
                "rock" => "pickaxe",
                "soil" => "shovel",
                "wood" => "axe",
                "" => _toolType[id],
                _ => "",
            };
        }
        /// <summary>True when the matching tool is required for this block to drop an item.</summary>
        public static bool ToolRequiredOf(int id) => id > 0 && id < Count && _toolRequired[id];
        /// <summary>Minimum matching tool tier needed to receive a drop from this block.</summary>
        public static int HarvestLevelOf(int id) => id > 0 && id < Count ? _harvestLevel[id] : 0;
        /// <summary>Optional item id dropped instead of this block's own item.</summary>
        public static string DropItemOf(int id) => id > 0 && id < Count ? _defs[id].DropItem : "";
        /// <summary>Smallest number of items a correct-tool break of this block drops (0 = one).</summary>
        public static int DropCountMinOf(int id) => id > 0 && id < Count ? _dropCountMin[id] : 0;
        /// <summary>Largest number of items a correct-tool break of this block drops (0 = one).</summary>
        public static int DropCountMaxOf(int id) => id > 0 && id < Count ? _dropCountMax[id] : 0;
        /// <summary>Carry weight of this block's item, in weight units (0 = weightless).</summary>
        public static int WeightOf(int id) => id > 0 && id < Count ? _weight[id] : 0;

        // ---- Doors ---------------------------------------------------------------
        //
        // A door occupies TWO cells (bottom + top half) and its per-instance state lives in
        // the existing per-cell meta byte, packed as:
        //
        //   bits 0-1 : facing  (0=North/-Z, 1=East/+X, 2=South/+Z, 3=West/-X)
        //   bit  2   : hinge side (0 = low side of the width axis, 1 = high side)
        //   bit  3   : open      (0 = closed, 1 = open / swung 90 degrees)
        //
        // NOTE: this facing encoding is NOT the same as StairFacingMeta's (which uses
        // 0=East, 1=West, 2=South, 3=North). Use DoorFacingFromNormal when converting a
        // raycast face normal, and never feed a StairFacingMeta result in directly.
        // Meta is already persisted to the save file and shipped over the network, so doors
        // need no format change.

        public const double DoorThickness = 3.0 / 16.0; // 0.1875 - matches Minecraft's door panel

        public const int DoorFacingMask = 0x3;
        public const int DoorHingeBit = 0x4;
        public const int DoorOpenBit = 0x8;

        /// <summary>Which way the door's panel faces: 0=North(-Z), 1=East(+X), 2=South(+Z), 3=West(-X).</summary>
        public static int DoorFacing(int meta) => meta & DoorFacingMask;
        /// <summary>True when the hinge is on the high side of the door's width axis.</summary>
        public static bool DoorRightHinge(int meta) => (meta & DoorHingeBit) != 0;
        public static bool IsDoorOpen(int meta) => (meta & DoorOpenBit) != 0;

        /// <summary>Packs door state into a meta byte.</summary>
        public static byte MakeDoorMeta(int facing, bool rightHinge, bool open)
            => (byte)((facing & DoorFacingMask)
                      | (rightHinge ? DoorHingeBit : 0)
                      | (open ? DoorOpenBit : 0));

        /// <summary>True when the two meta bytes describe the same door orientation (open state aside).</summary>
        public static bool DoorOrientationMatches(int a, int b) => (a & ~DoorOpenBit) == (b & ~DoorOpenBit);

        /// <summary>
        /// The cell-relative AABB of one door half's panel. Closed doors sit flush against the
        /// wall on the facing side, spanning the full width of the cell; open doors swing 90
        /// degrees about their hinge edge and so run perpendicular to the closed orientation.
        /// Both halves use the same X/Z bounds (the door is a straight vertical panel).
        /// </summary>
        public static void GetDoorBounds(int meta, out double minX, out double minZ, out double maxX, out double maxZ)
        {
            const double t = DoorThickness;
            int facing = DoorFacing(meta);
            bool high = DoorRightHinge(meta);
            bool open = IsDoorOpen(meta);

            // facings 0 (N) and 2 (S) have their panel normal along Z, so their width axis is X.
            // facings 1 (E) and 3 (W) have their panel normal along X, so their width axis is Z.
            bool widthOnX = facing == 0 || facing == 2;

            if (open)
            {
                // Swung perpendicular: thin along the width axis, pinned to the hinge edge.
                if (widthOnX)
                {
                    minX = high ? 1.0 - t : 0.0;
                    maxX = high ? 1.0 : t;
                    minZ = 0.0;
                    maxZ = 1.0;
                }
                else
                {
                    minZ = high ? 1.0 - t : 0.0;
                    maxZ = high ? 1.0 : t;
                    minX = 0.0;
                    maxX = 1.0;
                }
            }
            else
            {
                // Closed: thin along the facing axis, full width across it.
                if (widthOnX)
                {
                    minX = 0.0;
                    maxX = 1.0;
                    minZ = (facing == 0) ? 0.0 : 1.0 - t; // north -> -Z edge, south -> +Z edge
                    maxZ = (facing == 0) ? t : 1.0;
                }
                else
                {
                    minZ = 0.0;
                    maxZ = 1.0;
                    minX = (facing == 3) ? 0.0 : 1.0 - t; // west -> -X edge, east -> +X edge
                    maxX = (facing == 3) ? t : 1.0;
                }
            }
        }

        /// <summary>True when the block id is the top half of a door (as opposed to the bottom).</summary>
        public static bool IsDoorTop(int id) => IsDoor(id) && _defs[id].DoorHalf == "top";

        /// <summary>
        /// The other half of a door pair, given either half's id. Doors are declared as a
        /// "&lt;material&gt;_door_bottom" / "&lt;material&gt;_door_top" pair; this resolves the
        /// counterpart by name. Returns -1 when the id isn't a door or has no partner.
        /// </summary>
        public static int DoorOtherHalfId(int id)
        {
            if (!IsDoor(id)) return -1;
            string name = _defs[id].Id;
            string suffix = IsDoorTop(id) ? "_top" : "_bottom";
            if (!name.EndsWith(suffix, StringComparison.Ordinal)) return -1;
            string otherName = name[..^suffix.Length] + (IsDoorTop(id) ? "_bottom" : "_top");
            for (int i = 1; i < Count; i++)
            {
                if (string.Equals(_defs[i].Id, otherName, StringComparison.Ordinal) && IsDoor(i)) return i;
            }
            return -1;
        }

        /// <summary>
        /// The door facing for a raycast face normal. The normal points from the wall toward
        /// the free cell the door will occupy, so the door faces back along -normal: clicking
        /// a wall's east face puts a west-facing door in the cell to its east.
        /// </summary>
        public static int DoorFacingFromNormal(Point3D normal)
        {
            if (normal.X > 0) return 3; // wall to the west -> door faces west
            if (normal.X < 0) return 1; // wall to the east  -> door faces east
            if (normal.Z > 0) return 0; // wall to the south -> door faces north
            return 2;                   // wall to the north -> door faces south
        }

        /// <summary>The world-space direction the closed panel's thin axis points (unit, one of +-X/+-Z).</summary>
        public static Point3D DoorFacingDirection(int facing) => facing switch
        {
            0 => new Point3D(0, 0, -1),
            1 => new Point3D(1, 0, 0),
            2 => new Point3D(0, 0, 1),
            _ => new Point3D(-1, 0, 0),
        };

        /// <summary>
        /// Minecraft's hinge rule, simplified: the hinge goes on the side of the doorway the
        /// player is standing on, so the door swings away from them and leaves the opening clear.
        /// The hinge must sit on the door's width axis, which is X for north/south-facing
        /// doors and Z for east/west-facing ones.
        /// </summary>
        public static bool DoorHingeOnHighSide(int facing, double playerX, double playerZ, int cellX, int cellZ)
            => facing == 0 || facing == 2
                ? playerX > cellX + 0.5
                : playerZ > cellZ + 0.5;

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
