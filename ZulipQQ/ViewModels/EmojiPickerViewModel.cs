using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Core;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels;

/// <summary>表情网格的一行（按行虚拟化，全部表情约 1900 个）。</summary>
public sealed record EmojiRow(IReadOnlyList<EmojiEntry> Items);

/// <summary>
/// 表情选择器：搜索框、常用表情、全部表情（组织自定义表情在前）。选中后触发 <see cref="Picked"/>，
/// 并计入常用表情。
/// </summary>
public partial class EmojiPickerViewModel : ViewModelBase
{
    public const int Columns = 9;

    private readonly EmojiService _emoji;

    public EmojiPickerViewModel(EmojiService emoji)
    {
        _emoji = emoji;
        Rebuild();
        _ = LoadAsync();
    }

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<EmojiEntry> Recent { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<EmojiRow> Rows { get; private set; } = [];

    /// <summary>搜索时隐藏“常用”。</summary>
    public bool ShowRecent => Query.Trim().Length == 0 && Recent.Count > 0;

    public event EventHandler<EmojiEntry>? Picked;

    [RelayCommand]
    private void Pick(EmojiEntry emoji)
    {
        _emoji.AddRecent(emoji);
        Picked?.Invoke(this, emoji);
    }

    partial void OnQueryChanged(string value) => Rebuild();

    private async System.Threading.Tasks.Task LoadAsync()
    {
        await _emoji.EnsureLoadedAsync();
        Rebuild();
    }

    private void Rebuild()
    {
        Recent = _emoji.Recent;
        Rows = _emoji.Catalog.Search(Query).Chunk(Columns).Select(c => new EmojiRow(c)).ToList();
        OnPropertyChanged(nameof(ShowRecent));
    }
}
