using System;
using System.Collections.Generic;
using Veldrid;

namespace CubeApp
{
    /// <summary>
    /// Manages entities (mobs) in the game world.
    /// Now uses a unified system where all mobs implement IMobRenderable.
    /// </summary>
    public sealed class EntityManager : IDisposable
    {
        private readonly ChunkManager _chunkManager;
        private readonly List<IMobRenderable> _mobs = new();
        private readonly List<MobRenderData> _mobRenderData = new();
        private readonly Dictionary<string, MobModel> _loadedModels = new();
        private readonly Random _rand = new();

        // Natural spawning + despawning. Set to null to disable.
        private MobSpawner? _spawner;
        private MobSpawner? _monsterSpawner;
        private double _spawnAccumulator;
        private double _monsterSpawnAccumulator;
        private readonly System.Diagnostics.Stopwatch _entityWatch = new();
        public float LastUpdateMs { get; private set; }
        public int MobCount => _mobs.Count;
        private const double SpawnIntervalBase = 2.0; // check for spawning roughly every 2s
        // World->skylightSubtracted (0 day .. 11 night) for the monster darkness gate.
        private Func<int>? _skylightSubtractedFn;

        private const float BlockReach = 6.5f;

        public IReadOnlyList<MobRenderData> MobRenderData => _mobRenderData;

        public EntityManager(ChunkManager chunkManager)
        {
            _chunkManager = chunkManager ?? throw new ArgumentNullException(nameof(chunkManager));
            // Natural spawn table: ducks are common, coyotes rarer, players (Steve) rarest.
            _spawner = new MobSpawner(
                new[]
                {
                    new MobSpawnEntry("duck", 6, 1, 3),
                    new MobSpawnEntry("coyote", 3, 1, 2),
                    new MobSpawnEntry("steve", 1, 1, 1),
                },
                AddMobAt,
                () => _mobs.Count,
                CountMobsOfType);
            // Night monsters (zombies): separate spawner with the darkness/cave logic
            // and a 100-mob cap.
            _monsterSpawner = new MobSpawner(
                new[] { new MobSpawnEntry("zombie", 1, 1, 4) },
                AddMobAt,
                () => _mobs.Count,
                CountMobsOfType,
                monsterSpawner: true);
        }

        /// <summary>Total living mobs (for the spawn cap).</summary>
        public int CountMobs(Point3D ignore) => _mobs.Count;

        /// <summary>
        /// Supplies the current night-dim level (0..11) for the monster darkness gate.
        /// Wired to GameWorld.NightDimLevel so zombies know when the surface is dark.
        /// </summary>
        public void SetSkylightSource(Func<int> skylightSubtractedFn)
        {
            _skylightSubtractedFn = skylightSubtractedFn;
        }

        private int CountMobsOfType(string mobId)
        {
            int count = 0;
            foreach (var mob in _mobs)
            {
                if (mob is MobEntity me && string.Equals(me.MobTypeName, mobId, StringComparison.OrdinalIgnoreCase))
                    count++;
            }
            return count;
        }

        public void SpawnDuck(Point3D playerPosition, float playerYaw)
        {
            float yawRad = playerYaw * (float)Math.PI / 180f;
            double fx = Math.Sin(yawRad);
            double fz = -Math.Cos(yawRad);

            double spawnX = playerPosition.X + fx * 3.0;
            double spawnY = playerPosition.Y + 2.0;
            double spawnZ = playerPosition.Z + fz * 3.0;

            float duckYaw = playerYaw + 180f;
            _mobs.Add(new Duck(new Point3D(spawnX, spawnY, spawnZ), duckYaw));
        }

        public void SpawnCoyote(Point3D playerPosition, float playerYaw)
        {
            float yawRad = playerYaw * (float)Math.PI / 180f;
            double fx = Math.Sin(yawRad);
            double fz = -Math.Cos(yawRad);

            double spawnX = playerPosition.X + fx * 3.0;
            double spawnY = playerPosition.Y + 2.0;
            double spawnZ = playerPosition.Z + fz * 3.0;

            float mobYaw = playerYaw + 180f;
            _mobs.Add(new Coyote(new Point3D(spawnX, spawnY, spawnZ), mobYaw));
        }

