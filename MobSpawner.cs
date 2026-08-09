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
    /// Natural mob spawning. Ducks use the AUTHENTIC Infdev 20100630 animal spawner logic
    /// (SpawnerAnimals.java, which spawned pigs/sheep in real Infdev - the same code drives every
    /// animal, and chickens arrived in Alpha with the same class):
    ///
    ///  - Only spawn while total animals are under the cap (Infdev: maxSpawns = 20).
    ///  - Pick a random point within +-128 blocks of the player, any Y up to 128.
    ///  - Jitter 3x3 cells; a cell is valid when the block below is a normal solid cube, the
    ///    spawn cell and the one above are air (or non-liquid), and the spot is at least 32
    ///    blocks from the player (distSq >= 1024).
    ///  - Spawn up to a pack of 3 per pass; only if the entity's getCanSpawnHere passes
    ///    (clear AABB, no collision, not in liquid).
    ///
    /// Monsters (zombies) use the AUTHENTIC Infdev SpawnerMonsters.java on top of the same
    /// SpawnerAnimals base:
    ///  - Y is NOT uniform 0..128. It uses the triple-nested rand.Next(rand.Next(rand.Next(112)+8)+8),
    ///    which is heavily biased toward the bottom of the world - so monsters mostly emerge from
    ///    caves and deep shafts rather than clustering on the surface.
    ///  - The base spawn cell must be AIR (a normal cube immediately fails the pass, and a non-air
    ///    non-cube like water also fails) - Infdev's exact checks.
    ///  - EntityMonster.getCanSpawnHere requires block light <= rand.nextInt(8): monsters only
    ///    spawn in darkness, so caves (light 0) always work and the surface only works at night.
    ///  - Monster cap is 100 (vs 20 for animals).
    ///
    /// Coyotes and Steves keep the modern weighted-table path with per-type caps.
    /// </summary>
    public sealed class MobSpawner
    {
        private readonly MobSpawnEntry[] _entries;
        private readonly int _totalWeight;
        private readonly Func<string, Point3D, float, bool> _spawnFn;
        private readonly Func<int> _totalCountFn;
        private readonly Func<string, int> _typeCountFn;
        private readonly bool _monsterSpawner;
        private const double SpawnMinDistanceSq = 32.0 * 32.0; // Infdev: 1024.0
        private const double SpawnMaxDistanceSq = 128.0 * 128.0; // Infdev: +-128 blocks
        private const int MaxTotalMobs = 20;     // Infdev: maxSpawns = 20
        private const int MaxTotalMonsters = 100; // Infdev: monsterSpawner = 100
        private const int MaxPerType = 12;
        private const int MaxPackMembers = 3;    // Infdev: 3 entities per pass

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

            // Infdev: up to 10 spawning passes per update.
            int spawned = 0;
            for (int pass = 0; pass < 10 && spawned < pack; pass++)
            {
                // Pick a base point from a LOADED ground-layer chunk near the player (Infdev's
                // +-128 search worked because the whole area was loaded; our world streams chunks,
                // so we sample a loaded chunk and jitter within it). This keeps Infdev's surface
                // jitter + validation while guaranteeing the terrain actually exists.
                if (!TryPickLoadedChunk(manager, playerPosition, rand, out int chunkX, out int chunkZ))
                    continue;

                int px = chunkX * 16 + rand.Next(16);
                int pz = chunkZ * 16 + rand.Next(16);

                int py;
                if (_monsterSpawner)
                {
                    // Infdev SpawnerMonsters: triple-nested random biases Y toward the world bottom
                    // (rand.Next(rand.Next(rand.Next(112)+8)+8)) so monsters rise from caves/depths.
                    py = rand.Next(rand.Next(rand.Next(112) + 8) + 8);

                    // The base spawn cell must be AIR - a normal cube fails the pass outright and a
                    // non-air non-cube (water) also fails, exactly like Infdev's performSpawning.
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

                // Jitter 3x3 cells from the base point (Infdev's inner loop).
                for (int attempt = 0; attempt < 3 && spawned < pack; attempt++)
                {
                    int x = px + rand.Next(6) - rand.Next(6);
                    int y = py + rand.Next(1) - rand.Next(1);
                    int z = pz + rand.Next(6) - rand.Next(6);

                    // Valid: solid cube below, air/non-liquid at the cell and above, >= 32 from player.
                    if (!IsNormalCube(manager, x, y - 1, z)) continue;
                    if (manager.GetBlockAt(x, y, z) != BlockRegistry.AirId) continue;
                    if (manager.GetBlockAt(x, y + 1, z) != BlockRegistry.AirId) continue;

                    // EntityMonster.getCanSpawnHere: block light <= rand.nextInt(8) - darkness only.
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

        // Infdev's isBlockNormalCube: a solid, non-transparent block (grass/dirt/stone/cobble...).
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
