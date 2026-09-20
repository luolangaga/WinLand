namespace WinIsland.Core;

public sealed class PluginManifest
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string EntryDll { get; init; } = "";
    public int ApiVersion { get; init; } = IslandSdk.ApiVersion;
    public Version? MinHostVersion { get; init; }
    public string? Description { get; init; }
    public string? Author { get; init; }
    public string? IconGlyph { get; init; }
    public string? Homepage { get; init; }
    public string? License { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
}
