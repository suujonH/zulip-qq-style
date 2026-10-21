using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ZulipQQ.ViewModels;

/// <summary>
/// 联系人页中可折叠的一个分组（好友分组、我的设备、群聊分类共用）。
/// <see cref="Items"/> 中可以是 <see cref="FriendItemViewModel"/>、<see cref="DeviceItemViewModel"/> 或
/// <see cref="GroupItemViewModel"/>，由界面按类型选择模板。
/// </summary>
public partial class ContactSectionViewModel(long id, string name, string countText, IReadOnlyList<ViewModelBase> items)
    : ViewModelBase
{
    /// <summary>分组标识，数据刷新后用来恢复展开状态。</summary>
    public long Id { get; } = id;

    public string Name { get; } = name;

    public string CountText { get; } = countText;

    public IReadOnlyList<ViewModelBase> Items { get; } = items;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}
