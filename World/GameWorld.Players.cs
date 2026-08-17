using System;
using System.Collections.Generic;
using System.Numerics;

namespace Cubuild
{
    public sealed partial class GameWorld : IDisposable
    {
        public PlayerState AddRemotePlayer(int clientId)
        {
            lock (_remoteLock)
            {
                var state = new PlayerState();
                _remotePlayers[clientId] = state;
                return state;
            }
        }

        public bool TryGetRemotePlayer(int clientId, out PlayerState state)
        {
            lock (_remoteLock) return _remotePlayers.TryGetValue(clientId, out state!);
        }

        public void RemoveRemotePlayer(int clientId)
        {
            lock (_remoteLock) _remotePlayers.Remove(clientId);
        }

        // ------------------------------------------------------------------
        // lifecycle
        // ------------------------------------------------------------------

        public void EnsureVisibleChunks() => Chunks.EnsureChunksAround(
            WorldToChunkCoord(LocalPlayer.Position.X), WorldToChunkCoord(LocalPlayer.Position.Z), SpawnSyncRadius);

        public void PlaceCameraAtSafeSpawn()
        {
            if (SpawnPoint.HasValue)
            {
                LocalPlayer.Position = SpawnPoint.Value;
            }
            else
            {
                var spawn = FindSafeSpawnPosition();
                LocalPlayer.Position = spawn ?? new Point3D(0.5, 1.0 + EyeHeight, 0.5);
            }
            LocalPlayer.Velocity = new Point3D(0, 0, 0);
            LocalPlayer.Grounded = true;
        }

        /// <summary>
        /// Picks the world's default spawn point: a RANDOM spot within a ring near the world origin
        /// whose surface block is grass, grass_spreading, or sand, above sea level, with clear air
        /// for the player to stand in. Called once before the player enters the world; every respawn
        /// returns here. SpawnPoint stores the EYE position (matching LocalPlayer.Position).
        /// </summary>
        public bool SelectWorldSpawn()
        {
            int grassId = BlockRegistry.GetId("grass");
            int grassSpreadId = BlockRegistry.GetId("grass_spreading");
            int sandId = BlockRegistry.GetId("sand");
            var rand = new Random();

            // Try random spots in expanding-ish rings out to ~400 blocks from the origin - the
            // origin itself is often ocean, so land can be farther away. The cheap surface
            // estimator rejects ocean columns BEFORE generating any chunk, so the wide scan stays
            // fast.
            for (int attempt = 0; attempt < 2048; attempt++)
            {
                int range = 4 + rand.Next(396); // 4..399 blocks from origin
                double ang = rand.NextDouble() * Math.PI * 2.0;
                int wx = (int)Math.Round(Math.Cos(ang) * range);
                int wz = (int)Math.Round(Math.Sin(ang) * range);

                // Cheap reject: below/at sea level = ocean floor, never spawn there.
                if (ChunkProvider != null && ChunkProvider.EstimateSurfaceHeightAt(wx, wz) < 1) continue;

                // Make sure the chunk exists so the surface scan sees real terrain.
                Chunks.GetOrCreateChunk(WorldToChunkCoord(wx), WorldToChunkCoord(wz));
                int surfaceY = FindSurfaceWorldY(wx, wz);
                if (surfaceY < 0) continue; // below sea / no ground

                int surfaceBlock = Chunks.GetBlockAt(wx, surfaceY, wz);
                if (surfaceBlock != grassId && surfaceBlock != grassSpreadId && surfaceBlock != sandId) continue;

                // Must be open air above (feet just above the surface block, head above that), no ceiling.
                if (Chunks.GetBlockAt(wx, surfaceY + 1, wz) != BlockRegistry.AirId) continue;
                if (Chunks.GetBlockAt(wx, surfaceY + 2, wz) != BlockRegistry.AirId) continue;

                // Player AABB must be collision-free standing here. Eye = just above the block top +
                // eye height (matching FindSafeSpawnPosition's convention).
                double px = wx + 0.5, pz = wz + 0.5;
                var eye = new Point3D(px, surfaceY + 0.01 + EyeHeight, pz);
                if (IsPlayerColliding(eye)) continue;

                SpawnPoint = eye;
                return true;
            }

            // Fallback: no grass/sand found in the wide ring - use the old height-hunting search.
            var fallback = FindSafeSpawnPosition();
            if (fallback.HasValue)
            {
                SpawnPoint = fallback.Value;
                return true;
            }

            SpawnPoint = new Point3D(0.5, 1.0 + EyeHeight, 0.5);
            return true;
        }

        /// <summary>
        /// Teleports the local player to the nearest location of the given biome (from the biome
        /// teleport menu). Searches outward in expanding rings around the player's current chunk;
        /// for the first chunk whose biome label matches, it finds a safe surface spot and moves
        /// the camera there.
        /// </summary>
        public void TeleportToNearestBiome(string biomeName)
        {
            if (ChunkProvider == null) return;

            int playerChunkX = WorldToChunkCoord(LocalPlayer.Position.X);
            int playerChunkZ = WorldToChunkCoord(LocalPlayer.Position.Z);

            for (int radius = 0; radius <= 64; radius++)
            {
                // Walk the ring at this radius (square ring; inside the ring was checked at a
                // smaller radius already, so scanning the whole square would re-check a lot).
                for (int dx = -radius; dx <= radius; dx++)
                {
                    for (int dz = -radius; dz <= radius; dz++)
                    {
                        if (radius > 0 && Math.Abs(dx) != radius && Math.Abs(dz) != radius) continue;

                        int wx = (playerChunkX + dx) * ChunkManager.ChunkSize;
                        int wz = (playerChunkZ + dz) * ChunkManager.ChunkSize;
                        string biome = ChunkProvider.BiomeNameAt(wx, wz);
                        if (!string.Equals(biome, biomeName, StringComparison.OrdinalIgnoreCase)) continue;

                        // Found a matching chunk - try to land safely somewhere in it.
                        if (TryFindSafeSpotInChunk(wx, wz, out var eye))
                        {
                            LocalPlayer.Position = eye;
                            LocalPlayer.Velocity = new Point3D(0, 0, 0);
                            LocalPlayer.Grounded = true;
                            return;
                        }
                    }
                }
            }
        }

