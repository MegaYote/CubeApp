namespace Cubuild.World
{
    public interface IChunkProvider
    {
        Chunk GenerateChunk(int chunkX, int chunkZ, int chunkSize, int chunkHeight);
    }
}