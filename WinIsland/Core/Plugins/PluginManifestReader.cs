using System.Text.Json;
using System.Text.RegularExpressions;
using WinIsland.Core;

namespace WinIsland.Core.Plugins;

public sealed class PluginManifestException : Exception
{
    public PluginManifestException(string field, string message) : base(message) => Field = field;

    public string Field { get; }
}

public static class PluginManifestReader
{
    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9-]{1,63}$", RegexOptions.Compiled);

    public static PluginManifest Read(string pluginDirectory)
    {
        var path = Path.Combine(pluginDirectory, IslandSdk.ManifestFileName);
        if (!File.Exists(path))
        {
            throw new PluginManifestException(IslandSdk.ManifestFileName, $"缺少 {IslandSdk.ManifestFileName} 清单文件");
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            throw new PluginManifestException(IslandSdk.ManifestFileName, $"读取清单失败：{ex.Message}");
        }

        return Parse(json, pluginDirectory);
    }

    public static PluginManifest Parse(string json, string? pluginDirectory = null)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException ex)
        {
            throw new PluginManifestException(IslandSdk.ManifestFileName, $"不是有效的 JSON：{ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new PluginManifestException(IslandSdk.ManifestFileName, "清单根节点必须是 JSON 对象");
            }

            var id = RequiredString(root, "id");
            if (!IdPattern.IsMatch(id))
            {
                throw new PluginManifestException("id", $"不合法（要求小写字母/数字/连字符，2-64 字符）：\"{id}\"");
            }

            var name = RequiredString(root, "name");
            var version = RequiredString(root, "version");
            if (!Version.TryParse(version, out _))
            {
                throw new PluginManifestException("version", $"不是有效的版本号：\"{version}\"");
            }

            var entryDll = RequiredString(root, "entry_dll");
            if (entryDll.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || entryDll.Contains("..", StringComparison.Ordinal))
            {
                throw new PluginManifestException("entry_dll", $"必须是包内的文件名，不能包含路径：\"{entryDll}\"");
            }

            if (!entryDll.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                throw new PluginManifestException("entry_dll", $"必须是 .dll 文件：\"{entryDll}\"");
            }

            if (string.Equals(entryDll, IslandSdk.ContractAssemblyName + ".dll", StringComparison.OrdinalIgnoreCase))
            {
                throw new PluginManifestException("entry_dll", $"入口程序集不能是 {IslandSdk.ContractAssemblyName}.dll");
            }

            var apiVersion = RequiredInt(root, "api_version");
            if (apiVersion < 1)
            {
                throw new PluginManifestException("api_version", "必须大于 0");
            }

            if (apiVersion > IslandSdk.ApiVersion)
            {
                throw new PluginManifestException("api_version",
                    $"插件要求 API 版本 {apiVersion}，当前宿主只支持到 {IslandSdk.ApiVersion}，请更新 WinIsland");
            }

            Version? minHost = null;
            if (root.TryGetProperty("min_host_version", out var minHostElement))
            {
                var text = minHostElement.ValueKind == JsonValueKind.String ? minHostElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(text) || !Version.TryParse(text, out var parsed))
                {
                    throw new PluginManifestException("min_host_version", $"不是有效的版本号：\"{text}\"");
                }

                minHost = parsed;
                if (minHost > IslandSdk.HostVersion)
                {
                    throw new PluginManifestException("min_host_version",
                        $"插件要求宿主版本 >= {minHost}，当前宿主为 {IslandSdk.HostVersion}");
                }
            }

            var manifest = new PluginManifest
            {
                Id = id,
                Name = name,
                Version = version,
                EntryDll = entryDll,
                ApiVersion = apiVersion,
                MinHostVersion = minHost,
                Description = OptionalString(root, "description"),
                Author = OptionalString(root, "author"),
                IconGlyph = OptionalString(root, "icon_glyph"),
                Homepage = OptionalString(root, "homepage"),
                License = OptionalString(root, "license"),
                Tags = OptionalStringArray(root, "tags"),
            };

            if (pluginDirectory != null && !File.Exists(Path.Combine(pluginDirectory, manifest.EntryDll)))
            {
                throw new PluginManifestException("entry_dll", $"入口程序集不存在：{manifest.EntryDll}");
            }

            return manifest;
        }
    }

    private static string RequiredString(JsonElement root, string field)
    {
        if (!root.TryGetProperty(field, out var element))
        {
            throw new PluginManifestException(field, $"缺少必填字段 \"{field}\"");
        }

        if (element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()))
        {
            throw new PluginManifestException(field, $"\"{field}\" 必须是非空字符串");
        }

        return element.GetString()!.Trim();
    }

    private static int RequiredInt(JsonElement root, string field)
    {
        if (!root.TryGetProperty(field, out var element))
        {
            throw new PluginManifestException(field, $"缺少必填字段 \"{field}\"");
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
        {
            throw new PluginManifestException(field, $"\"{field}\" 必须是整数");
        }

        return value;
    }

    private static string? OptionalString(JsonElement root, string field)
        => root.TryGetProperty(field, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static IReadOnlyList<string> OptionalStringArray(JsonElement root, string field)
    {
        if (!root.TryGetProperty(field, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var list = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } value)
            {
                list.Add(value);
            }
        }

        return list;
    }
}
