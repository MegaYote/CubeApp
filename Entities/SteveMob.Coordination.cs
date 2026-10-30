using System;
using System.Collections.Generic;

namespace Cubuild
{
    internal enum SteveCooperativeKind { None, BuildCell, GatherMaterial }

    /// <summary>Transient, opt-in construction help. Assignments are rebuilt from live world state.</summary>
    internal sealed class SteveShelterCoordinator
    {
        private const double JoinDistance = 15.0;
        private const double LeaveDistance = 20.0;
        private const float DecisionInterval = 1.25f;
        private readonly List<SteveMob> _steves = new();
        private readonly HashSet<(int X, int Y, int Z)> _gatherTargetClaims = new();

        public void Update(GameWorld world, IReadOnlyList<IMobRenderable> mobs, float dt)
        {
            if (!world.IsAuthoritative) return;

            _steves.Clear();
            foreach (var renderable in mobs)
                if (renderable is SteveMob steve && !steve.IsDead && !steve.Removed)
                    _steves.Add(steve);

            _gatherTargetClaims.Clear();
            foreach (var leader in _steves)
            {
                leader.ResetCooperativeClaims();
                leader.AddPersonalGatherClaims(_gatherTargetClaims);
                leader.TickCooperativeCooldown(dt);
            }

            // Preserve viable assignments first. This provides hysteresis and makes the claim set
            // independent of the subsequent new-volunteer decisions.
            foreach (var helper in _steves)
            {
                if (!helper.HasCooperativeAssignment) continue;
                var leader = helper.CooperativeLeader;
                if (leader == null || !_steves.Contains(leader)
                    || !helper.RefreshCooperativeAssignment(world, LeaveDistance)
                    || (helper.CooperativeAssignmentKind == SteveCooperativeKind.BuildCell
                        && !leader.TryClaimCooperativeCell(helper.CooperativeCellIndex))
                    || (helper.CooperativeAssignmentKind == SteveCooperativeKind.GatherMaterial
                        && helper.CooperativeGatherTargetNeedsClaim
                        && !_gatherTargetClaims.Add((helper.CooperativeTargetX,
                            helper.CooperativeTargetY, helper.CooperativeTargetZ))))
                {
                    helper.LeaveCooperativeWork(helper.CooperativeTaskFailed ? 8f : 1.25f);
                    continue;
                }
            }

            // A Steve helps at most one peer. Build cells and gather resources are claimed
            // independently so multiple helpers can contribute distinct work.
            foreach (var helper in _steves)
            {
                if (helper.HasCooperativeAssignment || helper.CooperativeCooldown > 0f
                    || helper.CooperativeDecisionCooldown > 0f) continue;
                helper.SetCooperativeDecisionCooldown(DecisionInterval);
                bool canBuildForPeer = helper.IsWillingToHelp(world);
                bool canGatherForPeer = helper.IsWillingToGatherForPeer(world);
                if (!canBuildForPeer && !canGatherForPeer) continue;

                SteveMob? bestLeader = null;
                int bestCell = -1, bestX = 0, bestY = 0, bestZ = 0;
                int bestStandX = 0, bestStandY = 0, bestStandZ = 0;
                int bestItemId = 0, bestBlockId = 0, bestSeed = 0;
                double bestUtility = double.NegativeInfinity;

                // Use compatible carried blocks for a real open cell before assigning a resource
                // run. This keeps the existing direct-build help higher priority.
                if (canBuildForPeer)
                {
                    foreach (var leader in _steves)
                    {
                        if (!TryGetVolunteerPair(world, helper, leader, out double distance,
                            out double stableWillingness)) continue;
                        if (!leader.TryGetCooperativeBuildTarget(world, helper,
                            out int cell, out int x, out int y, out int z,
                            out int standX, out int standY, out int standZ,
                            out int itemId, out int blockId)) continue;

                        double utility = (1.0 / (1.0 + distance * 0.065))
                            + Math.Min(helper.ShelterInventoryCount(itemId), 4) * 0.025
                            - stableWillingness * 0.12;
                        if (utility <= bestUtility) continue;
                        bestUtility = utility;
                        bestLeader = leader;
                        bestCell = cell; bestX = x; bestY = y; bestZ = z;
                        bestStandX = standX; bestStandY = standY; bestStandZ = standZ;
                        bestItemId = itemId; bestBlockId = blockId;
                        bestSeed = leader.ShelterProjectSeed;
                    }
                }

                if (bestLeader != null)
                {
                    if (!bestLeader.TryClaimCooperativeCell(bestCell)) continue;
                    helper.JoinCooperativeBuildWork(bestLeader, bestSeed, bestCell,
                        bestX, bestY, bestZ, bestStandX, bestStandY, bestStandZ,
                        bestItemId, bestBlockId);
                    continue;
                }

                if (!canGatherForPeer) continue;
                bestUtility = double.NegativeInfinity;
                (int X, int Y, int Z, int StandX, int StandY, int StandZ,
                    int OutputCount, int BlockId, float MineDuration) bestTarget = default;
                foreach (var leader in _steves)
                {
                    if (!TryGetVolunteerPair(world, helper, leader, out double distance,
                        out double stableWillingness)
                        || !leader.TryGetCooperativeMaterialNeed(out int itemId, out int deficit)
                        || helper.GetShelterItemCapacity(itemId) <= 0
                        || !helper.TryFindCooperativeGatherTarget(world, itemId,
                            IsGatherTargetClaimed,
                            out var target)
                        || target.OutputCount <= 0
                        || helper.GetShelterItemCapacity(itemId) < target.OutputCount) continue;

                    double utility = (1.0 / (1.0 + distance * 0.065))
                        + Math.Min(deficit, 4) * 0.02
                        - stableWillingness * 0.12;
                    if (utility <= bestUtility) continue;
                    bestUtility = utility;
                    bestLeader = leader;
                    bestItemId = itemId;
                    bestSeed = leader.ShelterProjectSeed;
                    bestTarget = target;
                }

                if (bestLeader == null
                    || !_gatherTargetClaims.Add((bestTarget.X, bestTarget.Y, bestTarget.Z))) continue;
                helper.JoinCooperativeGatherWork(bestLeader, bestSeed, bestItemId,
                    bestTarget.X, bestTarget.Y, bestTarget.Z,
                    bestTarget.StandX, bestTarget.StandY, bestTarget.StandZ,
                    bestTarget.OutputCount, bestTarget.BlockId, bestTarget.MineDuration);
            }
        }

