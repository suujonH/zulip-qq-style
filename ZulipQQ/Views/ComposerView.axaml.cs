using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ZulipQQ.Core;
using ZulipQQ.Services;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Views;

/// <summary>
/// 输入区：发送快捷键（Enter / Ctrl+Enter，跟随 Zulip 的 enter_sends）、@ 补全的键盘操作、
/// 粘贴图片 / 文件、表情插入。
/// </summary>
public partial class ComposerView : UserControl
{
    private ComposerViewModel? _vm;

    // Flyout 不在名称范围中，不能用 x:Name 取得
    private Flyout EmojiFlyout => (Flyout)EmojiButton.Flyout!;

    public ComposerView()
    {
        InitializeComponent();
        InputBox.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        InputBox.PropertyChanged += OnInputPropertyChanged;
        InputBox.LostFocus += (_, _) => _vm?.Mentions.Close();
        // 隧道阶段处理：替换输入框自带的剪切 / 复制 / 粘贴菜单
        InputBox.AddHandler(ContextRequestedEvent, OnInputContextRequested, RoutingStrategies.Tunnel);
        MentionList.AddHandler(PointerReleasedEvent, OnMentionClicked, RoutingStrategies.Bubble, handledEventsToo: true);
        EmojiFlyout.Opening += OnEmojiFlyoutOpening;
        FileButton.Click += async (_, _) =>
        {
            if (_vm is not null && TopLevel.GetTopLevel(this) is { } top)
                await _vm.PickFileAsync(top);
        };
        ImageButton.Click += async (_, _) =>
        {
            if (_vm is not null && TopLevel.GetTopLevel(this) is { } top)
                await _vm.PickImagesAsync(top);
        };
        HistoryButton.Click += (_, _) =>
        {
            if (_vm is not null)
                HistoryWindow.Show(TopLevel.GetTopLevel(this) as Window, _vm.Chat.Main, _vm.Chat.Key);
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
            _vm.FocusRequested -= OnFocusRequested;
        _vm = DataContext as ComposerViewModel;
        if (_vm is not null)
            _vm.FocusRequested += OnFocusRequested;
    }

    /// <summary>把焦点放到输入框末尾。</summary>
    public void FocusInput()
    {
        InputBox.Focus();
        InputBox.CaretIndex = InputBox.Text?.Length ?? 0;
    }

    private void OnFocusRequested(object? sender, EventArgs e) => Dispatcher.UIThread.Post(FocusInput);

    private void OnInputPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_vm is not null && (e.Property == TextBox.TextProperty || e.Property == TextBox.CaretIndexProperty)
            && InputBox.IsFocused)
            _vm.Mentions.Update(InputBox.Text ?? "", InputBox.CaretIndex);
    }

    // 处理完后用 Post 执行命令：发送会清空文字，不能在同一次按键事件中修改 TextBox
    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null)
            return;
        var mentions = _vm.Mentions;
        if (mentions.IsOpen)
        {
            switch (e.Key)
            {
                case Key.Up:
                    mentions.Move(-1);
                    e.Handled = true;
                    return;
                case Key.Down:
                    mentions.Move(1);
                    e.Handled = true;
                    return;
                case Key.Enter or Key.Tab when e.KeyModifiers == KeyModifiers.None:
                    AcceptMention(null);
                    e.Handled = true;
                    return;
                case Key.Escape:
                    mentions.Close();
                    e.Handled = true;
                    return;
            }
        }

        if (e.Key == Key.V && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            PasteAsync();
            return;
        }
        if (e.Key != Key.Enter)
            return;

        var send = e.KeyModifiers switch
        {
            KeyModifiers.None => _vm.EnterSends,
            KeyModifiers.Control => !_vm.EnterSends,
            _ => false,
        };
        if (send)
        {
            e.Handled = true;
            Dispatcher.UIThread.Post(() =>
            {
                if (_vm?.SendCommand.CanExecute(null) == true)
                    _vm.SendCommand.Execute(null);
            });
        }
        else if (e.KeyModifiers == KeyModifiers.Control)
        {
            // Enter 发送时 Ctrl+Enter 换行（TextBox 默认不处理 Ctrl+Enter）
            e.Handled = true;
            InsertText("\n");
        }
    }

    private void OnMentionClicked(object? sender, PointerReleasedEventArgs e)
    {
        if ((e.Source as StyledElement)?.DataContext is MentionCandidate candidate)
            AcceptMention(candidate);
    }

    private void AcceptMention(MentionCandidate? candidate)
    {
        if (_vm?.Mentions.Accept(InputBox.Text ?? "", InputBox.CaretIndex, candidate) is not { } result)
            return;
        InputBox.Text = result.Text;
        InputBox.CaretIndex = result.Caret;
        InputBox.Focus();
    }

    // 粘贴顺序：文件（资源管理器中复制的）> 位图（截图）> 文字（TextBox 默认处理）
    private async void PasteAsync()
    {
        if (_vm is null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        try
        {
            if (await clipboard.TryGetFilesAsync() is { Length: > 0 } files)
            {
                _vm.AddFiles(files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList());
                return;
            }
            if (await clipboard.TryGetBitmapAsync() is { } bitmap)
            {
                _vm.AddBitmap(bitmap);
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("读取剪贴板失败", ex);
        }
        InputBox.Paste();
    }

    /// <summary>输入框右键菜单：剪切、复制、粘贴、全选，以及“常用语 ▸”（插入、新建、删除）。</summary>
    private void OnInputContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_vm is not { } vm)
            return;
        e.Handled = true;
        var hasText = !string.IsNullOrEmpty(InputBox.Text);
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("剪切", () => InputBox.Cut(), InputBox.CanCut));
        menu.Items.Add(MenuItem("复制", () => InputBox.Copy(), InputBox.CanCopy));
        menu.Items.Add(MenuItem("粘贴", PasteAsync, vm.CanEdit));
        menu.Items.Add(MenuItem("全选", () => InputBox.SelectAll(), hasText));
        menu.Items.Add(new Separator());

        var snippets = new MenuItem { Header = "常用语" };
        foreach (var snippet in vm.Snippets)
        {
            var item = MenuItem(snippet.Title, () =>
            {
                InsertText(snippet.Content);
                InputBox.Focus();
            }, vm.CanEdit);
            ToolTip.SetTip(item, snippet.Content);
            snippets.Items.Add(item);
        }
        if (vm.Snippets.Count > 0)
            snippets.Items.Add(new Separator());
        snippets.Items.Add(MenuItem("新建常用语…", vm.ShowNewSnippetDialog, !string.IsNullOrWhiteSpace(InputBox.Text)));
        if (vm.Snippets.Count > 0)
        {
            var remove = new MenuItem { Header = "删除常用语" };
            foreach (var snippet in vm.Snippets)
                remove.Items.Add(MenuItem(snippet.Title, () => _ = vm.DeleteSnippetAsync(snippet), true));
            snippets.Items.Add(remove);
        }
        menu.Items.Add(snippets);
        menu.Open(InputBox);
    }

    private static MenuItem MenuItem(string header, Action action, bool enabled)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    private void OnEmojiFlyoutOpening(object? sender, EventArgs e)
    {
        if (_vm is null)
            return;
        var picker = new EmojiPickerViewModel(_vm.Emoji);
        picker.Picked += (_, emoji) =>
        {
            EmojiFlyout.Hide();
            InsertText($":{emoji.Name}: ");
            InputBox.Focus();
        };
        EmojiFlyout.Content = new EmojiPickerView { DataContext = picker };
    }

    /// <summary>在光标处插入文字（替换选中部分）。</summary>
    private void InsertText(string s)
    {
        var text = InputBox.Text ?? "";
        var start = Math.Clamp(Math.Min(InputBox.SelectionStart, InputBox.SelectionEnd), 0, text.Length);
        var end = Math.Clamp(Math.Max(InputBox.SelectionStart, InputBox.SelectionEnd), 0, text.Length);
        InputBox.Text = string.Concat(text.AsSpan(0, start), s, text.AsSpan(end));
        InputBox.CaretIndex = start + s.Length;
    }
}
