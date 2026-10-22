using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using ZulipQQ.Core;
using ZulipQQ.Models;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Services;

/// <summary>打开中的聊天（主窗口右侧与独立窗口）。只在 UI 线程上使用。</summary>
public sealed class ChatRegistry(ZulipSession session)
{
    private readonly List<ChatViewModel> _open = [];

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
        if (m.SenderId == session.SelfId || m.IsRead || !chats.Exists(vm => vm.ShowsLatest && IsInActiveWindow(vm)))
            return;
        session.Forget(session.Chat.MarkReadAsync([m.Id]), "标记已读");
    }

    private static bool IsInActiveWindow(ChatViewModel vm) =>
        vm.HostWindow is { IsActive: true, IsVisible: true } window && window.WindowState != WindowState.Minimized;

    /// <summary>
    /// 会话已在独立窗口中打开时激活该窗口并返回 true。独立聊天窗口在经典模式分支实现，目前总是返回 false。
    /// </summary>
    public bool Activate(ConversationKey key, long? focusMessageId) => false;

    /// <summary>话题被整体移动或改名：打开中的聊天换成新键并重新加载。</summary>
    public void Rekey(ConversationKey oldKey, ConversationKey newKey)
    {
        foreach (var vm in _open.Where(vm => vm.Key.Equals(oldKey)).ToList())
            vm.Rekey(newKey);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