        // Teleports the local player to the Great Pyramid if this world has one (most don't),
        // otherwise to the first regular pyramid. Pyramids are SOLID brick, so we can't drop them
        // on the center column - land just outside the base instead so the monument is in view.
        public void TeleportToPyramid()
        {
            if (ChunkProvider == null) return;

            var great = ChunkProvider.Pyramids;
            if (great != null && great.Exists)
            {
                TeleportNear(great.Center.X + great.HalfWidth + 60, great.Center.Z);
                return;
            }

            var regulars = ChunkProvider.RegularPyramids?.Pyramids;
            if (regulars != null && regulars.Count > 0)
            {
                var p = regulars[0];
                TeleportNear(p.CenterX + p.HalfWidth + 40, p.CenterZ);
            }
        }

        private void TeleportNear(int worldX, int worldZ)
        {
            if (TryFindSafeSpotInChunk(worldX, worldZ, out var eye))
            {
                LocalPlayer.Position = eye;
                LocalPlayer.Velocity = new Point3D(0, 0, 0);
                LocalPlayer.Grounded = true;
            }
        }

        // Finds a safe landing spot in a biome chunk using the terrain generator's cheap surface
        // estimate (NO full chunk generation - that would stall the main thread for a far-away
        // biome). Returns the eye position. The player may fall a block or two after landing as the
        // estimate is slightly imprecise, which is fine.
        private bool TryFindSafeSpotInChunk(int chunkWorldX, int chunkWorldZ, out Point3D eye)
        {
            eye = default;

            // Ocean basins are underwater, so land the player at the WATER SURFACE (sea level)
            // instead of the basin floor - they shouldn't teleport to the bottom of the sea.
            if (string.Equals(ChunkProvider.BiomeNameAt(chunkWorldX, chunkWorldZ), "Ocean", StringComparison.OrdinalIgnoreCase))
            {
                double px = chunkWorldX + 0.5;
                double pz = chunkWorldZ + 0.5;
                // Sea level is at local Y 64 of the terrain band, which maps to world 0.
                double feetY = 0.0 + 0.01;
                eye = new Point3D(px, feetY + EyeHeight, pz);
                return true;
            }

            int surfaceY = ChunkProvider.EstimateSurfaceHeightAt(chunkWorldX, chunkWorldZ);
            if (surfaceY < ChunkManager.WorldOriginY) return false;

            double px2 = chunkWorldX + 0.5;
            double pz2 = chunkWorldZ + 0.5;
            // Feet just above the surface; eye = feet + EyeHeight.
            double feetY2 = surfaceY + 0.01;
            eye = new Point3D(px2, feetY2 + EyeHeight, pz2);
            return true;
        }

        public void SetSelectedSlot(int slot)
        {
            if (slot < 0 || slot >= HotbarSlots) return;
            SelectedSlot = slot;
            SelectedBlock = Hotbar[slot];
        }

        public void ApplyLookInput(Vector2 lookDelta) => ApplyLookInput(LocalPlayer, lookDelta);

        public void ApplyLookInput(PlayerState p, Vector2 lookDelta)
        {
            p.Yaw -= lookDelta.X;
            p.Yaw = NormalizeYaw(p.Yaw);
            p.Pitch = Math.Clamp(p.Pitch - lookDelta.Y, -89f, 89f);
        }

        /// <summary>
        /// Applies damage to the local player (mob hits, test key, falls...). Clamps at 0, resets
        /// the regen timer so healing has to wait the full delay again, and records the death cause
        /// when the hit drops health to 0.
        /// </summary>
        public void DamagePlayer(int amount, DeathCause cause = DeathCause.Unknown)
        {
            // Creative players are invulnerable: no mob hits, no falls, no damage.
            if (IsCreative) return;
            if (amount <= 0) return;
            LocalPlayer.Health = Math.Max(0, LocalPlayer.Health - amount);
            LocalPlayer.TimeSinceDamage = 0f;
            LocalPlayer.RegenAccumulator = 0f;
            // Hurt flash + flail on the third-person model (same 0.2s as the duck).
            LocalPlayer.HurtTimer = Math.Max(LocalPlayer.HurtTimer, 0.2f);
            if (LocalPlayer.Health <= 0)
            {
                LocalPlayer.DeathCause = cause;
                // Start the death roll (direction random, like mobs that die without an attacker).
                if (LocalPlayer.DeathTimer <= 0f)
                    LocalPlayer.DeathRollDir = _regenRandom.Next(2) == 0 ? -1f : 1f;
                LocalPlayer.DeathTimer += 1f / 60f;
            }
        }

        // Natural regeneration: after RegenDelay seconds without damage the player heals one heart
    }
}