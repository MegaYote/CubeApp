using System;

namespace CubeApp
{
    public readonly struct Point3D
    {
        public double X { get; }
        public double Y { get; }
        public double Z { get; }

        public Point3D(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

        public Point3D Normalized()
        {
            var len = Length;
            return len > 0 ? new Point3D(X / len, Y / len, Z / len) : new Point3D(0, 0, 0);
        }

        public static Point3D operator +(Point3D a, Point3D b) => new Point3D(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Point3D operator -(Point3D a, Point3D b) => new Point3D(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Point3D operator -(Point3D a) => new Point3D(-a.X, -a.Y, -a.Z);
        public static Point3D operator *(Point3D a, double scalar) => new Point3D(a.X * scalar, a.Y * scalar, a.Z * scalar);
        public static Point3D operator *(double scalar, Point3D a) => new Point3D(a.X * scalar, a.Y * scalar, a.Z * scalar);
        public static Point3D operator /(Point3D a, double scalar) => new Point3D(a.X / scalar, a.Y / scalar, a.Z / scalar);
    }
}
