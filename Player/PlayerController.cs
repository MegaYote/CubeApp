using System;
using System.Numerics;

namespace Cubuild
{
    /// <summary>
    /// Handles player movement, physics, and collision detection.
    /// </summary>
    public sealed class PlayerController
    {
        private readonly ChunkManager _chunkManager;

        public Point3D Position { get; private set; }
        public Point3D Velocity { get; private set; }
        public bool IsGrounded { get; private set; }

        private const float WalkSpeed = 4.0f;
        private const float JumpVelocity = 8.0f;
        private const float Gravity = 24.0f;
        private const float MaxFallSpeed = 36.0f;

        /// <summary>
        /// Player height in blocks (for collision detection).
        /// </summary>
        public const double PlayerHeight = 1.8;

        /// <summary>
        /// Player collision radius in blocks.
        /// </summary>
        public const double PlayerRadius = 0.30;

        /// <summary>
        /// Eye height above player position.
        /// </summary>
        public const double EyeHeight = 1.62;

        private const double CollisionStep = 0.05;

        public Point3D EyePosition => Position;

        // Camera orientation (exposed for HUD and other systems)
        public float Yaw { get; private set; }
        public float Pitch { get; private set; }

        public PlayerController(ChunkManager chunkManager, Point3D startPosition)
        {
            _chunkManager = chunkManager ?? throw new ArgumentNullException(nameof(chunkManager));
            Position = startPosition;
            Velocity = new Point3D(0, 0, 0);
            IsGrounded = true;
            Yaw = 0f;
            Pitch = 0f;
        }

        /// <summary>
        /// Returns the forward direction vector for the current camera orientation.
        /// </summary>
        public Point3D GetForward()
        {
            var yawRad = Yaw * Math.PI / 180.0;
            var pitchRad = Pitch * Math.PI / 180.0;
            var cosPitch = Math.Cos(pitchRad);
            return new Point3D(
                cosPitch * Math.Sin(yawRad),
                Math.Sin(pitchRad),
                cosPitch * Math.Cos(yawRad)).Normalized();
        }

        /// <summary>
        /// Applies raw look deltas (typically from captured mouse movement) to yaw and pitch.
        /// </summary>
        public void ApplyLook(Vector2 lookDelta, float sensitivity)
        {
            if (lookDelta.X == 0f && lookDelta.Y == 0f) return;

            Yaw -= lookDelta.X * sensitivity;
            Yaw = NormalizeYaw(Yaw);
            Pitch = Math.Clamp(Pitch - lookDelta.Y * sensitivity, -89f, 89f);
        }

        private static float NormalizeYaw(float yaw)
        {
            float result = yaw % 360f;
            if (result < 0f) result += 360f;
            return result;
        }

        public void Update(float deltaSeconds, Point3D moveInput, bool jumpRequested, Point3D cameraForward)
        {
            // Calculate movement direction
            var forwardHorizontal = new Point3D(cameraForward.X, 0, cameraForward.Z).Normalized();
            var right = new Point3D(-forwardHorizontal.Z, 0, forwardHorizontal.X).Normalized();

            var desiredDirection = new Point3D(0, 0, 0);
            if (moveInput.X != 0 || moveInput.Z != 0)
            {
                desiredDirection = (forwardHorizontal * moveInput.Z + right * moveInput.X).Normalized();
            }

            var horizontalVelocity = desiredDirection * WalkSpeed;
            var verticalVelocity = Velocity.Y;

            if (jumpRequested && IsGrounded)
            {
                verticalVelocity = JumpVelocity;
                IsGrounded = false;
            }

            verticalVelocity -= Gravity * deltaSeconds;
            if (verticalVelocity < -MaxFallSpeed)
            {
                verticalVelocity = -MaxFallSpeed;
            }

            Velocity = new Point3D(horizontalVelocity.X, verticalVelocity, horizontalVelocity.Z);

            var frameDisplacement = Velocity * deltaSeconds;
            MoveWithCollisions(frameDisplacement);
        }

