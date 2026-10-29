using System;
using System.Collections.Generic;

namespace Cubuild
{
    public sealed partial class SteveMob
    {
        private enum PrivateItemStatus { Ready, NeedGather, Impossible }

        private const int HandStationCode = 1;
        private const int CarpenterStationCode = 2;
        private const int MasonryStationCode = 3;

        private bool _hasToolGatherTarget;
        private int _toolGatherItemId;
        private int _toolGatherX, _toolGatherY, _toolGatherZ;
        private int _toolGatherStandX, _toolGatherStandY, _toolGatherStandZ;
        private int _toolGatherOutputCount;

        private readonly struct ToolCraftOption
        {
            public readonly int OutputItemId;
            public readonly int GridSize;
            public readonly string Station;
            public readonly int StationCode;
            public readonly int ProjectMaterialInputs;
            public readonly int ToolLevel;
            public readonly float MiningSpeed;

            public ToolCraftOption(int outputItemId, int gridSize, string station, int stationCode,
                int projectMaterialInputs, int toolLevel, float miningSpeed)
            {
                OutputItemId = outputItemId;
                GridSize = gridSize;
                Station = station;
                StationCode = stationCode;
                ProjectMaterialInputs = projectMaterialInputs;
                ToolLevel = toolLevel;
                MiningSpeed = miningSpeed;
            }
        }

        /// <summary>
        /// Tries to prepare a family-matching tool for the current real gather target. Missing
        /// ingredients are gathered only through Steve's loaded-world resource helper; crafting
        /// always stages against his private inventory and active recipe table.
        /// </summary>
        private bool TryPrepareToolUpgrade(GameWorld world, int targetBlockId)
        {
            if (_toolUpgradeRetryTimer > 0f) return false;
            string family = BlockRegistry.ToolTypeOf(targetBlockId);
            if (string.IsNullOrWhiteSpace(family))
            {
                string material = BlockRegistry.MaterialOf(targetBlockId);
                if (string.Equals(material, "rock", StringComparison.OrdinalIgnoreCase)) family = "pickaxe";
                else if (string.Equals(BlockRegistry.GetName(targetBlockId), "log", StringComparison.OrdinalIgnoreCase)) family = "axe";
            }
            if (string.IsNullOrWhiteSpace(family)) return false;

            // Any matching tool is useful here; among several private tools, mining time always
            // uses the highest level and then the highest declared speed.
            if (GetBestHeldTool(family) > 0)
            {
                if (_workMode == WorkMode.ToolUp)
                {
                    SetWorkMode(WorkMode.Gather);
                    _taskExecutor.SetPlan();
                }
                return false;
            }

            var options = GetToolCraftOptions(family);
            foreach (var option in options)
            {
                bool ingredientsReady = true;
                bool needsGather = false;
                int gatherItemId = 0;
                foreach (var ingredient in GetRecipeIngredients(option.OutputItemId,
                    option.GridSize, option.Station))
                {
                    var status = EnsurePrivateItem(world, ingredient.Key, ingredient.Value, 0,
                        new HashSet<int>(), out int missingItemId);
                    if (status == PrivateItemStatus.NeedGather)
                    {
                        needsGather = true;
                        gatherItemId = missingItemId;
                        break;
                    }
                    if (status == PrivateItemStatus.Impossible)
                    {
                        ingredientsReady = false;
                        break;
                    }
                }

                if (needsGather)
                {
                    if (BeginToolInputGather(world, gatherItemId)) return true;
                    continue;
                }
                if (!ingredientsReady) continue;

                int stationX = 0, stationY = 0, stationZ = 0;
                int stationStandX = 0, stationStandY = 0, stationStandZ = 0;
                if (option.GridSize == 3
                    && !world.TryFindSteveCraftingStation(this, option.Station,
                        out stationX, out stationY, out stationZ,
                        out stationStandX, out stationStandY, out stationStandZ))
                {
                    int stationItemId = GetSteveStationItemId(world, option.Station);
                    if (stationItemId <= 0) continue;
                    var stationStatus = EnsurePrivateItem(world, stationItemId, 1, 0,
                        new HashSet<int>(), out int gatherStationItemId);
                    if (stationStatus == PrivateItemStatus.NeedGather)
                    {
                        if (BeginToolInputGather(world, gatherStationItemId)) return true;
                        continue;
                    }
                    if (stationStatus != PrivateItemStatus.Ready) continue;
                    if (ShelterInventoryCount(stationItemId) <= 0) continue;
                    if (TryPlaceSteveCraftingStation(world, stationItemId)) return true;
                    continue;
                }

                SetWorkMode(WorkMode.ToolUp);
                if (option.GridSize == 2)
                {
                    if (TryCraftShelterItem(option.OutputItemId, option.GridSize, option.Station))
                        return true;
                    continue;
                }

                _taskExecutor.SetPlan(
                    AgentTask.Navigate(stationStandX, stationStandY, stationStandZ),
                    AgentTask.Craft(option.OutputItemId, option.GridSize, option.StationCode,
                        stationX, stationY, stationZ));
                PrepareShelterTasks();
                return true;
            }

            if (_workMode == WorkMode.ToolUp)
            {
                SetWorkMode(WorkMode.Gather);
                _taskExecutor.SetPlan();
            }
            // Avoid rescanning the loaded region for the same unavailable recipe chain every
            // simulation tick. The real project gather task continues during this short pause.
            _toolUpgradeRetryTimer = 3f;
            return false;
        }

