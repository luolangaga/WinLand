namespace WinIsland.Core;

public interface IPluginContext
{
    IPluginManifest Manifest { get; }
    string PluginDirectory { get; }
    IPluginLogger Logger { get; }
}

public interface IPluginLogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
    void Error(string message, Exception exception);
}
