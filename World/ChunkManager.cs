using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Cubuild.World;

namespace Cubuild
{
    /// <summary>
    /// Chunk storage + streaming for the whole world. The world is three vertically-stacked
    /// layers (three independent world generators):
    ///
    ///   Deep layer (0): OriginY=-256, Height=192, world -256..-65.
    ///     Lazy stone+caves; generated on demand when the player digs deep.
    ///   Ground layer (1): OriginY=-64, Height=448, world -64..383.
    ///     Terrain band + caves + monoliths; sky above.
    ///   Sky layer (2):    OriginY=384, Height=640, world 384..1023.
    ///     Empty air at generation; sky islands fill lazily when the player climbs high.
    ///
    /// Layers share the (X, Z) column grid, so chunk keys carry the layer
    /// (<see cref="ChunkCoordinates.Layer"/>). Block access by world Y routes to the correct layer.
    /// </summary>
    public sealed class ChunkManager
    {
        public const int ChunkSize = 16;
        public const int DeepLayer = 0;
        public const int GroundLayer = 1;
        public const int SkyLayer = 2;
        public const int DeepOriginY = -256;
        public const int DeepHeight = 192;
        public const int GroundOriginY = -64;
        public const int GroundHeight = 448;
        public const int SkyOriginY = 384;
        public const int SkyHeight = 640;
        // Back-compat aliases (most code means the ground layer when it says "the world").
        public const int WorldOriginY = GroundOriginY;
        public const int ChunkHeight = GroundHeight;

        // Keep transition support small: at most the center chunk and its four cardinals are
        // prefetched before a layer change. A separate camera ray can queue the same tiny
        // neighborhood below/above a visible seam opening, even when it is far from the player.
        private const int VerticalBoundaryPrefetchDistance = 32;
        private const int VerticalBoundaryPrefetchRadiusChunks = 1;
        private const int DeepBoundaryRayMaxDistance = 128;
        private const int OtherBoundaryRayMaxDistance = 64;
        private const int NearBoundaryRayMaxDistance = 128;
        private const double VerticalRayStep = 0.25;

        private static readonly int[] LayerOriginY = { DeepOriginY, GroundOriginY, SkyOriginY };
        private static readonly int[] LayerHeight = { DeepHeight, GroundHeight, SkyHeight };

        private readonly ConcurrentDictionary<ChunkCoordinates, Chunk> loadedChunks = new();
        // Chunk coords that have been modified (player edits / fluid flow) since load; these are
        // the only chunks a world save needs to serialize.
        private readonly HashSet<ChunkCoordinates> _modifiedChunks = new();
        private readonly object _modifiedLock = new();
        public IReadOnlyCollection<ChunkCoordinates> ModifiedChunks
        {
            get { lock (_modifiedLock) return _modifiedChunks.ToArray(); }
        }

        // ---- unload cache (survives chunk streaming) ----
        // Modified chunks remain cached until saved/reloaded. Trusted chunks keep their sparse
        // delta here rather than a full 16x16x-height array; full/untrusted chunks retain arrays.
        // There is intentionally no eviction: dropping an edited chunk would silently lose data.
        private readonly Dictionary<ChunkCoordinates, SavedChunk> _unloadedModifiedChunks = new();
        private readonly object _unloadCacheLock = new();

        private readonly PriorityQueue<ChunkRequest, double> queue = new();
        private readonly object queueLock = new();
        private readonly ConcurrentDictionary<ChunkCoordinates, byte> pendingGeneration = new();
        private readonly IChunkProvider[] chunkProviders = new IChunkProvider[3];

        /// <summary>Fired when any chunk flips NeedsRemesh false -> true. Wired to the
        /// MeshScheduler's dirty-list by GameWorld so the scheduler never scans every loaded
        /// chunk to find mesh work.</summary>
        public Action<Chunk>? ChunkDirty;

        /// <summary>Fired once when a chunk is first created/loaded (called from BOTH the main
        /// thread and chunk-gen workers, so handlers must be thread-safe). Used to seed hanging
        /// rail track for rails that come back with a loaded world.</summary>
        public Action<Chunk>? ChunkLoaded;

        public ChunkManager(IChunkProvider? deepProvider = null, IChunkProvider? groundProvider = null, IChunkProvider? skyProvider = null)
        {
            chunkProviders[DeepLayer] = deepProvider ?? new DeepChunkProvider();
            chunkProviders[GroundLayer] = groundProvider ?? new TerrainChunkProvider();
            chunkProviders[SkyLayer] = skyProvider ?? new SkyChunkProvider();
        }

        private void WireDirty(Chunk chunk)
        {
            chunk.OnDirty = ChunkDirty;
            // Newly generated/loaded chunks are already marked dirty; make sure the scheduler
            // knows without waiting for a false->true transition.
            if (chunk.NeedsRemesh && !chunk.IsMeshingQueued)
            {
                ChunkDirty?.Invoke(chunk);
            }
        }

        public static int LayerForWorldY(int worldY)
        {
            if (worldY < GroundOriginY) return DeepLayer;
            if (worldY >= SkyOriginY) return SkyLayer;
            return GroundLayer;
        }
        public static int OriginYForLayer(int layer) => layer switch
        {
            DeepLayer => DeepOriginY,
            SkyLayer => SkyOriginY,
            _ => GroundOriginY,
        };
        public static int HeightForLayer(int layer) => layer switch
        {
            DeepLayer => DeepHeight,
            SkyLayer => SkyHeight,
            _ => GroundHeight,
        };

        /// <summary>Returns the immediately adjacent layer when the player is within the small
        /// transition band at either end of the current layer; otherwise returns -1.</summary>
        public static int AdjacentLayerNearBoundary(int layer, double worldY)
        {
            if (layer < DeepLayer || layer > SkyLayer) return -1;

            int originY = OriginYForLayer(layer);
            int height = HeightForLayer(layer);
            if (layer > DeepLayer && worldY <= originY + VerticalBoundaryPrefetchDistance)
                return layer - 1;
            if (layer < SkyLayer && worldY >= originY + height - 1 - VerticalBoundaryPrefetchDistance)
                return layer + 1;
            return -1;
        }

        public Chunk GetOrCreateChunk(int chunkX, int chunkZ) => GetOrCreateChunk(GroundLayer, chunkX, chunkZ);

