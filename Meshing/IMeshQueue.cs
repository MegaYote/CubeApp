using System;
using System.Collections.Generic;
using System.Threading;

namespace Cubuild
{
    /// <summary>
    /// Abstraction over "something that can mesh chunks in the background". The real mesh worker
    /// uploads faces to the GPU renderer; a no-op queue is used when running headless (dedicated
    /// server / host without a window), so the simulation can run without any renderer present.
    /// </summary>
    public interface IMeshQueue
    {
        void Enqueue(ChunkCoordinates coords);
        void EnqueuePriority(ChunkCoordinates coords);
    }

    /// <summary>No-op mesh queue for headless operation: chunk flags are still tracked (dirty /
    /// queued) but no faces are generated and nothing is uploaded.</summary>
    public sealed class NoOpMeshQueue : IMeshQueue
    {
        public void Enqueue(ChunkCoordinates coords) { }
        public void EnqueuePriority(ChunkCoordinates coords) { }
    }
}
