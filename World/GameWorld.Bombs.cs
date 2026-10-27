using System;
using System.Collections.Generic;

namespace Cubuild
{
    public sealed partial class GameWorld
    {
        public const long BombFuseTicks = 107;
        private const int BombBlastRadius = 4;
        private const double BombProjectileGravity = 20.0;
        private const double BombProjectileMaxFallSpeed = 32.0;
        private const double BombProjectileMaxRiseSpeed = 24.0;
        private const double BombProjectileMaxSubstep = 0.2;
        private const double BombProjectileHalfSize = 0.5;
        private const double BombProjectileCollisionEpsilon = 0.001;
        private const double BombProjectilePinLookaheadSeconds = 0.35;
        private readonly int _idBomb = BlockRegistry.GetId("bomb");
        private readonly int _idBedrock = BlockRegistry.GetId("bedrock");
        private readonly Dictionary<(int X, int Y, int Z), long> _bombDueWorldTimes = new();
        private readonly List<SavedBombProjectile> _bombProjectiles = new();
        private readonly object _bombProjectileLock = new();
        private readonly HashSet<ChunkCoordinates> _bombProjectileChunkPins = new();
        private long _nextBombProjectileId = 1;

        /// <summary>Starts a fixed fuse for a newly placed bomb, and cancels it when that cell is
        /// changed to another block. Same-block metadata edits deliberately leave the deadline
        /// untouched; pre-v14 bombs without a timer remain inert.</summary>
        private void OnBombBlockChanged(int x, int y, int z, int blockId, int previousBlockId)
        {
            var position = (x, y, z);
            if (blockId != _idBomb)
            {
                _bombDueWorldTimes.Remove(position);
                return;
            }

            if (IsAuthoritative && previousBlockId != _idBomb && !_bombDueWorldTimes.ContainsKey(position))
                _bombDueWorldTimes[position] = WorldTime + BombFuseTicks;
        }

        private void StepBombFuses()
        {
            if (!IsAuthoritative || _bombDueWorldTimes.Count == 0) return;

            var positions = new List<(int X, int Y, int Z)>(_bombDueWorldTimes.Keys);
            foreach (var position in positions)
            {
                if (!_bombDueWorldTimes.TryGetValue(position, out long dueWorldTime)) continue;

                // Loaded edits (including direct simulation edits such as a zombie mining) cancel
                // here. An unloaded cell is retained until it is materialized at expiry.
                if (Chunks.TryGetLoadedBlock(position.X, position.Y, position.Z, out int loadedId)
                    && loadedId != _idBomb)
                {
                    _bombDueWorldTimes.Remove(position);
                    continue;
                }

                if (WorldTime < dueWorldTime) continue;
                DetonateBomb(position);
            }
        }

        private void DetonateBomb((int X, int Y, int Z) position)
        {
            // Materialize the source even when its chunk was streamed out before the fuse elapsed.
            int sourceLayer = ChunkManager.LayerForWorldY(position.Y);
            Chunks.GetOrCreateChunk(sourceLayer,
                WorldToChunkCoord(position.X), WorldToChunkCoord(position.Z));
            if (!Chunks.TryGetLoadedBlock(position.X, position.Y, position.Z, out int sourceId)
                || sourceId != _idBomb)
            {
                _bombDueWorldTimes.Remove(position);
                return;
            }

            // Remove the deadline before any edits. A blast can never re-enter this detonation.
            _bombDueWorldTimes.Remove(position);
            DetonateBombAt(position.X + 0.5, position.Y + 0.5, position.Z + 0.5, position);
        }

        private void DetonateBombProjectile(int projectileIndex)
        {
            SavedBombProjectile projectile;
            lock (_bombProjectileLock)
            {
                if (projectileIndex < 0 || projectileIndex >= _bombProjectiles.Count) return;
                projectile = _bombProjectiles[projectileIndex];
                _bombProjectiles.RemoveAt(projectileIndex);
            }

            // The projectile is removed first, so blast edits or another fuse can never detonate it
            // a second time. Its cube position is the center used for the blast sphere.
            DetonateBombAt(projectile.X, projectile.Y, projectile.Z, null);
        }

