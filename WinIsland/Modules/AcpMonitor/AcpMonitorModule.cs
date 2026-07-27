using System.Text.Json;
using WinIsland.Core;

namespace WinIsland.Modules.AcpMonitor;

public sealed class AcpMonitorModule : IIslandModule
{
    public string Id => "acpmonitor";
    public string DisplayName => "Agent 监控";

    private IDynamicIslandApi _api = null!;
    private AgentEventReceiver _receiver = null!;
    private AcpMonitorViewModel _vm = null!;
    private AcpMonitorIslandView _islandView = null!;
    private IslandLiveContent _content = null!;
    private bool _enabled;
    private bool _liveShown;

    public string StatusText { get; private set; } = "等待插件上报";
    public int CallbackPort => _api.Callbacks.Port;
    public string CallbackBaseUrl => _api.Callbacks.BaseUrl;

    public static readonly (string Name, string Id, string Exe, string EnvVar, string PluginDirName)[] KnownAgents =
    {
        ("OpenCode", "opencode", "opencode", "OPENCODE_PATH", "opencode-winisland"),
        ("Claude Code", "claude", "claude", "CLAUDE_PATH", "claude-winisland"),
        ("Codex CLI", "codex", "codex", "CODEX_PATH", "codex-winisland"),
    };

    private static readonly string[] ExecutableExtensions = { ".exe", ".cmd", ".bat", ".ps1" };

    public static string? ResolveAgentPath(string agentKey)
    {
        var known = KnownAgents.FirstOrDefault(a => a.Id.Equals(agentKey, StringComparison.OrdinalIgnoreCase));
        if (known == default) return null;

        var envPath = Environment.GetEnvironmentVariable(known.EnvVar);
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
            return envPath;

        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in pathDirs)
        {
            try
            {
                foreach (var ext in ExecutableExtensions)
                {
                    var full = Path.Combine(dir, known.Exe + ext);
                    if (File.Exists(full)) return full;
                }
            }
            catch { }
        }

