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

        private const float BlockReach = 6.5f;

        public IReadOnlyList<MobRenderData> MobRenderData => _mobRenderData;

        public EntityManager(ChunkManager chunkManager)
        {
            _chunkManager = chunkManager ?? throw new ArgumentNullException(nameof(chunkManager));
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
            var def = MobRegistry.Get(mobId);
            if (def == null) return false;

            float yawRad = playerYaw * (float)Math.PI / 180f;
            double fx = Math.Sin(yawRad);
            double fz = -Math.Cos(yawRad);

            double spawnX = playerPosition.X + fx * 3.0;
            double spawnY = playerPosition.Y + 2.0;
            double spawnZ = playerPosition.Z + fz * 3.0;

            float mobYaw = playerYaw + 180f;
            var position = new Point3D(spawnX, spawnY, spawnZ);
            
            // Spawn the appropriate mob type based on ID
            if (mobId == "duck")
                _mobs.Add(new Duck(position, mobYaw));
            else if (mobId == "coyote" || mobId == "coyotemob")
                _mobs.Add(new Coyote(position, mobYaw));
            else
                _mobs.Add(new GenericMobEntity(def, position, mobYaw));
            
            return true;
        }

        public void Update(float deltaSeconds)
        {
            // Update all mobs
            for (int i = _mobs.Count - 1; i >= 0; i--)
            {
                var mob = _mobs[i];
                
                // Update based on concrete type
                if (mob is Duck duck)
                {
                    duck.Update(deltaSeconds, _chunkManager);
                }
                else if (mob is MobEntity mobEntity)
                {
                    mobEntity.Update(deltaSeconds, _chunkManager);
                }

                if (mob is Duck d && d.Removed)
                {
                    _mobs.RemoveAt(i);
                }
                else if (mob is MobEntity me && me.Removed)
                {
                    _mobs.RemoveAt(i);
                }
            }

            // Build render data
            _mobRenderData.Clear();
            foreach (var mob in _mobs)
            {
                _mobRenderData.Add(CubeApp.MobRenderData.FromMob(mob));
            }
        }

        public bool TryAttackMob(Point3D cameraPosition, Point3D forward, BlockInteractionSystem.PickBlockResult? blockHit)
        {
            var mob = TryPickMob(cameraPosition, forward, out double mobDistance);
            if (mob == null) return false;

            if (blockHit.HasValue)
            {
                if (mobDistance > blockHit.Value.Distance + 0.02) return false;
            }

            // Call Damage based on concrete type
            if (mob is Duck duck)
            {
                duck.Damage(1, cameraPosition.X, cameraPosition.Z, true);
            }
            else if (mob is MobEntity mobEntity)
            {
                mobEntity.Damage(1, cameraPosition.X, cameraPosition.Z);
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

                // Get dimensions based on concrete type
                float width, height;
                if (mob is Duck)
                {
                    width = Duck.Width;
                    height = Duck.Height;
                }
                else if (mob is MobEntity mobEntity)
                {
                    width = mobEntity.Width;
                    height = mobEntity.Height;
                }
                else
                {
                    continue; // Unknown mob type
                }

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

        public bool LoadModel(GraphicsDevice graphicsDevice)
        {
            // The model needs a live GraphicsDevice to create GPU buffers, so it is built here
            // (lazily) rather than in the constructor - constructing with null used to NRE inside
            // CreateBuffers, get swallowed by the catch, and leave every GLB mob model-less.
            if (graphicsDevice == null) return false;
            _model = new MobModel(graphicsDevice);
            _model.LoadGLB(_definition.ModelPath);
            return _model.Loaded;
        }

        public override MobInstance ToInstance()
        {
            return new MobInstance(
                (float)Position.X, (float)Position.Y, (float)Position.Z,
                Yaw, _walkPhase, _walkAmount,
                (float)_velY, OnGround, _dead,
                _dead ? Math.Clamp(_deathTimer / Math.Max(0.001f, _deathDuration), 0f, 1f) : 0f,
                _deathRollDir, _hurtTimer, _definition.Id);
        }
    }
}
