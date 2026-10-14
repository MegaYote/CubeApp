using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Cubuild
{
    /// <summary>
    /// One crafting recipe. A recipe is either <see cref="Size"/>x<see cref="Size"/>: write 4
    /// (or fewer) input ids for a 2x2 recipe, or 9 for a 3x3 one. Empty string ids are empty
    /// slots and are matched positionally, so the pattern's shape matters.
    /// </summary>
    public sealed class CraftingRecipe
    {
        /// <summary>Input ids, row-major in a Size x Size box (always 9 long for convenience).</summary>
        public string[] Input { get; internal set; } = new string[9];
        /// <summary>2 or 3 - the box this recipe occupies.</summary>
        public int Size { get; internal set; } = 2;
        public int OutputItemId { get; internal set; }
        public int OutputCount { get; internal set; } = 1;
        public string OutputName { get; internal set; } = "";
    }

    /// <summary>
    /// Data-driven crafting recipes, loaded once at startup from recipes.json
    /// (embedded resource first, then a loose file next to the executable). Written
    /// to be hand-edited: comments and trailing commas are allowed. A recipe with 4 or
    /// fewer inputs is 2x2; 9 inputs makes it 3x3. Recipes match at any offset within the
    /// grid and in all 8 symmetries (4 rotations x mirrored/not), like Minecraft.
    /// </summary>
    public static class RecipeRegistry
    {
        private static readonly List<CraftingRecipe> _recipes = new();
        public static bool Loaded { get; private set; }
        public static IReadOnlyList<CraftingRecipe> All => _recipes;
        public static bool IsMinecraftMode { get; private set; }

        private sealed class RecipesFile
        {
            public List<RecipeDto> Recipes { get; set; } = new();
            /// <summary>Kiln/furnace recipes from the same file (see <see cref="SmeltRegistry"/>).</summary>
            public List<SmeltRegistry.Dto> Smelting { get; set; } = new();
        }

        private sealed class RecipeDto
        {
            public List<string?> Input { get; set; } = new();
            public string Output { get; set; } = "";
            public int Count { get; set; } = 1;
        }

        /// <summary>Must run AFTER BlockRegistry and ItemRegistry (ids resolve through both).</summary>
        public static void LoadDefault() => LoadFromFolder("Assets/Data");

        public static void LoadFromFolder(string folder)
        {
            bool wantsMinecraft = folder.IndexOf("Minecraft", StringComparison.OrdinalIgnoreCase) >= 0;
            byte[]? bytes = LoadResourceBytes(folder, "recipes.json");
            bool foundMinecraft = bytes != null && wantsMinecraft;
            if (bytes == null && wantsMinecraft)
                bytes = LoadResourceBytes("Assets/Data", "recipes.json");
            if (bytes == null) throw new FileNotFoundException("recipes.json not found as an embedded resource or loose file.");
            LoadFromJson(System.Text.Encoding.UTF8.GetString(bytes));
            IsMinecraftMode = foundMinecraft;
        }

        public static void LoadFromJson(string json)
        {
            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                // Hand-edited data file: skip // and /* */ comments, allow trailing commas.
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };
            var file = JsonSerializer.Deserialize<RecipesFile>(json, opts)
                ?? throw new InvalidDataException("recipes.json is empty or malformed.");

            _recipes.Clear();
            SmeltRegistry.LoadFromDtos(file.Smelting);
            foreach (var dto in file.Recipes)
            {
                if (string.IsNullOrWhiteSpace(dto.Output))
                    throw new InvalidDataException("A recipe in recipes.json has no \"output\".");
                if (!ItemRegistry.TryGet(dto.Output, out var outItem))
                    throw new InvalidDataException($"Recipe output \"{dto.Output}\" is not a known item/block id.");
                if (dto.Count < 1) dto.Count = 1;

                var r = new CraftingRecipe { OutputItemId = outItem.NumericId, OutputCount = dto.Count, OutputName = dto.Output };
                // 9 inputs -> a 3x3 recipe; anything shorter stays 2x2.
                r.Size = dto.Input.Count > 4 ? 3 : 2;
                for (int i = 0; i < 9; i++) r.Input[i] = "";
                for (int i = 0; i < dto.Input.Count && i < 9; i++)
                {
                    string? id = dto.Input[i];
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    if (!ItemRegistry.TryGet(id, out var inItem))
                        throw new InvalidDataException($"Recipe for \"{dto.Output}\" references unknown input id \"{id}\".");
                    // The JSON list is row-major at the recipe's OWN width - 2 wide for a 2x2
                    // recipe, 3 wide for a 3x3 one - while Input is always a 3-wide grid.
                    int sx = (i % r.Size), sy = (i / r.Size);
                    r.Input[sy * 3 + sx] = inItem.Id;
                }
                _recipes.Add(r);
            }
            Loaded = true;
        }

        /// <summary>
        /// Matches a crafting grid against every recipe. <paramref name="grid"/> is always the
        /// full 3x3 backing array in row-major order (9 entries); <paramref name="gridSize"/>
        /// is the currently active box (2 or 3) - only that corner of the grid is considered.
        ///
        /// Matching follows Minecraft exactly: the occupied cells of the grid form a bounding
        /// box, the recipe pattern is normalized to its own bounding box, and the pattern must
        /// cover that box COMPLETELY after one of 8 symmetries (4 rotations x mirrored/not).
        /// Requiring full coverage is what stops a 2x2 recipe from firing inside a larger shape
        /// - six planks in a 2x3 column is a door, not two planks stacked into sticks.
        /// </summary>
        public static bool TryMatch(ReadOnlySpan<string> grid, int gridSize, out CraftingRecipe match)
        {
            match = null!;
            if (grid.Length < 9 || gridSize < 1 || gridSize > 3) return false;

            // Bounding box of what's actually in the live corner of the grid.
            int minX = gridSize, minY = gridSize, maxX = -1, maxY = -1;
            for (int y = 0; y < gridSize; y++)
            for (int x = 0; x < gridSize; x++)
            {
                if (grid[y * 3 + x].Length == 0) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
            if (maxX < 0) return false;                    // empty grid: nothing matches
            int gw = maxX - minX + 1, gh = maxY - minY + 1;

            foreach (var r in _recipes)
            {
                if (r.Size > gridSize) continue;           // 3x3 recipes need a table

                // Normalize the pattern to its own bounding box.
                int pminX = 3, pminY = 3, pmaxX = -1, pmaxY = -1;
                for (int i = 0; i < 9; i++)
                {
                    if (r.Input[i].Length == 0) continue;
                    int px = i % 3, py = i / 3;
                    if (px < pminX) pminX = px;
                    if (px > pmaxX) pmaxX = px;
                    if (py < pminY) pminY = py;
                    if (py > pmaxY) pmaxY = py;
                }
                if (pmaxX < 0) continue;                   // all-empty pattern: nonsense
                int pw = pmaxX - pminX + 1, ph = pmaxY - pminY + 1;

                for (int mirror = 0; mirror < 2; mirror++)
                for (int rot = 0; rot < 4; rot++)
                {
                    // An odd number of quarter-turns swaps the box's width and height.
                    int tw = (rot & 1) == 1 ? ph : pw;
                    int th = (rot & 1) == 1 ? pw : ph;
                    if (tw != gw || th != gh) continue;
                    if (MatchesBox(grid, r.Input, pminX, pminY, pw, ph, minX, minY, mirror, rot))
                    {
                        match = r;
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Compares one symmetry of the normalized pattern against the grid's bounding box.
        /// Each pattern cell is mirrored (optionally) about the normalized box's vertical
        /// axis, rotated clockwise <c>rot</c> quarter-turns, then landed at
        /// (boxX + tx, boxY + ty). The caller has already checked that the transformed box
        /// is the same size as the grid box, so this is a bijection over it - every occupied
        /// grid cell is therefore compared, and none is left unchecked.
        /// </summary>
        private static bool MatchesBox(ReadOnlySpan<string> grid, string[] pattern,
            int pminX, int pminY, int pw, int ph, int boxX, int boxY, int mirror, int rot)
        {
            for (int i = 0; i < 9; i++)
            {
                int ax = i % 3, ay = i / 3;
                // Cells outside the pattern's own bounding box are empty by definition.
                if (ax < pminX || ax > pminX + pw - 1 || ay < pminY || ay > pminY + ph - 1) continue;

                int x = ax - pminX, y = ay - pminY;
                if (mirror == 1) x = pw - 1 - x;

                // Clockwise quarter-turns: (x, y) -> (h - 1 - y, x), swapping the box dims.
                int cw = pw, ch = ph;
                for (int k = 0; k < rot; k++)
                {
                    int nx = ch - 1 - y;
                    int ny = x;
                    x = nx; y = ny;
                    int t = cw; cw = ch; ch = t;
                }

                if (!Cell(grid[(boxY + y) * 3 + (boxX + x)], pattern[i])) return false;
            }
            return true;
        }

        private static bool Cell(string gridId, string patternId)
            => string.IsNullOrEmpty(patternId) ? string.IsNullOrEmpty(gridId) : string.Equals(gridId, patternId, StringComparison.OrdinalIgnoreCase);

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
            string folderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, folder, fileName);
            if (File.Exists(folderPath)) return File.ReadAllBytes(folderPath);
            string direct = Path.Combine(folder, fileName);
            if (File.Exists(direct)) return File.ReadAllBytes(direct);
            string baseFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
            if (File.Exists(baseFile)) return File.ReadAllBytes(baseFile);
            if (File.Exists(fileName)) return File.ReadAllBytes(fileName);
            return null;
        }
    }
}