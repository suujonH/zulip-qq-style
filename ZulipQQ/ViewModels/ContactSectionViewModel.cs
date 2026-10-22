using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Core;

namespace ZulipQQ.ViewModels;

/// <summary>联系人页分组的种类，决定标题的右键菜单。</summary>
public enum ContactSectionKind
{
    /// <summary>“我的好友”：全部用户。</summary>
    AllFriends,
    FriendGroup,
    Bots,
    PinnedTopics,
    Channel,
}

/// <summary>
/// 联系人页中可折叠的一个分组（好友分组、频道共用）。<see cref="Items"/> 中的条目由界面按类型选择模板。
/// 数据刷新时按 <see cref="Id"/> 复用同一个对象并就地更新，保留展开、重命名等状态。
/// 频道的话题列表懒加载：第一次展开时执行 <see cref="Loader"/>，加载结果由数据刷新填入。
/// </summary>
public partial class ContactSectionViewModel(ContactSectionKind kind, long id) : ViewModelBase
{
    public ContactSectionKind Kind { get; } = kind;

    /// <summary>分组标识：好友分组为分组 id（“我的好友”为 0），频道为频道 id。</summary>
    public long Id { get; } = id;

    [ObservableProperty]
    public partial string Name { get; private set; } = "";

    [ObservableProperty]
    public partial string CountText { get; private set; } = "";

    /// <summary>私有频道：标题前显示锁图标。</summary>
    [ObservableProperty]
    public partial bool IsLocked { get; private set; }

    /// <summary>静音的频道：标题显示为灰色。</summary>
    [ObservableProperty]
    public partial bool IsDimmed { get; private set; }

    public ObservableCollection<ViewModelBase> Items { get; } = [];

    /// <summary>第一次展开时加载内容（例如频道的话题列表），返回是否成功；已加载时为 null。</summary>
    public Func<Task<bool>>? Loader { get; private set; }

    // 加载失败后，数据刷新不再自动重试，等用户重新展开
    private bool _loadFailed;

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsRenaming { get; private set; }

    [ObservableProperty]
    public partial string RenameText { get; set; } = "";

    /// <summary>重命名提交时调用（参数为去掉首尾空白、非空的新名称）。</summary>
    public Action<string>? Renamed { get; set; }

    public void Update(string name, string countText, IReadOnlyList<ViewModelBase> items, Func<Task<bool>>? loader = null,
        bool isLocked = false, bool isDimmed = false)
    {
        Name = name;
        CountText = countText;
        IsLocked = isLocked;
        IsDimmed = isDimmed;
        Loader = loader;
        CollectionSync.SyncTo(Items, items);
        // 展开状态下内容被清空（例如重新同步后话题缓存失效）时重新加载
        if (IsExpanded && !_loadFailed)
            _ = LoadAsync();
    }

    /// <summary>在标题行进入重命名状态。</summary>
    public void BeginRename()
    {
        RenameText = Name;
        IsRenaming = true;
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (!value)
            return;
        _loadFailed = false;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (Loader is not { } loader || IsLoading)
            return;
        IsLoading = true;
        try
        {
            _loadFailed = !await loader();
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void ToggleExpanded()
    {
        if (!IsRenaming)
            IsExpanded = !IsExpanded;
    }

    // 回车或失去焦点时提交（可能执行两次，第二次时已不在重命名状态）；空白名称视为取消
    [RelayCommand]
    private void CommitRename()
    {
        if (!IsRenaming)
            return;
        IsRenaming = false;
        var name = RenameText.Trim();
        if (name.Length > 0 && name != Name)
            Renamed?.Invoke(name);
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;
}
