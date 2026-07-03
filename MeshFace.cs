namespace CubeApp
{
    public readonly struct MeshFace
    {
        public Point3D[] Vertices { get; }
        public TextureRect SrcRect { get; }
        public Point3D Normal { get; }
        public Point3D BlockPosition { get; }
        public float Shade { get; }
        public int TileWidth { get; }
        public int TileHeight { get; }

        public MeshFace(Point3D[] vertices, TextureRect srcRect, Point3D normal, Point3D blockPosition, float shade, int tileWidth, int tileHeight)
        {
            Vertices = vertices;
            SrcRect = srcRect;
            Normal = normal;
            BlockPosition = blockPosition;
            Shade = shade;
            TileWidth = tileWidth;
            TileHeight = tileHeight;
        }
    }
}