        public Chunk GetOrCreateChunk(int layer, int chunkX, int chunkZ)
        {
            var key = new ChunkCoordinates(layer, chunkX, chunkZ);
            Chunk? candidate = null;
            var result = loadedChunks.GetOrAdd(key, _ =>
            {
                var chunk = chunkProviders[layer].GenerateChunk(chunkX, chunkZ, ChunkSize, HeightForLayer(layer));
                chunk.NeedsRemesh = true;
                candidate = chunk;
                return chunk;
            });

            if (ReferenceEquals(candidate, result))
            {
                // Restore cached data before seam initialization. Sparse snapshots are replayed
                // against the same in-process generated base; full snapshots remain untrusted.
                bool restoredCachedChunk = false;
                lock (_unloadCacheLock)
                {
                    if (_unloadedModifiedChunks.TryGetValue(key, out var cached))
                    {
                        restoredCachedChunk = true;
                        lock (result.MeshLock)
                        {
                            if (cached.IsDelta)
                            {
                                result.ApplyDeltaCells(cached.DeltaCells);
                            }
                            else
                            {
                                Array.Copy(cached.Blocks, result.RawBlocks, Math.Min(cached.Blocks.Length, result.RawBlocks.Length));
                                Array.Copy(cached.Meta, result.RawMeta, Math.Min(cached.Meta.Length, result.RawMeta.Length));
                                result.MarkDeltaBaselineUntrusted();
                            }
                            result.InvalidateLightGrid();
                        }
                        result.HasRuntimeEdits = true;
                        _unloadedModifiedChunks.Remove(key);
                        result.NeedsRemesh = true;
                    }
                }

                // A ground-layer cave can punch through the bedrock floor at world -64 (local 0).
                // When the deep chunk below (world -65) generates, copy those openings onto its top
                // row so the player can actually fall through into the deep layer instead of hitting
                // a solid ceiling one block down. Same for the sky layer's bottom vs ground top.
                if (layer == DeepLayer)
                {
                    SyncDeepAccess(chunkX, chunkZ, result);
                }
                else if (layer == GroundLayer)
                {
                    // Reverse order: the deep chunk may already be loaded (e.g. save load). Push
                    // the ground chunk's bottom-row openings onto the deep chunk's top row.
                    if (loadedChunks.TryGetValue(new ChunkCoordinates(DeepLayer, chunkX, chunkZ), out var deep))
                    {
                        SyncDeepAccess(chunkX, chunkZ, deep);
                    }
                }
                // Try to enable tracking only after generation/seam initialization. Any bypass
                // write above has already marked the chunk untrusted and cannot be promoted.
                result.MarkGeneratedBaselineTrusted();
                // Sky islands are a lazy direct-array population whose timing depends on player
                // altitude. Newly populated chunks are full/untrusted rather than pretending they
                // share the empty, reproducible sky baseline.
                if (layer == SkyLayer && chunkProviders[SkyLayer] is World.SkyChunkProvider skyProvider
                    && skyProvider.Islands.AutoHighFill)
                {
                    if (restoredCachedChunk)
                        skyProvider.Islands.MarkChunkFilled(chunkX, chunkZ);
                    else if (skyProvider.Islands.HighFillChunk(chunkX, chunkZ, result, ChunkSize, HeightForLayer(layer)))
                        MarkChunkUntrusted(result);
                }
                // A seam is meshed from the lower chunk's perspective. When either side changes
                // residency, the other side must rebuild its border faces against the new state.
                MarkVerticalNeighborsDirty(layer, chunkX, chunkZ);
                // A newly loaded chunk can expose faces on adjacent, already-meshed chunks
                // (border faces are culled against neighbors, and an absent neighbor is treated
                // as air). Mark those neighbors dirty so their border faces get rebuilt now,
                // instead of staying wrong until some unrelated edit/unload triggers a remesh.
                if (loadedChunks.TryGetValue(new ChunkCoordinates(layer, chunkX - 1, chunkZ), out var left))
                    left.NeedsRemesh = true;
                if (loadedChunks.TryGetValue(new ChunkCoordinates(layer, chunkX + 1, chunkZ), out var right))
                    right.NeedsRemesh = true;
                if (loadedChunks.TryGetValue(new ChunkCoordinates(layer, chunkX, chunkZ - 1), out var back))
                    back.NeedsRemesh = true;
                if (loadedChunks.TryGetValue(new ChunkCoordinates(layer, chunkX, chunkZ + 1), out var front))
                    front.NeedsRemesh = true;
                WireDirty(result);
                // Announce the new chunk. Handlers run on a gen worker, so they must be
                // thread-safe (rail sag only enqueues the chunk and scans it later).
                ChunkLoaded?.Invoke(result);
            }

            return result;
        }

        private void MarkVerticalNeighborDirty(int layer, int chunkX, int chunkZ)
        {
            if (loadedChunks.TryGetValue(new ChunkCoordinates(layer, chunkX, chunkZ), out var neighbor))
                neighbor.NeedsRemesh = true;
        }

        private void MarkVerticalNeighborsDirty(int layer, int chunkX, int chunkZ)
        {
            if (layer > DeepLayer) MarkVerticalNeighborDirty(layer - 1, chunkX, chunkZ);
            if (layer < SkyLayer) MarkVerticalNeighborDirty(layer + 1, chunkX, chunkZ);
        }

