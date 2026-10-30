using System;

namespace Cubuild
{
    /// <summary>The deliberately small action vocabulary shared by autonomous mob tasks.</summary>
    public enum AgentTaskKind { Navigate, Mine, Place, Hold, BreakObstacle, Craft, Pickup, Deliver }

    /// <summary>One immutable step in a short, transient agent task sequence.</summary>
    public readonly struct AgentTask : IEquatable<AgentTask>
    {
        public readonly AgentTaskKind Kind;
        public readonly int X, Y, Z;
        public readonly int ItemId, BlockId, Tag;
        public readonly float Duration;
        public readonly bool AllowEmergencyBreak;
        public readonly DroppedItem? Drop;

        private AgentTask(AgentTaskKind kind, int x, int y, int z, int itemId,
            int blockId, int tag, float duration, bool allowEmergencyBreak = false,
            DroppedItem? drop = null)
        {
            Kind = kind;
            X = x; Y = y; Z = z;
            ItemId = itemId; BlockId = blockId; Tag = tag;
            Duration = Math.Max(0f, duration);
            AllowEmergencyBreak = allowEmergencyBreak;
            Drop = drop;
        }

        public static AgentTask Navigate(int standX, int standY, int standZ,
            bool allowEmergencyBreak = false)
            => new(AgentTaskKind.Navigate, standX, standY, standZ, 0, 0, 0, 0f,
                allowEmergencyBreak);

        public static AgentTask Hold(int standX, int standY, int standZ)
            => new(AgentTaskKind.Hold, standX, standY, standZ, 0, 0, 0, 0f);

        public static AgentTask Mine(int x, int y, int z, int itemId, float duration = 1f)
            => new(AgentTaskKind.Mine, x, y, z, itemId, 0, 0, duration);

        public static AgentTask Place(int x, int y, int z, int blockId, int itemId, int tag, float duration = 0.72f)
            => new(AgentTaskKind.Place, x, y, z, itemId, blockId, tag, duration);

        public static AgentTask BreakObstacle(int x, int y, int z, int expectedBlockId)
            => new(AgentTaskKind.BreakObstacle, x, y, z, 0, expectedBlockId, 0, 0f);

        public static AgentTask Craft(int outputItemId, int gridSize, int stationCode,
            int stationX = 0, int stationY = 0, int stationZ = 0, float duration = 0.35f)
            => new(AgentTaskKind.Craft, stationX, stationY, stationZ, outputItemId,
                gridSize, stationCode, duration);

        public static AgentTask Pickup(DroppedItem drop)
            => new(AgentTaskKind.Pickup, 0, 0, 0, drop?.ItemId ?? 0, 0, 0, 0f,
                drop: drop);

        public static AgentTask Deliver(int itemId)
            => new(AgentTaskKind.Deliver, 0, 0, 0, itemId, 0, 0, 0f);

        public bool Equals(AgentTask other)
            => Kind == other.Kind && X == other.X && Y == other.Y && Z == other.Z
                && ItemId == other.ItemId && BlockId == other.BlockId && Tag == other.Tag
                && Duration.Equals(other.Duration) && AllowEmergencyBreak == other.AllowEmergencyBreak
                && ReferenceEquals(Drop, other.Drop);

        public override bool Equals(object? obj) => obj is AgentTask other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(
            HashCode.Combine(Kind, X, Y, Z, ItemId, BlockId, Tag, Duration),
            AllowEmergencyBreak, Drop);
    }

    /// <summary>
    /// Runs a short sequence of navigation and world-action tasks. It owns transient timing and
    /// bounded retries; the supplied action callback must revalidate and perform edits through the
    /// authoritative world APIs. Executor state is intentionally not persisted.
    /// </summary>
    public sealed class AgentTaskExecutor
    {
        private const int MaxAttempts = 3;
        private const int MaxEmergencyBreaksPerNavigation = 12;
        private const float NavigationTimeout = 30f;
        private AgentTask[] _tasks = Array.Empty<AgentTask>();
        private int _index;
        private int _attempts;
        private float _actionTimer;
        private float _navigationTimer;
        private bool _actionStarted;
        private int _emergencyBreakCount;
        private bool _hasLastEmergencyTarget;
        private int _lastEmergencyX, _lastEmergencyY, _lastEmergencyZ, _lastEmergencyBlockId;
        private bool _hasNavigationProgressSample;
        private Point3D _lastNavigationPosition;
        private float _stuckNavigationTimer;
        private int _navigationRecoveryAttempts;

        public bool Failed { get; private set; }
        public bool IsComplete => _tasks.Length > 0 && _index >= _tasks.Length && !Failed;
        public bool HasTasks => _tasks.Length > 0;

