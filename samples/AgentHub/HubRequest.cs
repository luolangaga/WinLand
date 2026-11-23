using System.Text.Json;
using System.Text.Json.Nodes;
using AgentHub.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinIsland.Core;

namespace AgentHub;

internal static class HubRequest
{
    internal static UIElement Build(PendingRequest r, IPluginContext context, HubPalette palette, Func<string, string, bool> answer, Action details)
    {
        var panel = new StackPanel { Spacing = 8 }; var p = r.Payload;
        panel.Children.Add(palette.Label(r.Agent + " · " + p.At("tool_name").Text(), 13));
        var input = p.At("tool_input"); var command = input.At("command").Text();
        var code = new TextBox { Text = command.Length > 0 ? command : JsonSerializer.Serialize(input, new JsonSerializerOptions { WriteIndented = true }),
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 72, FontSize = 11,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono"), Background = palette.Code, BorderBrush = palette.Line, Padding = new Thickness(10, 7, 10, 7) };
        panel.Children.Add(code); var status = palette.Label("", 10, true); status.TextWrapping = TextWrapping.Wrap;
        var questions = input.At("questions").Items().ToArray(); var answers = new List<(string Key, TextBox Box)>();
        foreach (var question in questions)
        {
            var text = question.At("question").Text(); var box = new TextBox { Header = text, FontSize = 12, PlaceholderText = "输入回答", TextWrapping = TextWrapping.Wrap };
            var options = new StackPanel { Spacing = 4 };
            foreach (var option in question.At("options").Items())
            {
                var label = option.At("label").Text(); var button = new Button { Content = label, FontSize = 11, Padding = new Thickness(10, 4, 10, 4) };
                button.Click += (_, _) => box.Text = question.At("multiSelect").ValueKind == JsonValueKind.True && box.Text.Length > 0 ? box.Text + ", " + label : label; options.Children.Add(button);
            }
            panel.Children.Add(options); panel.Children.Add(box); answers.Add((text, box));
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        void Reply(string reply)
        {
            try { if (answer(r.Id, reply)) { actions.IsHitTestVisible = false; status.Text = "已发送"; } else status.Text = "请求已结束，请回到代理确认状态"; }
            catch (Exception ex) { context.Log.Error("发送审批回答", ex); status.Text = "发送失败，可重试"; }
        }
        Button ActionButton(string text, Action action, bool primary = false)
        {
            var b = new Button { Content = text, FontSize = 11, Padding = new Thickness(12, 5, 12, 5), CornerRadius = new(7) };
            if (primary) { b.Background = palette.Mint; b.Foreground = palette.Code; b.BorderThickness = new(0); }
            b.Click += (_, _) => { try { action(); } catch (Exception ex) { context.Log.Error("请求操作", ex); status.Text = "操作失败"; } }; return b;
        }
        if (questions.Length > 0 && r.Agent != "Codex")
            actions.Children.Add(ActionButton("提交回答", () => {
                if (answers.Any(a => string.IsNullOrWhiteSpace(a.Box.Text))) { status.Text = "请回答所有问题，或回到代理处理"; return; }
                var updated = JsonNode.Parse(input.GetRawText())!.AsObject(); var values = new JsonObject(); foreach (var (key, box) in answers) values[key] = box.Text; updated["answers"] = values;
                Reply(new JsonObject { ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "PermissionRequest", ["decision"] = new JsonObject { ["behavior"] = "allow", ["updatedInput"] = updated } } }.ToJsonString());
            }, true));
        else { actions.Children.Add(ActionButton("允许一次", () => Reply(HookServer.Decision(true)), true)); actions.Children.Add(ActionButton("拒绝", () => Reply(HookServer.Decision(false)))); }
        actions.Children.Add(ActionButton("回到代理", () => Reply("{}")));
        actions.Children.Add(ActionButton("明细 ↗", details));
        panel.Children.Add(actions); panel.Children.Add(status); return panel;
    }
}