        private void MoveWithCollisions(Point3D displacement)
        {
            bool hitX = false;
            bool hitY = false;
            bool hitZ = false;

            Position = MoveAlongAxis(Position, displacement.X, Axis.X, ref hitX);
            Position = MoveAlongAxis(Position, displacement.Y, Axis.Y, ref hitY);
            Position = MoveAlongAxis(Position, displacement.Z, Axis.Z, ref hitZ);

            if (hitX)
            {
                Velocity = new Point3D(0, Velocity.Y, Velocity.Z);
            }

            if (hitZ)
            {
                Velocity = new Point3D(Velocity.X, Velocity.Y, 0);
            }

            if (hitY)
            {
                if (Velocity.Y <= 0)
                {
                    IsGrounded = true;
                }

                Velocity = new Point3D(Velocity.X, 0, Velocity.Z);
            }
            else
            {
                IsGrounded = false;
            }
        }

        

        private Point3D MoveAlongAxis(Point3D start, double amount, Axis axis, ref bool collided)
        {
            if (amount == 0.0)
            {
                return start;
            }

            int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(amount) / CollisionStep));
            double step = amount / steps;
            var current = start;

            for (int i = 0; i < steps; i++)
            {
                var next = axis switch
                {
                    Axis.X => new Point3D(current.X + step, current.Y, current.Z),
                    Axis.Y => new Point3D(current.X, current.Y + step, current.Z),
                    Axis.Z => new Point3D(current.X, current.Y, current.Z + step),
                    _ => current,
                };

                if (IsColliding(next))
                {
                    collided = true;
                    return current;
                }

                current = next;
            }

            return current;
        }


        public void PlaceAtSafeSpawn()
        {
            var spawn = FindSafeSpawnPosition();
            if (spawn.HasValue)
            {
                Position = spawn.Value;
            }

            Velocity = new Point3D(0, 0, 0);
            IsGrounded = true;
        }

        private Point3D? FindSafeSpawnPosition()
        {
            int baseX = (int)Math.Floor(Position.X);
            int baseZ = (int)Math.Floor(Position.Z);

            for (int radius = 0; radius <= 6; radius++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    for (int dz = -radius; dz <= radius; dz++)
                    {
                        if (radius > 0 && Math.Abs(dx) != radius && Math.Abs(dz) != radius)
                        {
                            continue;
                        }

                        int wx = baseX + dx;
                        int wz = baseZ + dz;

                        int highestSolidY = -1;
                        for (int y = ChunkManager.ChunkHeight - 1; y >= 0; y--)
                        {
                            if (_chunkManager.TryGetLoadedBlock(wx, y, wz, out var block) && block != BlockRegistry.AirId)
                            {
                                highestSolidY = y;
                                break;
                            }
                        }

                        if (highestSolidY < 0)
                        {
                            continue;
                        }

                        double px = wx + 0.5;
                        double pz = wz + 0.5;
                        double minEyeY = highestSolidY + EyeHeight + 0.01;
                        double maxEyeY = ChunkManager.ChunkHeight + EyeHeight;

                        for (double eyeY = minEyeY; eyeY <= maxEyeY; eyeY += 0.25)
                        {
                            var candidate = new Point3D(px, eyeY, pz);
                            if (!IsColliding(candidate))
                            {
                                return candidate;
                            }
                        }
                    }
                }
            }

            return null;
        }


        


        public bool WouldCollideWithBlock(int x, int y, int z)
        {
            double minX = Position.X - PlayerRadius;
            double maxX = Position.X + PlayerRadius;
            double minY = Position.Y - EyeHeight;
            double maxY = minY + PlayerHeight;
            double minZ = Position.Z - PlayerRadius;
            double maxZ = Position.Z + PlayerRadius;

            bool overlapsX = (x + 1.0) > minX && x < maxX;
            bool overlapsY = (y + 1.0) > minY && y < maxY;
            bool overlapsZ = (z + 1.0) > minZ && z < maxZ;

            return overlapsX && overlapsY && overlapsZ;
        }

        private bool IsColliding(Point3D eyePosition)
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
            {
                for (int y = blockMinY; y <= blockMaxY; y++)
                {
                    for (int z = blockMinZ; z <= blockMaxZ; z++)
                    {
                        if (_chunkManager.TryGetLoadedBlock(x, y, z, out var block) && block != BlockRegistry.AirId)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }




        private enum Axis { X, Y, Z }
    }
}
