using System;
using System.Numerics;

namespace CubeApp
{
    /// <summary>
    /// Handles block interaction, raycasting, and editing.
    /// </summary>
    public sealed class BlockInteraction
    {
        private readonly ChunkManager _chunkManager;
        private readonly MeshScheduler _meshScheduler;
        private readonly PlayerController _playerController;

        private BlockType _selectedBlock = BlockType.Grass;
        private const float BlockReach = 6.5f;
        private const double PlayerHeight = 1.8;
        private const double PlayerRadius = 0.30;
        private const double EyeHeight = 1.62;

        public BlockInteraction(ChunkManager chunkManager, MeshScheduler meshScheduler, PlayerController playerController)
        {
            _chunkManager = chunkManager ?? throw new ArgumentNullException(nameof(chunkManager));
            _meshScheduler = meshScheduler ?? throw new ArgumentNullException(nameof(meshScheduler));
            _playerController = playerController ?? throw new ArgumentNullException(nameof(playerController));
        }

        public BlockType SelectedBlock
        {
            get => _selectedBlock;
            set => _selectedBlock = value;
        }

        public PickBlockResult? TryPickBlock(Point3D cameraPosition, Point3D cameraForward)
        {
            const int maxSteps = 200;
            const double stepSize = 0.05;

            var pos = cameraPosition;
            var dir = cameraForward.Normalized();

            for (int i = 0; i < maxSteps; i++)
            {
                var nextPos = pos + dir * stepSize;

                int bx = (int)Math.Floor(nextPos.X);
                int by = (int)Math.Floor(nextPos.Y);
                int bz = (int)Math.Floor(nextPos.Z);

                if (_chunkManager.TryGetLoadedBlock(bx, by, bz, out var block) && block != BlockType.Air)
                {
                    // Found a block - determine which face was hit
                    int prevBx = (int)Math.Floor(pos.X);
                    int prevBy = (int)Math.Floor(pos.Y);
                    int prevBz = (int)Math.Floor(pos.Z);

                    var normal = new Point3D(0, 0, 0);
                    if (prevBx != bx) normal = new Point3D(prevBx < bx ? -1 : 1, 0, 0);
                    else if (prevBy != by) normal = new Point3D(0, prevBy < by ? -1 : 1, 0);
                    else normal = new Point3D(0, 0, prevBz < bz ? -1 : 1);

                    return new PickBlockResult
                    {
                        Remove = new Point3D(bx, by, bz),
                        Place = new Point3D(prevBx, prevBy, prevBz),
                        Normal = normal
                    };
                }

                pos = nextPos;
            }

            return null;
        }

        public bool DeleteBlock(Point3D cameraPosition, Point3D cameraForward)
        {
            var pickResult = TryPickBlock(cameraPosition, cameraForward);
            if (!pickResult.HasValue) return false;

            var remove = pickResult.Value.Remove;
            if (!_chunkManager.TrySetBlock(remove.x, remove.y, remove.z, BlockType.Air)) return false;

            var editedChunk = new ChunkCoordinates(WorldToChunkCoord(remove.x), WorldToChunkCoord(remove.z));
            _meshScheduler.RequestImmediateRemesh(editedChunk);

            // Also request immediate remesh for neighbor chunks if edit was at a boundary
            RequestNeighborRemesh(remove.x, remove.z, editedChunk);

            return true;
        }

        public bool PlaceBlock(Point3D cameraPosition, Point3D cameraForward)
        {
            var pickResult = TryPickBlock(cameraPosition, cameraForward);
            if (!pickResult.HasValue) return false;

            var place = pickResult.Value.Place;
            if (WouldBlockIntersectPlayer(place.x, place.y, place.z)) return false;
            if (!_chunkManager.TrySetBlock(place.x, place.y, place.z, _selectedBlock)) return false;

            var editedChunk = new ChunkCoordinates(WorldToChunkCoord(place.x), WorldToChunkCoord(place.z));
            _meshScheduler.RequestImmediateRemesh(editedChunk);

            // Also request immediate remesh for neighbor chunks if edit was at a boundary
            RequestNeighborRemesh(place.x, place.z, editedChunk);

            return true;
        }

