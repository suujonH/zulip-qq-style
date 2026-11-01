using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ZulipQQ.Core;

namespace ZulipQQ.ViewModels;

/// <summary>输入框上方的代码块语言补全列表（在 <c>```</c> 后输入时打开），最多 8 个。</summary>
public partial class CodeLanguageCompletionViewModel : ViewModelBase
{
    private const int MaxItems = 8;

    private int _start = -1;

    public ObservableCollection<string> Items { get; } = [];

    [ObservableProperty]
    public partial int SelectedIndex { get; set; }

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    /// <summary>文字或光标变化后调用：光标在开头围栏行末时更新候选，否则关闭。</summary>
    public void Update(string text, int caret, MarkdownModel model)
    {
        if (!CodeLanguages.TryGetQuery(text, caret, model, out var start, out var query))
        {
            Close();
            return;
        }
        var matched = CodeLanguages.Search(query, MaxItems);
        if (matched.Count == 0)
        {
            Close();
            return;
        }
        _start = start;
        CollectionSync.SyncTo(Items, matched);
        SelectedIndex = 0;
        IsOpen = true;
    }

    public void Close()
    {
        IsOpen = false;
        _start = -1;
    }

    /// <summary>上下移动选中项（循环）。</summary>
    public void Move(int delta)
    {
        if (Items.Count > 0)
            SelectedIndex = (SelectedIndex + delta + Items.Count) % Items.Count;
    }

    /// <summary>选中的语言（为 null 时用当前选中项）对应的修改；没有打开时返回 null。</summary>
    public MarkdownEdit? Accept(string text, int caret, MarkdownModel model, string? language = null)
    {
        language ??= SelectedIndex >= 0 && SelectedIndex < Items.Count ? Items[SelectedIndex] : null;
        if (!IsOpen || language is null || _start < 0 || _start > caret)
            return null;
        var edit = CodeLanguages.Accept(text, caret, _start, language, model);
        Close();
        return edit;
    }
}
