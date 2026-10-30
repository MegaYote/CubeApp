using System;
using System.Collections.Generic;
using System.Numerics;

namespace Cubuild
{
    public sealed partial class GameWorld : IDisposable
    {
        // slice every RegenIntervalBase seconds, plus a random 1..2s fluctuation per slice.
        private const float RegenDelay = 15f;
        private const float RegenIntervalBase = 8.5f;
        private readonly Random _regenRandom = new();
        private readonly SteveShelterCoordinator _steveShelterCoordinator = new();

        // Steps dropped items: gravity, ground settle, player pickup, and despawn. Purely
        // survival-facing - creative breaks don't even spawn drops.
        private void StepItemDrops(float dt)
        {
            for (int i = _droppedItems.Count - 1; i >= 0; i--)
            {
                var d = _droppedItems[i];
                d.Age += dt;
                if (d.Age > ItemDropDespawnTime)
                {
                    _droppedItems.RemoveAt(i);
                    continue;
                }

                // Magnet phase: flying toward the player. Ignores gravity, homes in fast, and
                // gets collected on arrival - the classic "item flies to you" pickup.
                if (d.FlyTime > 0f)
                {
                    d.FlyTime -= dt;
                    double feetX = LocalPlayer.Position.X;
                    double feetY = LocalPlayer.Position.Y - EyeHeight + 0.4;
                    double feetZ = LocalPlayer.Position.Z;
                    double hx = feetX - d.Position.X;
                    double hy = feetY - d.Position.Y;
                    double hz = feetZ - d.Position.Z;
                    double dist = Math.Sqrt(hx * hx + hy * hy + hz * hz);
                    if (dist <= 0.25 || d.FlyTime <= 0f)
                    {
                        // Only destroy the drop for what we actually managed to pocket. A full
                        // (or partly full) inventory leaves the remainder lying on the ground
                        // instead of silently eating it; the drop keeps flying and retries as
                        // slots free up, and despawns normally if the player walks away.
                        int added = CollectItem(d.ItemId, d.Count);
                        if (added >= d.Count) _droppedItems.RemoveAt(i);
                        else if (added > 0) d.Count -= added;
                        continue;
                    }
                    double speed = Math.Min(18.0, 7.0 + (PickupFlyDuration - d.FlyTime) * 40.0);
                    d.Velocity = new Point3D(hx / dist * speed, hy / dist * speed, hz / dist * speed);
                    d.Position += d.Velocity * dt;
                    d.SpinSpeed = Math.Max(d.SpinSpeed, 14f); // spin up while flying to you
                    continue;
                }

                // Pickup trigger: within reach of the player, after the grace period. Instead of
                // vanishing instantly, the drop starts flying to the player.
                if (d.Age > ItemDropPickupDelay && !IsCreative)
                {
                    double feetX = LocalPlayer.Position.X;
                    double feetY = LocalPlayer.Position.Y - EyeHeight;
                    double feetZ = LocalPlayer.Position.Z;
                    double centerY = d.Position.Y + 0.2;
                    if (Math.Abs(d.Position.X - feetX) < 1.2
                        && Math.Abs(d.Position.Z - feetZ) < 1.2
                        && centerY > feetY - 0.5 && centerY < feetY + 2.0)
                    {
                        d.FlyTime = PickupFlyDuration;
                        continue;
                    }
                }

                // Gravity + horizontal drag.
                d.Velocity = new Point3D(
                    d.Velocity.X * (float)Math.Pow(0.5, dt * 4.0),
                    d.Velocity.Y - Gravity * dt,
                    d.Velocity.Z * (float)Math.Pow(0.5, dt * 4.0));
                
                // Horizontal movement with wall collision
                double newX = d.Position.X + d.Velocity.X * dt;
                double newZ = d.Position.Z + d.Velocity.Z * dt;
                
                // Check X collision
                int checkBx = (int)Math.Floor(newX - 0.2);
                int checkBy = (int)Math.Floor(d.Position.Y);
                int checkBz = (int)Math.Floor(d.Position.Z);
                if (Chunks.TryGetLoadedBlock(checkBx, checkBy, checkBz, out int wallId) && BlockRegistry.IsSolid(wallId))
                {
                    newX = d.Position.X; // cancel X movement
                    d.Velocity = new Point3D(-d.Velocity.X * 0.2, d.Velocity.Y, d.Velocity.Z); // bounce
                }
                
                // Check Z collision
                checkBx = (int)Math.Floor(newX);
                checkBz = (int)Math.Floor(newZ + 0.2);
                if (Chunks.TryGetLoadedBlock(checkBx, checkBy, checkBz, out int zWallId) && BlockRegistry.IsSolid(zWallId))
                {
                    newZ = d.Position.Z; // cancel Z movement
                    d.Velocity = new Point3D(d.Velocity.X, d.Velocity.Y, -d.Velocity.Z * 0.2); // bounce
                }
                
                d.Position = new Point3D(newX, d.Position.Y + d.Velocity.Y * dt, newZ);

                // Tumble while airborne: rotate the quaternion around the spin axis, with a
                // little drag so the spin dies down naturally.
                if (d.SpinSpeed > 0.01f)
                {
                    float angStep = d.SpinSpeed * dt;
                    float c = (float)Math.Cos(angStep * 0.5);
                    float s = (float)Math.Sin(angStep * 0.5);
                    float qx = d.SpinAxisX * s, qy = d.SpinAxisY * s, qz = d.SpinAxisZ * s, qw = c;
                    float nx = qw * d.RotX + qx * d.RotW + qy * d.RotZ - qz * d.RotY;
                    float ny = qw * d.RotY - qx * d.RotZ + qy * d.RotW + qz * d.RotX;
                    float nz = qw * d.RotZ + qx * d.RotY - qy * d.RotX + qz * d.RotW;
                    float nw = qw * d.RotW - qx * d.RotX - qy * d.RotY - qz * d.RotZ;
                    // Renormalize every step: float drift would otherwise grow the quaternion
                    // magnitude and shear/stretch the rendered mesh.
                    float lenInv = 1f / (float)Math.Sqrt(nx * nx + ny * ny + nz * nz + nw * nw);
                    d.RotX = nx * lenInv; d.RotY = ny * lenInv; d.RotZ = nz * lenInv; d.RotW = nw * lenInv;
                    d.SpinSpeed *= (float)Math.Pow(0.5, dt * 2.0);
                }

                // Settle on the first solid block below.
                int bx = (int)Math.Floor(d.Position.X);
                int by = (int)Math.Floor(d.Position.Y);
                int bz = (int)Math.Floor(d.Position.Z);
                if (Chunks.TryGetLoadedBlock(bx, by, bz, out int groundId) && BlockRegistry.IsSolid(groundId))
                {
                    d.Position = new Point3D(d.Position.X, by + 1.0, d.Position.Z);
                    d.Velocity = new Point3D(d.Velocity.X, 0, d.Velocity.Z);
                    d.SpinSpeed = 0f; // it lands and stops tumbling
                }

                // If a solid block appears ABOVE the item, push it up - but only if actually buried/inside it.
                // Old code checked column above regardless of gap, so mining up would wrongly teleport
                // an item that was just touching the underside (with 0.5 gap) to the top of the ceiling.
                int aboveY = by + 1;
                bool isInsideAbove = d.Position.Y + 0.01 >= aboveY && d.Position.Y < aboveY + 1;
                if (isInsideAbove && Chunks.TryGetLoadedBlock(bx, aboveY, bz, out int aboveId) && BlockRegistry.IsSolid(aboveId))
                {
                    // Find the next empty space above
                    int newY = aboveY + 1;
                    while (Chunks.TryGetLoadedBlock(bx, newY, bz, out int nextId) && BlockRegistry.IsSolid(nextId))
                    {
                        newY++;
                    }
                    d.Position = new Point3D(d.Position.X, newY + 0.01, d.Position.Z); // slight offset to prevent re-collision
                    d.Velocity = new Point3D(d.Velocity.X, 0, d.Velocity.Z);
                }

                else if (d.Position.Y < ChunkManager.GroundOriginY - 10)
                {
                    _droppedItems.RemoveAt(i); // fell out of the world
                }
            }
        }

