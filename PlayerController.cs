using System;

namespace CubeApp
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

        private const float WalkSpeed = 4.317f;
        private const float JumpVelocity = 8.0f;
        private const float Gravity = 24.0f;
        private const float MaxFallSpeed = 36.0f;
        private const double PlayerHeight = 1.8;
        private const double PlayerRadius = 0.30;
        private const double EyeHeight = 1.62;
        private const double CollisionStep = 0.05;

        public PlayerController(ChunkManager chunkManager, Point3D startPosition)
        {
            _chunkManager = chunkManager ?? throw new ArgumentNullException(nameof(chunkManager));
            Position = startPosition;
            Velocity = new Point3D(0, 0, 0);
            IsGrounded = true;
        }

        public void Update(float deltaSeconds, Point3D moveInput, bool jumpRequested)
        {
            // Apply gravity
            Velocity = new Point3D(Velocity.X, Velocity.Y - Gravity * deltaSeconds, Velocity.Z);
            Velocity = new Point3D(Velocity.X, Math.Max(Velocity.Y, -MaxFallSpeed), Velocity.Z);

            // Apply jump
            if (jumpRequested && IsGrounded)
            {
                Velocity = new Point3D(Velocity.X, JumpVelocity, Velocity.Z);
                IsGrounded = false;
            }

            // Calculate movement
            var forward = GetCameraForward();
            var forwardHorizontal = new Point3D(forward.X, 0, forward.Z).Normalized();
            var right = new Point3D(forwardHorizontal.Z, 0, -forwardHorizontal.X).Normalized();

            var frameDisplacement = new Point3D(
                (forwardHorizontal.X * moveInput.Z + right.X * moveInput.X) * WalkSpeed * deltaSeconds,
                Velocity.Y * deltaSeconds,
                (forwardHorizontal.Z * moveInput.Z + right.Z * moveInput.X) * WalkSpeed * deltaSeconds);

            MoveWithCollisions(frameDisplacement);
        }

        private Point3D GetCameraForward()
        {
            // This will be set by the InputHandler
            return new Point3D(0, 0, -1); // Default forward
        }

        public void SetCameraDirection(float yaw, float pitch)
        {
            // Store camera direction for movement calculations
            CameraYaw = yaw;
            CameraPitch = pitch;
        }

        public float CameraYaw { get; private set; }
        public float CameraPitch { get; private set; }

        private Point3D GetForwardVector()
        {
            float yawRad = CameraYaw * (float)Math.PI / 180f;
            float pitchRad = CameraPitch * (float)Math.PI / 180f;
            
            return new Point3D(
                (float)(Math.Sin(yawRad) * Math.Cos(pitchRad)),
                (float)-Math.Sin(pitchRad),
                (float)(-Math.Cos(yawRad) * Math.Cos(pitchRad)));
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
                        if (_chunkManager.TryGetLoadedBlock(x, y, z, out var block) && block != BlockType.Air)
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
