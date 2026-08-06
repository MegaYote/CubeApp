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

            // A block edit also affects the faces of the four cardinal neighbours (and the four
            // diagonal neighbours at a chunk corner) that share the edited cell's border: the
            // mesher culls border faces against the neighbour's blocks, and the water pass samples
            // the 2x2 corner neighbourhood. MarkDirty already set NeedsRemesh on those neighbours,
            // but nothing was ENQUEUING them - so a face touching the border stayed stale until the
            // neighbour happened to be re-streamed. Enqueue any flagged border neighbours now so
            // broken faces disappear and placed faces appear immediately.
            int cx = coords.X;
            int cz = coords.Z;
            EnqueueIfDirty(new ChunkCoordinates(cx - 1, cz));
            EnqueueIfDirty(new ChunkCoordinates(cx + 1, cz));
            EnqueueIfDirty(new ChunkCoordinates(cx, cz - 1));
            EnqueueIfDirty(new ChunkCoordinates(cx, cz + 1));
            EnqueueIfDirty(new ChunkCoordinates(cx - 1, cz - 1));
            EnqueueIfDirty(new ChunkCoordinates(cx + 1, cz - 1));
            EnqueueIfDirty(new ChunkCoordinates(cx - 1, cz + 1));
            EnqueueIfDirty(new ChunkCoordinates(cx + 1, cz + 1));
        }

        // Enqueues a neighbour for an immediate remesh if it is loaded AND was flagged dirty by the
        // edit (MarkDirty set NeedsRemesh on it). Priority so the border face updates with the edit.
        private void EnqueueIfDirty(ChunkCoordinates coords)
        {
            if (_manager.TryGetLoadedChunk(coords, out var chunk) && chunk.NeedsRemesh)
            {
                chunk.IsMeshingQueued = true;
                _meshWorker.EnqueuePriority(coords);
            }
        }

        public void Dispose() { }
    }
}
