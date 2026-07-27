namespace WinIsland.Core;

public static class PluginConstants
{
    public static readonly string PluginFolderName = "plugins";
    public static readonly string ManifestFileName = "plugin.json";

    public static string GetPluginDirectory(string baseDir)
        => Path.Combine(baseDir, PluginFolderName);
}
