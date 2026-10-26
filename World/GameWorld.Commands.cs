using System;
using System.Collections.Generic;

namespace Cubuild
{
    public sealed partial class GameWorld
    {
        private const int TeleportStreamMarginChunks = 34;
        private const double TeleportGroundClearance = 0.01;
        private const double TeleportSupportTolerance = 0.025;

        private static readonly long MinTeleportChunkCoordinate =
            (long)int.MinValue / ChunkManager.ChunkSize + TeleportStreamMarginChunks;
        private static readonly long MaxTeleportChunkCoordinate =
            (long)int.MaxValue / ChunkManager.ChunkSize - TeleportStreamMarginChunks;

        private static int WorldMaxYExclusive => ChunkManager.SkyOriginY + ChunkManager.SkyHeight;
        private static int WorldMinY => ChunkManager.DeepOriginY;

        /// <summary>
        /// Teleports to a numeric feet height after making every chunk touched by the player body
        /// real and checking the actual collision boxes at that exact destination.
        /// </summary>
        public bool TryTeleportPlayerToFeet(double x, double feetY, double z, out string error)
        {
            error = string.Empty;
            if (!TryValidateHorizontalCoordinate(x, out _) || !TryValidateHorizontalCoordinate(z, out _))
            {
                error = "X/Z exceed the supported integer or chunk coordinate range.";
                return false;
            }

            if (!double.IsFinite(feetY) || feetY < WorldMinY
                || feetY + PlayerHeight > WorldMaxYExclusive)
            {
                error = $"Feet Y must keep the player inside the supported world height ({WorldMinY} to {WorldMaxYExclusive - PlayerHeight:0.###}).";
                return false;
            }

            var eyePosition = new Point3D(x, feetY + EyeHeight, z);
            EnsureTeleportBodyChunks(eyePosition);
            if (IsTeleportPositionColliding(eyePosition))
            {
                error = "That destination intersects solid blocks; teleport was not applied.";
                return false;
            }

            ApplyTeleport(eyePosition, HasSolidSupportAtFeet(x, feetY, z));
            return true;
        }

        /// <summary>
        /// Finds a standing position at the requested X/Z by scanning generated block collision
        /// geometry, then verifies the complete player AABB and its support before moving.
        /// No terrain-height estimate or unsafe fallback is used.
        /// </summary>
        public bool TryTeleportPlayerToTerrain(double x, double z, out string error)
        {
            error = string.Empty;
            if (!TryValidateHorizontalCoordinate(x, out _) || !TryValidateHorizontalCoordinate(z, out _))
            {
                error = "X/Z exceed the supported integer or chunk coordinate range.";
                return false;
            }

            int blockMinX = (int)Math.Floor(x - PlayerRadius);
            int blockMaxX = (int)Math.Floor(x + PlayerRadius);
            int blockMinZ = (int)Math.Floor(z - PlayerRadius);
            int blockMaxZ = (int)Math.Floor(z + PlayerRadius);
            EnsureTeleportSurfaceChunks(blockMinX, blockMaxX, blockMinZ, blockMaxZ);

            double minX = x - PlayerRadius;
            double maxX = x + PlayerRadius;
            double minZ = z - PlayerRadius;
            double maxZ = z + PlayerRadius;
            double bestFeetY = double.NegativeInfinity;
            Point3D bestEyePosition = default;

            for (int worldY = WorldMaxYExclusive - 1; worldY >= WorldMinY; worldY--)
            {
                // Anything below this block row cannot produce a higher standing position.
                if (worldY + 1.0 + TeleportGroundClearance <= bestFeetY) break;

                for (int blockX = blockMinX; blockX <= blockMaxX; blockX++)
                for (int blockZ = blockMinZ; blockZ <= blockMaxZ; blockZ++)
                {
                    if (!Chunks.TryGetLoadedBlockAndMeta(blockX, worldY, blockZ, out int blockId, out byte meta)
                        || !BlockRegistry.IsSolid(blockId)) continue;

                    var boxes = GetBlockCollisionBoxes(blockId, meta);
                    foreach (var box in boxes)
                    {
                        double overlapX = Math.Min(maxX, blockX + box.maxX) - Math.Max(minX, blockX + box.minX);
                        double overlapZ = Math.Min(maxZ, blockZ + box.maxZ) - Math.Max(minZ, blockZ + box.minZ);
                        if (overlapX <= 0.05 || overlapZ <= 0.05) continue;

                        double feetY = worldY + box.maxY + TeleportGroundClearance;
                        if (feetY <= bestFeetY || feetY < WorldMinY
                            || feetY + PlayerHeight > WorldMaxYExclusive) continue;

                        var eyePosition = new Point3D(x, feetY + EyeHeight, z);
                        if (IsTeleportPositionColliding(eyePosition)
                            || !HasSolidSupportAtFeet(x, feetY, z)) continue;

                        bestFeetY = feetY;
                        bestEyePosition = eyePosition;
                    }
                }
            }

            if (double.IsNegativeInfinity(bestFeetY))
            {
                error = "No verified collision-free standing position above solid terrain exists at that X/Z.";
                return false;
            }

            ApplyTeleport(bestEyePosition, grounded: true);
            return true;
        }

