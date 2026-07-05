namespace CubeApp
{
    public readonly struct PickBlockResult
    {
        public (int x, int y, int z) Remove { get; }
        public (int x, int y, int z) Place { get; }
        public Point3D Normal { get; }

        public PickBlockResult((int x, int y, int z) remove, (int x, int y, int z) place, Point3D normal)
        {
            Remove = remove;
            Place = place;
            Normal = normal;
        }
    }
}
