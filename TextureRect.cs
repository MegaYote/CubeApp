namespace CubeApp
{
    /// <summary>
    /// Minimal integer rectangle used for atlas tile coordinates.
    /// Replaces System.Drawing.Rectangle so the project has no GDI+ dependency.
    /// </summary>
    public readonly struct TextureRect
    {
        public int X { get; }
        public int Y { get; }
        public int Width { get; }
        public int Height { get; }

        public TextureRect(int x, int y, int width, int height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }
    }
}
