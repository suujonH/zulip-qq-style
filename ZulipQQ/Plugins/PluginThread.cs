using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace ZulipQQ.Plugins;

/// <summary>
/// 插件线程：所有插件导出函数都在这个后台线程上按顺序调用。进程退出时随之结束，不需要 Join。
/// </summary>
public sealed class PluginThread
{
    private readonly BlockingCollection<Action> _queue = [];

    public PluginThread()
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "ZulipQQ.Plugins" };
        thread.Start();
        ManagedThreadId = thread.ManagedThreadId;
    }

    public int ManagedThreadId { get; }

    /// <summary>当前是否在插件线程上。</summary>
    public bool IsCurrent => Environment.CurrentManagedThreadId == ManagedThreadId;

    /// <summary>投递一个调用；返回的任务在调用结束后完成（异常通过任务传递）。线程已停止时任务以取消结束。</summary>
    public Task<T> RunAsync<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _queue.Add(() =>
            {
                try
                {
                    tcs.TrySetResult(f());
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
        }
        catch (InvalidOperationException)
        {
            tcs.TrySetCanceled();
        }
        return tcs.Task;
    }

    public Task RunAsync(Action a) => RunAsync(() =>
    {
        a();
        return true;
    });

    /// <summary>不再接受新的调用（已排队的调用继续执行）。</summary>
    public void Complete() => _queue.CompleteAdding();

    private void Run()
    {
        foreach (var action in _queue.GetConsumingEnumerable())
            action();
    }
}
