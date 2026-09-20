using System.Runtime.InteropServices;
using Windows.Graphics;

namespace WinIsland.Core;

/// <summary>
/// 读出任务栏上「已被 explorer 自己占用的横向区段」，从而算出「空闲区段」。
/// 岛据此落进真正空闲的位置：Win11 图标是否居中、搜索框多宽、托盘从哪开始，全靠实测，
/// 不再靠猜（早期实现里「任务栏左端一定是空的」在图标多的机器上就是错的）。
/// 实现走 UI Automation：外壳任务栏的每个按钮都是 UIA 元素，带真实包围框。
/// UIA 调用较慢（几十到几百毫秒）且可能不可用，因此本类只在后台线程调用，失败返回空数组，
/// 由调用方回退到「按条带边缘对齐」的落点。
/// </summary>
internal static class TaskbarLayout
{
    /// <summary>UIA TreeScope_Descendants。</summary>
    private const int TreeScopeDescendants = 4;

    /// <summary>UIA_BoundingRectanglePropertyId：返回 [left, top, width, height]（double）。</summary>
    private const int BoundingRectanglePropertyId = 30001;

    /// <summary>UIA_IsOffscreenPropertyId。</summary>
    private const int IsOffscreenPropertyId = 30022;

    /// <summary>UIA_ControlTypePropertyId。</summary>
    private const int ControlTypePropertyId = 30003;

    /// <summary>
    /// 只按控件类型排除「容器」：Pane/Group/Window 是布局容器（整条任务栏的 frame、输入站点），
    /// 它们本身不是可见内容，留着会把整段任务栏算成占用。
    /// 注意不能反过来「谁包含别的元素就当容器删掉」——任务栏按钮里就包着图标/文本子元素，
    /// 那样会把按钮本身误删（曾经就这么把 0..1432 全算成了占用）。
    /// </summary>
    private static readonly int[] ContainerControlTypes = { 50026 /* Group */, 50032 /* Window */, 50033 /* Pane */ };

    /// <summary>比这还窄的空闲段没有意义（物理像素）。</summary>
    private const int MinBandWidth = 24;

    /// <summary>最近一次失败原因（诊断用；成功时清空）。</summary>
    public static string? LastError { get; private set; }

    /// <summary>最近一次枚举的明细（诊断用）。</summary>
    public static string? LastDiagnostics { get; private set; }

    /// <summary>
    /// 任务栏条带内的空闲横向区段（物理像素，按左边界升序）。UIA 不可用、调用失败或没有可用空闲段时返回空数组。
    /// 必须从后台（MTA）线程调用：UIA 客户端调用是阻塞的，放在 UI 线程会卡住动画。
    /// </summary>
    public static RectInt32[] GetFreeBands(nint taskbarHwnd, RectInt32 strip)
    {
        LastError = null;
        if (taskbarHwnd == nint.Zero || strip.Width <= 0 || strip.Height <= 0)
        {
            LastError = "任务栏句柄/条带无效";
            return Array.Empty<RectInt32>();
        }

        try
        {
            var uia = (IUIAutomation)new CUIAutomation();

            int hr = uia.ElementFromHandle(taskbarHwnd, out var root);
            if (hr != 0 || root is null)
            {
                LastError = $"ElementFromHandle hr=0x{hr:X8}";
                return Array.Empty<RectInt32>();
            }

            hr = uia.CreateTrueCondition(out var trueCondition);
            if (hr != 0 || trueCondition is null)
            {
                LastError = $"CreateTrueCondition hr=0x{hr:X8}";
                return Array.Empty<RectInt32>();
            }

            hr = root.FindAll(TreeScopeDescendants, trueCondition, out var found);
            if (hr != 0 || found is null)
            {
                LastError = $"FindAll hr=0x{hr:X8}";
                return Array.Empty<RectInt32>();
            }

            found.GetLength(out int count);
            var rects = new List<RectInt32>(count);
            for (int i = 0; i < count; i++)
            {
                if (found.GetElement(i, out var element) != 0 || element is null) continue;

                // 收起的分组、被隐藏的项（包围框可能是 0 或离屏值）
                if (element.GetCurrentPropertyValue(IsOffscreenPropertyId, out var offscreen) == 0
                    && offscreen is true)
                {
                    continue;
                }

                if (element.GetCurrentPropertyValue(BoundingRectanglePropertyId, out var box) != 0) continue;
                if (ToRect(box) is not { } rect || rect.Width <= 0 || rect.Height <= 0) continue;

                // 容器类型不算占用（frame、输入站点）
                if (element.GetCurrentPropertyValue(ControlTypePropertyId, out var typeValue) == 0
                    && typeValue is int controlType
                    && ContainerControlTypes.Contains(controlType))
                {
                    continue;
                }

                // 兜底保护：任何横跨大半条带的东西都是容器，不是内容
                if (rect.Width * 20 >= strip.Width * 9) continue;

                // 只算真正落在任务栏这一横排里的元素：过滤掉浮层、提示、进度条之类的杂物
                int verticalOverlap = Math.Min(rect.Y + rect.Height, strip.Y + strip.Height) - Math.Max(rect.Y, strip.Y);
                if (verticalOverlap * 2 < strip.Height) continue;

                rects.Add(rect);
            }

            // 合并成占用段，再对条带取补集
            rects.Sort((a, b) => a.X.CompareTo(b.X));
            var free = new List<RectInt32>();
            var occupied = new List<string>();
            int cursor = strip.X;
            foreach (var rect in rects)
            {
                if (rect.X > cursor) free.Add(Band(cursor, rect.X - cursor, strip));
                if (rect.X + rect.Width > cursor) occupied.Add($"{rect.X}..{rect.X + rect.Width}");
                cursor = Math.Max(cursor, rect.X + rect.Width);
            }
            if (cursor < strip.X + strip.Width) free.Add(Band(cursor, strip.X + strip.Width - cursor, strip));

            LastDiagnostics = $"元素 {rects.Count}；占用段 {string.Join("、", occupied)}";
            return free.Where(b => b.Width >= MinBandWidth).ToArray();
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            return Array.Empty<RectInt32>();
        }
    }

