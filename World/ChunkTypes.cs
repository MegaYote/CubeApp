using System;

namespace Cubuild
{
    /// <summary>
    /// Identity of a chunk. The world is two vertically-stacked layers (like two world generators
    /// on top of each other):
    ///   Layer 0 = Ground (world -256..383): terrain band, caves, monoliths, deep fill.
    ///   Layer 1 = Sky (world 384..1023): empty air at gen; sky islands fill lazily when the
    ///            player climbs.
    /// Each layer has its own provider, origin and height, but shares the same (X, Z) column
    /// grid - so the chunk key carries the layer to keep the two worlds separate.
    /// </summary>
    public readonly struct ChunkCoordinates : IEquatable<ChunkCoordinates>
    {
        public int Layer { get; }
        public int X { get; }
        public int Z { get; }

        public ChunkCoordinates(int x, int z) : this(0, x, z)
        {
        }

        public ChunkCoordinates(int layer, int x, int z)
        {
            Layer = layer;
            X = x;
            Z = z;
        }

        public bool Equals(ChunkCoordinates other) => Layer == other.Layer && X == other.X && Z == other.Z;
        public override bool Equals(object? obj) => obj is ChunkCoordinates other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Layer, X, Z);
    }

    public readonly struct ChunkRequest
    {
        public int Layer { get; }
        public int X { get; }
        public int Z { get; }

        public ChunkRequest(int x, int z) : this(0, x, z)
        {
        }

        public ChunkRequest(int layer, int x, int z)
        {
            Layer = layer;
            X = x;
            Z = z;
        }
    }
}
