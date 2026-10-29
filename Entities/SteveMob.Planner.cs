using System;

namespace Cubuild
{
    public sealed partial class SteveMob
    {
        private enum ShelterGoal
        {
            None,
            ContinueConstructionProject,
            ExploreForBuildSite,
            GatherProjectMaterials,
        }

        private readonly struct GoalCandidate
        {
            public readonly ShelterGoal Goal;
            public readonly double Utility;

            public GoalCandidate(ShelterGoal goal, double utility)
            {
                Goal = goal;
                Utility = utility;
            }
        }

        private const int ExplorationSectorCount = 8;
        private static readonly (int X, int Z)[] ExplorationDirections =
        {
            (0, -1), (1, -1), (1, 0), (1, 1),
            (0, 1), (-1, 1), (-1, 0), (-1, -1),
        };

        private bool _hasShelterSite;
        private bool _siteBlocked;
        private int _projectSeed;
        private int _projectMaterialItemId;
        private int _projectRequiredCount;
        private ShelterGoal _plannerGoal;
        private ShelterGoal _lastFailedGoal;
        private int _plannerChoiceCount;
        private int _plannerFailureCount;
        private float _plannerFailureCooldown;
        private float _plannerDecisionTimer;
        private float _plannerScanTimer;
        private bool _hasExplorationTarget;
        private bool _explorationHold;
        private bool _explorationTaskInstalled;
        private int _explorationTargetX, _explorationTargetY, _explorationTargetZ;
        private int _explorationOriginX, _explorationOriginZ;
        private int _explorationRing, _explorationSector;
        private int _gatherOutputCount;
        private int _lastSiteFlatRejects, _lastSiteSafetyRejects;
        private int _lastSiteOccupiedRejects, _lastSiteApproachRejects;

        public bool ShelterHasSite => _hasShelterSite;
        public int ShelterProjectSeed => _projectSeed;
        public int ShelterMaterialItemId => _projectMaterialItemId;
        public int ShelterRequiredMaterialCount => _projectRequiredCount;
        public int ShelterPlannerGoal => (int)_plannerGoal;
        public int ShelterPlannerChoiceCount => _plannerChoiceCount;
        public int ShelterPlannerFailureCount => _plannerFailureCount;
        public int ShelterPlannerFailureCooldownTicks => (int)Math.Ceiling(_plannerFailureCooldown * 10f);
        public int ShelterPlannerLastFailedGoal => (int)_lastFailedGoal;
        public bool ShelterHasExplorationTarget => _hasExplorationTarget;
        public bool ShelterExplorationHold => _explorationHold;
        public int ShelterExplorationTargetX => _explorationTargetX;
        public int ShelterExplorationTargetY => _explorationTargetY;
        public int ShelterExplorationTargetZ => _explorationTargetZ;
        public int ShelterExplorationOriginX => _explorationOriginX;
        public int ShelterExplorationOriginZ => _explorationOriginZ;
        public int ShelterExplorationRing => _explorationRing;
        public int ShelterExplorationSector => _explorationSector;

        /// <summary>
        /// Restores v17 planner state. Older saves intentionally call only
        /// RestoreShelterState and deterministically initialize the missing planner fields later.
        /// </summary>
        public void RestoreShelterPlannerState(bool hasSite, int projectSeed, int materialItemId,
            int requiredCount, int plannerGoal, int choiceCount, int failureCount,
            int failureCooldownTicks, int lastFailedGoal, bool hasExploreTarget, bool exploreHold,
            int exploreX, int exploreY, int exploreZ, int exploreOriginX, int exploreOriginZ,
            int exploreRing, int exploreSector)
        {
            _hasShelterSite = _blueprintIndex >= 0 && hasSite;
            _siteBlocked = false;
            _projectSeed = projectSeed;
            _projectMaterialItemId = materialItemId;
            _projectRequiredCount = Math.Max(0, requiredCount);
            _plannerGoal = Enum.IsDefined(typeof(ShelterGoal), plannerGoal)
                ? (ShelterGoal)plannerGoal : ShelterGoal.None;
            _plannerChoiceCount = Math.Max(0, choiceCount);
            _plannerFailureCount = Math.Clamp(failureCount, 0, 1000);
            _plannerFailureCooldown = Math.Clamp(failureCooldownTicks / 10f, 0f, 600f);
            _lastFailedGoal = Enum.IsDefined(typeof(ShelterGoal), lastFailedGoal)
                ? (ShelterGoal)lastFailedGoal : ShelterGoal.None;
            _hasExplorationTarget = hasExploreTarget;
            _explorationHold = hasExploreTarget && exploreHold;
            _explorationTargetX = exploreX;
            _explorationTargetY = exploreY;
            _explorationTargetZ = exploreZ;
            _explorationOriginX = exploreOriginX;
            _explorationOriginZ = exploreOriginZ;
            _explorationRing = Math.Clamp(exploreRing, 0, 100000);
            _explorationSector = ((exploreSector % ExplorationSectorCount) + ExplorationSectorCount) % ExplorationSectorCount;
            _explorationTaskInstalled = false;
            _plannerDecisionTimer = 0f;
            _plannerScanTimer = 0f;
        }