        private bool IsGatherTargetClaimed(int x, int y, int z)
            => _gatherTargetClaims.Contains((x, y, z));

        private bool TryGetVolunteerPair(GameWorld world, SteveMob helper, SteveMob leader,
            out double distance, out double stableWillingness)
        {
            distance = double.PositiveInfinity;
            stableWillingness = 1.0;
            if (ReferenceEquals(helper, leader) || leader.IsDead || leader.Removed) return false;
            double dx = helper.Position.X - leader.Position.X;
            double dy = helper.Position.Y - leader.Position.Y;
            double dz = helper.Position.Z - leader.Position.Z;
            double distanceSquared = dx * dx + dy * dy + dz * dz;
            if (distanceSquared > JoinDistance * JoinDistance || Math.Abs(dy) > 8.0) return false;
            distance = Math.Sqrt(distanceSquared);
            stableWillingness = PairWillingness(helper, leader);
            double joinThreshold = 0.42 + (JoinDistance - distance) * 0.026;
            double leaderWillingness = PairWillingness(leader, helper);
            double acceptThreshold = 0.55 + (JoinDistance - distance) * 0.026;
            return stableWillingness <= joinThreshold && leaderWillingness <= acceptThreshold;
        }

        private static double PairWillingness(SteveMob helper, SteveMob leader)
        {
            uint hash = unchecked((uint)helper.ShelterProjectSeed
                ^ ((uint)leader.ShelterProjectSeed * 0x9E3779B9u));
            hash ^= hash >> 16;
            hash *= 0x7FEB352Du;
            hash ^= hash >> 15;
            return (hash & 0xFFFFu) / 65535.0;
        }
    }

