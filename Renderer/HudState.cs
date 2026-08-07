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
        public bool InventoryOpen;
        public bool FlyMode;
        /// <summary>Shared menu state (screen + create-world form + button flags).</summary>
        public MenuState Menu;
        public float Fps;
        public float UpdateMs;
        public float MeshMs;
        public float UploadMs;
        public float RenderMs;
        public string FacingText;
        public string SelectedBlockText;
        public string RenderDistanceText;
        public int SelectedSlot;
        public int WorldSeed;
        public string BiomeText;
        /// <summary>Current per-slot hotbar contents (block ids); may differ from the default list
        /// once the player drops inventory picks into slots.</summary>
        public IReadOnlyList<int> Hotbar;

        /// <summary>
        /// Four world-space corners of the targeted block face, or null if nothing is currently
        /// targeted. The renderer draws these as a depth-tested 3D quad so the highlight is
        /// occluded per-pixel by any block in front of it (matching the rest of the scene),
        /// instead of always painting over everything as a 2D overlay would.
        /// </summary>
        public Vector3[]? HighlightWorldQuad;

        /// <summary>
        /// Player's current world position for the debug overlay.
        /// </summary>
        public double PlayerX;
        public double PlayerY;
        public double PlayerZ;

        /// <summary>
        /// Player's current chunk coordinates for rendering chunk borders when debug is enabled.
        /// </summary>
        public int PlayerChunkX;
        public int PlayerChunkZ;
        public int RenderDistance;

        /// <summary>Networking status line shown in the debug overlay (e.g. "Hosting :26065" or
        /// "Joined host"). Empty = no session.</summary>
        public string NetStatus;

        /// <summary>Multiplayer error to display on the multiplayer menu (e.g. failed join).
        /// Empty = no error.</summary>
        public string MultiplayerError;

        public static HudState Empty => new HudState
        {
            ShowDebug = false,
            InventoryOpen = false,
            FlyMode = false,
            Menu = new MenuState(),
            Fps = 0f,
            UpdateMs = 0f,
            MeshMs = 0f,
            UploadMs = 0f,
            RenderMs = 0f,
            FacingText = string.Empty,
            SelectedBlockText = string.Empty,
            RenderDistanceText = string.Empty,
            SelectedSlot = 0,
            WorldSeed = 0,
            BiomeText = string.Empty,
            Hotbar = Array.Empty<int>(),
            HighlightWorldQuad = null,
            PlayerChunkX = 0,
            PlayerChunkZ = 0,
            RenderDistance = 0,
            NetStatus = string.Empty,
            MultiplayerError = string.Empty,
        };
    }
}
