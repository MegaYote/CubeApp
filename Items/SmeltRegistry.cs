using System;
using System.Collections.Generic;
using System.IO;

namespace Cubuild
{
    /// <summary>One furnace/kiln recipe: an input item id smelts into an output item id.</summary>
    public sealed class SmeltRecipe
    {
        public int InputItemId { get; internal set; }
        public int OutputItemId { get; internal set; }
        /// <summary>Seconds of burn to produce one item.</summary>
        public float Time { get; internal set; } = 10f;
        public string OutputName { get; internal set; } = "";
    }

    /// <summary>
    /// Kiln/furnace recipes, parsed out of the SAME recipes.json as crafting (its "smelting"
    /// array) by <see cref="RecipeRegistry.LoadFromJson"/>, so there is only ever one data
    /// file and one loader per mode. Must be loaded after ItemRegistry so ids resolve.
    /// </summary>
    public static class SmeltRegistry
    {
        /// <summary>JSON shape of one smelting entry.</summary>
        public sealed class Dto
        {
            public string Input { get; set; } = "";
            public string Output { get; set; } = "";
            public float Time { get; set; } = 10f;
        }

        private static readonly Dictionary<int, SmeltRecipe> _recipes = new();
        private static readonly Dictionary<int, float> _fuelSeconds = new();
        public static bool Loaded { get; private set; }
        public static int Count => _recipes.Count;

        /// <summary>Consumes the "smelting" array produced by the recipes.json parse.</summary>
        public static void LoadFromDtos(List<Dto>? entries)
        {
            _recipes.Clear();
            _fuelSeconds.Clear();
            if (entries != null)
            {
                foreach (var dto in entries)
                {
                    if (string.IsNullOrWhiteSpace(dto.Input) || string.IsNullOrWhiteSpace(dto.Output)) continue;
                    if (!ItemRegistry.TryGet(dto.Input, out var inItem))
                        throw new InvalidDataException($"Smelting recipe references unknown input id \"{dto.Input}\".");
                    if (!ItemRegistry.TryGet(dto.Output, out var outItem))
                        throw new InvalidDataException($"Smelting recipe references unknown output id \"{dto.Output}\".");
                    _recipes[inItem.NumericId] = new SmeltRecipe
                    {
                        InputItemId = inItem.NumericId,
                        OutputItemId = outItem.NumericId,
                        Time = dto.Time <= 0 ? 10f : dto.Time,
                        OutputName = dto.Output,
                    };
                }
            }
            // Burn times in seconds per unit, Minecraft-relative: a lump of coal smelts eight
            // items, a plank smelts one and a half, a stick half of one.
            RegisterFuel("coal", 80f);
            RegisterFuel("charcoal", 80f);
            RegisterFuel("planks", 15f);
            RegisterFuel("log", 15f);
            RegisterFuel("sapling", 15f);
            RegisterFuel("stick", 5f);
            Loaded = true;
        }

        private static void RegisterFuel(string id, float seconds)
        {
            if (ItemRegistry.TryGet(id, out var item) && item.NumericId > 0)
                _fuelSeconds[item.NumericId] = seconds;
        }

        /// <summary>The recipe for an input item, or null when it doesn't smelt.</summary>
        public static SmeltRecipe? Find(int itemId)
            => itemId > 0 && _recipes.TryGetValue(itemId, out var r) ? r : null;

        /// <summary>Burn seconds one unit of this item provides. 0 means it isn't fuel.</summary>
        public static float FuelSeconds(int itemId)
            => itemId > 0 && _fuelSeconds.TryGetValue(itemId, out var s) ? s : 0f;

        /// <summary>True when this item can be burnt as kiln fuel.</summary>
        public static bool IsFuel(int itemId) => FuelSeconds(itemId) > 0f;
    }
}