    private static RectInt32 Band(int x, int width, RectInt32 strip) => new(x, strip.Y, width, strip.Height);

    private static bool Contains(RectInt32 outer, RectInt32 inner)
        => inner.X >= outer.X && inner.Y >= outer.Y
           && inner.X + inner.Width <= outer.X + outer.Width
           && inner.Y + inner.Height <= outer.Y + outer.Height;

    /// <summary>UIA 的 BoundingRectangle 属性是 4 个 double 的数组，经 VARIANT 传回来。</summary>
    private static RectInt32? ToRect(object? value)
    {
        if (value is double[] { Length: 4 } d)
        {
            return new RectInt32((int)Math.Round(d[0]), (int)Math.Round(d[1]), (int)Math.Round(d[2]), (int)Math.Round(d[3]));
        }

        if (value is Array { Length: 4 } a)
        {
            try
            {
                return new RectInt32(
                    (int)Math.Round(Convert.ToDouble(a.GetValue(0))),
                    (int)Math.Round(Convert.ToDouble(a.GetValue(1))),
                    (int)Math.Round(Convert.ToDouble(a.GetValue(2))),
                    (int)Math.Round(Convert.ToDouble(a.GetValue(3))));
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    #region UI Automation COM 互操作

    // 说明：COM 互操作按「声明顺序」分配 vtable 槽位，所以未用到的成员也必须占位
    // （签名不影响槽位数，但顺序绝不能再排错——排错会调到别的方法上）。
    // 只需要前 19 个方法，其中真正调用的只有 ElementFromHandle 与 CreateTrueCondition。

    [ComImport, Guid("FF48DBA4-60EF-4201-AA87-54103EEF594E")]
    private class CUIAutomation
    {
    }

    [ComImport, Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        [PreserveSig] int CompareElements(nint el1, nint el2, out int areSame);
        [PreserveSig] int CompareRuntimeIds(nint ids1, nint ids2, out int areSame);
        [PreserveSig] int GetRootElement(out IUIAutomationElement root);
        [PreserveSig] int ElementFromHandle(nint hwnd, out IUIAutomationElement element);
        [PreserveSig] int ElementFromPoint(Win32.POINT pt, out IUIAutomationElement element);
        [PreserveSig] int GetFocusedElement(out IUIAutomationElement element);
        [PreserveSig] int GetRootElementBuildCache(nint cache, out IUIAutomationElement element);
        [PreserveSig] int ElementFromHandleBuildCache(nint hwnd, nint cache, out IUIAutomationElement element);
        [PreserveSig] int ElementFromPointBuildCache(Win32.POINT pt, nint cache, out IUIAutomationElement element);
        [PreserveSig] int GetFocusedElementBuildCache(nint cache, out IUIAutomationElement element);
        [PreserveSig] int CreateTreeWalker(IUIAutomationCondition condition, out nint walker);
        [PreserveSig] int GetControlViewWalker(out nint walker);
        [PreserveSig] int GetContentViewWalker(out nint walker);
        [PreserveSig] int GetRawViewWalker(out nint walker);
        [PreserveSig] int GetRawViewCondition(out nint condition);
        [PreserveSig] int GetControlViewCondition(out nint condition);
        [PreserveSig] int GetContentViewCondition(out nint condition);
        [PreserveSig] int CreateCacheRequest(out nint cache);
        [PreserveSig] int CreateTrueCondition(out IUIAutomationCondition condition);
    }

    // 只需要前 8 个方法，真正调用的是 FindAll 与 GetCurrentPropertyValue。

    [ComImport, Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        [PreserveSig] int SetFocus();
        [PreserveSig] int GetRuntimeId(out nint runtimeId);
        [PreserveSig] int FindFirst(int scope, IUIAutomationCondition condition, out IUIAutomationElement found);
        [PreserveSig] int FindAll(int scope, IUIAutomationCondition condition, out IUIAutomationElementArray found);
        [PreserveSig] int FindFirstBuildCache(int scope, IUIAutomationCondition condition, nint cache, out nint found);
        [PreserveSig] int FindAllBuildCache(int scope, IUIAutomationCondition condition, nint cache, out nint found);
        [PreserveSig] int BuildUpdatedCache(nint cache, out nint updated);
        [PreserveSig] int GetCurrentPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object value);
    }

    [ComImport, Guid("14314595-B4BC-4055-95F2-58F2E42C9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElementArray
    {
        [PreserveSig] int GetLength(out int length);
        [PreserveSig] int GetElement(int index, out IUIAutomationElement element);
    }

    /// <summary>条件对象只是句柄，不需要方法。</summary>
    [ComImport, Guid("352FFBA8-0973-437C-A61F-F64CAFD81DF9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationCondition
    {
    }

    #endregion
}
