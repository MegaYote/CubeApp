using System;
using System.Linq;

namespace Cubuild
{
    /// <summary>
    /// Saplings grow into a random tree type. Each sapling is scheduled for a future
    /// tick; on that tick it has a chance to grow if light/space/ground are suitable.
    /// The tree type is random among all existing types each time, so replanting can
    /// eventually yield any variety.
    /// </summary>
    public sealed class SaplingGrowthSimulation
    {
        // Saplings grow fast: 5 seconds no matter what, as requested
        private const int MinDelay = 90;
        private const int MaxDelay = 110;

        private readonly ChunkManager _manager;
        private readonly BlockTickScheduler _tickScheduler;
        private readonly Random _rand = new();
        private readonly System.Collections.Generic.Dictionary<(int x, int y, int z), (int startTick, int attempts, long startMs)> _growthData = new();
        private readonly int _saplingId;
        private readonly int _grassId;
        private readonly int _dirtId;
        private readonly int _woodId;
        private readonly int _leavesId;
        private readonly int _redClayId;
        private readonly int _sandId;

        // All tree types we can grow into — matches TerrainChunkProvider's switch.
        private static readonly string[] TreeTypes = new[]
        {
            "oak", "pine", "round", "tall", "dead", "willow", "gnarled"
            // cypress/palm are stubbed/disabled, so omit for now; add back if desired
        };

        public SaplingGrowthSimulation(ChunkManager manager, BlockTickScheduler tickScheduler)
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
            _tickScheduler = tickScheduler ?? throw new ArgumentNullException(nameof(tickScheduler));
            _saplingId = BlockRegistry.GetId("sapling");
            _grassId = BlockRegistry.GetId("grass");
            _dirtId = BlockRegistry.GetId("dirt");
            _woodId = BlockRegistry.GetId("log");
            _leavesId = BlockRegistry.GetId("leaves");
            _redClayId = BlockRegistry.GetId("redclay");
            _sandId = BlockRegistry.GetId("sand");
        }