        public void SpawnSteve(Point3D playerPosition, float playerYaw)
        {
            float yawRad = playerYaw * (float)Math.PI / 180f;
            double fx = Math.Sin(yawRad);
            double fz = -Math.Cos(yawRad);

            double spawnX = playerPosition.X + fx * 3.0;
            double spawnY = playerPosition.Y + 2.0;
            double spawnZ = playerPosition.Z + fz * 3.0;

            // MobEntity yaw is radians; face the mob back toward the player.
            float mobYaw = (playerYaw + 180f) * (float)Math.PI / 180f;
            _mobs.Add(new SteveMob(new Point3D(spawnX, spawnY, spawnZ), mobYaw));
        }

        public bool SpawnMobById(string mobId, Point3D playerPosition, float playerYaw)
        {
            // Built-in mobs spawn from hardcoded classes; registry mobs need a MobDefinition.
            if (mobId != "duck" && mobId != "coyote" && mobId != "coyotemob" && mobId != "steve"
                && MobRegistry.Get(mobId) == null) return false;

            float yawRad = playerYaw * (float)Math.PI / 180f;
            double fx = Math.Sin(yawRad);
            double fz = -Math.Cos(yawRad);

            double spawnX = playerPosition.X + fx * 3.0;
            double spawnY = playerPosition.Y + 2.0;
            double spawnZ = playerPosition.Z + fz * 3.0;

            float mobYaw = playerYaw + 180f;
            return AddMobAt(mobId, new Point3D(spawnX, spawnY, spawnZ), mobYaw);
        }

        /// <summary>Creates a mob exactly at the given position (used by the natural spawner).</summary>
        public bool AddMobAt(string mobId, Point3D position, float yaw)
        {
            MobEntity? mob = null;
            if (mobId == "duck")
                mob = new Duck(position, yaw);
            else if (mobId == "coyote" || mobId == "coyotemob")
                mob = new Coyote(position, yaw);
            else if (mobId == "steve")
                mob = new SteveMob(position, yaw);
            else
            {
                var def = MobRegistry.Get(mobId);
                if (def == null) return false;
                // Rare brute variant: 1 in 50 zombies spawn 2x size, half speed, double health.
                bool brute = string.Equals(def.Id, "zombie", StringComparison.OrdinalIgnoreCase)
                    && Random.Shared.Next(50) == 0;
                mob = new GenericMobEntity(def, position, yaw, brute);
            }

            // Give every mob the world's day/night source for environmental behaviors (sunburn).
            if (mob != null)
            {
                mob.SkylightSource = _skylightSubtractedFn;
                _mobs.Add(mob);
                return true;
            }
            return false;
        }

        public void Update(float deltaSeconds) => Update(deltaSeconds, new Point3D(0, 0, 0), false);

        /// <summary>
        /// Set by the owning world so hostile mobs can damage the local player. Receives the
        /// attack damage and the death cause to record if the hit is lethal. Null = attacks on the
        /// player are harmless (creative / pre-wiring).
        /// </summary>
        public Action<int, DeathCause>? PlayerDamageCallback { get; set; }

        // ---- player body for mob separation ----
        /// <summary>Player AABB center (world space, not the eye) used for player-mob repulsion; the world sets it each tick.</summary>
        public Point3D PlayerBodyCenter;
        /// <summary>Player XZ half-extent (radius) for repulsion.</summary>
        public double PlayerHalfWidth = 0.30;
        /// <summary>Player vertical half-extent for repulsion.</summary>
        public double PlayerHalfHeight = 0.90;
        /// <summary>Receives the player's XZ push velocity from mob separation (mobs shove the player back).</summary>
        public Action<Point3D>? PlayerPushCallback { get; set; }

