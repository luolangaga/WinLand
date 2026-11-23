namespace WinIsland.Core;

/// <summary>Optional host capability (2.5). Cast IPluginContext to this interface;
/// older contexts remain binary compatible. No credentials or host settings are exposed.</summary>
public interface IPluginHostServices
{
    /// <summary>Writable per-plugin data under LocalAppData; survives plugin updates.</summary>
    string DataDirectory { get; }
    /// <summary>Cancelled when registrations are revoked, including failed initialization.</summary>
    CancellationToken LifetimeToken { get; }
    /// <summary>Reveal an existing process's main window. Returns false when no destination exists
    /// or Windows declines foreground activation. Never starts a process or types into it.</summary>
    bool TryActivateProcess(int processId);
}
