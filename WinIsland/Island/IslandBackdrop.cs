using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using WinIsland.Core;
using WinRT;
using System.Numerics;

namespace WinIsland.Island;

/// <summary>
/// 窗口级系统材质（Desktop Acrylic / Mica）。
///
/// 不用内置的 MicaBackdrop / DesktopAcrylicBackdrop 的原因：它们把 SystemBackdropConfiguration
/// 的 IsInputActive 绑到窗口激活状态，而灵动岛窗口是 WS_EX_NOACTIVATE、永远不激活 —— 材质会被
/// 系统按「非激活」降级成纯色（深色主题下就是 #2C2C2C 那种灰）。这里直接使用控制器，自己提供配置
/// 并把 IsInputActive 固定为 true，才能拿到真正的材质外观。
///
/// 明暗主题同样由这份配置决定：材质是系统画的，浅色主题必须显式告诉它，否则浅色壁纸上会糊一层
/// 深色磨砂（那正是「没适配浅色/深色」的样子）。主题变化可以就地改配置，不必重建控制器。
/// </summary>
internal sealed class IslandBackdrop : IDisposable
{
    private readonly MaskedTarget _target;
    private SystemBackdropConfiguration? _configuration;
    private DesktopAcrylicController? _acrylic;
    private MicaController? _mica;

    public IslandBackdrop(Window window) => _target = new MaskedTarget(window);
    public bool GeometryMoving => _target.GeometryMoving;
    public event Action? ClipSettled;
    public void SetGeometryMoving(bool moving) => _target.SetGeometryMoving(moving, () => ClipSettled?.Invoke());

    // Physical client coordinates, from the same rendered bounds as the input region.
    public void UpdateClip(IReadOnlyList<(float X, float Y, float Width, float Height, float Radius)> bounds,
        int width, int height) => _target.UpdateClip(bounds, width, height);
#if DEBUG
    internal bool DebugMaskReady => _target.IsReady;
#endif

    public static bool IsSupported(IslandMaterialKind material)
        => material == IslandMaterialKind.Mica
            ? MicaController.IsSupported()
            : DesktopAcrylicController.IsSupported();

    /// <summary>
    /// 挂上指定材质与明暗主题。系统不支持该材质时返回 false 且保持现状；
    /// 材质没变时只更新主题（主题切换不该重建控制器，那会闪一下）。
    /// </summary>
    public bool TryApply(IslandMaterialKind material, bool light)
    {
        if (!IsSupported(material)) return false;

        CompositorProvider.EnsureDispatcherQueue();
        _configuration ??= new SystemBackdropConfiguration { IsInputActive = true };
        _configuration.Theme = light ? SystemBackdropTheme.Light : SystemBackdropTheme.Dark;

        var target = _target;
        if (material == IslandMaterialKind.Mica)
        {
            if (_mica != null) return true;

            var controller = new MicaController { Kind = MicaKind.Base };
            controller.AddSystemBackdropTarget(target);
            controller.SetSystemBackdropConfiguration(_configuration);
            Replace(acrylic: null, mica: controller);
        }
        else
        {
            if (_acrylic != null) return true;

            var controller = new DesktopAcrylicController();
            controller.AddSystemBackdropTarget(target);
            controller.SetSystemBackdropConfiguration(_configuration);
            Replace(acrylic: controller, mica: null);
        }

        return true;
    }

    public void Clear() => Replace(acrylic: null, mica: null);

    public void Dispose()
    {
        Clear();
        _configuration = null;
        _target.Dispose();
    }

    /// <summary>新材质先挂上、旧材质后释放，切换过程中不会出现「两个都不在」的空档。</summary>
    private void Replace(DesktopAcrylicController? acrylic, MicaController? mica)
    {
        var oldAcrylic = _acrylic;
        var oldMica = _mica;
        _acrylic = acrylic;
        _mica = mica;
        oldAcrylic?.Dispose();
        oldMica?.Dispose();
    }

    /// <summary>
    /// System materials use the Windows compositor, not XAML's lifted compositor.
    /// Mask the brush on that compositor before handing it to the window: the extra
    /// input-region pixels then remain transparent instead of exposing raw material.
    /// </summary>
    private sealed partial class MaskedTarget : ICompositionSupportsSystemBackdrop, IDisposable
    {
        private readonly ICompositionSupportsSystemBackdrop _window;
        private Windows.UI.Composition.CompositionBrush? _source;
        private Windows.UI.Composition.CompositionMaskBrush? _mask;
        private Windows.UI.Composition.CompositionSurfaceBrush? _maskSurface;
        private Windows.UI.Composition.CompositionVisualSurface? _surface;
        private Windows.UI.Composition.ShapeVisual? _shapes;
        private Windows.UI.Composition.CompositionColorBrush? _white;
        private readonly List<Windows.UI.Composition.CompositionRoundedRectangleGeometry> _geometry = [];
        private (float X, float Y, float Width, float Height, float Radius)[] _bounds = [];
        private Vector2 _size = Vector2.One;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _settle;
        private Action? _onSettled;
        internal bool GeometryMoving { get; private set; }
        internal void SetGeometryMoving(bool moving, Action settled)
        {
            if (moving)
            {
                _settle?.Stop();
                if (GeometryMoving) return;
                GeometryMoving = true;
                if (_source != null) _window.SystemBackdrop = _source;
            }
            else if (GeometryMoving && _settle?.IsRunning != true)
            {
                _onSettled = settled;
                if (_settle == null)
                {
                    _settle = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
                    _settle.Tick += (_, _) =>
                    {
                        GeometryMoving = false;
                        if (_mask != null && _source != null) _window.SystemBackdrop = _mask;
                        _onSettled?.Invoke();
                    };
                }
                _settle.IsRepeating = false;
                _settle.Interval = TimeSpan.FromMilliseconds(40);
                PaintClip();
                _settle.Start();
            }
        }