    public sealed partial class SteveMob
    {
        private SteveMob? _cooperativeLeader;
        private int _cooperativeProjectSeed, _cooperativeCellIndex = -1;
        private int _cooperativeX, _cooperativeY, _cooperativeZ;
        private int _cooperativeStandX, _cooperativeStandY, _cooperativeStandZ;
        private int _cooperativeItemId, _cooperativeBlockId;
        private SteveCooperativeKind _cooperativeKind;
        private int _cooperativeGatherOutputCount;
        private float _cooperativeMineDuration;
        private bool _cooperativeGatheringComplete, _cooperativeTransferComplete;
        private float _cooperativeCooldown;
        private float _cooperativeDecisionCooldown;
        private ulong _cooperativeClaimMask;

        internal bool HasCooperativeAssignment => _cooperativeLeader != null;
        internal SteveMob? CooperativeLeader => _cooperativeLeader;
        internal int CooperativeCellIndex => _cooperativeCellIndex;
        internal SteveCooperativeKind CooperativeAssignmentKind => _cooperativeKind;
        internal int CooperativeTargetX => _cooperativeX;
        internal int CooperativeTargetY => _cooperativeY;
        internal int CooperativeTargetZ => _cooperativeZ;
        internal bool CooperativeGatherTargetNeedsClaim => _cooperativeKind == SteveCooperativeKind.GatherMaterial
            && !_cooperativeGatheringComplete;
        internal float CooperativeCooldown => _cooperativeCooldown;
        internal float CooperativeDecisionCooldown => _cooperativeDecisionCooldown;
        internal bool CooperativeTaskFailed => _taskExecutor.Failed;

        internal void TickCooperativeCooldown(float dt)
        {
            float elapsed = Math.Max(0f, dt);
            _cooperativeCooldown = Math.Max(0f, _cooperativeCooldown - elapsed);
            _cooperativeDecisionCooldown = Math.Max(0f, _cooperativeDecisionCooldown - elapsed);
        }

        internal void SetCooperativeDecisionCooldown(float seconds)
            => _cooperativeDecisionCooldown = Math.Max(_cooperativeDecisionCooldown, seconds);

        internal void ResetCooperativeClaims()
        {
            _cooperativeClaimMask = 0;
            if (TryGetOwnBuildClaim(out int cell)) _cooperativeClaimMask |= 1UL << cell;
        }

        internal bool TryClaimCooperativeCell(int cellIndex)
        {
            if (cellIndex < 0 || cellIndex >= 63) return false;
            ulong bit = 1UL << cellIndex;
            if ((_cooperativeClaimMask & bit) != 0) return false;
            _cooperativeClaimMask |= bit;
            return true;
        }

        internal void AddPersonalGatherClaims(HashSet<(int X, int Y, int Z)> claims)
        {
            if (_hasGatherTarget) claims.Add((_gatherX, _gatherY, _gatherZ));
            if (_hasToolGatherTarget) claims.Add((_toolGatherX, _toolGatherY, _toolGatherZ));
        }

        internal bool IsWillingToHelp(GameWorld world)
        {
            if (!world.IsAuthoritative || IsDead || Removed || _blueprintIndex < 0
                || HasActiveThreat
                || _siteBlocked || _plannerGoal != ShelterGoal.ExploreForBuildSite
                || _workMode is WorkMode.Build or WorkMode.Gather or WorkMode.ToolUp
                || _hasGatherTarget || _hasToolGatherTarget || _pickupDropTarget != null
                || _projectMaterialItemId <= 0
                || ShelterInventoryCount(_projectMaterialItemId) <= 0)
                return false;
            return true;
        }

        internal bool IsWillingToGatherForPeer(GameWorld world)
        {
            return world.IsAuthoritative && !IsDead && !Removed && _blueprintIndex >= 0
                && !HasActiveThreat
                && _plannerGoal == ShelterGoal.ExploreForBuildSite
                && _workMode is not (WorkMode.Build or WorkMode.Gather or WorkMode.ToolUp)
                && !_hasGatherTarget && !_hasToolGatherTarget && _pickupDropTarget == null;
        }

