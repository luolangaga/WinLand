using WinIsland.Core;

namespace WinIsland.Core.Plugins;

public sealed class PluginLogService
{
    internal const int RingCapacity = 500;
    internal const long MaxFileBytes = 512 * 1024;

    private readonly Dictionary<string, PluginLogger> _loggers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public PluginLogService()
    {
        Directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinIsland",
            "logs");
        System.IO.Directory.CreateDirectory(Directory);
        Host = GetLogger("host");
    }

    public string Directory { get; }

    public IPluginLogger Host { get; }

    public IPluginLogger GetLogger(string id)
    {
        lock (_gate)
        {
            if (!_loggers.TryGetValue(id, out var logger))
            {
                logger = new PluginLogger(Path.Combine(Directory, $"plugin.{Sanitize(id)}.log"));
                _loggers[id] = logger;
            }
            return logger;
        }
    }

    public IReadOnlyList<string> GetLines(string id)
    {
        lock (_gate)
        {
            return _loggers.TryGetValue(id, out var logger) ? logger.Snapshot() : Array.Empty<string>();
        }
    }

    public string GetLogPath(string id)
    {
        lock (_gate)
        {
            return _loggers.TryGetValue(id, out var logger)
                ? logger.Path
                : Path.Combine(Directory, $"plugin.{Sanitize(id)}.log");
        }
    }

    private static string Sanitize(string id)
        => string.Concat(id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
}

internal sealed class PluginLogger : IPluginLogger
{
    private readonly Queue<string> _ring = new();
    private readonly object _gate = new();

    public PluginLogger(string path) => Path = path;

    public string Path { get; }

    public void Debug(string message) => Write("DEBUG", message, null);

    public void Info(string message) => Write("INFO", message, null);

    public void Warn(string message) => Write("WARN", message, null);

    public void Error(string message) => Write("ERROR", message, null);

    public void Error(string message, Exception exception) => Write("ERROR", message, exception);

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            return _ring.ToArray();
        }
    }

    private void Write(string level, string message, Exception? exception)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        lock (_gate)
        {
            _ring.Enqueue(line);
            if (exception != null)
            {
                foreach (var detail in exception.ToString().Split('\n'))
                {
                    _ring.Enqueue("    " + detail.TrimEnd('\r'));
                }
            }

            while (_ring.Count > PluginLogService.RingCapacity)
            {
                _ring.Dequeue();
            }

            AppendToFile(line, exception);
        }
    }

    private void AppendToFile(string line, Exception? exception)
    {
        try
        {
            var info = new FileInfo(Path);
            if (info.Exists && info.Length > PluginLogService.MaxFileBytes)
            {
                var backup = Path + ".1";
                if (File.Exists(backup)) File.Delete(backup);
                File.Move(Path, backup);
            }

            var text = exception == null
                ? line + Environment.NewLine
                : line + Environment.NewLine + exception + Environment.NewLine;
            File.AppendAllText(Path, text);
        }
        catch
        {
        }
    }
}