        /// <summary>
        /// When the deep chunk below a ground chunk is created, mirror any cave openings that
        /// broke through the ground bedrock floor (world -64 = ground local 0) onto the deep
        /// chunk's TOP row (world -65 = deep local 191). This is what lets players fall from a
        /// ground-layer cave straight down into the deep layer. The deep chunk is generated
        /// lazily AFTER the ground chunk, so the ground chunk is normally available.
        /// </summary>
        private void SyncDeepAccess(int chunkX, int chunkZ, Chunk deepChunk)
        {
            byte[] deepRaw = deepChunk.RawBlocks;
            int deepH = deepChunk.Height;
            bool haveGround = loadedChunks.TryGetValue(new ChunkCoordinates(GroundLayer, chunkX, chunkZ), out var ground);
            int groundH = haveGround ? ground!.Height : 0;
            int width = haveGround ? Math.Min(deepChunk.Width, ground!.Width) : deepChunk.Width;
            int depth = haveGround ? Math.Min(deepChunk.Depth, ground!.Depth) : deepChunk.Depth;
            bool metadataChanged = false;
            bool geometryChanged = false;

            void SyncColumns()
            {
                byte[]? groundRaw = haveGround ? ground!.RawBlocks : null;
                byte[]? lightGrid = null;
                bool currentLightGrid = haveGround && ground!.TryGetCurrentLightGrid(out lightGrid)
                    && lightGrid!.Length == groundRaw!.Length;

                for (int x = 0; x < deepChunk.Width; x++)
                {
                    for (int z = 0; z < deepChunk.Depth; z++)
                    {
                        bool seamIsAir = haveGround && x < width && z < depth
                            && groundRaw![(x * ground!.Depth + z) * groundH] == BlockRegistry.AirId; // ground local y=0 (world -64)
                        byte skySource = 0;
                        if (seamIsAir && currentLightGrid)
                        {
                            int groundIdx = (x * ground!.Depth + z) * groundH;
                            skySource = (byte)(lightGrid![groundIdx] >> 4);
                        }
                        metadataChanged |= deepChunk.SetDeepTopSkyLight(x, z, skySource);

                        if (seamIsAir)
                        {
                            int deepIdx = (x * deepChunk.Depth + z) * deepH + (deepH - 1); // deep top (world -65)
                            if (deepRaw[deepIdx] != BlockRegistry.AirId)
                            {
                                // Direct storage write is intentional: routing this synchronization
                                // through block-edit callbacks would recursively dirty the paired seam.
                                deepRaw[deepIdx] = (byte)BlockRegistry.AirId;
                                geometryChanged = true;
                            }
                        }
                    }
                }
            }

            void SyncAndInvalidate()
            {
                SyncColumns();
                if (metadataChanged || geometryChanged)
                    deepChunk.InvalidateLightGrid();
            }

            if (haveGround)
            {
                // Mesh generation writes LightGrid under this same lock. If a recent edit/load
                // invalidated it, the source remains conservatively zero until the next ground mesh.
                lock (ground!.MeshLock)
                lock (deepChunk.MeshLock)
                    SyncAndInvalidate();
            }
            else
            {
                lock (deepChunk.MeshLock)
                    SyncAndInvalidate();
            }
            if (metadataChanged || geometryChanged)
                deepChunk.NeedsRemesh = true;
            if (geometryChanged)
            {
                // Seam synchronization writes raw storage without old cell values. Persist a full
                // snapshot; never manufacture a sparse patch for this derived mutation.
                MarkChunkUntrusted(deepChunk);
            }
            if (geometryChanged && haveGround) ground!.NeedsRemesh = true;
        }

        private void SyncDeepAccessIfLoaded(int chunkX, int chunkZ)
        {
            if (loadedChunks.TryGetValue(new ChunkCoordinates(DeepLayer, chunkX, chunkZ), out var deepChunk))
                SyncDeepAccess(chunkX, chunkZ, deepChunk);
        }

        /// <summary>Copies freshly meshed ground seam sky values to the paired deep chunk.
        /// Called after the ground target's ChunkLighting pass has populated its LightGrid.</summary>
        public void RefreshDeepTopSkyLightFromGround(Chunk groundChunk)
        {
            if (groundChunk == null || LayerForWorldY(groundChunk.OriginY) != GroundLayer) return;

            int chunkX = FloorDiv(groundChunk.OriginX, ChunkSize);
            int chunkZ = FloorDiv(groundChunk.OriginZ, ChunkSize);
            var groundKey = new ChunkCoordinates(GroundLayer, chunkX, chunkZ);
            var deepKey = new ChunkCoordinates(DeepLayer, chunkX, chunkZ);
            if (!loadedChunks.TryGetValue(groundKey, out var loadedGround)
                || !ReferenceEquals(loadedGround, groundChunk)
                || !loadedChunks.TryGetValue(deepKey, out var deepChunk))
                return;

            bool changed = false;
            lock (groundChunk.MeshLock)
            lock (deepChunk.MeshLock)
            {
                byte[] raw = groundChunk.RawBlocks;
                byte[]? grid;
                bool currentGrid = groundChunk.TryGetCurrentLightGrid(out grid)
                    && grid!.Length == raw.Length;
                for (int x = 0; x < Math.Min(groundChunk.Width, deepChunk.Width); x++)
                {
                    for (int z = 0; z < Math.Min(groundChunk.Depth, deepChunk.Depth); z++)
                    {
                        int groundIdx = (x * groundChunk.Depth + z) * groundChunk.Height;
                        byte level = raw[groundIdx] == BlockRegistry.AirId && currentGrid
                            ? (byte)(grid![groundIdx] >> 4)
                            : (byte)0;
                        changed |= deepChunk.SetDeepTopSkyLight(x, z, level);
                    }
                }
                if (changed)
                    deepChunk.InvalidateLightGrid();
            }

            if (changed)
            {
                deepChunk.NeedsRemesh = true;
            }
        }

        // Stamps a saved full chunk's block+meta data over the (re)generated chunk on world load.
        public void ApplySavedChunk(int layer, int chunkX, int chunkZ, byte[] blocks, byte[] meta)
        {
            var chunk = GetOrCreateChunk(layer, chunkX, chunkZ);
            lock (chunk.MeshLock)
            {
                if (blocks != null) Array.Copy(blocks, chunk.RawBlocks, Math.Min(blocks.Length, chunk.RawBlocks.Length));
                if (meta != null) Array.Copy(meta, chunk.RawMeta, Math.Min(meta.Length, chunk.RawMeta.Length));
                chunk.InvalidateLightGrid();
            }
            MarkChunkUntrusted(chunk);
            chunk.NeedsRemesh = true;
            // Saved block arrays can change either seam row, even though their individual edits
            // are not replayed through MarkDirty.
            if (layer == GroundLayer || layer == DeepLayer)
                SyncDeepAccessIfLoaded(chunkX, chunkZ);
            MarkVerticalNeighborsDirty(layer, chunkX, chunkZ);
        }

        public void ApplySavedChunk(int chunkX, int chunkZ, byte[] blocks, byte[] meta)
            => ApplySavedChunk(GroundLayer, chunkX, chunkZ, blocks, meta);

