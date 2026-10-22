using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace ZulipQQ.Services;

/// <summary>
/// 定时向服务器报告自己的在线状态，并用响应更新其他用户的在线状态。
/// 5 分钟内有键盘或鼠标输入为 active，否则为 idle。
/// </summary>
public sealed class PresenceService(ZulipSession session)
{
    private static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(5);

    private DispatcherTimer? _timer;
    private IDisposable? _pointerHandler;
    private IDisposable? _keyHandler;
    private DateTimeOffset _lastInput = DateTimeOffset.UtcNow;
    private bool _hadInputSinceLastPing = true;
    private long _lastUpdateId = -1;
    private bool _pinging;

    public void Start()
    {
        if (_timer is not null)
            return;

        _pointerHandler = InputElement.PointerMovedEvent.AddClassHandler<Window>(OnInput, RoutingStrategies.Tunnel, handledEventsToo: true);
        _keyHandler = InputElement.KeyDownEvent.AddClassHandler<Window>(OnInput, RoutingStrategies.Tunnel, handledEventsToo: true);

        var interval = session.Realm.PresencePingIntervalSeconds > 0 ? session.Realm.PresencePingIntervalSeconds : 60;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(interval) };
        _timer.Tick += (_, _) => _ = PingAsync();
        _timer.Start();
        _ = PingAsync();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
        _pointerHandler?.Dispose();
        _keyHandler?.Dispose();
        _pointerHandler = null;
        _keyHandler = null;
    }

    private void OnInput(Window window, RoutedEventArgs e)
    {
        _lastInput = DateTimeOffset.UtcNow;
        _hadInputSinceLastPing = true;
    }

    private async Task PingAsync()
    {
        if (_pinging || session.IsDisposed)
            return;
        _pinging = true;
        try
        {
            var status = DateTimeOffset.UtcNow - _lastInput < IdleAfter ? "active" : "idle";
            var newInput = _hadInputSinceLastPing;
            _hadInputSinceLastPing = false;
            var result = await session.Client.UpdatePresenceAsync(status, _lastUpdateId, newInput, session.Token);
            if (session.IsDisposed)
                return;
            if (result.PresenceLastUpdateId is { } id)
                _lastUpdateId = id;
            if (result.Presences is { } presences)
                session.Users.ApplyPresences(presences);
        }
        catch (Exception ex) when (!session.IsDisposed)
        {
            Log.Warn("更新在线状态失败", ex);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _pinging = false;
        }
    }
}