        /// <summary>
        /// Advance all mobs by one frame. When <paramref name="playerPosition"/> is supplied,
        /// natural spawning (near the player, 24-32 blocks out) and despawning (far-away mobs)
        /// also run.
        /// </summary>
        public void Update(float deltaSeconds, Point3D playerPosition, bool enableSpawning = true)
        {
            _entityWatch.Restart();

            // World-streaming persistence: detach mobs whose chunk just unloaded (they'd otherwise
            // keep simulating against empty air and fall into the void), and restore mobs saved
            // for chunks the player just returned to.
            SyncDetachedMobs(playerPosition);

            // Update all mobs. Every mob derives from MobEntity (Duck, Coyote, SteveMob, generic
            // registry mobs all share one universal AI/physics implementation).
            for (int i = _mobs.Count - 1; i >= 0; i--)
            {
                var mob = _mobs[i];

                if (mob is MobEntity mobEntity)
                {
                    // Hostiles hunt the nearest human: the local player plus any Steve NPCs. The
                    // zombie re-paths toward the target each frame (A* routes around cliffs/walls)
                    // and its OnAttack damages a Steve when it closes in - or the local player via
                    // PlayerDamageCallback (health, hurt flash, regen reset, death cause).
                    if (mobEntity.Hostile)
                    {
                        IMobRenderable? steveTarget = null;
                        Point3D target = playerPosition;
                        double bestSq = DistSq(mobEntity.Position, playerPosition);
                        for (int h = 0; h < _mobs.Count; h++)
                        {
                            if (h == i) continue;
                            var other = _mobs[h];
                            if (other.IsDead) continue;
                            if (other is SteveMob)
                            {
                                double d = DistSq(mobEntity.Position, other.Position);
                                if (d < bestSq) { bestSq = d; steveTarget = other; target = other.Position; }
                            }
                        }

                        var capturedSteve = steveTarget;
                        var capturedMob = mobEntity;
                        mobEntity.OnAttack = capturedSteve is MobEntity steve
                            ? () => steve.Damage(capturedMob.AttackDamage, capturedMob.Position.X, capturedMob.Position.Z, true)
                            : () => PlayerDamageCallback?.Invoke(capturedMob.AttackDamage, DeathCause.Mob);
                        mobEntity.SetChaseTarget(target);
                    }
                    else
                    {
                        mobEntity.SetChaseTarget(null);
                        mobEntity.OnAttack = null;
                    }

                    mobEntity.Update(deltaSeconds, _chunkManager);

                    if (mobEntity.Removed)
                    {
                        _mobs.RemoveAt(i);
                        continue;
                    }

                    // Despawn: too far away, or idle too long at medium distance. Instead of
                    // deleting the mob, snapshot its state so it comes back when the player
                    // returns to its chunk (world-streaming persistence).
                    if (enableSpawning && ShouldDespawn(mobEntity, playerPosition))
                    {
                        DetachMob(mobEntity);
                        _mobs.RemoveAt(i);
                    }
                }
            }

            LastUpdateMs = (float)_entityWatch.Elapsed.TotalMilliseconds;

            // Push mobs apart so they don't clump. Same algorithm as MC 1.12: inverse-distance
            // horizontal repulsion applied to both entities.
            PushMobsApart(deltaSeconds);

            // Natural spawning: attempt multiple passes each tick while under the cap (the
            // interval only gates how often we check, so an empty area fills quickly without
            // hammering every frame).
            if (enableSpawning && _spawner != null)
            {
                _spawnAccumulator += deltaSeconds;
                if (_spawnAccumulator >= SpawnIntervalBase)
                {
                    _spawnAccumulator = 0;
                    for (int pass = 0; pass < 10; pass++)
                    {
                        _spawner.TrySpawn(_chunkManager, playerPosition, _rand);
                    }
                }
            }

            // Night monster spawning: same cadence, but zombies only appear in darkness and are
            // strongly biased toward caves. The light gate uses the current night-dim level so they
            // pour out of caves all day and over the surface at night.
            if (enableSpawning && _monsterSpawner != null && _skylightSubtractedFn != null)
            {
                _monsterSpawnAccumulator += deltaSeconds;
                if (_monsterSpawnAccumulator >= SpawnIntervalBase)
                {
                    _monsterSpawnAccumulator = 0;
                    int skylight = _skylightSubtractedFn();
                    for (int pass = 0; pass < 10; pass++)
                    {
                        _monsterSpawner.TrySpawn(_chunkManager, playerPosition, _rand,
                            (x, y, z) => _chunkManager.GetSkyLightEstimate(x, y, z, skylight));
                    }
                }
            }

            // Build render data
            _mobRenderData.Clear();
            foreach (var mob in _mobs)
            {
                _mobRenderData.Add(CubeApp.MobRenderData.FromMob(mob));
            }
        }