        internal bool TryGetCooperativeMaterialNeed(out int itemId, out int deficit)
        {
            itemId = deficit = 0;
            if (IsDead || Removed || HasActiveThreat || !_hasShelterSite || _siteBlocked || _blueprintIndex < 0
                || !TryResolveTemplate(_blueprintIndex, out var template, out itemId, out _)) return false;
            int unplaced = 0;
            for (int i = 0; i < template.Cells.Length; i++)
                if ((_placedMask & (1L << i)) == 0) unplaced++;
            deficit = Math.Max(0, unplaced - ShelterInventoryCount(itemId));
            return deficit > 0 && GetShelterItemCapacity(itemId) > 0;
        }

        internal bool TryFindCooperativeGatherTarget(GameWorld world, int itemId,
            Func<int, int, int, bool> targetClaimed,
            out (int X, int Y, int Z, int StandX, int StandY, int StandZ,
                int OutputCount, int BlockId, float MineDuration) target)
        {
            target = default;
            if (!world.IsAuthoritative || GetShelterItemCapacity(itemId) <= 0) return false;
            GatherTarget found;
            if (!TryFindGatherTargetCached(world, itemId, out found)) return false;
            if (IsUnavailable(found.X, found.Y, found.Z, found.OutputCount))
            {
                if (!TryFindGatherTarget(world, itemId, out found,
                    (x, y, z) => IsUnavailable(x, y, z, int.MaxValue))) return false;
            }
            if (GetShelterItemCapacity(itemId) < found.OutputCount
                || !world.Chunks.TryGetLoadedBlock(found.X, found.Y, found.Z, out int blockId)
                || !TryGetSteveGatherYield(world, found.X, found.Y, found.Z, blockId,
                    out int outputItem, out int outputCount)
                || outputItem != itemId || outputCount != found.OutputCount) return false;
            target = (found.X, found.Y, found.Z, found.StandX, found.StandY, found.StandZ,
                found.OutputCount, blockId, GetSteveMineDuration(blockId));
            return true;

            bool IsUnavailable(int x, int y, int z, int knownOutputCount)
            {
                if (targetClaimed(x, y, z)) return true;
                if (knownOutputCount <= GetShelterItemCapacity(itemId)) return false;
                if (!world.Chunks.TryGetLoadedBlock(x, y, z, out int blockId)
                    || !TryGetSteveGatherYield(world, x, y, z, blockId,
                        out int outputItemId, out int outputCount)) return true;
                return outputItemId != itemId || outputCount > GetShelterItemCapacity(itemId);
            }
        }

        internal bool TryGetOwnBuildClaim(out int cellIndex)
        {
            cellIndex = _activeBuildCell;
            return _hasShelterSite && !_siteBlocked && _blueprintIndex >= 0
                && cellIndex >= 0 && (_placedMask & (1L << cellIndex)) == 0;
        }

        internal bool TryGetCooperativeBuildTarget(GameWorld world, SteveMob helper,
            out int cellIndex, out int x, out int y, out int z,
            out int standX, out int standY, out int standZ, out int itemId, out int blockId)
        {
            cellIndex = -1;
            x = y = z = standX = standY = standZ = itemId = blockId = 0;
            if (!world.IsAuthoritative || IsDead || Removed || HasActiveThreat || !_hasShelterSite || _siteBlocked
                || _blueprintIndex < 0 || !TryResolveTemplate(_blueprintIndex,
                    out var template, out itemId, out blockId)
                || helper._projectMaterialItemId != itemId
                || helper.ShelterInventoryCount(itemId) <= 0) return false;

            double best = double.PositiveInfinity;
            for (int i = 0; i < template.Cells.Length; i++)
            {
                if ((_placedMask & (1L << i)) != 0 || i == _activeBuildCell
                    || (_cooperativeClaimMask & (1UL << i)) != 0) continue;
                var cell = template.Cells[i];
                RotateCell(cell, template.Size, _rotation, out int rx, out int rz);
                int targetX = _anchorX + rx, targetY = _anchorY + cell.Y, targetZ = _anchorZ + rz;
                if (!world.Chunks.TryGetLoadedBlock(targetX, targetY, targetZ, out int existing)
                    || existing != BlockRegistry.AirId
                    || !helper.TryFindCooperativeBuildStand(world, targetX, targetY, targetZ,
                        out int sx, out int sy, out int sz)) continue;

                double dx = sx + 0.5 - helper.Position.X;
                double dy = sy - helper.Position.Y;
                double dz = sz + 0.5 - helper.Position.Z;
                double score = dx * dx + dy * dy + dz * dz + i * 0.0001;
                if (score >= best) continue;
                best = score;
                cellIndex = i; x = targetX; y = targetY; z = targetZ;
                standX = sx; standY = sy; standZ = sz;
            }
            return cellIndex >= 0;
        }

