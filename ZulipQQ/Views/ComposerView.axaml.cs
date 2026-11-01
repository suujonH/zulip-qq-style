using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ZulipQQ.Controls;
using ZulipQQ.Core;
using ZulipQQ.Services;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Views;

/// <summary>
/// 输入区：发送快捷键（Enter / Ctrl+Enter，跟随 Zulip 的 enter_sends）、@ 补全的键盘操作、
/// 代码块语言补全、粘贴图片 / 文件、表情插入、格式按钮与快捷键、编辑消息时 Esc 取消。
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
        InputBox.TextChanged += (_, _) =>
        {
            UpdateCompletions();
            UpdatePlaceholder();
            UpdateFormatBar();
        };
        InputBox.CaretMoved += (_, _) =>
        {
            UpdateCompletions();
            UpdateFormatBar();
        };
        InputBox.PreeditChanged += (_, _) => UpdatePlaceholder();
        InputBox.LostFocus += (_, _) =>
        {
            _vm?.Mentions.Close();
            _vm?.Languages.Close();
        };
        // 隧道阶段处理：替换输入框自带的剪切 / 复制 / 粘贴菜单
        InputBox.AddHandler(ContextRequestedEvent, OnInputContextRequested, RoutingStrategies.Tunnel);
        MentionList.AddHandler(PointerReleasedEvent, OnMentionClicked, RoutingStrategies.Bubble, handledEventsToo: true);
        LanguageList.AddHandler(PointerReleasedEvent, OnLanguageClicked, RoutingStrategies.Bubble, handledEventsToo: true);
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
        foreach (var button in FormatBar.Children.OfType<Button>())
            button.Click += OnFormatClick;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.FocusRequested -= OnFocusRequested;
            _vm.InsertRequested -= OnInsertRequested;
            _vm.InsertBlockRequested -= OnInsertBlockRequested;
            _vm.TextReset -= OnTextReset;
        }
        _vm = DataContext as ComposerViewModel;
        if (_vm is not null)
        {
            _vm.FocusRequested += OnFocusRequested;
            _vm.InsertRequested += OnInsertRequested;
            _vm.InsertBlockRequested += OnInsertBlockRequested;
            _vm.TextReset += OnTextReset;
            var session = _vm.Chat.Session;
            InputBox.EmojiLookup = name => session.Emoji.Catalog.TryGet(name);
            InputBox.Resolver = new ZulipMarkdownResolver(session);
            // 绑定把草稿放进输入框后再清空撤销记录
            Dispatcher.UIThread.Post(InputBox.ResetHistory);
        }
        UpdatePlaceholder();
    }

    /// <summary>把焦点放到输入框末尾。</summary>
    public void FocusInput()
    {
        InputBox.TextArea.Focus();
        InputBox.CaretIndex = InputBox.Text.Length;
    }

    private void OnFocusRequested(object? sender, EventArgs e) => Dispatcher.UIThread.Post(FocusInput);

    private void OnTextReset(object? sender, EventArgs e) => InputBox.ResetHistory();

    // 占位文字（没有发言权限）只在输入框为空、也没有正在组字时显示
    private void UpdatePlaceholder() =>
        PlaceholderText.IsVisible = string.IsNullOrEmpty(InputBox.Text) && !InputBox.HasPreedit;

    // 插入到光标处（输入框失去焦点后仍记得光标位置），前后不是空白时加空格（多行内容结尾补一个换行），然后把焦点移回输入框
    private void OnInsertRequested(object? sender, string markdown)
    {
        var text = InputBox.Text;
        var start = Math.Clamp(InputBox.SelectionStart, 0, text.Length);
        var end = Math.Clamp(InputBox.SelectionEnd, start, text.Length);
        var before = start > 0 && !char.IsWhiteSpace(text[start - 1]) ? " " : "";
        var block = MarkdownFormat.WithTrailingLine(markdown);
        var after = block != markdown || (end < text.Length && char.IsWhiteSpace(text[end])) ? "" : " ";
        InputBox.Replace(start, end, before + block + after);
        Dispatcher.UIThread.Post(() => InputBox.TextArea.Focus());
    }

    // 作为独立的段落插入到光标处（前后补足空行，见 MarkdownFormat.PadBlock），然后把焦点移回输入框
    private void OnInsertBlockRequested(object? sender, string markdown)
    {
        var text = InputBox.Text;
        var start = Math.Clamp(InputBox.SelectionStart, 0, text.Length);
        var end = Math.Clamp(InputBox.SelectionEnd, start, text.Length);
        InputBox.Replace(start, end, MarkdownFormat.PadBlock(text, start, end, markdown));
        Dispatcher.UIThread.Post(() => InputBox.TextArea.Focus());
    }

    // 文字或光标变化时更新 @ 补全与代码块语言补全
    private void UpdateCompletions()
    {
        if (_vm is null || !InputBox.IsKeyboardFocusWithin)
            return;
        _vm.Mentions.Update(InputBox.MaskedText, InputBox.CaretIndex);
        _vm.Languages.Update(InputBox.Text, InputBox.CaretIndex, InputBox.Model);
    }

    // 处理完后用 Post 执行命令：发送会清空文字，不能在同一次按键事件中修改 TextBox
    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null)
            return;
        var languages = _vm.Languages;
        if (languages.IsOpen)
        {
            switch (e.Key)
            {
                case Key.Up:
                    languages.Move(-1);
                    e.Handled = true;
                    return;
                case Key.Down:
                    languages.Move(1);
                    e.Handled = true;
                    return;
                case Key.Enter or Key.Tab when e.KeyModifiers == KeyModifiers.None:
                    AcceptLanguage(null);
                    e.Handled = true;
                    return;
                case Key.Escape:
                    languages.Close();
                    e.Handled = true;
                    return;
            }
        }

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
        // Ctrl+Shift+V 只粘贴文字（不使用复制时附带的格式）
        if (e.Key == Key.V && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            e.Handled = true;
            InputBox.Paste();
            return;
        }
        // 复制、剪切连同格式（编辑器自己的复制只有原文）
        if (e.Key is Key.C or Key.X && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            _ = InputBox.CopyAsync(cut: e.Key == Key.X);
            return;
        }
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && _vm.IsEditingMessage)
        {
            e.Handled = true;
            Dispatcher.UIThread.Post(() => _vm?.CancelEditCommand.Execute(null));
            return;
        }
        if (FormatShortcut(e) is { } format)
        {
            e.Handled = true;
            InputBox.ApplyFormat(format);
            return;
        }
        if (HeadingShortcut(e) is { } level)
        {
            e.Handled = true;
            InputBox.SetHeading(level);
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
            // Enter 发送时 Ctrl+Enter 换行（编辑器默认不处理 Ctrl+Enter）
            e.Handled = true;
            InsertText("\n");
        }
    }

    // 与网页版相同的格式快捷键
    private static MarkdownFormatKind? FormatShortcut(KeyEventArgs e) => (e.KeyModifiers, e.Key) switch
    {
        (KeyModifiers.Control, Key.B) => MarkdownFormatKind.Bold,
        (KeyModifiers.Control, Key.I) => MarkdownFormatKind.Italic,
        (KeyModifiers.Control | KeyModifiers.Shift, Key.L) => MarkdownFormatKind.Link,
        (KeyModifiers.Control | KeyModifiers.Shift, Key.C) => MarkdownFormatKind.Code,
        _ => null,
    };

    // 与 Typora 相同：Ctrl+1～6 设为标题，Ctrl+0 改回段落
    private static int? HeadingShortcut(KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control)
            return null;
        return e.Key switch
        {
            >= Key.D0 and <= Key.D6 => e.Key - Key.D0,
            >= Key.NumPad0 and <= Key.NumPad6 => e.Key - Key.NumPad0,
            _ => null,
        };
    }

    // 格式按钮（Tag 为 MarkdownFormatKind 的名字）
    private void OnFormatClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<MarkdownFormatKind>(tag, out var kind))
            Format(kind);
    }

    private void Format(MarkdownFormatKind kind)
    {
        InputBox.ApplyFormat(kind);
        InputBox.TextArea.Focus();
    }

    // 光标处已有的格式显示为按下
    private void UpdateFormatBar()
    {
        foreach (var button in FormatBar.Children.OfType<Button>())
        {
            if (button.Tag is string tag && Enum.TryParse<MarkdownFormatKind>(tag, out var kind))
                button.Classes.Set("active", InputBox.IsFormatActive(kind));
        }
    }

    private async void InsertTimeAsync()
    {
        if (_vm is not { } vm)
            return;
        if (await DateTimeDialogViewModel.ShowAsync(vm.Chat.Host, "插入时间", "插入") is { } at)
            InputBox.InsertTime(new DateTimeOffset(at));
        InputBox.TextArea.Focus();
    }

    private void OnLanguageClicked(object? sender, PointerReleasedEventArgs e)
    {
        if ((e.Source as StyledElement)?.DataContext is string language)
            AcceptLanguage(language);
    }

    private void AcceptLanguage(string? language)
    {
        if (_vm?.Languages.Accept(InputBox.Text, InputBox.CaretIndex, InputBox.Model, language) is not { } edit)
            return;
        InputBox.ApplyEdit(edit);
        InputBox.TextArea.Focus();
    }

    private void OnMentionClicked(object? sender, PointerReleasedEventArgs e)
    {
        if ((e.Source as StyledElement)?.DataContext is MentionCandidate candidate)
            AcceptMention(candidate);
    }

    private void AcceptMention(MentionCandidate? candidate)
    {
        if (_vm?.Mentions.Accept(InputBox.CaretIndex, candidate) is not { } result)
            return;
        InputBox.Replace(result.Start, InputBox.CaretIndex, result.Insertion);
        InputBox.TextArea.Focus();
    }

    // 粘贴顺序：本程序复制的 Markdown（连同格式）> 文件（资源管理器中复制的）> 位图（截图）> 文字。
    // 选中了一段文字（单行）时粘贴网址：变成以选中文字为标题的链接（与网页版相同）
    private async void PasteAsync()
    {
        if (_vm is null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        var start = InputBox.SelectionStart;
        var end = InputBox.SelectionEnd;
        var selected = InputBox.SelectedText;
        try
        {
            if (await MarkdownClipboard.TryGetMarkdownAsync(clipboard) is { Length: > 0 } markdown)
            {
                if (_vm.CanEdit)
                    InputBox.InsertBlock(markdown);
                return;
            }
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
            if (selected.Length > 0 && !selected.Contains('\n')
                && (await clipboard.TryGetTextAsync())?.Trim() is { Length: > 0 } url && !url.Contains('\n')
                && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
                && InputBox.SelectionStart == start && InputBox.SelectionEnd == end)
            {
                InputBox.Replace(start, end, $"[{selected}]({url})");
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("读取剪贴板失败", ex);
        }
        InputBox.Paste();
    }

    /// <summary>输入框右键菜单：剪切、复制、粘贴、全选，“段落 ▸”“插入 ▸”，以及“常用语 ▸”（插入、新建、删除）。</summary>
    private void OnInputContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_vm is not { } vm)
            return;
        e.Handled = true;
        var hasText = InputBox.Text.Length > 0;
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("剪切", () => _ = InputBox.CopyAsync(cut: true), InputBox.CanCut));
        menu.Items.Add(MenuItem("复制", () => _ = InputBox.CopyAsync(cut: false), InputBox.CanCopy));
        menu.Items.Add(MenuItem("粘贴", PasteAsync, vm.CanEdit));
        menu.Items.Add(MenuItem("全选", () => InputBox.SelectAll(), hasText));
        menu.Items.Add(new Separator());
        menu.Items.Add(ParagraphMenu(vm.CanEdit));
        menu.Items.Add(InsertMenu(vm));
        menu.Items.Add(new Separator());

        var snippets = new MenuItem { Header = "常用语" };
        foreach (var snippet in vm.Snippets)
        {
            var item = MenuItem(snippet.Title, () =>
            {
                InputBox.InsertBlock(snippet.Content);
                InputBox.TextArea.Focus();
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

    // 段落 ▸：标题级别（光标所在行的级别打勾）、列表、引用
    private MenuItem ParagraphMenu(bool enabled)
    {
        var menu = new MenuItem { Header = "段落", IsEnabled = enabled };
        var current = InputBox.HeadingLevel;
        string[] names = ["一级标题", "二级标题", "三级标题", "四级标题", "五级标题", "六级标题"];
        for (var level = 1; level <= 6; level++)
            menu.Items.Add(HeadingItem(names[level - 1], level, current));
        menu.Items.Add(new Separator());
        menu.Items.Add(HeadingItem("段落", 0, current));
        menu.Items.Add(new Separator());
        menu.Items.Add(FormatItem("有序列表", MarkdownFormatKind.NumberedList));
        menu.Items.Add(FormatItem("无序列表", MarkdownFormatKind.BulletedList));
        menu.Items.Add(FormatItem("引用", MarkdownFormatKind.Quote));
        return menu;
    }

    private MenuItem HeadingItem(string header, int level, int current)
    {
        var item = MenuItem(header, () =>
        {
            InputBox.SetHeading(level);
            InputBox.TextArea.Focus();
        }, true);
        item.ToggleType = MenuItemToggleType.CheckBox;
        item.IsChecked = level == current;
        item.InputGesture = new KeyGesture(Key.D0 + level, KeyModifiers.Control);
        return item;
    }

    private MenuItem FormatItem(string header, MarkdownFormatKind kind)
    {
        var item = MenuItem(header, () => Format(kind), true);
        item.ToggleType = MenuItemToggleType.CheckBox;
        item.IsChecked = InputBox.IsFormatActive(kind);
        return item;
    }

    // 插入 ▸：块、图片 / 链接 / 时间、分隔线 / 表格、不在工具栏中的行内格式
    private MenuItem InsertMenu(ComposerViewModel vm)
    {
        var menu = new MenuItem { Header = "插入", IsEnabled = vm.CanEdit };
        menu.Items.Add(MenuItem("代码块", () => Format(MarkdownFormatKind.CodeBlock), true));
        menu.Items.Add(MenuItem("公式块", () => Format(MarkdownFormatKind.MathBlock), true));
        menu.Items.Add(MenuItem("引用块", () => Format(MarkdownFormatKind.QuoteBlock), true));
        menu.Items.Add(MenuItem("剧透", () => Format(MarkdownFormatKind.Spoiler), true));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("图片…", async () =>
        {
            if (TopLevel.GetTopLevel(this) is { } top)
                await vm.PickImagesAsync(top);
        }, true));
        menu.Items.Add(MenuItem("链接", () => Format(MarkdownFormatKind.Link), true));
        menu.Items.Add(MenuItem("全局时间…", InsertTimeAsync, true));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("分隔线", () => Format(MarkdownFormatKind.Rule), true));
        menu.Items.Add(MenuItem("表格", () => Format(MarkdownFormatKind.Table), true));
        menu.Items.Add(new Separator());
        menu.Items.Add(FormatItem("删除线", MarkdownFormatKind.Strikethrough));
        menu.Items.Add(FormatItem("行内公式", MarkdownFormatKind.Math));
        return menu;
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
            InputBox.TextArea.Focus();
        };
        EmojiFlyout.Content = new EmojiPickerView { DataContext = picker };
    }

    /// <summary>在光标处插入 Markdown（替换选中部分）。</summary>
    private void InsertText(string s) => InputBox.Insert(s);
}
