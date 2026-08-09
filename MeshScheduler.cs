using System;
using System.Collections.Generic;

namespace CubeApp
{
    public sealed class MeshScheduler
    {
        private readonly ChunkManager _manager;
        private readonly IMeshQueue _meshQueue;

        public bool NeedsMeshUpdate { get; set; }

        /// <summary>False when running headless with a no-op queue (dedicated server / tests):
        /// mesh versions never advance, so callers that wait on remesh must release immediately.</summary>
        public bool HasRealMeshQueue { get; }

        // Dirty-list of chunks needing a mesh rebuild (instead of scanning every loaded chunk).
        // Populated via MarkDirty (called wherever NeedsRemesh is set); Update() drains only these.
        private readonly HashSet<ChunkCoordinates> _dirty = new();
        // Reusable scratch so Update() doesn't allocate a List per call.
        private readonly List<ChunkCoordinates> _dirtyScratch = new();

        public MeshScheduler(ChunkManager manager, IMeshQueue meshQueue)
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
            _meshQueue = meshQueue ?? throw new ArgumentNullException(nameof(meshQueue));
            HasRealMeshQueue = meshQueue is not NoOpMeshQueue;
        }

        /// <summary>Registers a chunk for a mesh rebuild. Call wherever NeedsRemesh is set so the
        /// scheduler can drain a dirty-list instead of scanning every loaded chunk.</summary>
        public void MarkDirty(ChunkCoordinates coords)
        {
            _dirty.Add(coords);
        }

        public void MarkDirtyChunk(Chunk chunk)
        {
            int chunkX = chunk.OriginX / ChunkManager.ChunkSize;
            int chunkZ = chunk.OriginZ / ChunkManager.ChunkSize;
            int layer = ChunkManager.LayerForWorldY(chunk.OriginY);
            _dirty.Add(new ChunkCoordinates(layer, chunkX, chunkZ));
        }

        public int Update()
        {
            int queued = 0;

            if (_dirty.Count == 0)
            {
                NeedsMeshUpdate = false;
                return 0;
            }

            // Copy so we can safely remove entries while iterating. Reuse the scratch list to
            // avoid a per-update allocation (FPS roadmap #6).
            _dirtyScratch.Clear();
            _dirtyScratch.AddRange(_dirty);
            foreach (var coords in _dirtyScratch)
            {
                if (!_manager.TryGetLoadedChunk(coords, out var chunk))
                {
                    _dirty.Remove(coords); // chunk gone - nothing to mesh
                    continue;
                }

                if (!chunk.NeedsRemesh)
                {
                    _dirty.Remove(coords); // already meshed since the flag was set
                    continue;
                }

                if (chunk.IsMeshingQueued)
                {
                    continue; // keep in _dirty: it may be re-flagged while meshing, and NeedsRemesh
                              // stays true until the worker finishes, so the next Update retries it
                }

                chunk.IsMeshingQueued = true;
                _meshQueue.Enqueue(coords);
                _dirty.Remove(coords);
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
            _meshQueue.EnqueuePriority(coords);

            // A block edit also affects the faces of the four cardinal neighbours (and the four
            // diagonal neighbours at a chunk corner) that share the edited cell's border: the
            // mesher culls border faces against the neighbour's blocks, and the water pass samples
            // the 2x2 corner neighbourhood. MarkDirty already set NeedsRemesh on those neighbours,
            // but nothing was ENQUEUING them - so a face touching the border stayed stale until the
            // neighbour happened to be re-streamed. Enqueue any flagged border neighbours now so
            // broken faces disappear and placed faces appear immediately.
            int cx = coords.X;
            int cz = coords.Z;
            int layer = coords.Layer;
            EnqueueIfDirty(new ChunkCoordinates(layer, cx - 1, cz));
            EnqueueIfDirty(new ChunkCoordinates(layer, cx + 1, cz));
            EnqueueIfDirty(new ChunkCoordinates(layer, cx, cz - 1));
            EnqueueIfDirty(new ChunkCoordinates(layer, cx, cz + 1));
            EnqueueIfDirty(new ChunkCoordinates(layer, cx - 1, cz - 1));
            EnqueueIfDirty(new ChunkCoordinates(layer, cx + 1, cz - 1));
            EnqueueIfDirty(new ChunkCoordinates(layer, cx - 1, cz + 1));
            EnqueueIfDirty(new ChunkCoordinates(layer, cx + 1, cz + 1));
        }

        // Enqueues a neighbour for an immediate remesh if it is loaded AND was flagged dirty by the
        // edit (MarkDirty set NeedsRemesh on it). Priority so the border face updates with the edit.
        private void EnqueueIfDirty(ChunkCoordinates coords)
        {
            if (_manager.TryGetLoadedChunk(coords, out var chunk) && chunk.NeedsRemesh)
            {
                chunk.IsMeshingQueued = true;
                _meshQueue.EnqueuePriority(coords);
            }
        }

        public void Dispose() { }
    }
}