        /// <summary>Forces the normal streaming pass to refresh around the teleported player.</summary>
        public void NotifyPlayerTeleported() => _forceChunkStream = true;

        private static bool TryValidateHorizontalCoordinate(double coordinate, out int blockCoordinate)
        {
            blockCoordinate = 0;
            if (!double.IsFinite(coordinate)) return false;

            double chunkCoordinate = Math.Floor(coordinate / ChunkManager.ChunkSize);
            if (chunkCoordinate < MinTeleportChunkCoordinate || chunkCoordinate > MaxTeleportChunkCoordinate)
                return false;

            double floored = Math.Floor(coordinate);
            if (floored < int.MinValue || floored > int.MaxValue) return false;
            blockCoordinate = (int)floored;
            return true;
        }

        private void EnsureTeleportBodyChunks(Point3D eyePosition)
        {
            double feetY = eyePosition.Y - EyeHeight;
            int minX = (int)Math.Floor(eyePosition.X - PlayerRadius);
            int maxX = (int)Math.Floor(eyePosition.X + PlayerRadius);
            int minZ = (int)Math.Floor(eyePosition.Z - PlayerRadius);
            int maxZ = (int)Math.Floor(eyePosition.Z + PlayerRadius);
            int minY = (int)Math.Floor(feetY);
            int maxY = (int)Math.Floor(feetY + PlayerHeight - 1e-5);
            EnsureChunksForBounds(minX, maxX, minY, maxY, minZ, maxZ);
            // Ground support may be in the block row immediately below the feet (including across
            // a vertical layer seam), so make that row real before deciding Grounded.
            if (minY > WorldMinY)
                EnsureChunksForBounds(minX, maxX, minY - 1, minY - 1, minZ, maxZ);
        }

        private void EnsureTeleportSurfaceChunks(int minX, int maxX, int minZ, int maxZ)
        {
            var chunks = new HashSet<ChunkCoordinates>();
            for (int x = minX; x <= maxX; x++)
            for (int z = minZ; z <= maxZ; z++)
            {
                int chunkX = GameWorld.WorldToChunkCoord(x);
                int chunkZ = GameWorld.WorldToChunkCoord(z);
                for (int layer = ChunkManager.DeepLayer; layer <= ChunkManager.SkyLayer; layer++)
                {
                    var key = new ChunkCoordinates(layer, chunkX, chunkZ);
                    if (chunks.Add(key)) Chunks.GetOrCreateChunk(layer, chunkX, chunkZ);
                }
            }
        }