        return null;
    }

    public static Dictionary<string, string> DiscoverAgents()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var agent in KnownAgents)
        {
            var resolved = ResolveAgentPath(agent.Id);
            if (resolved != null)
                result[agent.Name] = resolved;
        }
        return result;
    }

    public static bool IsPluginInstalled(string agentId)
    {
        var known = KnownAgents.FirstOrDefault(a => a.Id.Equals(agentId, StringComparison.OrdinalIgnoreCase));
        if (known == default) return false;

        return agentId switch
        {
            "opencode" => Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "opencode", "plugins", known.PluginDirName))
                       || File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "opencode", "plugins", known.PluginDirName + ".js"))
                       || File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "opencode", "plugins", known.PluginDirName + ".ts")),
            "claude" => IsClaudePluginInstalled(known.PluginDirName),
            "codex" => IsCodexPluginInstalled(known.PluginDirName),
            _ => false
        };
    }

    private static bool IsClaudePluginInstalled(string pluginName)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var skillsDir = Path.Combine(home, ".claude", "skills", pluginName);
        if (Directory.Exists(skillsDir) && File.Exists(Path.Combine(skillsDir, ".claude-plugin", "plugin.json")))
            return true;

        var cacheDir = Path.Combine(home, ".claude", "plugins", "cache");
        if (Directory.Exists(cacheDir))
        {
            foreach (var dir in Directory.GetDirectories(cacheDir))
            {
                if (Path.GetFileName(dir).StartsWith(pluginName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private static bool IsCodexPluginInstalled(string pluginName)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var codexPlugins = Path.Combine(home, ".codex", "plugins", pluginName);
        if (Directory.Exists(codexPlugins) && File.Exists(Path.Combine(codexPlugins, ".codex-plugin", "plugin.json")))
            return true;

        var agentsPlugins = Path.Combine(home, ".agents", "plugins");
        if (Directory.Exists(agentsPlugins))
        {
            var marketplace = Path.Combine(agentsPlugins, "marketplace.json");
            if (File.Exists(marketplace))
            {
                try
                {
                    var json = JsonDocument.Parse(File.ReadAllText(marketplace));
                    if (json.RootElement.TryGetProperty("plugins", out var plugins))
                    {
                        foreach (var p in plugins.EnumerateArray())
                        {
                            if (p.TryGetProperty("name", out var n) && n.GetString() == pluginName)
                                return true;
                        }
                    }
                }
                catch { }
            }
        }
        return false;
    }

    public async Task InitializeAsync(IDynamicIslandApi api)
    {
        _api = api;
        _enabled = api.Settings.Get("acpmonitor.enabled", true);

        _vm = new AcpMonitorViewModel
        {
            AgentName = "Agent",
            StatusText = "等待插件上报",
        };

        _receiver = new AgentEventReceiver();
        _receiver.StateChanged += state => RunOnUI(() =>
        {
            _vm.State = state;
            _vm.StatusText = state switch
            {
                AgentSessionState.Idle => "等待任务",
                AgentSessionState.Running => "执行中...",
                AgentSessionState.Connected => "已完成",
                _ => ""
            };
            StatusText = _vm.StatusText;

            switch (state)
            {
                case AgentSessionState.Running:
                    SetLive(true);
                    break;
                case AgentSessionState.Connected:
                    if (_liveShown)
                    {
                        _api.SendMessage(new IslandMessage
                        {
                            Title = $"{_vm.AgentName} 任务完成",
                            Text = _vm.TotalSteps > 0 ? $"完成 {_vm.CompletedSteps}/{_vm.TotalSteps} 步" : "任务已完成",
                            Glyph = "\uE73E",
                            AccentColor = Windows.UI.Color.FromArgb(255, 108, 203, 95),
                            Duration = TimeSpan.FromSeconds(4),
                        });
                    }
                    SetLive(false);
                    break;
                case AgentSessionState.Idle:
                    SetLive(false);
                    break;
            }
        });

        _receiver.PlanUpdated += entries => RunOnUI(() =>
        {
            _vm.PlanEntries = entries;
            _islandView.UpdateFromPlan(entries);
        });

        _receiver.ToolCallUpdated += toolCall => RunOnUI(() =>
        {
            _vm.ToolCalls = _receiver.ToolCalls;
            _islandView.UpdateFromToolCall(toolCall);
        });

        _receiver.UsageUpdated += usage => RunOnUI(() =>
        {
            _vm.UsedTokens = usage.UsedTokens;
            _vm.CostAmount = usage.CostAmount;
            _vm.CostCurrency = usage.CostCurrency;
        });

        _receiver.ErrorOccurred += error => RunOnUI(() =>
        {
            StatusText = $"错误: {error}";
            _vm.StatusText = StatusText;
        });

        _receiver.EventReceived += evt => RunOnUI(() =>
        {
            if (!string.IsNullOrEmpty(evt.Agent))
            {
                var known = KnownAgents.FirstOrDefault(a => a.Id.Equals(evt.Agent, StringComparison.OrdinalIgnoreCase));
                if (known != default)
                {
                    _vm.AgentIcon = GetAgentIcon(known.Id);
                    _vm.AccentColor = GetAgentAccentColor(known.Id);
                    _vm.AgentName = known.Name;
                }
                else
                {
                    _vm.AgentName = evt.Agent;
                }
            }

            switch (evt.Type)
            {
                case "prompt.submit":
                    var promptText = "";
                    if (evt.Data.TryGetProperty("prompt", out var pt))
                        promptText = pt.GetString() ?? "";
                    else if (evt.Data.TryGetProperty("content", out var pc))
                        promptText = pc.GetString() ?? "";

                    _vm.CurrentTaskName = string.IsNullOrWhiteSpace(promptText) ? "新任务" : Truncate(promptText, 40);
                    _vm.State = AgentSessionState.Running;
                    _vm.StatusText = "执行中...";
                    StatusText = _vm.StatusText;
                    SetLive(true);
                    _api.SendMessage(new IslandMessage
                    {
                        Title = $"{_vm.AgentName} 开始任务",
                        Text = _vm.CurrentTaskName,
                        Glyph = _vm.AgentIcon,
                        AccentColor = _vm.AccentColor,
                        Duration = TimeSpan.FromSeconds(3),
                    });
                    break;
                case "session.end":
                case "stop":
                    if (_vm.State == AgentSessionState.Running)
                    {
                        _vm.State = AgentSessionState.Connected;
                        _vm.StatusText = "已完成";
                        StatusText = _vm.StatusText;
                        _api.SendMessage(new IslandMessage
                        {
                            Title = $"{_vm.AgentName} 任务完成",
                            Text = _vm.TotalSteps > 0 ? $"完成 {_vm.CompletedSteps}/{_vm.TotalSteps} 步" : "任务已完成",
                            Glyph = "\uE73E",
                            AccentColor = Windows.UI.Color.FromArgb(255, 108, 203, 95),
                            Duration = TimeSpan.FromSeconds(4),
                        });
                        SetLive(false);
                    }
                    break;
            }
        });

        _receiver.Register(api.Callbacks, "/agent/event");

        _islandView = new AcpMonitorIslandView(_vm);

        _content = new IslandLiveContent
        {
            MorphView = _islandView,
            CompactSize = new Windows.Foundation.Size(230, 40),
            ExpandedSize = new Windows.Foundation.Size(420, 150),
        };

        api.AddSettingsPage(new SettingsPageDescriptor(
            "acpmonitor", DisplayName, "\uE932", () => new AcpMonitorSettingsPage(_api, this)));

        api.Settings.Changed += key =>
        {
            if (key == "acpmonitor.enabled")
            {
                _enabled = _api.Settings.Get("acpmonitor.enabled", true);
                if (!_enabled) SetLive(false);
            }
        };

        await Task.CompletedTask;
    }

    public async Task ShutdownAsync()
    {
        await _receiver.DisposeAsync();
        SetLive(false);
    }

    public async Task InstallPluginAsync(string agentId)
    {
        var known = KnownAgents.FirstOrDefault(a => a.Id.Equals(agentId, StringComparison.OrdinalIgnoreCase));
        if (known == default) return;

        var callbackUrl = $"{CallbackBaseUrl}/agent/event";

        switch (agentId)
        {
            case "opencode":
                await InstallOpenCodePluginAsync(known, callbackUrl);
                break;
            case "claude":
                await InstallClaudePluginAsync(known, callbackUrl);
                break;
            case "codex":
                await InstallCodexPluginAsync(known, callbackUrl);
                break;
        }
    }

    private async Task InstallOpenCodePluginAsync((string Name, string Id, string Exe, string EnvVar, string PluginDirName) known, string callbackUrl)
    {
        var pluginsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "opencode", "plugins");
        Directory.CreateDirectory(pluginsDir);

        var pluginFile = Path.Combine(pluginsDir, known.PluginDirName + ".js");

        var script = $@"export const WinIslandPlugin = async ({{ project, client, $, directory, worktree }}) => {{
  const CALLBACK_URL = ""{callbackUrl}"";

  async function report(type, data = {{}}) {{
    try {{
      await fetch(CALLBACK_URL, {{
        method: ""POST"",
        headers: {{ ""Content-Type"": ""application/json"" }},
        body: JSON.stringify({{ type, agent: ""{known.Id}"", ...data }}),
      }});
    }} catch {{}}
  }}

  await report(""session.start"", {{ agentName: ""{known.Name}"" }});

  return {{
    event: async ({{ event }}) => {{
      const data = event.data || {{}};

      switch (event.type) {{
        case ""session.idle"":
          await report(""session.idle"", {{ sessionId: data.id }});
          break;
        case ""session.updated"":
          await report(""session.start"", {{ sessionId: data.id, agentName: data.title || ""{known.Name}"" }});
          break;
        case ""session.diff"":
          if (data.summary) {{
            await report(""usage.update"", {{
              usedTokens: data.summary.tokens || 0,
              costAmount: data.summary.cost || 0,
            }});
          }}
          break;
        case ""tool.execute.before"":
          await report(""tool.start"", {{
            toolCallId: data.id || String(Date.now()),
            toolName: data.tool || ""unknown"",
            title: data.tool || ""unknown"",
          }});
          break;
        case ""tool.execute.after"":
          await report(""tool.end"", {{
            toolCallId: data.id || String(Date.now()),
            toolName: data.tool || ""unknown"",
          }});
          break;
      }}
    }},

    ""session.created"": async () => {{
      await report(""session.start"", {{ agentName: ""{known.Name}"" }});
    }},

    ""session.idle"": async () => {{
      await report(""session.idle"");
    }},
  }};
}};
";

        await File.WriteAllTextAsync(pluginFile, script);
    }

    private async Task InstallClaudePluginAsync((string Name, string Id, string Exe, string EnvVar, string PluginDirName) known, string callbackUrl)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var pluginDir = Path.Combine(home, ".claude", "skills", known.PluginDirName);
        var manifestDir = Path.Combine(pluginDir, ".claude-plugin");
        var hooksDir = Path.Combine(pluginDir, "hooks");

        Directory.CreateDirectory(manifestDir);
        Directory.CreateDirectory(hooksDir);

        var manifest = $$"""
{
  "name": "{{known.PluginDirName}}",
  "displayName": "WinIsland Monitor",
  "description": "Reports Claude Code activity to WinIsland Dynamic Island",
  "version": "1.0.0",
  "author": { "name": "WinIsland" },
  "hooks": "./hooks/hooks.json"
}
""";
        await File.WriteAllTextAsync(Path.Combine(manifestDir, "plugin.json"), manifest);

        var hooksJson = $$"""
{
  "hooks": {
    "UserPromptSubmit": [
      {
        "hooks": [
          {
            "type": "http",
            "url": "{{callbackUrl}}",
            "timeout": 5
          }
        ]
      }
    ],
    "PreToolUse": [
      {
        "hooks": [
          {
            "type": "http",
            "url": "{{callbackUrl}}",
            "timeout": 5
          }
        ]
      }
    ],
    "PostToolUse": [
      {
        "hooks": [
          {
            "type": "http",
            "url": "{{callbackUrl}}",
            "timeout": 5
          }
        ]
      }
    ],
    "Stop": [
      {
        "hooks": [
          {
            "type": "http",
            "url": "{{callbackUrl}}",
            "timeout": 5
          }
        ]
      }
    ],
    "SessionStart": [
      {
        "hooks": [
          {
            "type": "http",
            "url": "{{callbackUrl}}",
            "timeout": 5
          }
        ]
      }
    ]
  }
}
""";
        await File.WriteAllTextAsync(Path.Combine(hooksDir, "hooks.json"), hooksJson);
    }

    private async Task InstallCodexPluginAsync((string Name, string Id, string Exe, string EnvVar, string PluginDirName) known, string callbackUrl)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var pluginDir = Path.Combine(home, ".codex", "plugins", known.PluginDirName);
        var manifestDir = Path.Combine(pluginDir, ".codex-plugin");
        var hooksDir = Path.Combine(pluginDir, "hooks");
        var scriptsDir = Path.Combine(pluginDir, "scripts");

        Directory.CreateDirectory(manifestDir);
        Directory.CreateDirectory(hooksDir);
        Directory.CreateDirectory(scriptsDir);

        var manifestObj = new Dictionary<string, object>
        {
            ["name"] = known.PluginDirName,
            ["version"] = "1.0.0",
            ["description"] = "Reports Codex activity to WinIsland Dynamic Island",
            ["author"] = new { name = "WinIsland" },
            ["hooks"] = "./hooks/hooks.json",
        };
        await File.WriteAllTextAsync(Path.Combine(manifestDir, "plugin.json"),
            JsonSerializer.Serialize(manifestObj, new JsonSerializerOptions { WriteIndented = true }));

        var reportPs1 = "$body = @{ type = $args[0]; agent = \"codex\" } | ConvertTo-Json -Compress\n" +
            "try { Invoke-RestMethod -Uri \"" + callbackUrl + "\" -Method Post -ContentType \"application/json\" -Body $body } catch { }";
        await File.WriteAllTextAsync(Path.Combine(scriptsDir, "report.ps1"), reportPs1);

        var sessionStartPs1 = "$body = @{ type = \"session.start\"; agent = \"codex\"; agentName = \"Codex CLI\" } | ConvertTo-Json -Compress\n" +
            "try { Invoke-RestMethod -Uri \"" + callbackUrl + "\" -Method Post -ContentType \"application/json\" -Body $body } catch { }";
        await File.WriteAllTextAsync(Path.Combine(scriptsDir, "session_start.ps1"), sessionStartPs1);

        var sessionEndPs1 = "$body = @{ type = \"session.idle\"; agent = \"codex\" } | ConvertTo-Json -Compress\n" +
            "try { Invoke-RestMethod -Uri \"" + callbackUrl + "\" -Method Post -ContentType \"application/json\" -Body $body } catch { }";
        await File.WriteAllTextAsync(Path.Combine(scriptsDir, "session_end.ps1"), sessionEndPs1);

        var hooksObj = new Dictionary<string, object>
        {
            ["hooks"] = new Dictionary<string, object[]>
            {
                ["SessionStart"] = new object[]
                {
                    new { hooks = new object[] { new { type = "command", command = $"powershell -ExecutionPolicy Bypass -File \"${{PLUGIN_ROOT}}/scripts/session_start.ps1\"" } } }
                },
                ["PostToolUse"] = new object[]
                {
                    new { hooks = new object[] { new { type = "command", command = $"powershell -ExecutionPolicy Bypass -File \"${{PLUGIN_ROOT}}/scripts/report.ps1\" tool.end" } } }
                },
                ["Stop"] = new object[]
                {
                    new { hooks = new object[] { new { type = "command", command = $"powershell -ExecutionPolicy Bypass -File \"${{PLUGIN_ROOT}}/scripts/session_end.ps1\"" } } }
                },
            }
        };
        await File.WriteAllTextAsync(Path.Combine(hooksDir, "hooks.json"),
            JsonSerializer.Serialize(hooksObj, new JsonSerializerOptions { WriteIndented = true }));

        var marketplaceDir = Path.Combine(home, ".agents", "plugins");
        Directory.CreateDirectory(marketplaceDir);

        var marketplacePath = Path.Combine(marketplaceDir, "marketplace.json");
        Dictionary<string, object>? marketplace = null;
        if (File.Exists(marketplacePath))
        {
            try
            {
                marketplace = JsonSerializer.Deserialize<Dictionary<string, object>>(await File.ReadAllTextAsync(marketplacePath));
            }
            catch { }
        }
        if (marketplace == null)
        {
            marketplace = new Dictionary<string, object>
            {
                ["name"] = "winisland-plugins",
                ["plugins"] = new List<object>(),
            };
        }

        if (!marketplace.TryGetValue("plugins", out var pluginsObj) || pluginsObj is not JsonElement pluginsEl)
        {
            marketplace["plugins"] = new List<object>();
            pluginsEl = default;
        }

        var hasEntry = false;
        if (pluginsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in pluginsEl.EnumerateArray())
            {
                if (p.TryGetProperty("name", out var n) && n.GetString() == known.PluginDirName)
                {
                    hasEntry = true;
                    break;
                }
            }
        }

        if (!hasEntry)
        {
            var newPlugin = new Dictionary<string, object>
            {
                ["name"] = known.PluginDirName,
                ["source"] = new { source = "local", path = $"./.codex/plugins/{known.PluginDirName}" },
                ["policy"] = new { installation = "AVAILABLE", authentication = "ON_INSTALL" },
                ["category"] = "Productivity",
            };

            var existingList = new List<object>();
            if (pluginsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in pluginsEl.EnumerateArray())
                    existingList.Add(p);
            }
            existingList.Add(newPlugin);
            marketplace["plugins"] = existingList;

            await File.WriteAllTextAsync(marketplacePath,
                JsonSerializer.Serialize(marketplace, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private void SetLive(bool show)
    {
        if (!_enabled) show = false;
        if (show == _liveShown) return;
        _liveShown = show;
        _api.SetLiveContent(Id, show ? _content : null);
    }

    private void RunOnUI(Action action)
    {
        if (_api.Dispatcher.HasThreadAccess)
            action();
        else
            _api.Dispatcher.TryEnqueue(() => action());
    }

    private static string GetAgentIcon(string agentId) => agentId switch
    {
        "opencode" => "\uE943",
        "claude" => "\uE8F1",
        "codex" => "\uE912",
        _ => "\uE932"
    };

    private static Windows.UI.Color GetAgentAccentColor(string agentId) => agentId switch
    {
        "opencode" => Windows.UI.Color.FromArgb(255, 46, 204, 113),
        "claude" => Windows.UI.Color.FromArgb(255, 222, 133, 34),
        "codex" => Windows.UI.Color.FromArgb(255, 52, 152, 219),
        _ => Windows.UI.Color.FromArgb(255, 138, 43, 226)
    };

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen] + "...";
}
