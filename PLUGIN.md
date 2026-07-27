# WinIsland 插件开发指南

WinIsland 支持动态加载外部 DLL 插件。主程序负责窗口管理（透明、置顶、无白边）、悬浮检测、尺寸动画、临时消息覆盖/恢复、设置窗口外壳。插件只提供内容——不接触尺寸、圆角、悬浮事件。

## 快速开始

### 1. 创建插件项目

```powershell
dotnet new classlib -n MyIslandPlugin -o MyIslandPlugin
```

编辑 `MyIslandPlugin.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
    <TargetPlatformMinVersion>10.0.17763.0</TargetPlatformMinVersion>
    <UseWinUI>true</UseWinUI>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <WindowsPackageType>None</WindowsPackageType>
    <EnableMsixTooling>false</EnableMsixTooling>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Windows.SDK.BuildTools" Version="10.0.28000.2526" />
    <PackageReference Include="Microsoft.WindowsAppSDK" Version="2.3.1" />
    <!-- 引用 WinIsland.Core SDK DLL -->
    <Reference Include="WinIsland.Core">
      <HintPath>..\path\to\WinIsland.Core.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

### 2. 实现插件

**方式 A：使用 `IslandPluginAttribute`（推荐）**

```csharp
using WinIsland.Core;

namespace MyIslandPlugin;

[IslandPlugin("my-plugin", "我的插件", Description = "一个示例插件", Author = "作者", Version = "1.0.0")]
public sealed class MyPlugin : IslandPluginBase
{
    public override string Id => "my-plugin";
    public override string DisplayName => "我的插件";
    public override string? Description => "一个示例插件";
    public override string? Author => "作者";
    public override string? Version => "1.0.0";

    public override Task InitializeAsync(IDynamicIslandApi api)
    {
        Api = api; // IslandPluginBase 自动设置

        // 注册设置页面
        Api.AddSettingsPage(new SettingsPageDescriptor(
            "my-plugin", DisplayName, "\uE713", () => new MySettingsPage(Api)));

        // 注册常驻内容
        var view = new MyIslandView();
        Api.SetLiveContent(Id, new IslandLiveContent
        {
            MorphView = view,
            CompactSize = new Size(250, 40),
            ExpandedSize = new Size(420, 158),
        });

        return Task.CompletedTask;
    }

    public override Task ShutdownAsync()
    {
        Api.SetLiveContent(Id, null);
        return Task.CompletedTask;
    }
}
```

**方式 B：直接实现 `IIslandModule`**

```csharp
using WinIsland.Core;

namespace MyIslandPlugin;

public sealed class MyPlugin : IIslandModule
{
    public string Id => "my-plugin";
    public string DisplayName => "我的插件";

    private IDynamicIslandApi _api = null!;

    public Task InitializeAsync(IDynamicIslandApi api)
    {
        _api = api;
        return Task.CompletedTask;
    }

    public Task ShutdownAsync() => Task.CompletedTask;
}
```

### 3. 编译 & 部署

```powershell
dotnet build -c Release
```

将输出的 `MyIslandPlugin.dll` 复制到 WinIsland 的 `plugins/` 目录：

```
WinIsland/
  WinIsland.exe
  WinIsland.Core.dll
  plugins/
    MyIslandPlugin.dll    ← 放这里
