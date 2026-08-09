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

        // Natural spawning + despawning (1.12-style). Set to null to disable.
        private MobSpawner? _spawner;
        private double _spawnAccumulator;
        private const double SpawnIntervalBase = 2.0; // check for spawning roughly every 2s

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
                    new MobSpawnEntry("zombie", 4, 1, 4),
                    new MobSpawnEntry("steve", 1, 1, 1),
                },
                AddMobAt,
                () => _mobs.Count,
                CountMobsOfType);
        }

        /// <summary>Total living mobs (for the spawn cap).</summary>
        public int CountMobs(Point3D ignore) => _mobs.Count;

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
            if (mobId == "duck")
                _mobs.Add(new Duck(position, yaw));
            else if (mobId == "coyote" || mobId == "coyotemob")
                _mobs.Add(new Coyote(position, yaw));
            else if (mobId == "steve")
                _mobs.Add(new SteveMob(position, yaw));
            else
            {
                var def = MobRegistry.Get(mobId);
                if (def == null) return false;
                _mobs.Add(new GenericMobEntity(def, position, yaw));
            }
            return true;
        }

        public void Update(float deltaSeconds) => Update(deltaSeconds, new Point3D(0, 0, 0), false);

        /// <summary>
        /// Advance all mobs by one frame. When <paramref name="playerPosition"/> is supplied,
        /// natural spawning (near the player, 24-32 blocks out) and despawning (far-away mobs)
        /// also run.
        /// </summary>
        public void Update(float deltaSeconds, Point3D playerPosition, bool enableSpawning = true)
        {
            // Update all mobs. Every mob derives from MobEntity (Duck, Coyote, SteveMob, generic
            // registry mobs all share one universal AI/physics implementation).
            for (int i = _mobs.Count - 1; i >= 0; i--)
            {
                var mob = _mobs[i];

                if (mob is MobEntity mobEntity)
                {
                    mobEntity.Update(deltaSeconds, _chunkManager);

                    if (mobEntity.Removed)
                    {
                        _mobs.RemoveAt(i);
                        continue;
                    }

                    // Despawn: too far away, or idle too long at medium distance (1.12).
                    if (enableSpawning && ShouldDespawn(mobEntity, playerPosition))
                    {
                        _mobs.RemoveAt(i);
                    }
                }
            }

            // Natural spawning. Like Infdev's SpawnerAnimals.onUpdate, we attempt multiple passes
            // (Infdev did 10) each tick while under the cap; the interval only gates how often we
            // check so an empty area fills quickly without hammering every frame.
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

            // Build render data
            _mobRenderData.Clear();
            foreach (var mob in _mobs)
            {
                _mobRenderData.Add(CubeApp.MobRenderData.FromMob(mob));
            }
        }

        // Despawn (1.12-style but tuned so natural spawns don't instantly vanish): instant despawn
        // beyond 128 blocks; between 64 and 128 blocks, despawn after 600 idle ticks. The natural
        // spawn ring is 24-32 blocks, so mobs that wander a little don't cross the idle threshold.
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
        }

        public string MobId => _definition.Id;

        // The renderer routes render data to the per-type MobModel entry by this name, so it MUST
        // be the registry id (e.g. "zombie"), not the class name "genericmobentity".
        public override string MobTypeName => _definition.Id;

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
