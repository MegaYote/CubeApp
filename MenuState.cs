namespace CubeApp
{
    using System.Collections.Generic;
    // Which top-level screen the game is showing. The title/create/pause screens are rendered by
    // the ImGui overlay; the world sim only runs while Playing.
    public enum GameScreen
    {
        Title,
        CreateWorld,
        Playing,
        Paused,
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
        public int SelectedWorldIndex = -1;
        /// <summary>Display names of saved worlds (from the saves folder), for the title list.</summary>
        public List<string> SavedWorlds = new();

        public void ResetFlags()
        {
            CreateWorldClicked = false;
            ResumeClicked = false;
            QuitToTitleClicked = false;
            QuitClicked = false;
            LoadWorldClicked = false;
        }
    }
}
