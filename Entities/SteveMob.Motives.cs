using System;

namespace Cubuild
{
    public sealed partial class SteveMob
    {
        private enum PriorityMotive { None, Recovery, PersimmonPickup, Refuge }

        private const int RecoveryStartHealth = 10;
        private const int RecoveryStopHealth = 14;
        private const long RefugeStartTime = 10000;
        private const long RefugeEndTime = 23000;
        private PriorityMotive _priorityMotive;
        private bool _healthRecoveryActive;
        private bool _seekingPersimmonDrop;
        private float _foodUseCooldown;
        private float _refugeValidationTimer;
        private float _refugeRetryTimer;
        private bool _hasHomeRefuge;
        private int _homeRefugeX, _homeRefugeY, _homeRefugeZ;

        internal bool HasPriorityMotive => _priorityMotive != PriorityMotive.None;
        public bool SteveHasHomeRefuge => _hasHomeRefuge;
        public int SteveHomeRefugeX => _homeRefugeX;
        public int SteveHomeRefugeY => _homeRefugeY;
        public int SteveHomeRefugeZ => _homeRefugeZ;

        public void RestoreSteveHomeRefuge(bool hasHome, int x, int y, int z)
        {
            _hasHomeRefuge = hasHome;
            _homeRefugeX = x;
            _homeRefugeY = y;
            _homeRefugeZ = z;
            _refugeValidationTimer = 0f;
            _refugeRetryTimer = 0f;
        }

        private bool PrepareStevePriorityMotive(GameWorld world, float dt)
        {
            _foodUseCooldown = Math.Max(0f, _foodUseCooldown - Math.Max(0f, dt));
            _refugeValidationTimer = Math.Max(0f, _refugeValidationTimer - Math.Max(0f, dt));
            _refugeRetryTimer = Math.Max(0f, _refugeRetryTimer - Math.Max(0f, dt));

            if (_hasHomeRefuge && _refugeValidationTimer <= 0f)
            {
                _refugeValidationTimer = 0.75f;
                if (TryValidateHomeRefuge(world, out bool valid) && !valid) ClearHomeRefuge();
            }

            if (Health <= RecoveryStartHealth) _healthRecoveryActive = true;
            else if (Health >= RecoveryStopHealth) _healthRecoveryActive = false;

            _seekingPersimmonDrop = false;
            if (_healthRecoveryActive && Health < MaxHealth)
            {
                int persimmonId = GetPersimmonItemId();
                if (persimmonId > 0 && ShelterInventoryCount(persimmonId) > 0)
                {
                    if (_foodUseCooldown <= 0f) TryEatCarriedPersimmon(persimmonId);
                    if (Health < RecoveryStopHealth)
                    {
                        _priorityMotive = PriorityMotive.Recovery;
                        SetPriorityHoldPlan();
                        return true;
                    }
                }
                else if (persimmonId > 0 && _foodUseCooldown <= 0f)
                {
                    _priorityMotive = PriorityMotive.PersimmonPickup;
                    _seekingPersimmonDrop = true;
                    if (TryPreparePersimmonPickup(world, persimmonId))
                    {
                        PrepareShelterTasks();
                        return true;
                    }
                    _priorityMotive = PriorityMotive.None;
                    _seekingPersimmonDrop = false;
                }

                // No carried food or eligible drop is available. Keep the planner productive;
                // the bounded drop scan will retry on a later tick.
            }

            _healthRecoveryActive = Health < RecoveryStopHealth && _healthRecoveryActive;
            if (_hasHomeRefuge && IsNightApproaching(world.WorldTime))
            {
                // Let one already-started world action commit before the lower-priority refuge
                // route replaces its executor step. Emergency recovery and threats were handled
                // above and may still preempt immediately.
                if (_priorityMotive != PriorityMotive.Refuge
                    && !_taskExecutor.Failed
                    && _taskExecutor.TryGetCurrent(out var currentTask)
                    && currentTask.Kind is AgentTaskKind.Mine or AgentTaskKind.Place or AgentTaskKind.Craft)
                {
                    _priorityMotive = PriorityMotive.None;
                    return false;
                }
                if (!TryValidateHomeRefuge(world, out bool valid))
                {
                    DeferRefugeRoute();
                    return false;
                }
                if (!valid)
                {
                    ClearHomeRefuge();
                    _priorityMotive = PriorityMotive.None;
                    return false;
                }
                if (_refugeRetryTimer > 0f)
                {
                    DeferRefugeRoute();
                    return false;
                }
                _priorityMotive = PriorityMotive.Refuge;
                _seekingPersimmonDrop = false;
                _taskExecutor.SetPlan(
                    AgentTask.Navigate(_homeRefugeX, _homeRefugeY, _homeRefugeZ),
                    AgentTask.Hold(_homeRefugeX, _homeRefugeY, _homeRefugeZ));
                PrepareShelterTasks();
                return true;
            }

            _priorityMotive = PriorityMotive.None;
            _seekingPersimmonDrop = false;
            return false;
        }