        internal MaskedTarget(Window window) => _window = window.As<ICompositionSupportsSystemBackdrop>();
#if DEBUG
        internal bool IsReady => _source != null && _mask?.Source != null && _bounds.Length > 0
            && _mask.Compositor.Equals(_source.Compositor) && _window.SystemBackdrop != null;
#endif

        public Windows.UI.Composition.CompositionBrush SystemBackdrop
        {
            get => _source!;
            set
            {
                _source = value;
                if (value == null) { ReleaseMask(); return; }
                if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
                { _window.SystemBackdrop = value; return; }
                // Each controller can supply a brush from a different compositor.
                // Composition objects from different compositors must never be mixed.
                ReleaseMask();
                {
                    var compositor = value.Compositor;
                    _white = compositor.CreateColorBrush(Microsoft.UI.Colors.White);
                    _shapes = compositor.CreateShapeVisual();
                    _surface = compositor.CreateVisualSurface();
                    _surface.SourceVisual = _shapes;
                    _maskSurface = compositor.CreateSurfaceBrush(_surface);
                    _maskSurface.BitmapInterpolationMode = Windows.UI.Composition.CompositionBitmapInterpolationMode.Linear;
                    _mask = compositor.CreateMaskBrush();
                    _mask.Mask = _maskSurface;
                }
                _mask.Source = value;
                PaintClip();
                _window.SystemBackdrop = GeometryMoving ? value : _mask;
            }
        }

        internal void UpdateClip(IReadOnlyList<(float X, float Y, float Width, float Height, float Radius)> bounds,
            int width, int height)
        {
            var size = new Vector2(Math.Max(1, width), Math.Max(1, height));
            if (_size == size && _bounds.SequenceEqual(bounds)) return;
            _size = size;
            _bounds = bounds.ToArray();
            // Do not re-rasterize an offscreen visual surface on every layout frame.
            // The native contour clips stable material while geometry is moving.
            if (_settle?.IsRunning == true || !GeometryMoving) PaintClip();
        }

        private void PaintClip()
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362)
                || _shapes == null || _surface == null) return;
            // Render the alpha contour at 2x, then downsample. The GDI region stays
            // outside this feathered edge and never defines the visible silhouette.
            const float sampling = 2;
            _shapes.Size = _size * sampling;
            _surface.SourceSize = _size * sampling;
            var compositor = _shapes.Compositor;
            // Reuse geometry during size morphs; no per-frame composition allocation.
            while (_geometry.Count < _bounds.Length)
            {
                var geometry = compositor.CreateRoundedRectangleGeometry();
                var shape = compositor.CreateSpriteShape(geometry);
                shape.FillBrush = _white;
                _geometry.Add(geometry);
                _shapes.Shapes.Add(shape);
            }
            for (int i = 0; i < _geometry.Count; i++)
            {
                var g = _geometry[i];
                if (i >= _bounds.Length) { g.Size = Vector2.Zero; continue; }
                var b = _bounds[i];
                g.Offset = new Vector2(b.X, b.Y) * sampling;
                g.Size = new Vector2(Math.Max(0, b.Width), Math.Max(0, b.Height)) * sampling;
                float radius = Math.Clamp(b.Radius * sampling, 0, Math.Min(g.Size.X, g.Size.Y) / 2);
                g.CornerRadius = new Vector2(radius);
            }
        }

        private void ReleaseMask()
        {
            _window.SystemBackdrop = null;
            if (_mask != null) { _mask.Source = null; _mask.Mask = null; }
            _mask?.Dispose(); _maskSurface?.Dispose(); _surface?.Dispose();
            if (_shapes != null)
                foreach (var shape in _shapes.Shapes) shape.Dispose();
            _shapes?.Shapes.Clear();
            foreach (var geometry in _geometry) geometry.Dispose();
            _geometry.Clear();
            _shapes?.Dispose(); _white?.Dispose();
            _mask = null; _maskSurface = null; _surface = null; _shapes = null; _white = null;
        }

        public void Dispose() { _settle?.Stop(); ReleaseMask(); _source = null; }
    }
}