        private void StepRegen(float dt)
        {
            var p = LocalPlayer;
            if (p.Health <= 0) return; // dead players don't heal
            if (p.Health >= 10)
            {
                p.TimeSinceDamage = 0f;
                p.RegenAccumulator = 0f;
                return;
            }

            p.TimeSinceDamage += dt;
            if (p.TimeSinceDamage < RegenDelay) return;

            p.RegenAccumulator += dt;
            if (p.RegenAccumulator >= p.NextRegenInterval)
            {
                p.RegenAccumulator = 0f;
                p.Health = Math.Min(10, p.Health + 1);
                // Random fluctuation of 1..2 seconds per slice.
                p.NextRegenInterval = RegenIntervalBase + 1f + (float)_regenRandom.NextDouble();
            }
        }

        /// <summary>Advance the simulation by one frame. Pure logic; no rendering here.</summary>
        public void StepSimulation(TickInputState tickInput, float deltaSeconds)
        {
            // Day/night clock: worldTime advances at a fixed 20 ticks/sec (worldTime
            // advances once per tick, full cycle = 24000 ticks = 20 minutes). Accumulate the
            // fractional delta so time flows at exactly 20 tps regardless of frame rate.
            // Math.Round(deltaSeconds*20) froze the clock at high FPS (0.333 rounds to 0 every
            // frame), so the sun/moon/stars never rotated.
            // Day/night length: the 24000-tick cycle now spans 30 minutes instead of 20
            // (each of day and night is 5 minutes longer), so the clock ticks at
            // 24000 / 1800s = 13.3333 ticks/sec.
            _worldTimeAccumulator += deltaSeconds * (24000.0 / 1800.0);
            long advance = (long)_worldTimeAccumulator;
            WorldTime += advance;
            _worldTimeAccumulator -= advance;
            StepSapTorchBurnTimers();
            StepBombFuses();
            StepBombProjectiles(deltaSeconds);
            StepRegen(deltaSeconds);
            if (LocalPlayer.HurtTimer > 0f)
                LocalPlayer.HurtTimer = Math.Max(0f, LocalPlayer.HurtTimer - deltaSeconds);
            // Advance the death roll while dead (capped so it doesn't spin forever).
            if (LocalPlayer.Health <= 0)
                LocalPlayer.DeathTimer = Math.Min(1f, LocalPlayer.DeathTimer + deltaSeconds);
            StepItemDrops(deltaSeconds);
            // Encumbrance from carried ore. Recomputed every tick so a pickup or a drop
            // changes the player's speed right away. Creative is a sandbox: never encumbered.
            if (IsCreative) LocalPlayer.WeightLevel = 0;
            else
            {
                UpdateCarryWeight();
                LocalPlayer.WeightLevel = CarryWeightLevel;
            }
            BlockTicks?.Tick(deltaSeconds);
            UpdateLeafDecay(deltaSeconds);
            UpdateRailSag(deltaSeconds);
            // While riding a cart or a boat, THAT vehicle is what moves: StepRidingCart /
            // StepRiding steer it and pin the player's eye to the hull instead of running the
            // normal walk/fall/collide path.
            if (IsRidingCart) StepRidingCart(tickInput, deltaSeconds);
            else if (IsRiding) StepRiding(tickInput, deltaSeconds);
            else StepPlayer(LocalPlayer, tickInput, deltaSeconds);

            // A block that ends up inside the player (falling sand, a badly timed placement) pushes
            // them out sideways, or crushes them when boxed in.
            ResolvePlayerEmbedding(LocalPlayer, deltaSeconds);

            // Third-person body yaw: the body lags the look direction (slowly while idle, faster
            // while moving/flying) so the head can swivel ahead of the body like a real person.
            if (LocalPlayer.Health > 0)
            {
                float camYaw = LocalPlayer.Yaw * (float)Math.PI / 180f;
                float bodyYaw = LocalPlayer.BodyYaw;
                float delta = NormalizeRadians(camYaw - bodyYaw);
                float turnRate = (LocalPlayer.WalkAmount > 0.05f || LocalPlayer.NoclipMode || LocalPlayer.CreativeFlightMode) ? 9f : 3f;
                float maxStep = turnRate * deltaSeconds;
                LocalPlayer.BodyYaw = Math.Abs(delta) <= maxStep ? camYaw : bodyYaw + Math.Sign(delta) * maxStep;
            }
            // Player body center for mob separation: AABB runs from eye - EyeHeight up to
            // + PlayerHeight, so the center sits at eye - EyeHeight + half height.
            Entities.PlayerBodyCenter = new Point3D(
                LocalPlayer.Position.X,
                LocalPlayer.Position.Y - EyeHeight + PlayerHeight * 0.5,
                LocalPlayer.Position.Z);
            if (IsAuthoritative)
            {
                foreach (var mob in Entities.Mobs)
                    if (mob is SteveMob steve && !steve.IsDead) steve.PrepareShelterWork(this, deltaSeconds);
                _steveShelterCoordinator.Update(this, Entities.Mobs, deltaSeconds);
            }
            Entities.Update(deltaSeconds, LocalPlayer.Position, true, LocalPlayer.Health > 0);
            if (IsAuthoritative)
            {
                foreach (var mob in Entities.Mobs)
                    if (mob is SteveMob steve && !steve.IsDead && !steve.Removed) steve.FinishShelterWork(this, deltaSeconds);
            }
            LastEntityMs = Entities.LastUpdateMs;
            // The train pass inside Entities.Update places a hauled carriage, which may be the very
            // cart the player is sitting in, so re-pin the eye now that it has moved (see PinRiderEye).
            if (IsRidingCart) PinRiderEye(deltaSeconds);
            int chunkX = WorldToChunkCoord(LocalPlayer.Position.X);
            int chunkZ = WorldToChunkCoord(LocalPlayer.Position.Z);
            // Request/unload scans cost O(radius^2) + O(loadedChunks); only run them when the
            // player actually enters a new chunk column, the render distance changed, OR the
            // player crosses into a different layer (digging straight down in one column
            // keeps X/Z constant but must still wake the new layer).
            double py = LocalPlayer.Position.Y;
            int playerLayer = ChunkManager.LayerForWorldY((int)py);
            bool crossedLayer = playerLayer != _lastPlayerLayer;
            bool streamCurrentLayer = _forceChunkStream || chunkX != _lastStreamChunkX || chunkZ != _lastStreamChunkZ || crossedLayer;
            int adjacentLayer = ChunkManager.AdjacentLayerNearBoundary(playerLayer, py);
            bool refreshVerticalSupport = streamCurrentLayer
                || adjacentLayer != _lastAdjacentBoundaryLayer
                || _verticalBoundaryRefreshTimer <= 0f;

            if (streamCurrentLayer)
            {
                _forceChunkStream = false;
                _lastStreamChunkX = chunkX;
                _lastStreamChunkZ = chunkZ;
                _lastPlayerLayer = playerLayer;
                // Only stream the chunk layer the player is standing in — deep, ground, or sky.
                // The other two layers sit idle until the player crosses into them, saving CPU
                // and keeping generation focused on the one layer that matters right now.
                Chunks.RequestChunksAround(chunkX, chunkZ, ChunkRenderRadius, LocalPlayer.Position, playerLayer);
                var unloaded = Chunks.UnloadChunksOutside(chunkX, chunkZ, ChunkRenderRadius,
                    BombProjectileChunkPins);
                foreach (var uc in unloaded) ChunkUnloaded?.Invoke(uc);
            }

            // Keep the normal render-radius disk exclusive to the player's current layer. A
            // throttled camera-ray probe runs in every layer; near a seam it also queues a tiny
            // adjacent-layer prefetch ring, even when the ray does not find an opening.
            if (refreshVerticalSupport)
            {
                Chunks.RequestVerticalBoundarySupport(playerLayer, chunkX, chunkZ,
                    LocalPlayer.Position, GetCameraForward());
                _verticalBoundaryRefreshTimer = VerticalBoundaryRefreshSeconds;
            }
            else _verticalBoundaryRefreshTimer = Math.Max(0f, _verticalBoundaryRefreshTimer - deltaSeconds);
            _lastAdjacentBoundaryLayer = adjacentLayer;
            UpdateHighFill();
        }

        private int _lastPlayerLayer = ChunkManager.GroundLayer;
        private int _lastAdjacentBoundaryLayer = -1;
        private const float VerticalBoundaryRefreshSeconds = 0.5f;
        private float _verticalBoundaryRefreshTimer;

        /// <summary>Day/night clock in world ticks. Full cycle = 24000 ticks.</summary>
        public long WorldTime { get; private set; }

        /// <summary>Restores the day/night clock from a save.</summary>
        public void SetWorldTime(long ticks) => WorldTime = Math.Max(0, ticks);

        }
}
