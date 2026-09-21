# 天气小岛（weather-island）

给 WinIsland 灵动岛用的天气插件：**小岛显示当前温度，展开看三天预报**。

- 小岛（紧凑态）：天气图标 + 当前温度 + 天气描述
- 大岛（展开态）：地点与更新时间 + 今天 / 明天 / 后天三天的最高最低温
- 点击小岛：弹一条当天详细消息（未取到数据时会顺手刷新一次）

## 实现约束

**形态动画必须逐帧直接赋值，不要用 `Storyboard`**。属性路径动画在动态加载的插件程序集里会抛
`COMException (0x800F1001) Invalid attribute value Unknown for property Height`；
异常在动画 tick 里抛出，调用点的 try/catch 拦不住，会冒到宿主的未处理异常处理器并打断状态机
（表现为大岛变小之后卡死）。宿主内置视图能安全用 Storyboard，因为它们在宿主程序集里。
曲线与宿主保持一致：`BackEase(EaseOut, Amplitude: 0.45)`。

## 数据来源

[Open-Meteo](https://open-meteo.com/)：免费、不需要注册、不用填 API Key。
插件只把你填的城市名（或坐标）发给它，不发送任何其他信息。网络不通时继续显示上一次拿到的数据。

## 设置项

| 设置 | 说明 |
|------|------|
| 在灵动岛上显示 | 关掉后不占小岛，天气仍在后台刷新 |
| 城市 | 中文城市名（如「杭州」）自动查经纬度；也支持直接填「30.25,120.17」 |
| 刷新间隔 | 10 / 15 / 30 / 60 / 120 分钟 |

设置键都会自动带上 `weather-island.` 前缀（如 `weather-island.city`）。

## 图标

Segoe Fluent Icons 与 Segoe MDL2 Assets 里都没有可用的天气字形（E9C4 附近的天气位是空的），
所以图标是按 24×24 设计网格用形状自绘的，见 `WeatherIcon.cs`。

## 开发

工程放在 WinIsland 源码仓库的 `samples\` 下，`dotnet build` 会自动把插件拷到
`WinIsland\bin\<配置>\net10.0-windows10.0.26100.0\win-x64\plugins\weather-island\`。

```powershell
dotnet build                 # 编译并部署到宿主
pwsh tools/pack-plugin.ps1 -ProjectDir samples\WeatherIsland -Configuration Release
```

日志：`%LocalAppData%\WinIsland\logs\plugin.weather-island.log`

## 许可

MIT
