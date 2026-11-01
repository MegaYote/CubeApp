namespace Cubuild.World
{
    public interface IChunkProvider
    {
        Chunk GenerateChunk(int chunkX, int chunkZ, int chunkSize, int chunkHeight);

        Chunk GenerateChunk(int chunkX, int chunkZ, int chunkSize, int chunkHeight, int sectionY)
            => GenerateChunk(chunkX, chunkZ, chunkSize, chunkHeight);
    }
}