        private void RequestNeighborRemesh(int worldX, int worldZ, ChunkCoordinates editedChunk)
        {
            int localX = worldX - (editedChunk.X * ChunkManager.ChunkSize);
            int localZ = worldZ - (editedChunk.Z * ChunkManager.ChunkSize);
            if (localX == 0)
                _meshScheduler.RequestImmediateRemesh(new ChunkCoordinates(editedChunk.X - 1, editedChunk.Z));
            if (localX == ChunkManager.ChunkSize - 1)
                _meshScheduler.RequestImmediateRemesh(new ChunkCoordinates(editedChunk.X + 1, editedChunk.Z));
            if (localZ == 0)
                _meshScheduler.RequestImmediateRemesh(new ChunkCoordinates(editedChunk.X, editedChunk.Z - 1));
            if (localZ == ChunkManager.ChunkSize - 1)
                _meshScheduler.RequestImmediateRemesh(new ChunkCoordinates(editedChunk.X, editedChunk.Z + 1));
        }

        private bool WouldBlockIntersectPlayer(int x, int y, int z)
        {
            var playerPos = _playerController.Position;
            double minX = playerPos.X - PlayerRadius;
            double maxX = playerPos.X + PlayerRadius;
            double minY = playerPos.Y - EyeHeight;
            double maxY = minY + PlayerHeight;
            double minZ = playerPos.Z - PlayerRadius;
            double maxZ = playerPos.Z + PlayerRadius;

            bool overlapsX = (x + 1.0) > minX && x < maxX;
            bool overlapsY = (y + 1.0) > minY && y < maxY;
            bool overlapsZ = (z + 1.0) > minZ && z < maxZ;

            return overlapsX && overlapsY && overlapsZ;
        }

        private static int WorldToChunkCoord(double value)
        {
            return (int)Math.Floor(value / ChunkManager.ChunkSize);
        }

        public Vector3[]? ComputeHighlightWorldQuad(PickBlockResult hit)
        {
            var remove = hit.Remove;
            var n = hit.Normal;

            Point3D[] faceCorners = new Point3D[4];
            if (Math.Abs(n.X) > 0.5)
            {
                double xplane = remove.x + (n.X > 0 ? 1.0 : 0.0);
                faceCorners[0] = new Point3D(xplane, remove.y, remove.z);
                faceCorners[1] = new Point3D(xplane, remove.y, remove.z + 1.0);
                faceCorners[2] = new Point3D(xplane, remove.y + 1.0, remove.z + 1.0);
                faceCorners[3] = new Point3D(xplane, remove.y + 1.0, remove.z);
            }
            else if (Math.Abs(n.Y) > 0.5)
            {
                double yplane = remove.y + (n.Y > 0 ? 1.0 : 0.0);
                faceCorners[0] = new Point3D(remove.x, yplane, remove.z);
                faceCorners[1] = new Point3D(remove.x + 1.0, yplane, remove.z);
                faceCorners[2] = new Point3D(remove.x + 1.0, yplane, remove.z + 1.0);
                faceCorners[3] = new Point3D(remove.x, yplane, remove.z + 1.0);
            }
            else
            {
                double zplane = remove.z + (n.Z > 0 ? 1.0 : 0.0);
                faceCorners[0] = new Point3D(remove.x, remove.y, zplane);
                faceCorners[1] = new Point3D(remove.x + 1.0, remove.y, zplane);
                faceCorners[2] = new Point3D(remove.x + 1.0, remove.y + 1.0, zplane);
                faceCorners[3] = new Point3D(remove.x, remove.y + 1.0, zplane);
            }

            faceCorners = CanonicalizeFaceCornersByAxes(faceCorners, n);

            // Nudge the quad a hair off the block face along its outward normal so it wins the
            // depth test against the coplanar block face (avoids z-fighting) while still being
            // occluded by any nearer block, which is what gives correct per-pixel occlusion.
            const double faceEpsilon = 0.002;
            var offset = n * faceEpsilon;

            var result = new Vector3[4];
            for (int i = 0; i < 4; i++)
            {
                var pos = faceCorners[i] + offset;
                result[i] = new Vector3((float)pos.X, (float)pos.Y, (float)pos.Z);
            }

            return result;
        }