        private bool TryFindCooperativeBuildStand(GameWorld world, int targetX, int targetY,
            int targetZ, out int standX, out int standY, out int standZ)
        {
            standX = standY = standZ = 0;
            double best = double.PositiveInfinity;
            for (int ring = 0; ring <= 8; ring++)
            {
                for (int dz = -ring; dz <= ring; dz++)
                for (int dx = -ring; dx <= ring; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring) continue;
                    int sx = targetX + dx, sz = targetZ + dz;
                    for (int sy = targetY + 1; sy >= targetY - 2; sy--)
                    {
                        if (!IsWalkableStand(world, sx, sy, sz)
                            || !world.CanStevePlaceCellFromStand(this, targetX, targetY, targetZ,
                                sx, sy, sz)
                            || StandIsCrowded(world, sx, sy, sz)) continue;
                        double px = sx + 0.5 - Position.X;
                        double py = sy - Position.Y;
                        double pz = sz + 0.5 - Position.Z;
                        double score = px * px + py * py + pz * pz;
                        if (score >= best) continue;
                        best = score;
                        standX = sx; standY = sy; standZ = sz;
                    }
                }
                // Prefer a nearby reachable ring; farther rings are only used if needed.
                if (!double.IsPositiveInfinity(best)) return true;
            }
            return false;
        }

        private bool StandIsCrowded(GameWorld world, int x, int y, int z)
        {
            foreach (var renderable in world.Entities.Mobs)
            {
                if (renderable is not MobEntity other || ReferenceEquals(other, this)
                    || other.IsDead || other.Removed) continue;
                double dx = other.Position.X - (x + 0.5);
                double dz = other.Position.Z - (z + 0.5);
                if (dx * dx + dz * dz < 0.85 * 0.85
                    && Math.Abs(other.Position.Y - y) < other.Height) return true;
            }
            return false;
        }

        internal bool IsCooperativeCellAvailable(GameWorld world, int projectSeed, int cellIndex,
            int x, int y, int z, int itemId, int blockId)
        {
            if (!world.IsAuthoritative || IsDead || Removed || !_hasShelterSite || _siteBlocked
                || _blueprintIndex < 0 || _projectSeed != projectSeed
                || cellIndex < 0 || cellIndex >= 63 || cellIndex == _activeBuildCell
                || (_placedMask & (1L << cellIndex)) != 0
                || !TryResolveTemplate(_blueprintIndex, out var template,
                    out int actualItemId, out int actualBlockId)
                || itemId != actualItemId || blockId != actualBlockId
                || cellIndex >= template.Cells.Length) return false;
            var cell = template.Cells[cellIndex];
            RotateCell(cell, template.Size, _rotation, out int rx, out int rz);
            return x == _anchorX + rx && y == _anchorY + cell.Y && z == _anchorZ + rz
                && world.Chunks.TryGetLoadedBlock(x, y, z, out int existing)
                && existing == BlockRegistry.AirId;
        }

