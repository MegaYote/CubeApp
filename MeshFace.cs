namespace CubeApp
{
    /// <summary>
    /// A single greedy-merged quad. Vertices are stored as four inline value-type corners
    /// instead of a heap-allocated Point3D[] so meshing emits zero per-face allocations.
    /// </summary>
    public readonly struct MeshFace
    {
        public Point3D V0 { get; }
        public Point3D V1 { get; }
        public Point3D V2 { get; }
        public Point3D V3 { get; }
        public TextureRect SrcRect { get; }
        public Point3D Normal { get; }
        public Point3D BlockPosition { get; }
        public float Shade { get; }
        public int TileWidth { get; }
        public int TileHeight { get; }
        public float Alpha { get; }
        /// <summary>
        /// When true (fluid side walls), the tile's bottom edge is anchored to the lowest face
        /// vertex and the fluid surface cuts across the tile, matching Infdev's
        /// RenderBlocks.renderBlockFluids. Without it, a partial-height wall shows the tile's
        /// TOP strip instead of its bottom portion.
        /// </summary>
        public bool AnchorVBottom { get; }

        public MeshFace(Point3D v0, Point3D v1, Point3D v2, Point3D v3, TextureRect srcRect, Point3D normal, Point3D blockPosition, float shade, int tileWidth, int tileHeight, float alpha = 1f, bool anchorVBottom = false)
        {
            V0 = v0;
            V1 = v1;
            V2 = v2;
            V3 = v3;
            SrcRect = srcRect;
            Normal = normal;
            BlockPosition = blockPosition;
            Shade = shade;
            TileWidth = tileWidth;
            TileHeight = tileHeight;
            Alpha = alpha;
            AnchorVBottom = anchorVBottom;
        }
    }
}