        private void DetonateBombAt(double centerX, double centerY, double centerZ,
            (int X, int Y, int Z)? sourceBlock)
        {
            int minX = (int)Math.Floor(centerX - BombBlastRadius - 0.5);
            int maxX = (int)Math.Floor(centerX + BombBlastRadius - 0.5);
            int minY = (int)Math.Floor(centerY - BombBlastRadius - 0.5);
            int maxY = (int)Math.Floor(centerY + BombBlastRadius - 0.5);
            int minZ = (int)Math.Floor(centerZ - BombBlastRadius - 0.5);
            int maxZ = (int)Math.Floor(centerZ + BombBlastRadius - 0.5);

            // Materialize each chunk intersecting the sphere before any reads or writes. Besides
            // handling normal X/Z seams this also routes cells through the correct vertical layer.
            var preparedChunks = new HashSet<ChunkCoordinates>();
            for (int y = minY; y <= maxY; y++)
            {
                if (!IsWorldYInBounds(y)) continue;
                int layer = ChunkManager.LayerForWorldY(y);
                for (int z = minZ; z <= maxZ; z++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        double dx = x + 0.5 - centerX;
                        double dy = y + 0.5 - centerY;
                        double dz = z + 0.5 - centerZ;
                        if (dx * dx + dy * dy + dz * dz > BombBlastRadius * BombBlastRadius) continue;
                        var chunk = new ChunkCoordinates(layer, WorldToChunkCoord(x), WorldToChunkCoord(z));
                        if (preparedChunks.Add(chunk)) Chunks.GetOrCreateChunk(layer, chunk.X, chunk.Z);
                    }
                }
            }

            var blastCells = new List<(int X, int Y, int Z, int BlockId, byte Meta)>();
            var chainBombs = new List<(int X, int Y, int Z, long DueWorldTime)>();
            for (int y = minY; y <= maxY; y++)
            {
                if (!IsWorldYInBounds(y)) continue;
                for (int z = minZ; z <= maxZ; z++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        double dx = x + 0.5 - centerX;
                        double dy = y + 0.5 - centerY;
                        double dz = z + 0.5 - centerZ;
                        if (dx * dx + dy * dy + dz * dz > BombBlastRadius * BombBlastRadius) continue;
                        if (!Chunks.TryGetLoadedBlockAndMeta(x, y, z, out int blockId, out byte meta)
                            || blockId == BlockRegistry.AirId || blockId == _idBedrock
                            || float.IsPositiveInfinity(BlockRegistry.HardnessOf(blockId))) continue;

                        bool isSourceCell = sourceBlock.HasValue && sourceBlock.Value.X == x
                            && sourceBlock.Value.Y == y && sourceBlock.Value.Z == z;
                        if (blockId == _idBomb && !isSourceCell)
                        {
                            long due = _bombDueWorldTimes.TryGetValue((x, y, z), out long existingDue)
                                ? existingDue : WorldTime + BombFuseTicks;
                            // Even a legacy or already-expired block gets a full simulation tick to
                            // exist as a launched projectile before it can detonate.
                            if (due <= WorldTime) due = WorldTime + 1;
                            chainBombs.Add((x, y, z, due));
                            continue;
                        }
                        blastCells.Add((x, y, z, blockId, meta));
                    }
                }
            }

            // Chain targets leave through the regular tracked edit path before becoming entities.
            // This sends their removal to clients and clears the cell's fuse timer exactly once.
            foreach (var target in chainBombs)
            {
                if (!Chunks.TryGetLoadedBlock(target.X, target.Y, target.Z, out int currentId)
                    || currentId != _idBomb) continue;
                if (ApplyBlockEdit(target.X, target.Y, target.Z, BlockRegistry.AirId, 0))
                    LaunchBombProjectile(target.X, target.Y, target.Z, target.DueWorldTime,
                        centerX, centerY, centerZ);
            }

            // Ordinary destructible blocks use the same tracked edit pipeline (ticks, remesh,
            // fluid/gravity, rails, support cleanup, and network broadcast), without mining drops.
            foreach (var cell in blastCells)
            {
                if (Chunks.TryGetLoadedBlock(cell.X, cell.Y, cell.Z, out int currentId)
                    && currentId != BlockRegistry.AirId
                    && currentId != _idBedrock
                    && !float.IsPositiveInfinity(BlockRegistry.HardnessOf(currentId)))
                {
                    ApplyBlockEdit(cell.X, cell.Y, cell.Z, BlockRegistry.AirId, 0);
                }
            }