        private static bool IsNightApproaching(long worldTime)
        {
            long timeOfDay = Math.Max(0L, worldTime) % 24000L;
            return timeOfDay >= RefugeStartTime && timeOfDay < RefugeEndTime;
        }

        private static int GetPersimmonItemId()
            => ItemRegistry.TryGet("persimmon", out var item) ? item.NumericId : 0;

        private bool TryEatCarriedPersimmon(int itemId)
        {
            int foodValue = ItemRegistry.FoodValueOf(itemId);
            int healing = Math.Min(Math.Max(0, MaxHealth - Health), foodValue);
            if (healing <= 0 || ShelterInventoryCount(itemId) <= 0
                || !ConsumeShelterItem(itemId, 1)) return false;
            Health += healing;
            _foodUseCooldown = 1.0f;
            return true;
        }

        private bool TryPreparePersimmonPickup(GameWorld world, int persimmonId)
        {
            if (!world.IsAuthoritative || GetShelterItemCapacity(persimmonId) <= 0) return false;
            if (_pickupDropTarget != null)
            {
                if (IsActiveUsefulDropTarget(world, _pickupDropTarget))
                {
                    PrepareShelterTasks();
                    return true;
                }
                _pickupDropTarget = null;
                _pickupDropTargetValidationTimer = 0f;
                _taskExecutor.SetPlan();
                _pickupDropScanTimer = 0f;
            }

            if (_pickupDropScanTimer > 0f) return false;
            _pickupDropScanTimer = 0.75f;
            DroppedItem? bestDrop = null;
            int bestX = 0, bestY = 0, bestZ = 0;
            double bestDistance = double.PositiveInfinity;
            foreach (var drop in world.DroppedItems)
            {
                if (drop.ItemId != persimmonId || drop.Count <= 0
                    || drop.Age <= GameWorld.ItemDropPickupDelay || drop.FlyTime > 0f
                    || world.IsDropInLocalPlayerPickupRange(drop) || IsIgnoredPickupDrop(drop)) continue;

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

        internal bool IsSeekingPersimmonDrop(int itemId)
            => _seekingPersimmonDrop && _priorityMotive == PriorityMotive.PersimmonPickup
                && itemId == GetPersimmonItemId() && Health < RecoveryStopHealth;

        private void SetPriorityHoldPlan()
        {
            int x = (int)Math.Floor(Position.X);
            int y = (int)Math.Floor(Position.Y);
            int z = (int)Math.Floor(Position.Z);
            _taskExecutor.SetPlan(AgentTask.Hold(x, y, z));
            PrepareShelterTasks();
        }

        private bool TryCaptureHomeRefuge(GameWorld world, ShelterTemplate template)
        {
            bool found = false;
            double bestScore = double.PositiveInfinity;
            int bestX = 0, bestY = 0, bestZ = 0;
            for (int localZ = 0; localZ < template.Size; localZ++)
            for (int localX = 0; localX < template.Size; localX++)
            {
                bool inPlanFootprint = false;
                bool hasPlanRoof = false;
                foreach (var cell in template.Cells)
                {
                    if (cell.X != localX || cell.Z != localZ) continue;
                    inPlanFootprint = true;
                    if (cell.Y >= 2) hasPlanRoof = true;
                }
                if (!inPlanFootprint || !hasPlanRoof) continue;
                RotateCell(new BlueprintCell(localX, 0, localZ), template.Size, _rotation,
                    out int rx, out int rz);
                int x = _anchorX + rx, y = _anchorY, z = _anchorZ + rz;
                if (!IsWalkableStand(world, x, y, z) || !HasHomeRoofCover(world, x, y, z)) continue;
                int edgeDistance = Math.Min(Math.Min(localX, template.Size - 1 - localX),
                    Math.Min(localZ, template.Size - 1 - localZ));
                double dx = x + 0.5 - Position.X, dy = y - Position.Y, dz = z + 0.5 - Position.Z;
                double score = dx * dx + dy * dy + dz * dz - edgeDistance * 100.0;
                if (score >= bestScore) continue;
                bestScore = score;
                bestX = x; bestY = y; bestZ = z;
                found = true;
            }
            if (!found) return false;
            _hasHomeRefuge = true;
            _homeRefugeX = bestX; _homeRefugeY = bestY; _homeRefugeZ = bestZ;
            _refugeValidationTimer = 0f;
            return true;
        }

        private bool IsValidHomeRefuge(GameWorld world)
            => IsWalkableStand(world, _homeRefugeX, _homeRefugeY, _homeRefugeZ)
                && HasHomeRoofCover(world, _homeRefugeX, _homeRefugeY, _homeRefugeZ);

        // A missing chunk is unknown, not evidence that a saved refuge was damaged. Require the
        // bounded area used by the stand, roof, and nearby-structure checks before judging it.
        private bool TryValidateHomeRefuge(GameWorld world, out bool valid)
        {
            valid = false;
            if (!world.Chunks.TryGetLoadedBlock(_homeRefugeX,
                _homeRefugeY - 1, _homeRefugeZ, out _)) return false;
            int bodyCells = Math.Max(1, (int)Math.Ceiling(GameWorld.PlayerHeight));
            for (int dy = 0; dy < bodyCells; dy++)
                if (!world.Chunks.TryGetLoadedBlock(_homeRefugeX,
                    _homeRefugeY + dy, _homeRefugeZ, out _)) return false;
            for (int dy = 1; dy <= 3; dy++)
            for (int dz = -2; dz <= 2; dz++)
            for (int dx = -2; dx <= 2; dx++)
            {
                if (!world.Chunks.TryGetLoadedBlock(_homeRefugeX + dx,
                    _homeRefugeY + dy, _homeRefugeZ + dz, out _)) return false;
            }
            valid = IsValidHomeRefuge(world);
            return true;
        }

        private void DeferRefugeRoute()
        {
            if (_priorityMotive == PriorityMotive.Refuge)
            {
                _taskExecutor.SetPlan();
                ClearPathGoal();
            }
            _priorityMotive = PriorityMotive.None;
            _seekingPersimmonDrop = false;
        }

        private void HandleRefugeRouteFailure(GameWorld world)
        {
            // Route failure is transient. Only a loaded, conclusive structure check can invalidate
            // the remembered anchor; otherwise leave it for the next bounded retry.
            if (_hasHomeRefuge && TryValidateHomeRefuge(world, out bool valid) && !valid)
                ClearHomeRefuge();
            _taskExecutor.SetPlan();
            ClearPathGoal();
            _refugeRetryTimer = Math.Max(_refugeRetryTimer, 6f);
            _priorityMotive = PriorityMotive.None;
        }

        private static bool HasHomeRoofCover(GameWorld world, int x, int y, int z)
        {
            bool covered = false;
            for (int dy = 2; dy <= 3; dy++)
            {
                if (world.Chunks.TryGetLoadedBlock(x, y + dy, z, out int roof)
                    && IsSafeSteveBuildBlock(roof))
                {
                    covered = true;
                    break;
                }
            }
            if (!covered) return false;

            int nearbyStructure = 0;
            for (int dy = 1; dy <= 3; dy++)
            for (int dz = -2; dz <= 2; dz++)
            for (int dx = -2; dx <= 2; dx++)
            {
                if (dx == 0 && dz == 0 && dy == 1) continue;
                if (!world.Chunks.TryGetLoadedBlock(x + dx, y + dy, z + dz, out int block)
                    || !IsSafeSteveBuildBlock(block)) continue;
                if (++nearbyStructure >= 2) return true;
            }
            return false;
        }

        private void ClearHomeRefuge()
        {
            _hasHomeRefuge = false;
            _homeRefugeX = _homeRefugeY = _homeRefugeZ = 0;
            _refugeValidationTimer = 0f;
        }
    }
}
