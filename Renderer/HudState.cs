using System.Numerics;

namespace CubeApp.Renderer
{
    /// <summary>
    /// Plain data describing what the HUD overlay should currently display.
    /// Populated by Program each tick and consumed by VeldridRenderer's ImGui pass.
    /// This replaces the old GDI+-based DrawHotbar/DrawCrosshair/DrawSelectedBlockLabel overlay.
    /// </summary>
    public struct HudState
    {
        public bool ShowDebug;
        public float Fps;
        public float UpdateMs;
        public float MeshMs;
        public float UploadMs;
        public float RenderMs;
        public string FacingText;
        public string SelectedBlockText;
        public string RenderDistanceText;
        public int SelectedSlot;

        /// <summary>
        /// Four world-space corners of the targeted block face, or null if nothing is currently
        /// targeted. The renderer draws these as a depth-tested 3D quad so the highlight is
        /// occluded per-pixel by any block in front of it (matching the rest of the scene),
        /// instead of always painting over everything as a 2D overlay would.
        /// </summary>
        public Vector3[]? HighlightWorldQuad;

        public static HudState Empty => new HudState
        {
            ShowDebug = false,
            Fps = 0f,
            UpdateMs = 0f,
            MeshMs = 0f,
            UploadMs = 0f,
            RenderMs = 0f,
            FacingText = string.Empty,
            SelectedBlockText = string.Empty,
            RenderDistanceText = string.Empty,
            SelectedSlot = 0,
            HighlightWorldQuad = null,
        };
    }
}
