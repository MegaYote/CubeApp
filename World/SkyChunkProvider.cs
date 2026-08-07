using System;

namespace CubeApp.World
{
    /// <summary>
    /// Generates the SKY layer (world 384..1023): a completely empty air chunk at generation
    /// time, so creating a sky chunk is nearly free. Sky islands are NOT generated here - they
    /// fill lazily via <see cref="SkyIslandSculptor.HighFillChunk"/> when the player climbs into
    /// the stratosphere (mirroring the ground layer's lazy deep-fill).
    /// </summary>
    public sealed class SkyChunkProvider : IChunkProvider
    {
        public SkyIslandSculptor Islands { get; }

        public SkyChunkProvider(int seed = 0)
        {
            Islands = new SkyIslandSculptor(seed);
        }

        public Chunk GenerateChunk(int chunkX, int chunkZ, int chunkSize, int chunkHeight)
        {
            int originX = chunkX * chunkSize;
            int originZ = chunkZ * chunkSize;
            // Sky layer chunks sit above the ground layer (world 384..1023).
            int originY = ChunkManager.SkyOriginY;
            var chunk = new Chunk(chunkSize, chunkHeight, chunkSize, originX, originY, originZ);
            // When the player is already high, new sky chunks are born with their islands;
            // otherwise they're empty air and HighFillChunk fills them lazily on approach.
            if (Islands.AutoHighFill)
            {
                Islands.HighFillChunk(chunkX, chunkZ, chunk, chunkSize, chunkHeight);
            }
            return chunk;
        }
    }
}
