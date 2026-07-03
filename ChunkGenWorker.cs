using System;
using System.Threading;
using System.Threading.Tasks;

namespace CubeApp
{
    /// <summary>
    /// Generates chunks off the main thread. A small pool of workers drains the
    /// <see cref="ChunkManager"/>'s distance-prioritized request queue so that walking into new
    /// territory no longer stalls the render loop on terrain generation. Generation is pure CPU
    /// work and thread-safe (each request produces an independent chunk, inserted through the
    /// manager's concurrent map), so multiple workers run in parallel.
    /// </summary>
    public sealed class ChunkGenWorker : IDisposable
    {
        private readonly ChunkManager _manager;
        private readonly Action _onChunkGenerated;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task[] _workers;

        public ChunkGenWorker(ChunkManager manager, Action onChunkGenerated, int workerCount)
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
            _onChunkGenerated = onChunkGenerated ?? throw new ArgumentNullException(nameof(onChunkGenerated));

            int count = Math.Max(1, workerCount);
            _workers = new Task[count];
            for (int i = 0; i < count; i++)
            {
                _workers[i] = Task.Run(WorkerLoop, _cts.Token);
            }
        }

        private async Task WorkerLoop()
        {
            var token = _cts.Token;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (_manager.TryGenerateNext())
                    {
                        // A new chunk (and its dirtied neighbors) needs meshing.
                        _onChunkGenerated();
                        continue;
                    }

                    await Task.Delay(4, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { Task.WaitAll(_workers, 1000); } catch { }
            _cts.Dispose();
        }
    }
}
