using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using ZulipQQ.Core;
using ZulipQQ.Models;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Services;

/// <summary>打开中的聊天（主窗口右侧与独立窗口）。只在 UI 线程上使用。</summary>
public sealed class ChatRegistry(ZulipSession session)
{
    private readonly List<ChatViewModel> _open = [];
    private readonly List<Task> _closing = [];

    /// <summary>打开或关闭了聊天（会话列表据此决定是否显示草稿）。</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<ChatViewModel> All => _open;

    public void Register(ChatViewModel vm)
    {
        if (_open.Contains(vm))
            return;
        _open.Add(vm);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>记录关闭中的聊天保存草稿的任务；结束会话前由 <see cref="WaitClosingAsync"/> 等待，避免草稿保存被取消。</summary>
    public void TrackClosing(Task task)
    {
        _closing.RemoveAll(t => t.IsCompleted);
        if (!task.IsCompleted)
            _closing.Add(task);
    }

    /// <summary>等待所有关闭中的聊天保存完草稿（失败已由保存方记录日志）。</summary>
    public async Task WaitClosingAsync()
    {
        var tasks = _closing.ToList();
        _closing.Clear();
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            Log.Warn("关闭聊天时保存草稿失败", ex);
        }
    }

    public void Unregister(ChatViewModel vm)
    {
        if (_open.Remove(vm))
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool IsOpen(ConversationKey key) => _open.Exists(vm => vm.Key.Equals(key));

    /// <summary>该会话打开在一个可见、未最小化且处于前台的窗口中。</summary>
    public bool IsActivelyViewing(ConversationKey key) => _open.Exists(vm => vm.Key.Equals(key) && IsInActiveWindow(vm));

    /// <summary>
    /// 会话收到新消息：通知打开中的聊天；别人的消息出现在正在查看的列表末尾时立即标记已读
    /// （正在查看较早的历史时消息不显示，保持未读）。
    /// </summary>
    public void OnNewMessage(ChatMessage m)
    {
        var chats = _open.Where(vm => vm.Key.Equals(m.Key)).ToList();
        foreach (var vm in chats)
            vm.OnIncomingMessage(m);
        if (m.SenderId == session.SelfId || m.IsRead || !chats.Exists(vm => vm.ShowsLatest && vm.AutoMarksRead && IsInActiveWindow(vm)))
            return;
        session.Forget(session.Chat.MarkReadAsync([m.Id]), "标记已读");
    }

    /// <summary>
    /// typing 事件：通知该会话打开中的聊天（标题下方显示“正在输入”）。自己和被屏蔽的用户的输入状态忽略。
    /// </summary>
    public void OnTyping(JsonElement ev)
    {
        var senderId = ev.GetProperty("sender").GetProperty("user_id").GetInt64();
        if (senderId == session.SelfId || session.Users.IsMuted(senderId))
            return;
        ConversationKey key;
        if (ev.TryGetProperty("stream_id", out var streamId))
            key = new TopicKey(streamId.GetInt64(), ev.TryGetProperty("topic", out var topic) ? topic.GetString() ?? "" : "");
        else if (ev.TryGetProperty("recipients", out var recipients))
            key = DmKey.Create(recipients.EnumerateArray().Select(r => r.GetProperty("user_id").GetInt64()), session.SelfId);
        else
            return;

        var started = ev.GetProperty("op").GetString() == "start";
        foreach (var vm in _open.Where(vm => vm.Key.Equals(key)).ToList())
            vm.OnTyping(senderId, started);
    }

    private static bool IsInActiveWindow(ChatViewModel vm) =>
        vm.HostWindow is { IsActive: true, IsVisible: true } window && window.WindowState != WindowState.Minimized
        && !EdgeHideController.IsHiddenAtEdge(window);

    /// <summary>缩进屏幕边缘的窗口移回：其中的聊天与窗口被激活时一样补标已读。</summary>
    public void OnWindowRevealed(Window window)
    {
        foreach (var vm in _open.Where(vm => vm.HostWindow == window).ToList())
            vm.OnHostActivated();
    }

    /// <summary>
    /// 会话已在独立窗口中打开时还原并激活该窗口（有 <paramref name="focusMessageId"/> 时跳转到该消息），返回 true。
    /// </summary>
    public bool Activate(ConversationKey key, long? focusMessageId)
    {
        if (_open.FirstOrDefault(vm => vm.IsStandalone && vm.Key.Equals(key)) is not { } chat)
            return false;
        if (chat.HostWindow is { } window)
        {
            window.Show();
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;
            window.Activate();
        }
        if (focusMessageId is { } id)
            session.Forget(chat.JumpToMessageAsync(id), "跳转到消息");
        return true;
    }

    /// <summary>关闭该会话的独立聊天窗口（例如话题被删除）。</summary>
    public void CloseWindows(ConversationKey key)
    {
        foreach (var chat in _open.Where(vm => vm.IsStandalone && vm.Key.Equals(key)).ToList())
            chat.HostWindow?.Close();
    }

    /// <summary>话题被整体移动或改名：打开中的聊天换成新键并重新加载。</summary>
    public void Rekey(ConversationKey oldKey, ConversationKey newKey)
    {
        foreach (var vm in _open.Where(vm => vm.Key.Equals(oldKey)).ToList())
            vm.Rekey(newKey);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