        /// <summary>Recreates the current deterministic provider baseline, applies sparse final
        /// cell values, and seeds provenance so later edits can continue using deltas.</summary>
        public void ApplySavedDeltaChunk(int layer, int chunkX, int chunkZ, IReadOnlyList<SavedChunkDeltaCell> cells)
        {
            if (layer < DeepLayer || layer > SkyLayer)
                throw new InvalidDataException($"Sparse chunk delta has unsupported layer {layer}.");
            if (layer == SkyLayer)
                throw new InvalidDataException("Sparse sky-layer deltas are unsupported because sky-island fill depends on when the chunk is visited; the save must contain a full sky chunk.");
            // GetOrCreateChunk already generates the deterministic baseline and performs the
            // layer-seam initialization. Reuse that one chunk instead of generating a second
            // temporary baseline (which nearly doubles load-time generation and memory cost).
            var chunk = GetOrCreateChunk(layer, chunkX, chunkZ);
            if (!chunk.HasTrustedDeltaBaseline)
                throw new InvalidDataException("Sparse chunk delta does not match the generated chunk baseline; a full chunk snapshot is required.");
            lock (chunk.MeshLock)
            {
                chunk.ApplyDeltaCells(cells);
                chunk.InvalidateLightGrid();
            }
            chunk.HasRuntimeEdits = true;
            chunk.SurfaceLodFaces = null;
            chunk.NeedsRemesh = true;
            MarkModified(new ChunkCoordinates(layer, chunkX, chunkZ));
            if (layer == GroundLayer || layer == DeepLayer)
                SyncDeepAccessIfLoaded(chunkX, chunkZ);
            MarkVerticalNeighborsDirty(layer, chunkX, chunkZ);
        }

        /// <summary>Captures a modified chunk from either loaded storage or its unload cache.</summary>
        public bool TryGetSaveChunk(ChunkCoordinates coords, bool forceFull, out SavedChunk savedChunk)
        {
            // Sky islands are lazily generated based on player altitude, so an empty provider
            // baseline is not stable across when a chunk is visited. Always keep sky chunks full.
            forceFull |= coords.Layer == SkyLayer;
            if (loadedChunks.TryGetValue(coords, out var loaded))
            {
                savedChunk = CaptureChunkForSave(loaded, coords, forceFull);
                return true;
            }

            SavedChunk? cachedSnapshot;
            lock (_unloadCacheLock)
            {
                if (!_unloadedModifiedChunks.TryGetValue(coords, out cachedSnapshot!))
                {
                    savedChunk = new SavedChunk();
                    return false;
                }
                cachedSnapshot = CloneSavedChunk(cachedSnapshot);
            }

            if (!forceFull || !cachedSnapshot.IsDelta)
            {
                savedChunk = cachedSnapshot;
                return true;
            }

            // An old-generation world cannot advertise a delta baseline. Materialize the cached
            // patch against the exact in-process current provider and write it as a full snapshot.
            var materialized = chunkProviders[coords.Layer].GenerateChunk(
                coords.X, coords.Z, ChunkSize, HeightForLayer(coords.Layer));
            materialized.ApplyDeltaCells(cachedSnapshot.DeltaCells);
            savedChunk = new SavedChunk
            {
                Layer = coords.Layer,
                X = coords.X,
                Z = coords.Z,
                Blocks = (byte[])materialized.RawBlocks.Clone(),
                Meta = (byte[])materialized.RawMeta.Clone(),
            };
            return true;
        }

        private static SavedChunk CaptureChunkForSave(Chunk chunk, ChunkCoordinates coords, bool forceFull)
        {
            if (!forceFull && chunk.TryCaptureDelta(out var cells))
            {
                return new SavedChunk
                {
                    Layer = coords.Layer,
                    X = coords.X,
                    Z = coords.Z,
                    IsDelta = true,
                    DeltaCells = cells,
                };
            }
            return new SavedChunk
            {
                Layer = coords.Layer,
                X = coords.X,
                Z = coords.Z,
                Blocks = (byte[])chunk.RawBlocks.Clone(),
                Meta = (byte[])chunk.RawMeta.Clone(),
            };
        }

        private static SavedChunk CloneSavedChunk(SavedChunk source) => new()
        {
            Layer = source.Layer,
            X = source.X,
            Z = source.Z,
            IsDelta = source.IsDelta,
            Blocks = (byte[])source.Blocks.Clone(),
            Meta = (byte[])source.Meta.Clone(),
            DeltaCells = (SavedChunkDeltaCell[])source.DeltaCells.Clone(),
        };

        /// <summary>Marks a direct/unknown chunk mutation as full-snapshot-only.</summary>
        public void MarkChunkUntrusted(Chunk chunk)
        {
            int chunkX = FloorDiv(chunk.OriginX, ChunkSize);
            int chunkZ = FloorDiv(chunk.OriginZ, ChunkSize);
            int layer = LayerForWorldY(chunk.OriginY);
            chunk.MarkDeltaBaselineUntrusted();
            chunk.HasRuntimeEdits = true;
            chunk.SurfaceLodFaces = null;
            MarkModified(new ChunkCoordinates(layer, chunkX, chunkZ));
        }

        private void MarkModified(ChunkCoordinates key)
        {
            lock (_modifiedLock) _modifiedChunks.Add(key);
        }

        private bool IsModified(ChunkCoordinates key)
        {
            lock (_modifiedLock) return _modifiedChunks.Contains(key);
        }

        public bool TrySetBlock(int worldX, int worldY, int worldZ, int blockId)
        {
            return TrySetBlock(worldX, worldY, worldZ, blockId, 0);
        }

        public bool TrySetBlock(int worldX, int worldY, int worldZ, int blockId, int meta)
        {
            int layer = LayerForWorldY(worldY);
            int chunkX = FloorDiv(worldX, ChunkSize);
            int chunkZ = FloorDiv(worldZ, ChunkSize);
            var chunk = GetOrCreateChunk(layer, chunkX, chunkZ);
            int localX = worldX - chunk.OriginX;
            int localZ = worldZ - chunk.OriginZ;
            int localY = chunk.WorldYToLocal(worldY);
            if (!chunk.IsInBounds(localX, localY, localZ))
            {
                return false;
            }

            byte finalMeta = (byte)meta;
            int index = chunk.Index(localX, localY, localZ);
            int oldId;
            byte oldMeta;
            lock (chunk.MeshLock)
            {
                oldId = chunk[localX, localY, localZ];
                oldMeta = chunk.GetMeta(localX, localY, localZ);
                chunk[localX, localY, localZ] = blockId;
                chunk.SetMeta(localX, localY, localZ, finalMeta);
                chunk.RecordTrackedMutation(index, (byte)oldId, oldMeta, (byte)blockId, finalMeta);
                // Keep the raw mutation and its revision change indivisible to mesh capture and
                // completion. Invalidating before the writes let a worker accept a pre-edit mesh.
                chunk.InvalidateLightGrid();
            }
            MarkDirty(chunkX, chunkZ, localX, localY, localZ, layer, blockId, oldId);
            return true;
        }

