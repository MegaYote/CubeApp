using System;

namespace Cubuild.World
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
            // Keep provider output a reproducible empty baseline. ChunkManager may apply the
            // altitude-triggered island fill afterward, but that direct mutation is full-save-only.
            return chunk;
        }
    }
}
