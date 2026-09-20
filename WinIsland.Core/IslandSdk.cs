namespace WinIsland.Core;

public static class IslandSdk
{
    public const int ApiVersion = 2;

    public const string ContractAssemblyName = "WinIsland.Core";
    public const string ManifestFileName = "plugin.json";
    public const string PluginFolderName = "plugins";
    public const string PackageExtension = ".lwp";

    public static Version HostVersion { get; } =
        typeof(IslandSdk).Assembly.GetName().Version ?? new Version(2, 0, 0);

    public static string GetPluginsDirectory(string baseDirectory)
        => Path.Combine(baseDirectory, PluginFolderName);
}
