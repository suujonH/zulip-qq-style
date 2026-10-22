using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using ZulipQQ.Core;

namespace ZulipQQ.Services;

/// <summary>
/// 按 Zulip 协议发送“正在输入”：开始输入时发 start，持续输入时每隔 started_wait 毫秒再发一次 start；
/// 停止输入 stopped_wait 毫秒、清空输入框、发送消息或关闭聊天时发 stop。
/// 关闭了对应的 send_*_typing_notifications 设置时、以及与自己的私聊不发送。只在 UI 线程上使用。
/// </summary>
public sealed class TypingNotifier
{
    private readonly ZulipSession _session;
    private readonly DispatcherTimer _idleTimer = new();

    // 已发出 start、尚未发出 stop 的会话
    private ConversationKey? _typingTo;
    private DateTime _lastStartSent;

    public TypingNotifier(ZulipSession session)
    {
        _session = session;
        _idleTimer.Tick += (_, _) => Stop();
    }

    /// <summary>输入框的文字被用户修改。</summary>
    public void OnInput(ConversationKey key, bool hasText)
    {
        if (!hasText || !IsEnabled(key))
        {
            Stop();
            return;
        }
        if (_typingTo is not null && !_typingTo.Equals(key))
            Stop();

        var now = DateTime.UtcNow;
        if (_typingTo is null || now - _lastStartSent >= TimeSpan.FromMilliseconds(_session.Realm.TypingStartedWaitMs))
        {
            _typingTo = key;
            _lastStartSent = now;
            Send(key, "start");
        }
        _idleTimer.Stop();
        _idleTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1000, _session.Realm.TypingStoppedWaitMs));
        _idleTimer.Start();
    }

    /// <summary>停止输入：之前发过 start 时发送 stop。</summary>
    public void Stop()
    {
        _idleTimer.Stop();
        if (_typingTo is not { } key)
            return;
        _typingTo = null;
        Send(key, "stop");
    }

    private bool IsEnabled(ConversationKey key) => key switch
    {
        TopicKey => _session.Settings.Zulip.SendStreamTypingNotifications,
        DmKey { IsSelfDm: false } => _session.Settings.Zulip.SendPrivateTypingNotifications,
        _ => false,
    };

    private void Send(ConversationKey key, string op)
    {
        if (_session.IsDisposed)
            return;
        var task = key switch
        {
            TopicKey t => _session.Client.SendTypingAsync(op, t.StreamId, _session.Conversations.GetTopicName(t), null, _session.Token),
            DmKey d => _session.Client.SendTypingAsync(op, null, null, [.. d.OtherUserIds], _session.Token),
            _ => Task.CompletedTask,
        };
        _session.Forget(task, "发送正在输入状态");
    }
}
