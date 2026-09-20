namespace WinIsland.Core.Plugins;

public enum PluginState
{
    Discovered,
    Loaded,
    Active,
    Disabled,
    Faulted,
    Unloading,
}

public sealed class PluginError
{
    public required string Message { get; init; }
    public required string Phase { get; init; }
    public string? ExceptionType { get; init; }
    public DateTimeOffset Time { get; init; } = DateTimeOffset.Now;

    public string Describe()
        => ExceptionType == null ? Message : $"{Message}（{ExceptionType}）";
}

public sealed class PluginInfo
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? Author { get; set; }
    public string? Version { get; set; }
    public string? IconGlyph { get; set; }
    public required bool IsBuiltIn { get; init; }
    public string Source { get; init; } = "";
    public PluginState State { get; set; } = PluginState.Discovered;
    public PluginError? LastError { get; set; }
    public PluginManifest? Manifest { get; set; }

    public void ApplyManifest(PluginManifest manifest)
    {
        Manifest = manifest;
        Name = manifest.Name;
        Description = manifest.Description;
        Author = manifest.Author;
        Version = manifest.Version;
        IconGlyph = manifest.IconGlyph;
    }
}