        /// <summary>Collects mining targets from all zombies actively breaking blocks.</summary>
        public void CollectMiningTargets(System.Collections.Generic.List<CubeApp.Renderer.ZombieMiningTarget> list)
        {
            list.Clear();
            foreach (var mob in _mobs)
            {
                var mb = ((IMobRenderable)mob).MiningBlock;
                if (mb.HasValue)
                {
                    list.Add(new CubeApp.Renderer.ZombieMiningTarget
                    {
                        X = mb.Value.X, Y = mb.Value.Y, Z = mb.Value.Z,
                        BlockId = mb.Value.BlockId,
                        Progress = mb.Value.Progress,
                    });
                }
            }
        }

        /// <summary>
        /// MC-style entity repulsion. Pairs of nearby mobs push apart with inverse-distance
        /// horizontal force so they spread out instead of clumping into one spot.
        /// </summary>
        private void PushMobsApart(float dt)
        {
            int count = _mobs.Count;
            if (count == 0) return;
            double dtScale = dt * 60.0; // normalize to 60fps tick equivalent

            for (int i = 0; i < count; i++)
            {
                if (_mobs[i] is not MobEntity a || a.IsDead) continue;
                double ax = a.Position.X, ay = a.Position.Y, az = a.Position.Z;

                for (int j = i + 1; j < count; j++)
                {
                    if (_mobs[j] is not MobEntity b || b.IsDead) continue;
                    double dx = ax - b.Position.X, dz = az - b.Position.Z;
                    double dy = ay - b.Position.Y;
                    double distXZSq = dx * dx + dz * dz;
                    double minDistXZ = (a.Width * 0.5 + b.Width * 0.5);
                    double minDistXZSq = minDistXZ * minDistXZ;

                    if (distXZSq < minDistXZSq && distXZSq > 0.0001)
                    {
                        // Only push when vertically overlapping too — mobs at different Y levels
                        // (e.g. one on a platform above) shouldn't repel each other.
                        double minDistY = (a.Height * 0.5 + b.Height * 0.5);
                        if (Math.Abs(dy) >= minDistY) continue;

                        double dist = Math.Sqrt(distXZSq);
                        double invDist = 1.0 / dist;
                        double push = 3.0 * Math.Min(1.0, invDist) * dtScale;
                        a.AddVelocity(dx * invDist * push, 0, dz * invDist * push);
                        b.AddVelocity(-dx * invDist * push, 0, -dz * invDist * push);
                    }
                }

                // The local player participates in separation like a heavy mob: mobs touching
                // the player's AABB get pushed away, and the player receives a lighter shove back
                // (MC-style mass ratio ~2:1 - the player is heavier than most mobs).
                if (PlayerPushCallback != null)
                {
                    double pdx = ax - PlayerBodyCenter.X, pdz = az - PlayerBodyCenter.Z;
                    double pdy = ay - PlayerBodyCenter.Y;
                    double pMinXZ = a.Width * 0.5 + PlayerHalfWidth;
                    double pDistXZSq = pdx * pdx + pdz * pdz;

                    if (pDistXZSq < pMinXZ * pMinXZ && pDistXZSq > 0.0001)
                    {
                        double pMinY = a.Height * 0.5 + PlayerHalfHeight;
                        if (Math.Abs(pdy) < pMinY)
                        {
                            double pDist = Math.Sqrt(pDistXZSq);
                            double pInv = 1.0 / pDist;
                            double pPush = 3.0 * Math.Min(1.0, pInv) * dtScale;
                            a.AddVelocity(pdx * pInv * pPush, 0, pdz * pInv * pPush);
                            PlayerPushCallback.Invoke(new Point3D(
                                -pdx * pInv * pPush * 0.5, 0, -pdz * pInv * pPush * 0.5));
                        }
                    }
                }
            }
        }

