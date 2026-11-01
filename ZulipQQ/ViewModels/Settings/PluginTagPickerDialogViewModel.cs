using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ZulipQQ.ViewModels.Settings;

/// <summary>
/// tags 设置项的候选列表（例如获取到的模型列表）：每一项可以添加到设置项或从中删除；不在列表中的可以手动输入后添加。
/// 修改直接作用于设置项（保存仍由设置窗口决定）。
/// </summary>
public sealed partial class PluginTagPickerDialogViewModel : ViewModelBase, IDisposable
{
    private readonly PluginFieldViewModel _field;
    private readonly Action _onClosed;

    public PluginTagPickerDialogViewModel(PluginFieldViewModel field, Action onClosed)
    {
        _field = field;
        _onClosed = onClosed;
        Title = $"选择{field.Label}";
        Items = field.Candidates.Select(c => new PluginTagPickerItemViewModel(field, c)).ToList();
    }

    public string Title { get; }

    public IReadOnlyList<PluginTagPickerItemViewModel> Items { get; }

    /// <summary>筛选后的项（名称包含 <see cref="Filter"/>，不区分大小写）。</summary>
    public IReadOnlyList<PluginTagPickerItemViewModel> VisibleItems =>
        string.IsNullOrWhiteSpace(Filter) ? Items : Items.Where(i => i.Value.Contains(Filter.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

    public bool HasNoVisibleItems => VisibleItems.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleItems), nameof(HasNoVisibleItems))]
    public partial string? Filter { get; set; }

    /// <summary>手动输入的名称。</summary>
    [ObservableProperty]
    public partial string? CustomInput { get; set; }

    [RelayCommand]
    private void AddCustom()
    {
        var value = CustomInput?.Trim() ?? "";
        if (value.Length == 0)
            return;
        _field.AddTagValue(value);
        CustomInput = "";
        // 列表中有同名的项时同步按钮状态
        foreach (var item in Items.Where(i => i.Value == value))
            item.Refresh();
    }

    [RelayCommand]
    private void Close() => _onClosed();

    public void Dispose() => _onClosed();
}

/// <summary>候选列表的一项。</summary>
public sealed partial class PluginTagPickerItemViewModel(PluginFieldViewModel owner, string value) : ObservableObject
{
    public string Value { get; } = value;

    public bool IsAdded => owner.HasTag(Value);

    public string ButtonText => IsAdded ? "删除" : "添加";

    [RelayCommand]
    private void Toggle()
    {
        if (IsAdded)
            owner.RemoveTagValue(Value);
        else
            owner.AddTagValue(Value);
        Refresh();
    }

    internal void Refresh()
    {
        OnPropertyChanged(nameof(IsAdded));
        OnPropertyChanged(nameof(ButtonText));
    }
}
