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
        private readonly ConcurrentQueue<ChunkCoordinates> _queue = new();
        private readonly ConcurrentDictionary<ChunkCoordinates, byte> _pending = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _workerTask;

        public MeshWorker(ChunkManager manager, Func<Renderer.IRenderer?> getRenderer)
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
            _getRenderer = getRenderer ?? throw new ArgumentNullException(nameof(getRenderer));
            _workerTask = Task.Run(WorkerLoop, _cts.Token);
        }

        public void Enqueue(ChunkCoordinates coords)
        {
            if (_pending.TryAdd(coords, 0))
            {
                _queue.Enqueue(coords);
            }
        }

        private async Task WorkerLoop()
        {
            var token = _cts.Token;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (_queue.TryDequeue(out var coords))
                    {
                        _pending.TryRemove(coords, out _);
                        if (_manager.TryGetLoadedChunk(coords, out var chunk))
                        {
                            // double-check chunk still needs remesh
                            if (!chunk.NeedsRemesh)
                                continue;

                            // mark as being processed to avoid duplicate enqueues
                            chunk.IsMeshingQueued = true;

                            try
                            {
                                // Snapshot the dirty version BEFORE reading blocks. Any edit that
                                // lands while we mesh bumps DirtyVersion past this, so the chunk
                                // stays dirty and we don't clear a remesh we never applied.
                                int builtVersion = chunk.DirtyVersion;

                                // include adjacent chunks so faces on chunk borders are culled correctly
                                var chunksToPass = new System.Collections.Generic.List<Chunk> { chunk };
                                var chunkX = chunk.OriginX / ChunkManager.ChunkSize;
                                var chunkZ = chunk.OriginZ / ChunkManager.ChunkSize;
                                if (_manager.TryGetLoadedChunk(new ChunkCoordinates(chunkX - 1, chunkZ), out var left)) chunksToPass.Add(left);
                                if (_manager.TryGetLoadedChunk(new ChunkCoordinates(chunkX + 1, chunkZ), out var right)) chunksToPass.Add(right);
                                if (_manager.TryGetLoadedChunk(new ChunkCoordinates(chunkX, chunkZ - 1), out var back)) chunksToPass.Add(back);
                                if (_manager.TryGetLoadedChunk(new ChunkCoordinates(chunkX, chunkZ + 1), out var front)) chunksToPass.Add(front);

                                var faces = Mesher.GenerateMesh(chunksToPass);
                                chunk.MeshFaces = new System.Collections.Generic.List<MeshFace>(faces);
                                System.Threading.Interlocked.Increment(ref chunk.MeshVersion);
                                chunk.MarkMeshed(builtVersion);
                            }
                            finally
                            {
                                chunk.IsMeshingQueued = false;
                            }

                            var renderer = _getRenderer();
                            if (renderer != null && chunk.MeshFaces != null && chunk.MeshFaces.Count > 0)
                            {
                                renderer.UploadChunk(coords, chunk.MeshFaces);
                            }

                            // If an edit landed while we were meshing, the chunk is still dirty
                            // (DirtyVersion moved past the version we built). Requeue it ourselves
                            // so the update isn't stranded waiting on an unrelated remesh trigger.
                            if (chunk.NeedsRemesh)
                            {
                                Enqueue(coords);
                            }
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
            try { _workerTask.Wait(1000); } catch { }
            _cts.Dispose();
        }
    }
}
