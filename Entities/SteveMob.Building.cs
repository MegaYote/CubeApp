using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Cubuild
{
    public sealed partial class SteveMob
    {
        public const int InventorySlotCount = 8;

        private readonly InventorySlot[] _shelterInventory = new InventorySlot[InventorySlotCount];
        private int _blueprintIndex = -1;
        private int _anchorX, _anchorY, _anchorZ;
        private int _rotation;
        private int _lastShelterTemplateIndex = -1;
        private long _placedMask;
        private float _planRetryTimer;
        private readonly AgentTaskExecutor _taskExecutor = new();
        private int _activeBuildCell = -1;
        private bool _hasGatherTarget;
        private int _gatherX, _gatherY, _gatherZ;
        private int _gatherStandX, _gatherStandY, _gatherStandZ;
        private float _gatherTargetValidationTimer;
        private int _plannedBuildCell = -1;
        private DroppedItem? _pickupDropTarget;
        private float _pickupDropScanTimer;
        private float _pickupDropTargetValidationTimer;
        private DroppedItem? _ignoredPickupDrop;
        private float _ignoredPickupDropTimer;
        private int _buildStandX, _buildStandY, _buildStandZ;
        private float _ignoredSiteTimer, _ignoredGatherTimer;
        private float _toolUpgradeRetryTimer;
        private bool _hasIgnoredSite, _hasIgnoredGather;
        private int _ignoredSiteX, _ignoredSiteY, _ignoredSiteZ;
        private int _ignoredGatherX, _ignoredGatherY, _ignoredGatherZ;
        private WorkMode _workMode;
        private readonly Func<AgentTask, bool> _taskExecutorAction;
        private GameWorld? _taskActionWorld;
        private ShelterTemplate? _taskActionTemplate;
        private int _taskActionItemId, _taskActionBlockId;
        private const int ProceduralBlueprintIndex = 27;
        private const int ProceduralPlanGeneratorVersion = 1;
        private ShelterTemplate? _proceduralPlan;
        private ShelterTemplate? _proceduralShell;
        private int _proceduralPlanGeneratorVersion;

        private enum WorkMode { None, Gather, Build, Waiting, ToolUp }

        private readonly struct BlueprintCell
        {
            public readonly int X, Y, Z;
            public BlueprintCell(int x, int y, int z) { X = x; Y = y; Z = z; }
        }

        private sealed class ShelterTemplate
        {
            public readonly string Name;
            public readonly string ItemName;
            public readonly int Size;
            public readonly BlueprintCell[] Cells;

            public ShelterTemplate(string name, string itemName, int size, BlueprintCell[] cells)
            {
                Name = name;
                ItemName = itemName;
                Size = size;
                Cells = cells;
            }
        }

        private readonly struct GatherTarget
        {
            public readonly int X, Y, Z;
            public readonly int ItemId;
            public readonly int StandX, StandY, StandZ;
            public readonly int OutputCount;
            public readonly double Score;
            public GatherTarget(int x, int y, int z, int standX, int standY, int standZ,
                int outputCount, double score, int itemId = 0)
            {
                X = x; Y = y; Z = z;
                ItemId = itemId;
                StandX = standX; StandY = standY; StandZ = standZ;
                OutputCount = outputCount;
                Score = score;
            }
        }

        // Small, private, immutable local blueprint library. The first three entries are the
        // original save-stable layouts; generated entries are appended in a fixed order. Cell
        // coordinates use a square footprint and are rotated around it before being anchored.
        private static readonly ShelterTemplate[] ShelterTemplates = BuildShelterTemplates();

        private static ShelterTemplate[] BuildShelterTemplates()
        {
            var leanTo = new List<BlueprintCell>(14);
            for (int x = 0; x < 3; x++)
            {
                leanTo.Add(new BlueprintCell(x, 0, 0));
                leanTo.Add(new BlueprintCell(x, 1, 0));
                leanTo.Add(new BlueprintCell(x, 2, 0));
                leanTo.Add(new BlueprintCell(x, 2, 1));
            }
            leanTo.Add(new BlueprintCell(0, 0, 1));
            leanTo.Add(new BlueprintCell(2, 0, 1));

            var cabin = new List<BlueprintCell>(24);
            for (int z = 0; z < 3; z++)
            {
                for (int x = 0; x < 3; x++)
                {
                    if (x != 0 && x != 2 && z != 0 && z != 2) continue;
                    if (x == 1 && z == 2) continue; // leave a simple doorway
                    cabin.Add(new BlueprintCell(x, 0, z));
                    cabin.Add(new BlueprintCell(x, 1, z));
                }
            }
            for (int z = 0; z < 3; z++)
                for (int x = 0; x < 3; x++)
                    cabin.Add(new BlueprintCell(x, 2, z));

            var stoneNook = new List<BlueprintCell>(11);
            for (int z = 0; z < 2; z++)
            {
                for (int x = 0; x < 2; x++)
                {
                    bool perimeter = x == 0 || x == 1 || z == 0 || z == 1;
                    if (!perimeter) continue;
                    if (x == 0 && z == 1) continue; // doorway
                    stoneNook.Add(new BlueprintCell(x, 0, z));
                    stoneNook.Add(new BlueprintCell(x, 1, z));
                }
            }
            for (int z = 0; z < 2; z++)
                for (int x = 0; x < 2; x++)
                    stoneNook.Add(new BlueprintCell(x, 2, z));

            var templates = new List<ShelterTemplate>
            {
                new ShelterTemplate("lean-to", "planks", 3, leanTo.ToArray()),
                new ShelterTemplate("cabin", "planks", 3, cabin.ToArray()),
                new ShelterTemplate("stone nook", "cobblestone", 2, stoneNook.ToArray()),
            };

            // Keep generated variants deterministic: their array indices are persisted in saves.
            // Never insert or reorder these ahead of the original indices 0..2.
            for (int variant = 0; variant < 24; variant++)
                templates.Add(BuildGeneratedShelterTemplate(variant));
            return templates.ToArray();
        }

        private static ShelterTemplate BuildGeneratedShelterTemplate(int variant)
        {
            uint randomState = unchecked(0x9E3779B9u + (uint)variant * 0x85EBCA6Bu);
            int size = 3 + NextTemplateChoice(ref randomState, 2);
            int wallHeight = NextTemplateChoice(ref randomState, 4) == 0 ? 1 : 2;
            int doorSide = NextTemplateChoice(ref randomState, 4);
            int doorOffset = size == 3 ? 1 : 1 + NextTemplateChoice(ref randomState, 2);
            int materialChoice = NextTemplateChoice(ref randomState, 5);
            string itemName = materialChoice == 0 ? "cobblestone" : "planks";
            int roofProfile = NextTemplateChoice(ref randomState, 3);
            int roofAxis = NextTemplateChoice(ref randomState, 2);
            int roofOpenSide = (doorSide + 1 + NextTemplateChoice(ref randomState, 3)) % 4;

            int doorX, doorZ;
            switch (doorSide)
            {
                case 0: doorX = doorOffset; doorZ = 0; break;
                case 1: doorX = size - 1; doorZ = doorOffset; break;
                case 2: doorX = doorOffset; doorZ = size - 1; break;
                default: doorX = 0; doorZ = doorOffset; break;
            }

            var wallPositions = new List<(int X, int Z)>();
            for (int z = 0; z < size; z++)
            {
                for (int x = 0; x < size; x++)
                {
                    if (x != 0 && x != size - 1 && z != 0 && z != size - 1) continue;
                    wallPositions.Add((x, z));
                }
            }

            var windowPositions = new List<(int X, int Z)>();
            if (wallHeight > 1)
            {
                foreach (var position in wallPositions)
                {
                    bool corner = (position.X == 0 || position.X == size - 1)
                        && (position.Z == 0 || position.Z == size - 1);
                    if (!corner && (position.X != doorX || position.Z != doorZ))
                        windowPositions.Add(position);
                }
            }

            int windowCount = wallHeight > 1 ? NextTemplateChoice(ref randomState, 3) : 0;
            var windows = new List<(int X, int Z)>(windowCount);
            while (windows.Count < windowCount && windowPositions.Count > 0)
            {
                int pick = NextTemplateChoice(ref randomState, windowPositions.Count);
                windows.Add(windowPositions[pick]);
                windowPositions.RemoveAt(pick);
            }

            var cells = new List<BlueprintCell>(40);
            foreach (var position in wallPositions)
            {
                bool doorway = position.X == doorX && position.Z == doorZ;
                bool window = windows.Contains(position);
                for (int y = 0; y < wallHeight; y++)
                {
                    if (doorway && y < Math.Min(2, wallHeight)) continue;
                    if (window && y == 1) continue;
                    cells.Add(new BlueprintCell(position.X, y, position.Z));
                }
            }

            // All roofs stay within the planner's existing three-block vertical safety envelope.
            // Low-wall variants can have a simple pitched profile; taller walls use a flat cap or
            // a small roof vent. The entrance is kept two blocks clear even under a low eave.
            for (int z = 0; z < size; z++)
            {
                for (int x = 0; x < size; x++)
                {
                    if (roofProfile == 2 && x == size / 2 && z == size / 2) continue;
                    bool openEave = roofOpenSide switch
                    {
                        0 => z == 0,
                        1 => x == size - 1,
                        2 => z == size - 1,
                        _ => x == 0,
                    };
                    if (wallHeight > 1 && roofProfile == 1 && openEave) continue;
                    int roofY = 2;
                    bool atDoor = x == doorX && z == doorZ;
                    if (wallHeight == 1 && roofProfile == 1 && !atDoor)
                    {
                        bool eave = roofAxis == 0
                            ? x == 0 || x == size - 1
                            : z == 0 || z == size - 1;
                        if (eave) roofY = 1;
                    }
                    cells.Add(new BlueprintCell(x, roofY, z));
                }
            }

            return new ShelterTemplate($"shelter-{variant:D2}", itemName, size, cells.ToArray());
        }

        private static int NextTemplateChoice(ref uint state, int exclusiveMax)
        {
            // Xorshift32 has fixed cross-runtime behavior, unlike relying on Random's implementation.
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return (int)(state % (uint)exclusiveMax);
        }

        private static ShelterTemplate BuildProceduralShelterPlan(int seed)
        {
            uint state = unchecked((uint)seed ^ 0xD1B54A35u);
            if (state == 0) state = 0x6D2B79F5u;
            int width = 3 + NextTemplateChoice(ref state, 2);
            int depth = 3 + NextTemplateChoice(ref state, 2);
            int porchDepth = NextTemplateChoice(ref state, 3) == 0 ? 0 : 1 + NextTemplateChoice(ref state, 2);
            if (depth + porchDepth > 5) porchDepth = 5 - depth;
            int size = Math.Max(width, depth + porchDepth);
            int wallHeight = width == 4 && depth == 4 ? 1 : 1 + NextTemplateChoice(ref state, 2);
            int roofProfile = NextTemplateChoice(ref state, 3); // flat, raised ridge, or flat with vent
            int roofAxis = NextTemplateChoice(ref state, 2);
            int materialChoice = NextTemplateChoice(ref state, 5);
            string itemName = materialChoice == 0 ? "cobblestone" : "planks";

            var footprint = new HashSet<(int X, int Z)>();
            for (int z = 0; z < depth; z++)
                for (int x = 0; x < width; x++) footprint.Add((x, z));
            int porchWidth = Math.Max(1, width - (NextTemplateChoice(ref state, 2) == 0 ? 0 : 1));
            int porchStartX = (width - porchWidth) / 2;
            for (int z = 0; z < porchDepth; z++)
            {
                for (int x = porchStartX; x < porchStartX + porchWidth; x++) footprint.Add((x, depth + z));
            }

            var perimeter = new List<(int X, int Z)>();
            for (int z = 0; z < size; z++)
            for (int x = 0; x < size; x++)
            {
                if (!footprint.Contains((x, z))) continue;
                if (!footprint.Contains((x - 1, z)) || !footprint.Contains((x + 1, z))
                    || !footprint.Contains((x, z - 1)) || !footprint.Contains((x, z + 1)))
                    perimeter.Add((x, z));
            }

            // The porch extends along +Z, so its entrance belongs in the outside wall.
            // Without a porch, vary the exterior entrance across the room's four sides.
            int doorX, doorZ;
            if (porchDepth > 0)
            {
                doorX = porchStartX + porchWidth / 2;
                doorZ = depth + porchDepth - 1;
            }
            else
            {
                int doorSide = NextTemplateChoice(ref state, 4);
                doorX = doorSide switch { 0 or 2 => width / 2, 1 => width - 1, _ => 0 };
                doorZ = doorSide switch { 0 => 0, 1 or 3 => depth / 2, _ => depth - 1 };
            }
            var windows = new HashSet<(int X, int Z)>();
            var windowCandidates = new List<(int X, int Z)>();
            if (wallHeight > 1)
            {
                foreach (var point in perimeter)
                {
                    bool corner = (point.X == 0 || point.X == width - 1)
                        && (point.Z == 0 || point.Z == depth - 1);
                    if (!corner && !(point.X == doorX && point.Z == doorZ)) windowCandidates.Add(point);
                }
                int windowCount = NextTemplateChoice(ref state, 3);
                while (windows.Count < windowCount && windowCandidates.Count > 0)
                {
                    int pick = NextTemplateChoice(ref state, windowCandidates.Count);
                    windows.Add(windowCandidates[pick]);
                    windowCandidates.RemoveAt(pick);
                }
            }

            var cells = new List<BlueprintCell>(48);
            foreach (var point in perimeter)
            {
                bool doorway = point.X == doorX && point.Z == doorZ;
                bool window = windows.Contains(point);
                for (int y = 0; y < wallHeight; y++)
                {
                    if (doorway && y < 2) continue;
                    if (window && y == 1) continue;
                    cells.Add(new BlueprintCell(point.X, y, point.Z));
                }
            }
            for (int z = 0; z < size; z++)
            for (int x = 0; x < size; x++)
            {
                if (!footprint.Contains((x, z))) continue;
                if (roofProfile == 2 && x == width / 2 && z == depth / 2) continue;
                int roofY = wallHeight == 1 && x == doorX && z == doorZ ? 2
                    : wallHeight == 1 ? 1 : 2;
                cells.Add(new BlueprintCell(x, roofY, z));
                if (roofProfile == 1
                    && (roofAxis == 0 ? x == width / 2 : z == depth / 2))
                    cells.Add(new BlueprintCell(x, roofY + 1, z));
            }
            // Keep the plan within the current 63-bit progress mask even for future generator
            // tweaks. A compact, single-room design is the deterministic safe fallback.
            if (cells.Count > 63)
            {
                cells.Clear();
                for (int z = 0; z < 3; z++)
                    for (int x = 0; x < 3; x++)
                    {
                        if ((x == 0 || x == 2 || z == 0 || z == 2) && !(x == 1 && z == 2))
                            cells.Add(new BlueprintCell(x, 0, z));
                        cells.Add(new BlueprintCell(x, x == 1 && z == 2 ? 2 : 1, z));
                    }
                size = 3;
            }
            return new ShelterTemplate("procedural", itemName, size, cells.ToArray());
        }

        public int ShelterBlueprintIndex => _blueprintIndex;
        public int ShelterAnchorX => _anchorX;
        public int ShelterAnchorY => _anchorY;
        public int ShelterAnchorZ => _anchorZ;
        public int ShelterRotation => _rotation;
        public long ShelterPlacedMask => _placedMask;
        public int ShelterPlanSize => _proceduralPlan?.Size ?? 0;
        public int ShelterPlanMaterialItemId => _proceduralPlan != null
            && TryResolveTemplate(ProceduralBlueprintIndex, out _, out int itemId, out _) ? itemId : 0;
        public int ShelterPlanGeneratorVersion => _proceduralPlanGeneratorVersion;

        public int[] CaptureShelterPlanCells()
        {
            if (_proceduralPlan == null) return Array.Empty<int>();
            var result = new int[_proceduralPlan.Cells.Length * 3];
            for (int i = 0; i < _proceduralPlan.Cells.Length; i++)
            {
                result[i * 3] = _proceduralPlan.Cells[i].X;
                result[i * 3 + 1] = _proceduralPlan.Cells[i].Y;
                result[i * 3 + 2] = _proceduralPlan.Cells[i].Z;
            }
            return result;
        }

        public InventorySlot[] CaptureShelterInventory() => (InventorySlot[])_shelterInventory.Clone();

        public void RestoreShelterState(InventorySlot[]? inventory, int blueprintIndex,
            int anchorX, int anchorY, int anchorZ, int rotation, long placedMask,
            int planSize = 0, int planMaterialItemId = 0, int planGeneratorVersion = 0,
            int[]? planCells = null)
        {
            InvalidateUsefulShelterDropDemandCache();
            InvalidateGatherTargetSearchCaches();
            InvalidateSteveCraftingStationSearchCaches();
            _lastShelterTemplateIndex = -1;
            _hasShelterSite = false;
            _siteBlocked = false;
            _projectSeed = 0;
            _proceduralPlan = null;
            _proceduralShell = null;
            _proceduralPlanGeneratorVersion = 0;
            _projectMaterialItemId = 0;
            _projectRequiredCount = 0;
            _plannerGoal = ShelterGoal.None;
            _plannerChoiceCount = 0;
            _plannerFailureCount = 0;
            _plannerFailureCooldown = 0f;
            _plannerDecisionTimer = 0f;
            _lastFailedGoal = ShelterGoal.None;
            _hasExplorationTarget = false;
            _explorationHold = false;
            _explorationRing = _explorationSector = 0;
            _explorationTaskInstalled = false;
            Array.Clear(_shelterInventory, 0, _shelterInventory.Length);
            if (inventory != null)
            {
                for (int i = 0; i < Math.Min(inventory.Length, _shelterInventory.Length); i++)
                {
                    var slot = inventory[i];
                    if (slot.ItemId > 0 && slot.ItemId < ItemRegistry.Count && slot.Count > 0)
                    {
                        _shelterInventory[i] = new InventorySlot
                        {
                            ItemId = slot.ItemId,
                            Count = Math.Min(GameWorld.MaxStackSize, slot.Count),
                        };
                    }
                }
            }

            if (blueprintIndex == ProceduralBlueprintIndex
                && planGeneratorVersion > 0
                && planSize >= 3 && planSize <= 5
                && planCells != null && planCells.Length >= 3 && planCells.Length <= 63 * 3
                && planCells.Length % 3 == 0
                && planMaterialItemId > 0 && planMaterialItemId < ItemRegistry.Count)
            {
                var cells = new BlueprintCell[planCells.Length / 3];
                var uniqueCells = new HashSet<(int X, int Y, int Z)>();
                bool valid = true;
                for (int i = 0; i < cells.Length; i++)
                {
                    int x = planCells[i * 3], y = planCells[i * 3 + 1], z = planCells[i * 3 + 2];
                    if (x < 0 || x >= planSize || z < 0 || z >= planSize || y < -2 || y > 3
                        || !uniqueCells.Add((x, y, z)))
                    { valid = false; break; }
                    cells[i] = new BlueprintCell(x, y, z);
                }
                string itemName = ItemRegistry.GetName(planMaterialItemId);
                if (valid && IsSafeSteveBuildMaterial(planMaterialItemId))
                {
                    _proceduralPlan = new ShelterTemplate("procedural", itemName, planSize, cells);
                    _proceduralPlanGeneratorVersion = planGeneratorVersion;
                    var shellCells = new List<BlueprintCell>(cells.Length);
                    foreach (var cell in cells) if (cell.Y >= 0) shellCells.Add(cell);
                    if (shellCells.Count > 0)
                        _proceduralShell = new ShelterTemplate("procedural", itemName, planSize, shellCells.ToArray());
                }
            }
            if (blueprintIndex < 0 || (blueprintIndex >= ShelterTemplates.Length
                && !(blueprintIndex == ProceduralBlueprintIndex && _proceduralPlan != null)))
            {
                ClearShelterPlan();
                return;
            }

            _blueprintIndex = blueprintIndex;
            _lastShelterTemplateIndex = blueprintIndex;
            _anchorX = anchorX; _anchorY = anchorY; _anchorZ = anchorZ;
            _rotation = ((rotation % 4) + 4) % 4;
            if (!TryResolveTemplate(blueprintIndex, out var template, out _, out _))
            {
                ClearShelterPlan();
                return;
            }
            _placedMask = placedMask & ValidPlacedMask(template.Cells.Length);
            // v16 and earlier only serialized plans that already had a site. v17 may persist a
            // selected project before a site has been found.
            _hasShelterSite = true;
            if (TryResolveTemplate(blueprintIndex, out template, out int itemId, out _))
            {
                _projectMaterialItemId = itemId;
                _projectRequiredCount = template.Cells.Length;
            }
            _workMode = WorkMode.None;
            _planRetryTimer = 0f;
            _plannerScanTimer = 0f;
            _hasGatherTarget = false;
            _gatherTargetValidationTimer = 0f;
            _hasToolGatherTarget = false;
            _toolUpgradeRetryTimer = 0f;
            _pickupDropTarget = null;
            _pickupDropScanTimer = 0f;
            _pickupDropTargetValidationTimer = 0f;
            _ignoredPickupDrop = null;
            _ignoredPickupDropTimer = 0f;
            _gatherOutputCount = 0;
            _activeBuildCell = -1;
            _plannedBuildCell = -1;
            _taskExecutor.SetPlan();
            ClearPathGoal();
        }

        public int ShelterInventoryCount(int itemId)
        {
            int total = 0;
            foreach (var slot in _shelterInventory)
                if (slot.ItemId == itemId) total += slot.Count;
            return total;
        }

        private int ShelterItemCapacity(int itemId)
        {
            if (itemId <= 0 || itemId >= ItemRegistry.Count) return 0;
            int stackLimit = Math.Min(GameWorld.MaxStackSize, ItemRegistry.StackSizeOf(itemId));
            if (stackLimit <= 0) return 0;
            int capacity = 0;
            foreach (var slot in _shelterInventory)
            {
                if (slot.ItemId == itemId) capacity += Math.Max(0, stackLimit - slot.Count);
                else if (slot.ItemId == 0 || slot.Count <= 0) capacity += stackLimit;
            }
            return capacity;
        }

        private bool CanAddShelterItem(int itemId, int count)
            => itemId > 0 && count > 0 && ShelterItemCapacity(itemId) >= count;

        internal int GetShelterItemCapacity(int itemId) => ShelterItemCapacity(itemId);

        public bool CanAcceptShelterItem(int itemId, int count) => CanAddShelterItem(itemId, count);

        public bool AddShelterItem(int itemId, int count) => TryAddShelterItem(itemId, count);

        public bool ConsumeShelterItem(int itemId, int count) => TryConsumeShelterItem(itemId, count);

        private bool TryAddShelterItem(int itemId, int count)
        {
            if (!CanAddShelterItem(itemId, count)) return false;
            int remaining = count;
            int stackLimit = Math.Min(GameWorld.MaxStackSize, ItemRegistry.StackSizeOf(itemId));
            for (int i = 0; i < _shelterInventory.Length && remaining > 0; i++)
            {
                var slot = _shelterInventory[i];
                if (slot.ItemId != itemId || slot.Count >= stackLimit) continue;
                int add = Math.Min(remaining, stackLimit - slot.Count);
                slot.Count += add;
                _shelterInventory[i] = slot;
                remaining -= add;
            }
            for (int i = 0; i < _shelterInventory.Length && remaining > 0; i++)
            {
                if (_shelterInventory[i].ItemId != 0 && _shelterInventory[i].Count > 0) continue;
                int add = Math.Min(remaining, stackLimit);
                _shelterInventory[i] = new InventorySlot { ItemId = itemId, Count = add };
                remaining -= add;
            }
            bool added = remaining == 0;
            if (added) InvalidateUsefulShelterDropDemandCache();
            return added;
        }

        private bool TryConsumeShelterItem(int itemId, int count)
        {
            if (ShelterInventoryCount(itemId) < count) return false;
            int remaining = count;
            for (int i = 0; i < _shelterInventory.Length && remaining > 0; i++)
            {
                var slot = _shelterInventory[i];
                if (slot.ItemId != itemId || slot.Count <= 0) continue;
                int take = Math.Min(remaining, slot.Count);
                slot.Count -= take;
                if (slot.Count == 0) slot.ItemId = 0;
                _shelterInventory[i] = slot;
                remaining -= take;
            }
            bool consumed = remaining == 0;
            if (consumed) InvalidateUsefulShelterDropDemandCache();
            return consumed;
        }

        public void PrepareShelterWork(GameWorld world, float dt)
        {
            long started = Stopwatch.GetTimestamp();
            ResetShelterPrepareDiagnostics();
            try
            {
                PrepareShelterWorkCore(world, dt);
            }
            finally
            {
                _lastShelterPrepareTicks = Stopwatch.GetTimestamp() - started;
            }
        }

        private void PrepareShelterWorkCore(GameWorld world, float dt)
        {
            if (!world.IsAuthoritative || IsDead || Removed) return;
            TickStevePlanningCaches(dt);
            _planRetryTimer = Math.Max(0f, _planRetryTimer - dt);
            _plannerDecisionTimer = Math.Max(0f, _plannerDecisionTimer - dt);
            _plannerScanTimer = Math.Max(0f, _plannerScanTimer - dt);
            _plannerFailureCooldown = Math.Max(0f, _plannerFailureCooldown - dt);
            float oldToolRetryTimer = _toolUpgradeRetryTimer;
            _toolUpgradeRetryTimer = Math.Max(0f, _toolUpgradeRetryTimer - dt);
            if (oldToolRetryTimer > 0f && _toolUpgradeRetryTimer <= 0f)
                InvalidateUsefulShelterDropDemandCache();
            _ignoredSiteTimer = Math.Max(0f, _ignoredSiteTimer - dt);
            float oldIgnoredGatherTimer = _ignoredGatherTimer;
            _ignoredGatherTimer = Math.Max(0f, _ignoredGatherTimer - dt);
            if (_ignoredSiteTimer <= 0f) _hasIgnoredSite = false;
            if (_ignoredGatherTimer <= 0f)
            {
                _hasIgnoredGather = false;
                if (oldIgnoredGatherTimer > 0f) InvalidateGatherTargetSearchCaches();
            }
            _gatherTargetValidationTimer = Math.Max(0f, _gatherTargetValidationTimer - dt);
            _pickupDropScanTimer = Math.Max(0f, _pickupDropScanTimer - dt);
            _pickupDropTargetValidationTimer = Math.Max(0f, _pickupDropTargetValidationTimer - dt);
            _ignoredPickupDropTimer = Math.Max(0f, _ignoredPickupDropTimer - dt);
            if (_ignoredPickupDropTimer <= 0f) _ignoredPickupDrop = null;
            UpdateSteveThreat(world, dt);
            if (HasActiveThreat)
            {
                if (_cooperativeLeader != null) LeaveCooperativeWork(1.25f);
                SetWorkMode(WorkMode.Waiting);
                _plannerDecisionTimer = 0f;
                PrepareSteveThreatResponse(world);
                return;
            }
            // The coordinator refreshes this agent's temporary Navigate+Place plan after all
            // personal planners have prepared. Preserve it through the movement/action phase;
            // replanning solo here would reset the executor before its Place step can run.
            if (_cooperativeLeader != null) return;
            if (_plannerGoal == ShelterGoal.ExploreForBuildSite && _explorationTaskInstalled
                && PathStatus == NavigationStatus.Arrived) _plannerScanTimer = 0f;

            if (_blueprintIndex < 0 && _planRetryTimer <= 0f) EnsureShelterProject(world);
            if (_blueprintIndex < 0)
            {
                PrepareExploration(world);
                return;
            }

            if (!TryResolveTemplate(_blueprintIndex, out var template, out int itemId, out int blockId))
            {
                ClearShelterPlan();
                PrepareExploration(world);
                return;
            }

            bool plannerScanDue = _plannerScanTimer <= 0f;
            if (_hasShelterSite && !_siteBlocked && plannerScanDue && !ValidateShelterPlan(world))
            {
                ReplanShelter(1.5f);
            }
            else if (_hasShelterSite && _siteBlocked && plannerScanDue
                && ValidateShelterPlan(world)) _siteBlocked = false;

            if (!_hasShelterSite && !_siteBlocked && plannerScanDue)
            {
                if (!TryChooseShelter(world)) _plannerScanTimer = 2.5f;
            }

            if (_hasGatherTarget && _gatherTargetValidationTimer <= 0f)
            {
                _gatherTargetValidationTimer = 0.35f;
                if (!GatherTargetStillValid(world, itemId))
                {
                    _hasGatherTarget = false;
                    _gatherOutputCount = 0;
                    _plannerDecisionTimer = 0f;
                    _plannerScanTimer = 0f;
                    plannerScanDue = true;
                    InvalidateGatherTargetSearchCaches();
                    InvalidateUsefulShelterDropDemandCache();
                }
            }
            if (ShelterInventoryCount(itemId) >= _projectRequiredCount && _hasGatherTarget)
            {
                _hasGatherTarget = false;
                _gatherOutputCount = 0;
                _plannerDecisionTimer = 0f;
                InvalidateUsefulShelterDropDemandCache();
            }
            if (ShelterInventoryCount(itemId) < _projectRequiredCount && !_hasGatherTarget
                && plannerScanDue && TryFindGatherTargetCached(world, itemId, out var availableTarget))
            {
                _gatherX = availableTarget.X; _gatherY = availableTarget.Y; _gatherZ = availableTarget.Z;
                _gatherStandX = availableTarget.StandX; _gatherStandY = availableTarget.StandY; _gatherStandZ = availableTarget.StandZ;
                _gatherOutputCount = availableTarget.OutputCount;
                _hasGatherTarget = true;
                _gatherTargetValidationTimer = 0.35f;
                InvalidateUsefulShelterDropDemandCache();
            }

            // Physical drops are a supporting resource subtask, not a replacement construction
            // goal. Keep the selected blueprint and planner goal sticky while a useful nearby
            // drop is collected into Steve's private inventory.
            bool constructionPlanFinalized = _blueprintIndex != ProceduralBlueprintIndex || _hasShelterSite;
            if (constructionPlanFinalized && TryPrepareUsefulDropPickup(world))
            {
                if (_plannerScanTimer <= 0f) _plannerScanTimer = 2.5f;
                return;
            }

            if (_hasShelterSite && !_siteBlocked)
            {
                int nextCell = FindNextUnplacedCell(template);
                if (nextCell < 0)
                {
                    CompleteShelterProject();
                    _planRetryTimer = 3f;
                    return;
                }

                bool hasMaterial = ShelterInventoryCount(itemId) > 0;
                double buildDistance = double.PositiveInfinity;
                bool canBuild = hasMaterial;
                var buildCell = template.Cells[nextCell];
                RotateCell(buildCell, template.Size, _rotation, out int buildRx, out int buildRz);
                int buildX = _anchorX + buildRx, buildY = _anchorY + buildCell.Y, buildZ = _anchorZ + buildRz;
                if (canBuild)
                {
                    bool approachStillValid = _plannedBuildCell == nextCell
                        && IsWalkableStand(world, _buildStandX, _buildStandY, _buildStandZ)
                        && world.CanStevePlaceCellFromStand(this, buildX, buildY, buildZ,
                            _buildStandX, _buildStandY, _buildStandZ);
                    if (!approachStillValid)
                    {
                        if (TryFindBuildApproach(world, template, nextCell,
                            out _buildStandX, out _buildStandY, out _buildStandZ))
                            _plannedBuildCell = nextCell;
                        else canBuild = false;
                    }
                    if (canBuild)
                    {
                        double dx = _buildStandX + 0.5 - Position.X;
                        double dy = _buildStandY - Position.Y;
                        double dz = _buildStandZ + 0.5 - Position.Z;
                        buildDistance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    }
                }

                double gatherDistance = double.PositiveInfinity;
                if (_hasGatherTarget)
                {
                    double dx = _gatherStandX + 0.5 - Position.X;
                    double dy = _gatherStandY - Position.Y;
                    double dz = _gatherStandZ + 0.5 - Position.Z;
                    gatherDistance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                }

                bool canGather = constructionPlanFinalized && _hasGatherTarget
                    && ShelterInventoryCount(itemId) < _projectRequiredCount;
                bool needDecision = _plannerGoal == ShelterGoal.None || _plannerDecisionTimer <= 0f
                    || (_plannerGoal == ShelterGoal.ExploreForBuildSite && (canBuild || canGather))
                    || (_plannerGoal == ShelterGoal.ContinueConstructionProject && !canBuild)
                    || (_plannerGoal == ShelterGoal.GatherProjectMaterials && !canGather);
                if (needDecision)
                {
                    var selectedGoal = SelectShelterGoal(canBuild, buildDistance, canGather,
                        gatherDistance, canGather ? _gatherOutputCount : 0,
                        canExplore: true);
                    if (selectedGoal != _plannerGoal)
                    {
                        _plannerGoal = selectedGoal;
                        if (selectedGoal != ShelterGoal.ExploreForBuildSite) _explorationTaskInstalled = false;
                    }
                    _plannerDecisionTimer = 1.25f;
                }

                if (_plannerGoal == ShelterGoal.ContinueConstructionProject && canBuild)
                {
                    SetWorkMode(WorkMode.Build);
                    _activeBuildCell = nextCell;
                    var cell = template.Cells[nextCell];
                    RotateCell(cell, template.Size, _rotation, out int rx, out int rz);
                    _taskExecutor.SetPlan(
                        AgentTask.Navigate(_buildStandX, _buildStandY, _buildStandZ,
                            allowEmergencyBreak: true),
                        AgentTask.Place(buildX, buildY, buildZ, blockId, itemId, nextCell));
                    PrepareShelterTasks();
                }
                else if (_plannerGoal == ShelterGoal.GatherProjectMaterials && canGather)
                {
                    _activeBuildCell = nextCell;
                    if (!world.Chunks.TryGetLoadedBlock(_gatherX, _gatherY, _gatherZ, out int gatherBlockId)
                        || !TryPrepareToolUpgrade(world, gatherBlockId))
                    {
                        SetWorkMode(WorkMode.Gather);
                        _taskExecutor.SetPlan(
                            AgentTask.Navigate(_gatherStandX, _gatherStandY, _gatherStandZ,
                                allowEmergencyBreak: true),
                            AgentTask.Mine(_gatherX, _gatherY, _gatherZ, itemId,
                                GetSteveMineDuration(gatherBlockId)));
                    }
                    PrepareShelterTasks();
                }
                else PrepareExploration(world);

                if (_plannerScanTimer <= 0f) _plannerScanTimer = 2.5f;
                return;
            }

            bool needFrontierDecision = _plannerGoal == ShelterGoal.None || _plannerDecisionTimer <= 0f
                || (_plannerGoal == ShelterGoal.ExploreForBuildSite
                    && _hasGatherTarget && ShelterInventoryCount(itemId) < _projectRequiredCount)
                || (_plannerGoal == ShelterGoal.GatherProjectMaterials
                    && (!_hasGatherTarget || ShelterInventoryCount(itemId) >= _projectRequiredCount));
            if (needFrontierDecision)
            {
                var previousGoal = _plannerGoal;
                double gatherDistance = double.PositiveInfinity;
                if (_hasGatherTarget)
                {
                    double dx = _gatherStandX + 0.5 - Position.X;
                    double dy = _gatherStandY - Position.Y;
                    double dz = _gatherStandZ + 0.5 - Position.Z;
                    gatherDistance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                }
                bool canGather = constructionPlanFinalized && _hasGatherTarget
                    && ShelterInventoryCount(itemId) < _projectRequiredCount;
                _plannerGoal = SelectShelterGoal(false, double.PositiveInfinity, canGather,
                    gatherDistance, canGather ? _gatherOutputCount : 0,
                    canExplore: true);
                if (_plannerGoal != previousGoal && _plannerGoal != ShelterGoal.ExploreForBuildSite)
                    _explorationTaskInstalled = false;
                _plannerDecisionTimer = 1.25f;
            }
            if (_plannerGoal == ShelterGoal.GatherProjectMaterials && _hasGatherTarget
                && ShelterInventoryCount(itemId) < _projectRequiredCount)
            {
                if (!world.Chunks.TryGetLoadedBlock(_gatherX, _gatherY, _gatherZ, out int gatherBlockId)
                    || !TryPrepareToolUpgrade(world, gatherBlockId))
                {
                    SetWorkMode(WorkMode.Gather);
                    _taskExecutor.SetPlan(
                        AgentTask.Navigate(_gatherStandX, _gatherStandY, _gatherStandZ,
                            allowEmergencyBreak: true),
                        AgentTask.Mine(_gatherX, _gatherY, _gatherZ, itemId,
                            GetSteveMineDuration(gatherBlockId)));
                }
                PrepareShelterTasks();
            }
            else PrepareExploration(world);
            if (_plannerScanTimer <= 0f) _plannerScanTimer = 2.5f;
        }

        public void FinishShelterWork(GameWorld world, float dt)
        {
            long started = Stopwatch.GetTimestamp();
            ResetShelterFinishDiagnostics();
            try
            {
                FinishShelterWorkCore(world, dt);
            }
            finally
            {
                _lastShelterFinishTicks = Stopwatch.GetTimestamp() - started;
            }
        }

        private void FinishShelterWorkCore(GameWorld world, float dt)
        {
            if (!world.IsAuthoritative || IsDead || Removed) return;
            if (HasActiveThreat)
            {
                _taskActionWorld = world;
                try { _taskExecutor.Tick(dt, this, _ => false); }
                finally { _taskActionWorld = null; }
                FinishSteveThreatResponse(world);
                return;
            }
            if (_blueprintIndex < 0) return;
            if (!TryResolveTemplate(_blueprintIndex, out var template, out int itemId, out int blockId)) return;
            _taskActionWorld = world;
            _taskActionTemplate = template;
            _taskActionItemId = itemId;
            _taskActionBlockId = blockId;
            try
            {
                _taskExecutor.Tick(dt, this, _taskExecutorAction);
            }
            finally
            {
                _taskActionWorld = null;
                _taskActionTemplate = null;
                _taskActionItemId = _taskActionBlockId = 0;
            }
            // Cooperative actions have their own validation and failure cooldown in the
            // coordinator. Never feed their task failure into the helper's solo planner.
            if (_cooperativeLeader != null) return;
            if (!_taskExecutor.Failed) return;

            // A stale/claimed physical drop is a failed supporting subtask, not a failed gather
            // target or construction project.
            if (_pickupDropTarget != null)
            {
                _ignoredPickupDrop = _pickupDropTarget;
                _ignoredPickupDropTimer = 4f;
                _pickupDropTarget = null;
                _pickupDropTargetValidationTimer = 0f;
                _pickupDropScanTimer = Math.Max(_pickupDropScanTimer, 0.75f);
                _taskExecutor.SetPlan();
                _taskExecutor.Prepare(this);
                _plannerDecisionTimer = 0f;
                return;
            }

            if (_workMode == WorkMode.ToolUp)
            {
                // A tool chain or table route is a transient optimization, not a project
                // failure. Cool it down and resume the actual gather task with bare hands.
                _toolUpgradeRetryTimer = 12f;
                InvalidateUsefulShelterDropDemandCache();
                _hasToolGatherTarget = false;
                _taskExecutor.SetPlan();
                _taskExecutor.Prepare(this);
                _plannerDecisionTimer = 0f;
                return;
            }

            RecordPlannerFailure();
            _plannerDecisionTimer = 0f;
            if (_plannerGoal == ShelterGoal.ExploreForBuildSite)
            {
                _hasExplorationTarget = false;
                _explorationHold = false;
                _explorationTaskInstalled = false;
                AdvanceExplorationSector();
                _taskExecutor.SetPlan();
                _taskExecutor.Prepare(this);
            }
            else if (_workMode == WorkMode.Gather && _hasGatherTarget)
            {
                _ignoredGatherX = _gatherX; _ignoredGatherY = _gatherY; _ignoredGatherZ = _gatherZ;
                _ignoredGatherTimer = 12f;
                _hasIgnoredGather = true;
                _hasGatherTarget = false;
                _gatherTargetValidationTimer = 0f;
                _gatherOutputCount = 0;
                InvalidateGatherTargetSearchCaches();
                InvalidateUsefulShelterDropDemandCache();
                _taskExecutor.SetPlan();
                _taskExecutor.Prepare(this);
            }
            else
            {
                ReplanShelter(0.5f);
            }
        }

        private void PrepareShelterTasks() => _taskExecutor.Prepare(this);

        internal bool TryRecoverStuckNavigation(int recoveryAttempt)
        {
            if (recoveryAttempt <= 0) return TryJumpForNavigationRecovery();
            if (recoveryAttempt != 1 || _taskActionWorld is not { IsAuthoritative: true } world
                || IsDead || Removed) return false;

            // One last-resort bridge/step per installed route. It must be attached to a safe
            // support or fill exactly one loaded gap; emergency terrain mining is never the first
            // response to lack of progress.
            int cx = (int)Math.Floor(Position.X), cy = (int)Math.Floor(Position.Y), cz = (int)Math.Floor(Position.Z);
            if (!IsWalkableStand(world, cx, cy, cz)) return false;
            int dx = Math.Abs(Math.Sin(Yaw)) >= Math.Abs(Math.Cos(Yaw)) ? Math.Sign(Math.Sin(Yaw)) : 0;
            int dz = dx == 0 ? Math.Sign(Math.Cos(Yaw)) : 0;
            if (dx == 0 && dz == 0) return false;
            int tx = cx + dx, tz = cz + dz;
            if (!world.Chunks.TryGetLoadedBlock(tx, cy, tz, out int body)
                || body != BlockRegistry.AirId
                || !world.Chunks.TryGetLoadedBlock(tx, cy + 1, tz, out int head)
                || head != BlockRegistry.AirId) return false;

            int placeY;
            if (world.Chunks.TryGetLoadedBlock(tx, cy - 1, tz, out int floor) && IsSafeGroundBlock(floor))
                placeY = cy; // a single, jumpable step on existing ground
            else
            {
                if (!world.Chunks.TryGetLoadedBlock(tx, cy - 1, tz, out int gap)
                    || gap != BlockRegistry.AirId) return false;
                placeY = cy - 1; // bridge one cell only; never extend a pillar or tunnel
            }
            if (!world.Chunks.TryGetLoadedBlock(tx, placeY, tz, out int existing)
                || existing != BlockRegistry.AirId) return false;

            for (int i = 0; i < _shelterInventory.Length; i++)
            {
                var slot = _shelterInventory[i];
                int itemId = slot.ItemId;
                if (slot.Count <= 0 || !IsSafeSteveBuildMaterial(itemId)
                    || IsShelterItemReserved(itemId)) continue;
                int blockId = ItemRegistry.ResolveBlockId(itemId);
                if (blockId <= 0 || !world.TryPlaceBlockForSteve(this, tx, placeY, tz, blockId, itemId)) continue;
                ConsumeShelterItem(itemId, 1);
                return true;
            }
            return false;
        }

        private bool IsShelterItemReserved(int itemId)
        {
            if (itemId == _projectMaterialItemId && _blueprintIndex >= 0)
            {
                int unplaced = Math.Max(0, _projectRequiredCount - CountPlaced(_projectRequiredCount));
                if (ShelterInventoryCount(itemId) <= unplaced) return true;
            }
            if (_cooperativeLeader != null && itemId == _cooperativeItemId)
            {
                int assigned = _cooperativeKind == SteveCooperativeKind.GatherMaterial
                    ? ShelterInventoryCount(itemId) : 1;
                if (ShelterInventoryCount(itemId) <= assigned) return true;
            }
            return false;
        }

        private const double MaxDropPickupRouteDistance = GameWorld.SteveDropPickupSearchDistance;

        private bool TryPrepareUsefulDropPickup(GameWorld world)
        {
            if (!world.IsAuthoritative || _blueprintIndex < 0) return false;

            if (_pickupDropTarget != null)
            {
                if (IsActiveUsefulDropTarget(world, _pickupDropTarget))
                {
                    // Keep the current pickup plan installed while its navigation/action step
                    // advances. A throttled scan must never hand control back to wander.
                    PrepareShelterTasks();
                    return true;
                }

                // The selected drop was collected, claimed, exhausted, or its executor ended.
                // Cancel only this supporting subtask, then let the project/gather planner resume.
                _pickupDropTarget = null;
                _pickupDropTargetValidationTimer = 0f;
                _taskExecutor.SetPlan();
                _pickupDropScanTimer = 0f;
            }

            if (_pickupDropScanTimer > 0f) return false;
            _pickupDropScanTimer = 0.75f;
            if (world.DroppedItems.Count == 0) return false;

            RecordShelterDropScan();

            bool hasPickableDrop = false;
            var drops = world.DroppedItems;
            for (int i = 0; i < drops.Count; i++)
            {
                var drop = drops[i];
                if (drop.Count > 0 && drop.Age > GameWorld.ItemDropPickupDelay && drop.FlyTime <= 0f
                    && !world.IsDropInLocalPlayerPickupRange(drop)
                    && !IsIgnoredPickupDrop(drop))
                {
                    hasPickableDrop = true;
                    break;
                }
            }
            if (!hasPickableDrop) return false;

            var demandByItem = GetCachedUsefulShelterDropDemands(world);
            DroppedItem? bestDrop = null;
            int bestX = 0, bestY = 0, bestZ = 0;
            double bestDistance = double.PositiveInfinity;
            for (int i = 0; i < drops.Count; i++)
            {
                var drop = drops[i];
                if (drop.Count <= 0 || drop.Age <= GameWorld.ItemDropPickupDelay || drop.FlyTime > 0f
                    || world.IsDropInLocalPlayerPickupRange(drop) || IsIgnoredPickupDrop(drop)) continue;

                int usefulCount = demandByItem.TryGetValue(drop.ItemId, out int demand)
                    ? Math.Min(demand, GetShelterItemCapacity(drop.ItemId)) : 0;
                if (usefulCount <= 0) continue;

                bool alreadyInRange = GameWorld.IsItemDropInPickupRange(drop, Position);
                int standX = 0, standY = 0, standZ = 0;
                if (!alreadyInRange && !TryFindDropPickupStand(world, drop,
                    out standX, out standY, out standZ)) continue;
                int sx = alreadyInRange ? (int)Math.Floor(Position.X) : standX;
                int sy = alreadyInRange ? (int)Math.Floor(Position.Y) : standY;
                int sz = alreadyInRange ? (int)Math.Floor(Position.Z) : standZ;
                double dx = sx + 0.5 - Position.X;
                double dy = sy - Position.Y;
                double dz = sz + 0.5 - Position.Z;
                double distance = alreadyInRange ? -1.0 : dx * dx + dy * dy + dz * dz;
                if (distance > MaxDropPickupRouteDistance * MaxDropPickupRouteDistance
                    || distance >= bestDistance) continue;

                bestDistance = distance;
                bestDrop = drop;
                bestX = sx; bestY = sy; bestZ = sz;
            }

            if (bestDrop == null) return false;
            _pickupDropTarget = bestDrop;
            _pickupDropTargetValidationTimer = StevePlanningCacheSeconds;
            if (GameWorld.IsItemDropInPickupRange(bestDrop, Position))
                _taskExecutor.SetPlan(AgentTask.Pickup(bestDrop));
            else
                _taskExecutor.SetPlan(AgentTask.Navigate(bestX, bestY, bestZ),
                    AgentTask.Pickup(bestDrop));
            PrepareShelterTasks();
            return true;
        }

        private bool IsActiveUsefulDropTarget(GameWorld world, DroppedItem drop)
        {
            if (!_taskExecutor.HasTasks || _taskExecutor.IsComplete || _taskExecutor.Failed
                || !_taskExecutor.TryGetCurrent(out var task)
                || task.Kind is not (AgentTaskKind.Navigate or AgentTaskKind.Pickup)
                || (task.Kind == AgentTaskKind.Pickup && !ReferenceEquals(task.Drop, drop))) return false;

            if (_pickupDropTargetValidationTimer <= 0f)
            {
                _pickupDropTargetValidationTimer = StevePlanningCacheSeconds;
                bool stillPresent = false;
                var drops = world.DroppedItems;
                for (int i = 0; i < drops.Count; i++)
                {
                    if (!ReferenceEquals(drops[i], drop)) continue;
                    stillPresent = true;
                    break;
                }
                if (!stillPresent) return false;
            }
            return drop.Count > 0 && drop.Age > GameWorld.ItemDropPickupDelay
                && drop.FlyTime <= 0f && !world.IsDropInLocalPlayerPickupRange(drop)
                && GetUsefulShelterDropDemand(world, drop.ItemId) > 0
                && GetShelterItemCapacity(drop.ItemId) > 0;
        }

        private bool IsIgnoredPickupDrop(DroppedItem drop)
            => _ignoredPickupDropTimer > 0f && ReferenceEquals(drop, _ignoredPickupDrop);

        private bool TryFindDropPickupStand(GameWorld world, DroppedItem drop,
            out int standX, out int standY, out int standZ)
        {
            standX = standY = standZ = 0;
            int centerX = (int)Math.Floor(drop.Position.X);
            int centerY = (int)Math.Floor(drop.Position.Y);
            int centerZ = (int)Math.Floor(drop.Position.Z);
            double bestDistance = double.PositiveInfinity;
            bool found = false;
            for (int sy = centerY - 1; sy <= centerY + 1; sy++)
            for (int sz = centerZ - 1; sz <= centerZ + 1; sz++)
            for (int sx = centerX - 1; sx <= centerX + 1; sx++)
            {
                if (!IsWalkableStand(world, sx, sy, sz)) continue;
                var feet = new Point3D(sx + 0.5, sy, sz + 0.5);
                if (!GameWorld.IsItemDropInPickupRange(drop, feet)) continue;
                double dx = sx + 0.5 - Position.X;
                double dy = sy - Position.Y;
                double dz = sz + 0.5 - Position.Z;
                double distance = dx * dx + dy * dy + dz * dz;
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                standX = sx; standY = sy; standZ = sz;
                found = true;
            }
            return found;
        }

        private bool ExecuteCurrentTaskAction(AgentTask task)
        {
            if (_taskActionWorld == null || _taskActionTemplate == null) return false;
            if (_cooperativeLeader != null)
                return TryExecuteCooperativeTask(_taskActionWorld, task);
            return ExecuteShelterAction(_taskActionWorld, _taskActionTemplate,
                _taskActionItemId, _taskActionBlockId, task);
        }

        private bool ExecuteShelterAction(GameWorld world, ShelterTemplate template, int itemId, int blockId, AgentTask task)
        {
            RecordShelterTaskAction();
            if (task.Kind == AgentTaskKind.Pickup)
            {
                if (task.Drop == null || !ReferenceEquals(task.Drop, _pickupDropTarget)) return false;
                return world.TryPickupDropForSteve(this, task.Drop, out _);
            }

            if (task.Kind == AgentTaskKind.Craft)
            {
                if (_workMode != WorkMode.ToolUp) return false;
                string station = SteveCraftStationName(task.Tag);
                if (task.BlockId == 3
                    && !world.CanSteveUseCraftingStation(this, task.X, task.Y, task.Z, station)) return false;
                return TryCraftShelterItem(task.ItemId, task.BlockId, station);
            }

            if (task.Kind == AgentTaskKind.BreakObstacle)
            {
                if (_workMode is not (WorkMode.Gather or WorkMode.Build)) return false;
                var selectedBlocker = NavigationBreakTarget;
                return selectedBlocker.HasValue
                    && selectedBlocker.Value.X == task.X && selectedBlocker.Value.Y == task.Y
                    && selectedBlocker.Value.Z == task.Z
                    && selectedBlocker.Value.BlockId == task.BlockId
                    && world.TryBreakObstacleForSteve(this, task.X, task.Y, task.Z, task.BlockId);
            }

            if (task.Kind == AgentTaskKind.Mine)
            {
                if (_workMode == WorkMode.ToolUp && _hasToolGatherTarget
                    && task.X == _toolGatherX && task.Y == _toolGatherY && task.Z == _toolGatherZ
                    && task.ItemId == _toolGatherItemId
                    && world.TryMineForSteve(this, task.X, task.Y, task.Z,
                        _toolGatherItemId, out int toolGathered))
                {
                    if (!AddShelterItem(_toolGatherItemId, toolGathered)) return false;
                    _hasToolGatherTarget = false;
                    return true;
                }

                if (_workMode != WorkMode.Gather || !_hasGatherTarget
                    || task.X != _gatherX || task.Y != _gatherY || task.Z != _gatherZ
                    || task.ItemId != itemId
                    || !world.TryMineForSteve(this, task.X, task.Y, task.Z, itemId, out int gathered)) return false;

                // The world helper reserves capacity before its authoritative edit, so the
                // inventory gain is guaranteed on this same simulation thread.
                if (!AddShelterItem(itemId, gathered)) return false;
                _hasGatherTarget = false;
                _gatherOutputCount = 0;
                _gatherTargetValidationTimer = 0f;
                _plannerDecisionTimer = 0f;
                return true;
            }

            if (task.Kind != AgentTaskKind.Place || _workMode != WorkMode.Build
                || task.Tag != _activeBuildCell || task.Tag < 0 || task.Tag >= template.Cells.Length
                || (_placedMask & (1L << task.Tag)) != 0 || task.ItemId != itemId || task.BlockId != blockId
                || ShelterInventoryCount(itemId) < 1) return false;

            var cell = template.Cells[task.Tag];
            RotateCell(cell, template.Size, _rotation, out int rx, out int rz);
            int x = _anchorX + rx, y = _anchorY + cell.Y, z = _anchorZ + rz;
            if (task.X != x || task.Y != y || task.Z != z
                || !world.TryPlaceBlockForSteve(this, x, y, z, blockId, itemId)) return false;

            // Placement has succeeded through the loaded-only edit path; only now spend the item
            // and advance the private blueprint bit.
            ConsumeShelterItem(itemId, 1);
            _placedMask |= 1L << task.Tag;
            InvalidateUsefulShelterDropDemandCache();
            _plannerDecisionTimer = 0f;
            return true;
        }

        public string GetShelterStatus()
        {
            if (_workMode is WorkMode.Gather or WorkMode.ToolUp) return "Steve: gathering";
            if (_workMode == WorkMode.Build || (_blueprintIndex >= 0 && _workMode != WorkMode.Waiting))
                return "Steve: building";
            return "Steve: scouting";
        }

        public BuildGhostCell[] CaptureShelterGhostCells()
        {
            // Keep saved plans private: no preview cells are exposed to the renderer.
            return Array.Empty<BuildGhostCell>();
        }

        // Internal-only diagnostic for headless checks/debugging; the normal HUD stays generic.
        internal string ShelterSiteSearchDiagnostics
            => $"flat={_lastSiteFlatRejects}, unsafe={_lastSiteSafetyRejects}, occupied={_lastSiteOccupiedRejects}, approach={_lastSiteApproachRejects}";

        private bool TryChooseShelter(GameWorld world)
        {
            RecordShelterSiteSearch();
            double bestScore = double.NegativeInfinity;
            int bestX = 0, bestY = 0, bestZ = 0, bestRotation = 0;
            int bestStandX = 0, bestStandY = 0, bestStandZ = 0;
            ShelterTemplate? bestTemplate = null;
            int baseX = (int)Math.Floor(Position.X);
            int baseZ = (int)Math.Floor(Position.Z);
            int nearY = (int)Math.Floor(Position.Y);
            if (_blueprintIndex < 0 || !TryResolveTemplate(_blueprintIndex, out var template,
                out int itemId, out _)) return false;
            _projectMaterialItemId = itemId;
            if (_projectRequiredCount <= 0) _projectRequiredCount = template.Cells.Length;
            var siteShell = _blueprintIndex == ProceduralBlueprintIndex
                ? _proceduralShell ?? template : template;
            int size = template.Size;
            int flatRejects = 0, safetyRejects = 0, occupiedRejects = 0, approachRejects = 0;

            for (int dz = -8; dz <= 8; dz++)
            {
                for (int dx = -8; dx <= 8; dx++)
                {
                    int ax = baseX + dx - (size - 1) / 2;
                    int az = baseZ + dz - (size - 1) / 2;
                    double steveDx = ax + size * 0.5 - Position.X;
                    double steveDz = az + size * 0.5 - Position.Z;
                    double distance = Math.Sqrt(steveDx * steveDx + steveDz * steveDz);
                    if (distance < 2.7 || distance > 11.5) continue;
                    if (_blueprintIndex == ProceduralBlueprintIndex)
                    {
                        if (!TrySampleProceduralSiteTerrain(world, ax, az, size, nearY,
                            out var terrainSurfaces, out int terrainY))
                        {
                            flatRejects++;
                            continue;
                        }
                        if (!IsShelterAreaSafe(world, ax, terrainY, az, size))
                        {
                            safetyRejects++;
                            continue;
                        }
                        for (int rotation = 0; rotation < 4; rotation++)
                        {
                            if (!TryBuildProceduralSitePlan(world, siteShell, ax, az,
                                terrainSurfaces, terrainY, rotation, out var siteTemplate))
                            {
                                flatRejects++;
                                continue;
                            }
                            if (_hasIgnoredSite && _ignoredSiteTimer > 0f
                                && ax == _ignoredSiteX && terrainY == _ignoredSiteY && az == _ignoredSiteZ) continue;
                            if (!ShelterCellsAreClear(world, siteTemplate, ax, terrainY, az, rotation))
                            {
                                occupiedRejects++;
                                continue;
                            }
                            if (!FindApproach(world, siteTemplate, ax, terrainY, az,
                                out int standX, out int standY, out int standZ))
                            {
                                approachRejects++;
                                continue;
                            }
                            ScoreCandidate(siteTemplate, terrainY, rotation, standX, standY, standZ, ax, az, distance);
                        }
                        continue;
                    }
                    if (!TryFindFlatSiteY(world, ax, az, size, nearY, out int ay))
                    {
                        flatRejects++;
                        continue;
                    }
                    if (_hasIgnoredSite && _ignoredSiteTimer > 0f
                        && ax == _ignoredSiteX && ay == _ignoredSiteY && az == _ignoredSiteZ) continue;
                    if (!IsShelterAreaSafe(world, ax, ay, az, size))
                    {
                        safetyRejects++;
                        continue;
                    }

                    for (int rotation = 0; rotation < 4; rotation++)
                    {
                        if (!ShelterCellsAreClear(world, template, ax, ay, az, rotation))
                        {
                            occupiedRejects++;
                            continue;
                        }
                        if (!FindApproach(world, template, ax, ay, az,
                            out int standX, out int standY, out int standZ))
                        {
                            approachRejects++;
                            continue;
                        }
                        ScoreCandidate(template, ay, rotation, standX, standY, standZ, ax, az, distance);
                    }
                }
            }

            _lastSiteFlatRejects = flatRejects;
            _lastSiteSafetyRejects = safetyRejects;
            _lastSiteOccupiedRejects = occupiedRejects;
            _lastSiteApproachRejects = approachRejects;
            if (double.IsNegativeInfinity(bestScore)) return false;
            _anchorX = bestX; _anchorY = bestY; _anchorZ = bestZ;
            _rotation = bestRotation;
            if (bestTemplate != null)
            {
                _proceduralPlan = bestTemplate;
                template = bestTemplate;
                _projectRequiredCount = template.Cells.Length;
            }
            _placedMask = 0;
            _hasShelterSite = true;
            _siteBlocked = false;
            _activeBuildCell = -1;
            _plannedBuildCell = -1;
            _buildStandX = bestStandX; _buildStandY = bestStandY; _buildStandZ = bestStandZ;
            _plannedBuildCell = FindNextUnplacedCell(template);
            _explorationTaskInstalled = false;
            _planRetryTimer = 0f;
            _taskExecutor.SetPlan();
            ClearPathGoal();
            return true;

            void ScoreCandidate(ShelterTemplate candidate, int ay, int rotation,
                int standX, int standY, int standZ, int ax, int az, double distance)
            {
                double standDx = standX + 0.5 - Position.X;
                double standDy = standY - Position.Y;
                double standDz = standZ + 0.5 - Position.Z;
                double approachDistance = Math.Sqrt(standDx * standDx + standDy * standDy + standDz * standDz);
                double playerDx = ax + size * 0.5 - world.LocalPlayer.Position.X;
                double playerDz = az + size * 0.5 - world.LocalPlayer.Position.Z;
                double playerDistance = Math.Sqrt(playerDx * playerDx + playerDz * playerDz);
                double score = 8.0 - distance * 0.42 - approachDistance * 0.22
                    - candidate.Cells.Length * 0.018 + Math.Min(12, playerDistance) * 0.08;
                score += TemplateSelectionJitter(world.Seed, ax, az, _blueprintIndex);
                if (score <= bestScore) return;
                bestScore = score;
                bestX = ax; bestY = ay; bestZ = az;
                bestRotation = rotation;
                bestStandX = standX; bestStandY = standY; bestStandZ = standZ;
                bestTemplate = _blueprintIndex == ProceduralBlueprintIndex ? candidate : null;
            }
        }

        private static double TemplateSelectionJitter(int worldSeed, int anchorX, int anchorZ, int templateIndex)
        {
            uint hash = unchecked((uint)worldSeed);
            hash ^= unchecked((uint)anchorX * 0x9E3779B9u);
            hash = (hash << 13) | (hash >> 19);
            hash ^= unchecked((uint)anchorZ * 0x85EBCA6Bu);
            hash ^= unchecked((uint)(templateIndex + 1) * 0xC2B2AE35u);
            hash ^= hash >> 16;
            hash *= 0x7FEB352Du;
            hash ^= hash >> 15;
            double unit = (hash & 0xFFFFu) / 65535.0;
            return (unit - 0.5) * 0.5;
        }

        private bool ValidateShelterPlan(GameWorld world)
        {
            RecordShelterSiteValidation();
            if (!_hasShelterSite) return false;
            if (!TryResolveTemplate(_blueprintIndex, out var template, out _, out int blockId)) return false;
            if (_rotation < 0 || _rotation > 3 || template.Cells.Length > 63) return false;
            for (int i = 0; i < template.Cells.Length; i++)
            {
                var cell = template.Cells[i];
                RotateCell(cell, template.Size, _rotation, out int rx, out int rz);
                int x = _anchorX + rx, y = _anchorY + cell.Y, z = _anchorZ + rz;
                if (!world.Chunks.TryGetLoadedBlock(x, y, z, out int current)) return false;
                bool placed = (_placedMask & (1L << i)) != 0;
                if (placed)
                {
                    if (current == BlockRegistry.AirId)
                    {
                        // A removed Steve-placed block is work to resume, not a reason to forget
                        // the project or select another design.
                        _placedMask &= ~(1L << i);
                    }
                    else if (current != blockId) return false;
                }
                else if (current != BlockRegistry.AirId) return false;
            }
            return true;
        }

        private bool ShelterCellsAreClear(GameWorld world, ShelterTemplate template, int ax, int ay, int az, int rotation)
        {
            for (int i = 0; i < template.Cells.Length; i++)
            {
                var cell = template.Cells[i];
                RotateCell(cell, template.Size, rotation, out int rx, out int rz);
                int x = ax + rx, y = ay + cell.Y, z = az + rz;
                if (!world.Chunks.TryGetLoadedBlock(x, y, z, out int id) || id != BlockRegistry.AirId) return false;
                if (world.BlockTicks.IsCellOccupiedByFalling(x, y, z)) return false;
            }
            return true;
        }

        private bool TrySampleProceduralSiteTerrain(GameWorld world, int ax, int az,
            int size, int nearY, out int[,] surfaces, out int anchorY)
        {
            anchorY = 0;
            surfaces = new int[size, size];
            int lowest = int.MaxValue, highest = int.MinValue;
            for (int z = 0; z < size; z++)
            for (int x = 0; x < size; x++)
            {
                if (!TryFindSafeSurface(world, ax + x, az + z, nearY, out int surfaceY)) return false;
                surfaces[x, z] = surfaceY;
                lowest = Math.Min(lowest, surfaceY);
                highest = Math.Max(highest, surfaceY);
            }
            if (highest - lowest > 2) return false;
            anchorY = highest;
            return true;
        }

        private bool TryBuildProceduralSitePlan(GameWorld world, ShelterTemplate shell,
            int ax, int az, int[,] surfaces, int anchorY, int rotation, out ShelterTemplate sitePlan)
        {
            sitePlan = shell;
            var cells = new List<BlueprintCell>(shell.Cells.Length + shell.Size * 4);
            for (int i = 0; i < shell.Cells.Length; i++)
            {
                var cell = shell.Cells[i];
                if (cell.Y != 0) continue;
                RotateCell(cell, shell.Size, rotation, out int rx, out int rz);
                int surfaceY = surfaces[rx, rz];
                for (int y = surfaceY; y < anchorY; y++)
                    cells.Add(new BlueprintCell(cell.X, y - anchorY, cell.Z));
            }
            // Foundations occupy negative local Y below the horizontal build plane. They are
            // first in the build list and therefore placed before the room shell.
            int foundationCount = cells.Count;
            if (foundationCount + shell.Cells.Length > 63) return false;
            cells.AddRange(shell.Cells);
            if (foundationCount > 0)
            {
                for (int i = 0; i < foundationCount; i++)
                {
                    var foundation = cells[i];
                    int y = anchorY + foundation.Y;
                    RotateCell(foundation, shell.Size, rotation, out int rx, out int rz);
                    if (!world.Chunks.TryGetLoadedBlock(ax + rx, y, az + rz, out int id)
                        || id != BlockRegistry.AirId
                        || world.BlockTicks.IsCellOccupiedByFalling(ax + rx, y, az + rz)) return false;
                }
            }
            sitePlan = new ShelterTemplate(shell.Name, shell.ItemName, shell.Size, cells.ToArray());
            return cells.Count <= 63;
        }

        private bool TryFindSafeSurface(GameWorld world, int x, int z, int nearY, out int surfaceY)
        {
            surfaceY = 0;
            for (int y = nearY + 4; y >= nearY - 7; y--)
            {
                if (!world.Chunks.TryGetLoadedBlock(x, y, z, out int air)
                    || air != BlockRegistry.AirId
                    || !world.Chunks.TryGetLoadedBlock(x, y - 1, z, out int floor)
                    || !IsSafeGroundBlock(floor)) continue;
                surfaceY = y;
                return true;
            }
            return false;
        }

        private bool TryFindFlatSiteY(GameWorld world, int ax, int az, int size, int nearY, out int anchorY)
        {
            anchorY = 0;
            for (int y = nearY + 4; y >= nearY - 7; y--)
            {
                bool flat = true;
                for (int z = 0; z < size && flat; z++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        if (!world.Chunks.TryGetLoadedBlock(ax + x, y, az + z, out int air)
                            || air != BlockRegistry.AirId
                            || !world.Chunks.TryGetLoadedBlock(ax + x, y - 1, az + z, out int floor)
                            || !IsSafeGroundBlock(floor))
                        {
                            flat = false;
                            break;
                        }
                    }
                }
                if (!flat) continue;
                anchorY = y;
                return true;
            }
            return false;
        }

        private static bool IsSafeGroundBlock(int id)
        {
            if (!BlockRegistry.IsSolid(id) || BlockRegistry.IsPartialShape(id)
                || BlockRegistry.IsCross(id) || BlockRegistry.IsGravity(id)) return false;
            string material = BlockRegistry.MaterialOf(id);
            return string.Equals(material, "soil", StringComparison.OrdinalIgnoreCase)
                || string.Equals(material, "rock", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsShelterAreaSafe(GameWorld world, int ax, int ay, int az, int size)
        {
            double minX = ax, maxX = ax + size;
            double minY = _blueprintIndex == ProceduralBlueprintIndex ? ay - 2 : ay;
            double maxY = ay + 4;
            double minZ = az, maxZ = az + size;
            var player = world.LocalPlayer.Position;
            double pMinY = player.Y - GameWorld.EyeHeight;
            if (maxX > player.X - GameWorld.PlayerRadius && minX < player.X + GameWorld.PlayerRadius
                && maxY > pMinY && minY < pMinY + GameWorld.PlayerHeight
                && maxZ > player.Z - GameWorld.PlayerRadius && minZ < player.Z + GameWorld.PlayerRadius)
                return false;

            foreach (var entity in world.Entities.Mobs)
            {
                if (entity is not MobEntity mob || mob.IsDead || mob.Removed) continue;
                double half = mob.Width * 0.5;
                if (maxX > mob.Position.X - half && minX < mob.Position.X + half
                    && maxY > mob.Position.Y && minY < mob.Position.Y + mob.Height
                    && maxZ > mob.Position.Z - half && minZ < mob.Position.Z + half)
                    return false;
            }

            // Keep the footprint away from exposed lava/spike cells and do not choose a pad whose
            // immediately surrounding rim is a cliff or unloaded boundary.
            for (int z = az - 1; z <= az + size; z++)
            {
                for (int x = ax - 1; x <= ax + size; x++)
                {
                    bool rim = x < ax || x >= ax + size || z < az || z >= az + size;
                    for (int y = ay - 3; y <= ay + 3; y++)
                    {
                        if (!world.Chunks.TryGetLoadedBlock(x, y, z, out int id))
                        {
                            if (rim && y == ay - 1) return false;
                            continue;
                        }
                        if (id <= 0) continue;
                        string blockName = BlockRegistry.GetName(id);
                        if (string.Equals(blockName, "lava", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(blockName, "spikes", StringComparison.OrdinalIgnoreCase)) return false;
                    }
                    if (rim)
                    {
                        if (_blueprintIndex == ProceduralBlueprintIndex)
                        {
                            if (!TryFindSafeSurface(world, x, z, ay, out int rimY)
                                || Math.Abs(rimY - ay) > 2) return false;
                        }
                        else if (!world.Chunks.TryGetLoadedBlock(x, ay - 1, z, out int floor)
                            || !IsSafeGroundBlock(floor)) return false;
                    }
                }
            }
            return true;
        }

        private bool TryFindBuildApproach(GameWorld world, ShelterTemplate template, int cellIndex,
            out int x, out int y, out int z)
        {
            var cell = template.Cells[cellIndex];
            RotateCell(cell, template.Size, _rotation, out int rx, out int rz);
            int targetX = _anchorX + rx, targetY = _anchorY + cell.Y, targetZ = _anchorZ + rz;
            return FindApproach(world, template, _anchorX, _anchorY, _anchorZ,
                (targetX, targetY, targetZ), out x, out y, out z);
        }

        private bool FindApproach(GameWorld world, ShelterTemplate template, int ax, int ay, int az,
            out int bestX, out int bestY, out int bestZ)
            // Site scoring only needs a broad clearance precheck; the active build plan passes
            // its exact target cell through the overload below.
            => FindApproach(world, template, ax, ay, az, null, out bestX, out bestY, out bestZ);

        private bool FindApproach(GameWorld world, ShelterTemplate template, int ax, int ay, int az,
            (int X, int Y, int Z)? targetCell, out int bestX, out int bestY, out int bestZ)
        {
            bestX = bestY = bestZ = 0;
            double best = double.PositiveInfinity;
            int foundX = 0, foundY = 0, foundZ = 0;
            int size = template.Size;
            for (int i = 0; i < size; i++)
            {
                ConsiderStand(ax - 1, az + i);
                ConsiderStand(ax + size, az + i);
                ConsiderStand(ax + i, az - 1);
                ConsiderStand(ax + i, az + size);
            }
            if (double.IsPositiveInfinity(best)) return false;
            bestX = foundX; bestY = foundY; bestZ = foundZ;
            return true;

            void ConsiderStand(int sx, int sz)
            {
                for (int standY = ay + 1; standY >= ay - 2; standY--)
                {
                    if (!IsWalkableStand(world, sx, standY, sz)) continue;
                    if (targetCell.HasValue
                        && !world.CanStevePlaceCellFromStand(this, targetCell.Value.X,
                            targetCell.Value.Y, targetCell.Value.Z, sx, standY, sz)) continue;
                    double dx = sx + 0.5 - Position.X;
                    double dy = standY - Position.Y;
                    double dz = sz + 0.5 - Position.Z;
                    double score = dx * dx + dy * dy + dz * dz;
                    if (score >= best) continue;
                    best = score;
                    foundX = sx; foundY = standY; foundZ = sz;
                }
            }
        }

        private bool TryFindGatherTarget(GameWorld world, int wantedItemId, out GatherTarget bestTarget,
            Func<int, int, int, bool>? targetClaimed = null)
        {
            RecordShelterResourceSearch();
            bestTarget = default;
            double best = double.PositiveInfinity;
            int centerX = (int)Math.Floor(Position.X);
            int centerZ = (int)Math.Floor(Position.Z);
            int centerY = (int)Math.Floor(Position.Y);
            var player = world.LocalPlayer.Position;
            int minY = centerY - 8, maxY = centerY + 8;

            for (int z = centerZ - 20; z <= centerZ + 20; z++)
            {
                for (int x = centerX - 20; x <= centerX + 20; x++)
                {
                    double dx = x + 0.5 - player.X;
                    double dz = z + 0.5 - player.Z;
                    if (dx * dx + dz * dz < 9.0) continue;
                    for (int y = minY; y <= maxY; y++)
                    {
                        if (!world.Chunks.TryGetLoadedBlock(x, y, z, out int id)
                            || (targetClaimed != null && targetClaimed(x, y, z))
                            || !TryGetSteveGatherYield(world, x, y, z, id, out int outputItem, out int outputCount)
                            || (wantedItemId > 0 && outputItem != wantedItemId)) continue;
                        if (_hasIgnoredGather && _ignoredGatherTimer > 0f
                            && x == _ignoredGatherX && y == _ignoredGatherY && z == _ignoredGatherZ) continue;
                        double steveDx = x + 0.5 - Position.X;
                        double steveDy = y + 0.5 - (Position.Y + 0.5);
                        double steveDz = z + 0.5 - Position.Z;
                        double distance = steveDx * steveDx + steveDy * steveDy + steveDz * steveDz;
                        // Search through the player's full reach plus the block-center margin;
                        // the actual collision ray and LOS decide whether a stand is valid.
                        double searchRadius = GameWorld.BlockReach + 1.5;
                        if (distance > searchRadius * searchRadius) continue;
                        if (!TryFindGatherStand(world, x, y, z, out int sx, out int sy, out int sz)) continue;
                        double standDx = sx + 0.5 - Position.X;
                        double standDy = sy - Position.Y;
                        double standDz = sz + 0.5 - Position.Z;
                        double score = standDx * standDx + standDy * standDy + standDz * standDz
                            + distance * 0.08 - Math.Min(outputCount, 4) * 0.12;
                        if (score >= best) continue;
                        best = score;
                        bestTarget = new GatherTarget(x, y, z, sx, sy, sz, outputCount, score, outputItem);
                    }
                }
            }
            return !double.IsPositiveInfinity(best);
        }

        private bool GatherTargetStillValid(GameWorld world, int wantedItemId)
        {
            var target = new GatherTarget(_gatherX, _gatherY, _gatherZ,
                _gatherStandX, _gatherStandY, _gatherStandZ, _gatherOutputCount, 0.0);
            return GatherTargetStillValid(world, wantedItemId, target);
        }

        private bool GatherTargetStillValid(GameWorld world, int wantedItemId, GatherTarget target)
        {
            if (_hasIgnoredGather && _ignoredGatherTimer > 0f
                && target.X == _ignoredGatherX && target.Y == _ignoredGatherY
                && target.Z == _ignoredGatherZ) return false;
            return world.Chunks.TryGetLoadedBlock(target.X, target.Y, target.Z, out int id)
                && TryGetSteveGatherYield(world, target.X, target.Y, target.Z, id,
                    out int outputItem, out int outputCount)
                && outputItem == wantedItemId && outputCount == target.OutputCount
                && IsWalkableStand(world, target.StandX, target.StandY, target.StandZ)
                && world.CanSteveReachBlockFromStand(this, target.X, target.Y, target.Z,
                    target.StandX, target.StandY, target.StandZ);
        }

        private bool TryFindGatherStand(GameWorld world, int blockX, int blockY, int blockZ,
            out int standX, out int standY, out int standZ)
            => world.TryFindSteveBlockApproach(this, blockX, blockY, blockZ,
                out standX, out standY, out standZ, maxSearchRing: 1);

        internal static bool IsWalkableStand(GameWorld world, int x, int y, int z)
        {
            if (!world.Chunks.TryGetLoadedBlock(x, y - 1, z, out int floor) || !IsSafeGroundBlock(floor)) return false;
            int bodyCells = Math.Max(1, (int)Math.Ceiling(GameWorld.PlayerHeight));
            for (int dy = 0; dy < bodyCells; dy++)
            {
                if (!world.Chunks.TryGetLoadedBlock(x, y + dy, z, out int id) || id != BlockRegistry.AirId) return false;
            }
            return true;
        }

        private static bool CanMineResource(int blockId)
        {
            float hardness = BlockRegistry.HardnessOf(blockId);
            return BlockRegistry.IsSolid(blockId) && hardness > 0f && !float.IsNaN(hardness) && !float.IsInfinity(hardness);
        }

        internal static bool TryGetSteveGatherYield(GameWorld world, int x, int y, int z,
            int blockId, out int itemId, out int count)
        {
            itemId = 0; count = 0;
            if (!CanMineResource(blockId)) return false;
            string outputName;
            string name = BlockRegistry.GetName(blockId);
            if (string.Equals(name, "log", StringComparison.OrdinalIgnoreCase))
            {
                if (!HasNearbyLeaves(world, x, y, z)) return false;
                outputName = "planks";
                count = 4;
            }
            else if (string.Equals(name, "stone", StringComparison.OrdinalIgnoreCase))
            {
                outputName = "cobblestone";
                count = 1;
            }
            else
            {
                // Other materials are gathered only when the block's own registered item is a
                // safe full cube with an exact place-back mapping. Do not invent crafting or drops.
                if (!IsSafeSteveBuildBlock(blockId)
                    || !string.IsNullOrWhiteSpace(BlockRegistry.DropItemOf(blockId))
                    || BlockRegistry.ToolRequiredOf(blockId)
                    || BlockRegistry.HarvestLevelOf(blockId) > 0) return false;
                outputName = name;
                int min = BlockRegistry.DropCountMinOf(blockId);
                int max = BlockRegistry.DropCountMaxOf(blockId);
                count = min > 0 && max >= min && max <= 16 ? max : 1;
            }

            try
            {
                itemId = ItemRegistry.GetId(outputName);
                return itemId > 0 && (itemId == BlockRegistry.GetId("planks")
                    || itemId == BlockRegistry.GetId("cobblestone")
                    || (itemId == blockId && IsSafeSteveBuildMaterial(itemId)));
            }
            catch
            {
                itemId = 0; count = 0;
                return false;
            }
        }

        private static bool IsSafeSteveBuildBlock(int blockId)
        {
            if (blockId <= 0 || blockId >= BlockRegistry.Count
                || !BlockRegistry.IsPlaceable(blockId) || !BlockRegistry.IsSolid(blockId)
                || BlockRegistry.IsPartialShape(blockId) || BlockRegistry.IsCross(blockId)
                || BlockRegistry.IsGravity(blockId) || BlockRegistry.IsDoor(blockId)
                || BlockRegistry.IsRail(blockId) || BlockRegistry.IsTranslucent(blockId)) return false;
            string material = BlockRegistry.MaterialOf(blockId);
            if (material is not ("wood" or "rock" or "soil" or "misc")) return false;
            string name = BlockRegistry.GetName(blockId);
            if (name.Contains("workbench", StringComparison.OrdinalIgnoreCase)
                || name.Contains("furnace", StringComparison.OrdinalIgnoreCase)
                || name.Contains("kiln", StringComparison.OrdinalIgnoreCase)
                || name.Contains("forge", StringComparison.OrdinalIgnoreCase)
                || name.Contains("door", StringComparison.OrdinalIgnoreCase)
                || name.Contains("rail", StringComparison.OrdinalIgnoreCase)
                || name.Contains("spike", StringComparison.OrdinalIgnoreCase)
                || name.Contains("lava", StringComparison.OrdinalIgnoreCase)
                || name.Contains("bomb", StringComparison.OrdinalIgnoreCase)) return false;
            return ItemRegistry.IsInInventory(blockId)
                && ItemRegistry.ResolveBlockId(blockId) == blockId;
        }

        internal static bool IsSafeSteveBuildMaterial(int itemId)
            => itemId > 0 && itemId < BlockRegistry.Count
                && IsSafeSteveBuildBlock(itemId);

        private bool TrySelectProceduralMaterial(GameWorld world, string fallbackName, int requiredCount,
            out string itemName, out int itemId)
        {
            itemName = fallbackName;
            itemId = 0;
            int bestItem = 0, bestCount = 0;
            foreach (var slot in _shelterInventory)
            {
                if (slot.Count <= bestCount || !IsSafeSteveBuildMaterial(slot.ItemId)) continue;
                bestItem = slot.ItemId;
                bestCount = slot.Count;
            }
            if (bestItem > 0)
            {
                if (bestCount >= requiredCount || TryFindGatherTarget(world, bestItem, out _))
                {
                    itemId = bestItem;
                    itemName = ItemRegistry.GetName(bestItem);
                    return true;
                }
            }

            if (TryFindGatherTarget(world, 0, out var target) && target.ItemId > 0)
            {
                itemId = target.ItemId;
                itemName = ItemRegistry.GetName(itemId);
                return true;
            }
            return TryResolveTemplateMaterial(fallbackName, out itemId, out _);
        }

        private static bool HasNearbyLeaves(GameWorld world, int x, int y, int z)
        {
            if (!BlockRegistry.TryGet("leaves", out var leaves)) return false;
            for (int dy = -2; dy <= 4; dy++)
            {
                for (int dz = -3; dz <= 3; dz++)
                {
                    for (int dx = -3; dx <= 3; dx++)
                    {
                        if (Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz) > 6) continue;
                        if (world.Chunks.TryGetLoadedBlock(x + dx, y + dy, z + dz, out int neighbor)
                            && neighbor == leaves.NumericId) return true;
                    }
                }
            }
            return false;
        }

        private bool TryResolveTemplate(int index, out ShelterTemplate template, out int itemId, out int blockId)
        {
            template = null!;
            itemId = blockId = 0;
            if (index == ProceduralBlueprintIndex)
            {
                if (_proceduralPlan == null) return false;
                template = _proceduralPlan;
            }
            else
            {
                if (index < 0 || index >= ShelterTemplates.Length) return false;
                template = ShelterTemplates[index];
            }
            return TryResolveTemplateMaterial(template.ItemName, out itemId, out blockId);
        }

        private static bool TryResolveTemplateMaterial(string itemName, out int itemId, out int blockId)
        {
            itemId = blockId = 0;
            if (!BlockRegistry.TryGet(itemName, out var block) || !BlockRegistry.IsPlaceable(block.NumericId)) return false;
            try
            {
                itemId = ItemRegistry.GetId(itemName);
                blockId = block.NumericId;
                return itemId > 0;
            }
            catch
            {
                itemId = blockId = 0;
                return false;
            }
        }

        private static void RotateCell(BlueprintCell cell, int size, int rotation, out int x, out int z)
        {
            switch (rotation & 3)
            {
                case 1: x = size - 1 - cell.Z; z = cell.X; break;
                case 2: x = size - 1 - cell.X; z = size - 1 - cell.Z; break;
                case 3: x = cell.Z; z = size - 1 - cell.X; break;
                default: x = cell.X; z = cell.Z; break;
            }
        }

        private int FindNextUnplacedCell(ShelterTemplate template)
        {
            for (int i = 0; i < template.Cells.Length; i++)
                if ((_placedMask & (1L << i)) == 0) return i;
            return -1;
        }

        private int CountPlaced(int count)
        {
            int result = 0;
            for (int i = 0; i < count; i++)
                if ((_placedMask & (1L << i)) != 0) result++;
            return result;
        }

        private static long ValidPlacedMask(int count) => count >= 63 ? long.MaxValue : (1L << count) - 1;

        private void SetWorkMode(WorkMode mode)
        {
            if (_workMode == mode) return;
            bool clearsGatherTarget = mode is not (WorkMode.Gather or WorkMode.ToolUp)
                && _hasGatherTarget;
            bool clearsToolGatherTarget = mode != WorkMode.ToolUp && _hasToolGatherTarget;
            _workMode = mode;
            if (mode is not (WorkMode.Gather or WorkMode.ToolUp)) _hasGatherTarget = false;
            if (mode != WorkMode.ToolUp) _hasToolGatherTarget = false;
            if (clearsGatherTarget || clearsToolGatherTarget)
                InvalidateUsefulShelterDropDemandCache();
        }

        private void ReplanShelter(float retryDelay)
        {
            if (_hasShelterSite && _blueprintIndex >= 0)
            {
                if (_placedMask == 0)
                {
                    _ignoredSiteX = _anchorX; _ignoredSiteY = _anchorY; _ignoredSiteZ = _anchorZ;
                    _ignoredSiteTimer = Math.Max(_ignoredSiteTimer, 8f);
                    _hasIgnoredSite = true;
                    _hasShelterSite = false;
                    _siteBlocked = false;
                    _anchorX = _anchorY = _anchorZ = 0;
                    _rotation = 0;
                    if (_blueprintIndex == ProceduralBlueprintIndex && _proceduralShell != null)
                    {
                        _proceduralPlan = _proceduralShell;
                        _projectRequiredCount = _proceduralShell.Cells.Length;
                    }
                }
                else
                {
                    // Once any block is placed, its anchor/design are committed. A temporary
                    // obstruction pauses this site but never discards the construction project.
                    _siteBlocked = true;
                }
            }
            _planRetryTimer = Math.Max(0f, retryDelay);
            _plannerScanTimer = Math.Max(_plannerScanTimer, retryDelay);
            _activeBuildCell = -1;
            _plannedBuildCell = -1;
            _hasGatherTarget = false;
            _gatherTargetValidationTimer = 0f;
            _gatherOutputCount = 0;
            InvalidateUsefulShelterDropDemandCache();
            InvalidateGatherTargetSearchCaches();
            _taskExecutor.SetPlan();
            _explorationTaskInstalled = false;
            _plannerGoal = ShelterGoal.ExploreForBuildSite;
            _plannerDecisionTimer = 0f;
            ClearPathGoal();
        }

        private void ClearShelterPlan()
        {
            _blueprintIndex = -1;
            _anchorX = _anchorY = _anchorZ = 0;
            _rotation = 0;
            _placedMask = 0;
            _hasShelterSite = false;
            _siteBlocked = false;
            _projectSeed = 0;
            _proceduralPlan = null;
            _proceduralShell = null;
            _proceduralPlanGeneratorVersion = 0;
            _projectMaterialItemId = 0;
            _projectRequiredCount = 0;
            _activeBuildCell = -1;
            _plannedBuildCell = -1;
            _hasGatherTarget = false;
            _gatherTargetValidationTimer = 0f;
            _pickupDropTarget = null;
            _pickupDropScanTimer = 0f;
            _pickupDropTargetValidationTimer = 0f;
            _ignoredPickupDrop = null;
            _ignoredPickupDropTimer = 0f;
            InvalidateUsefulShelterDropDemandCache();
            InvalidateGatherTargetSearchCaches();
            InvalidateSteveCraftingStationSearchCaches();
            _plannerGoal = ShelterGoal.None;
            _plannerDecisionTimer = 0f;
            _hasExplorationTarget = false;
            _explorationHold = false;
            _explorationTaskInstalled = false;
            _workMode = WorkMode.Waiting;
            _taskExecutor.SetPlan();
            ClearPathGoal();
        }
    }
}
