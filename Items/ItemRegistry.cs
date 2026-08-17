using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Cubuild
{
    /// <summary>
    /// Central, data-driven catalogue of every item. Items share one numeric id space with
    /// blocks, Minecraft-style: every block automatically becomes an item (id == block id),
    /// and genuine items (tools, food, gemstones) defined in items.json are appended after the
    /// block catalogue. A stack id can therefore be resolved to either an item or a block.
    /// Must be loaded AFTER <see cref="BlockRegistry"/> so block-items can be seeded.
    /// </summary>
    public static class ItemRegistry
    {
        private static List<ItemDefinition> _items = new();
        private static Dictionary<string, int> _byName = new(StringComparer.OrdinalIgnoreCase);
        private static string[] _category = Array.Empty<string>();
        private static string[] _toolType = Array.Empty<string>();
        private static int[] _toolLevel = Array.Empty<int>();
        private static int[] _durability = Array.Empty<int>();
        private static int[] _foodValue = Array.Empty<int>();
        private static int[] _stackSize = Array.Empty<int>();
        private static bool[] _inInventory = Array.Empty<bool>();
        private static bool[] _fromItemsAtlas = Array.Empty<bool>();
        private static TextureRect[] _itemTiles = Array.Empty<TextureRect>();
        private static string[] _placedBlock = Array.Empty<string>();
        private static uint[] _mapColor = Array.Empty<uint>();
        private static bool _loaded;

        /// <summary>First numeric id that is NOT a block (genuine items start here).</summary>
        public static int ItemIdBase => BlockRegistry.Count;
        public static bool Loaded => _loaded;
        public static int Count => _items.Count;

        private sealed class ItemsFile
        {
            public List<ItemDefDto> Items { get; set; } = new();
        }

        private sealed class ItemDefDto
        {
            public string Id { get; set; } = "";
            public string? DisplayName { get; set; }
            public string? Category { get; set; }
            public string? ItemTile { get; set; }
            public int StackSize { get; set; } = 64;
            public string? PlacedBlock { get; set; }
            public bool InInventory { get; set; } = true;
            public string? ToolType { get; set; }
            public int ToolLevel { get; set; }
            public int Durability { get; set; }
            public int FoodValue { get; set; }
            public string? MapColor { get; set; }
        }

        /// <summary>Seeds every registered block as an item, then appends genuine items from
        /// items.json (embedded resource first, then loose file next to the exe).</summary>
        public static void LoadDefault()
        {
            if (_loaded) return;
            if (!BlockRegistry.Loaded)
                throw new InvalidOperationException("ItemRegistry.LoadDefault must run after BlockRegistry is loaded.");

            _items = new List<ItemDefinition>(BlockRegistry.Count + 32);
            _byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            // Every block is also an item: numeric id == block id, category "block", places
            // itself, stack size 64, and its tile comes from the terrain atlas (or the block's
            // optional items.png "itemTile" for things like flint that are really items).
            for (int i = 0; i < BlockRegistry.Count; i++)
            {
                var def = BlockRegistry.GetById(i);
                _items.Add(new ItemDefinition
                {
                    Id = def.Id,
                    NumericId = i,
                    DisplayName = def.DisplayName,
                    Category = "block",
                    ItemTile = def.ItemTile,
                    PlacedBlock = def.Id,
                    StackSize = 64,
                    InInventory = def.Inventory,
                    MapColor = def.MapColor,
                });
                _byName[def.Id] = i;
            }

            byte[]? bytes = LoadResourceBytes("items.json");
            if (bytes != null)
            {
                // items.json is hand-edited: allow // and /* */ comments + trailing commas
                // so entries stay friendly to edit by hand.
                var opts = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                };
                var file = JsonSerializer.Deserialize<ItemsFile>(System.Text.Encoding.UTF8.GetString(bytes), opts);
                if (file != null)
                {
                    foreach (var dto in file.Items)
                    {
                        if (string.IsNullOrWhiteSpace(dto.Id)) throw new InvalidDataException("Item in items.json has no id.");

                        // An item whose id matches a BLOCK redefines that block's auto-seeded
                        // item in place (e.g. the "sap" block's drop uses the sap item tile).
                        // It keeps its numeric id (== block id) so saves stay compatible, and
                        // still places its block when used. Genuine duplicate ids still throw.
                        if (_byName.TryGetValue(dto.Id, out int existingId))
                        {
                            if (existingId >= BlockRegistry.Count)
                                throw new InvalidDataException($"Duplicate item id \"{dto.Id}\".");
                            if (dto.ItemTile == null)
                                throw new InvalidDataException($"Item \"{dto.Id}\" has no itemTile (items.png atlas tile).");
                            var existing = _items[existingId];
                            existing.DisplayName = dto.DisplayName ?? existing.DisplayName;
                            existing.Category = dto.Category ?? "misc";
                            existing.ItemTile = ParseTile(dto.ItemTile);
                            existing.StackSize = dto.StackSize > 0 ? dto.StackSize : existing.StackSize;
                            existing.InInventory = dto.InInventory;
                            existing.ToolType = dto.ToolType ?? "";
                            existing.ToolLevel = dto.ToolLevel;
                            existing.Durability = dto.Durability;
                            existing.FoodValue = dto.FoodValue;
                            existing.MapColor = ParseMapColor(dto.MapColor ?? "#7F7F7F");
                            // PlacedBlock intentionally stays the block's own id: redefining the
                            // item does not change what it places. UNLESS the entry explicitly
                            // declares "placedBlock": "" - that clears placement so crafting-only
                            // blocks (like sap) can't be plopped down from the hotbar yet.
                            if (dto.PlacedBlock != null)
                            {
                                existing.PlacedBlock = string.IsNullOrEmpty(dto.PlacedBlock) ? null : dto.PlacedBlock;
                            }
                            continue;
                        }

                        TextureRect tile = dto.ItemTile != null ? ParseTile(dto.ItemTile) : default;
                        var item = new ItemDefinition
                        {
                            Id = dto.Id,
                            NumericId = _items.Count,
                            DisplayName = dto.DisplayName ?? dto.Id,
                            Category = dto.Category ?? "misc",
                            ItemTile = dto.ItemTile != null ? tile : null,
                            StackSize = dto.StackSize > 0 ? dto.StackSize : 64,
                            PlacedBlock = string.IsNullOrEmpty(dto.PlacedBlock) ? null : dto.PlacedBlock,
                            InInventory = dto.InInventory,
                            ToolType = dto.ToolType ?? "",
                            ToolLevel = dto.ToolLevel,
                            Durability = dto.Durability,
                            FoodValue = dto.FoodValue,
                            MapColor = ParseMapColor(dto.MapColor ?? "#7F7F7F"),
                        };
                        if (item.Category != "block" && dto.ItemTile == null)
                            throw new InvalidDataException($"Item \"{dto.Id}\" has no itemTile (items.png atlas tile).");
                        _items.Add(item);
                        _byName[item.Id] = item.NumericId;
                    }
                }
            }

            // Fast array lookups keyed by numeric id.
            int n = _items.Count;
            _category = new string[n];
            _toolType = new string[n];
            _toolLevel = new int[n];
            _durability = new int[n];
            _foodValue = new int[n];
            _stackSize = new int[n];
            _inInventory = new bool[n];
            _fromItemsAtlas = new bool[n];
            _itemTiles = new TextureRect[n];
            _placedBlock = new string[n];
            _mapColor = new uint[n];
            for (int i = 0; i < n; i++)
            {
                var it = _items[i];
                _category[i] = it.Category ?? "misc";
                _toolType[i] = it.ToolType ?? "";
                _toolLevel[i] = it.ToolLevel;
                _durability[i] = it.Durability;
                _foodValue[i] = it.FoodValue;
                _stackSize[i] = it.StackSize > 0 ? it.StackSize : 64;
                _inInventory[i] = it.InInventory;
                _placedBlock[i] = it.PlacedBlock ?? "";
                _mapColor[i] = it.MapColor;

                // Which atlas provides the stack icon: block-items render terrain tiles unless
                // they declare an items.png "itemTile"; genuine items always use the item atlas.
                if (it.ItemTile.HasValue)
                {
                    _fromItemsAtlas[i] = true;
                    _itemTiles[i] = it.ItemTile.Value;
                }
                else
                {
                    _fromItemsAtlas[i] = false;
                    _itemTiles[i] = BlockRegistry.GetById(i).AllTexture ?? default;
                }
            }
            _loaded = true;
        }

        /// <summary>Resolves a stack id to the block it PLACES, or -1 when the item has no block
        /// behavior (tools, food, gemstones). Block-items place themselves.</summary>
        public static int ResolveBlockId(int itemId)
        {
            if (itemId < 0 || itemId >= _items.Count) return -1;
            string placed = _placedBlock[itemId];
            if (string.IsNullOrEmpty(placed)) return -1;
            return BlockRegistry.TryGet(placed, out var def) ? def.NumericId : -1;
        }

        // ---- Lookups -------------------------------------------------------------

        public static ItemDefinition Get(int itemId) => _items[itemId];
        public static bool TryGet(string name, out ItemDefinition def)
        {
            if (_byName.TryGetValue(name, out int id)) { def = _items[id]; return true; }
            def = null!;
            return false;
        }
        public static int GetId(string name) => _byName[name];
        public static string GetName(int itemId) => _items[itemId].Id;

        /// <summary>Atlas tile for a stack id. out fromItemsAtlas: true = items.png, false = terrain.png.</summary>
        public static TextureRect GetTile(int itemId, out bool fromItemsAtlas)
        {
            fromItemsAtlas = false;
            if (itemId < 0 || itemId >= _items.Count) return default;
            fromItemsAtlas = _fromItemsAtlas[itemId];
            return _itemTiles[itemId];
        }

        public static bool IsBlockItem(int itemId) => itemId >= 0 && itemId < BlockRegistry.Count;
        public static string CategoryOf(int itemId) => itemId >= 0 && itemId < _items.Count ? _category[itemId] : "misc";
        public static string ToolTypeOf(int itemId) => itemId >= 0 && itemId < _items.Count ? _toolType[itemId] : "";
        public static int ToolLevelOf(int itemId) => itemId >= 0 && itemId < _items.Count ? _toolLevel[itemId] : 0;
        public static int DurabilityOf(int itemId) => itemId >= 0 && itemId < _items.Count ? _durability[itemId] : 0;
        public static int FoodValueOf(int itemId) => itemId >= 0 && itemId < _items.Count ? _foodValue[itemId] : 0;
        public static int StackSizeOf(int itemId) => itemId >= 0 && itemId < _items.Count ? _stackSize[itemId] : 64;
        public static bool IsInInventory(int itemId) => itemId >= 0 && itemId < _items.Count && _inInventory[itemId];
        public static uint MapColorOf(int itemId) => itemId >= 0 && itemId < _items.Count ? _mapColor[itemId] : 0;

        // ---- Parsers -------------------------------------------------------------

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

        private static TextureRect ParseTile(string s)
        {
            var parts = s.Split(',');
            if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int col)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int row))
            {
                throw new InvalidDataException($"Bad item tile \"{s}\" (expected \"col,row\").");
            }
            return new TextureRect(col * BlockRegistry.TileSize, row * BlockRegistry.TileSize, BlockRegistry.TileSize, BlockRegistry.TileSize);
        }

        private static uint ParseMapColor(string s)
        {
            if (s.StartsWith("#", StringComparison.Ordinal)) s = s.Substring(1);
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
    }
}