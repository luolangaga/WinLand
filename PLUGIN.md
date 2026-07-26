# WinIsland 插件开发指南

WinIsland 是一个 Windows 桌面灵动岛应用。主程序负责窗口管理（透明、置顶、无白边）、悬浮检测、尺寸动画、临时消息覆盖/恢复、设置窗口外壳。插件只提供内容——不接触尺寸、圆角、悬浮事件。

## 快速开始

### 1. 实现插件

创建一个类，实现 `IIslandModule` 接口：

```csharp
using Microsoft.UI.Xaml;
using WinIsland.Core;

public sealed class MyModule : IIslandModule
{
    public string Id => "my-module";
    public string DisplayName => "我的插件";

    public Task InitializeAsync(IDynamicIslandApi api)
    {
        // 初始化逻辑
        return Task.CompletedTask;
    }

    public Task ShutdownAsync() => Task.CompletedTask;
}
```

### 2. 注册插件

在 `App.xaml.cs` 的 `OnLaunched` 中添加：

```csharp
_modules.Add(new MyModule());
```

### 3. 运行

```powershell
dotnet build
dotnet run
```

---

## 核心概念

### 职责划分

| 职责 | 谁负责 |
|------|--------|
| 窗口透明、置顶、无白边 | 主程序 |
| 悬浮检测（进入/离开） | 主程序 |
| 尺寸动画、圆角 | 主程序 |
| 临时消息覆盖/恢复 | 主程序 |
| 空闲态（无内容时的小圆点） | 主程序 |
| **内容（紧凑视图、展开视图）** | **插件** |
| **内容变形动画（元素级 morph）** | **插件** |
| **业务逻辑（何时显示/隐藏）** | **插件** |
| **设置页面** | **插件** |

插件**不调用**任何尺寸/圆角/悬浮相关的方法。主程序根据插件提供的 `IslandLiveContent` 自动处理展开/收起。

---

## 接口参考

### IDynamicIslandApi

插件通过此接口与主程序交互。在 `InitializeAsync` 中传入。

| 成员 | 类型 | 说明 |
|------|------|------|
| `Dispatcher` | `DispatcherQueue` | UI 线程调度器 |
| `Settings` | `ISettingsStore` | 持久化键值存储 |
| `SetLiveContent(ownerId, content)` | `void` | 注册/取消常驻内容 |
| `SendMessage(message)` | `void` | 发送临时文字消息 |
| `ShowContent(content, size, duration)` | `void` | 临时展示任意 XAML 控件 |
| `DismissTemporary()` | `void` | 立即收起临时内容 |
| `AddSettingsPage(page)` | `void` | 注册设置页面 |
| `OpenSettings(pageId)` | `void` | 打开设置窗口 |

### IslandLiveContent

插件通过 `SetLiveContent` 注册的常驻内容描述。

| 属性 | 类型 | 说明 |
|------|------|------|
| `MorphView` | `IMorphView?` | 变形视图（优先使用） |
| `CompactContent` | `UIElement?` | 紧凑态视图（MorphView 为空时使用） |
| `ExpandedContent` | `UIElement?` | 展开态视图（MorphView 为空时使用，为空则不展开） |
| `CompactSize` | `Size` | 紧凑态尺寸（默认 230×40） |
| `ExpandedSize` | `Size` | 展开态尺寸（默认 420×150） |

**两种模式：**
- **MorphView 模式**（推荐）：单个视图承载所有元素，主程序调用 `AnimateToExpanded`/`AnimateToCompact` 让元素流畅变形
- **双视图模式**：紧凑/展开两个独立视图，主程序在悬停时切换（无元素级动画）

### IMorphView

```csharp
public interface IMorphView
{
    UIElement View { get; }
    void AnimateToExpanded(TimeSpan duration);
    void AnimateToCompact(TimeSpan duration);
}
```

主程序在悬停展开/收起时调用，与岛体尺寸动画**同步执行**（相同时长、相同缓动）。插件在这些方法中动画内部元素的 `Width`、`Height`、`FontSize`、`Opacity` 等。

### ISettingsStore

| 成员 | 类型 | 说明 |
|------|------|------|
| `Get<T>(key, default)` | `T` | 读取设置值 |
| `Set<T>(key, value)` | `void` | 写入设置值 |
| `Changed` | `event Action<string>?` | 设置变更事件（参数为键名） |

### IslandMessage

| 属性 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `Title` | `string` | （必填） | 消息标题 |
| `Text` | `string?` | `null` | 消息正文 |
| `Glyph` | `string` | `\uE8BD` | Segoe Fluent Icons 字形 |
| `AccentColor` | `Color?` | 系统蓝 | 图标底色 |
| `Duration` | `TimeSpan` | 4 秒 | 展示时长 |

### SettingsPageDescriptor