        private List<ToolCraftOption> GetToolCraftOptions(string family)
        {
            var options = new List<ToolCraftOption>();
            foreach (var recipe in RecipeRegistry.All)
            {
                int outputId = recipe.OutputItemId;
                if (outputId <= 0 || outputId >= ItemRegistry.Count
                    || !string.Equals(ItemRegistry.ToolTypeOf(outputId), family, StringComparison.OrdinalIgnoreCase))
                    continue;

                int gridSize;
                string station;
                int stationCode;
                if (recipe.Size <= 2)
                {
                    gridSize = 2;
                    station = "hand";
                    stationCode = HandStationCode;
                }
                else
                {
                    gridSize = 3;
                    station = string.IsNullOrWhiteSpace(recipe.Station) ? "carpenter" : recipe.Station;
                    if (string.Equals(station, "carpenter", StringComparison.OrdinalIgnoreCase))
                        stationCode = CarpenterStationCode;
                    else if (string.Equals(station, "masonry", StringComparison.OrdinalIgnoreCase))
                        stationCode = MasonryStationCode;
                    else continue;
                }

                if (!RecipeRegistry.TryMatch(recipe.Input, gridSize, station, out var actual)
                    || actual.OutputItemId != outputId) continue;
                int projectInputs = CountRecipeInputs(recipe, _projectMaterialItemId, gridSize);
                options.Add(new ToolCraftOption(outputId, gridSize, station, stationCode,
                    projectInputs, ItemRegistry.ToolLevelOf(outputId),
                    ItemRegistry.MiningSpeedOf(outputId)));
            }

            // Prefer recipes that do not consume this project's reserved material, then choose
            // the strongest available tool among otherwise equivalent legal options.
            options.Sort((a, b) =>
            {
                int byProjectMaterial = a.ProjectMaterialInputs.CompareTo(b.ProjectMaterialInputs);
                if (byProjectMaterial != 0) return byProjectMaterial;
                int byLevel = b.ToolLevel.CompareTo(a.ToolLevel);
                return byLevel != 0 ? byLevel : b.MiningSpeed.CompareTo(a.MiningSpeed);
            });
            return options;
        }

        private static int CountRecipeInputs(CraftingRecipe recipe, int itemId, int gridSize)
        {
            if (itemId <= 0) return 0;
            int count = 0;
            for (int y = 0; y < gridSize; y++)
            for (int x = 0; x < gridSize; x++)
                if (string.Equals(recipe.Input[y * 3 + x], ItemRegistry.GetName(itemId),
                    StringComparison.OrdinalIgnoreCase)) count++;
            return count;
        }

        private Dictionary<int, int> GetRecipeIngredients(int outputItemId, int gridSize, string station)
        {
            var ingredients = new Dictionary<int, int>();
            foreach (var recipe in RecipeRegistry.All)
            {
                if (recipe.OutputItemId != outputItemId || recipe.Size > gridSize) continue;
                if (!RecipeRegistry.TryMatch(recipe.Input, gridSize, station, out var actual)
                    || actual.OutputItemId != outputItemId) continue;
                return CountRecipeIngredients(actual, gridSize);
            }
            return ingredients;
        }

