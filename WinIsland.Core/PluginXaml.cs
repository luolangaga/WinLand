using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace WinIsland.Core;

public static class PluginXaml
{
    public static void Load(object component, string? resourcePath = null)
    {
        var type = component.GetType();
        var candidates = BuildCandidates(type, resourcePath);
        Exception? lastError = null;

        foreach (var candidate in candidates)
        {
            try
            {
                Application.LoadComponent(component, new Uri(candidate), ComponentResourceLocation.Application);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw new InvalidOperationException(
            $"无法加载插件 XAML（{type.FullName}）。请确认：XAML 文件与类同名、所在文件夹与命名空间一致、" +
            $".xbf 随插件程序集一起发布、且 XAML 内只使用框架类型。已尝试：{string.Join("、", candidates)}",
            lastError);
    }

    private static List<string> BuildCandidates(Type type, string? resourcePath)
    {
        var candidates = new List<string>();

        var classPath = resourcePath ?? type.FullName?.Replace('.', '/');
        if (string.IsNullOrEmpty(classPath))
        {
            return candidates;
        }

        var assemblyDirectory = Path.GetDirectoryName(type.Assembly.Location);
        if (!string.IsNullOrEmpty(assemblyDirectory))
        {
            var normalizedDirectory = assemblyDirectory.Replace('\\', '/').TrimEnd('/');
            var appRelative = GetAppRelativeDirectory(normalizedDirectory);

            if (!string.IsNullOrEmpty(appRelative))
            {
                candidates.Add(resourcePath == null
                    ? $"ms-appx:///{appRelative}/{classPath}.xaml"
                    : $"ms-appx:///{appRelative}/{resourcePath}");
            }

            candidates.Add(resourcePath == null
                ? $"ms-appx:///{normalizedDirectory.TrimStart('/')}/{classPath}.xaml"
                : $"ms-appx:///{normalizedDirectory.TrimStart('/')}/{resourcePath}");
        }

        return candidates;
    }

    private static string? GetAppRelativeDirectory(string normalizedDirectory)
    {
        var baseDirectory = AppContext.BaseDirectory.Replace('\\', '/').TrimEnd('/');
        if (!normalizedDirectory.StartsWith(baseDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return normalizedDirectory.Substring(baseDirectory.Length).TrimStart('/');
    }
}