| 参数 | 类型 | 说明 |
|------|------|------|
| `id` | `string` | 页面唯一 Id |
| `title` | `string` | 导航栏显示标题 |
| `glyph` | `string` | Segoe Fluent Icons 字形 |
| `factory` | `Func<UIElement>` | 页面内容工厂（每次进入页面时调用） |

---

## 典型模式

### 模式一：常驻内容（MorphView 变形）

```csharp
public async Task InitializeAsync(IDynamicIslandApi api)
{
    _api = api;
    _view = new MyIslandView();

    _content = new IslandLiveContent
    {
        MorphView = _view,                          // 变形视图
        CompactSize = new Size(250, 40),
        ExpandedSize = new Size(420, 158),
    };

    // 业务逻辑触发显示
    OnSomethingHappened += () => _api.SetLiveContent(Id, _content);

    // 业务逻辑触发隐藏
    OnSomethingEnded += () => _api.SetLiveContent(Id, null);
}
```

主程序自动处理：悬浮→展开（调用 `_view.AnimateToExpanded`）、离开→收起（调用 `_view.AnimateToCompact`）、尺寸动画、圆角。

### 模式二：常驻内容（双视图）

```csharp
_content = new IslandLiveContent
{
    CompactContent = new MyCompactView(),
    ExpandedContent = new MyExpandedView(),
    CompactSize = new Size(250, 40),
    ExpandedSize = new Size(420, 150),
};
_api.SetLiveContent(Id, _content);
```

### 模式三：临时消息

```csharp
_api.SendMessage(new IslandMessage
{
    Title = "下载完成",
    Text = "文件已保存到 Downloads 文件夹",
    Glyph = "\uE74C",
    Duration = TimeSpan.FromSeconds(3),
});
```

临时消息会覆盖常驻内容，超时后自动恢复。展示期间悬停事件被抑制。

### 模式四：临时自定义控件

```csharp
var panel = new StackPanel { /* ... */ };
_api.ShowContent(panel, new Size(340, 92), TimeSpan.FromSeconds(5));
```

### 模式五：注册设置页面

```csharp
api.AddSettingsPage(new SettingsPageDescriptor(
    id: "my-module",
    title: "我的插件",
    glyph: "\uE713",
    factory: () => new MySettingsPage(api)));
```

---

## MorphView 实现要点

1. 创建 `UserControl`，实现 `IMorphView`
2. 所有元素（紧凑态 + 展开态）常驻可视树
3. 紧凑态不显示的元素设 `Height=0` + `Opacity=0`
4. `AnimateToExpanded`：动画各元素到展开态值
5. `AnimateToCompact`：动画各元素回紧凑态值
6. 使用 `BackEase` 缓动，与主程序尺寸动画一致

```csharp
public void AnimateToExpanded(TimeSpan duration)
{
    var sb = new Storyboard();
    var easing = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 };

    // 封面放大
    sb.Children.Add(Anim(Cover, "Width", 26, 72, duration, easing));
    sb.Children.Add(Anim(Cover, "Height", 26, 72, duration, easing));

    // 隐藏的元素淡入
    sb.Children.Add(Anim(Details, "Height", 0, 20, duration, easing));
    sb.Children.Add(Anim(Details, "Opacity", 0, 1, duration, easing));

    sb.Begin();
}
```

---

## 线程模型

- `InitializeAsync` 在 UI 线程调用
- 所有 `IDynamicIslandApi` 方法可在任意线程调用（自动编组到 UI 线程）
- 后台线程创建 UIElement 前必须用 `Dispatcher.TryEnqueue` 编组

---

## 生命周期

```
App 启动
  ├─ 创建 IslandWindow（主程序窗口）
  ├─ 创建 IslandService（实现 IDynamicIslandApi）
  ├─ module.InitializeAsync(api)    ← 插件初始化
  │     └─ 注册内容 / 设置页面
  ├─ 运行中…
  │     ├─ 主程序检测悬停 → 自动展开/收起
  │     ├─ 插件调用 SendMessage → 临时覆盖 → 超时恢复
  │     └─ 插件调用 SetLiveContent → 常驻内容更新
  └─ App 退出
       └─ module.ShutdownAsync()    ← 插件清理资源
```

---

## 内置插件

| 插件 | Id | 说明 |
|------|-----|------|
| `GeneralModule` | `general` | 通用设置：显示/隐藏、空闲隐藏、顶部偏移 |
| `MediaModule` | `media` | 正在播放：GSMTC 系统媒体会话，MorphView 变形 |
| `MessageModule` | `messenger` | 发送消息：设置页提供消息发送测试 |

---

## 约定

- **设置键命名**：`<moduleId>.<key>`，如 `media.enabled`、`island.visible`
- **Segoe Fluent Icons**：字形码参考 [Microsoft 文档](https://learn.microsoft.com/windows/apps/design/style/segoe-fluent-icons-font)
- **尺寸单位**：逻辑像素（DIP），主程序自动处理 DPI 缩放
- **动画时长**：展开 333ms，收起 250ms（主程序统一管理）
