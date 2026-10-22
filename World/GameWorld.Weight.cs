namespace Cubuild
{
    public sealed partial class GameWorld
    {
        /// <summary>Weight units needed for one encumbrance level. Every 8 units of ore in the
        /// inventory add a level.</summary>
        public const int WeightUnitsPerLevel = 8;

        /// <summary>Highest encumbrance level. The effects stop getting worse past this, so a
        /// player hauling a mountain of ore is no worse off than one at 3.</summary>
        public const int MaxWeightLevel = 3;

        /// <summary>Total carry weight of everything in the bag and hotbar, in weight units.</summary>
        public int CarriedWeightUnits { get; private set; }

        /// <summary>Encumbrance level 0..3 derived from <see cref="CarriedWeightUnits"/>.</summary>
        public int CarryWeightLevel { get; private set; }

        /// <summary>
        /// Recomputes the carried weight from the bag and the hotbar and derives the encumbrance
        /// level from it. Called every simulation tick so picking up or dropping ore changes the
        /// player's speed immediately.
        /// </summary>
        public void UpdateCarryWeight()
        {
            int units = 0;
            for (int i = 0; i < BagSlotCount; i++)
            {
                var slot = _bagSlots[i];
                if (slot.ItemId > 0 && slot.Count > 0)
                    units += ItemRegistry.WeightOf(slot.ItemId) * slot.Count;
            }
            for (int i = 0; i < HotbarSlots; i++)
            {
                int id = Hotbar[i];
                if (id > 0 && HotbarCounts[i] > 0)
                    units += ItemRegistry.WeightOf(id) * HotbarCounts[i];
            }
            CarriedWeightUnits = units;
            CarryWeightLevel = Math.Min(MaxWeightLevel, units / WeightUnitsPerLevel);
        }

        /// <summary>Walk-speed multiplier for an encumbrance level (1 = unencumbered).</summary>
        public static float WeightSpeedMultiplier(int level) => level switch
        {
            1 => 0.85f, // a little heavy
            2 => 0.5f,  // encumbered: half speed
            3 => 0.25f, // badly encumbered: a quarter speed
            _ => 1f,
        };

        /// <summary>Fall-damage multiplier: at the heaviest level a bad landing hurts twice as much.</summary>
        public static float WeightFallDamageMultiplier(int level) => level >= MaxWeightLevel ? 2f : 1f;

        /// <summary>True when the carried load stops the player from swimming.</summary>
        public static bool WeightBlocksSwimming(int level) => level >= MaxWeightLevel;
    }
}
