using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinIsland.Core;

namespace WinIsland;

/// <summary>
/// 应用入口。WinUI 默认的 Main 由 XAML 编译器生成（见 csproj 里的 DISABLE_XAML_GENERATED_MAIN），
/// 这里换成自己的，有两个目的：
///  1. 把「多开」挡在启动流程前面：两个实例会各建一座岛、各占一个托盘图标、插件目录也会互抢，
///     必须在建岛之前就判断掉；
///  2. 解析命令行 / <c>winisland://</c> 自动化参数：已经在跑就把请求转交过去，否则带着请求启动。
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var request = CommandLine.Parse(args);

        // --help 不需要实例：直接弹原生提示框，谁也不抢
        if (request?.Kind == CommandKind.Help)
        {
            Core.Win32.ShowStartupMessage(CommandLine.HelpText, "WinIsland");
            return;
        }

        var gate = SingleInstance.TryAcquire(request);
        if (gate is null)
        {
            // 已经有实例在跑（闸门已经把 request 交给它了）：带参数时不弹框，避免脚本被模态窗卡住
            SingleInstance.ReportDuplicateLaunch(silent: request != null);
            return;
        }

        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            // 首次启动就带着自动化参数（没有别的实例在跑）：交给 App，等它把岛建好之后再执行
            new App { InstanceGate = gate, StartupCommand = request };
        });

        // Application.Start 返回 = 应用已退出，闸门句柄要活到这一刻
        gate.Dispose();
    }
}