        private PrivateItemStatus EnsurePrivateItem(GameWorld world, int itemId, int needed,
            int depth, HashSet<int> visiting, out int gatherItemId)
        {
            gatherItemId = 0;
            if (itemId <= 0 || needed <= 0 || depth > 6) return PrivateItemStatus.Impossible;
            if (AvailablePrivateCount(itemId) >= needed) return PrivateItemStatus.Ready;
            if (!visiting.Add(itemId)) return PrivateItemStatus.Impossible;

            try
            {
                foreach (var recipe in RecipeRegistry.All)
                {
                    if (recipe.OutputItemId != itemId || recipe.Size > 2) continue;
                    if (!RecipeRegistry.TryMatch(recipe.Input, 2, "hand", out var actual)
                        || actual.OutputItemId != itemId) continue;

                    var ingredients = CountRecipeIngredients(recipe, 2);
                    bool possible = true;
                    foreach (var ingredient in ingredients)
                    {
                        var status = EnsurePrivateItem(world, ingredient.Key, ingredient.Value,
                            depth + 1, visiting, out int missing);
                        if (status == PrivateItemStatus.NeedGather)
                        {
                            gatherItemId = missing;
                            return status;
                        }
                        if (status == PrivateItemStatus.Impossible)
                        {
                            possible = false;
                            break;
                        }
                    }
                    if (!possible) continue;

                    int attempts = 0;
                    while (AvailablePrivateCount(itemId) < needed && attempts++ < 8
                        && TryCraftShelterItem(itemId, 2, "hand")) { }
                    if (AvailablePrivateCount(itemId) >= needed) return PrivateItemStatus.Ready;
                }

                if (IsSteveGatherableMaterial(itemId)
                    && TryFindGatherTarget(world, itemId, out var target)
                    && CanAcceptShelterItem(itemId, target.OutputCount))
                {
                    gatherItemId = itemId;
                    return PrivateItemStatus.NeedGather;
                }
                return PrivateItemStatus.Impossible;
            }
            finally
            {
                visiting.Remove(itemId);
            }
        }

        private static Dictionary<int, int> CountRecipeIngredients(CraftingRecipe recipe, int gridSize)
        {
            var result = new Dictionary<int, int>();
            for (int y = 0; y < gridSize; y++)
            for (int x = 0; x < gridSize; x++)
            {
                string name = recipe.Input[y * 3 + x];
                if (name.Length == 0) continue;
                int ingredientId = ItemRegistry.GetId(name);
                result[ingredientId] = result.TryGetValue(ingredientId, out int count) ? count + 1 : 1;
            }
            return result;
        }

        /// <summary>
        /// Returns how many of this physical drop can still help the committed shelter project or
        /// the best currently feasible tool recipe. It deliberately does not expose generic
        /// inventory capacity as demand: unrelated drops are never collected just because a slot
        /// happens to be empty.
        /// </summary>
        internal int GetUsefulShelterDropDemand(GameWorld world, int itemId)
        {
            if (!world.IsAuthoritative || _blueprintIndex < 0
                || itemId <= 0 || itemId >= ItemRegistry.Count) return 0;

            var demands = BuildUsefulShelterDropDemands(world);
            if (!demands.TryGetValue(itemId, out int wanted)) return 0;
            return Math.Min(Math.Max(0, wanted), GetShelterItemCapacity(itemId));
        }

        private Dictionary<int, int> BuildUsefulShelterDropDemands(GameWorld world)
        {
            var demands = new Dictionary<int, int>();
            int unplaced = Math.Max(0, _projectRequiredCount - CountPlaced(_projectRequiredCount));
            int projectDeficit = Math.Max(0, unplaced - ShelterInventoryCount(_projectMaterialItemId));
            if (projectDeficit > 0)
            {
                AddDropDemand(demands, _projectMaterialItemId, projectDeficit);
                AddRecipeInputDropDemand(world, _projectMaterialItemId, projectDeficit,
                    demands, new HashSet<int>(), 0);
            }

            AddActiveToolRecipeDropDemand(world, demands);
            return demands;
        }