        private void EnsureShelterProject(GameWorld world)
        {
            if (_blueprintIndex >= 0)
            {
                if (_projectSeed == 0)
                    _projectSeed = CreateProjectSeed(world.Seed, Position);
                if (TryResolveTemplate(_blueprintIndex, out var savedTemplate, out int savedItemId, out _))
                {
                    _projectMaterialItemId = savedItemId;
                    if (_projectRequiredCount <= 0) _projectRequiredCount = savedTemplate.Cells.Length;
                }
                return;
            }

            var projectPosition = Position + new Point3D(_plannerChoiceCount * 0.37, 0, _plannerChoiceCount * 0.61);
            _projectSeed = CreateProjectSeed(world.Seed, projectPosition);
            uint state = unchecked((uint)_projectSeed ^ 0xA511E9B3u);
            if (state == 0) state = 0x6D2B79F5u;
            double totalWeight = 0;
            for (int i = 0; i < ShelterTemplates.Length; i++)
                totalWeight += TemplateProjectWeight(i);
            double pick = (NextProjectRandom(ref state) / (double)uint.MaxValue) * totalWeight;
            int selected = 0;
            for (int i = 0; i < ShelterTemplates.Length; i++)
            {
                pick -= TemplateProjectWeight(i);
                if (pick <= 0)
                {
                    selected = i;
                    break;
                }
            }

            // Keep the selected design even when the material or a safe site is not currently
            // available. Project identity is independent of local resource scans.
            for (int offset = 0; offset < ShelterTemplates.Length; offset++)
            {
                int index = (selected + offset) % ShelterTemplates.Length;
                if (!TryResolveTemplate(index, out var template, out int itemId, out _)) continue;
                _blueprintIndex = index;
                _lastShelterTemplateIndex = index;
                _projectMaterialItemId = itemId;
                _projectRequiredCount = template.Cells.Length;
                _anchorX = _anchorY = _anchorZ = 0;
                _rotation = 0;
                _placedMask = 0;
                _hasShelterSite = false;
                _siteBlocked = false;
                _activeBuildCell = -1;
                _plannedBuildCell = -1;
                _hasGatherTarget = false;
                _taskExecutor.SetPlan();
                ClearPathGoal();
                return;
            }
        }

        private double TemplateProjectWeight(int index)
        {
            // Compact layouts remain more likely, while all deterministic variants are eligible.
            int cells = ShelterTemplates[index].Cells.Length;
            double weight = Math.Max(0.35, 3.6 - cells * 0.055);
            return index == _lastShelterTemplateIndex ? weight * 0.65 : weight;
        }

        private static int CreateProjectSeed(int worldSeed, Point3D position)
        {
            uint hash = unchecked((uint)worldSeed);
            hash ^= unchecked((uint)(int)Math.Floor(position.X) * 0x9E3779B9u);
            hash = (hash << 13) | (hash >> 19);
            hash ^= unchecked((uint)(int)Math.Floor(position.Y) * 0x85EBCA6Bu);
            hash = (hash << 11) | (hash >> 21);
            hash ^= unchecked((uint)(int)Math.Floor(position.Z) * 0xC2B2AE35u);
            hash ^= hash >> 16;
            hash *= 0x7FEB352Du;
            hash ^= hash >> 15;
            int seed = unchecked((int)hash);
            return seed == 0 ? unchecked((int)0x6D2B79F5u) : seed;
        }

        private static uint NextProjectRandom(ref uint state)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }

