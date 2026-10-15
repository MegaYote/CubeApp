using System;
using System.Numerics;

namespace Cubuild
{
    /// <summary>
    /// Handles raycasting for block interaction (pick, break, place) and duck/entity targeting.
    /// Extracted from Program.cs to reduce main-class bloat and make block-interaction logic testable independently.
    /// </summary>
    public sealed class BlockInteractionSystem : IDisposable
    {
        private readonly ChunkManager _manager;

        // Constants from Program
        private const float BlockReach = 6.5f;

        /// <summary>
        /// Result of a block pick operation. Used by both break and place systems.
        /// </summary>
        public readonly struct PickBlockResult
        {
            public (int x, int y, int z) Remove { get; }
            public (int x, int y, int z) Place { get; }
            public Point3D Normal { get; }
            public double Distance { get; }

            public PickBlockResult((int x, int y, int z) remove, (int x, int y, int z) place, Point3D normal, double distance)
            {
                Remove = remove;
                Place = place;
                Normal = normal;
                Distance = distance;
            }
        }

        public BlockInteractionSystem(ChunkManager manager) => _manager = manager;

        /// <summary>
        /// Performs a block hit-test along a ray. Returns null if nothing is within reach or all blocks are Air.
        /// </summary>
        public PickBlockResult? TryPickBlock(Point3D origin, Point3D direction)
        {
            direction = direction.Normalized();
            var blockX = (int)Math.Floor(origin.X);
            var blockY = (int)Math.Floor(origin.Y);
            var blockZ = (int)Math.Floor(origin.Z);

            var stepX = Math.Sign(direction.X);
            var stepY = Math.Sign(direction.Y);
            var stepZ = Math.Sign(direction.Z);

            var tDeltaX = stepX != 0 ? Math.Abs(1.0 / direction.X) : double.PositiveInfinity;
            var tDeltaY = stepY != 0 ? Math.Abs(1.0 / direction.Y) : double.PositiveInfinity;
            var tDeltaZ = stepZ != 0 ? Math.Abs(1.0 / direction.Z) : double.PositiveInfinity;

            var tMaxX = stepX > 0 ? (blockX + 1.0 - origin.X) * tDeltaX : (origin.X - blockX) * tDeltaX;
            var tMaxY = stepY > 0 ? (blockY + 1.0 - origin.Y) * tDeltaY : (origin.Y - blockY) * tDeltaY;
            var tMaxZ = stepZ > 0 ? (blockZ + 1.0 - origin.Z) * tDeltaZ : (origin.Z - blockZ) * tDeltaZ;

            var currentX = blockX;
            var currentY = blockY;
            var currentZ = blockZ;
            var distance = 0.0;
            var lastX = currentX;
            var lastY = currentY;
            var lastZ = currentZ;
            var normal = Point3D.Zero;

            for (int iteration = 0; iteration < 200 && distance <= BlockReach; iteration++)
            {
                if (_manager.TryGetLoadedBlock(currentX, currentY, currentZ, out var block) && block != BlockRegistry.AirId)
                {
                    // A full cube is hit the moment the ray enters its cell. A partial shape
                    // (slab, rail) only fills part of the cell, so the ray must actually pass
                    // through its hitbox - otherwise aiming at the empty air above a rail would
                    // still select it. On a miss the cell is treated as passable and the DDA
                    // continues, so you can place a block in the space above a rail.
                    if (!TryGetHitbox(block, currentX, currentY, currentZ, out var hb))
                        return new PickBlockResult((currentX, currentY, currentZ), (lastX, lastY, lastZ), normal, distance);

                    if (RayAABB(origin, direction, hb.minX, hb.minY, hb.minZ, hb.maxX, hb.maxY, hb.maxZ,
                               out double tHit, out Point3D hitNormal))
                    {
                        if (tHit >= distance - 1e-6 && tHit <= BlockReach)
                            return new PickBlockResult((currentX, currentY, currentZ), (lastX, lastY, lastZ), hitNormal, tHit);
                    }
                }

                lastX = currentX;
                lastY = currentY;
                lastZ = currentZ;

                if (tMaxX < tMaxY)
                {
                    if (tMaxX < tMaxZ)
                    {
                        currentX += stepX;
                        distance = tMaxX;
                        tMaxX += tDeltaX;
                        normal = new Point3D(-stepX, 0, 0);
                    }
                    else
                    {
                        currentZ += stepZ;
                        distance = tMaxZ;
                        tMaxZ += tDeltaZ;
                        normal = new Point3D(0, 0, -stepZ);
                    }
                }
                else
                {
                    if (tMaxY < tMaxZ)
                    {
                        currentY += stepY;
                        distance = tMaxY;
                        tMaxY += tDeltaY;
                        normal = new Point3D(0, -stepY, 0);
                    }
                    else
                    {
                        currentZ += stepZ;
                        distance = tMaxZ;
                        tMaxZ += tDeltaZ;
                        normal = new Point3D(0, 0, -stepZ);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// The world-space hitbox of a partial block, or false when the block is a full cube and
        /// needs no refinement. Slabs, slab-tops and rails occupy only part of their cell, so the
        /// raycast tests against the real bounds instead of the whole cell.
        /// </summary>
        private static bool TryGetHitbox(int id, int x, int y, int z,
            out (double minX, double minY, double minZ, double maxX, double maxY, double maxZ) hb)
        {
            if (BlockRegistry.IsSlab(id))     { hb = (x, y,         z, x + 1, y + 0.5,               z + 1); return true; }
            if (BlockRegistry.IsSlabTop(id))  { hb = (x, y + 0.5,    z, x + 1, y + 1,                 z + 1); return true; }
            if (BlockRegistry.IsRail(id))     { hb = (x, y,         z, x + 1, y + BlockRegistry.RailPickHeight, z + 1); return true; }
            hb = (0, 0, 0, 0, 0, 0);
            return false;
        }

        /// <summary>
        /// Slab-method ray/AABB intersection. Returns true when the ray hits the box, with the
        /// entry distance and the face normal it entered through.
        /// </summary>
        private static bool RayAABB(Point3D o, Point3D d, double minX, double minY, double minZ,
            double maxX, double maxY, double maxZ, out double tHit, out Point3D normal)
        {
            tHit = 0;
            normal = new Point3D(0, 1, 0);
            double tMin = double.NegativeInfinity;
            double tMax = double.PositiveInfinity;
            int axis = -1;
            bool positive = false;

            // X
            if (Math.Abs(d.X) < 1e-9) { if (o.X < minX || o.X > maxX) return false; }
            else
            {
                double inv = 1.0 / d.X;
                double t1 = (minX - o.X) * inv, t2 = (maxX - o.X) * inv;
                bool pos = d.X > 0;
                if (t1 > t2) { double t = t1; t1 = t2; t2 = t; }
                if (t1 > tMin) { tMin = t1; axis = 0; positive = pos; }
                if (t2 < tMax) tMax = t2;
            }
            // Y
            if (Math.Abs(d.Y) < 1e-9) { if (o.Y < minY || o.Y > maxY) return false; }
            else
            {
                double inv = 1.0 / d.Y;
                double t1 = (minY - o.Y) * inv, t2 = (maxY - o.Y) * inv;
                bool pos = d.Y > 0;
                if (t1 > t2) { double t = t1; t1 = t2; t2 = t; }
                if (t1 > tMin) { tMin = t1; axis = 1; positive = pos; }
                if (t2 < tMax) tMax = t2;
            }
            // Z
            if (Math.Abs(d.Z) < 1e-9) { if (o.Z < minZ || o.Z > maxZ) return false; }
            else
            {
                double inv = 1.0 / d.Z;
                double t1 = (minZ - o.Z) * inv, t2 = (maxZ - o.Z) * inv;
                bool pos = d.Z > 0;
                if (t1 > t2) { double t = t1; t1 = t2; t2 = t; }
                if (t1 > tMin) { tMin = t1; axis = 2; positive = pos; }
                if (t2 < tMax) tMax = t2;
            }

            if (tMax < tMin || tMax < 0) return false;
            tHit = tMin > 0 ? tMin : 0;
            normal = axis switch
            {
                0 => new Point3D(positive ? -1 : 1, 0, 0),
                1 => new Point3D(0, positive ? -1 : 1, 0),
                2 => new Point3D(0, 0, positive ? -1 : 1),
                _ => new Point3D(0, 1, 0),
            };
            return true;
        }

        /// <summary>
        /// Performs a block hit-test along a ray specifically for FLUIDS (water, etc.).
        /// Unlike TryPickBlock, this stops at fluid blocks instead of passing through them.
        /// Used for bucket filling. Returns the first fluid block hit, or null.
        /// </summary>
        public PickBlockResult? TryPickFluid(Point3D origin, Point3D direction, int fluidBlockId = -1)
        {
            direction = direction.Normalized();
            var blockX = (int)Math.Floor(origin.X);
            var blockY = (int)Math.Floor(origin.Y);
            var blockZ = (int)Math.Floor(origin.Z);

            var stepX = Math.Sign(direction.X);
            var stepY = Math.Sign(direction.Y);
            var stepZ = Math.Sign(direction.Z);

            var tDeltaX = stepX != 0 ? Math.Abs(1.0 / direction.X) : double.PositiveInfinity;
            var tDeltaY = stepY != 0 ? Math.Abs(1.0 / direction.Y) : double.PositiveInfinity;
            var tDeltaZ = stepZ != 0 ? Math.Abs(1.0 / direction.Z) : double.PositiveInfinity;

            var tMaxX = stepX > 0 ? (blockX + 1.0 - origin.X) * tDeltaX : (origin.X - blockX) * tDeltaX;
            var tMaxY = stepY > 0 ? (blockY + 1.0 - origin.Y) * tDeltaY : (origin.Y - blockY) * tDeltaY;
            var tMaxZ = stepZ > 0 ? (blockZ + 1.0 - origin.Z) * tDeltaZ : (origin.Z - blockZ) * tDeltaZ;

            var currentX = blockX;
            var currentY = blockY;
            var currentZ = blockZ;
            var distance = 0.0;
            var lastX = currentX;
            var lastY = currentY;
            var lastZ = currentZ;
            var normal = Point3D.Zero;

            for (int iteration = 0; iteration < 200 && distance <= BlockReach; iteration++)
            {
                if (_manager.TryGetLoadedBlock(currentX, currentY, currentZ, out var block))
                {
                    // Match specific fluid if provided, otherwise match any non-air, non-solid block
                    // that reports as fluid (for now just match the given fluid ID or water)
                    bool isTargetFluid = (fluidBlockId > 0 && block == fluidBlockId) ||
                                         (fluidBlockId <= 0 && block != BlockRegistry.AirId &&
                                          !BlockRegistry.IsSolid(block)); // non-solid = fluid-like
                    if (isTargetFluid)
                    {
                        return new PickBlockResult((currentX, currentY, currentZ), (lastX, lastY, lastZ), normal, distance);
                    }
                }

                lastX = currentX;
                lastY = currentY;
                lastZ = currentZ;

                if (tMaxX < tMaxY)
                {
                    if (tMaxX < tMaxZ)
                    {
                        currentX += stepX;
                        distance = tMaxX;
                        tMaxX += tDeltaX;
                        normal = new Point3D(-stepX, 0, 0);
                    }
                    else
                    {
                        currentZ += stepZ;
                        distance = tMaxZ;
                        tMaxZ += tDeltaZ;
                        normal = new Point3D(0, 0, -stepZ);
                    }
                }
                else
                {
                    if (tMaxY < tMaxZ)
                    {
                        currentY += stepY;
                        distance = tMaxY;
                        tMaxY += tDeltaY;
                        normal = new Point3D(0, -stepY, 0);
                    }
                    else
                    {
                        currentZ += stepZ;
                        distance = tMaxZ;
                        tMaxZ += tDeltaZ;
                        normal = new Point3D(0, 0, -stepZ);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Checks if a block at the given coordinates would intersect the player's bounding box.
        /// Returns true if there is an intersection (block placement should be rejected).
        /// </summary>
        public bool WouldBlockIntersectPlayer(Point3D playerPos, int x, int y, int z)
        {
            double minX = playerPos.X - PlayerController.PlayerRadius;
            double maxX = playerPos.X + PlayerController.PlayerRadius;
            double minY = playerPos.Y - PlayerController.EyeHeight;
            double maxY = minY + PlayerController.PlayerHeight;
            double minZ = playerPos.Z - PlayerController.PlayerRadius;
            double maxZ = playerPos.Z + PlayerController.PlayerRadius;

            bool overlapsX = (x + 1.0) > minX && x < maxX;
            bool overlapsY = (y + 1.0) > minY && y < maxY;
            bool overlapsZ = (z + 1.0) > minZ && z < maxZ;

            return overlapsX && overlapsY && overlapsZ;
        }

        /// <summary>
        /// Requests immediate remesh for the chunk containing the block and its neighbors if at a boundary.
        /// Used after block modifications to ensure correct mesh updates.
        /// </summary>
        public void RequestNeighborRemesh(int x, int y, int z, MeshScheduler meshScheduler)
        {
            var editedChunk = new ChunkCoordinates(WorldToChunkCoord(x), WorldToChunkCoord(z));
            meshScheduler.RequestImmediateRemesh(editedChunk);

            int localX = x - (editedChunk.X * ChunkManager.ChunkSize);
            int localZ = z - (editedChunk.Z * ChunkManager.ChunkSize);
            if (localX == 0)
                meshScheduler.RequestImmediateRemesh(new ChunkCoordinates(editedChunk.X - 1, editedChunk.Z));
            if (localX == ChunkManager.ChunkSize - 1)
                meshScheduler.RequestImmediateRemesh(new ChunkCoordinates(editedChunk.X + 1, editedChunk.Z));
            if (localZ == 0)
                meshScheduler.RequestImmediateRemesh(new ChunkCoordinates(editedChunk.X, editedChunk.Z - 1));
            if (localZ == ChunkManager.ChunkSize - 1)
                meshScheduler.RequestImmediateRemesh(new ChunkCoordinates(editedChunk.X, editedChunk.Z + 1));
        }

        private static int WorldToChunkCoord(double value)
        {
            return (int)Math.Floor(value / ChunkManager.ChunkSize);
        }

        public void Dispose() { }
    }
}
