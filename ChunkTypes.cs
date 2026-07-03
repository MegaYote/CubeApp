using System;

namespace CubeApp
{
    public readonly struct ChunkCoordinates : IEquatable<ChunkCoordinates>
    {
        public int X { get; }
        public int Z { get; }

        public ChunkCoordinates(int x, int z)
        {
            X = x;
            Z = z;
        }

        public bool Equals(ChunkCoordinates other) => X == other.X && Z == other.Z;
        public override bool Equals(object? obj) => obj is ChunkCoordinates other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(X, Z);
    }

    public readonly struct ChunkRequest
    {
        public int X { get; }
        public int Z { get; }

        public ChunkRequest(int x, int z)
        {
            X = x;
            Z = z;
        }
    }
}
