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
                mob = new GenericMobEntity(def, position, yaw);
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
        /// Advance all mobs by one frame. When <paramref name="playerPosition"/> is supplied,
        /// natural spawning (near the player, 24-32 blocks out) and despawning (far-away mobs)
        /// also run.
        /// </summary>
        public void Update(float deltaSeconds, Point3D playerPosition, bool enableSpawning = true)
        {
            _entityWatch.Restart();

            // Update all mobs. Every mob derives from MobEntity (Duck, Coyote, SteveMob, generic
            // registry mobs all share one universal AI/physics implementation).
            for (int i = _mobs.Count - 1; i >= 0; i--)
            {
                var mob = _mobs[i];

                if (mob is MobEntity mobEntity)
                {
                    // Hostiles hunt the nearest human: the local player plus any Steve NPCs. The
                    // zombie re-paths toward the target each frame (A* routes around cliffs/walls)
                    // and its OnAttack damages a Steve when it closes in. The player has no health
                    // system yet, so attacks on the player just land as a harmless hit.
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
                            : null;
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

                    // Despawn: too far away, or idle too long at medium distance.
                    if (enableSpawning && ShouldDespawn(mobEntity, playerPosition))
                    {
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
            if (count < 2) return;
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

        // Serializes the current mob state for a world save.
        public List<SavedMob> SaveMobs()
        {
            var result = new List<SavedMob>();
            foreach (var mob in _mobs)
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
                result.Add(new SavedMob { Type = type, X = mob.Position.X, Y = mob.Position.Y, Z = mob.Position.Z, Yaw = mob.Yaw, Health = health });
            }
            return result;
        }

        // Restores mobs from a world save.
        public void LoadMobs(IEnumerable<SavedMob> mobs)
        {
            _mobs.Clear();
            foreach (var m in mobs)
            {
                SpawnSavedMob(m);
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
                var generic = new GenericMobEntity(def, pos, m.Yaw);
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
    public class GenericMobEntity : MobEntity
    {
        private readonly MobDefinition _definition;
        private MobModel? _model;

        public GenericMobEntity(MobDefinition definition, Point3D position, float yaw) : base(position, yaw)
        {
            _definition = definition ?? throw new ArgumentNullException(nameof(definition));
            Width = definition.Width;
            Height = definition.Height;
            MaxHealth = definition.MaxHealth;
            Health = MaxHealth;
            Hostile = definition.Hostile;
            AttackDamage = definition.AttackDamage;
            AggroRange = definition.AggroRange;
            AttackRange = definition.AttackRange;
            AttackCooldown = definition.AttackCooldown;
        }

        public string MobId => _definition.Id;

        // The renderer routes render data to the per-type MobModel entry by this name, so it MUST
        // be the registry id (e.g. "zombie"), not the class name "genericmobentity".
        public override string MobTypeName => _definition.Id;

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
                ModelScale = _definition.Scale > 0f ? _definition.Scale : 1.0f,
                YawCorrection = _definition.YawCorrection,
            };
            _model.Load(_definition.ModelPath, _definition.TexturePath);
            return _model.Loaded;
        }
    }
}
