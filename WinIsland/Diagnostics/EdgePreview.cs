#if DEBUG
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;
namespace WinIsland.Island;

public sealed partial class IslandWindow
{
    internal void PreviewEdgeExpansion()
    {
        if (_expanded) OnHoverExit(); else OnHoverEnter();
    }
    // Isolated preview only: exercise both palettes without changing Windows settings.
    internal void PreviewEdges(IslandStyleKind style, bool light)
    {
        _style = style;
        _light = IslandStyle.IsLightChrome(style, light);
        RootGrid.RequestedTheme = _light ? ElementTheme.Light : ElementTheme.Dark;
        ApplyBackdrop();
        _mainSurface.ApplyStyle(_style, _materialApplied, _light);
        ApplyActivityCountStyle();
        ApplyCornerRadius(_radiusHeight, _radiusExpanded);
        RefreshQueueStyle();
        UpdateHitRegion(force: true);
    }

    internal async Task RunEdgeChecksAsync()
    {
        var results = new List<object>();
        try
        {
            foreach (var style in new[] { "apple", "fluent-dark", "fluent-light" })
            foreach (var material in new[] { "acrylic", "mica" })
            foreach (var position in new[] { "top", "bottom" })
            foreach (int scale in new[] { 75, 100, 125 })
            {
                _settings.Set("island.material", material);
                _settings.Set("island.position", position);
                _settings.Set("island.scale.island", scale);
                _settings.Set("island.radius", scale == 75 ? 0d : scale == 100 ? 100d : 200d);
                PreviewEdges(style == "apple" ? IslandStyleKind.Apple : IslandStyleKind.Fluent, style == "fluent-light");
                foreach (bool expanded in new[] { true, false })
                {
                    if (expanded) OnHoverEnter(); else OnHoverExit();
                    if (expanded && style == "fluent-light" && material == "acrylic" && scale == 100)
                    {
                        await Task.Delay(150);
                        if (_queueSurfaces.Any(surface => Math.Abs(surface.Border.ActualHeight - surface.RestingHeight) > 1))
                            throw new InvalidOperationException("Queue did not appear at its complete height.");
                        if (_queueSurfaces.Any(surface => surface.Content.Opacity != 1))
                            throw new InvalidOperationException("Queue revealed an empty card before content.");
                        await Task.Delay(410);
                    }
                    else await Task.Delay(expanded ? 560 : 320);
                    // Dependent XAML animations can finish after their nominal wall
                    // time on a busy desktop. Wait for completion within a bound.
                    for (int wait = 0; _sizeStoryboard != null && wait < 30; wait++)
                        await Task.Delay(50);
                    UpdateHitRegion(force: true);
                    if (_sizeStoryboard != null || _expanded != expanded)
                        throw new InvalidOperationException("Size transition did not settle.");
                    if (Math.Abs(IslandRoot.CornerRadius.TopLeft - _radiusTarget) > .01)
                        throw new InvalidOperationException("Contour did not reach its target.");
                    if (_materialApplied && _backdrop?.DebugMaskReady != true)
                        throw new InvalidOperationException("Material mask is missing or belongs to a different compositor.");
                    if (_queueSurfaces.Any(surface => surface.Border.Opacity != 1 || surface.Content.Opacity != 1))
                        throw new InvalidOperationException("Queue contains an unfinished surface.");
                    results.Add(new { style, material, position, scale, expanded, materialApplied = _materialApplied, passed = true });
                }
            }
            for (int i = 0; i < 4; i++)
            {
                OnHoverEnter();
                await Task.Delay(70);
                OnHoverExit();
                await Task.Delay(80);
            }
            await Task.Delay(350);
            if (_expanded || _sizeStoryboard != null
                || Math.Abs(IslandRoot.ActualHeight - _currentIsland.Height) > 1)
                throw new InvalidOperationException("Rapid reversal left an unfinished transition.");
        }
        catch (Exception error) { results.Add(new { passed = false, error = error.ToString() }); }
        var path = Path.Combine(AppContext.BaseDirectory, "edge-checks.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        Application.Current.Exit();
    }
}
#endif