        internal bool RefreshCooperativeAssignment(GameWorld world, double maxDistance)
        {
            var leader = _cooperativeLeader;
            if (leader == null || _taskExecutor.Failed) return false;
            if (_cooperativeKind == SteveCooperativeKind.BuildCell)
            {
                if (!IsWillingToHelp(world) || leader.HasActiveThreat
                    || !leader.IsCooperativeCellAvailable(world, _cooperativeProjectSeed,
                        _cooperativeCellIndex, _cooperativeX, _cooperativeY, _cooperativeZ,
                        _cooperativeItemId, _cooperativeBlockId)) return false;
            }
            else if (_cooperativeKind == SteveCooperativeKind.GatherMaterial)
            {
                if (!IsWillingToGatherForPeer(world) || leader.HasActiveThreat
                    || leader.ShelterProjectSeed != _cooperativeProjectSeed
                    || !leader.TryGetCooperativeMaterialNeed(out int leaderItemId, out _)
                    || leaderItemId != _cooperativeItemId || _cooperativeTransferComplete) return false;
                double gatherDx = Position.X - leader.Position.X;
                double gatherDy = Position.Y - leader.Position.Y;
                double gatherDz = Position.Z - leader.Position.Z;
                if (gatherDx * gatherDx + gatherDy * gatherDy + gatherDz * gatherDz
                    > maxDistance * maxDistance) return false;
                if (!_cooperativeGatheringComplete)
                {
                    var target = new GatherTarget(_cooperativeX, _cooperativeY, _cooperativeZ,
                        _cooperativeStandX, _cooperativeStandY, _cooperativeStandZ,
                        _cooperativeGatherOutputCount, 0.0);
                    if (!GatherTargetStillValid(world, _cooperativeItemId, target)
                        || !world.Chunks.TryGetLoadedBlock(_cooperativeX, _cooperativeY,
                            _cooperativeZ, out int blockId)
                        || blockId != _cooperativeBlockId
                        || GetShelterItemCapacity(_cooperativeItemId) < _cooperativeGatherOutputCount)
                        return false;

                    _taskExecutor.SetPlan(
                        AgentTask.Navigate(_cooperativeStandX, _cooperativeStandY,
                            _cooperativeStandZ, allowEmergencyBreak: true),
                        AgentTask.Mine(_cooperativeX, _cooperativeY, _cooperativeZ,
                            _cooperativeItemId, _cooperativeMineDuration));
                    _taskExecutor.Prepare(this);
                    return true;
                }

                int carried = ShelterInventoryCount(_cooperativeItemId);
                if (carried <= 0) return false;
                double transferDx = Position.X - leader.Position.X;
                double transferDy = Position.Y - leader.Position.Y;
                double transferDz = Position.Z - leader.Position.Z;
                if (transferDx * transferDx + transferDy * transferDy + transferDz * transferDz
                    <= CooperativeTransferRange * CooperativeTransferRange)
                    _taskExecutor.SetPlan(AgentTask.Deliver(_cooperativeItemId));
                else
                    _taskExecutor.SetPlan(
                        AgentTask.Navigate((int)Math.Floor(leader.Position.X),
                            (int)Math.Floor(leader.Position.Y),
                            (int)Math.Floor(leader.Position.Z), allowEmergencyBreak: true),
                        AgentTask.Deliver(_cooperativeItemId));
                _taskExecutor.Prepare(this);
                return true;
            }
            else return false;

            double dx = Position.X - leader.Position.X;
            double dy = Position.Y - leader.Position.Y;
            double dz = Position.Z - leader.Position.Z;
            if (dx * dx + dy * dy + dz * dz > maxDistance * maxDistance) return false;
            int sx = _cooperativeStandX, sy = _cooperativeStandY, sz = _cooperativeStandZ;
            if (!IsWalkableStand(world, sx, sy, sz)
                || !world.CanStevePlaceCellFromStand(this, _cooperativeX, _cooperativeY,
                    _cooperativeZ, sx, sy, sz)
                || StandIsCrowded(world, sx, sy, sz))
            {
                if (!TryFindCooperativeBuildStand(world, _cooperativeX, _cooperativeY,
                    _cooperativeZ, out sx, out sy, out sz)) return false;
            }

            _cooperativeStandX = sx; _cooperativeStandY = sy; _cooperativeStandZ = sz;
            _taskExecutor.SetPlan(
                AgentTask.Navigate(sx, sy, sz, allowEmergencyBreak: true),
                AgentTask.Place(_cooperativeX, _cooperativeY, _cooperativeZ,
                    _cooperativeBlockId, _cooperativeItemId, _cooperativeCellIndex));
            _taskExecutor.Prepare(this);
            return true;
        }

        private const double CooperativeTransferRange = 3.5;
        private const double CooperativeLeaveRange = 20.0;

