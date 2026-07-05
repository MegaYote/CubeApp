using System;
using System.Collections.Concurrent;
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
        private readonly CancellationTokenSource _cts = new();
        private readonly Task[] _workerTasks;

        // workerCount = how many mesh jobs can run truly at the same time. 2 is a good starting
        // point: enough that one slow chunk can't block everything else, without stealing so many
        // cores that chunk generation or the render loop start to feel starved.
        public MeshWorker(ChunkManager manager, Func<Renderer.IRenderer?> getRenderer, int workerCount = 2)
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
            _getRenderer = getRenderer ?? throw new ArgumentNullException(nameof(getRenderer));

            int count = Math.Max(1, workerCount);
            _workerTasks = new Task[count];
            for (int i = 0; i < count; i++)
            {
                _workerTasks[i] = Task.Run(WorkerLoop, _cts.Token);
            }
        }

        public void Enqueue(ChunkCoordinates coords)
        {
            if (_pending.TryAdd(coords, 0))
            {
                _queue.Enqueue(coords);
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
        }

        private async Task WorkerLoop()
        {
            var token = _cts.Token;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (_priorityQueue.TryDequeue(out var coords) || _queue.TryDequeue(out coords))
                    {
                        _pending.TryRemove(coords, out _);

                        // With more than one worker now running, two of them could both grab the
                        // same chunk coordinate around the same moment. Only the worker that wins
                        // this atomic "claim" processes it; the other skips it as a harmless
                        // duplicate, since the winner is about to remesh it anyway.
                        if (!_processing.TryAdd(coords, 0))
                        {
                            continue;
                        }

                        try
                        {
                            if (_manager.TryGetLoadedChunk(coords, out var chunk))
                            {
                                // double-check chunk still needs remesh
                                if (!chunk.NeedsRemesh)
                                {
                                    chunk.IsMeshingQueued = false;
                                    continue;
                                }

                                // mark as being processed to avoid duplicate enqueues
                                chunk.IsMeshingQueued = true;

                                try
                                {
                                    // include adjacent chunks so faces on chunk borders are culled correctly
                                    var chunksToPass = new System.Collections.Generic.List<Chunk> { chunk };
                                    var chunkX = chunk.OriginX / ChunkManager.ChunkSize;
                                    var chunkZ = chunk.OriginZ / ChunkManager.ChunkSize;
                                    if (_manager.TryGetLoadedChunk(new ChunkCoordinates(chunkX - 1, chunkZ), out var left)) chunksToPass.Add(left);
                                    if (_manager.TryGetLoadedChunk(new ChunkCoordinates(chunkX + 1, chunkZ), out var right)) chunksToPass.Add(right);
                                    if (_manager.TryGetLoadedChunk(new ChunkCoordinates(chunkX, chunkZ - 1), out var back)) chunksToPass.Add(back);
                                    if (_manager.TryGetLoadedChunk(new ChunkCoordinates(chunkX, chunkZ + 1), out var front)) chunksToPass.Add(front);

                                    var faces = Mesher.GenerateMesh(chunksToPass);

                                    // Lock to ensure atomic mesh update - renderer won't see inconsistent state
                                    System.Collections.Generic.IReadOnlyList<MeshFace> facesToUpload;
                                    lock (chunk.MeshLock)
                                    {
                                        chunk.MeshFaces = new System.Collections.Generic.List<MeshFace>(faces);
                                        chunk.MeshVersion++;
                                        chunk.NeedsRemesh = false;
                                        facesToUpload = chunk.MeshFaces;
                                    }

                                    var renderer = _getRenderer();
                                    if (renderer != null && facesToUpload != null && facesToUpload.Count > 0)
                                    {
                                        renderer.UploadChunk(coords, facesToUpload);
                                    }
                                }
                                finally
                                {
                                    chunk.IsMeshingQueued = false;
                                }
                            }
                        }
                        finally
                        {
                            _processing.TryRemove(coords, out _);
                        }

                        continue;
                    }

                    await Task.Delay(8, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { Task.WaitAll(_workerTasks, 1000); } catch { }
            _cts.Dispose();
        }
    }
}