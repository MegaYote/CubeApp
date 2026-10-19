using System;
using System.Collections.Generic;

namespace Cubuild
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
        // Runtime-only rail droop (same layout as blocks/meta). Quantized 0..255 over
        // [0, RailSag.MaxDrop] blocks: how far below the rail plane this cell's track has sunk
        // because it lost the block under it. Lives on the chunk rather than in a world table
        // so the background mesh workers can read it with the same thread-safety story as
        // blocks/meta. NOT persisted and NOT network-synced: it re-settles itself the moment a
        // chunk reloads, so there is nothing worth saving (leaf-decay timers work the same way).
        // Zero means "flat" - every rail that has never been solved, and every anchored rail.
        private readonly byte[] railDroop;
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

        /// <summary>
        /// Optional far-field surface mesh derived from this chunk's actual block data. It is a
        /// visual-only heightfield approximation; full MeshFaces remain authoritative for nearby
        /// rendering and gameplay. Null means the chunk is ineligible for this representation.
        /// </summary>
        public IReadOnlyList<MeshFace>? SurfaceLodFaces { get; set; }

        /// <summary>True once block data has been edited or restored from a save. Player-edited
        /// chunks deliberately stay full-detail at every distance.</summary>
        public volatile bool HasRuntimeEdits;

        /// <summary>
        /// Cached per-block combined light (0..15) for this chunk, same layout as RawBlocks
        /// (column-major ((x*Depth+z)*Height+y)). Filled by the mesher while it flood-fills light
        /// for faces, so mobs/entities can sample the block they stand on with one array lookup
        /// instead of rebuilding a lighting region. Null until first mesh; stale after edits until
        /// the next remesh (same lifetime as MeshFaces).
        /// </summary>
        // Packed per-cell light channels: sky light in the high nibble, block/emitted light in
        // the low nibble. This stays one byte per block while allowing renderers and gameplay
        // checks to distinguish ambient daylight from torches.
        public byte[]? LightGrid;
        private bool _needsRemesh = true;
        /// <summary>True when this chunk's mesh must be regenerated. The setter fires
        /// <see cref="OnDirty"/> (used by MeshScheduler's dirty-list) whenever it transitions
        /// to true, so the scheduler never scans every loaded chunk to find work.
        /// Set from BOTH chunk-gen worker threads and the main thread, so it's lock-guarded.</summary>
        private readonly object _remeshLock = new();

        public bool NeedsRemesh
        {
            get => _needsRemesh;
            set
            {
                bool fire = false;
                lock (_remeshLock)
                {
                    if (value && !_needsRemesh)
                    {
                        _needsRemesh = true;
                        fire = true;
                    }
                    else
                    {
                        _needsRemesh = value;
                    }
                }
                if (fire) OnDirty?.Invoke(this);
            }
        }
        /// <summary>Called once when NeedsRemesh flips false -> true. Wired to the MeshScheduler's
        /// dirty-list by GameWorld after constructing the scheduler.</summary>
        public Action<Chunk>? OnDirty;
        // Prevent duplicate enqueueing while meshing is pending. Written by the mesh workers
        // (background) and the mesh scheduler (main thread), so volatile for cross-thread visibility.
        public volatile bool IsMeshingQueued = false;
        // Incremented each time MeshFaces is updated by the mesher
        public int MeshVersion = 0;

        /// <summary>World Y of the highest solid block in this chunk (or OriginY-1 if empty).
        /// Computed by the mesher from the live block scan. Used by heightmap occlusion culling
        /// to skip chunks hidden behind nearer terrain.</summary>
        public int TopSolidY = int.MinValue;

        /// <summary>Absolute highest solid world Y (the true per-chunk max), kept alongside the
        /// representative <see cref="TopSolidY"/> so callers can pick the aggressiveness.</summary>
        public int MaxSolidY = int.MinValue;

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
            railDroop = new byte[width * height * depth];
        }

        /// <summary>Flat index of a local block coordinate (column-major; y contiguous).</summary>
        public int Index(int x, int y, int z) => (x * Depth + z) * Height + y;

        /// <summary>Raw block storage for hot paths (mesher/lighting). Values are block ids.</summary>
        public byte[] RawBlocks => blocks;
        /// <summary>Raw metadata storage (same layout as RawBlocks). Values are block metadata.</summary>
        public byte[] RawMeta => meta;

        public byte GetMeta(int x, int y, int z) => meta[(x * Depth + z) * Height + y];
        public void SetMeta(int x, int y, int z, byte value) => meta[(x * Depth + z) * Height + y] = value;

        /// <summary>Raw rail-droop storage (same layout as blocks/meta). Quantized 0..255 over
        /// [0, RailSag.MaxDrop] blocks. See <see cref="railDroop"/> for the lifetime rules.</summary>
        public byte[] RawRailDroop => railDroop;

        public byte GetRailDroop(int x, int y, int z) => railDroop[(x * Depth + z) * Height + y];
        public void SetRailDroop(int x, int y, int z, byte value) => railDroop[(x * Depth + z) * Height + y] = value;

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
