using System;

namespace Cubuild
{
    /// <summary>
    /// A binary min-heap of PathPoints ordered by their A* f-score (TotalPathDistance +
    /// DistanceToTarget).
    /// </summary>
    public sealed class PathHeap
    {
        private PathPoint[] _points = new PathPoint[128];
        private int _count;

        public int Count => _count;
        public bool IsEmpty => _count == 0;

        public void ClearPath()
        {
            _count = 0;
        }

        public void AddPoint(PathPoint point)
        {
            if (_count == _points.Length)
            {
                Array.Resize(ref _points, _points.Length * 2);
            }

            _points[_count] = point;
            point.Assigned = true;
            int idx = _count;
            _count++;

            while (idx > 0)
            {
                int parent = (idx - 1) / 2;
                if (Compare(_points[parent], _points[idx]) <= 0) break;
                Swap(parent, idx);
                idx = parent;
            }
        }

        public PathPoint Dequeue()
        {
            if (_count == 0) throw new InvalidOperationException("Empty heap");

            var result = _points[0];
            result.Assigned = false;
            _count--;
            if (_count > 0)
            {
                _points[0] = _points[_count];
                int idx = 0;
                while (true)
                {
                    int left = idx * 2 + 1;
                    int right = left + 1;
                    int smallest = idx;
                    if (left < _count && Compare(_points[left], _points[smallest]) < 0) smallest = left;
                    if (right < _count && Compare(_points[right], _points[smallest]) < 0) smallest = right;
                    if (smallest == idx) break;
                    Swap(idx, smallest);
                    idx = smallest;
                }
            }
            return result;
        }

        public void ChangeDistance(PathPoint point, float newDistance)
        {
            point.TotalPathDistance = newDistance;
            // Re-sift the point up from its (unknown) position by scanning (O(n), but paths are small).
            for (int i = 0; i < _count; i++)
            {
                if (!ReferenceEquals(_points[i], point)) continue;
                while (i > 0)
                {
                    int parent = (i - 1) / 2;
                    if (Compare(_points[parent], _points[i]) <= 0) break;
                    Swap(parent, i);
                    i = parent;
                }
                return;
            }
        }

        private static float Compare(PathPoint a, PathPoint b)
        {
            float fa = a.TotalPathDistance + a.DistanceToTarget;
            float fb = b.TotalPathDistance + b.DistanceToTarget;
            return fa - fb;
        }

        private void Swap(int a, int b)
        {
            (_points[a], _points[b]) = (_points[b], _points[a]);
        }
    }
}
