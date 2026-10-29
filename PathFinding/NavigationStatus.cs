namespace Cubuild
{
    /// <summary>Outcome of an explicit stand-position navigation request.</summary>
    public enum NavigationStatus
    {
        None,
        Navigating,
        Arrived,
        Partial,
        Unreachable,
        NeedsBreak,
    }
}