        // Despawn (tuned so natural spawns don't instantly vanish): instant despawn
        // beyond 128 blocks; between 64 and 128 blocks, despawn after 600 idle ticks. The natural
        // spawn ring is 24-32 blocks, so mobs that wander a little don't cross the idle threshold.
        private static double DistSq(Point3D a, Point3D b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return dx * dx + dy * dy + dz * dz;
        }

        private bool ShouldDespawn(MobEntity mob, Point3D playerPosition)
        {
            double dx = mob.Position.X - playerPosition.X;
            double dy = mob.Position.Y - playerPosition.Y;
            double dz = mob.Position.Z - playerPosition.Z;
            double distSq = dx * dx + dy * dy + dz * dz;
            if (distSq > 128.0 * 128.0) return true;
            if (distSq > 64.0 * 64.0)
            {
                _idleTimeAccum[mob] = _idleTimeAccum.TryGetValue(mob, out var t) ? t + 1 : 1;
                if (_idleTimeAccum[mob] > 600 && _rand.Next(800) == 0) return true;
            }
            else
            {
                _idleTimeAccum[mob] = 0;
            }
            return false;
        }

        private readonly Dictionary<IMobRenderable, int> _idleTimeAccum = new();

        // ---- Per-chunk mob persistence (world streaming) ----
        // Mobs whose chunk is not currently loaded (or that despawned by distance) are detached
        // here instead of being deleted: state is snapshotted and they come back when the player
        // returns to their chunk. Keeps them from falling through unloaded terrain and from
        // permanently vanishing when the player leaves the area.
        private readonly Dictionary<ChunkCoordinates, List<SavedMob>> _detachedMobs = new();
        private readonly Queue<ChunkCoordinates> _detachedOrder = new();
        private const int MaxDetachedMobs = 512;    // bound memory when roaming a large world
        // Re-activate a saved mob only when the player is genuinely close. Must be BELOW the
        // distance-despawn band (64-128 blocks) or a mob despawned there would be restored on the
        // next frame and immediately re-despawned, flickering forever.
        private const double RestoreRadiusBlocks = 48.0;

        public bool TryAttackMob(Point3D cameraPosition, Point3D forward, BlockInteractionSystem.PickBlockResult? blockHit)
        {
            var mob = TryPickMob(cameraPosition, forward, out double mobDistance);
            if (mob == null) return false;

            if (blockHit.HasValue)
            {
                if (mobDistance > blockHit.Value.Distance + 0.02) return false;
            }

            // Every mob is a MobEntity now (Duck, Coyote, SteveMob, generic) - one universal damage
            // path with a real attacker source for knockback/panic direction.
            if (mob is MobEntity mobEntity)
            {
                mobEntity.Damage(1, cameraPosition.X, cameraPosition.Z, true);
            }
            return true;
        }

        private IMobRenderable? TryPickMob(Point3D origin, Point3D direction, out double hitDistance)
        {
            hitDistance = double.PositiveInfinity;
            IMobRenderable? best = null;
            var dir = direction.Normalized();

            foreach (var mob in _mobs)
            {
                if (mob.IsDead) continue;

                // Every mob is a MobEntity now - one universal dimension source.
                if (mob is not MobEntity mobEntity) continue;

                float width = mobEntity.Width;
                float height = mobEntity.Height;

                float half = width * 0.5f;
                double minX = mob.Position.X - half;
                double maxX = mob.Position.X + half;
                double minY = mob.Position.Y;
                double maxY = mob.Position.Y + height;
                double minZ = mob.Position.Z - half;
                double maxZ = mob.Position.Z + half;

                if (RayBox(origin, dir, minX, minY, minZ, maxX, maxY, maxZ, out double t)
                    && t <= BlockReach && t < hitDistance)
                {
                    hitDistance = t;
                    best = mob;
                }
            }

            return best;
        }