        private void AddActiveToolRecipeDropDemand(GameWorld world, Dictionary<int, int> demands)
        {
            if (_toolUpgradeRetryTimer > 0f) return;
            int targetBlockId = 0;
            if (_hasGatherTarget)
                world.Chunks.TryGetLoadedBlock(_gatherX, _gatherY, _gatherZ, out targetBlockId);
            else if (_hasToolGatherTarget)
                world.Chunks.TryGetLoadedBlock(_toolGatherX, _toolGatherY, _toolGatherZ, out targetBlockId);
            if (targetBlockId <= 0) return;

            string family = BlockRegistry.ToolTypeOf(targetBlockId);
            if (string.IsNullOrWhiteSpace(family))
            {
                string material = BlockRegistry.MaterialOf(targetBlockId);
                if (string.Equals(material, "rock", StringComparison.OrdinalIgnoreCase)) family = "pickaxe";
                else if (string.Equals(BlockRegistry.GetName(targetBlockId), "log", StringComparison.OrdinalIgnoreCase)) family = "axe";
            }
            if (string.IsNullOrWhiteSpace(family) || GetBestHeldTool(family) > 0) return;

            var stationReadiness = new Dictionary<string, (bool Exists, bool Usable, int ItemId)>(
                StringComparer.OrdinalIgnoreCase);
            var gatherTargetAvailability = new Dictionary<int, bool>();
            foreach (var option in GetToolCraftOptions(family))
            {
                var ingredients = GetRecipeIngredients(option.OutputItemId, option.GridSize, option.Station);
                if (ingredients.Count == 0) continue;
                bool feasible = true;
                foreach (var ingredient in ingredients)
                {
                    int missing = Math.Max(0, ingredient.Value - AvailablePrivateCount(ingredient.Key));
                    if (missing > 0 && !CanEventuallySupplySteveItem(world, ingredient.Key,
                        missing, 0, new HashSet<int>(), gatherTargetAvailability))
                    {
                        feasible = false;
                        break;
                    }
                }
                if (!feasible) continue;

                bool stationExists = false;
                int stationItemId = 0;
                if (option.GridSize == 3)
                {
                    if (!stationReadiness.TryGetValue(option.Station, out var readiness))
                    {
                        stationExists = world.TryFindSteveCraftingStation(this, option.Station,
                            out _, out _, out _, out _, out _, out _);
                        stationItemId = GetSteveStationItemId(world, option.Station);
                        bool usable = stationExists || (stationItemId > 0
                            && (ShelterInventoryCount(stationItemId) > 0
                                || CanEventuallySupplySteveItem(world, stationItemId, 1, 0,
                                    new HashSet<int>(), gatherTargetAvailability)));
                        readiness = (stationExists, usable, stationItemId);
                        stationReadiness[option.Station] = readiness;
                    }
                    stationExists = readiness.Exists;
                    stationItemId = readiness.ItemId;
                    if (!readiness.Usable) continue;
                }

                foreach (var ingredient in ingredients)
                {
                    int missing = Math.Max(0, ingredient.Value - AvailablePrivateCount(ingredient.Key));
                    if (missing <= 0) continue;
                    AddDropDemand(demands, ingredient.Key, missing);
                    AddRecipeInputDropDemand(world, ingredient.Key, missing,
                        demands, new HashSet<int>(), 0);
                }
                if (option.GridSize == 3 && !stationExists && stationItemId > 0
                    && ShelterInventoryCount(stationItemId) <= 0)
                {
                    AddDropDemand(demands, stationItemId, 1);
                    AddRecipeInputDropDemand(world, stationItemId, 1,
                        demands, new HashSet<int>(), 0);
                }
                // The tool planner tries options in this same sorted order. Once one option has
                // all inputs obtainable from private stock, gatherable resources, or nearby
                // physical drops, later tiers are not additional pickup goals.
                return;
            }
        }

        private void AddRecipeInputDropDemand(GameWorld world, int outputItemId, int outputCount,
            Dictionary<int, int> demands, HashSet<int> visiting, int depth)
        {
            if (outputItemId <= 0 || outputCount <= 0 || depth >= 6 || !visiting.Add(outputItemId)) return;
            try
            {
                foreach (var recipe in RecipeRegistry.All)
                {
                    if (recipe.OutputItemId != outputItemId || recipe.Size > 2
                        || !RecipeRegistry.TryMatch(recipe.Input, 2, "hand", out var actual)
                        || actual.OutputItemId != outputItemId) continue;

                    int outputPerCraft = Math.Max(1, actual.OutputCount);
                    int craftCount = (outputCount + outputPerCraft - 1) / outputPerCraft;
                    foreach (var ingredient in CountRecipeIngredients(actual, 2))
                    {
                        int required = ingredient.Value * craftCount;
                        int missing = Math.Max(0, required - AvailablePrivateCount(ingredient.Key));
                        if (missing <= 0) continue;
                        AddDropDemand(demands, ingredient.Key, missing);
                        AddRecipeInputDropDemand(world, ingredient.Key, missing,
                            demands, visiting, depth + 1);
                    }
                    return;
                }
            }
            finally
            {
                visiting.Remove(outputItemId);
            }
        }

