using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Cubuild
{
    /// <summary>
    /// One 2x2 crafting recipe: four input ids ("" = empty) and an output item id + count.
    /// </summary>
    public sealed class CraftingRecipe
    {
        public string[] Input { get; internal set; } = new string[4];
        public int OutputItemId { get; internal set; }
        public int OutputCount { get; internal set; } = 1;
        public string OutputName { get; internal set; } = "";
    }

    /// <summary>
    /// Data-driven 2x2 crafting recipes, loaded once at startup from recipes.json
    /// (embedded resource first, then a loose file next to the executable). Written
    /// to be hand-edited: comments and trailing commas are allowed. Recipes match in
    /// any of the four 2x2 rotations.
    /// </summary>
    public static class RecipeRegistry
    {
        private static readonly List<CraftingRecipe> _recipes = new();
        public static bool Loaded { get; private set; }
        public static IReadOnlyList<CraftingRecipe> All => _recipes;

        private sealed class RecipesFile
        {
            public List<RecipeDto> Recipes { get; set; } = new();
        }

        private sealed class RecipeDto
        {
            public List<string?> Input { get; set; } = new();
            public string Output { get; set; } = "";
            public int Count { get; set; } = 1;
        }

        /// <summary>Must run AFTER BlockRegistry and ItemRegistry (ids resolve through both).</summary>
        public static void LoadDefault()
        {
            byte[]? bytes = LoadResourceBytes("recipes.json");
            if (bytes == null) throw new FileNotFoundException("recipes.json not found as an embedded resource or loose file.");
            LoadFromJson(System.Text.Encoding.UTF8.GetString(bytes));
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
            foreach (var dto in file.Recipes)
            {
                if (string.IsNullOrWhiteSpace(dto.Output))
                    throw new InvalidDataException("A recipe in recipes.json has no \"output\".");
                if (!ItemRegistry.TryGet(dto.Output, out var outItem))
                    throw new InvalidDataException($"Recipe output \"{dto.Output}\" is not a known item/block id.");
                if (dto.Count < 1) dto.Count = 1;

                var r = new CraftingRecipe { OutputItemId = outItem.NumericId, OutputCount = dto.Count, OutputName = dto.Output };
                for (int i = 0; i < 4; i++)
                {
                    string? id = i < dto.Input.Count ? dto.Input[i] : null;
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        r.Input[i] = "";
                        continue;
                    }
                    if (!ItemRegistry.TryGet(id, out var inItem))
                        throw new InvalidDataException($"Recipe for \"{dto.Output}\" references unknown input id \"{id}\".");
                    r.Input[i] = inItem.Id;
                }
                _recipes.Add(r);
            }
            Loaded = true;
        }

        /// <summary>
        /// Matches a 2x2 grid of item ids ("" = empty) against all recipes in any rotation.
        /// Grid slots are row-major: [ top-left, top-right, bottom-left, bottom-right ].
        /// </summary>
        public static bool TryMatch(ReadOnlySpan<string> gridIds, out CraftingRecipe match)
        {
            foreach (var r in _recipes)
            {
                for (int rot = 0; rot < 4; rot++)
                {
                    if (Matches(gridIds, r.Input, rot)) { match = r; return true; }
                }
            }
            match = null!;
            return false;
        }

        // 2x2 rotation: rot 0 = as-written; 90/180/270 rotate the pattern clockwise.
        // Pattern slots: p0=p[0] p1=p[1] / p2=p[2] p3=p[3]. Rotating 90° clockwise maps
        // a,b,c,d -> c,a,d,b; 180° -> d,c,b,a; 270° -> b,d,a,c.
        private static bool Matches(ReadOnlySpan<string> grid, string[] pattern, int rot)
        {
            return rot switch
            {
                0 => Cell(grid[0], pattern[0]) && Cell(grid[1], pattern[1]) && Cell(grid[2], pattern[2]) && Cell(grid[3], pattern[3]),
                1 => Cell(grid[0], pattern[2]) && Cell(grid[1], pattern[0]) && Cell(grid[2], pattern[3]) && Cell(grid[3], pattern[1]),
                2 => Cell(grid[0], pattern[3]) && Cell(grid[1], pattern[2]) && Cell(grid[2], pattern[1]) && Cell(grid[3], pattern[0]),
                _ => Cell(grid[0], pattern[1]) && Cell(grid[1], pattern[3]) && Cell(grid[2], pattern[0]) && Cell(grid[3], pattern[2]),
            };
        }

        private static bool Cell(string gridId, string patternId)
            => string.IsNullOrEmpty(patternId) ? string.IsNullOrEmpty(gridId) : string.Equals(gridId, patternId, StringComparison.OrdinalIgnoreCase);

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
    }
}