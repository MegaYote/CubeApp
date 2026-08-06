using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CubeApp
{
    public sealed class MeshWorker : IDisposable
    {
        private readonly ChunkManager _manager;
        private readonly Func<Renderer.IRenderer?> _getRenderer;
        private readonly ConcurrentQueue<ChunkCoordinates> _priorityQueue = new(); // player edits jump the line
        private readonly ConcurrentQueue<ChunkCoordinates> _queue = new();          // normal terrain-streaming remeshes
        private readonly ConcurrentDictionary<ChunkCoordinates, byte> _pending = new();
        private readonly ConcurrentDictionary<ChunkCoordinates, byte> _processing = new(); // chunk a worker has "claimed" right now
        private readonly SemaphoreSlim _workAvailable = new(0); // signals workers that work is queued
        private readonly int _workerCount;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task[] _workerTasks;

        // workerCount = how many mesh jobs can run truly at the same time. 2 is a good starting
        // point: enough that one slow chunk can't block everything else, without stealing so many
        // cores that chunk generation or the render thread start to feel starved.
        public MeshWorker(ChunkManager manager, Func<Renderer.IRenderer?> getRenderer, int workerCount = 2)
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
            _getRenderer = getRenderer ?? throw new ArgumentNullException(nameof(getRenderer));
            _workerCount = Math.Max(1, workerCount);

            _workerTasks = new Task[_workerCount];
            for (int i = 0; i < _workerCount; i++)
            {
                _workerTasks[i] = Task.Run(WorkerLoop, _cts.Token);
            }
        }

        public void Enqueue(ChunkCoordinates coords)
        {
            if (_pending.TryAdd(coords, 0))
            {
                _queue.Enqueue(coords);
                _workAvailable.Release();
            }
        }

        // Deliberately does NOT check _pending like the regular Enqueue does. During heavy chunk
        // generation, this exact chunk might already be sitting in the normal queue - if we bailed
        // out here because of that, a player's edit would get stuck waiting behind it again, which
        // defeats the whole point of this "express lane." Adding it twice is harmless: the worker
        // loop below double-checks whether a chunk still needs remeshing (and claims it before
        // touching it) so a stale duplicate is simply skipped instead of causing any problem.
        public void EnqueuePriority(ChunkCoordinates coords)
        {
            _pending.TryAdd(coords, 0);
            _priorityQueue.Enqueue(coords);
            // Wake workers immediately for player edits - no delay waiting for polling.
            _workAvailable.Release();
        }

        private async Task WorkerLoop()
        {
            var token = _cts.Token;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    // Check priority queue first, then regular queue
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
                        isPriority = false;
                        hasWork = true;
                    }

                    if (hasWork)
                    {
                        _pending.TryRemove(coords, out _);

                        // Prevent duplicate processing by multiple workers
                        if (!_processing.TryAdd(coords, 0))
                        {
                            continue;
                        }

                        // Track whether we need to clear IsMeshingQueued
                        bool needsFlagReset = false;
                        Chunk? chunk = null;
                        try
                        {
                            if (_manager.TryGetLoadedChunk(coords, out chunk))
                            {                                needsFlagReset = true;
                                
                                // double-check chunk still needs remesh
                                if (!chunk.NeedsRemesh)
                                {
                                    continue;
                                }

                                // Do the meshing work. The target chunk plus its loaded neighbours:
                                // cardinal neighbours for greedy border occlusion, and DIAGONAL
                                // neighbours for the water pass (a cell's corner heights sample the
                                // 2x2 block neighbourhood around each corner, which crosses into the
                                // diagonal chunk at a chunk corner). Missing diagonals would make
                                // water surfaces dip at the seams where four chunks meet.
                                var chunksToPass = new List<Chunk> { chunk };
                                var chunkX = chunk.OriginX / ChunkManager.ChunkSize;
                                var chunkZ = chunk.OriginZ / ChunkManager.ChunkSize;
                                if (_manager.TryGetLoadedChunk(new ChunkCoordinates(chunkX - 1, chunkZ), out var left)) chunksToPass.Add(left);
                                if (_manager.TryGetLoadedChunk(new ChunkCoordinates(chunkX + 1, chunkZ), out var right)) chunksToPass.Add(right);
                                if (_manager.TryGetLoadedChunk(new ChunkCoordinates(chunkX, chunkZ - 1), out var back)) chunksToPass.Add(back);
                                if (_manager.TryGetLoadedChunk(new ChunkCoordinates(chunkX, chunkZ + 1), out var front)) chunksToPass.Add(front);
                                if (_manager.TryGetLoadedChunk(new ChunkCoordinates(chunkX - 1, chunkZ - 1), out var diagNW)) chunksToPass.Add(diagNW);
                                if (_manager.TryGetLoadedChunk(new ChunkCoordinates(chunkX + 1, chunkZ - 1), out var diagNE)) chunksToPass.Add(diagNE);
                                if (_manager.TryGetLoadedChunk(new ChunkCoordinates(chunkX - 1, chunkZ + 1), out var diagSW)) chunksToPass.Add(diagSW);
                                if (_manager.TryGetLoadedChunk(new ChunkCoordinates(chunkX + 1, chunkZ + 1), out var diagSE)) chunksToPass.Add(diagSE);

                                var renderer = _getRenderer();
                                var faces = Mesher.GenerateMesh(chunksToPass);

                                // Lock to ensure atomic mesh update. Mesher.GenerateMesh always
                                // returns a List<MeshFace>, and the worker owns it exclusively here,
                                // so hand the instance straight to the chunk instead of copying it.
                                IReadOnlyList<MeshFace> facesToUpload;
                                lock (chunk.MeshLock)
                                {
                                    chunk.MeshFaces = faces as List<MeshFace> ?? new List<MeshFace>(faces);
                                    chunk.MeshVersion++;
                                    chunk.NeedsRemesh = false;
                                    facesToUpload = chunk.MeshFaces;
                                }

                                if (renderer != null && facesToUpload != null)
                                {
                                    if (facesToUpload.Count > 0)
                                    {
                                        if (isPriority)
                                        {
                                            renderer.UploadChunkPriority(coords, facesToUpload);
                                        }
                                        else
                                        {
                                            renderer.UploadChunk(coords, facesToUpload);
                                        }
                                    }
                                    else
                                    {
                                        // A chunk that meshes to nothing (e.g. the last block was
                                        // removed) must release its GPU buffers, otherwise the stale
                                        // mesh keeps rendering forever.
                                        renderer.RemoveChunk(coords);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            // One bad chunk (meshing bug, out-of-bounds, etc.) must never kill the
                            // whole worker pool - otherwise chunks stop appearing and it looks like
                            // terrain generation froze. Log and move on; the chunk stays dirty so a
                            // later pass retries it.
                            try { System.IO.File.AppendAllText("mesh_worker.log", DateTime.Now + " mesh failed " + coords.X + "," + coords.Z + ": " + ex + Environment.NewLine); } catch { }
                        }
                        finally
                        {
                            _processing.TryRemove(coords, out _);
                            if (needsFlagReset && chunk != null)
                            {
                                chunk.IsMeshingQueued = false;
                            }
                        }

                        continue;
                    }

                    // Wait for signal that work is available
                    try
                    {
                        await _workAvailable.WaitAsync(100, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Timeout expired - loop back and re-check queues
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { Task.WaitAll(_workerTasks, 1000); } catch { }
            _cts.Dispose();
            _workAvailable.Dispose();
        }
    }
}