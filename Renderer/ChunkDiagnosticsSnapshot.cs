using System.Collections.Generic;

namespace Cubuild.Renderer
{
    /// <summary>
    /// Bounded, read-only F3 diagnostics captured from the world and renderer. This deliberately
    /// exposes diagnostic values rather than the renderer's range dictionaries or GPU resources.
    /// </summary>
    public sealed class ChunkDiagnosticsSnapshot
    {
        public int LoadedChunks { get; }
        public int RenderRadius { get; }
        public int DirtyMeshQueueCount { get; }
        public int PendingUploads { get; }
        public int LastFrameStreamUploads { get; }
        public int LastFramePriorityUploads { get; }
        public int UploadedChunkRanges { get; }
        public int DrawCommandCount { get; }
        public uint VertexBufferCapacityBytes { get; }
        public uint IndexBufferCapacityBytes { get; }
        public uint VertexBufferTailBytes { get; }
        public uint IndexBufferTailBytes { get; }
        public int PendingBufferCopies { get; }
        public string CullMode { get; }
        public bool GpuCullActive { get; }
        public bool GpuCullSupported { get; }
        public string HzbStatus { get; }
        public IReadOnlyList<string> NearbyMapRows { get; }
        public IReadOnlyList<string> TargetLines { get; }

        internal ChunkDiagnosticsSnapshot(
            int loadedChunks,
            int renderRadius,
            int dirtyMeshQueueCount,
            int pendingUploads,
            int lastFrameStreamUploads,
            int lastFramePriorityUploads,
            int uploadedChunkRanges,
            int drawCommandCount,
            uint vertexBufferCapacityBytes,
            uint indexBufferCapacityBytes,
            uint vertexBufferTailBytes,
            uint indexBufferTailBytes,
            int pendingBufferCopies,
            string cullMode,
            bool gpuCullActive,
            bool gpuCullSupported,
            string hzbStatus,
            IReadOnlyList<string> nearbyMapRows,
            IReadOnlyList<string> targetLines)
        {
            LoadedChunks = loadedChunks;
            RenderRadius = renderRadius;
            DirtyMeshQueueCount = dirtyMeshQueueCount;
            PendingUploads = pendingUploads;
            LastFrameStreamUploads = lastFrameStreamUploads;
            LastFramePriorityUploads = lastFramePriorityUploads;
            UploadedChunkRanges = uploadedChunkRanges;
            DrawCommandCount = drawCommandCount;
            VertexBufferCapacityBytes = vertexBufferCapacityBytes;
            IndexBufferCapacityBytes = indexBufferCapacityBytes;
            VertexBufferTailBytes = vertexBufferTailBytes;
            IndexBufferTailBytes = indexBufferTailBytes;
            PendingBufferCopies = pendingBufferCopies;
            CullMode = cullMode;
            GpuCullActive = gpuCullActive;
            GpuCullSupported = gpuCullSupported;
            HzbStatus = hzbStatus;
            NearbyMapRows = Array.AsReadOnly(new List<string>(nearbyMapRows).ToArray());
            TargetLines = Array.AsReadOnly(new List<string>(targetLines).ToArray());
        }
    }
}