        private bool CanEventuallySupplySteveItem(GameWorld world, int itemId, int needed,
            int depth, HashSet<int> visiting, Dictionary<int, bool> gatherTargetAvailability)
        {
            if (itemId <= 0 || needed <= 0) return false;
            if (AvailablePrivateCount(itemId) >= needed
                || world.HasNearbyStevePickupDrop(this, itemId)) return true;
            if (IsSteveGatherableMaterial(itemId))
            {
                if (!gatherTargetAvailability.TryGetValue(itemId, out bool hasTarget))
                {
                    hasTarget = TryFindGatherTarget(world, itemId, out _);
                    gatherTargetAvailability[itemId] = hasTarget;
                }
                if (hasTarget) return true;
            }
            if (depth >= 6 || !visiting.Add(itemId)) return false;

            try
            {
                foreach (var recipe in RecipeRegistry.All)
                {
                    if (recipe.OutputItemId != itemId || recipe.Size > 2
                        || !RecipeRegistry.TryMatch(recipe.Input, 2, "hand", out var actual)
                        || actual.OutputItemId != itemId) continue;
                    int craftCount = (needed + Math.Max(1, actual.OutputCount) - 1)
                        / Math.Max(1, actual.OutputCount);
                    bool canCraft = true;
                    foreach (var ingredient in CountRecipeIngredients(actual, 2))
                    {
                        if (!CanEventuallySupplySteveItem(world, ingredient.Key,
                            ingredient.Value * craftCount, depth + 1, visiting,
                            gatherTargetAvailability))
                        {
                            canCraft = false;
                            break;
                        }
                    }
                    if (canCraft) return true;
                }
                return false;
            }
            finally
            {
                visiting.Remove(itemId);
            }
        }

        private static void AddDropDemand(Dictionary<int, int> demands, int itemId, int count)
        {
            if (itemId <= 0 || count <= 0) return;
            int current = demands.TryGetValue(itemId, out int existing) ? existing : 0;
            demands[itemId] = current > int.MaxValue - count ? int.MaxValue : current + count;
        }

        private static bool IsSteveGatherableMaterial(int itemId)
            => string.Equals(ItemRegistry.GetName(itemId), "planks", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ItemRegistry.GetName(itemId), "cobblestone", StringComparison.OrdinalIgnoreCase);

        private int AvailablePrivateCount(int itemId)
        {
            int reserved = itemId == _projectMaterialItemId
                ? Math.Max(0, _projectRequiredCount - CountPlaced(_projectRequiredCount)) : 0;
            return Math.Max(0, ShelterInventoryCount(itemId) - reserved);
        }

        private bool BeginToolInputGather(GameWorld world, int itemId)
        {
            if (itemId <= 0 || !IsSteveGatherableMaterial(itemId)) return false;
            if (!_hasToolGatherTarget || _toolGatherItemId != itemId
                || !ToolGatherTargetStillValid(world))
            {
                _hasToolGatherTarget = false;
                if (!TryFindGatherTarget(world, itemId, out var target)) return false;
                _toolGatherX = target.X; _toolGatherY = target.Y; _toolGatherZ = target.Z;
                _toolGatherStandX = target.StandX; _toolGatherStandY = target.StandY;
                _toolGatherStandZ = target.StandZ;
                _toolGatherOutputCount = target.OutputCount;
                _toolGatherItemId = itemId;
                _hasToolGatherTarget = true;
            }
            if (!CanAcceptShelterItem(itemId, _toolGatherOutputCount))
            {
                _hasToolGatherTarget = false;
                return false;
            }

            SetWorkMode(WorkMode.ToolUp);
            if (!world.Chunks.TryGetLoadedBlock(_toolGatherX, _toolGatherY, _toolGatherZ, out int blockId))
                return false;
            _taskExecutor.SetPlan(
                AgentTask.Navigate(_toolGatherStandX, _toolGatherStandY, _toolGatherStandZ,
                    allowEmergencyBreak: true),
                AgentTask.Mine(_toolGatherX, _toolGatherY, _toolGatherZ, itemId,
                    GetSteveMineDuration(blockId)));
            PrepareShelterTasks();
            return true;
        }