        /// <summary>Replace the current plan only when its task sequence actually changed.</summary>
        public void SetPlan(params AgentTask[] tasks)
        {
            tasks ??= Array.Empty<AgentTask>();
            bool equal = tasks.Length == _tasks.Length;
            for (int i = 0; equal && i < tasks.Length; i++) equal = tasks[i].Equals(_tasks[i]);
            if (equal) return;

            _tasks = (AgentTask[])tasks.Clone();
            _index = 0;
            _attempts = 0;
            _actionTimer = _navigationTimer = 0f;
            _actionStarted = false;
            _emergencyBreakCount = 0;
            _hasLastEmergencyTarget = false;
            _hasNavigationProgressSample = false;
            _stuckNavigationTimer = 0f;
            _navigationRecoveryAttempts = 0;
            Failed = false;
        }

        /// <summary>
        /// Allocation-free fast path for the common single-step plan. A replacement array is
        /// created only when the task actually changes.
        /// </summary>
        public void SetPlan(AgentTask task)
        {
            if (_tasks.Length == 1 && task.Equals(_tasks[0])) return;
            SetPlanCore(new[] { task });
        }

        /// <summary>
        /// Allocation-free comparison for the common navigate/action pair used by Steve. A
        /// replacement array is created only when the task sequence actually changes.
        /// </summary>
        public void SetPlan(AgentTask first, AgentTask second)
        {
            if (_tasks.Length == 2 && first.Equals(_tasks[0]) && second.Equals(_tasks[1])) return;
            SetPlanCore(new[] { first, second });
        }

        private void SetPlanCore(AgentTask[] tasks)
        {
            _tasks = tasks;
            _index = 0;
            _attempts = 0;
            _actionTimer = _navigationTimer = 0f;
            _actionStarted = false;
            _emergencyBreakCount = 0;
            _hasLastEmergencyTarget = false;
            _hasNavigationProgressSample = false;
            _stuckNavigationTimer = 0f;
            _navigationRecoveryAttempts = 0;
            Failed = false;
        }

        /// <summary>Install the current navigation step before the mob's movement update.</summary>
        public void Prepare(MobEntity agent)
        {
            if (agent == null) return;
            if (_index < _tasks.Length && !Failed
                && _tasks[_index].Kind is AgentTaskKind.Navigate or AgentTaskKind.Hold)
            {
                var task = _tasks[_index];
                agent.SetNavigationGoal(task.X, task.Y, task.Z,
                    allowEmergencyBreak: task.AllowEmergencyBreak);
            }
            else if (_index > 0 && _index < _tasks.Length && _tasks[_index - 1].Kind == AgentTaskKind.Navigate)
            {
                // Keep the mob anchored at its stand position while a mine/place action runs.
                var stand = _tasks[_index - 1];
                agent.SetNavigationGoal(stand.X, stand.Y, stand.Z);
            }
            else if (agent.PathStatus != NavigationStatus.None)
            {
                agent.ClearPathGoal();
            }
        }

