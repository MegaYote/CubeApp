namespace Cubuild
{
    /// <summary>Master switch for the experimental far-field terrain LOD pipeline. Keep the
    /// implementation in place for later work, but use ordinary full-detail chunk meshes until
    /// the next LOD iteration is ready.</summary>
    public static class LodSettings
    {
        public static readonly bool SurfaceLodEnabled = false;
    }
}
