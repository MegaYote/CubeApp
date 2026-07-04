using System;

namespace CubeApp
{
    /// <summary>
    /// A snapshot of a duck's placement handed to the renderer each frame: where it is and which
    /// way it faces. Kept separate from <see cref="Duck"/> so rendering never touches sim state.
    /// </summary>
    public readonly struct DuckInstance
    {
        public readonly Point3D Position;   // feet position (model origin) in world space
        public readonly float Yaw;          // radians, rotation about +Y

        public DuckInstance(Point3D position, float yaw)
        {
            Position = position;
            Yaw = yaw;
        }
    }

    /// <summary>
    /// The duck test-mob: a static model with gravity and ground collision. No AI or animation yet
    /// (that lives in Cubuild and is out of scope for this first port). Position is the feet point;
    /// the collision box is centred on that point horizontally and rises <see cref="Height"/> above it.
    /// </summary>
    public sealed class Duck
    {
        public const float Width = 0.68f;   // full width/depth of the collision box (blocks)
        public const float Height = 1.35f;
        public const float Gravity = 22f;   // blocks / s^2 (matches Cubuild's test mob)
        private const float TerminalVelocity = 40f;

        public Point3D Position;
        public float VelocityY;
        public float Yaw;
        public bool OnGround;

        public Duck(Point3D position, float yaw)
        {
            Position = position;
            VelocityY = 0f;
            Yaw = yaw;
            OnGround = false;
        }

        public DuckInstance ToInstance() => new DuckInstance(Position, Yaw);

        /// <summary>
        /// Advance gravity and resolve vertical collision against solid blocks. Horizontal motion is
        /// intentionally absent for this first test mob.
        /// </summary>
        public void Update(float dt, ChunkManager manager)
        {
            VelocityY -= Gravity * dt;
            if (VelocityY < -TerminalVelocity)
            {
                VelocityY = -TerminalVelocity;
            }

            double newY = Position.Y + VelocityY * dt;

            if (VelocityY <= 0f)
            {
                double groundTop = FindGroundTop(manager, newY);
                if (newY <= groundTop)
                {
                    newY = groundTop;
                    VelocityY = 0f;
                    OnGround = true;
                }
                else
                {
                    OnGround = false;
                }
            }
            else
            {
                OnGround = false;
            }

            Position = new Point3D(Position.X, newY, Position.Z);
        }

        // Highest solid block top at or below the feet, sampled under the footprint corners.
        private double FindGroundTop(ChunkManager manager, double feetY)
        {
            float half = Width * 0.5f;
            double best = double.NegativeInfinity;

            foreach (var (ox, oz) in Footprint(half))
            {
                int bx = (int)Math.Floor(Position.X + ox);
                int bz = (int)Math.Floor(Position.Z + oz);
                int startY = (int)Math.Floor(feetY);
                for (int by = startY; by >= 0; by--)
                {
                    if (manager.TryGetLoadedBlock(bx, by, bz, out var block) && block != BlockType.Air)
                    {
                        double top = by + 1;
                        if (top > best)
                        {
                            best = top;
                        }
                        break;
                    }
                }
            }

            return best;
        }

        private static (double, double)[] Footprint(float half)
        {
            return new (double, double)[]
            {
                (-half, -half),
                (-half, half),
                (half, -half),
                (half, half),
            };
        }
    }
}
