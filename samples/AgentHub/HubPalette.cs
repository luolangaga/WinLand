using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinIsland.Core;

namespace AgentHub;

internal sealed class HubPalette : IDisposable
{
    private readonly IIslandTheme _theme;
    public readonly SolidColorBrush Text = new(), Secondary = new(), Muted = new(), Surface = new(),
        Line = new(), Mint = new(), Amber = new(), Coral = new(), Code = new();
    public HubPalette(IIslandTheme theme) { _theme = theme; Apply(); theme.Changed += Apply; }
    private void Apply()
    {
        bool light = _theme.IsLight;
        Text.Color = light ? Color.FromArgb(255, 30, 34, 38) : Color.FromArgb(255, 237, 239, 241);
        Secondary.Color = light ? Color.FromArgb(255, 98, 105, 113) : Color.FromArgb(255, 155, 163, 173);
        Muted.Color = light ? Color.FromArgb(255, 127, 135, 145) : Color.FromArgb(255, 113, 124, 136);
        Surface.Color = light ? Color.FromArgb(13, 35, 50, 65) : Color.FromArgb(14, 210, 225, 240);
        Line.Color = light ? Color.FromArgb(22, 35, 50, 65) : Color.FromArgb(24, 210, 225, 240);
        Mint.Color = light ? Color.FromArgb(255, 22, 120, 93) : Color.FromArgb(255, 113, 212, 180);
        Amber.Color = light ? Color.FromArgb(255, 157, 103, 22) : Color.FromArgb(255, 220, 184, 113);
        Coral.Color = light ? Color.FromArgb(255, 174, 84, 57) : Color.FromArgb(255, 217, 149, 120);
        Code.Color = light ? Color.FromArgb(255, 244, 246, 248) : Color.FromArgb(255, 17, 20, 24);
    }
    public SolidColorBrush Agent(string agent) => agent == "Claude Code" ? Coral : agent == "Codex" ? Mint : Secondary;
    public TextBlock Label(string text, double size = 13, bool secondary = false) => new() {
        Text = text, FontSize = size, Foreground = secondary ? Secondary : Text, TextTrimming = TextTrimming.CharacterEllipsis,
        FontFamily = new FontFamily("Segoe UI Variable Text") };
    public Border Mark(string agent, double size = 32) => new() {
        Width = size, Height = size, CornerRadius = new CornerRadius(size * .30), Background = Surface,
        Child = new TextBlock { Text = agent == "Claude Code" ? "C" : agent == "Codex" ? ">_" : agent[..Math.Min(2, agent.Length)],
            FontSize = size * .40, FontWeight = new() { Weight = 600 }, Foreground = Agent(agent),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
    public void Dispose() => _theme.Changed -= Apply;
}
