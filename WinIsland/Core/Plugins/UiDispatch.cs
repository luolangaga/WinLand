using Microsoft.UI.Dispatching;

namespace WinIsland.Core.Plugins;

internal static class UiDispatch
{
    public static void Run(DispatcherQueue dispatcher, Action action)
    {
        if (dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        dispatcher.TryEnqueue(() => action());
    }

    public static Task RunAsync(DispatcherQueue dispatcher, Func<Task> action)
    {
        if (dispatcher.HasThreadAccess)
        {
            return action();
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(async () =>
        {
            try
            {
                await action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }))
        {
            completion.SetException(new InvalidOperationException("UI 线程不可用（应用可能正在退出）。"));
        }

        return completion.Task;
    }

    public static Task<T> Invoke<T>(DispatcherQueue dispatcher, Func<T> func)
    {
        if (dispatcher.HasThreadAccess)
        {
            return Task.FromResult(func());
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(() =>
        {
            try
            {
                completion.SetResult(func());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }))
        {
            completion.SetException(new InvalidOperationException("UI 线程不可用（应用可能正在退出）。"));
        }

        return completion.Task;
    }

    public static Task<T> InvokeAsync<T>(DispatcherQueue dispatcher, Func<Task<T>> func)
    {
        if (dispatcher.HasThreadAccess)
        {
            return func();
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(async () =>
        {
            try
            {
                completion.SetResult(await func());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }))
        {
            completion.SetException(new InvalidOperationException("UI 线程不可用（应用可能正在退出）。"));
        }

        return completion.Task;
    }
}