        /// <summary>
        /// Like <see cref="TrySetBlock(int,int,int,int,int)"/> but refuses to generate the target
        /// chunk. Fluid simulation uses this so a spreading water edge can never force terrain
        /// generation into unloaded territory; it simply doesn't flow there.
        /// </summary>
        public bool TrySetBlockLoadedOnly(int worldX, int worldY, int worldZ, int blockId, int meta)
        {
            int layer = LayerForWorldY(worldY);
            int chunkX = FloorDiv(worldX, ChunkSize);
            int chunkZ = FloorDiv(worldZ, ChunkSize);
            if (!loadedChunks.TryGetValue(new ChunkCoordinates(layer, chunkX, chunkZ), out var chunk))
            {
                return false;
            }

            int localX = worldX - chunk.OriginX;
            int localZ = worldZ - chunk.OriginZ;
            int localY = chunk.WorldYToLocal(worldY);
            if (!chunk.IsInBounds(localX, localY, localZ))
            {
                return false;
            }

            byte finalMeta = (byte)meta;
            int index = chunk.Index(localX, localY, localZ);
            int oldId;
            byte oldMeta;
            lock (chunk.MeshLock)
            {
                oldId = chunk[localX, localY, localZ];
                oldMeta = chunk.GetMeta(localX, localY, localZ);
                chunk[localX, localY, localZ] = blockId;
                chunk.SetMeta(localX, localY, localZ, finalMeta);
                chunk.RecordTrackedMutation(index, (byte)oldId, oldMeta, (byte)blockId, finalMeta);
                chunk.InvalidateLightGrid();
            }
            MarkDirty(chunkX, chunkZ, localX, localY, localZ, layer, blockId, oldId);
            return true;
        }

        /// <summary>Reads the rail-droop byte at a loaded cell. False when the chunk isn't loaded
        /// or the coord is out of bounds (mirrors the flush in TryGetLoadedBlockAndMeta).</summary>
        public bool TryGetLoadedChunkAndDrop(int worldX, int worldY, int worldZ, out byte droop)
        {
            droop = 0;
            int layer = LayerForWorldY(worldY);
            int chunkX = FloorDiv(worldX, ChunkSize);
            int chunkZ = FloorDiv(worldZ, ChunkSize);
            if (!loadedChunks.TryGetValue(new ChunkCoordinates(layer, chunkX, chunkZ), out var chunk))
                return false;

            int localX = worldX - chunk.OriginX;
            int localZ = worldZ - chunk.OriginZ;
            int localY = chunk.WorldYToLocal(worldY);
            if (!chunk.IsInBounds(localX, localY, localZ)) return false;

            droop = chunk.GetRailDroop(localX, localY, localZ);
            return true;
        }

        /// <summary>Writes the rail-droop byte at a loaded cell. Returns true only when the value
        /// actually changed, so the caller can skip a re-mesh. Never generates the chunk.</summary>
        public bool TrySetRailDroop(int worldX, int worldY, int worldZ, byte value)
        {
            int layer = LayerForWorldY(worldY);
            int chunkX = FloorDiv(worldX, ChunkSize);
            int chunkZ = FloorDiv(worldZ, ChunkSize);
            if (!loadedChunks.TryGetValue(new ChunkCoordinates(layer, chunkX, chunkZ), out var chunk))
                return false;

            int localX = worldX - chunk.OriginX;
            int localZ = worldZ - chunk.OriginZ;
            int localY = chunk.WorldYToLocal(worldY);
            if (!chunk.IsInBounds(localX, localY, localZ)) return false;
            if (chunk.GetRailDroop(localX, localY, localZ) == value) return false;

            chunk.SetRailDroop(localX, localY, localZ, value);
            return true;
        }

