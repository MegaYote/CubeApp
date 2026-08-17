namespace Cubuild.Renderer
{
    public interface IRenderer
    {
        void Initialize(Veldrid.GraphicsDevice graphicsDevice, Veldrid.Swapchain swapchain);
        void Resize(int width, int height);
        void Render();
        void Dispose();
        void UploadChunk(Cubuild.ChunkCoordinates coords, System.Collections.Generic.IReadOnlyList<Cubuild.MeshFace> faces);
        void UploadChunkPriority(Cubuild.ChunkCoordinates coords, System.Collections.Generic.IReadOnlyList<Cubuild.MeshFace> faces);
        void RemoveChunk(Cubuild.ChunkCoordinates coords);
        void UpdateCamera(Cubuild.Point3D position, float yaw, float pitch, float walkPhase = 0f, float walkAmount = 0f, bool firstPerson = false, bool grounded = true);
        Cubuild.Point3D? CameraPosition { get; }
        System.Numerics.Matrix4x4? ViewProjection { get; }
        void SetRenderDistance(int chunkRadius);
        void SetResolutionScale(float scale);
        void SetPixelatedUpscale(bool pixelated);
        void SetHud(HudState hud);
        void SetEntities(System.Collections.Generic.IReadOnlyList<Cubuild.MobRenderData> mobRenderData);
        void SetFallingBlocks(System.Collections.Generic.IReadOnlyList<Cubuild.FallingBlockData> fallingBlocks);
        void SetItemDrops(System.Collections.Generic.IReadOnlyList<Cubuild.ItemDropRenderData> itemDrops);
        void SetChunkManager(Cubuild.ChunkManager manager);
        void SetWorldSeed(int seed);
        void SetCullingMode(Cubuild.CullingMode mode);
        Cubuild.CullingMode GetCullingMode();
        void ToggleGpuCulling();
        void MeshChunkImmediate(Cubuild.ChunkCoordinates coords);
        void ProcessPendingPriorityMeshes();
        int CountPendingUploads();
        void SetUiInputSnapshot(Veldrid.InputSnapshot snapshot);
        bool TryTakeInventorySelection(out int blockId);
        bool TryTakeInventoryClick(out (int Kind, int Target, int Button) click);
        int HoveredInventorySlot { get; }
        bool TryTakeBiomeSelection(out string biomeName);
        void SpawnBlockBreakParticles(int worldX, int worldY, int worldZ, int blockId, int count);
        void ResetWorld();
    }
}
