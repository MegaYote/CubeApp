namespace CubeApp
{
    /// <summary>
    /// The play mode of a world. Creative is the sandbox: unlimited flight, invulnerability,
    /// every block available, debug keys enabled. Survival turns the screws: no flight, real
    /// damage and death, and blocks must be mined to be collected before they can be placed.
    /// </summary>
    public enum GameMode
    {
        Creative,
        Survival,
    }
}
