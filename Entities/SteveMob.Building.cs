using System;
using System.Collections.Generic;

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
        private int _plannedBuildCell = -1;
        private DroppedItem? _pickupDropTarget;
        private int _buildStandX, _buildStandY, _buildStandZ;
        private float _ignoredSiteTimer, _ignoredGatherTimer;
        private float _toolUpgradeRetryTimer;
        private bool _hasIgnoredSite, _hasIgnoredGather;
        private int _ignoredSiteX, _ignoredSiteY, _ignoredSiteZ;
        private int _ignoredGatherX, _ignoredGatherY, _ignoredGatherZ;
        private WorkMode _workMode;

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
            public readonly int StandX, StandY, StandZ;
            public readonly int OutputCount;
            public readonly double Score;
            public GatherTarget(int x, int y, int z, int standX, int standY, int standZ, int outputCount, double score)
            {
                X = x; Y = y; Z = z;
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

        public int ShelterBlueprintIndex => _blueprintIndex;
        public int ShelterAnchorX => _anchorX;
        public int ShelterAnchorY => _anchorY;
        public int ShelterAnchorZ => _anchorZ;
        public int ShelterRotation => _rotation;
        public long ShelterPlacedMask => _placedMask;

        public InventorySlot[] CaptureShelterInventory() => (InventorySlot[])_shelterInventory.Clone();

        public void RestoreShelterState(InventorySlot[]? inventory, int blueprintIndex,
            int anchorX, int anchorY, int anchorZ, int rotation, long placedMask)
        {
            _lastShelterTemplateIndex = -1;
            _hasShelterSite = false;
            _siteBlocked = false;
            _projectSeed = 0;
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

            if (blueprintIndex < 0 || blueprintIndex >= ShelterTemplates.Length)
            {
                ClearShelterPlan();
                return;
            }

            _blueprintIndex = blueprintIndex;
            _lastShelterTemplateIndex = blueprintIndex;
            _anchorX = anchorX; _anchorY = anchorY; _anchorZ = anchorZ;
            _rotation = ((rotation % 4) + 4) % 4;
            _placedMask = placedMask & ValidPlacedMask(ShelterTemplates[blueprintIndex].Cells.Length);
            // v16 and earlier only serialized plans that already had a site. v17 may persist a
            // selected project before a site has been found.
            _hasShelterSite = true;
            if (TryResolveTemplate(blueprintIndex, out var template, out int itemId, out _))
            {
                _projectMaterialItemId = itemId;
                _projectRequiredCount = template.Cells.Length;
            }
            _workMode = WorkMode.None;
            _planRetryTimer = 0f;
            _plannerScanTimer = 0f;
            _hasGatherTarget = false;
            _hasToolGatherTarget = false;
            _toolUpgradeRetryTimer = 0f;
            _pickupDropTarget = null;
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
            return remaining == 0;
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
            return remaining == 0;
        }

        public void PrepareShelterWork(GameWorld world, float dt)
        {
            if (!world.IsAuthoritative || IsDead || Removed) return;
            _planRetryTimer = Math.Max(0f, _planRetryTimer - dt);
            _plannerDecisionTimer = Math.Max(0f, _plannerDecisionTimer - dt);
            _plannerScanTimer = Math.Max(0f, _plannerScanTimer - dt);
            _plannerFailureCooldown = Math.Max(0f, _plannerFailureCooldown - dt);
            _toolUpgradeRetryTimer = Math.Max(0f, _toolUpgradeRetryTimer - dt);
            _ignoredSiteTimer = Math.Max(0f, _ignoredSiteTimer - dt);
            _ignoredGatherTimer = Math.Max(0f, _ignoredGatherTimer - dt);
            if (_ignoredSiteTimer <= 0f) _hasIgnoredSite = false;
            if (_ignoredGatherTimer <= 0f) _hasIgnoredGather = false;
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

            if (_hasGatherTarget && !GatherTargetStillValid(world, itemId))
            {
                _hasGatherTarget = false;
                _gatherOutputCount = 0;
                _plannerDecisionTimer = 0f;
            }
            if (ShelterInventoryCount(itemId) >= _projectRequiredCount && _hasGatherTarget)
            {
                _hasGatherTarget = false;
                _gatherOutputCount = 0;
                _plannerDecisionTimer = 0f;
            }
            if (ShelterInventoryCount(itemId) < _projectRequiredCount && !_hasGatherTarget
                && plannerScanDue && TryFindGatherTarget(world, itemId, out var availableTarget))
            {
                _gatherX = availableTarget.X; _gatherY = availableTarget.Y; _gatherZ = availableTarget.Z;
                _gatherStandX = availableTarget.StandX; _gatherStandY = availableTarget.StandY; _gatherStandZ = availableTarget.StandZ;
                _gatherOutputCount = availableTarget.OutputCount;
                _hasGatherTarget = true;
            }

            // Physical drops are a supporting resource subtask, not a replacement construction
            // goal. Keep the selected blueprint and planner goal sticky while a useful nearby
            // drop is collected into Steve's private inventory.
            if (TryPrepareUsefulDropPickup(world))
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

                bool canGather = _hasGatherTarget && ShelterInventoryCount(itemId) < _projectRequiredCount;
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
                bool canGather = _hasGatherTarget && ShelterInventoryCount(itemId) < _projectRequiredCount;
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
            if (!world.IsAuthoritative || IsDead || Removed || _blueprintIndex < 0) return;
            if (!TryResolveTemplate(_blueprintIndex, out var template, out int itemId, out int blockId)) return;
            _taskExecutor.Tick(dt, this, task => ExecuteShelterAction(world, template, itemId, blockId, task));
            if (!_taskExecutor.Failed) return;

            // A stale/claimed physical drop is a failed supporting subtask, not a failed gather
            // target or construction project.
            if (_pickupDropTarget != null)
            {
                _pickupDropTarget = null;
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
                _gatherOutputCount = 0;
                _taskExecutor.SetPlan();
                _taskExecutor.Prepare(this);
            }
            else
            {
                ReplanShelter(0.5f);
            }
        }

        private void PrepareShelterTasks() => _taskExecutor.Prepare(this);

        private const double MaxDropPickupRouteDistance = GameWorld.SteveDropPickupSearchDistance;

        private bool TryPrepareUsefulDropPickup(GameWorld world)
        {
            _pickupDropTarget = null;
            if (!world.IsAuthoritative || _blueprintIndex < 0 || world.DroppedItems.Count == 0) return false;

            bool hasPickableDrop = false;
            foreach (var drop in world.DroppedItems)
            {
                if (drop.Count > 0 && drop.Age > GameWorld.ItemDropPickupDelay && drop.FlyTime <= 0f
                    && !world.IsDropInLocalPlayerPickupRange(drop))
                {
                    hasPickableDrop = true;
                    break;
                }
            }
            if (!hasPickableDrop) return false;

            var demandByItem = BuildUsefulShelterDropDemands(world);
            DroppedItem? bestDrop = null;
            int bestX = 0, bestY = 0, bestZ = 0;
            double bestDistance = double.PositiveInfinity;
            foreach (var drop in world.DroppedItems)
            {
                if (drop.Count <= 0 || drop.Age <= GameWorld.ItemDropPickupDelay || drop.FlyTime > 0f
                    || world.IsDropInLocalPlayerPickupRange(drop)) continue;

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
            if (GameWorld.IsItemDropInPickupRange(bestDrop, Position))
                _taskExecutor.SetPlan(AgentTask.Pickup(bestDrop));
            else
                _taskExecutor.SetPlan(AgentTask.Navigate(bestX, bestY, bestZ),
                    AgentTask.Pickup(bestDrop));
            PrepareShelterTasks();
            return true;
        }

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

        private bool ExecuteShelterAction(GameWorld world, ShelterTemplate template, int itemId, int blockId, AgentTask task)
        {
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
            double bestScore = double.NegativeInfinity;
            int bestX = 0, bestY = 0, bestZ = 0, bestRotation = 0;
            int bestStandX = 0, bestStandY = 0, bestStandZ = 0;
            int baseX = (int)Math.Floor(Position.X);
            int baseZ = (int)Math.Floor(Position.Z);
            int nearY = (int)Math.Floor(Position.Y);
            if (_blueprintIndex < 0 || !TryResolveTemplate(_blueprintIndex, out var template,
                out int itemId, out _)) return false;
            _projectMaterialItemId = itemId;
            if (_projectRequiredCount <= 0) _projectRequiredCount = template.Cells.Length;
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
                        double standDx = standX + 0.5 - Position.X;
                        double standDy = standY - Position.Y;
                        double standDz = standZ + 0.5 - Position.Z;
                        double approachDistance = Math.Sqrt(standDx * standDx + standDy * standDy + standDz * standDz);
                        double playerDx = ax + size * 0.5 - world.LocalPlayer.Position.X;
                        double playerDz = az + size * 0.5 - world.LocalPlayer.Position.Z;
                        double playerDistance = Math.Sqrt(playerDx * playerDx + playerDz * playerDz);
                        // Exact flat ground and the existing hazard/clearance checks remain
                        // intentional constraints. Failed candidates are classified above; when
                        // none survive, the planner advances its explicit frontier target.
                        double score = 8.0 - distance * 0.42 - approachDistance * 0.22
                            - template.Cells.Length * 0.018 + Math.Min(12, playerDistance) * 0.08;
                        score += TemplateSelectionJitter(world.Seed, ax, az, _blueprintIndex);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestX = ax; bestY = ay; bestZ = az;
                            bestRotation = rotation;
                            bestStandX = standX; bestStandY = standY; bestStandZ = standZ;
                        }
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
            double minY = ay, maxY = ay + 3;
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
                    for (int y = ay - 1; y <= ay + 2; y++)
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
                    if (rim && (!world.Chunks.TryGetLoadedBlock(x, ay - 1, z, out int floor)
                        || !IsSafeGroundBlock(floor))) return false;
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
                if (!IsWalkableStand(world, sx, ay, sz)) return;
                if (targetCell.HasValue
                    && !world.CanStevePlaceCellFromStand(this, targetCell.Value.X,
                        targetCell.Value.Y, targetCell.Value.Z, sx, ay, sz)) return;
                double dx = sx + 0.5 - Position.X;
                double dz = sz + 0.5 - Position.Z;
                double score = dx * dx + dz * dz;
                if (score >= best) return;
                best = score;
                foundX = sx; foundY = ay; foundZ = sz;
            }
        }

        private bool TryFindGatherTarget(GameWorld world, int wantedItemId, out GatherTarget bestTarget)
        {
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
                            || !TryGetSteveGatherYield(world, x, y, z, id, out int outputItem, out int outputCount)
                            || outputItem != wantedItemId) continue;
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
                        bestTarget = new GatherTarget(x, y, z, sx, sy, sz, outputCount, score);
                    }
                }
            }
            return !double.IsPositiveInfinity(best);
        }

        private bool GatherTargetStillValid(GameWorld world, int wantedItemId)
        {
            return world.Chunks.TryGetLoadedBlock(_gatherX, _gatherY, _gatherZ, out int id)
                && TryGetSteveGatherYield(world, _gatherX, _gatherY, _gatherZ, id, out int outputItem, out _)
                && outputItem == wantedItemId
                && IsWalkableStand(world, _gatherStandX, _gatherStandY, _gatherStandZ)
                && world.CanSteveReachBlockFromStand(this, _gatherX, _gatherY, _gatherZ,
                    _gatherStandX, _gatherStandY, _gatherStandZ);
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
            else return false;

            try
            {
                itemId = ItemRegistry.GetId(outputName);
                return itemId > 0;
            }
            catch
            {
                itemId = 0; count = 0;
                return false;
            }
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

        private static bool TryResolveTemplate(int index, out ShelterTemplate template, out int itemId, out int blockId)
        {
            template = null!;
            itemId = blockId = 0;
            if (index < 0 || index >= ShelterTemplates.Length) return false;
            template = ShelterTemplates[index];
            if (!BlockRegistry.TryGet(template.ItemName, out var block) || !BlockRegistry.IsPlaceable(block.NumericId)) return false;
            try
            {
                itemId = ItemRegistry.GetId(template.ItemName);
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
            _workMode = mode;
            if (mode is not (WorkMode.Gather or WorkMode.ToolUp)) _hasGatherTarget = false;
            if (mode != WorkMode.ToolUp) _hasToolGatherTarget = false;
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
            _gatherOutputCount = 0;
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
            _projectMaterialItemId = 0;
            _projectRequiredCount = 0;
            _activeBuildCell = -1;
            _plannedBuildCell = -1;
            _hasGatherTarget = false;
            _pickupDropTarget = null;
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
