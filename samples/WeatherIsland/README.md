# 天气小岛（weather-island）

给 WinIsland 灵动岛用的天气插件：**小岛显示当前温度，展开看三天预报，点一下弹出大卡片看全部细节**。

- 小岛（紧凑态）：天气图标 + 当前温度 + 天气描述
- 大岛（展开态）：地点与更新时间 + 今天 / 明天 / 后天三天的最高最低温
- **聚光卡（点击岛体）**：实况细节 + 未来 24 小时逐时 + 未来 7 天预报 + 空气质量

## 聚光卡里有什么

| 区块 | 内容 |
|------|------|
| 头部 | 大图标、当前温度、天气描述（夜间会标注）、地点、更新时间、今日温区、日出 / 日落 / 白昼时长 |
| 实况指标（8 项） | 体感温度（含今日体感区间）、相对湿度（含露点）、风（风向 + 风速 + 阵风）、降水（概率 + 当前 + 今日累计）、紫外线（指数 + 等级 + 防护建议）、云量与能见度、气压、空气质量（PM2.5 / PM10 / 美标 AQI） |
| 未来 24 小时 | 逐小时的时间、天气图标、温度、降水概率；悬停看该小时的风速与天气描述 |
| 未来 7 天 | 每天的天气图标、降水概率、温度区间条（跨天共用一把尺子）、最低 ~ 最高温；悬停看日出日落、紫外线、最大风速、累计降水 |

按 `Esc` 或点卡片外区域收起。卡片里的「立即刷新」**会先提交设置页里还没保存的城市**再取数。

## 实现约束

**形态动画必须逐帧直接赋值，不要用 `Storyboard`**。属性路径动画在动态加载的插件程序集里会抛
`COMException (0x800F1001) Invalid attribute value Unknown for property Height`；
异常在动画 tick 里抛出，调用点的 try/catch 拦不住，会冒到宿主的未处理异常处理器并打断状态机
（表现为大岛变小之后卡死）。宿主内置视图能安全用 Storyboard，因为它们在宿主程序集里。
曲线与宿主保持一致：`BackEase(EaseOut, Amplitude: 0.45)`。

配色跟着**岛体**主题走（`IIslandTheme`），不是系统主题 —— Apple 外观恒为深色胶囊。
中性色做成共享 `SolidColorBrush`，`theme.Changed` 时只改它们的 `Color`，
所有用到的地方当场跟着变，不用遍历可视树。

聚光卡必须是**独立于岛视图的另一棵可视树**（每个窗口一棵树，共用同一个 `UIElement` 会白屏）。

## 数据来源

[Open-Meteo](https://open-meteo.com/)：免费、不需要注册、不用填 API Key。
天气走 `api.open-meteo.com`，空气质量走 `air-quality-api.open-meteo.com`
（拿不到只影响那一格显示 `--`，不会拖垮主数据）。
插件只把你填的城市名 / 坐标发给它，不发送任何其他信息。网络不通时继续显示上一次拿到的数据。

## 设置项

| 设置 | 说明 |
|------|------|
| 在灵动岛上显示 | 关掉后不占小岛，天气仍在后台刷新 |
| 城市 | 中文城市名（如「杭州」）自动查经纬度；也支持直接填「30.25,120.17」 |
| 刷新间隔 | 10 / 15 / 30 / 60 / 120 分钟 |

设置键都会自动带上 `weather-island.` 前缀（如 `weather-island.city`）。

## 宿主版本要求

`plugin.json` 的 `min_host_version` 是 `2.3.0`：聚光卡需要 2.1.0，岛体主题（`Context.Theme`）需要 2.3.0。

## 图标

Segoe Fluent Icons 与 Segoe MDL2 Assets 里都没有可用的天气字形（E9C4 附近的天气位是空的），
所以图标是按 24×24 设计网格用形状自绘的，见 `WeatherIcon.cs`。

## 开发

工程放在 WinIsland 源码仓库的 `samples\` 下，`dotnet build` 会自动把插件拷到
`WinIsland\bin\<配置>\net10.0-windows10.0.26100.0\win-x64\plugins\weather-island\`。

项目根目录的 `global.json` 钉住 .NET 10（宿主也是 .NET 10 构建的，用更高的 SDK 编译出的插件在正式版宿主上会加载失败），**不要删**。构建前先 `cd` 进项目目录 —— `global.json` 只按当前目录生效。

```powershell
cd samples\WeatherIsland
dotnet build                 # 编译并部署到宿主
powershell tools\pack-plugin.ps1 -ProjectDir samples\WeatherIsland -Configuration Release
```

日志：`%LocalAppData%\WinIsland\logs\plugin.weather-island.log`

## 许可

MIT