        /// <summary>
        /// Tests the ray against every living mob AABB and returns the nearest hit distance.
        /// Used by the block-pick ray so a mob's hitbox stops the ray: the player can never
        /// break or place through a living mob (no more accidental wall-damage while fighting).
        /// AABB math matches TryPickMob exactly (Position.Y = feet).
        /// </summary>
        public bool TryRaycastMobs(Point3D origin, Point3D direction, double maxDistance, out double hitDistance)
        {
            hitDistance = double.PositiveInfinity;
            var dir = direction.Normalized();

            foreach (var mob in _mobs)
            {
                if (mob.IsDead) continue;
                if (mob is not MobEntity mobEntity) continue;

                float half = mobEntity.Width * 0.5f;
                double minX = mob.Position.X - half;
                double maxX = mob.Position.X + half;
                double minY = mob.Position.Y;
                double maxY = mob.Position.Y + mobEntity.Height;
                double minZ = mob.Position.Z - half;
                double maxZ = mob.Position.Z + half;

                if (RayBox(origin, dir, minX, minY, minZ, maxX, maxY, maxZ, out double t)
                    && t <= maxDistance && t < hitDistance)
                {
                    hitDistance = t;
                }
            }

            return hitDistance <= maxDistance;
        }

        private static bool RayBox(
            Point3D origin, Point3D dir,
            double minX, double minY, double minZ,
            double maxX, double maxY, double maxZ,
            out double tEntry)
        {
            tEntry = 0;
            double tMin = double.NegativeInfinity;
            double tMax = double.PositiveInfinity;

            for (int axis = 0; axis < 3; axis++)
            {
                double o = axis == 0 ? origin.X : (axis == 1 ? origin.Y : origin.Z);
                double d = axis == 0 ? dir.X : (axis == 1 ? dir.Y : dir.Z);
                double lo = axis == 0 ? minX : (axis == 1 ? minY : minZ);
                double hi = axis == 0 ? maxX : (axis == 1 ? maxY : maxZ);

                if (Math.Abs(d) < 1e-9)
                {
                    if (o < lo || o > hi) return false;
                }
                else
                {
                    double t1 = (lo - o) / d;
                    double t2 = (hi - o) / d;
                    if (t1 > t2) (t1, t2) = (t2, t1);
                    if (t1 > tMin) tMin = t1;
                    if (t2 < tMax) tMax = t2;
                    if (tMin > tMax) return false;
                }
            }

            if (tMax < 0) return false;
            tEntry = tMin < 0 ? 0 : tMin;
            return true;
        }

        public void Clear()
        {
            _mobs.Clear();
            _mobRenderData.Clear();
        }

        // Serializes the current mob state for a world save. Includes detached (chunk-unloaded)
        // mobs so roaming mobs survive a save/quit too.
        public List<SavedMob> SaveMobs()
        {
            var result = new List<SavedMob>();
            foreach (var mob in _mobs)
            {
                result.Add(SnapshotMob(mob));
            }
            foreach (var list in _detachedMobs.Values)
            {
                result.AddRange(list);
            }
            return result;
        }

        private static SavedMob SnapshotMob(IMobRenderable mob)
        {
            string type = mob switch
            {
                Coyote => "coyote",
                SteveMob => "steve",
                GenericMobEntity g => g.MobId,
                MobEntity me => me.MobTypeName,
                _ => "duck",
            };
            int health = mob is MobEntity me2 ? me2.Health : 10;
            bool brute = mob is GenericMobEntity g2 && g2.IsBrute;
            return new SavedMob { Type = type, X = mob.Position.X, Y = mob.Position.Y, Z = mob.Position.Z, Yaw = mob.Yaw, Health = health, Brute = brute };
        }

        // Restores mobs from a world save.
        public void LoadMobs(IEnumerable<SavedMob> mobs)
        {
            _mobs.Clear();
            _detachedMobs.Clear();
            _detachedOrder.Clear();
            foreach (var m in mobs)
            {
                SpawnSavedMob(m);
            }
        }

        // ---- World-streaming mob persistence ----

        // The chunk a world position lives in (layer from Y, column from X/Z).
        private static ChunkCoordinates ChunkOf(Point3D pos)
        {
            int layer = ChunkManager.LayerForWorldY((int)Math.Floor(pos.Y));
            return new ChunkCoordinates(layer, GameWorld.WorldToChunkCoord(pos.X), GameWorld.WorldToChunkCoord(pos.Z));
        }

