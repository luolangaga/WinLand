namespace WinIsland.Core;

public enum PluginState
{
    Discovered,
    Loaded,
    Initialized,
    Disabled,
    Error,
}

public sealed class PluginInfo
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public string? Author { get; init; }
    public string? Version { get; init; }
    public string? IconGlyph { get; init; }
    public required string DllPath { get; init; }
    public required bool IsBuiltIn { get; init; }
    public required PluginState State { get; set; }
    public string? ErrorMessage { get; set; }
    public IIslandModule? Module { get; set; }
}
