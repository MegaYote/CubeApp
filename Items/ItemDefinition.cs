namespace Cubuild
{
    /// <summary>
    /// A fully data-driven item definition loaded from items.json. Items live in the same
    /// numeric id space as blocks: every block automatically becomes an item (id == block id),
    /// and genuine items (tools, food, gemstones) are appended after the block catalogue.
    /// A stack in the inventory is one of these ids + a count, exactly like Minecraft.
    /// </summary>
    public sealed class ItemDefinition
    {
        /// <summary>Stable string id ("apple", "stone_pickaxe"). For block-items this is the
        /// owning block's id.</summary>
        public string Id { get; set; } = "";
        /// <summary>Numeric id used by inventory stacks, dropped items and the hotbar.</summary>
        public int NumericId { get; set; }
        public string DisplayName { get; set; } = "";
        /// <summary>Item category: "block" (auto), "tool", "food", "gemstone", "misc".</summary>
        public string Category { get; set; } = "misc";
        /// <summary>items.png atlas tile (16x16). Block-items use the block's own tile instead.</summary>
        public TextureRect? ItemTile { get; set; }
        /// <summary>Max stack size (Minecraft: tools 1, food/gems 64).</summary>
        public int StackSize { get; set; } = 64;
        /// <summary>Block this item places when used; null/"" = not placeable (tools, food, gems).</summary>
        public string? PlacedBlock { get; set; }
        /// <summary>Shows up in the E-menu creative item list.</summary>
        public bool InInventory { get; set; } = true;
        /// <summary>Mining tool family this item belongs to ("pickaxe"/"axe"/"shovel"). Empty = not a tool.</summary>
        public string ToolType { get; set; } = "";
        /// <summary>Mining power tier (1 = wood, 2 = stone, 3 = iron, ...). Higher breaks harder blocks.</summary>
        public int ToolLevel { get; set; } = 0;
        /// <summary>Uses before the tool breaks. 0 = unbreakable.</summary>
        public int Durability { get; set; } = 0;
        /// <summary>Hunger/health restored when eaten. 0 = not edible.</summary>
        public int FoodValue { get; set; } = 0;
        /// <summary>Swatch colour for the E-menu/debug, ImGui packed U32.</summary>
        public uint MapColor { get; set; }
    }
}