        public void OnBlockChanged(int x, int y, int z)
        {
            // Wake saplings in a small neighbourhood, like grass does.
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                int nx = x + dx, ny = y + dy, nz = z + dz;
                if (_manager.TryGetLoadedBlock(nx, ny, nz, out var id) && id == _saplingId)
                {
                    Schedule(nx, ny, nz);
                }
            }
            // Also check the changed block itself if it's a sapling placement
            if (_manager.TryGetLoadedBlock(x, y, z, out var changedId) && changedId == _saplingId)
            {
                Schedule(x, y, z);
            }
        }

        private void Schedule(int x, int y, int z)
        {
            int delay = _rand.Next(MinDelay, MaxDelay + 1);
            _tickScheduler.Schedule(x, y, z, delay);
            var key = (x, y, z);
            if (!_growthData.ContainsKey(key))
            {
                _growthData[key] = (_tickScheduler.CurrentTick, 0, Environment.TickCount64);
            }
        }

        public string? GetGrowthInfo(int x, int y, int z)
        {
            var key = (x, y, z);
            if (!_growthData.TryGetValue(key, out var data)) return null;
            if (!_manager.TryGetLoadedBlock(x, y, z, out var id) || id != _saplingId) return null;

            int elapsedTicks = _tickScheduler.CurrentTick - data.startTick;
            double elapsedSec = elapsedTicks / 20.0; // 20 TPS
            int attempts = data.attempts;
            long elapsedMs = Environment.TickCount64 - data.startMs;
            double elapsedSecMs = elapsedMs / 1000.0;

            // 5-second force grow — progress is just elapsed / 5s
            double targetSec = 5.0;
            double progress = Math.Min(1.0, elapsedSec / targetSec);
            string status;
            if (progress < 0.3) status = "just planted";
            else if (progress < 0.6) status = "growing";
            else if (progress < 1.0) status = "almost ready";
            else status = "sprouting...";

            return $"Sapling: {elapsedSecMs:0.0}s ({elapsedSec:0.0}t) {status} {progress*100:0}% [{attempts} tries]";
        }

        public void TickBlock(int x, int y, int z)
        {
            var key = (x, y, z);
            if (!_manager.TryGetLoadedBlock(x, y, z, out var id) || id != _saplingId)
            {
                _growthData.Remove(key);
                return;
            }
            // Update attempts count
            if (_growthData.TryGetValue(key, out var g))
            {
                _growthData[key] = (g.startTick, g.attempts + 1, g.startMs);
            }

            // 5-second force grow: ignore light/ground/space, just grow no matter what as requested

            // Always try to grow - no more 1-in-3 coin flip that made it feel stuck at 100%
            // Try all tree types in random order until one fits — so a tight spot can still grow a small oak instead of failing on a big tall pick
            // For 5-second force grow, if nothing fits, just force a tiny oak anyway
            bool grew = false;
            foreach (var treeType in TreeTypes.OrderBy(_ => _rand.Next()))
            {
                if (TryGrowTree(x, y, z, treeType))
                {
                    grew = true;
                    break;
                }
            }
            if (!grew)
            {
                // Force a tiny oak even if blocked — no matter what means no matter what
                TryForceTinyOak(x, y, z);
                grew = true;
            }
            if (!grew)
            {
                Schedule(x, y, z);
            }
            else
            {
                _growthData.Remove(key);
            }
            // If it grew, the sapling block is already replaced by the tree's trunk/leaves
            // and no further scheduling is needed.
        }

        private bool IsSkyLit(int x, int y, int z)
        {
            // Check that the block above is not opaque (like grass does)
            return _manager.TryGetLoadedBlock(x, y + 1, z, out var above) && !BlockRegistry.IsOpaque(above);
        }

        private bool HasSpaceForAnyTree(int x, int y, int z)
        {
            // Quick check: is there at least a 3x3x6 column of air/leaves above the sapling for the smallest tree?
            // Start at y+1 so the sapling's own spot doesn't count as blocked
            // Unloaded chunks are treated as air for this check — the tree can still sprout in loaded area
            for (int yy = y + 1; yy <= y + 6; yy++)
            {
                int radius = 1;
                // At the very top (y+6) need a little more room for canopy, check 2 radius there
                if (yy == y + 6) radius = 2;
                for (int dx = -radius; dx <= radius; dx++)
                for (int dz = -radius; dz <= radius; dz++)
                {
                    int wx = x + dx, wz = z + dz;
                    if (!_manager.TryGetLoadedBlock(wx, yy, wz, out var b)) continue; // chunk not loaded -> treat as air
                    if (b != 0 && b != _leavesId) return false;
                }
            }
            return true;
        }

        private bool CanGrowAnyTree(int x, int y, int z)
        {
            // Actually test each tree type's full space check (without placing) — so F3 doesn't lie and say "ready" when every type would still be blocked
            // We do a dry-run of the same checks TryGrowTree does, but just the space part
            foreach (var t in TreeTypes)
            {
                if (CanGrowTreeType(x, y, z, t)) return true;
            }
            return false;
        }

        private bool CanGrowTreeType(int x, int y, int z, string treeType)
        {
            // Minimal space check per type — mirrors the start of each TryGenerate* without the ground check (ground already checked in Tick)
            int trunkHeight, topY, radiusAtTop;
            switch (treeType)
            {
                case "pine": trunkHeight = 8; topY = y + trunkHeight; radiusAtTop = 3; break;
                case "round": trunkHeight = 4; topY = y + trunkHeight; radiusAtTop = 2; break;
                case "tall": trunkHeight = 8; topY = y + trunkHeight; radiusAtTop = 3; break;
                case "dead": trunkHeight = 3; topY = y + trunkHeight; radiusAtTop = 1; break;
                case "willow": trunkHeight = 4; topY = y + trunkHeight; radiusAtTop = 3; break;
                case "gnarled": trunkHeight = 6; topY = y + trunkHeight; radiusAtTop = 3; break;
                default: trunkHeight = 4; topY = y + trunkHeight; radiusAtTop = 2; break; // oak
            }
            for (int yy = y + 1; yy <= topY + 1; yy++)
            {
                int radius = yy == y + 1 ? 0 : yy >= topY ? radiusAtTop : 1;
                if (treeType == "tall" && yy >= topY - 1) radius = 3;
                if (treeType == "pine" && yy < y + 3) radius = 0;
                for (int dx = -radius; dx <= radius; dx++)
                for (int dz = -radius; dz <= radius; dz++)
                {
                    int wx = x + dx, wz = z + dz;
                    if (!_manager.TryGetLoadedBlock(wx, yy, wz, out var b)) continue;
                    if (b != 0 && b != _leavesId) return false;
                }
            }
            return true;
        }

        private bool TryGrowTree(int x, int y, int z, string treeType)
        {
            // Use a temporary Random seeded by world position + world seed for variety,
            // but also mix in a fresh random for true randomness each time.
            var rand = new Random(_rand.Next() ^ x * 73856093 ^ y * 19349663 ^ z * 83492791);

            // Remove the sapling first (so the trunk can occupy its space)
            // We'll place the tree with its base at y (sapling's y)
            // The generators expect baseY to be the ground level +1, which is exactly y (sapling is at y, ground at y-1)
            // For simplicity, we directly place blocks via ChunkManager.

            // Choose generator based on type
            bool success = treeType switch
            {
                "pine" => TryGeneratePine(x, y, z, rand),
                "round" => TryGenerateRound(x, y, z, rand),
                "tall" => TryGenerateTall(x, y, z, rand),
                "dead" => TryGenerateDead(x, y, z, rand),
                "willow" => TryGenerateWillow(x, y, z, rand),
                "gnarled" => TryGenerateGnarled(x, y, z, rand),
                _ => TryGenerateOak(x, y, z, rand), // oak default
            };

            return success;
        }

        // Helpers that place blocks via ChunkManager, checking for space and ground.
        // Each returns true if it placed something, false if blocked.

        private bool TryGenerateOak(int x, int y, int z, Random rand)
        {
            // Small oak: trunk 4-6, canopy at top
            int trunkHeight = rand.Next(3) + 4; // 4..6
            int topY = y + trunkHeight;

            // Check space
            for (int yy = y; yy <= topY + 1; yy++)
            {
                int radius = (yy == y) ? 0 : (yy >= topY ? 2 : 1);
                for (int dx = -radius; dx <= radius; dx++)
                for (int dz = -radius; dz <= radius; dz++)
                {
                    int wx = x + dx, wz = z + dz;
                    if (!_manager.TryGetLoadedBlock(wx, yy, wz, out var b)) continue; // chunk not loaded -> treat as air
                    if (b != 0 && b != _leavesId) return false; // blocked
                }
            }

            // Check ground
            if (!_manager.TryGetLoadedBlock(x, y - 1, z, out var ground) || !BlockRegistry.IsSolid(ground)) return false;

            // Place trunk
            for (int i = 0; i < trunkHeight; i++)
            {
                int yy = y + i;
                _manager.TrySetBlockLoadedOnly(x, yy, z, _woodId, 0);
            }

            // Simple canopy: 3x3 at top, plus a few extra leaves
            for (int dx = -2; dx <= 2; dx++)
            for (int dz = -2; dz <= 2; dz++)
            {
                if (Math.Abs(dx) == 2 && Math.Abs(dz) == 2 && rand.Next(3) == 0) continue;
                int wx = x + dx, wz = z + dz, wy = topY;
                if (_manager.TryGetLoadedBlock(wx, wy, wz, out var b) && (b == 0 || b == _leavesId))
                    _manager.TrySetBlockLoadedOnly(wx, wy, wz, _leavesId, 0);
                if (topY + 1 < ChunkManager.ChunkHeight)
                {
                    wy = topY + 1;
                    if (Math.Abs(dx) <= 1 && Math.Abs(dz) <= 1)
                    {
                        if (_manager.TryGetLoadedBlock(wx, wy, wz, out b) && (b == 0 || b == _leavesId))
                            _manager.TrySetBlockLoadedOnly(wx, wy, wz, _leavesId, 0);
                    }
                }
            }

            // Remove sapling is already overwritten by trunk at y, but ensure ground is dirt if it was grass
            if (ground == _grassId) _manager.TrySetBlockLoadedOnly(x, y - 1, z, _dirtId, 0);
            return true;
        }

        private bool TryGenerateRound(int x, int y, int z, Random rand)
        {
            int trunkHeight = rand.Next(3) + 4;
            int topY = y + trunkHeight;
            // Check space 5x5 at top
            for (int yy = y; yy <= topY + 1; yy++)
            {
                int radius = yy == y ? 0 : 2;
                for (int dx = -radius; dx <= radius; dx++)
                for (int dz = -radius; dz <= radius; dz++)
                {
                    int wx = x + dx, wz = z + dz;
                    if (!_manager.TryGetLoadedBlock(wx, yy, wz, out var b)) continue; // chunk not loaded -> treat as air
                    if (b != 0 && b != _leavesId) return false;
                }
            }
            if (!_manager.TryGetLoadedBlock(x, y - 1, z, out var ground) || !BlockRegistry.IsSolid(ground)) return false;

            for (int i = 0; i < trunkHeight; i++) _manager.TrySetBlockLoadedOnly(x, y + i, z, _woodId, 0);

            for (int i = 0; i < 3; i++)
            {
                int yy = topY - 1 + i;
                int radius = i < 2 ? 2 : 1;
                for (int dx = -radius; dx <= radius; dx++)
                for (int dz = -radius; dz <= radius; dz++)
                {
                    bool isCorner = Math.Abs(dx) == radius && Math.Abs(dz) == radius;
                    if (isCorner && i != 2 && (i == 1 || rand.Next(3) != 0)) continue;
                    int wx = x + dx, wz = z + dz;
                    if (_manager.TryGetLoadedBlock(wx, yy, wz, out var b) && (b == 0 || b == _leavesId))
                        _manager.TrySetBlockLoadedOnly(wx, yy, wz, _leavesId, 0);
                }
            }
            if (ground == _grassId) _manager.TrySetBlockLoadedOnly(x, y - 1, z, _dirtId, 0);
            return true;
        }

        private bool TryGenerateTall(int x, int y, int z, Random rand)
        {
            int trunkHeight = rand.Next(5) + 8; // 8..12
            int topY = y + trunkHeight;
            for (int yy = y; yy <= topY + 1; yy++)
            {
                int radius = yy == y ? 0 : yy >= topY - 1 ? 3 : 1;
                for (int dx = -radius; dx <= radius; dx++)
                for (int dz = -radius; dz <= radius; dz++)
                {
                    int wx = x + dx, wz = z + dz;
                    if (!_manager.TryGetLoadedBlock(wx, yy, wz, out var b)) continue; // chunk not loaded -> treat as air
                    if (b != 0 && b != _leavesId) return false;
                }
            }
            if (!_manager.TryGetLoadedBlock(x, y - 1, z, out var ground) || !BlockRegistry.IsSolid(ground)) return false;

            for (int i = 0; i < trunkHeight; i++) _manager.TrySetBlockLoadedOnly(x, y + i, z, _woodId, 0);

            // Big ragged crown
            for (int yy = topY - 2; yy <= topY + 1; yy++)
            {
                int radius = yy == topY + 1 ? 1 : 2;
                if (yy == topY - 2) radius = 1;
                for (int dx = -radius; dx <= radius; dx++)
                for (int dz = -radius; dz <= radius; dz++)
                {
                    if (Math.Abs(dx) == radius && Math.Abs(dz) == radius && rand.Next(2) == 0) continue;
                    int wx = x + dx, wz = z + dz;
                    if (_manager.TryGetLoadedBlock(wx, yy, wz, out var b) && (b == 0 || b == _leavesId))
                        _manager.TrySetBlockLoadedOnly(wx, yy, wz, _leavesId, 0);
                }
            }
            if (ground == _grassId) _manager.TrySetBlockLoadedOnly(x, y - 1, z, _dirtId, 0);
            return true;
        }

        private bool TryGenerateDead(int x, int y, int z, Random rand)
        {
            int trunkHeight = rand.Next(3) + 3; // 3..5
            for (int yy = y; yy < y + trunkHeight; yy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    int wx = x + dx, wz = z + dz;
                    if (!_manager.TryGetLoadedBlock(wx, yy, wz, out var b)) continue; // chunk not loaded -> treat as air
                    if (b != 0 && b != _leavesId) return false;
                }
            }
            if (!_manager.TryGetLoadedBlock(x, y - 1, z, out var ground) || !BlockRegistry.IsSolid(ground)) return false;

            for (int i = 0; i < trunkHeight; i++) _manager.TrySetBlockLoadedOnly(x, y + i, z, _woodId, 0);
            // Dead tree has no leaves or very sparse
            if (rand.Next(2) == 0)
            {
                int wy = y + trunkHeight;
                _manager.TrySetBlockLoadedOnly(x, wy, z, _leavesId, 0);
            }
            if (ground == _grassId) _manager.TrySetBlockLoadedOnly(x, y - 1, z, _dirtId, 0);
            return true;
        }

        private bool TryGenerateWillow(int x, int y, int z, Random rand)
        {
            int trunkHeight = rand.Next(3) + 4; // 4..6
            int topY = y + trunkHeight;
            for (int yy = y; yy <= topY + 1; yy++)
            {
                int radius = yy == y ? 0 : 2;
                for (int dx = -radius; dx <= radius; dx++)
                for (int dz = -radius; dz <= radius; dz++)
                {
                    int wx = x + dx, wz = z + dz;
                    if (!_manager.TryGetLoadedBlock(wx, yy, wz, out var b)) continue; // chunk not loaded -> treat as air
                    if (b != 0 && b != _leavesId) return false;
                }
            }
            if (!_manager.TryGetLoadedBlock(x, y - 1, z, out var ground) || !BlockRegistry.IsSolid(ground)) return false;

            for (int i = 0; i < trunkHeight; i++) _manager.TrySetBlockLoadedOnly(x, y + i, z, _woodId, 0);

            // Willow has a wide flat canopy that droops
            for (int dx = -3; dx <= 3; dx++)
            for (int dz = -3; dz <= 3; dz++)
            {
                if (Math.Abs(dx) == 3 && Math.Abs(dz) == 3) continue;
                double dist = Math.Sqrt(dx*dx + dz*dz);
                if (dist > 2.5 && rand.Next(2) == 0) continue;
                int wx = x + dx, wz = z + dz, wy = topY;
                if (_manager.TryGetLoadedBlock(wx, wy, wz, out var b) && (b == 0 || b == _leavesId))
                    _manager.TrySetBlockLoadedOnly(wx, wy, wz, _leavesId, 0);
                // Drooping leaves below
                if (rand.Next(3) == 0)
                {
                    wy = topY - 1;
                    if (_manager.TryGetLoadedBlock(wx, wy, wz, out b) && (b == 0 || b == _leavesId))
                        _manager.TrySetBlockLoadedOnly(wx, wy, wz, _leavesId, 0);
                }
            }
            if (ground == _grassId) _manager.TrySetBlockLoadedOnly(x, y - 1, z, _dirtId, 0);
            return true;
        }

        private bool TryGenerateGnarled(int x, int y, int z, Random rand)
        {
            int trunkHeight = rand.Next(4) + 6; // 6..9
            int topY = y + trunkHeight;
            for (int yy = y; yy <= topY + 2; yy++)
            {
                int radius = yy <= y ? 0 : yy >= topY ? 3 : 2;
                for (int dx = -radius; dx <= radius; dx++)
                for (int dz = -radius; dz <= radius; dz++)
                {
                    int wx = x + dx, wz = z + dz;
                    if (!_manager.TryGetLoadedBlock(wx, yy, wz, out var b)) continue; // chunk not loaded -> treat as air
                    if (b != 0 && b != _leavesId) return false;
                }
            }
            if (!_manager.TryGetLoadedBlock(x, y - 1, z, out var ground) || !BlockRegistry.IsSolid(ground)) return false;

            for (int i = 0; i < trunkHeight; i++) _manager.TrySetBlockLoadedOnly(x, y + i, z, _woodId, 0);

            // Wide dome
            for (int dx = -3; dx <= 3; dx++)
            for (int dz = -3; dz <= 3; dz++)
            {
                double dist = Math.Sqrt(dx*dx + dz*dz);
                if (dist > 3.0) continue;
                if (dist > 2.0 && rand.Next(2) == 0) continue;
                int wx = x + dx, wz = z + dz, wy = topY;
                if (_manager.TryGetLoadedBlock(wx, wy, wz, out var b) && (b == 0 || b == _leavesId))
                    _manager.TrySetBlockLoadedOnly(wx, wy, wz, _leavesId, 0);
                wy = topY + 1;
                if (Math.Abs(dx) <= 1 && Math.Abs(dz) <= 1)
                {
                    if (_manager.TryGetLoadedBlock(wx, wy, wz, out b) && (b == 0 || b == _leavesId))
                        _manager.TrySetBlockLoadedOnly(wx, wy, wz, _leavesId, 0);
                }
            }
            if (ground == _grassId) _manager.TrySetBlockLoadedOnly(x, y - 1, z, _dirtId, 0);
            return true;
        }

        private bool TryForceTinyOak(int x, int y, int z)
        {
            // Emergency 5-second oak that just *goes* — no space checks, just plops wood where it can
            for (int i = 0; i < 4; i++)
            {
                _manager.TrySetBlockLoadedOnly(x, y + i, z, _woodId, 0);
            }
            int topY = y + 4;
            for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
            {
                if (Math.Abs(dx) == 1 && Math.Abs(dz) == 1) continue;
                _manager.TrySetBlockLoadedOnly(x + dx, topY, z + dz, _leavesId, 0);
            }
            _manager.TrySetBlockLoadedOnly(x, topY + 1, z, _leavesId, 0);
            return true;
        }

        private bool TryGeneratePine(int x, int y, int z, Random rand)
        {
            int trunkHeight = rand.Next(5) + 8; // 8..12
            int topY = y + trunkHeight;
            // Check space: narrow trunk, wide at top
            for (int yy = y; yy <= topY; yy++)
            {
                int radius = yy < y + 3 ? 0 : yy >= topY - 2 ? 3 : 1;
                for (int dx = -radius; dx <= radius; dx++)
                for (int dz = -radius; dz <= radius; dz++)
                {
                    int wx = x + dx, wz = z + dz;
                    if (!_manager.TryGetLoadedBlock(wx, yy, wz, out var b)) continue; // chunk not loaded -> treat as air
                    if (b != 0 && b != _leavesId) return false;
                }
            }
            if (!_manager.TryGetLoadedBlock(x, y - 1, z, out var ground) || !BlockRegistry.IsSolid(ground)) return false;

            for (int i = 0; i < trunkHeight; i++) _manager.TrySetBlockLoadedOnly(x, y + i, z, _woodId, 0);

            // Conical layers
            for (int yy = y + 3; yy < topY; yy++)
            {
                int layer = yy - (y + 3);
                int radius = Math.Max(1, 3 - layer / 2);
                if (yy >= topY - 1) radius = 1;
                for (int dx = -radius; dx <= radius; dx++)
                for (int dz = -radius; dz <= radius; dz++)
                {
                    if (Math.Abs(dx) == radius && Math.Abs(dz) == radius) continue;
                    int wx = x + dx, wz = z + dz;
                    if (_manager.TryGetLoadedBlock(wx, yy, wz, out var b) && (b == 0 || b == _leavesId))
                        _manager.TrySetBlockLoadedOnly(wx, yy, wz, _leavesId, 0);
                }
            }
            // Top
            _manager.TrySetBlockLoadedOnly(x, topY, z, _leavesId, 0);
            if (ground == _grassId) _manager.TrySetBlockLoadedOnly(x, y - 1, z, _dirtId, 0);
            return true;
        }
    }
}