        /// <summary>Advance actions after movement, so an action cannot complete before arrival.</summary>
        public void Tick(float dt, MobEntity agent, Func<AgentTask, bool> executeAction)
        {
            if (Failed || _index >= _tasks.Length) return;
            var task = _tasks[_index];
            if (task.Kind is AgentTaskKind.Navigate or AgentTaskKind.Hold)
            {
                _navigationTimer += Math.Max(0f, dt);
                var status = agent.PathStatus;
                if (status == NavigationStatus.Arrived)
                {
                    if (task.Kind == AgentTaskKind.Hold)
                    {
                        _navigationTimer = 0f;
                        _attempts = 0;
                    }
                    else Advance(agent);
                    return;
                }

                if (task.Kind == AgentTaskKind.Navigate && status == NavigationStatus.NeedsBreak)
                {
                    _navigationTimer = 0f;
                    var target = agent.NavigationBreakTarget;
                    if (!target.HasValue)
                    {
                        if (Retry()) agent.RetryNavigationGoal();
                        return;
                    }

                    var blocker = target.Value;
                    if (!_hasLastEmergencyTarget || blocker.X != _lastEmergencyX
                        || blocker.Y != _lastEmergencyY || blocker.Z != _lastEmergencyZ
                        || blocker.BlockId != _lastEmergencyBlockId)
                    {
                        _hasLastEmergencyTarget = true;
                        _lastEmergencyX = blocker.X; _lastEmergencyY = blocker.Y;
                        _lastEmergencyZ = blocker.Z; _lastEmergencyBlockId = blocker.BlockId;
                        _attempts = 0;
                        _actionStarted = false;
                        _actionTimer = 0f;
                    }

                    if (_emergencyBreakCount >= MaxEmergencyBreaksPerNavigation)
                    {
                        Failed = true;
                        return;
                    }
                    if (!_actionStarted)
                    {
                        // Steve's private family-matching tool speed applies here too; other mobs
                        // retain the existing hardness-only delay. The world callback revalidates
                        // the exact block at completion.
                        _actionTimer = agent is SteveMob steve
                            ? steve.GetSteveMineDuration(blocker.BlockId)
                            : Math.Clamp(0.75f + blocker.Hardness * 0.4f, 0.8f, 8f);
                        _actionStarted = true;
                        return;
                    }
                    _actionTimer = Math.Max(0f, _actionTimer - Math.Max(0f, dt));
                    if (_actionTimer > 0f) return;

                    var breakTask = AgentTask.BreakObstacle(blocker.X, blocker.Y, blocker.Z,
                        blocker.BlockId);
                    if (executeAction(breakTask))
                    {
                        _emergencyBreakCount++;
                        _attempts = 0;
                        _actionStarted = false;
                        _actionTimer = 0f;
                        _hasLastEmergencyTarget = false;
                        agent.RetryNavigationGoal();
                    }
                    else
                    {
                        bool retry = Retry();
                        agent.RetryNavigationGoal();
                        if (!retry) return;
                    }
                    return;
                }

                if (task.Kind == AgentTaskKind.Navigate && TryRecoverStuckNavigation(dt, agent))
                    return;

                bool routeExhausted = status == NavigationStatus.Unreachable
                    || (status == NavigationStatus.Partial && !agent.HasPath);
                if (routeExhausted || _navigationTimer >= NavigationTimeout)
                {
                    if (!Retry()) return;
                    agent.RetryNavigationGoal();
                    _navigationTimer = 0f;
                }
                return;
            }

            if (_index > 0 && _tasks[_index - 1].Kind == AgentTaskKind.Navigate
                && agent.PathStatus != NavigationStatus.Arrived)
            {
                _navigationTimer += Math.Max(0f, dt);
                bool routeExhausted = agent.PathStatus == NavigationStatus.Unreachable
                    || (agent.PathStatus == NavigationStatus.Partial && !agent.HasPath);
                if (routeExhausted || _navigationTimer >= NavigationTimeout)
                {
                    if (!Retry()) return;
                    var stand = _tasks[_index - 1];
                    agent.SetNavigationGoal(stand.X, stand.Y, stand.Z);
                    agent.RetryNavigationGoal();
                    _navigationTimer = 0f;
                    _actionStarted = false;
                }
                return;
            }
            _navigationTimer = 0f;

            if (!_actionStarted)
            {
                _actionTimer = task.Duration;
                _actionStarted = true;
                return;
            }
            _actionTimer = Math.Max(0f, _actionTimer - Math.Max(0f, dt));
            if (_actionTimer > 0f) return;

            // The callback is invoked once per attempt. It owns target validation, the actual edit,
            // and inventory changes; a false result must mean no edit was committed.
            if (executeAction(task)) Advance(agent);
            else Retry();
        }

        public bool TryGetCurrent(out AgentTask task)
        {
            if (!Failed && _index < _tasks.Length)
            {
                task = _tasks[_index];
                return true;
            }
            task = default;
            return false;
        }

        private bool Retry()
        {
            _attempts++;
            _actionStarted = false;
            _actionTimer = 0f;
            if (_attempts < MaxAttempts) return true;
            Failed = true;
            return false;
        }

        private bool TryRecoverStuckNavigation(float dt, MobEntity agent)
        {
            var position = agent.Position;
            if (!_hasNavigationProgressSample)
            {
                _hasNavigationProgressSample = true;
                _lastNavigationPosition = position;
                _stuckNavigationTimer = 0f;
                return false;
            }

            double dx = position.X - _lastNavigationPosition.X;
            double dy = position.Y - _lastNavigationPosition.Y;
            double dz = position.Z - _lastNavigationPosition.Z;
            if (dx * dx + dy * dy + dz * dz >= 0.04)
            {
                _lastNavigationPosition = position;
                _stuckNavigationTimer = 0f;
                return false;
            }

            _stuckNavigationTimer += Math.Max(0f, dt);
            if (_stuckNavigationTimer < 1.25f) return false;
            _stuckNavigationTimer = 0f;
            bool recovered = false;
            if (_navigationRecoveryAttempts < 2 && agent is SteveMob steve)
                recovered = steve.TryRecoverStuckNavigation(_navigationRecoveryAttempts);
            _navigationRecoveryAttempts++;
            if (recovered) agent.RetryNavigationGoal();
            return recovered;
        }

        private void Advance(MobEntity agent)
        {
            _index++;
            _attempts = 0;
            _actionTimer = _navigationTimer = 0f;
            _actionStarted = false;
            _hasLastEmergencyTarget = false;
        }
    }
}