        private void MarkDirty(int chunkX, int chunkZ, int localX, int localY, int localZ, int layer, int blockId = 0, int oldId = 0)
        {
            if (!loadedChunks.TryGetValue(new ChunkCoordinates(layer, chunkX, chunkZ), out var chunk))
            {
                return;
            }

            MarkModified(new ChunkCoordinates(layer, chunkX, chunkZ));

            chunk.HasRuntimeEdits = true;
            chunk.SurfaceLodFaces = null;
            // The block write already invalidated this chunk's light grid atomically with the
            // storage mutation. This dirty assignment also advances its mesh-input revision.
            chunk.NeedsRemesh = true;

            // Only seam-row edits affect the vertically adjacent mesh; preserve the existing
            // horizontal-neighbor invalidation rules below.
            if (localY == 0 && layer > DeepLayer)
                MarkVerticalNeighborDirty(layer - 1, chunkX, chunkZ);
            if (localY == chunk.Height - 1 && layer < SkyLayer)
                MarkVerticalNeighborDirty(layer + 1, chunkX, chunkZ);

            // Deep seam sky is sampled from a fresh ground LightGrid only through an open bottom
            // cell. Recompute the source and mirrored geometry after edits; SyncDeepAccess writes
            // raw arrays directly and never re-enters BlockEdit/MarkDirty.
            if (layer == GroundLayer
                || (layer == DeepLayer && localY == chunk.Height - 1))
                SyncDeepAccessIfLoaded(chunkX, chunkZ);

            // Light-emitting blocks (torches, glowstone) spread light across the entire 3×3
            // lighting region, not just border faces. Always wake neighbours so their LightGrids
            // get re-baked with the new emission.
            bool isEmitter = BlockRegistry.LightEmissionOf(blockId) > 0 || BlockRegistry.LightEmissionOf(oldId) > 0;

            if (isEmitter || localX == 0)
            {
                if (loadedChunks.TryGetValue(new ChunkCoordinates(layer, chunkX - 1, chunkZ), out var left))
                    left.NeedsRemesh = true;
            }
            if (isEmitter || localX == ChunkSize - 1)
            {
                if (loadedChunks.TryGetValue(new ChunkCoordinates(layer, chunkX + 1, chunkZ), out var right))
                    right.NeedsRemesh = true;
            }
            if (isEmitter || localZ == 0)
            {
                if (loadedChunks.TryGetValue(new ChunkCoordinates(layer, chunkX, chunkZ - 1), out var back))
                    back.NeedsRemesh = true;
            }
            if (isEmitter || localZ == ChunkSize - 1)
            {
                if (loadedChunks.TryGetValue(new ChunkCoordinates(layer, chunkX, chunkZ + 1), out var front))
                    front.NeedsRemesh = true;
            }

            if (isEmitter || (localX == 0 && localZ == 0))
            {
                if (loadedChunks.TryGetValue(new ChunkCoordinates(layer, chunkX - 1, chunkZ - 1), out var diagNW))
                    diagNW.NeedsRemesh = true;
            }
            if (isEmitter || (localX == ChunkSize - 1 && localZ == 0))
            {
                if (loadedChunks.TryGetValue(new ChunkCoordinates(layer, chunkX + 1, chunkZ - 1), out var diagNE))
                    diagNE.NeedsRemesh = true;
            }
            if (isEmitter || (localX == 0 && localZ == ChunkSize - 1))
            {
                if (loadedChunks.TryGetValue(new ChunkCoordinates(layer, chunkX - 1, chunkZ + 1), out var diagSW))
                    diagSW.NeedsRemesh = true;
            }
            if (isEmitter || (localX == ChunkSize - 1 && localZ == ChunkSize - 1))
            {
                if (loadedChunks.TryGetValue(new ChunkCoordinates(layer, chunkX + 1, chunkZ + 1), out var diagSE))
                    diagSE.NeedsRemesh = true;
            }
        }

        public bool TryGetLoadedBlock(int worldX, int worldY, int worldZ, out int blockId)
        {
            int layer = LayerForWorldY(worldY);
            int chunkX = FloorDiv(worldX, ChunkSize);
            int chunkZ = FloorDiv(worldZ, ChunkSize);
            if (!loadedChunks.TryGetValue(new ChunkCoordinates(layer, chunkX, chunkZ), out var chunk))
            {
                blockId = BlockRegistry.AirId;
                return false;
            }

            int localX = worldX - chunk.OriginX;
            int localZ = worldZ - chunk.OriginZ;
            int localY = chunk.WorldYToLocal(worldY);
            if (!chunk.IsInBounds(localX, localY, localZ))
            {
                blockId = BlockRegistry.AirId;
                return false;
            }

            blockId = chunk[localX, localY, localZ];
            return true;
        }

        public bool TryGetLoadedBlockAndMeta(int worldX, int worldY, int worldZ, out int blockId, out byte meta)
        {
            int layer = LayerForWorldY(worldY);
            int chunkX = FloorDiv(worldX, ChunkSize);
            int chunkZ = FloorDiv(worldZ, ChunkSize);
            if (!loadedChunks.TryGetValue(new ChunkCoordinates(layer, chunkX, chunkZ), out var chunk))
            {
                blockId = BlockRegistry.AirId;
                meta = 0;
                return false;
            }

            int localX = worldX - chunk.OriginX;
            int localZ = worldZ - chunk.OriginZ;
            int localY = chunk.WorldYToLocal(worldY);
            if (!chunk.IsInBounds(localX, localY, localZ))
            {
                blockId = BlockRegistry.AirId;
                meta = 0;
                return false;
            }

            blockId = chunk[localX, localY, localZ];
            meta = chunk.GetMeta(localX, localY, localZ);
            return true;
        }

        /// <summary>Block id at world coords, or AirId when the chunk isn't loaded / coords out of bounds.</summary>
        public int GetBlockAt(int worldX, int worldY, int worldZ)
        {
            return TryGetLoadedBlock(worldX, worldY, worldZ, out var id) ? id : BlockRegistry.AirId;
        }

        /// <summary>Block metadata at world coords, or 0 when the chunk isn't loaded / coords out of bounds.</summary>
        public byte GetMetaAt(int worldX, int worldY, int worldZ)
        {
            return TryGetLoadedBlockAndMeta(worldX, worldY, worldZ, out _, out var meta) ? meta : (byte)0;
        }

        /// <summary>Authoritative biome definition for a world column, when the ground provider exposes terrain biomes.</summary>
        public BiomeDefinition BiomeAt(int worldX, int worldZ)
        {
            return chunkProviders[GroundLayer] is TerrainChunkProvider terrainProvider
                ? terrainProvider.BiomeAt(worldX, worldZ)
                : BiomeRegistry.Get("plains");
        }

        public bool TryGetLoadedChunk(ChunkCoordinates coords, out Chunk chunk)
        {
            return loadedChunks.TryGetValue(coords, out chunk);
        }

        /// <summary>
        /// Cheap light estimate for mob spawning (the monster darkness gate). Walks up from
        /// (x,y,z): if an opaque block is found before the scan limit the cell is in shade (cave /
        /// under a roof) and reads 0; otherwise it is open sky and reads 15 - nightDim (day =
        /// bright, night = dim). Block-light sources (torches) are not consulted - matching the
        /// spawner's "darkness only" intent.
        /// </summary>
        public int GetSkyLightEstimate(int x, int y, int z, int skylightSubtracted)
        {
            for (int wy = y + 1; wy <= y + 24; wy++)
            {
                int id = GetBlockAt(x, wy, z);
                if (id == BlockRegistry.AirId) continue;
                if (BlockRegistry.IsOpaque(id)) return 0;
            }
            int sky = 15 - skylightSubtracted;
            return sky < 0 ? 0 : sky;
        }

