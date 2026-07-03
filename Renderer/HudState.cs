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
        public int SelectedSlot;

        /// <summary>
        /// Four screen-space corners (top-left, top-right, bottom-right, bottom-left) of the
        /// targeted block face highlight, or null if nothing is currently targeted.
        /// </summary>
        public Vector2[]? HighlightQuad;

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
            SelectedSlot = 0,
            HighlightQuad = null,
        };
    }
}
