using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Core;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels;

/// <summary>选择用户对话框中的一行。</summary>
public partial class UserPickerItem(long userId, string displayName, string? avatarUrl, string email) : ObservableObject
{
    public long UserId { get; } = userId;

    public string DisplayName { get; } = displayName;

    public string? AvatarUrl { get; } = avatarUrl;

    public string Email { get; } = email;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }
}

/// <summary>
/// 选择用户对话框：左侧搜索并勾选，右侧显示已选用户（可移除）。
/// 至少选择 <paramref name="minCount"/> 人才能确定。关闭时调用 onClosed：确定传入所选用户 id（按选择顺序），取消传入 null。
/// </summary>
public partial class UserPickerDialogViewModel : ViewModelBase
{
    private static readonly StringComparer NameComparer =
        StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), CompareOptions.None);

    private readonly List<UserPickerItem> _all;
    private readonly int _minCount;
    private readonly Action<IReadOnlyList<long>?> _onClosed;

    public UserPickerDialogViewModel(ZulipSession session, string title, int minCount, IEnumerable<long>? preselected,
        Action<IReadOnlyList<long>?> onClosed)
    {
        Title = title;
        _minCount = minCount;
        _onClosed = onClosed;
        _all = session.Users.ActiveHumans(excludeSelf: true)
            .Select(u => new UserPickerItem(u.Id, session.Contacts.DisplayName(u.Id), u.AvatarUrl, u.Email))
            .OrderBy(i => i.DisplayName, NameComparer)
            .ToList();
        foreach (var item in _all)
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(UserPickerItem.IsChecked))
                    OnItemChecked(item);
            };

        var pre = preselected?.ToHashSet() ?? [];
        foreach (var item in _all.Where(i => pre.Contains(i.UserId)))
            item.IsChecked = true;
        ApplyFilter();
    }

    public string Title { get; }

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    /// <summary>左侧：符合搜索条件的用户。</summary>
    public ObservableCollection<UserPickerItem> Results { get; } = [];

    /// <summary>右侧：已选择的用户，按选择顺序。</summary>
    public ObservableCollection<UserPickerItem> Selected { get; } = [];

    public string SelectedText => $"已选择 {Selected.Count} 人";

    private bool CanConfirm => Selected.Count >= _minCount;

    partial void OnQueryChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var q = Query.Trim();
        var desired = q.Length == 0
            ? _all
            : _all.Where(i => i.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                              || i.Email.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        CollectionSync.SyncTo(Results, desired);
    }

    private void OnItemChecked(UserPickerItem item)
    {
        if (item.IsChecked && !Selected.Contains(item))
            Selected.Add(item);
        else if (!item.IsChecked)
            Selected.Remove(item);
        OnPropertyChanged(nameof(SelectedText));
        ConfirmCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void Remove(UserPickerItem item) => item.IsChecked = false;

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm() => _onClosed(Selected.Select(i => i.UserId).ToList());

    [RelayCommand]
    private void Cancel() => _onClosed(null);
}
