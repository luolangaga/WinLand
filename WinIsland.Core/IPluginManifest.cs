namespace WinIsland.Core;

public interface IPluginManifest
{
    string Id { get; }
    string DisplayName { get; }
    string? Description { get; }
    string? Author { get; }
    string? Version { get; }
    string? IconGlyph { get; }
}

public sealed class PluginManifest : IPluginManifest
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public string? Author { get; init; }
    public string? Version { get; init; }
    public string? IconGlyph { get; init; }
}
