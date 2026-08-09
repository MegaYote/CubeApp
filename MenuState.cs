namespace CubeApp
{
    using System.Collections.Generic;
    // Which top-level screen the game is showing. The title/create/pause screens are rendered by
    // the ImGui overlay; the world sim only runs while Playing.
    public enum GameScreen
    {
        Title,
        CreateWorld,
        Multiplayer,
        Loading,
        Playing,
        Paused,
        Dead,
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
        }
    }
}