        // Snapshots a mob into the detached pool for its chunk (bounded so roaming a large world
        // can't grow memory without limit).
        private void DetachMob(MobEntity mob)
        {
            var cc = ChunkOf(mob.Position);
            if (!_detachedMobs.TryGetValue(cc, out var list))
            {
                list = new List<SavedMob>();
                _detachedMobs[cc] = list;
                _detachedOrder.Enqueue(cc);
            }
            list.Add(SnapshotMob(mob));

            if (_detachedMobs.Count > MaxDetachedMobs)
            {
                while (_detachedMobs.Count > MaxDetachedMobs && _detachedOrder.Count > 0)
                {
                    _detachedMobs.Remove(_detachedOrder.Dequeue());
                }
            }
        }

        // Runs every frame BEFORE mob physics: detaches mobs standing in unloaded chunks (they'd
        // fall through the void otherwise) and re-activates saved mobs whose chunk is loaded again
        // and the player is close enough to simulate.
        private void SyncDetachedMobs(Point3D playerPosition)
        {
            // Detach any active mob whose chunk is no longer loaded.
            for (int i = _mobs.Count - 1; i >= 0; i--)
            {
                if (_mobs[i] is not MobEntity me) continue;
                if (!_chunkManager.TryGetLoadedChunk(ChunkOf(me.Position), out _))
                {
                    DetachMob(me);
                    _mobs.RemoveAt(i);
                }
            }

            // Restore detached mobs for chunks that are loaded again, when within restore range.
            if (_detachedMobs.Count == 0) return;
            var loadedKeys = new List<ChunkCoordinates>();
            foreach (var key in _detachedMobs.Keys)
            {
                if (_chunkManager.TryGetLoadedChunk(key, out _)) loadedKeys.Add(key);
            }
            foreach (var key in loadedKeys)
            {
                var list = _detachedMobs[key];
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var m = list[i];
                    double dx = m.X - playerPosition.X;
                    double dz = m.Z - playerPosition.Z;
                    if (dx * dx + dz * dz <= RestoreRadiusBlocks * RestoreRadiusBlocks)
                    {
                        SpawnSavedMob(m);
                        list.RemoveAt(i);
                    }
                }
                if (list.Count == 0) _detachedMobs.Remove(key);
            }

            // The eviction queue can accumulate stale entries as chunks repeatedly load/unload;
            // rebuild it from the live keys when it grows well past the actual map size.
            if (_detachedMobs.Count > 0 && _detachedOrder.Count > _detachedMobs.Count * 2)
            {
                _detachedOrder.Clear();
                foreach (var key in _detachedMobs.Keys) _detachedOrder.Enqueue(key);
            }
        }

        private void SpawnSavedMob(SavedMob m)
        {
            var pos = new Point3D(m.X, m.Y, m.Z);
            if (m.Type == "duck")
            {
                var mob = new Duck(pos, m.Yaw);
                mob.RestoreState(pos, m.Yaw, m.Health);
                _mobs.Add(mob);
            }
            else if (m.Type == "coyote" || m.Type == "coyotemob")
            {
                var mob = new Coyote(pos, m.Yaw);
                mob.RestoreState(pos, m.Yaw, m.Health);
                _mobs.Add(mob);
            }
            else if (m.Type == "steve")
            {
                var mob = new SteveMob(pos, m.Yaw);
                mob.RestoreState(pos, m.Yaw, m.Health);
                _mobs.Add(mob);
            }
            else
            {
                var def = MobRegistry.Get(m.Type);
                if (def == null) return;
                var generic = new GenericMobEntity(def, pos, m.Yaw, m.Brute);
                generic.RestoreState(pos, m.Yaw, m.Health);
                _mobs.Add(generic);
            }
        }