        private static Point3D[] CanonicalizeFaceCornersByAxes(Point3D[] corners, Point3D normal)
        {
            if (corners.Length != 4)
            {
                return corners;
            }

            if (!TryGetHighlightFaceAxes(normal, out var uAxis, out var vAxis))
            {
                return corners;
            }

            Span<(double U, double V)> uv = stackalloc (double U, double V)[4];
            double minU = double.PositiveInfinity;
            double maxU = double.NegativeInfinity;
            double minV = double.PositiveInfinity;
            double maxV = double.NegativeInfinity;

            for (int i = 0; i < 4; i++)
            {
                var c = corners[i];
                var u = Dot(c, uAxis);
                var v = Dot(c, vAxis);
                uv[i] = (u, v);
                if (u < minU) minU = u;
                if (u > maxU) maxU = u;
                if (v < minV) minV = v;
                if (v > maxV) maxV = v;
            }

            bool[] used = new bool[4];
            var result = new Point3D[4];
            result[0] = PickClosestCornerByUv(corners, uv, minU, minV, used);
            result[1] = PickClosestCornerByUv(corners, uv, maxU, minV, used);
            result[2] = PickClosestCornerByUv(corners, uv, maxU, maxV, used);
            result[3] = PickClosestCornerByUv(corners, uv, minU, maxV, used);

            return result;
        }

        private static Point3D PickClosestCornerByUv(Point3D[] corners, Span<(double U, double V)> uv, double targetU, double targetV, bool[] used)
        {
            int bestIndex = -1;
            double bestDistSq = double.PositiveInfinity;

            for (int i = 0; i < corners.Length; i++)
            {
                if (used[i])
                {
                    continue;
                }

                var du = uv[i].U - targetU;
                var dv = uv[i].V - targetV;
                var distSq = du * du + dv * dv;
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0)
            {
                return corners[0];
            }

            used[bestIndex] = true;
            return corners[bestIndex];
        }

        private static bool TryGetHighlightFaceAxes(Point3D normal, out Point3D uAxis, out Point3D vAxis)
        {
            if (normal.X > 0.5)
            {
                uAxis = new Point3D(0, 0, -1);
                vAxis = new Point3D(0, 1, 0);
                return true;
            }

            if (normal.X < -0.5)
            {
                uAxis = new Point3D(0, 0, 1);
                vAxis = new Point3D(0, 1, 0);
                return true;
            }

            if (normal.Y > 0.5)
            {
                uAxis = new Point3D(1, 0, 0);
                vAxis = new Point3D(0, 0, 1);
                return true;
            }

            if (normal.Y < -0.5)
            {
                uAxis = new Point3D(1, 0, 0);
                vAxis = new Point3D(0, 0, -1);
                return true;
            }

            if (normal.Z > 0.5)
            {
                uAxis = new Point3D(1, 0, 0);
                vAxis = new Point3D(0, 1, 0);
                return true;
            }

            if (normal.Z < -0.5)
            {
                uAxis = new Point3D(-1, 0, 0);
                vAxis = new Point3D(0, 1, 0);
                return true;
            }

            uAxis = new Point3D(0, 0, 0);
            vAxis = new Point3D(0, 0, 0);
            return false;
        }

        private static double Dot(Point3D a, Point3D b)
        {
            return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        }
    }
}