        internal void JoinCooperativeBuildWork(SteveMob leader, int projectSeed, int cellIndex,
            int x, int y, int z, int standX, int standY, int standZ, int itemId, int blockId)
        {
            _cooperativeLeader = leader;
            _cooperativeKind = SteveCooperativeKind.BuildCell;
            _cooperativeDecisionCooldown = 0f;
            _cooperativeProjectSeed = projectSeed;
            _cooperativeCellIndex = cellIndex;
            _cooperativeX = x; _cooperativeY = y; _cooperativeZ = z;
            _cooperativeStandX = standX; _cooperativeStandY = standY; _cooperativeStandZ = standZ;
            _cooperativeItemId = itemId; _cooperativeBlockId = blockId;
            _cooperativeGatherOutputCount = 0;
            _cooperativeMineDuration = 0f;
            _cooperativeGatheringComplete = _cooperativeTransferComplete = false;
            _taskExecutor.SetPlan(
                AgentTask.Navigate(standX, standY, standZ, allowEmergencyBreak: true),
                AgentTask.Place(x, y, z, blockId, itemId, cellIndex));
            _taskExecutor.Prepare(this);
        }

        internal void JoinCooperativeGatherWork(SteveMob leader, int projectSeed, int itemId,
            int x, int y, int z, int standX, int standY, int standZ,
            int outputCount, int blockId, float mineDuration)
        {
            _cooperativeLeader = leader;
            _cooperativeKind = SteveCooperativeKind.GatherMaterial;
            _cooperativeDecisionCooldown = 0f;
            _cooperativeProjectSeed = projectSeed;
            _cooperativeCellIndex = -1;
            _cooperativeX = x; _cooperativeY = y; _cooperativeZ = z;
            _cooperativeStandX = standX; _cooperativeStandY = standY; _cooperativeStandZ = standZ;
            _cooperativeItemId = itemId; _cooperativeBlockId = blockId;
            _cooperativeGatherOutputCount = outputCount;
            _cooperativeMineDuration = mineDuration;
            _cooperativeGatheringComplete = _cooperativeTransferComplete = false;
            _taskExecutor.SetPlan(
                AgentTask.Navigate(standX, standY, standZ, allowEmergencyBreak: true),
                AgentTask.Mine(x, y, z, itemId, mineDuration));
            _taskExecutor.Prepare(this);
        }

        internal void LeaveCooperativeWork(float cooldown)
        {
            if (_cooperativeLeader == null) return;
            _cooperativeLeader = null;
            _cooperativeKind = SteveCooperativeKind.None;
            _cooperativeProjectSeed = 0;
            _cooperativeCellIndex = -1;
            _cooperativeItemId = _cooperativeBlockId = 0;
            _cooperativeGatherOutputCount = 0;
            _cooperativeMineDuration = 0f;
            _cooperativeGatheringComplete = _cooperativeTransferComplete = false;
            _cooperativeCooldown = Math.Max(_cooperativeCooldown, cooldown);
            // The solo plan was prepared earlier this frame, then temporarily replaced by this
            // assignment. Do not let a stale cooperative Place step execute during FinishWork.
            _taskExecutor.SetPlan();
            _taskExecutor.Prepare(this);
        }

        private bool ExecuteCooperativePlace(GameWorld world, AgentTask task)
        {
            var leader = _cooperativeLeader;
            if (leader == null || _cooperativeKind != SteveCooperativeKind.BuildCell
                || task.Kind != AgentTaskKind.Place
                || task.Tag != _cooperativeCellIndex
                || task.X != _cooperativeX || task.Y != _cooperativeY || task.Z != _cooperativeZ
                || task.ItemId != _cooperativeItemId || task.BlockId != _cooperativeBlockId
                || ShelterInventoryCount(_cooperativeItemId) <= 0
                || !leader.IsCooperativeCellAvailable(world, _cooperativeProjectSeed,
                    _cooperativeCellIndex, _cooperativeX, _cooperativeY, _cooperativeZ,
                    _cooperativeItemId, _cooperativeBlockId)
                || !world.TryPlaceBlockForSteve(this, task.X, task.Y, task.Z,
                    task.BlockId, task.ItemId)) return false;

            if (!ConsumeShelterItem(_cooperativeItemId, 1)) return false;
            leader.MarkCooperativeCellPlaced(_cooperativeProjectSeed, _cooperativeCellIndex);
            return true;
        }

