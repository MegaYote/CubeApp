namespace CubeApp
{
    /// <summary>
    /// Render data for one dropped item: a small block the renderer draws as a scaled 3D cube
    /// that tumbles. Position is the cube's base corner; rotation is a unit quaternion.
    /// </summary>
    public readonly struct ItemDropRenderData
    {
        public int BlockId { get; }
        /// <summary>Base corner of the small cube in world space (cube spans X..X+Scale).</summary>
        public float X { get; }
        public float Y { get; }
        public float Z { get; }
        /// <summary>Unit quaternion orientation (tumble).</summary>
        public float RotX { get; }
        public float RotY { get; }
        public float RotZ { get; }
        public float RotW { get; }

        public ItemDropRenderData(int blockId, float x, float y, float z,
            float rotX, float rotY, float rotZ, float rotW)
        {
            BlockId = blockId;
            X = x;
            Y = y;
            Z = z;
            RotX = rotX;
            RotY = rotY;
            RotZ = rotZ;
            RotW = rotW;
        }
    }
}