        private bool ToolGatherTargetStillValid(GameWorld world)
            => world.Chunks.TryGetLoadedBlock(_toolGatherX, _toolGatherY, _toolGatherZ, out int blockId)
                && TryGetSteveGatherYield(world, _toolGatherX, _toolGatherY, _toolGatherZ,
                    blockId, out int outputItem, out int outputCount)
                && outputItem == _toolGatherItemId && outputCount == _toolGatherOutputCount
                && IsWalkableStand(world, _toolGatherStandX, _toolGatherStandY, _toolGatherStandZ)
                && world.CanSteveReachBlockFromStand(this, _toolGatherX, _toolGatherY, _toolGatherZ,
                    _toolGatherStandX, _toolGatherStandY, _toolGatherStandZ);

        private int GetSteveStationItemId(GameWorld world, string station)
        {
            string itemName;
            if (string.Equals(station, "masonry", StringComparison.OrdinalIgnoreCase))
                itemName = "masons_workbench";
            else if (world.FrontierProgression)
                itemName = "carpenters_workbench";
            else
                itemName = "workbench";
            return ItemRegistry.TryGet(itemName, out var item) ? item.NumericId : 0;
        }

        private bool TryPlaceSteveCraftingStation(GameWorld world, int stationItemId)
        {
            int blockId = ItemRegistry.ResolveBlockId(stationItemId);
            if (blockId <= 0 || ShelterInventoryCount(stationItemId) <= 0) return false;
            int y = (int)Math.Floor(Position.Y);
            ReadOnlySpan<(int X, int Z)> offsets = stackalloc (int, int)[]
            {
                (1, 0), (-1, 0), (0, 1), (0, -1), (2, 0), (-2, 0), (0, 2), (0, -2)
            };
            for (int i = 0; i < offsets.Length; i++)
            {
                int x = (int)Math.Floor(Position.X) + offsets[i].X;
                int z = (int)Math.Floor(Position.Z) + offsets[i].Z;
                if (!world.Chunks.TryGetLoadedBlock(x, y - 1, z, out int floor)
                    || !IsSafeGroundBlock(floor)
                    || !world.Chunks.TryGetLoadedBlock(x, y, z, out int existing)
                    || existing != BlockRegistry.AirId) continue;
                if (!world.TryPlaceBlockForSteve(this, x, y, z, blockId, stationItemId)) continue;
                return ConsumeShelterItem(stationItemId, 1);
            }
            return false;
        }

        /// <summary>
        /// Private inventory craft transaction. The live eight slots are copied, the active
        /// registry must match the exact pattern/station, project materials remain reserved, and
        /// output capacity is proven before the staged array is committed.
        /// </summary>
        internal bool TryCraftShelterItem(int outputItemId, int gridSize, string station)
        {
            if (outputItemId <= 0 || gridSize < 2 || gridSize > 3 || _shelterInventory.Length != InventorySlotCount)
                return false;

            foreach (var recipe in RecipeRegistry.All)
            {
                if (recipe.OutputItemId != outputItemId || recipe.Size > gridSize) continue;
                if (!RecipeRegistry.TryMatch(recipe.Input, gridSize, station, out var matched)
                    || matched.OutputItemId != outputItemId) continue;

                var staged = (InventorySlot[])_shelterInventory.Clone();
                var required = CountRecipeIngredients(matched, gridSize);
                bool hasInputs = true;
                foreach (var ingredient in required)
                {
                    int reserved = ingredient.Key == _projectMaterialItemId
                        ? Math.Max(0, _projectRequiredCount - CountPlaced(_projectRequiredCount)) : 0;
                    if (CountSlots(staged, ingredient.Key) - reserved < ingredient.Value)
                    {
                        hasInputs = false;
                        break;
                    }
                }
                if (!hasInputs) continue;

                foreach (var ingredient in required)
                    if (!ConsumeSlots(staged, ingredient.Key, ingredient.Value))
                    {
                        hasInputs = false;
                        break;
                    }
                if (!hasInputs || !AddSlots(staged, outputItemId, matched.OutputCount)) continue;

                Array.Copy(staged, _shelterInventory, InventorySlotCount);
                return true;
            }
            return false;
        }