        private bool ExecuteCooperativeMine(GameWorld world, AgentTask task)
        {
            var leader = _cooperativeLeader;
            if (leader == null) return false;
            double dx = Position.X - leader.Position.X;
            double dy = Position.Y - leader.Position.Y;
            double dz = Position.Z - leader.Position.Z;
            if (dx * dx + dy * dy + dz * dz > CooperativeLeaveRange * CooperativeLeaveRange)
                return false;

            if (_cooperativeKind != SteveCooperativeKind.GatherMaterial
                || _cooperativeGatheringComplete || task.Kind != AgentTaskKind.Mine
                || task.X != _cooperativeX || task.Y != _cooperativeY || task.Z != _cooperativeZ
                || task.ItemId != _cooperativeItemId
                || leader.ShelterProjectSeed != _cooperativeProjectSeed
                || !leader.TryGetCooperativeMaterialNeed(out int itemId, out _)
                || itemId != _cooperativeItemId
                || GetShelterItemCapacity(itemId) < _cooperativeGatherOutputCount
                || !world.Chunks.TryGetLoadedBlock(task.X, task.Y, task.Z, out int blockId)
                || blockId != _cooperativeBlockId
                || !GatherTargetStillValid(world, itemId,
                    new GatherTarget(_cooperativeX, _cooperativeY, _cooperativeZ,
                        _cooperativeStandX, _cooperativeStandY, _cooperativeStandZ,
                        _cooperativeGatherOutputCount, 0.0))
                || !world.TryMineForSteve(this, task.X, task.Y, task.Z,
                    itemId, out int gathered)
                || gathered != _cooperativeGatherOutputCount
                || !AddShelterItem(itemId, gathered)) return false;

            _cooperativeGatheringComplete = true;
            return true;
        }

        private bool ExecuteCooperativeTransfer(GameWorld world, AgentTask task)
        {
            var leader = _cooperativeLeader;
            if (leader == null || _cooperativeKind != SteveCooperativeKind.GatherMaterial
                || !_cooperativeGatheringComplete || _cooperativeTransferComplete
                || task.Kind != AgentTaskKind.Deliver || task.ItemId != _cooperativeItemId
                || leader.ShelterProjectSeed != _cooperativeProjectSeed
                || !leader.TryGetCooperativeMaterialNeed(out int itemId, out int deficit)
                || itemId != _cooperativeItemId) return false;

            double dx = Position.X - leader.Position.X;
            double dy = Position.Y - leader.Position.Y;
            double dz = Position.Z - leader.Position.Z;
            if (dx * dx + dy * dy + dz * dz > CooperativeTransferRange * CooperativeTransferRange)
                return false;

            int carried = ShelterInventoryCount(itemId);
            int transferCount = Math.Min(deficit,
                Math.Min(leader.GetShelterItemCapacity(itemId), carried));
            if (transferCount <= 0 || !ConsumeShelterItem(itemId, transferCount)) return false;
            if (!leader.AddShelterItem(itemId, transferCount))
            {
                // Same-thread capacity checks make this a defensive rollback, not a normal path.
                AddShelterItem(itemId, transferCount);
                return false;
            }
            _cooperativeTransferComplete = true;
            return true;
        }

        private void MarkCooperativeCellPlaced(int projectSeed, int cellIndex)
        {
            if (_projectSeed == projectSeed && cellIndex >= 0 && cellIndex < 63
                && (_placedMask & (1L << cellIndex)) == 0)
            {
                _placedMask |= 1L << cellIndex;
                _plannerDecisionTimer = 0f;
            }
        }

        private bool TryExecuteCooperativeTask(GameWorld world, AgentTask task)
        {
            if (_cooperativeLeader == null) return false;
            return task.Kind switch
            {
                AgentTaskKind.Place => ExecuteCooperativePlace(world, task),
                AgentTaskKind.Mine => ExecuteCooperativeMine(world, task),
                AgentTaskKind.Deliver => ExecuteCooperativeTransfer(world, task),
                _ => false,
            };
        }
    }
}