        private void EnsureChunksForBounds(int minX, int maxX, int minY, int maxY, int minZ, int maxZ)
        {
            var chunks = new HashSet<ChunkCoordinates>();
            for (int y = minY; y <= maxY; y++)
            {
                int layer = ChunkManager.LayerForWorldY(y);
                for (int x = minX; x <= maxX; x++)
                for (int z = minZ; z <= maxZ; z++)
                {
                    int chunkX = GameWorld.WorldToChunkCoord(x);
                    int chunkZ = GameWorld.WorldToChunkCoord(z);
                    var key = new ChunkCoordinates(layer, chunkX, chunkZ);
                    if (chunks.Add(key)) Chunks.GetOrCreateChunk(layer, chunkX, chunkZ);
                }
            }
        }

        private bool IsTeleportPositionColliding(Point3D eyePosition)
        {
            double minX = eyePosition.X - PlayerRadius;
            double maxX = eyePosition.X + PlayerRadius;
            double minY = eyePosition.Y - EyeHeight;
            double maxY = minY + PlayerHeight;
            double minZ = eyePosition.Z - PlayerRadius;
            double maxZ = eyePosition.Z + PlayerRadius;
            int blockMinX = (int)Math.Floor(minX);
            int blockMaxX = (int)Math.Floor(maxX);
            int blockMinY = (int)Math.Floor(minY);
            int blockMaxY = (int)Math.Floor(maxY - 1e-5);
            int blockMinZ = (int)Math.Floor(minZ);
            int blockMaxZ = (int)Math.Floor(maxZ);

            for (int x = blockMinX; x <= blockMaxX; x++)
            for (int y = blockMinY; y <= blockMaxY; y++)
            for (int z = blockMinZ; z <= blockMaxZ; z++)
            {
                if (Chunks.TryGetLoadedBlockAndMeta(x, y, z, out int block, out byte meta)
                    && BlockRegistry.IsSolid(block)
                    && BoxesOverlapPlayer(GetBlockCollisionBoxes(block, meta), x, y, z,
                        minX, maxX, minY, maxY, minZ, maxZ))
                    return true;
            }
            return false;
        }

        private bool HasSolidSupportAtFeet(double x, double feetY, double z)
        {
            double minX = x - PlayerRadius;
            double maxX = x + PlayerRadius;
            double minZ = z - PlayerRadius;
            double maxZ = z + PlayerRadius;
            int minXBlock = (int)Math.Floor(minX);
            int maxXBlock = (int)Math.Floor(maxX);
            int minZBlock = (int)Math.Floor(minZ);
            int maxZBlock = (int)Math.Floor(maxZ);
            int minYBlock = (int)Math.Floor(feetY - TeleportSupportTolerance);
            int maxYBlock = (int)Math.Floor(feetY);

            for (int blockX = minXBlock; blockX <= maxXBlock; blockX++)
            for (int blockZ = minZBlock; blockZ <= maxZBlock; blockZ++)
            for (int blockY = minYBlock; blockY <= maxYBlock; blockY++)
            {
                if (!Chunks.TryGetLoadedBlockAndMeta(blockX, blockY, blockZ, out int block, out byte meta)
                    || !BlockRegistry.IsSolid(block)) continue;

                foreach (var box in GetBlockCollisionBoxes(block, meta))
                {
                    if (Math.Abs(blockY + box.maxY - feetY) > TeleportSupportTolerance) continue;
                    double overlapX = Math.Min(maxX, blockX + box.maxX) - Math.Max(minX, blockX + box.minX);
                    double overlapZ = Math.Min(maxZ, blockZ + box.maxZ) - Math.Max(minZ, blockZ + box.minZ);
                    if (overlapX > 0.05 && overlapZ > 0.05) return true;
                }
            }
            return false;
        }

        private void ApplyTeleport(Point3D eyePosition, bool grounded)
        {
            if (IsRidingCart) DismountCart();
            LocalPlayer.Position = eyePosition;
            LocalPlayer.Velocity = Point3D.Zero;
            LocalPlayer.Grounded = grounded;
            LocalPlayer.WalkAmount = 0f;
            NotifyPlayerTeleported();
        }
    }
}
