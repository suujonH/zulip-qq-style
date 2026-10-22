using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using ZulipQQ.Core;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Views;

/// <summary>
/// 独立聊天窗口（经典模式，或设置为在独立窗口中打开聊天）。每个会话最多一个，已打开时激活。
/// 窗口关闭时关闭聊天（保存草稿）。
/// </summary>
public partial class ChatWindow : Window, ISessionWindow
{
    private static readonly List<ChatWindow> OpenWindows = [];

    private ChatWindowViewModel? _vm;
    private MainViewModel? _main;

    public ChatWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 在独立窗口中打开会话。<paramref name="existing"/> 不为 null 时直接使用这个已经打开的聊天
    /// （从主窗口右侧移过来，不重新加载，输入框内容保留）。
    /// </summary>
    public static void Open(MainViewModel main, ConversationKey key, ChatViewModel? existing, long? focusMessageId)
    {
        if (OpenWindows.FirstOrDefault(w => w._vm?.Chat.Key.Equals(key) == true) is { _vm: { } opened } window)
        {
            window.Show();
            WindowPlacement.BringToFront(window);
            if (focusMessageId is { } id)
                _ = opened.Chat.JumpToMessageAsync(id);
            return;
        }

        var chat = existing ?? new ChatViewModel(main.Session, main, key);
        var vm = new ChatWindowViewModel(chat);
        chat.Host = vm;
        chat.IsStandalone = true;
        var created = new ChatWindow { DataContext = vm, _vm = vm, _main = main };
        WindowPlacement.Cascade(created, OpenWindows.LastOrDefault());
        OpenWindows.Add(created);
        created.Show();
        if (existing is null)
            _ = chat.OpenAsync(focusMessageId);
        else if (focusMessageId is { } focus)
            _ = chat.JumpToMessageAsync(focus);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        OpenWindows.Remove(this);
        if (_vm is null)
            return;
        _vm.Close();
        _ = _vm.Chat.CloseAsync();
        _main?.OnChatWindowClosed(_vm.Chat.Key);
    }
}
