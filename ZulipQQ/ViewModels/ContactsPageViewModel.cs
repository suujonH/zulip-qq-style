using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels;

/// <summary>“联系人”页：好友 / 群聊两个标签，各自是一组可折叠的分组。</summary>
public partial class ContactsPageViewModel : ViewModelBase
{
    /// <summary>“我的设备”分组的 Id，与好友分组 Id（从 1 开始）不冲突。</summary>
    private const long DeviceSectionId = -1;

    private readonly FriendStore _store = FriendStore.Instance;

    public ContactsPageViewModel()
    {
        FriendSections = BuildFriendSections();
        GroupSections = MockData.GroupCategories
            .Select((c, i) => new ContactSectionViewModel(i, c.Name, c.Groups.Count.ToString(),
                c.Groups.Select(g => new GroupItemViewModel(g)).ToList()))
            .ToList();

        // 好友管理器中修改分组 / 备注后，重建好友分组，并保留原来的展开状态
        _store.Changed += (_, _) =>
        {
            var expanded = FriendSections.Where(s => s.IsExpanded).Select(s => s.Id).ToHashSet();
            FriendSections = BuildFriendSections();
            foreach (var section in FriendSections)
                section.IsExpanded = expanded.Contains(section.Id);
        };
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSections))]
    public partial IReadOnlyList<ContactSectionViewModel> FriendSections { get; private set; }

    public IReadOnlyList<ContactSectionViewModel> GroupSections { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSections))]
    public partial bool IsGroupTab { get; set; }

    public IReadOnlyList<ContactSectionViewModel> CurrentSections => IsGroupTab ? GroupSections : FriendSections;

    [RelayCommand]
    private void ShowFriends() => IsGroupTab = false;

    [RelayCommand]
    private void ShowGroups() => IsGroupTab = true;

    // “我的设备”只显示设备数，普通分组显示“在线数/总数”
    private List<ContactSectionViewModel> BuildFriendSections()
    {
        var devices = MockData.Devices;
        return
        [
            new ContactSectionViewModel(DeviceSectionId, "我的设备", devices.Count.ToString(),
                devices.Select(d => new DeviceItemViewModel(d)).ToList()),
            .. _store.Groups.Select(g =>
            {
                var friends = _store.Friends.Where(f => f.GroupId == g.Id).ToList();
                return new ContactSectionViewModel(
                    g.Id,
                    g.Name,
                    $"{friends.Count(f => f.User.IsOnline)}/{friends.Count}",
                    friends.Select(f => new FriendItemViewModel(f)).ToList());
            }),
        ];
    }
}
