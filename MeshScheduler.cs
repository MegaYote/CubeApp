using System;

namespace CubeApp
{
    public sealed class MeshScheduler
    {
        private readonly ChunkManager _manager;
        private readonly MeshWorker _meshWorker;

        public bool NeedsMeshUpdate { get; set; }

        public MeshScheduler(ChunkManager manager, MeshWorker meshWorker)
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
            _meshWorker = meshWorker ?? throw new ArgumentNullException(nameof(meshWorker));
        }

        public int Update()
        {
            int queued = 0;

            foreach (var chunk in _manager.GetLoadedChunks())
            {
                if (!chunk.NeedsRemesh)
                    continue;

                if (chunk.IsMeshingQueued)
                    continue;

                chunk.IsMeshingQueued = true;

                int chunkX = chunk.OriginX / ChunkManager.ChunkSize;
                int chunkZ = chunk.OriginZ / ChunkManager.ChunkSize;

                _meshWorker.Enqueue(new ChunkCoordinates(chunkX, chunkZ));

                queued++;
            }

            NeedsMeshUpdate = false;

            return queued;
        }

// Jumps a specific chunk to the front of the mesh queue. Call this the moment the player
        // edits a block, so they see the change right away instead of waiting on whatever terrain
        // happens to be streaming in.
        public void RequestImmediateRemesh(ChunkCoordinates coords)
        {
            if (!_manager.TryGetLoadedChunk(coords, out var chunk))
                return;

            // Always set NeedsRemesh so the chunk will be remeshed
            chunk.NeedsRemesh = true;
            chunk.IsMeshingQueued = true;
            _meshWorker.EnqueuePriority(coords);
        }

        public void Dispose() { }
    }
}
