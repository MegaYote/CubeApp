using System;

namespace Cubuild
{
    public sealed partial class SteveMob
    {
        private const int WaterShoreSearchRadius = 10;
        private const float WaterShoreRescanInterval = 0.8f;
        private float _waterShoreRescanTimer;
        private bool _waterEscapeActive;
        private bool _hasWaterShoreTarget;
        private int _waterShoreX, _waterShoreY, _waterShoreZ;

        protected override bool UpdateWaterNavigation(float dt, ChunkManager manager)
        {
            bool bodySubmerged = IsBodySubmerged(manager);
            bool feetInWater = AreFeetInWater(manager);
            // Shallow water over firm ground is wading, not swimming. Let the ordinary grounded
            // route/jump logic take over so Steve can step or jump up the bank.
            if (feetInWater && !bodySubmerged && IsWadingOnSafeGround(manager))
            {
                if (_waterEscapeActive)
                {
                    _waterEscapeActive = false;
                    _hasWaterShoreTarget = false;
                    RetryNavigationGoal();
                }
                _waterShoreRescanTimer = 0f;
                return false;
            }

            bool inWater = bodySubmerged || feetInWater;
            if (!inWater)
            {
                if (_waterEscapeActive)
                {
                    _waterEscapeActive = false;
                    _hasWaterShoreTarget = false;
                    RetryNavigationGoal();
                }
                _waterShoreRescanTimer = 0f;
                return false;
            }

            if (!_waterEscapeActive)
            {
                _waterEscapeActive = true;
                _hasWaterShoreTarget = false;
                _waterShoreRescanTimer = 0f;
                // Discard any grounded-only route once; preserve the goal and retry it after
                // Steve reaches land.
                RetryNavigationGoal();
            }

            _waterShoreRescanTimer -= dt;
            if (_waterShoreRescanTimer <= 0f || !WaterShoreTargetStillSafe(manager))
            {
                _waterShoreRescanTimer = WaterShoreRescanInterval;
                _hasWaterShoreTarget = TryFindLocalShore(manager,
                    out _waterShoreX, out _waterShoreY, out _waterShoreZ);
            }

            if (_hasWaterShoreTarget)
            {
                double dx = _waterShoreX + 0.5 - Position.X;
                double dz = _waterShoreZ + 0.5 - Position.Z;
                double distanceSq = dx * dx + dz * dz;
                SetWaterNavigationIntent(_waterShoreX + 0.5, _waterShoreZ + 0.5,
                    distanceSq < 0.4 * 0.4 ? 0f : 0.85f);
            }
            else
            {
                // If no loaded bank is nearby, keep the rescue non-destructive and let buoyancy
                // plus a real swim-up input carry Steve toward the surface.
                SetWaterNavigationIntent(Position.X, Position.Z, 0f);
            }
            return true;
        }

        internal bool IsWadingOnSafeGround(ChunkManager manager)
        {
            int x = (int)Math.Floor(Position.X);
            int y = (int)Math.Floor(Position.Y);
            int z = (int)Math.Floor(Position.Z);
            return manager.TryGetLoadedBlock(x, y - 1, z, out int floor)
                && IsSafeGroundBlock(floor);
        }

        private bool WaterShoreTargetStillSafe(ChunkManager manager)
            => _hasWaterShoreTarget && IsSafeShoreStand(manager,
                _waterShoreX, _waterShoreY, _waterShoreZ);

        private bool TryFindLocalShore(ChunkManager manager, out int bestX, out int bestY, out int bestZ)
        {
            bestX = bestY = bestZ = 0;
            double bestScore = double.PositiveInfinity;
            int centerX = (int)Math.Floor(Position.X);
            int centerY = (int)Math.Floor(Position.Y);
            int centerZ = (int)Math.Floor(Position.Z);
            for (int dx = -WaterShoreSearchRadius; dx <= WaterShoreSearchRadius; dx++)
            {
                for (int dz = -WaterShoreSearchRadius; dz <= WaterShoreSearchRadius; dz++)
                {
                    int horizontalSq = dx * dx + dz * dz;
                    if (horizontalSq == 0 || horizontalSq > WaterShoreSearchRadius * WaterShoreSearchRadius)
                        continue;

                    for (int dy = -2; dy <= 5; dy++)
                    {
                        int x = centerX + dx, y = centerY + dy, z = centerZ + dz;
                        if (!IsSafeShoreStand(manager, x, y, z)) continue;
                        double vertical = y + 0.5 - Position.Y;
                        double score = horizontalSq + vertical * vertical * 1.5;
                        if (score >= bestScore) continue;
                        bestScore = score;
                        bestX = x; bestY = y; bestZ = z;
                    }
                }
            }
            return double.IsFinite(bestScore);
        }

        private static bool IsSafeShoreStand(ChunkManager manager, int x, int y, int z)
        {
            if (!manager.TryGetLoadedBlock(x, y - 1, z, out int floor)
                || !BlockRegistry.IsSolid(floor)) return false;
            return manager.TryGetLoadedBlock(x, y, z, out int feet)
                && feet == BlockRegistry.AirId
                && manager.TryGetLoadedBlock(x, y + 1, z, out int head)
                && head == BlockRegistry.AirId;
        }
    }
}
