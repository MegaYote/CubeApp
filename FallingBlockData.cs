namespace CubeApp
{
    /// <summary>
    /// Render data for one falling block (the Minecraft "falling sand/gravel" entity). The block
    /// was removed from the world grid and is falling through the air; the renderer draws it as a
    /// 3D cube of the block's tiles using the world pipeline (so it depth-tests against terrain).
    /// </summary>
    public readonly struct FallingBlockData
    {
        public int BlockId { get; }
        /// <summary>Bottom of the cube in world Y (the cube occupies Y..Y+1).</summary>
        public float X { get; }
        public float Y { get; }
        public float Z { get; }

        public FallingBlockData(int blockId, float x, float y, float z)
        {
            BlockId = blockId;
            X = x;
            Y = y;
            Z = z;
        }
    }
}