        /// <summary>
        /// Light value for the spawner's light gates (0..15), day/night-aware AND torch-aware.
        /// Cells that see the sky use the time-aware estimate (full 15 by day, dimmed at night);
        /// shaded cells (caves, under roofs) read the baked torch/glow block-light grid, so a
        /// torch-lit room genuinely protects you from monsters. 0 when the cell's chunk hasn't
        /// meshed yet (fail dark - freshly generated caves may spawn until their first mesh).
        /// </summary>
        public int GetSpawnLight(int x, int y, int z, int skylightSubtracted)
        {
            int sky = GetSkyLightEstimate(x, y, z, skylightSubtracted);
            if (sky > 0) return sky; // sees the sky: time of day is the only factor

            // Shaded: use the block-light nibble only. Daylight is a separate nibble, and is
            // deliberately excluded here so skylight does not masquerade as torch light.
            int layer = LayerForWorldY(y);
            int cx = FloorDiv(x, ChunkSize);
            int cz = FloorDiv(z, ChunkSize);
            if (TryGetLoadedChunk(new ChunkCoordinates(layer, cx, cz), out var chunk) && chunk.LightGrid != null)
            {
                int localX = x - chunk.OriginX;
                int localY = y - chunk.OriginY;
                int localZ = z - chunk.OriginZ;
                if (localX >= 0 && localX < ChunkSize && localY >= 0 && localY < chunk.Height && localZ >= 0 && localZ < ChunkSize)
                {
                    return chunk.LightGrid[(localX * chunk.Depth + localZ) * chunk.Height + localY] & 0x0F;
                }
            }
            return 0;
        }

        private static int FloorDiv(int value, int divisor)
        {
            int result = value / divisor;
            if ((value ^ divisor) < 0 && value % divisor != 0)
            {
                result--;
            }

            return result;
        }

        /// <summary>
        /// Live view of all loaded chunks. No defensive copy: ConcurrentDictionary.Values
        /// enumerates over an internal snapshot, so iterating is safe against chunks being
        /// generated/removed by worker threads while the render thread scans.
        /// </summary>
        public ICollection<Chunk> GetLoadedChunks() => loadedChunks.Values;

        public bool EnsureChunksAround(int centerChunkX, int centerChunkZ, int radius, int layer = GroundLayer)
        {
            bool addedNewChunk = false;
            long radiusSq = (long)radius * radius; // circle, not square: skips the ~27% corner chunks
            for (int dz = -radius; dz <= radius; dz++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    // Euclidean circle around the player's chunk: |d| <= r instead of the old
                    // Chebyshev square (max(dx,dz) <= r). Saves corner-chunk generation/mesh/memory
                    // with no visible quality loss - the corners were outside the view circle anyway.
                    if ((long)dx * dx + (long)dz * dz > radiusSq) continue;

                    int chunkX = centerChunkX + dx;
                    int chunkZ = centerChunkZ + dz;
                    var key = new ChunkCoordinates(layer, chunkX, chunkZ);
                    if (!loadedChunks.ContainsKey(key))
                    {
                        addedNewChunk = true;
                    }

                    GetOrCreateChunk(layer, chunkX, chunkZ);
                }
            }

            return addedNewChunk;
        }

