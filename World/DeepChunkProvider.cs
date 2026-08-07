using System;

namespace CubeApp.World
{
    /// <summary>
    /// Generates the DEEP layer (world -256..-65): solid stone with a few large caves, plus a
    /// bedrock floor at the very bottom (local 0..3, world -256..-253). This is the "super deep
    /// terrain below the old bedrock layer". Chunks are born filled immediately (the deep zone
    /// is meant to be solid, unlike the lazy sky layer) so generation stays cheap and there is
    /// no separate fill pass racing the mesh worker.
    ///
    /// Cave tunnels are deterministic per chunk (seeded) and stop at chunk borders exactly like
    /// the ground layer's caves, so deep cave systems read as natural caverns.
    /// </summary>
    public sealed class DeepChunkProvider : IChunkProvider
    {
        private readonly int seed;

        public DeepChunkProvider(int seed = 0)
        {
            this.seed = seed;
        }

        public Chunk GenerateChunk(int chunkX, int chunkZ, int chunkSize, int chunkHeight)
        {
            int originX = chunkX * chunkSize;
            int originZ = chunkZ * chunkSize;
            int originY = ChunkManager.DeepOriginY;
            var chunk = new Chunk(chunkSize, chunkHeight, chunkSize, originX, originY, originZ);

            int idBedrock = BlockRegistry.GetId("bedrock");
            int idStone = BlockRegistry.GetId("stone");

            // Solid stone throughout, then carve caves.
            byte[] blocks = chunk.RawBlocks;
            const int height = 192;
            for (int x = 0; x < chunkSize; x++)
            for (int z = 0; z < chunkSize; z++)
            {
                int baseIdx = (x * chunkSize + z) * height;
                for (int y = 0; y < height; y++)
                {
                    blocks[baseIdx + y] = (byte)(y < 4 ? idBedrock : idStone);
                }
            }

            // Carve a few large caves so the deep isn't boring solid rock.
            var rand = new Random(unchecked(chunkX * 341873128 + chunkZ * 132897987 ^ seed));
            int caveCount = 2 + rand.Next(4);
            for (int i = 0; i < caveCount; i++)
            {
                double x = chunkX * 16 + rand.Next(16);
                double y = 8 + rand.Next(160);
                double z = chunkZ * 16 + rand.Next(16);
                float yaw = (float)(rand.NextDouble() * Math.PI * 2.0);
                float pitch = (float)((rand.NextDouble() - 0.5) * 2.0 / 8.0);
                float radius = (float)(rand.NextDouble() * 1.5 + 2.0);
                int len = rand.Next(8) + 8;
                for (int step = 0; step < len; step++)
                {
                    x += Math.Sin(yaw) * Math.Cos(pitch);
                    z += Math.Cos(yaw) * Math.Cos(pitch);
                    y += Math.Sin(pitch);
                    int cx = (int)Math.Floor(x);
                    int cy = (int)Math.Floor(y);
                    int cz = (int)Math.Floor(z);
                    if (cx < chunkX * 16 || cx >= chunkX * 16 + 16) break;
                    if (cz < chunkZ * 16 || cz >= chunkZ * 16 + 16) break;
                    if (cy < 4 || cy >= height) continue;
                    int lx = cx - chunkX * 16;
                    int lz = cz - chunkZ * 16;
                    blocks[(lx * chunkSize + lz) * height + cy] = 0;
                }
            }

            return chunk;
        }
    }
}
