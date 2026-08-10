using System;

namespace CubeApp
{
    /// <summary>
    /// A spawnable mob type with a weight and pack size. Weight controls how often it's picked
    /// relative to other entries; packSizeMin/Max control how many spawn together.
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
    /// Natural mob spawning for a single weight table of entries.
    ///
    /// The daytime/passive path:
    ///  - Only spawn while total mobs are under the cap (20).
    ///  - Sample a loaded ground chunk near the player, then jitter a few cells around a point.
    ///  - A cell is valid when the block below is a solid cube, the cell and the one above are
    ///    air/non-liquid, and the spot is at least 32 blocks from the player.
    ///  - Spawn up to a pack of 3 per pass if the target has a clear, non-liquid footprint.
    ///
    /// The night/monster path (zombies) adds:
    ///  - Y is not uniform - it uses a triple-nested random roll biased heavily toward the bottom
    ///    of the world, so monsters mostly emerge from caves and shafts rather than the surface.
    ///  - The base spawn cell must be air (a solid cube immediately fails the pass; other
    ///    non-air blocks like water also fail).
    ///  - Monsters only spawn where block light is dark (light <= rand(8)), so caves always work
    ///    and the surface only produces them at night.
    ///  - The monster cap is 100 (the passive cap is 20).
    ///
    /// Coyotes and Steves use the passive weighted-table path with per-type caps.
    /// </summary>
    public sealed class MobSpawner
    {
        private readonly MobSpawnEntry[] _entries;
        private readonly int _totalWeight;
        private readonly Func<string, Point3D, float, bool> _spawnFn;
        private readonly Func<int> _totalCountFn;
        private readonly Func<string, int> _typeCountFn;
        private readonly bool _monsterSpawner;
        private const double SpawnMinDistanceSq = 32.0 * 32.0;
        private const int MaxTotalMobs = 20;
        private const int MaxTotalMonsters = 100;
        private const int MaxPerType = 12;
        private const int MaxPackMembers = 3;
        private const int SpawnPasses = 10;

        public MobSpawner(MobSpawnEntry[] entries,
            Func<string, Point3D, float, bool> spawnFn,
            Func<int> totalCountFn,
            Func<string, int> typeCountFn,
            bool monsterSpawner = false)
        {
            _entries = entries;
            _spawnFn = spawnFn;
            _totalCountFn = totalCountFn;
            _typeCountFn = typeCountFn;
            _monsterSpawner = monsterSpawner;
            foreach (var e in _entries) _totalWeight += e.Weight;
        }

