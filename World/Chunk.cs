using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Cubuild
{
    /// <summary>A final block/meta value stored in a sparse chunk save delta.</summary>
    public readonly struct SavedChunkDeltaCell
    {
        public int Index { get; }
        public byte Block { get; }
        public byte Meta { get; }

        public SavedChunkDeltaCell(int index, byte block, byte meta)
        {
            Index = index;
            Block = block;
            Meta = meta;
        }
    }

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
        // Per-column sky light reaching the ground/deep seam (0..15). Runtime-only and
        // conservatively zero until ChunkManager has a current ground LightGrid to sample.
        private readonly byte[] deepTopSkyLight;
        private readonly object _meshLock = new();
        private readonly object _deltaLock = new();
        private readonly Dictionary<int, DeltaCellState> _deltaCells = new();
        private bool _deltaTrackingInitialized;
        private bool _deltaBaselineTrusted;
        private long _lightingRevision;
        private long _meshInputRevision;
        private long _lightGridRevision = -1;

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

        /// <summary>Runtime-only worldgen fluid outlets to wake after this chunk is published.
        /// The positions are world coordinates; source blocks and metadata are already in RawBlocks
        /// and RawMeta. Saved block arrays remain authoritative, so consumers must revalidate them.</summary>
        public List<(int X, int Y, int Z)> WorldgenFluidSources { get; } = new();

        /// <summary>True once block data has been edited or restored from a save. Player-edited
        /// chunks deliberately stay full-detail at every distance.</summary>
        public volatile bool HasRuntimeEdits;

        private readonly struct DeltaCellState
        {
            public readonly byte OriginalBlock;
            public readonly byte OriginalMeta;
            public readonly byte FinalBlock;
            public readonly byte FinalMeta;

            public DeltaCellState(byte originalBlock, byte originalMeta, byte finalBlock, byte finalMeta)
            {
                OriginalBlock = originalBlock;
                OriginalMeta = originalMeta;
                FinalBlock = finalBlock;
                FinalMeta = finalMeta;
            }
        }

        /// <summary>True only when this chunk still has an exact, reproducible generated base
        /// and every post-generation block mutation went through the tracked manager paths.</summary>
        public bool HasTrustedDeltaBaseline
        {
            get { lock (_deltaLock) return _deltaTrackingInitialized && _deltaBaselineTrusted; }
        }

        /// <summary>Marks a freshly generated chunk as delta-eligible after generation and seam
        /// initialization have completed. A chunk already marked untrusted cannot be promoted.</summary>
        public void MarkGeneratedBaselineTrusted()
        {
            lock (_deltaLock)
            {
                if (_deltaTrackingInitialized) return;
                _deltaTrackingInitialized = true;
                _deltaBaselineTrusted = true;
                _deltaCells.Clear();
            }
        }

        /// <summary>Disables sparse-delta saving when a write bypassed the before-value tracker.</summary>
        public void MarkDeltaBaselineUntrusted()
        {
            lock (_deltaLock)
            {
                _deltaTrackingInitialized = true;
                _deltaBaselineTrusted = false;
                _deltaCells.Clear();
            }
        }

        /// <summary>Records a manager-mediated write. The first before-value is retained until the
        /// cell returns to it, at which point the sparse entry is removed.</summary>
        public void RecordTrackedMutation(int index, byte oldBlock, byte oldMeta, byte finalBlock, byte finalMeta)
        {
            lock (_deltaLock)
            {
                if (!_deltaTrackingInitialized || !_deltaBaselineTrusted) return;
                if (_deltaCells.TryGetValue(index, out var existing))
                {
                    if (existing.OriginalBlock == finalBlock && existing.OriginalMeta == finalMeta)
                        _deltaCells.Remove(index);
                    else
                        _deltaCells[index] = new DeltaCellState(existing.OriginalBlock, existing.OriginalMeta, finalBlock, finalMeta);
                }
                else if (oldBlock != finalBlock || oldMeta != finalMeta)
                {
                    _deltaCells[index] = new DeltaCellState(oldBlock, oldMeta, finalBlock, finalMeta);
                }
            }
        }

        /// <summary>Applies saved/cache delta values to the current generated base and seeds the
        /// tracker with each cell's actual base value. Caller must only use this for a verified
        /// matching generator identity.</summary>
        public void ApplyDeltaCells(IReadOnlyList<SavedChunkDeltaCell> cells)
        {
            lock (_deltaLock)
            {
                _deltaCells.Clear();
                _deltaTrackingInitialized = true;
                _deltaBaselineTrusted = true;
                int previousIndex = -1;
                foreach (var cell in cells)
                {
                    if (cell.Index < 0 || cell.Index >= blocks.Length || cell.Index <= previousIndex)
                        throw new InvalidDataException("Sparse chunk delta indexes must be unique, sorted, and inside the chunk.");
                    previousIndex = cell.Index;
                    byte originalBlock = blocks[cell.Index];
                    byte originalMeta = meta[cell.Index];
                    blocks[cell.Index] = cell.Block;
                    meta[cell.Index] = cell.Meta;
                    if (originalBlock != cell.Block || originalMeta != cell.Meta)
                        _deltaCells[cell.Index] = new DeltaCellState(originalBlock, originalMeta, cell.Block, cell.Meta);
                }
            }
        }

        /// <summary>Copies a stable sparse view for saving. False means the caller must use full
        /// arrays because this chunk's provenance is unknown.</summary>
        public bool TryCaptureDelta(out SavedChunkDeltaCell[] cells)
        {
            lock (_deltaLock)
            {
                if (!_deltaTrackingInitialized || !_deltaBaselineTrusted)
                {
                    cells = Array.Empty<SavedChunkDeltaCell>();
                    return false;
                }

                cells = new SavedChunkDeltaCell[_deltaCells.Count];
                int i = 0;
                foreach (var entry in _deltaCells)
                    cells[i++] = new SavedChunkDeltaCell(entry.Key, entry.Value.FinalBlock, entry.Value.FinalMeta);
                Array.Sort(cells, static (a, b) => a.Index.CompareTo(b.Index));
                return true;
            }
        }

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
        /// to true, so the scheduler never scans every loaded chunk to find work. All invalidations
        /// advance the mesh-input revision under MeshLock, including repeated true assignments.
        /// Set from BOTH chunk-gen worker threads and the main thread.</summary>
        public bool NeedsRemesh
        {
            get { lock (_meshLock) return _needsRemesh; }
            set
            {
                bool fire = false;
                lock (_meshLock)
                {
                    if (value)
                    {
                        _meshInputRevision++;
                        fire = !_needsRemesh;
                        _needsRemesh = true;
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

        /// <summary>Captures the mesh input revision only while this chunk is currently dirty.</summary>
        public bool TryCaptureMeshInputRevision(out long revision)
        {
            lock (_meshLock)
            {
                revision = _meshInputRevision;
                return _needsRemesh;
            }
        }

        /// <summary>Captures a stamp for a clean cached mesh, optionally requiring the supplied
        /// face lists to be the exact lists currently published by this chunk.</summary>
        public bool TryCaptureCleanMeshStamp(IReadOnlyList<MeshFace> expectedFaces,
            IReadOnlyList<MeshFace>? expectedSurfaceLodFaces, out int meshVersion, out long meshInputRevision)
        {
            lock (_meshLock)
            {
                meshVersion = MeshVersion;
                meshInputRevision = _meshInputRevision;
                return !_needsRemesh
                    && ReferenceEquals(MeshFaces, expectedFaces)
                    && (expectedSurfaceLodFaces == null || ReferenceEquals(SurfaceLodFaces, expectedSurfaceLodFaces));
            }
        }

        /// <summary>Checks that a mesh stamp is still the exact clean input/output generation.</summary>
        public bool IsCleanMeshStampCurrent(int meshVersion, long meshInputRevision)
        {
            lock (_meshLock)
            {
                return !_needsRemesh
                    && MeshVersion == meshVersion
                    && _meshInputRevision == meshInputRevision;
            }
        }

        /// <summary>Atomically publishes a mesh only if no mesh input changed since capture.</summary>
        public bool TryCommitMesh(long sourceRevision, List<MeshFace> faces,
            IReadOnlyList<MeshFace>? surfaceLodFaces, out int meshVersion, out long committedInputRevision)
        {
            bool fire = false;
            lock (_meshLock)
            {
                if (_meshInputRevision != sourceRevision)
                {
                    if (!_needsRemesh)
                    {
                        _needsRemesh = true;
                        fire = true;
                    }
                    meshVersion = MeshVersion;
                    committedInputRevision = _meshInputRevision;
                }
                else
                {
                    MeshFaces = faces;
                    SurfaceLodFaces = surfaceLodFaces;
                    meshVersion = ++MeshVersion;
                    committedInputRevision = _meshInputRevision;
                    _needsRemesh = false;
                    return true;
                }
            }

            if (fire) OnDirty?.Invoke(this);
            return false;
        }

        /// <summary>World Y of the highest solid block in this chunk (or OriginY-1 if empty).
        /// Computed by the mesher from the live block scan. Used by heightmap occlusion culling
        /// to skip chunks hidden behind nearer terrain.</summary>
        public int TopSolidY = int.MinValue;

        /// <summary>Absolute highest solid world Y (the true per-chunk max), kept alongside the
        /// representative <see cref="TopSolidY"/> so callers can pick the aggressiveness.</summary>
        public int MaxSolidY = int.MinValue;

        public object MeshLock => _meshLock;

        /// <summary>Invalidates cached lighting after block data changes.</summary>
        public void InvalidateLightGrid()
        {
            bool fire = false;
            lock (_meshLock)
            {
                Interlocked.Increment(ref _lightingRevision);
                _meshInputRevision++;
                LightGrid = null;
                _lightGridRevision = -1;
                fire = !_needsRemesh;
                _needsRemesh = true;
            }
            if (fire) OnDirty?.Invoke(this);
        }

        /// <summary>Revision to capture before computing the chunk's next LightGrid.</summary>
        public long LightingRevision => Volatile.Read(ref _lightingRevision);

        /// <summary>Publishes a LightGrid only if no block edit invalidated the lighting pass.</summary>
        public void CommitLightGrid(byte[] grid, long sourceRevision)
        {
            lock (_meshLock)
            {
                if (LightingRevision != sourceRevision)
                {
                    LightGrid = null;
                    _lightGridRevision = -1;
                    return;
                }

                LightGrid = grid;
                Volatile.Write(ref _lightGridRevision, sourceRevision);
            }
        }

        /// <summary>Reads the cached grid only when it still matches the current block revision.</summary>
        public bool TryGetCurrentLightGrid(out byte[]? grid)
        {
            lock (_meshLock)
            {
                if (LightGrid != null && _lightGridRevision == LightingRevision)
                {
                    grid = LightGrid;
                    return true;
                }

                grid = null;
                return false;
            }
        }

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
            deepTopSkyLight = new byte[width * depth];
        }

        /// <summary>Sky-light level at the ground/deep seam for this column. Zero until verified.</summary>
        public byte GetDeepTopSkyLight(int x, int z)
        {
            if (x < 0 || x >= Width || z < 0 || z >= Depth) return 0;
            return Volatile.Read(ref deepTopSkyLight[x * Depth + z]);
        }

        /// <summary>Updates runtime seam-light metadata. Returns true when the level changed.</summary>
        public bool SetDeepTopSkyLight(int x, int z, byte skyLight)
        {
            if (x < 0 || x >= Width || z < 0 || z >= Depth) return false;
            skyLight = (byte)Math.Min(15, (int)skyLight);
            ref byte level = ref deepTopSkyLight[x * Depth + z];
            byte previous = Volatile.Read(ref level);
            if (previous == skyLight) return false;
            Volatile.Write(ref level, skyLight);
            return true;
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
