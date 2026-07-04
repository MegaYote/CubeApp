using System;
using System.Collections.Generic;
using System.Threading;

namespace CubeApp
{
    public enum BlockType
    {
        Air,
        Grass,
        Dirt,
        Stone,
        Cobblestone,
        Sand,
        Planks,
        Bedrock,
        Gravel,
        Obsidian,
        MossyCobblestone
    }

    public sealed class Chunk
    {
        private readonly BlockType[,,] blocks;

        public int Width { get; }
        public int Height { get; }
        public int Depth { get; }
        public int OriginX { get; }
        public int OriginZ { get; }
        // Cached mesh for this chunk (regenerated when NeedsRemesh is true)
        public List<MeshFace> MeshFaces { get; set; } = new List<MeshFace>();

        // Mesh dirty-tracking via monotonic version counters instead of a single bool. A chunk
        // needs remeshing whenever it has been dirtied (block edit, neighbor load/unload, ...) more
        // recently than it was last meshed. This closes the lost-remesh race: if a dirty lands
        // while a background mesh is in flight, DirtyVersion moves past the version the mesh was
        // built from, so the chunk stays dirty and is re-meshed instead of being wrongly cleared.
        private int _dirtyVersion = 1;
        private int _meshedVersion = 0;

        // Monotonic counter bumped every time the chunk is dirtied. Safe to call from any thread.
        public int DirtyVersion => Volatile.Read(ref _dirtyVersion);
        public void MarkDirty() => Interlocked.Increment(ref _dirtyVersion);

        // Record that the current MeshFaces were built from block state as of builtFromVersion
        // (captured before meshing began). Called by the mesher on the worker thread.
        public void MarkMeshed(int builtFromVersion) => Volatile.Write(ref _meshedVersion, builtFromVersion);

        public bool NeedsRemesh => DirtyVersion != Volatile.Read(ref _meshedVersion);

        // Prevent duplicate enqueueing while meshing is pending
        public bool IsMeshingQueued { get; set; } = false;
        // Incremented each time MeshFaces is updated by the mesher
        public int MeshVersion = 0;

        public Chunk(int width, int height, int depth, int originX, int originZ)
        {
            Width = width;
            Height = height;
            Depth = depth;
            OriginX = originX;
            OriginZ = originZ;
            blocks = new BlockType[width, height, depth];
        }

        public BlockType this[int x, int y, int z]
        {
            get => blocks[x, y, z];
            set => blocks[x, y, z] = value;
        }

        public bool IsInBounds(int x, int y, int z)
        {
            return x >= 0 && x < Width && y >= 0 && y < Height && z >= 0 && z < Depth;
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
                            blocks[x, y, z] = BlockType.Grass;
                        }
                        else if (y < grassHeight)
                        {
                            blocks[x, y, z] = BlockType.Dirt;
                        }
                        else
                        {
                            blocks[x, y, z] = BlockType.Air;
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
                            blocks[x, y, z] = (y < groundHeight - 3) ? BlockType.Stone : BlockType.Dirt;
                        }
                        else if (y == groundHeight - 1 && !carveCave)
                        {
                            blocks[x, y, z] = BlockType.Grass;
                        }
                        else
                        {
                            blocks[x, y, z] = BlockType.Air;
                        }
                    }
                }
            }
        }
    }
}