```

或者在设置窗口的「插件管理」页面点击「浏览 DLL...」直接加载。

---

## 插件管理器

主程序内置插件管理器（设置 → 插件管理），支持：

| 功能 | 说明 |
|------|------|
| 浏览加载 DLL | 选择外部 DLL 文件，自动复制到 plugins/ 目录并加载 |
| 启用/禁用 | 每个插件有独立开关，禁用后调用 `ShutdownAsync` 并清除内容 |
| 卸载 | 移除外部插件（内置插件不可卸载） |
| 打开插件目录 | 快速打开 plugins/ 文件夹 |
| 状态显示 | 运行中 / 已禁用 / 错误（含错误信息） |

禁用状态持久化到设置：`plugin.<moduleId>.disabled = true`

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

| 属性 | 类型 | 说明 |
|------|------|------|
| `MorphView` | `IMorphView?` | 变形视图（优先使用） |
| `CompactContent` | `UIElement?` | 紧凑态视图 |
| `ExpandedContent` | `UIElement?` | 展开态视图 |
| `CompactSize` | `Size` | 紧凑态尺寸（默认 230×40） |
| `ExpandedSize` | `Size` | 展开态尺寸（默认 420×150） |

### IMorphView

```csharp
public interface IMorphView
{
    UIElement View { get; }
    void AnimateToExpanded(TimeSpan duration);
    void AnimateToCompact(TimeSpan duration);
}
```

### ISettingsStore

| 成员 | 类型 | 说明 |
|------|------|------|
| `Get<T>(key, default)` | `T` | 读取设置值 |
| `Set<T>(key, value)` | `void` | 写入设置值 |
| `Changed` | `event Action<string>?` | 设置变更事件 |

### IslandPluginAttribute

```csharp
[IslandPlugin("id", "显示名称", Description = "...", Author = "...", Version = "1.0.0", IconGlyph = "\uE712")]
```

标记在插件类上，主程序自动读取元数据。不标记也能工作（使用 `IIslandModule.Id` / `DisplayName`）。

### IslandPluginBase

便捷基类，提供 `Api`、`Settings`、`Log` 等属性，减少样板代码。

### IPluginManifest / PluginInfo

| 属性 | 说明 |
|------|------|
| `Id` | 唯一标识 |
| `DisplayName` | 显示名称 |
| `Description` | 描述（可选） |
| `Author` | 作者（可选） |
| `Version` | 版本号（可选） |
| `IconGlyph` | Segoe Fluent Icons 字形（可选） |
| `IsBuiltIn` | 是否内置模块 |
| `State` | 插件状态（Discovered/Loaded/Initialized/Disabled/Error） |

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
| `factory` | `Func<UIElement>` | 页面内容工厂 |

---

## 典型模式

### 模式一：常驻内容（MorphView 变形）

```csharp
public override Task InitializeAsync(IDynamicIslandApi api)
{
    var view = new MyIslandView();
    Api.SetLiveContent(Id, new IslandLiveContent
    {
        MorphView = view,
        CompactSize = new Size(250, 40),
        ExpandedSize = new Size(420, 158),
    });
    return Task.CompletedTask;
}
```

### 模式二：常驻内容（双视图）

```csharp
Api.SetLiveContent(Id, new IslandLiveContent
{
    CompactContent = new MyCompactView(),
    ExpandedContent = new MyExpandedView(),
    CompactSize = new Size(250, 40),
    ExpandedSize = new Size(420, 150),
});
```

### 模式三：临时消息

```csharp
Api.SendMessage(new IslandMessage
{
    Title = "下载完成",
    Text = "文件已保存到 Downloads 文件夹",
    Glyph = "\uE74C",
    Duration = TimeSpan.FromSeconds(3),
});
```

### 模式四：临时自定义控件

```csharp
var panel = new StackPanel { /* ... */ };
Api.ShowContent(panel, new Size(340, 92), TimeSpan.FromSeconds(5));
```

### 模式五：注册设置页面

```csharp
Api.AddSettingsPage(new SettingsPageDescriptor(
    "my-plugin", "我的插件", "\uE713", () => new MySettingsPage(Api)));
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
    sb.Children.Add(Anim(Cover, "Width", 26, 72, duration, easing));
    sb.Children.Add(Anim(Cover, "Height", 26, 72, duration, easing));
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
  ├─ 创建 IslandWindow
  ├─ 创建 IslandService (IDynamicIslandApi)
  ├─ PluginLoader.RegisterBuiltIn(...)     ← 注册内置模块
  ├─ PluginLoader.LoadBuiltInModulesAsync() ← 初始化内置模块
  ├─ PluginLoader.DiscoverAndLoadAsync()    ← 扫描 plugins/ 目录加载外部 DLL
  │     ├─ 发现 IIslandModule 实现
  │     ├─ 读取 IslandPluginAttribute 元数据
  │     └─ 检查 plugin.<id>.disabled → 跳过或初始化
  ├─ 运行中…
  │     ├─ 用户在插件管理器中启用/禁用插件
  │     ├─ 用户浏览加载新 DLL
  │     └─ 插件调用 API 方法
  └─ App 退出
       └─ PluginLoader.ShutdownAllAsync()   ← 所有模块 ShutdownAsync
```

---

## 内置插件

| 插件 | Id | 说明 |
|------|-----|------|
| `GeneralModule` | `general` | 通用设置：显示/隐藏、空闲隐藏、顶部偏移 |
| `MediaModule` | `media` | 正在播放：GSMTC 系统媒体会话，MorphView 变形 |
| `MessageModule` | `messenger` | 发送消息：设置页提供消息发送测试 |
| `PluginManager` | `plugins` | 插件管理：加载/禁用/卸载插件 |

---

## 约定

- **设置键命名**：`<moduleId>.<key>`，如 `media.enabled`、`island.visible`
- **插件禁用键**：`plugin.<moduleId>.disabled`
- **Segoe Fluent Icons**：字形码参考 [Microsoft 文档](https://learn.microsoft.com/windows/apps/design/style/segoe-fluent-icons-font)
- **尺寸单位**：逻辑像素（DIP），主程序自动处理 DPI 缩放
- **动画时长**：展开 333ms，收起 250ms（主程序统一管理）
- **DLL 放置**：`plugins/` 目录，主程序启动时自动扫描