        /// <summary>
        /// Queue any not-yet-loaded chunks within <paramref name="radius"/> for background
        /// generation, closest-first. Cheap to call every tick: already-loaded or already-queued
        /// chunks are skipped. Actual generation happens off the main thread via
        /// <see cref="TryGenerateNext"/>.
        /// </summary>
        public void RequestChunksAround(int centerChunkX, int centerChunkZ, int radius, Point3D cameraPosition, int layer = GroundLayer)
        {
            long radiusSq = (long)radius * radius; // circle, not square: skips the ~27% corner chunks
            for (int dz = -radius; dz <= radius; dz++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    // Euclidean circle (matches EnsureChunksAround): only queue chunks within the
                    // radius so corner chunks outside the view circle are never generated.
                    if ((long)dx * dx + (long)dz * dz > radiusSq) continue;

                    int chunkX = centerChunkX + dx;
                    int chunkZ = centerChunkZ + dz;
                    RequestChunkIfNeeded(layer, chunkX, chunkZ, cameraPosition);
                }
            }
        }

        /// <summary>Queues a bounded amount of support for an imminent or visible vertical layer
        /// change. Requests use the normal priority queue; no terrain generation runs on the caller.</summary>
        public void RequestVerticalBoundarySupport(int currentLayer, int centerChunkX, int centerChunkZ,
            Point3D cameraPosition, Point3D cameraForward)
        {
            if (currentLayer < DeepLayer || currentLayer > SkyLayer) return;

            int nearLayer = AdjacentLayerNearBoundary(currentLayer, cameraPosition.Y);
            if (nearLayer >= 0)
            {
                // A five-chunk ring smooths transitions even when there is no visible opening.
                RequestChunksAround(centerChunkX, centerChunkZ, VerticalBoundaryPrefetchRadiusChunks,
                    cameraPosition, nearLayer);
            }

            Point3D direction = cameraForward.Normalized();
            int adjacentLayer = direction.Y < 0 && currentLayer > DeepLayer
                ? currentLayer - 1
                : direction.Y > 0 && currentLayer < SkyLayer
                    ? currentLayer + 1
                    : -1;
            if (adjacentLayer < 0) return;

            bool towardLowerBoundary = adjacentLayer < currentLayer;
            double directionY = direction.Y;
            if (Math.Abs(directionY) < 1e-6) return;

            double boundaryY = towardLowerBoundary
                ? OriginYForLayer(currentLayer)
                : OriginYForLayer(currentLayer) + HeightForLayer(currentLayer);
            double distance = (boundaryY - cameraPosition.Y) / directionY;
            if (distance < 0) return;

            int maxDistance = adjacentLayer == DeepLayer
                ? DeepBoundaryRayMaxDistance
                : nearLayer == adjacentLayer
                    ? NearBoundaryRayMaxDistance
                    : OtherBoundaryRayMaxDistance;
            if (distance > maxDistance) return;

            double hitX = cameraPosition.X + direction.X * distance;
            double hitZ = cameraPosition.Z + direction.Z * distance;
            int apertureX = (int)Math.Floor(hitX);
            int apertureZ = (int)Math.Floor(hitZ);
            int boundaryCellY = towardLowerBoundary
                ? OriginYForLayer(currentLayer)
                : OriginYForLayer(currentLayer) + HeightForLayer(currentLayer) - 1;

            // The ray cannot infer unseen terrain: every cell it crosses in the current layer
            // must already be loaded and free of opaque solid blocks, and the seam cell itself
            // must be air. This also avoids treating absent chunks as empty shafts.
            if (!IsClearLoadedRayToBoundary(currentLayer, cameraPosition, direction, distance,
                    apertureX, boundaryCellY, apertureZ))
                return;

            // Request only the aperture's adjacent-layer chunk and its four cardinals.
            int apertureChunkX = FloorDiv(apertureX, ChunkSize);
            int apertureChunkZ = FloorDiv(apertureZ, ChunkSize);
            RequestChunksAround(apertureChunkX, apertureChunkZ, VerticalBoundaryPrefetchRadiusChunks,
                cameraPosition, adjacentLayer);
        }

        private bool IsClearLoadedRayToBoundary(int currentLayer, Point3D origin, Point3D direction,
            double distance, int apertureX, int boundaryCellY, int apertureZ)
        {
            int layerOriginY = OriginYForLayer(currentLayer);
            int layerEndY = layerOriginY + HeightForLayer(currentLayer);
            int lastX = int.MinValue;
            int lastY = int.MinValue;
            int lastZ = int.MinValue;

            // Short fixed steps are smaller than a voxel, so the ray cannot skip an intervening
            // cell. Use floor for all voxel addressing, including negative world coordinates.
            for (double t = 0; t < distance; t += VerticalRayStep)
            {
                int x = (int)Math.Floor(origin.X + direction.X * t);
                int y = (int)Math.Floor(origin.Y + direction.Y * t);
                int z = (int)Math.Floor(origin.Z + direction.Z * t);
                if (y < layerOriginY || y >= layerEndY) return false;
                if (x == lastX && y == lastY && z == lastZ) continue;

                if (!TryGetLoadedBlock(x, y, z, out int blockId)) return false;
                if (BlockRegistry.IsSolid(blockId) && BlockRegistry.IsOpaque(blockId)) return false;
                lastX = x;
                lastY = y;
                lastZ = z;
            }

            if (!TryGetLoadedBlock(apertureX, boundaryCellY, apertureZ, out int boundaryBlock))
                return false;
            return boundaryBlock == BlockRegistry.AirId;
        }

        private void RequestChunkIfNeeded(int layer, int chunkX, int chunkZ, Point3D cameraPosition)
        {
            var key = new ChunkCoordinates(layer, chunkX, chunkZ);
            if (loadedChunks.ContainsKey(key)) return;
            if (pendingGeneration.TryAdd(key, 0)) EnqueueChunk(layer, chunkX, chunkZ, cameraPosition);
        }

        /// <summary>
        /// Pull the next queued chunk request and generate it (on a background worker). Returns
        /// true only when a chunk was actually created, so callers can trigger a remesh.
        /// </summary>
        public bool TryGenerateNext()
        {
            if (!TryDequeueNext(out var request))
            {
                return false;
            }

            var key = new ChunkCoordinates(request.Layer, request.X, request.Z);
            bool created = !loadedChunks.ContainsKey(key);

            GetOrCreateChunk(request.Layer, request.X, request.Z);
            pendingGeneration.TryRemove(key, out _);
            return created;
        }

        public List<ChunkCoordinates> UnloadChunksOutside(int centerChunkX, int centerChunkZ, int radius)
        {
            var removed = new List<ChunkCoordinates>();
            long radiusSq = (long)radius * radius; // circle, matching the load loops
            foreach (var key in loadedChunks.Keys)
            {
                long dx = key.X - (long)centerChunkX;
                long dz = key.Z - (long)centerChunkZ;
                if (dx * dx + dz * dz > radiusSq)
                {
                    if (loadedChunks.TryRemove(key, out var removedChunk))
                    {
                        removed.Add(key);
                        pendingGeneration.TryRemove(key, out _);

                        // Preserve player edits: snapshot modified chunks before discarding them so
                        // their data survives until the next world save, and can be re-applied when
                        // the player returns. Unmodified chunks regenerate identically from the seed,
                        // so they don't need caching.
                        if (IsModified(key))
                        {
                            lock (_unloadCacheLock)
                            {
                                _unloadedModifiedChunks[key] = CaptureChunkForSave(removedChunk, key, forceFull: false);
                            }
                        }

                        if (loadedChunks.TryGetValue(new ChunkCoordinates(key.Layer, key.X - 1, key.Z), out var left))
                            left.NeedsRemesh = true;
                        if (loadedChunks.TryGetValue(new ChunkCoordinates(key.Layer, key.X + 1, key.Z), out var right))
                            right.NeedsRemesh = true;
                        if (loadedChunks.TryGetValue(new ChunkCoordinates(key.Layer, key.X, key.Z - 1), out var back))
                            back.NeedsRemesh = true;
                        if (loadedChunks.TryGetValue(new ChunkCoordinates(key.Layer, key.X, key.Z + 1), out var front))
                            front.NeedsRemesh = true;
                        if (key.Layer == GroundLayer)
                            SyncDeepAccessIfLoaded(key.X, key.Z); // no loaded source => clear deep sky access
                        MarkVerticalNeighborsDirty(key.Layer, key.X, key.Z);
                    }
                }
            }

            return removed;
        }

        public void EnqueueChunk(int chunkX, int chunkZ, Point3D cameraPosition)
            => EnqueueChunk(GroundLayer, chunkX, chunkZ, cameraPosition);

        public void EnqueueChunk(int layer, int chunkX, int chunkZ, Point3D cameraPosition)
        {
            double priority = ComputeDistancePriority(chunkX, chunkZ, cameraPosition);
            lock (queueLock)
            {
                queue.Enqueue(new ChunkRequest(layer, chunkX, chunkZ), priority);
            }
        }

        public bool TryDequeueNext(out ChunkRequest request)
        {
            lock (queueLock)
            {
                if (queue.Count > 0)
                {
                    request = queue.Dequeue();
                    return true;
                }
            }

            request = default;
            return false;
        }

        private static double ComputeDistancePriority(int chunkX, int chunkZ, Point3D cameraPosition)
        {
            double centerX = chunkX * ChunkSize + ChunkSize / 2.0;
            double centerZ = chunkZ * ChunkSize + ChunkSize / 2.0;
            double dx = cameraPosition.X - centerX;
            double dz = cameraPosition.Z - centerZ;
            return Math.Sqrt(dx * dx + dz * dz);
        }
    }
}

