using System;

namespace Cubuild
{
    /// <summary>
    /// A single node in a pathfinding grid search: integer cell coords, accumulated path cost,
    /// Manhattan estimate, and the back-pointer chain used to rebuild the final path.
    /// Hashable/equatable by cell so the closed set can be a HashSet.
    /// </summary>
    public sealed class PathPoint
    {
        public readonly int X, Y, Z;
        public float DistanceFromOrigin;
        public float TotalPathDistance;
        public float DistanceToNext;
        public float DistanceToTarget;
        public PathPoint? Previous;
        public bool Visited;
        public bool Assigned;
        /// <summary>Terrain preference penalty (e.g. avoid water/lava).</summary>
        public float CostMalus;
        /// <summary>Step cost of moving onto this node (Manhattan distance + CostMalus).</summary>
        public float Cost;

        public PathPoint(int x, int y, int z)
        {
            X = x; Y = y; Z = z;
        }

        public float DistanceManhattan(PathPoint other)
            => Math.Abs(other.X - X) + Math.Abs(other.Y - Y) + Math.Abs(other.Z - Z);

        public float DistanceSquared(PathPoint other)
        {
            float dx = other.X - X, dy = other.Y - Y, dz = other.Z - Z;
            return dx * dx + dy * dy + dz * dz;
        }

        public override bool Equals(object? obj) => obj is PathPoint p && p.X == X && p.Y == Y && p.Z == Z;
        public override int GetHashCode() => HashCode.Combine(X, Y, Z);
    }

    /// <summary>
    /// An ordered list of waypoints for an entity to follow. The entity advances along it as it
    /// reaches each point.
    /// </summary>
    public sealed class PathEntity
    {
        private readonly PathPoint[] _points;
        private int _index;

        public PathEntity(PathPoint[] points)
        {
            _points = points;
            _index = 0;
        }

        public int Length => _points.Length;
        public int CurrentIndex => _index;
        public bool IsDone => _index >= _points.Length;

        /// <summary>The next waypoint to steer toward; null when done.</summary>
        public PathPoint? GetNext()
        {
            return _index < _points.Length ? _points[_index] : null;
        }

        /// <summary>Advance to the next waypoint (call when the current one is reached).</summary>
        public void Advance()
        {
            _index++;
        }

        public void SkipToClosest(PathPoint reference)
        {
            float best = float.MaxValue;
            int bestIdx = _index;
            for (int i = _index; i < _points.Length; i++)
            {
                float d = _points[i].DistanceSquared(reference);
                if (d < best)
                {
                    best = d;
                    bestIdx = i;
                }
            }
            _index = bestIdx;
        }

        public PathPoint GetFinal()
        {
            return _points.Length > 0 ? _points[_points.Length - 1] : _points[0];
        }

        public PathPoint[] GetPoints() => _points;
    }
}
