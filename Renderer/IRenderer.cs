namespace CubeApp.Renderer
{
    public interface IRenderer
    {
        void Initialize(Veldrid.GraphicsDevice graphicsDevice, Veldrid.Swapchain swapchain);
        void Resize(int width, int height);
        void Render();
        void Dispose();
        void UploadChunk(CubeApp.ChunkCoordinates coords, System.Collections.Generic.IReadOnlyList<CubeApp.MeshFace> faces);
        void RemoveChunk(CubeApp.ChunkCoordinates coords);
        void UpdateCamera(CubeApp.Point3D position, float yaw, float pitch);
        void SetRenderDistance(int chunkRadius);
        void SetHud(HudState hud);
    }
}
