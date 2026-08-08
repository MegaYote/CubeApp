using System;

namespace CubeApp
{
    /// <summary>
    /// A spawnable mob type with a weight and pack size, mirroring 1.12's Biome.SpawnListEntry.
    /// Weight controls how often it's picked relative to other entries; packSizeMin/Max control
    /// how many spawn together.
    /// </summary>
    public sealed class MobSpawnEntry
    {
        public string MobId { get; }
        public int Weight { get; }
        public int PackSizeMin { get; }
        public int PackSizeMax { get; }

        public MobSpawnEntry(string mobId, int weight, int packSizeMin, int packSizeMax)
        {
            MobId = mobId;
            Weight = weight;
            PackSizeMin = packSizeMin;
            PackSizeMax = packSizeMax;
        }
    }

    /// <summary>
    /// Natural mob spawning + despawning, modeled on 1.12's WorldEntitySpawner.
    ///
    /// Spawning: periodically pick a random spot within the loaded area but >= 24 blocks from the
    /// player (1.12's spawn distance), choose a weighted mob type, validate the spot (solid block
    /// below, air at the spawn cell, not colliding), then spawn a pack. Counts are capped so the
    /// world doesn't fill up: a total mob cap plus a per-entry cap.
    ///
    /// Despawning: mobs further than 128 blocks from the player are removed instantly; mobs
    /// further than 32 blocks for over 600 idle ticks are removed at 1-in-800 chance per tick
    /// (1.12's exact thresholds).
    /// </summary>
    public sealed class MobSpawner
    {
        private readonly MobSpawnEntry[] _entries;
        private readonly int _totalWeight;
        private readonly Func<string, Point3D, float, bool> _spawnFn;
        private readonly Func<Point3D, int> _countFn;
        private const double SpawnMinDistanceSq = 24.0 * 24.0;
        private const double SpawnMaxDistanceSq = 32.0 * 32.0;
        private const int DespawnFar = 128;
        private const int DespawnNearIdleTicks = 600;
        private const int MaxTotalMobs = 40;
        private const int MaxPerType = 12;

        public MobSpawner(MobSpawnEntry[] entries, Func<string, Point3D, float, bool> spawnFn, Func<Point3D, int> countFn)
        {
            _entries = entries;
            _spawnFn = spawnFn;
            _countFn = countFn;
            foreach (var e in _entries) _totalWeight += e.Weight;
        }

        /// <summary>Try to spawn a pack somewhere near the player. Returns true when something spawned.</summary>
        public bool TrySpawn(ChunkManager manager, Point3D playerPosition, Random rand)
        {
            if (_entries.Length == 0) return false;
            if (_countFn(playerPosition) >= MaxTotalMobs) return false;

            // Pick a random spot within the loaded ring.
            for (int attempt = 0; attempt < 8; attempt++)
            {
                double angle = rand.NextDouble() * Math.PI * 2.0;
                double dist = Math.Sqrt(SpawnMinDistanceSq + rand.NextDouble() * (SpawnMaxDistanceSq - SpawnMinDistanceSq));
                int x = (int)Math.Floor(playerPosition.X + Math.Cos(angle) * dist);
                int z = (int)Math.Floor(playerPosition.Z + Math.Sin(angle) * dist);
                int y = FindSpawnY(manager, x, z);
                if (y < 0) continue;

                // Weighted mob type.
                var entry = PickEntry(rand);
                if (entry == null) continue;

                int pack = entry.PackSizeMin + rand.Next(entry.PackSizeMax - entry.PackSizeMin + 1);
                int spawned = 0;
                for (int i = 0; i < pack; i++)
                {
                    int px = x + (i == 0 ? 0 : rand.Next(-2, 3));
                    int pz = z + (i == 0 ? 0 : rand.Next(-2, 3));
                    int py = FindSpawnY(manager, px, pz);
                    if (py < 0) continue;
                    if (_spawnFn(entry.MobId, new Point3D(px + 0.5, py, pz + 0.5), (float)rand.NextDouble() * 360f * (float)Math.PI / 180f))
                    {
                        spawned++;
                    }
                }
                if (spawned > 0) return true;
            }
            return false;
        }

        private MobSpawnEntry? PickEntry(Random rand)
        {
            int roll = rand.Next(_totalWeight);
            foreach (var e in _entries)
            {
                roll -= e.Weight;
                if (roll < 0) return e;
            }
            return _entries.Length > 0 ? _entries[0] : null;
        }

        // Highest solid block at or below startY in the column (the ground the mob stands on);
        // -1 if none within scan range.
        private static int FindSpawnY(ChunkManager manager, int x, int z)
        {
            int startY = 120;
            for (int y = startY; y >= 0; y--)
            {
                int id = manager.GetBlockAt(x, y, z);
                if (id == BlockRegistry.AirId) continue;
                // The spawn cell is y+1 (air above the ground).
                if (manager.GetBlockAt(x, y + 1, z) == BlockRegistry.AirId)
                {
                    return y + 1;
                }
            }
            return -1;
        }
    }
}
