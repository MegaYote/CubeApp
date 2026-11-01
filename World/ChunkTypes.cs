using System;

namespace Cubuild
{
    /// <summary>
    /// Identity of a chunk within a world layer and horizontal (X, Z) grid. Deep (layer 0),
    /// ground (layer 1), and sky (layer 2) retain their existing meanings and independent
    /// providers/origins/heights. SectionY identifies a vertical section within a layer. Sky
    /// chunks use 16-high sections keyed by their vertical section index; legacy constructors
    /// that omit SectionY default to section zero.
    /// </summary>
    public readonly struct ChunkCoordinates : IEquatable<ChunkCoordinates>
    {
        public int Layer { get; }
        public int X { get; }
        public int Z { get; }
        public int SectionY { get; }

        public ChunkCoordinates(int x, int z) : this(0, x, z, 0)
        {
        }

        public ChunkCoordinates(int layer, int x, int z) : this(layer, x, z, 0)
        {
        }

        public ChunkCoordinates(int layer, int x, int z, int sectionY)
        {
            Layer = layer;
            X = x;
            Z = z;
            SectionY = sectionY;
        }

        public bool Equals(ChunkCoordinates other) => Layer == other.Layer && X == other.X && Z == other.Z && SectionY == other.SectionY;
        public override bool Equals(object? obj) => obj is ChunkCoordinates other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Layer, X, Z, SectionY);
    }

    /// <summary>
    /// Queued chunk-generation identity. Sky requests target 16-high sections; legacy constructors
    /// that omit SectionY default to section zero.
    /// </summary>
    public readonly struct ChunkRequest : IEquatable<ChunkRequest>
    {
        public int Layer { get; }
        public int X { get; }
        public int Z { get; }
        public int SectionY { get; }

        public ChunkRequest(int x, int z) : this(0, x, z, 0)
        {
        }

        public ChunkRequest(int layer, int x, int z) : this(layer, x, z, 0)
        {
        }

        public ChunkRequest(int layer, int x, int z, int sectionY)
        {
            Layer = layer;
            X = x;
            Z = z;
            SectionY = sectionY;
        }

        public bool Equals(ChunkRequest other) => Layer == other.Layer && X == other.X && Z == other.Z && SectionY == other.SectionY;
        public override bool Equals(object? obj) => obj is ChunkRequest other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Layer, X, Z, SectionY);
    }
}
