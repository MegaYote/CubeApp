using System;
using System.Collections.Generic;

namespace CubeApp
{
    public sealed class Chunk
    {
        // Flat byte array (1 byte per block instead of a 4-byte enum in a multidimensional
        // array): 4x less memory per chunk and faster indexing (single bounds check, no
        // per-dimension checks). Layout is column-major: ((x * Depth + z) * Height + y), so a
        // vertical column is contiguous - the common access pattern for generation and lighting.
        private readonly byte[] blocks;
        // Per-block metadata (same layout as blocks). For fluids this holds the flow level:
        // 0 = source/still, 1..7 = flowing, >=8 = falling stream. Zero-initialized like blocks.
        private readonly byte[] meta;
        private readonly object _meshLock = new();

        public int Width { get; }
        public int Height { get; }
        public int Depth { get; }
        public int OriginX { get; }
        public int OriginY { get; }
        public int OriginZ { get; }

        /// <summary>
        /// Converts a world Y coordinate to the local array index.
        /// </summary>
        public int WorldYToLocal(int worldY) => worldY - OriginY;

        /// <summary>
        /// Returns true if the given local coordinates are within the chunk's array bounds.
        /// </summary>
        public bool IsInBounds(int localX, int localY, int localZ)
        {
            return localX >= 0 && localX < Width && localY >= 0 && localY < Height && localZ >= 0 && localZ < Depth;
        }
        // Cached mesh for this chunk (regenerated when NeedsRemesh is true)
        public List<MeshFace> MeshFaces { get; set; } = new List<MeshFace>();
        private bool _needsRemesh = true;
        /// <summary>True when this chunk's mesh must be regenerated. The setter fires
        /// <see cref="OnDirty"/> (used by MeshScheduler's dirty-list) whenever it transitions
        /// to true, so the scheduler never scans every loaded chunk to find work.</summary>
        public bool NeedsRemesh
        {
            get => _needsRemesh;
            set
            {
                if (value && !_needsRemesh)
                {
                    _needsRemesh = true;
                    OnDirty?.Invoke(this);
                }
                else
                {
                    _needsRemesh = value;
                }
            }
        }
        /// <summary>Called once when NeedsRemesh flips false -> true. Wired to the MeshScheduler's
        /// dirty-list by GameWorld after constructing the scheduler.</summary>
        public Action<Chunk>? OnDirty;
        // Prevent duplicate enqueueing while meshing is pending
        public bool IsMeshingQueued { get; set; } = false;
        // Incremented each time MeshFaces is updated by the mesher
        public int MeshVersion = 0;

        /// <summary>World Y of the highest solid block in this chunk (or OriginY-1 if empty).
        /// Computed by the mesher from the live block scan. Used by heightmap occlusion culling
        /// to skip chunks hidden behind nearer terrain.</summary>
        public int TopSolidY = int.MinValue;

        public object MeshLock => _meshLock;

        public Chunk(int width, int height, int depth, int originX, int originY, int originZ)
        {
            Width = width;
            Height = height;
            Depth = depth;
            OriginX = originX;
            OriginY = originY;
            OriginZ = originZ;
            blocks = new byte[width * height * depth];
            meta = new byte[width * height * depth];
        }

        /// <summary>Flat index of a local block coordinate (column-major; y contiguous).</summary>
        public int Index(int x, int y, int z) => (x * Depth + z) * Height + y;

        /// <summary>Raw block storage for hot paths (mesher/lighting). Values are block ids.</summary>
        public byte[] RawBlocks => blocks;
        /// <summary>Raw metadata storage (same layout as RawBlocks). Values are block metadata.</summary>
        public byte[] RawMeta => meta;

        public byte GetMeta(int x, int y, int z) => meta[(x * Depth + z) * Height + y];
        public void SetMeta(int x, int y, int z, byte value) => meta[(x * Depth + z) * Height + y] = value;

        public int this[int x, int y, int z]
        {
            get => blocks[(x * Depth + z) * Height + y];
            set => blocks[(x * Depth + z) * Height + y] = (byte)value;
        }

        public void GenerateFlatPlane(int grassHeight)
        {
            if (grassHeight < 0 || grassHeight >= Height)
            {
                throw new ArgumentOutOfRangeException(nameof(grassHeight));
            }

            for (int x = 0; x < Width; x++)
            {
                for (int z = 0; z < Depth; z++)
                {
                    for (int y = 0; y < Height; y++)
                    {
                        if (y == grassHeight)
                        {
                            this[x, y, z] = BlockRegistry.GetId("grass");
                        }
                        else if (y < grassHeight)
                        {
                            this[x, y, z] = BlockRegistry.GetId("dirt");
                        }
                        else if (y <= 0)
                        {
                            this[x, y, z] = BlockRegistry.GetId("water");
                        }
                        else
                        {
                            this[x, y, z] = BlockRegistry.AirId;
                        }
                    }
                }
            }
        }

        public void GenerateTerrain(int chunkX, int chunkZ)
        {
            var randSeed = (chunkX << 16) ^ (chunkZ & 0xFFFF);
            for (int x = 0; x < Width; x++)
            {
                for (int z = 0; z < Depth; z++)
                {
                    int worldX = OriginX + x;
                    int worldZ = OriginZ + z;
                    // base terrain height using combined sinusoidal noise
                    double noise = Math.Sin(worldX * 0.18) * 2.2 + Math.Cos(worldZ * 0.16) * 2.0 + Math.Sin((worldX + worldZ) * 0.12) * 1.5;
                    int groundHeight = 2 + (int)Math.Floor(2.5 + noise);
                    groundHeight = Math.Clamp(groundHeight, 0, Height - 1);

                    for (int y = 0; y < Height; y++)
                    {
                        // create caves: use a cheap 3D wave-based noise to carve pockets underground
                        bool carveCave = false;
                        if (y < groundHeight - 2)
                        {
                            double caveNoise = Math.Sin(worldX * 0.31 + worldZ * 0.29 + y * 0.37 + randSeed * 0.0001)
                                             + Math.Cos(worldX * 0.21 - worldZ * 0.19 + y * 0.33);
                            carveCave = caveNoise > 1.1; // threshold tuned to create sparse caves
                        }

                        if (y < groundHeight - 1 && !carveCave)
                        {
                            this[x, y, z] = (y < groundHeight - 3) ? BlockRegistry.GetId("stone") : BlockRegistry.GetId("dirt");
                        }
                        else if (y == groundHeight - 1 && !carveCave)
                        {
                            this[x, y, z] = BlockRegistry.GetId("grass");
                        }
                        else if (y <= 0)
                        {
                            this[x, y, z] = BlockRegistry.GetId("water");
                        }
                        else
                        {
                            this[x, y, z] = BlockRegistry.AirId;
                        }
                    }
                }
            }
        }
    }
}