        private ShelterGoal SelectShelterGoal(bool canBuild, double buildDistance,
            bool canGather, double gatherDistance, int gatherOutput, bool canExplore)
        {
            // Exploration is only a fallback when the committed project has no actionable
            // work. In particular, distance/utility noise must never pull Steve away from a
            // build cell or a known resource target.
            if (canBuild || canGather)
            {
                if (_plannerGoal == ShelterGoal.ContinueConstructionProject && canBuild)
                    return ShelterGoal.ContinueConstructionProject;
                if (_plannerGoal == ShelterGoal.GatherProjectMaterials && canGather)
                    return ShelterGoal.GatherProjectMaterials;
                if (canBuild) return ShelterGoal.ContinueConstructionProject;
                return ShelterGoal.GatherProjectMaterials;
            }

            var candidates = new GoalCandidate[3];
            int count = 0;
            if (canBuild)
                candidates[count++] = new GoalCandidate(ShelterGoal.ContinueConstructionProject,
                    GoalUtility(ShelterGoal.ContinueConstructionProject, 8.0, 1.0, buildDistance,
                        ShelterInventoryCount(_projectMaterialItemId), 0.12));
            if (canGather)
                candidates[count++] = new GoalCandidate(ShelterGoal.GatherProjectMaterials,
                    GoalUtility(ShelterGoal.GatherProjectMaterials, 6.2, 1.0, gatherDistance,
                        gatherOutput, 0.18));
            if (canExplore)
                candidates[count++] = new GoalCandidate(ShelterGoal.ExploreForBuildSite,
                    GoalUtility(ShelterGoal.ExploreForBuildSite,
                        _hasShelterSite ? 5.4 : 6.3, 1.0,
                        _hasExplorationTarget ? DistanceToExplorationTarget() : 12.0,
                        1, _siteBlocked ? 0.35 : 0.12));

            if (count == 0) return ShelterGoal.ExploreForBuildSite;
            _plannerChoiceCount++;
            uint randomState = unchecked((uint)_projectSeed
                + (uint)_plannerChoiceCount * 0x9E3779B9u + 0xA511E9B3u);
            if (randomState == 0) randomState = 0x6D2B79F5u;
            double total = 0;
            for (int i = 0; i < count; i++) total += candidates[i].Utility * candidates[i].Utility;
            double roll = NextProjectRandom(ref randomState) / (double)uint.MaxValue * total;
            ShelterGoal selected = candidates[count - 1].Goal;
            for (int i = 0; i < count; i++)
            {
                roll -= candidates[i].Utility * candidates[i].Utility;
                if (roll <= 0)
                {
                    selected = candidates[i].Goal;
                    break;
                }
            }

            // Commitment: keep a still-feasible goal unless the weighted challenger is clearly
            // better. The project itself is never replaced by a goal transition.
            if (selected != _plannerGoal)
            {
                double currentUtility = 0;
                for (int i = 0; i < count; i++)
                    if (candidates[i].Goal == _plannerGoal) currentUtility = candidates[i].Utility;
                double selectedUtility = 0;
                for (int i = 0; i < count; i++)
                    if (candidates[i].Goal == selected) selectedUtility = candidates[i].Utility;
                if (currentUtility > 0 && selectedUtility < currentUtility * 1.30 + 0.25)
                    selected = _plannerGoal;
            }
            return selected;
        }

        private double GoalUtility(ShelterGoal goal, double priority, double feasibility, double distance,
            int resourceAvailable, double risk)
        {
            double resourceNeed = Math.Max(1, Math.Min(_projectRequiredCount, 4));
            double resourceFactor = 0.72 + 0.56 * Math.Clamp(resourceAvailable / resourceNeed, 0.0, 1.0);
            double costFactor = 1.0 / (1.0 + Math.Max(0, distance) * 0.045);
            double riskFactor = 1.0 - Math.Clamp(risk, 0.0, 0.9) * 0.35;
            double cooldownFactor = _lastFailedGoal == goal && _plannerFailureCooldown > 0f ? 0.42 : 1.0;
            double stableVariation = 0.96 + StableGoalVariation(goal) * 0.08;
            return Math.Max(0.05, priority * Math.Clamp(feasibility, 0.0, 1.0)
                * resourceFactor * costFactor * riskFactor
                * cooldownFactor * stableVariation);
        }

        private double StableGoalVariation(ShelterGoal goal)
        {
            uint hash = unchecked((uint)_projectSeed ^ ((uint)goal * 0x9E3779B9u));
            hash ^= hash >> 16;
            hash *= 0x7FEB352Du;
            hash ^= hash >> 15;
            return (hash & 0xFFFFu) / 65535.0;
        }