        public void Dispose() { }
    }

    /// <summary>
    /// Generic mob entity that uses MobDefinition properties and loads models from MobRegistry.
    /// This allows any mob to be spawned just by having files in the MobEntities folder.
    /// </summary>
    public class GenericMobEntity : MobEntity, IMobRenderable
    {
        private readonly MobDefinition _definition;
        private MobModel? _model;

        /// <summary>True for the rare "brute" variant (1 in 50 zombies): 2x size, half speed,
        /// double health. The model renders at <see cref="ScaleOverride"/> instead of the
        /// definition's scale so the visual doubles with the collision box.</summary>
        public bool IsBrute { get; }

        /// <summary>Model scale override for the brute variant (definition.Scale * 2), applied
        /// when the model loads. 0 = use the definition's scale.</summary>
        public float ScaleOverride { get; }

        public GenericMobEntity(MobDefinition definition, Point3D position, float yaw, bool brute = false) : base(position, yaw)
        {
            _definition = definition ?? throw new ArgumentNullException(nameof(definition));
            IsBrute = brute;
            Width = definition.Width;
            Height = definition.Height;
            MaxHealth = definition.MaxHealth;
            Health = MaxHealth;
            Hostile = definition.Hostile;
            AttackDamage = definition.AttackDamage;
            AggroRange = definition.AggroRange;
            AttackRange = definition.AttackRange;
            AttackCooldown = definition.AttackCooldown;
            if (brute)
            {
                Width *= 2f;
                Height *= 2f;
                MaxHealth *= 2;
                Health = MaxHealth;
                MaxSpeed *= 0.5f;               // half their current effective speed
                ScaleOverride = definition.Scale * 2f;
            }
        }

        public string MobId => _definition.Id;

        // The renderer routes render data to the per-type MobModel entry by this name, so it MUST
        // be the registry id (e.g. "zombie"), not the class name "genericmobentity".
        public override string MobTypeName => _definition.Id;

        // The renderer's shared per-type model is baked at the definition's scale, so the brute's
        // extra size is expressed as a per-instance multiplier (2x for a brute, 1x otherwise).
        float IMobRenderable.RenderScale =>
            ScaleOverride > 0f ? ScaleOverride / (_definition.Scale > 0f ? _definition.Scale : 1f) : 1f;

        // Zombie sunburn: in daytime, if the sky is visible above and the brightness is high
        // enough, the zombie periodically catches fire.
        private float _burnAccumulator;

        protected override void UpdateEnvironment(float dt, ChunkManager manager)
        {
            if (!_definition.BurnsInDaylight) return;
            if (_dead) return;
            if (SkylightSource == null) return;

            int skylight = SkylightSource();
            if (skylight >= 4) return; // not daytime (night-dim 0..3 = daylight)

            // Nothing opaque above within the probe range (sky visible).
            for (int wy = (int)Math.Floor(Position.Y) + 1; wy <= (int)Math.Floor(Position.Y) + 8; wy++)
            {
                int id = manager.GetBlockAt((int)Math.Floor(Position.X), wy, (int)Math.Floor(Position.Z));
                if (id != BlockRegistry.AirId && BlockRegistry.IsOpaque(id)) return;
            }

            _burnAccumulator += dt;
            // Fire chance scales with how bright the daylight is (roughly 1/20s at noon).
            float brightness = Math.Max(0f, (15f - skylight) / 15f);
            float chancePerSecond = ((brightness - 0.4f) * 2f) / 30f;
            if (_burnAccumulator > 1f / Math.Max(0.001f, chancePerSecond))
            {
                _burnAccumulator = 0f;
                Damage(1, Position.X, Position.Z, false);
            }
        }

        public bool LoadModel(GraphicsDevice graphicsDevice)
        {
            // The model needs a live GraphicsDevice to create GPU buffers, so it is built here
            // (lazily) rather than in the constructor - constructing with null used to NRE inside
            // CreateBuffers, get swallowed by the catch, and leave every GLB mob model-less.
            if (graphicsDevice == null) return false;
            _model = new MobModel(graphicsDevice)
            {
                ModelScale = ScaleOverride > 0f ? ScaleOverride : (_definition.Scale > 0f ? _definition.Scale : 1.0f),
                YawCorrection = _definition.YawCorrection,
            };
            _model.Load(_definition.ModelPath, _definition.TexturePath);
            return _model.Loaded;
        }
    }
}
