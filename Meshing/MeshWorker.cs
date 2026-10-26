using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Cubuild
{
    public sealed class MeshWorker : IDisposable, IMeshQueue
    {
        private readonly ChunkManager _manager;
        private readonly Func<Renderer.IRenderer?> _getRenderer;
        private readonly ConcurrentQueue<ChunkCoordinates> _priorityQueue = new(); // player edits jump the line
        private readonly ConcurrentQueue<ChunkCoordinates> _queue = new();          // normal terrain-streaming remeshes
        private readonly ConcurrentDictionary<ChunkCoordinates, byte> _pending = new();
        private readonly ConcurrentDictionary<ChunkCoordinates, byte> _processing = new();
        private readonly ConcurrentDictionary<ChunkCoordinates, int> _failureCounts = new();
        private readonly SemaphoreSlim _workAvailable = new(0);
        private readonly int _workerCount;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task[] _workerTasks;

        // Two workers allow a slow chunk to overlap without starving generation or rendering.
        public MeshWorker(ChunkManager manager, Func<Renderer.IRenderer?> getRenderer, int workerCount = 2)
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
            _getRenderer = getRenderer ?? throw new ArgumentNullException(nameof(getRenderer));
            _workerCount = Math.Max(1, workerCount);

            _workerTasks = new Task[_workerCount];
            for (int i = 0; i < _workerCount; i++)
                _workerTasks[i] = Task.Run(WorkerLoop, _cts.Token);
        }

        public void Enqueue(ChunkCoordinates coords)
        {
            if (_pending.TryAdd(coords, 0))
            {
                _queue.Enqueue(coords);
                _workAvailable.Release();
            }
        }

        // Priority entries may intentionally duplicate a normal queue entry. A worker claims each
        // coordinate once and rechecks its dirty revision before spending time meshing it.
        public void EnqueuePriority(ChunkCoordinates coords)
        {
            _pending.TryAdd(coords, 0);
            _priorityQueue.Enqueue(coords);
            _workAvailable.Release();
        }

        private async Task WorkerLoop()
        {
            var token = _cts.Token;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    ChunkCoordinates coords = default;
                    bool isPriority = false;
                    bool hasWork = false;

                    if (_priorityQueue.TryDequeue(out coords))
                    {
                        isPriority = true;
                        hasWork = true;
                    }
                    else if (_queue.TryDequeue(out coords))
                    {
                        hasWork = true;
                    }

                    if (!hasWork)
                    {
                        try { await _workAvailable.WaitAsync(100, token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { }
                        continue;
                    }

                    _pending.TryRemove(coords, out _);
                    if (!_processing.TryAdd(coords, 0))
                        continue;

                    Chunk? chunk = null;
                    bool committed = false;
                    bool failed = false;
                    try
                    {
                        if (_manager.TryGetLoadedChunk(coords, out chunk)
                            && chunk.TryCaptureMeshInputRevision(out long meshInputRevision))
                        {
                            // Capture before collecting inputs or meshing. Any block, lighting, or
                            // neighbor invalidation advances this revision and prevents stale commit.
                            var chunksToPass = new List<Chunk> { chunk };
                            int chunkX = chunk.OriginX / ChunkManager.ChunkSize;
                            int chunkZ = chunk.OriginZ / ChunkManager.ChunkSize;
                            int layer = coords.Layer;
                            if (_manager.TryGetLoadedChunk(new ChunkCoordinates(layer, chunkX - 1, chunkZ), out var left)) chunksToPass.Add(left);
                            if (_manager.TryGetLoadedChunk(new ChunkCoordinates(layer, chunkX + 1, chunkZ), out var right)) chunksToPass.Add(right);
                            if (_manager.TryGetLoadedChunk(new ChunkCoordinates(layer, chunkX, chunkZ - 1), out var back)) chunksToPass.Add(back);
                            if (_manager.TryGetLoadedChunk(new ChunkCoordinates(layer, chunkX, chunkZ + 1), out var front)) chunksToPass.Add(front);
                            if (_manager.TryGetLoadedChunk(new ChunkCoordinates(layer, chunkX - 1, chunkZ - 1), out var diagNW)) chunksToPass.Add(diagNW);
                            if (_manager.TryGetLoadedChunk(new ChunkCoordinates(layer, chunkX + 1, chunkZ - 1), out var diagNE)) chunksToPass.Add(diagNE);
                            if (_manager.TryGetLoadedChunk(new ChunkCoordinates(layer, chunkX - 1, chunkZ + 1), out var diagSW)) chunksToPass.Add(diagSW);
                            if (_manager.TryGetLoadedChunk(new ChunkCoordinates(layer, chunkX + 1, chunkZ + 1), out var diagSE)) chunksToPass.Add(diagSE);
                            // The mesher also occludes against the vertically adjacent layer.
                            if (layer > ChunkManager.DeepLayer && _manager.TryGetLoadedChunk(new ChunkCoordinates(layer - 1, chunkX, chunkZ), out var below)) chunksToPass.Add(below);
                            if (layer < ChunkManager.SkyLayer && _manager.TryGetLoadedChunk(new ChunkCoordinates(layer + 1, chunkX, chunkZ), out var above)) chunksToPass.Add(above);

                            var faces = Mesher.GenerateMesh(chunksToPass,
                                (worldX, worldZ) => _manager.BiomeAt(worldX, worldZ).Id,
                                out var surfaceLodFaces);
                            if (layer == ChunkManager.GroundLayer)
                                _manager.RefreshDeepTopSkyLightFromGround(chunk);

                            var facesToUpload = faces as List<MeshFace> ?? new List<MeshFace>(faces);
                            if (chunk.TryCommitMesh(meshInputRevision, facesToUpload, surfaceLodFaces,
                                out int meshVersion, out long committedInputRevision))
                            {
                                committed = true;
                                _failureCounts.TryRemove(coords, out _);
                                var renderer = _getRenderer();
                                if (renderer != null)
                                {
                                    if (facesToUpload.Count > 0)
                                    {
                                        renderer.UploadChunkVersioned(_manager, chunk, meshVersion, committedInputRevision,
                                            coords, facesToUpload, surfaceLodFaces, isPriority);
                                    }
                                    else
                                    {
                                        renderer.RemoveChunkVersioned(_manager, chunk, meshVersion, committedInputRevision);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        failed = true;
                        // A failed render enqueue is also retried: restore dirty state when this
                        // exact chunk still belongs to this manager.
                        if (chunk != null
                            && _manager.TryGetLoadedChunk(coords, out var current)
                            && ReferenceEquals(current, chunk))
                        {
                            chunk.NeedsRemesh = true;
                        }
                        try
                        {
                            System.IO.File.AppendAllText("mesh_worker.log",
                                DateTime.Now + " mesh failed " + coords.X + "," + coords.Z + ": " + ex + Environment.NewLine);
                        }
                        catch { }
                    }
                    finally
                    {
                        _processing.TryRemove(coords, out _);
                        if (chunk != null)
                            chunk.IsMeshingQueued = false;
                    }

                    if (chunk == null
                        || !_manager.TryGetLoadedChunk(coords, out var activeChunk)
                        || !ReferenceEquals(activeChunk, chunk)
                        || !chunk.NeedsRemesh)
                    {
                        continue;
                    }

                    if (failed)
                    {
                        int failures = _failureCounts.AddOrUpdate(coords, 1, static (_, n) => Math.Min(n + 1, 7));
                        int delayMs = Math.Min(5000, 100 * (1 << Math.Min(failures - 1, 6)));
                        try { await Task.Delay(delayMs, token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { break; }
                    }

                    // A changed revision or exception must not consume the only dirty entry.
                    // Enqueue deduplicates with any event raised while this job was running.
                    if (!committed || chunk.NeedsRemesh)
                        Enqueue(coords);
                }
            }
            catch (OperationCanceledException) { }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { Task.WaitAll(_workerTasks, 5000); } catch { }
            _cts.Dispose();
            _workAvailable.Dispose();
        }
    }
}