        private double DistanceToExplorationTarget()
        {
            double dx = _explorationTargetX + 0.5 - Position.X;
            double dy = _explorationTargetY - Position.Y;
            double dz = _explorationTargetZ + 0.5 - Position.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private void PrepareExploration(GameWorld world)
        {
            SetWorkMode(WorkMode.Waiting);
            if (_explorationHold && _plannerScanTimer <= 0f)
            {
                // A hold is an explicit idle state, not a completed route that should hand
                // control back to the legacy wander brain. Try the next sector on each rescan.
                _hasExplorationTarget = false;
                _explorationHold = false;
                _explorationTaskInstalled = false;
                AdvanceExplorationSector();
            }
            if (_explorationTaskInstalled && _hasExplorationTarget && !_explorationHold
                && PathStatus == NavigationStatus.Arrived)
            {
                _hasExplorationTarget = false;
                AdvanceExplorationSector();
                _explorationTaskInstalled = false;
            }
            if (!_hasExplorationTarget && !TryFindExplorationTarget(world))
            {
                // Do not let the legacy wander brain take over when no safe loaded waypoint was
                // found. Keep an explicit stand-cell target and retry after the next local scan.
                int x = (int)Math.Floor(Position.X), y = (int)Math.Floor(Position.Y), z = (int)Math.Floor(Position.Z);
                _explorationTargetX = x;
                _explorationTargetY = y;
                _explorationTargetZ = z;
                _hasExplorationTarget = true;
                _explorationHold = true;
                _plannerScanTimer = Math.Max(_plannerScanTimer, 2f);
            }
            if (_hasExplorationTarget)
            {
                var task = _explorationHold
                    ? AgentTask.Hold(_explorationTargetX, _explorationTargetY, _explorationTargetZ)
                    : AgentTask.Navigate(_explorationTargetX, _explorationTargetY, _explorationTargetZ);
                _taskExecutor.SetPlan(task);
                _explorationTaskInstalled = true;
            }
            PrepareShelterTasks();
        }

        private bool TryFindExplorationTarget(GameWorld world)
        {
            if (_explorationRing == 0 && _explorationSector == 0
                && _explorationOriginX == 0 && _explorationOriginZ == 0)
            {
                _explorationOriginX = (int)Math.Floor(Position.X);
                _explorationOriginZ = (int)Math.Floor(Position.Z);
            }
            var direction = ExplorationDirections[_explorationSector];
            int radius = Math.Min(44, 12 + _explorationRing * 12);
            int desiredX = _explorationOriginX + direction.X * radius;
            int desiredZ = _explorationOriginZ + direction.Z * radius;
            int nearY = (int)Math.Floor(Position.Y);
            if (TryFindStandNear(world, desiredX, desiredZ, 8, nearY, direction.X, direction.Z,
                out _explorationTargetX, out _explorationTargetY, out _explorationTargetZ))
            {
                _hasExplorationTarget = true;
                _explorationHold = false;
                return true;
            }

            // A desired sector can be outside the loaded frontier. Search known, loaded ground
            // between Steve and that frontier; no chunk creation or worker-thread world access.
            if (TryFindStandNear(world,
                (int)Math.Floor(Position.X) + direction.X * 16,
                (int)Math.Floor(Position.Z) + direction.Z * 16,
                16, nearY, direction.X, direction.Z,
                out _explorationTargetX, out _explorationTargetY, out _explorationTargetZ))
            {
                _hasExplorationTarget = true;
                _explorationHold = false;
                return true;
            }
            return false;
        }

        private bool TryFindStandNear(GameWorld world, int centerX, int centerZ, int radius, int nearY,
            int directionX, int directionZ, out int bestX, out int bestY, out int bestZ)
        {
            bestX = (int)Math.Floor(Position.X);
            bestY = (int)Math.Floor(Position.Y);
            bestZ = (int)Math.Floor(Position.Z);
            double bestScore = double.PositiveInfinity;
            bool found = false;
            int currentX = (int)Math.Floor(Position.X), currentZ = (int)Math.Floor(Position.Z);
            for (int dz = -radius; dz <= radius; dz++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int x = centerX + dx, z = centerZ + dz;
                    double fromSteveX = x + 0.5 - Position.X;
                    double fromSteveZ = z + 0.5 - Position.Z;
                    double distanceSquared = fromSteveX * fromSteveX + fromSteveZ * fromSteveZ;
                    if (distanceSquared < 36 || distanceSquared > 52 * 52) continue;
                    double progress = (x - currentX) * directionX + (z - currentZ) * directionZ;
                    if (progress < 4) continue;
                    for (int y = nearY + 4; y >= nearY - 7; y--)
                    {
                        if (!IsWalkableStand(world, x, y, z)) continue;
                        double desiredX = x - centerX, desiredZ = z - centerZ;
                        double score = desiredX * desiredX + desiredZ * desiredZ
                            + distanceSquared * 0.018 - progress * 0.08;
                        if (score >= bestScore) continue;
                        bestScore = score;
                        bestX = x; bestY = y; bestZ = z;
                        found = true;
                    }
                }
            }
            return found;
        }

        private void AdvanceExplorationSector()
        {
            _explorationSector++;
            if (_explorationSector >= ExplorationSectorCount)
            {
                _explorationSector = 0;
                _explorationRing = Math.Min(100000, _explorationRing + 1);
            }
        }

        private void RecordPlannerFailure()
        {
            _lastFailedGoal = _plannerGoal;
            _plannerFailureCount = Math.Min(1000, _plannerFailureCount + 1);
            _plannerFailureCooldown = Math.Min(45f, 8f + _plannerFailureCount * 2f);
        }

        private void CompleteShelterProject()
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
            _plannerScanTimer = 0f;
            _taskExecutor.SetPlan();
            ClearPathGoal();
        }
    }
}
