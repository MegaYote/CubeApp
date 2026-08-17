namespace Cubuild
{
    using System.Collections.Generic;
    // Which top-level screen the game is showing. The title/create/pause screens are rendered by
    // the ImGui overlay; the world sim only runs while Playing.
    public enum GameScreen
    {
        Title,
        WorldSelect,
        CreateWorld,
        Multiplayer,
        Loading,
        Playing,
        Paused,
        Dead,
        Settings,
    }

    /// <summary>Which frustum-culling path the renderer uses. Auto resolves by GPU vendor
    /// (CPU on Intel integrated, GPU on NVIDIA/AMD); the explicit modes force one side.</summary>
    public enum CullingMode
    {
        Auto = 0,
        Cpu = 1,
        Gpu = 2,
    }

    /// <summary>
    /// Shared UI state between Program (owns the flow) and the renderer (draws the ImGui menus).
    /// Button presses are set by the renderer's ImGui pass and consumed by Program's next tick.
    /// </summary>
    public sealed class MenuState
    {
        public GameScreen Screen = GameScreen.Title;
        public string WorldName = "World 1";
        public string SeedInput = "";
        /// <summary>The mode chosen on the Create World screen (Creative by default).</summary>
        public GameMode SelectedMode = GameMode.Creative;

        public bool CreateWorldClicked;
        public bool ResumeClicked;
        public bool QuitToTitleClicked;
        public bool QuitClicked;
        public bool LoadWorldClicked;
        public bool OpenToLanClicked;
        public bool RespawnClicked;
        public int SelectedWorldIndex = -1;
        /// <summary>Display names of saved worlds (from the saves folder), for the title list.</summary>
        public List<string> SavedWorlds = new();

        // ---- world select screen (rename / delete) ----
        public bool DeleteWorldClicked;
        public int DeleteWorldIndex = -1;
        public bool RenameWorldClicked;
        public int RenameWorldIndex = -1;
        public string RenameTarget = "";

        // ---- multiplayer screen ----
        public bool HostGameClicked;
        public bool JoinGameClicked;
        public bool MultiplayerBackClicked;
        public string JoinAddress = "127.0.0.1:26065";
        public string HostPort = "26065";

        // ---- loading screen ----
        /// <summary>Current generation phase name shown on the loading screen ("Generating terrain",
        /// "Meshing chunks", etc).</summary>
        public string LoadingPhase = "";
        /// <summary>Fraction (0..1) of the current phase.</summary>
        public float LoadingPhaseProgress;
        /// <summary>Fraction (0..1) of the entire load.</summary>
        public float LoadingTotalProgress;

        // ---- settings screen ----
        /// <summary>True while the settings screen is open (rendered by the ImGui pass).</summary>
        public bool SettingsOpen;
        /// <summary>Clicked "Back" on the settings screen - return to where we came from.</summary>
        public bool SettingsBackClicked;
        /// <summary>The screen to return to after closing settings (Title or Paused).</summary>
        public GameScreen SettingsReturnTo = GameScreen.Title;
        /// <summary>Culling mode the player chose (Auto/Cpu/Gpu). Applied via the renderer.</summary>
        public CullingMode SelectedCullingMode = CullingMode.Auto;
        /// <summary>Set when the culling mode radio changed; Program applies it to the renderer.</summary>
        public bool CullingModeChanged;
        /// <summary>Render distance index the player chose (0=Far,1=Normal,2=Short,3=Tiny).</summary>
        public int SelectedRenderDistance = 0;
        /// <summary>Set when the render distance radio changed; Program applies it.</summary>
        public bool RenderDistanceChanged;
        /// <summary>Mouse sensitivity (0.05..2.0); applied to the input pipeline.</summary>
        public float SelectedMouseSensitivity = 0.5f;
        /// <summary>Set when the sensitivity slider changed.</summary>
        public bool MouseSensitivityChanged;
        /// <summary>Internal render resolution as a fraction of the window (1.0, 0.75, 0.5, 0.25).
        /// Lower = fewer pixels shaded = faster on weak GPUs (iGPUs share system RAM).</summary>
        public float SelectedResolutionScale = 1f;
        /// <summary>Set when the resolution scale radio changed; Program applies it.</summary>
        public bool ResolutionScaleChanged;
        /// <summary>When true, low-res upscale uses NEAREST filtering (chunky blocky pixels).
        /// When false, linear filtering smooths the upscale.</summary>
        public bool SelectedPixelatedUpscale;
        /// <summary>Set when the low-res filter radio changed; Program applies it.</summary>
        public bool PixelFilterChanged;

        public void ResetFlags()
        {
            CreateWorldClicked = false;
            ResumeClicked = false;
            QuitToTitleClicked = false;
            QuitClicked = false;
            LoadWorldClicked = false;
            OpenToLanClicked = false;
            RespawnClicked = false;
            HostGameClicked = false;
            JoinGameClicked = false;
            MultiplayerBackClicked = false;
            DeleteWorldClicked = false;
            RenameWorldClicked = false;
            SettingsBackClicked = false;
            CullingModeChanged = false;
            RenderDistanceChanged = false;
            MouseSensitivityChanged = false;
            ResolutionScaleChanged = false;
            PixelFilterChanged = false;
        }
    }
}