        private static int CountSlots(InventorySlot[] slots, int itemId)
        {
            int count = 0;
            foreach (var slot in slots)
                if (slot.ItemId == itemId && slot.Count > 0) count += slot.Count;
            return count;
        }

        private static bool ConsumeSlots(InventorySlot[] slots, int itemId, int count)
        {
            if (CountSlots(slots, itemId) < count) return false;
            int left = count;
            for (int i = 0; i < slots.Length && left > 0; i++)
            {
                var slot = slots[i];
                if (slot.ItemId != itemId || slot.Count <= 0) continue;
                int take = Math.Min(left, slot.Count);
                slot.Count -= take;
                if (slot.Count == 0) slot.ItemId = 0;
                slots[i] = slot;
                left -= take;
            }
            return left == 0;
        }

        private static bool AddSlots(InventorySlot[] slots, int itemId, int count)
        {
            if (itemId <= 0 || count <= 0) return false;
            int cap = Math.Min(GameWorld.MaxStackSize, ItemRegistry.StackSizeOf(itemId));
            int space = 0;
            foreach (var slot in slots)
            {
                if (slot.ItemId == itemId) space += Math.Max(0, cap - slot.Count);
                else if (slot.ItemId == 0 || slot.Count <= 0) space += cap;
            }
            if (space < count) return false;

            int left = count;
            for (int i = 0; i < slots.Length && left > 0; i++)
            {
                var slot = slots[i];
                if (slot.ItemId != itemId || slot.Count >= cap) continue;
                int add = Math.Min(left, cap - slot.Count);
                slot.Count += add;
                slots[i] = slot;
                left -= add;
            }
            for (int i = 0; i < slots.Length && left > 0; i++)
            {
                if (slots[i].ItemId != 0 && slots[i].Count > 0) continue;
                int add = Math.Min(left, cap);
                slots[i] = new InventorySlot { ItemId = itemId, Count = add };
                left -= add;
            }
            return left == 0;
        }

        private int GetBestHeldTool(string family)
        {
            int bestItemId = 0;
            int bestLevel = int.MinValue;
            float bestSpeed = float.MinValue;
            foreach (var slot in _shelterInventory)
            {
                int itemId = slot.ItemId;
                if (slot.Count <= 0 || itemId <= 0
                    || !string.Equals(ItemRegistry.ToolTypeOf(itemId), family, StringComparison.OrdinalIgnoreCase))
                    continue;
                int level = ItemRegistry.ToolLevelOf(itemId);
                float speed = ItemRegistry.MiningSpeedOf(itemId);
                if (level < bestLevel || (level == bestLevel && speed <= bestSpeed)) continue;
                bestItemId = itemId;
                bestLevel = level;
                bestSpeed = speed;
            }
            return bestItemId;
        }

        internal float GetSteveMineDuration(int blockId)
        {
            float hardness = Math.Max(0.1f, BlockRegistry.HardnessOf(blockId));
            float baseDuration = Math.Clamp(0.8f + hardness * 0.32f, 0.9f, 3.0f);
            string family = BlockRegistry.ToolTypeOf(blockId);
            if (string.IsNullOrWhiteSpace(family))
            {
                if (string.Equals(BlockRegistry.MaterialOf(blockId), "rock", StringComparison.OrdinalIgnoreCase))
                    family = "pickaxe";
                else if (string.Equals(BlockRegistry.GetName(blockId), "log", StringComparison.OrdinalIgnoreCase))
                    family = "axe";
            }
            int toolId = string.IsNullOrWhiteSpace(family) ? 0 : GetBestHeldTool(family);
            float speed = toolId > 0 ? ItemRegistry.MiningSpeedOf(toolId) : 1f;
            if (!(speed > 0f) || float.IsNaN(speed) || float.IsInfinity(speed)) speed = 1f;
            return Math.Clamp(baseDuration / speed, 0.15f, 3.0f);
        }

        private static string SteveCraftStationName(int stationCode) => stationCode switch
        {
            MasonryStationCode => "masonry",
            CarpenterStationCode => "carpenter",
            _ => "hand",
        };
    }
}
