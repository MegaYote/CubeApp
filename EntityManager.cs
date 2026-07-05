using System;
using System.Collections.Generic;

namespace CubeApp
{
    /// <summary>
    /// Manages entities (ducks/mobs) in the game world.
    /// </summary>
    public sealed class EntityManager
    {
        private readonly ChunkManager _chunkManager;
        private readonly List<Duck> _ducks = new();
        private readonly List<DuckInstance> _duckInstances = new();

        public EntityManager(ChunkManager chunkManager)
        {
            _chunkManager = chunkManager ?? throw new ArgumentNullException(nameof(chunkManager));
        }

        public IReadOnlyList<DuckInstance> DuckInstances => _duckInstances;

        public void SpawnDuck(Point3D playerPosition, float playerYaw)
        {
            // Spawn a duck a couple of blocks ahead of and above the player
            float yawRad = playerYaw * (float)Math.PI / 180f;
            double fx = Math.Sin(yawRad);
            double fz = -Math.Cos(yawRad);

            double spawnX = playerPosition.X + fx * 3.0;
            double spawnY = playerPosition.Y + 2.0;
            double spawnZ = playerPosition.Z + fz * 3.0;

            // Duck faces back toward the player
            float duckYaw = playerYaw + 180f;

            _ducks.Add(new Duck(new Point3D(spawnX, spawnY, spawnZ), duckYaw));
        }

        public void Update(float deltaSeconds)
        {
            for (int i = _ducks.Count - 1; i >= 0; i--)
            {
                var duck = _ducks[i];
                duck.Update(deltaSeconds, _chunkManager);

                if (!duck.IsAlive)
                {
                    _ducks.RemoveAt(i);
                }
            }

            // Rebuild instance list for rendering
            _duckInstances.Clear();
            foreach (var duck in _ducks)
            {
                _duckInstances.Add(new DuckInstance
                {
                    Position = duck.Position,
                    Yaw = duck.Yaw,
                    Pitch = duck.Pitch,
                    WalkPhase = duck.WalkPhase,
                    WalkAmount = duck.WalkAmount,
                    FlapPhase = duck.FlapPhase,
                    OnGround = duck.OnGround,
                    VelocityY = duck.VelocityY,
                    HurtTimer = duck.HurtTimer,
                    DeathRollDir = duck.DeathRollDir,
                    DeathT = duck.DeathT,
                    IsDead = duck.IsDead
                });
            }
        }

        public void Clear()
        {
            _ducks.Clear();
            _duckInstances.Clear();
        }
    }
}