        /// <summary>Try to spawn a pack somewhere near the player. Returns true when something spawned.</summary>
        public bool TrySpawn(ChunkManager manager, Point3D playerPosition, Random rand, Func<int,int,int,int>? getLight = null)
        {
            if (_entries.Length == 0) return false;
            if (_totalCountFn() >= (_monsterSpawner ? MaxTotalMonsters : MaxTotalMobs)) return false;

            var entry = PickEntry(rand);
            if (entry == null) return false;
            if (_typeCountFn(entry.MobId) >= MaxPerType) return false;

            int pack = entry.PackSizeMin + rand.Next(entry.PackSizeMax - entry.PackSizeMin + 1);
            pack = Math.Min(pack, MaxPackMembers);
            int room = MaxPerType - _typeCountFn(entry.MobId);
            pack = Math.Min(pack, Math.Max(0, room));

            // Try several passes per update so an empty area fills quickly.
            int spawned = 0;
            for (int pass = 0; pass < SpawnPasses && spawned < pack; pass++)
            {
                // Pick a base point from a LOADED ground-layer chunk near the player. The old
                // +-128 search worked because the whole area was loaded; our world streams chunks,
                // so we sample a loaded chunk and jitter within it. This keeps the surface jitter +
                // validation while guaranteeing the terrain actually exists.
                if (!TryPickLoadedChunk(manager, playerPosition, rand, out int chunkX, out int chunkZ))
                    continue;

                int px = chunkX * 16 + rand.Next(16);
                int pz = chunkZ * 16 + rand.Next(16);

                int py;
                if (_monsterSpawner)
                {
                    // Triple-nested random biases Y toward the world bottom so monsters rise from
                    // caves and depths rather than clustering on the surface.
                    py = rand.Next(rand.Next(rand.Next(112) + 8) + 8);

                    // The base spawn cell must be AIR - a solid cube fails the pass outright and a
                    // non-air non-cube (water) also fails.
                    int baseBlock = manager.GetBlockAt(px, py, pz);
                    if (baseBlock == BlockRegistry.AirId) { /* ok */ }
                    else if (BlockRegistry.IsSolid(baseBlock) && BlockRegistry.IsOpaque(baseBlock)) continue;
                    else continue;
                }
                else
                {
                    py = SurfaceY(manager, px, pz);
                    if (py < 0) continue;
                }

                // Jitter a few cells from the base point.
                for (int attempt = 0; attempt < 3 && spawned < pack; attempt++)
                {
                    int x = px + rand.Next(6) - rand.Next(6);
                    int y = py + rand.Next(1) - rand.Next(1);
                    int z = pz + rand.Next(6) - rand.Next(6);

                    // Valid: solid cube below, air/non-liquid at the cell and above, >= 32 from player.
                    if (!IsNormalCube(manager, x, y - 1, z)) continue;
                    if (manager.GetBlockAt(x, y, z) != BlockRegistry.AirId) continue;
                    if (manager.GetBlockAt(x, y + 1, z) != BlockRegistry.AirId) continue;

                    // Night path only: darkness gate (block light <= rand(8)).
                    if (_monsterSpawner)
                    {
                        int light = getLight != null ? getLight(x, y, z) : 15;
                        if (light > rand.Next(8)) continue;
                    }

                    double dx = (x + 0.5) - playerPosition.X;
                    double dy = (y + 1.0) - playerPosition.Y;
                    double dz = (z + 0.5) - playerPosition.Z;
                    if (dx * dx + dy * dy + dz * dz < 1024.0) continue;

                    if (_spawnFn(entry.MobId, new Point3D(x + 0.5, y + 1.0, z + 0.5),
                        (float)(rand.NextDouble() * Math.PI * 2.0)))
                    {
                        spawned++;
                    }
                }
            }
            return spawned > 0;
        }

        // Picks a loaded ground-layer chunk at least 2 chunks from the player (so the 32-block
        // spawn-distance gate has a chance), and within a few chunks so mobs appear near the world.
        private static bool TryPickLoadedChunk(ChunkManager manager, Point3D playerPosition, Random rand,
            out int chunkX, out int chunkZ)
        {
            chunkX = 0; chunkZ = 0;
            int pcx = (int)Math.Floor(playerPosition.X / 16.0);
            int pcz = (int)Math.Floor(playerPosition.Z / 16.0);

            for (int attempt = 0; attempt < 24; attempt++)
            {
                int radius = 2 + rand.Next(6); // 2..7 chunks out (32..112 blocks)
                double ang = rand.NextDouble() * Math.PI * 2.0;
                int cx = pcx + (int)Math.Round(Math.Cos(ang) * radius);
                int cz = pcz + (int)Math.Round(Math.Sin(ang) * radius);
                if (manager.TryGetLoadedChunk(new ChunkCoordinates(ChunkManager.GroundLayer, cx, cz), out _))
                {
                    chunkX = cx; chunkZ = cz;
                    return true;
                }
            }
            return false;
        }

        // Highest solid block in the column (the ground surface the mob would stand on); -1 if none.
        private static int SurfaceY(ChunkManager manager, int x, int z)
        {
            for (int y = 150; y >= 0; y--)
            {
                if (manager.GetBlockAt(x, y, z) != BlockRegistry.AirId) return y;
            }
            return -1;
        }

        // True when the block is a solid, non-transparent cube (grass/dirt/stone/cobble...).
        private static bool IsNormalCube(ChunkManager manager, int x, int y, int z)
        {
            int id = manager.GetBlockAt(x, y, z);
            return id != BlockRegistry.AirId && BlockRegistry.IsSolid(id) && BlockRegistry.IsOpaque(id);
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
    }
}