            DamageExplosionTargets(centerX, centerY, centerZ);
        }

        private void LaunchBombProjectile(int x, int y, int z, long dueWorldTime,
            double blastX, double blastY, double blastZ)
        {
            double centerX = x + 0.5;
            double centerY = y + 0.5;
            double centerZ = z + 0.5;
            double dx = centerX - blastX;
            double dy = centerY - blastY;
            double dz = centerZ - blastZ;
            double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (length < 1e-6)
            {
                dx = 0;
                dy = 1;
                dz = 0;
                length = 1;
            }

            long id;
            lock (_bombProjectileLock)
            {
                id = _nextBombProjectileId++;
                _bombProjectiles.Add(new SavedBombProjectile
                {
                    Id = id,
                    X = centerX,
                    Y = centerY,
                    Z = centerZ,
                    VelocityX = dx / length * 8.0,
                    VelocityY = 6.0 + Math.Max(0.0, dy / length) * 2.0,
                    VelocityZ = dz / length * 8.0,
                    DueWorldTime = dueWorldTime,
                });
            }
        }

        private void StepBombProjectiles(float deltaSeconds)
        {
            if (!IsAuthoritative) return;
            double dt = Math.Clamp(deltaSeconds, 0.0, 0.1);
            lock (_bombProjectileLock)
            {
                for (int i = _bombProjectiles.Count - 1; i >= 0; i--)
                {
                    var projectile = _bombProjectiles[i];
                    if (WorldTime >= projectile.DueWorldTime
                        || projectile.Y < ChunkManager.DeepOriginY
                        || projectile.Y >= ChunkManager.SkyOriginY + ChunkManager.SkyHeight)
                    {
                        DetonateBombProjectile(i);
                        continue;
                    }

                    if (dt <= 0.0) continue;
                    projectile.VelocityY = Math.Max(-BombProjectileMaxFallSpeed,
                        projectile.VelocityY - BombProjectileGravity * dt);
                    projectile.VelocityY = Math.Min(BombProjectileMaxRiseSpeed, projectile.VelocityY);
                    double horizontalDrag = Math.Pow(0.5, dt * 0.8);
                    projectile.VelocityX *= horizontalDrag;
                    projectile.VelocityZ *= horizontalDrag;

                    double maxDistance = Math.Max(Math.Abs(projectile.VelocityX * dt),
                        Math.Max(Math.Abs(projectile.VelocityY * dt), Math.Abs(projectile.VelocityZ * dt)));
                    int steps = Math.Max(1, (int)Math.Ceiling(maxDistance / BombProjectileMaxSubstep));
                    bool landed = false;
                    for (int step = 0; step < steps; step++)
                    {
                        double stepSeconds = dt / steps;
                        double nextX = projectile.X + projectile.VelocityX * stepSeconds;
                        if (HasBombProjectileCollision(nextX, projectile.Y, projectile.Z))
                            projectile.VelocityX *= -0.2;
                        else projectile.X = nextX;

                        double nextZ = projectile.Z + projectile.VelocityZ * stepSeconds;
                        if (HasBombProjectileCollision(projectile.X, projectile.Y, nextZ))
                            projectile.VelocityZ *= -0.2;
                        else projectile.Z = nextZ;

                        double nextY = projectile.Y + projectile.VelocityY * stepSeconds;
                        if (HasBombProjectileCollision(projectile.X, nextY, projectile.Z))
                        {
                            if (projectile.VelocityY < 0.0)
                            {
                                landed = true;
                                break;
                            }
                            projectile.VelocityY *= -0.2;
                        }
                        else projectile.Y = nextY;
                    }

                    if (landed)
                    {
                        if (TryReplantBomb(projectile))
                        {
                            _bombProjectiles.RemoveAt(i);
                        }
                        else
                        {
                            DetonateBombProjectile(i);
                        }
                    }
                }
            }
            RefreshBombProjectileChunkPins();
        }

        private bool HasBombProjectileCollision(double centerX, double centerY, double centerZ)
        {
            int minX = (int)Math.Floor(centerX - BombProjectileHalfSize + BombProjectileCollisionEpsilon);
            int maxX = (int)Math.Floor(centerX + BombProjectileHalfSize - BombProjectileCollisionEpsilon);
            int minY = (int)Math.Floor(centerY - BombProjectileHalfSize + BombProjectileCollisionEpsilon);
            int maxY = (int)Math.Floor(centerY + BombProjectileHalfSize - BombProjectileCollisionEpsilon);
            int minZ = (int)Math.Floor(centerZ - BombProjectileHalfSize + BombProjectileCollisionEpsilon);
            int maxZ = (int)Math.Floor(centerZ + BombProjectileHalfSize - BombProjectileCollisionEpsilon);
            for (int y = minY; y <= maxY; y++)
            {
                if (!IsWorldYInBounds(y)) return true;
                int layer = ChunkManager.LayerForWorldY(y);
                for (int z = minZ; z <= maxZ; z++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        Chunks.GetOrCreateChunk(layer, WorldToChunkCoord(x), WorldToChunkCoord(z));
                        if (Chunks.TryGetLoadedBlock(x, y, z, out int blockId)
                            && BlockRegistry.IsSolid(blockId)) return true;
                    }
                }
            }
            return false;
        }

        private bool TryReplantBomb(SavedBombProjectile projectile)
        {
            int x = (int)Math.Floor(projectile.X);
            int z = (int)Math.Floor(projectile.Z);
            int y = (int)Math.Floor(projectile.Y - BombProjectileHalfSize + BombProjectileCollisionEpsilon);
            for (int attempt = 0; attempt < 8 && IsWorldYInBounds(y); attempt++, y++)
            {
                int layer = ChunkManager.LayerForWorldY(y);
                Chunks.GetOrCreateChunk(layer, WorldToChunkCoord(x), WorldToChunkCoord(z));
                if (!Chunks.TryGetLoadedBlock(x, y, z, out int blockId) || BlockRegistry.IsSolid(blockId))
                    continue;
                if (!ApplyBlockEdit(x, y, z, _idBomb, 0)) return false;
                // ApplyBlockEdit starts a new fuse on a newly placed bomb. Restore the projectile's
                // original absolute deadline so flight/landing never resets the countdown.
                _bombDueWorldTimes[(x, y, z)] = projectile.DueWorldTime;
                return true;
            }
            return false;
        }

        /// <summary>Loads and pins the current and near-future chunk footprint for each projectile.
        /// This keeps collision queries valid across X/Z seams and vertical layer transitions.</summary>
        private void RefreshBombProjectileChunkPins()
        {
            _bombProjectileChunkPins.Clear();
            lock (_bombProjectileLock)
            {
                foreach (var projectile in _bombProjectiles)
                {
                    double futureX = projectile.X + projectile.VelocityX * BombProjectilePinLookaheadSeconds;
                    double futureY = projectile.Y + projectile.VelocityY * BombProjectilePinLookaheadSeconds
                        - 0.5 * BombProjectileGravity * BombProjectilePinLookaheadSeconds * BombProjectilePinLookaheadSeconds;
                    double futureZ = projectile.Z + projectile.VelocityZ * BombProjectilePinLookaheadSeconds;
                    int minX = (int)Math.Floor(Math.Min(projectile.X, futureX) - 1.0);
                    int maxX = (int)Math.Floor(Math.Max(projectile.X, futureX) + 1.0);
                    int minZ = (int)Math.Floor(Math.Min(projectile.Z, futureZ) - 1.0);
                    int maxZ = (int)Math.Floor(Math.Max(projectile.Z, futureZ) + 1.0);
                    int minY = Math.Max(ChunkManager.DeepOriginY,
                        (int)Math.Floor(Math.Min(projectile.Y, futureY) - 1.0));
                    int maxY = Math.Min(ChunkManager.SkyOriginY + ChunkManager.SkyHeight - 1,
                        (int)Math.Floor(Math.Max(projectile.Y, futureY) + 1.0));
                    for (int y = minY; y <= maxY; y++)
                    {
                        int layer = ChunkManager.LayerForWorldY(y);
                        for (int cz = WorldToChunkCoord(minZ); cz <= WorldToChunkCoord(maxZ); cz++)
                        {
                            for (int cx = WorldToChunkCoord(minX); cx <= WorldToChunkCoord(maxX); cx++)
                            {
                                var coords = new ChunkCoordinates(layer, cx, cz);
                                _bombProjectileChunkPins.Add(coords);
                                if (!Chunks.TryGetLoadedChunk(coords, out _))
                                    Chunks.GetOrCreateChunk(layer, cx, cz);
                            }
                        }
                    }
                }
            }
        }

        internal IReadOnlySet<ChunkCoordinates> BombProjectileChunkPins => _bombProjectileChunkPins;

        public void RestoreBombFuses(IEnumerable<SavedBombFuse> fuses)
        {
            _bombDueWorldTimes.Clear();
            foreach (var fuse in fuses)
                _bombDueWorldTimes[(fuse.X, fuse.Y, fuse.Z)] = fuse.DueWorldTime;
        }

        /// <summary>Copies absolute due-world-time deadlines for a save snapshot.</summary>
        public List<SavedBombFuse> CaptureBombFuses()
        {
            var result = new List<SavedBombFuse>(_bombDueWorldTimes.Count);
            foreach (var entry in _bombDueWorldTimes)
            {
                result.Add(new SavedBombFuse
                {
                    X = entry.Key.X,
                    Y = entry.Key.Y,
                    Z = entry.Key.Z,
                    DueWorldTime = entry.Value,
                });
            }
            result.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X)
                : a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.Z.CompareTo(b.Z));
            return result;
        }

        public void RestoreBombProjectiles(IEnumerable<SavedBombProjectile> projectiles)
        {
            lock (_bombProjectileLock)
            {
                _bombProjectiles.Clear();
                _nextBombProjectileId = 1;
                foreach (var projectile in projectiles)
                {
                    var copy = CopyBombProjectile(projectile);
                    _bombProjectiles.Add(copy);
                    _nextBombProjectileId = Math.Max(_nextBombProjectileId, copy.Id + 1);
                }
            }
            RefreshBombProjectileChunkPins();
        }

        public List<SavedBombProjectile> CaptureBombProjectiles()
        {
            lock (_bombProjectileLock)
            {
                var result = new List<SavedBombProjectile>(_bombProjectiles.Count);
                foreach (var projectile in _bombProjectiles) result.Add(CopyBombProjectile(projectile));
                result.Sort((a, b) => a.Id.CompareTo(b.Id));
                return result;
            }
        }

        public List<FallingBlockData> CaptureBombProjectileRenderData()
        {
            lock (_bombProjectileLock)
            {
                var result = new List<FallingBlockData>(_bombProjectiles.Count);
                foreach (var projectile in _bombProjectiles)
                    result.Add(new FallingBlockData(_idBomb,
                        projectile.X - BombProjectileHalfSize,
                        projectile.Y - BombProjectileHalfSize,
                        projectile.Z - BombProjectileHalfSize));
                return result;
            }
        }

        private static SavedBombProjectile CopyBombProjectile(SavedBombProjectile projectile)
            => new()
            {
                Id = projectile.Id,
                X = projectile.X,
                Y = projectile.Y,
                Z = projectile.Z,
                VelocityX = projectile.VelocityX,
                VelocityY = projectile.VelocityY,
                VelocityZ = projectile.VelocityZ,
                DueWorldTime = projectile.DueWorldTime,
            };

        private static bool IsWorldYInBounds(int y)
            => y >= ChunkManager.DeepOriginY && y < ChunkManager.SkyOriginY + ChunkManager.SkyHeight;

        private void DamageExplosionTargets(double centerX, double centerY, double centerZ)
        {
            if (IsCreative) return;

            int localDamage = ExplosionDamageAt(LocalPlayer.Position.X, LocalPlayer.Position.Y, LocalPlayer.Position.Z,
                centerX, centerY, centerZ);
            if (localDamage > 0) DamagePlayer(localDamage, DeathCause.Explosion);

            PlayerState[] remotePlayers;
            lock (_remoteLock)
            {
                remotePlayers = new PlayerState[_remotePlayers.Count];
                _remotePlayers.Values.CopyTo(remotePlayers, 0);
            }
            foreach (var player in remotePlayers)
            {
                int damage = ExplosionDamageAt(player.Position.X, player.Position.Y, player.Position.Z,
                    centerX, centerY, centerZ);
                if (damage > 0) DamagePlayer(player, damage, DeathCause.Explosion);
            }

            foreach (var renderable in Entities.Mobs)
            {
                if (renderable is not MobEntity mob || mob is CartEntity || mob is BoatEntity
                    || mob.Removed || mob.IsDead) continue;
                var mobPosition = mob.Position;
                double damageX = mobPosition.X;
                double damageY = mobPosition.Y + mob.Height * 0.5;
                double damageZ = mobPosition.Z;
                int damage = ExplosionDamageAt(damageX, damageY, damageZ, centerX, centerY, centerZ);
                if (damage > 0) mob.Damage(damage, centerX, centerZ);
            }
        }

        private static int ExplosionDamageAt(double x, double y, double z,
            double centerX, double centerY, double centerZ)
        {
            double dx = x - centerX;
            double dy = y - centerY;
            double dz = z - centerZ;
            double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (distance > BombBlastRadius) return 0;
            double impact = 1.0 - distance / BombBlastRadius;
            return impact <= 0 ? 0 : Math.Max(1, (int)Math.Ceiling(impact * 10.0));
        }
    }
}
