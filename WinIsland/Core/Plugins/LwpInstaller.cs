using System.IO.Compression;
using WinIsland.Core;

namespace WinIsland.Core.Plugins;

public sealed class LwpInstallException : Exception
{
    public LwpInstallException(string message) : base(message)
    {
    }
}

public sealed record InstallResult(bool Success, string? Error, PluginManifest? Manifest, bool IsUpdate, string? OldVersion, IReadOnlyList<string> Warnings)
{
    public static InstallResult Fail(string error) => new(false, error, null, false, null, Array.Empty<string>());

    public static InstallResult Ok(PluginManifest manifest, bool isUpdate, string? oldVersion, IReadOnlyList<string> warnings)
        => new(true, null, manifest, isUpdate, oldVersion, warnings);
}

public sealed class PreparedInstall
{
    public required string TempDirectory { get; init; }
    public required string TargetDirectory { get; init; }
    public required PluginManifest Manifest { get; init; }
    public bool IsUpdate { get; init; }
    public string? OldVersion { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public static class LwpInstaller
{
    private const long MaxUncompressedBytes = 256L * 1024 * 1024;

    public static PreparedInstall Prepare(string packagePath, string pluginsDirectory)
    {
        if (!File.Exists(packagePath))
        {
            throw new LwpInstallException($"插件包不存在：{packagePath}");
        }

        if (!string.Equals(Path.GetExtension(packagePath), IslandSdk.PackageExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new LwpInstallException($"只支持 {IslandSdk.PackageExtension} 插件包");
        }

        System.IO.Directory.CreateDirectory(pluginsDirectory);
        var tempDirectory = Path.Combine(pluginsDirectory, $".installing-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(tempDirectory);

        try
        {
            using var archive = ZipFile.OpenRead(packagePath);

            long totalBytes = 0;
            foreach (var entry in archive.Entries)
            {
                totalBytes += entry.Length;
            }

            if (totalBytes > MaxUncompressedBytes)
            {
                throw new LwpInstallException($"插件包解压后过大（{totalBytes / 1024 / 1024} MB，上限 {MaxUncompressedBytes / 1024 / 1024} MB）");
            }

            var manifestEntry = archive.GetEntry(IslandSdk.ManifestFileName);
            if (manifestEntry == null)
            {
                throw new LwpInstallException($"插件包根目录缺少 {IslandSdk.ManifestFileName}");
            }

            manifestEntry.ExtractToFile(Path.Combine(tempDirectory, IslandSdk.ManifestFileName), true);
            var manifest = PluginManifestReader.Parse(File.ReadAllText(Path.Combine(tempDirectory, IslandSdk.ManifestFileName)));

            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue;
                }

                ExtractEntry(entry, tempDirectory);
            }

            if (!File.Exists(Path.Combine(tempDirectory, manifest.EntryDll)))
            {
                throw new LwpInstallException($"包内缺少入口程序集：{manifest.EntryDll}");
            }

            if (File.Exists(Path.Combine(tempDirectory, IslandSdk.ContractAssemblyName + ".dll")))
            {
                throw new LwpInstallException($"插件包不允许携带 {IslandSdk.ContractAssemblyName}.dll（由宿主提供）");
            }

            var warnings = new List<string>();
            if (File.Exists(Path.Combine(tempDirectory, "Microsoft.WinUI.dll")) ||
                System.IO.Directory.GetFiles(tempDirectory, "Microsoft.WindowsAppRuntime*.dll", SearchOption.AllDirectories).Length > 0)
            {
                warnings.Add("包内含 Windows App SDK 运行时文件，宿主已自带，建议打包时排除以减小体积。");
            }

            var targetDirectory = Path.Combine(pluginsDirectory, manifest.Id);
            var oldVersion = ReadInstalledVersion(targetDirectory);

            return new PreparedInstall
            {
                TempDirectory = tempDirectory,
                TargetDirectory = targetDirectory,
                Manifest = manifest,
                IsUpdate = oldVersion != null,
                OldVersion = oldVersion,
                Warnings = warnings,
            };
        }
        catch (PluginManifestException ex)
        {
            TryDeleteDirectory(tempDirectory);
            throw new LwpInstallException($"{IslandSdk.ManifestFileName} 校验失败（{ex.Field}）：{ex.Message}");
        }
        catch (InvalidDataException)
        {
            TryDeleteDirectory(tempDirectory);
            throw new LwpInstallException("不是有效的 zip 包（.lwp 底层应为 zip 格式）");
        }
        catch
        {
            TryDeleteDirectory(tempDirectory);
            throw;
        }
    }

    public static void Commit(PreparedInstall prepared)
    {
        // 旧版本目录可能仍被上一代程序集占用（Windows 文件锁），此时直接删除会失败；
        // 改为先改名（改名通常不受文件锁影响），再把新目录移入，最后尽力清理旧目录。
        if (System.IO.Directory.Exists(prepared.TargetDirectory))
        {
            var retired = prepared.TargetDirectory + ".uninstalling-" + Guid.NewGuid().ToString("N")[..8];
            System.IO.Directory.Move(prepared.TargetDirectory, retired);
            TryDeleteDirectory(retired);
        }

        System.IO.Directory.Move(prepared.TempDirectory, prepared.TargetDirectory);
    }

    public static void Cleanup(PreparedInstall? prepared)
    {
        if (prepared == null)
        {
            return;
        }

        TryDeleteDirectory(prepared.TempDirectory);
    }

    public static string? ReadInstalledVersion(string pluginDirectory)
    {
        try
        {
            var manifestPath = Path.Combine(pluginDirectory, IslandSdk.ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                return null;
            }

            return PluginManifestReader.Parse(File.ReadAllText(manifestPath)).Version;
        }
        catch
        {
            return null;
        }
    }

    private static void ExtractEntry(ZipArchiveEntry entry, string destinationDirectory)
    {
        var normalized = entry.FullName.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.StartsWith('/') ||
            Path.IsPathRooted(normalized) ||
            normalized.Contains(':') ||
            normalized.Split('/').Any(segment => segment == ".."))
        {
            throw new LwpInstallException($"插件包内包含非法路径：{entry.FullName}");
        }

        var root = Path.GetFullPath(destinationDirectory);
        var target = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new LwpInstallException($"插件包内包含越界路径：{entry.FullName}");
        }

        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        entry.ExtractToFile(target, true);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (System.IO.Directory.Exists(path))
            {
                System.IO.Directory.Delete(path, true);
            }
        }
        catch
        {
        }
    }
}
