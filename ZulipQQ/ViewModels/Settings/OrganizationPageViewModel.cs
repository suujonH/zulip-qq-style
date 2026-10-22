using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Core;
using ZulipQQ.Services;
using ZulipQQ.Zulip;

namespace ZulipQQ.ViewModels.Settings;

/// <summary>消息编辑时限的选项；null 为不限。</summary>
public sealed record EditLimitOption(int? Seconds, string Text)
{
    public override string ToString() => Text;
}

/// <summary>
/// 组织管理（只有组织管理员可见）：组织名称、组织级权限、消息编辑、已读回执。
/// 只提交改动过的项；权限以 {"new","old"} 提交。组织设置变化（realm 事件）时，没有未保存的修改就重新读取。
/// </summary>
public sealed partial class OrganizationPageViewModel : SettingsPageViewModel
{
    private static readonly (string Name, string Label)[] Permissions =
    [
        ("can_create_public_channel_group", "创建公开频道"),
        ("can_create_private_channel_group", "创建私有频道"),
        ("can_invite_users_group", "邀请新成员"),
        ("can_add_subscribers_group", "为他人订阅频道"),
        ("can_move_messages_between_topics_group", "在话题间移动消息"),
        ("can_move_messages_between_channels_group", "在频道间移动消息"),
        ("can_resolve_topics_group", "标记话题已解决"),
        ("can_mention_many_users_group", "使用 @全体成员"),
        ("can_create_groups", "创建用户组"),
        ("can_manage_all_groups", "管理所有用户组"),
        ("can_add_custom_emoji_group", "添加自定义表情"),
        ("can_create_bots_group", "创建机器人"),
        ("direct_message_permission_group", "谁可以接收私信"),
        ("direct_message_initiator_group", "谁可以发起私信"),
        // 本客户端不提供删除消息，这两项只是显示和修改服务器的配置
        ("can_delete_any_message_group", "删除任何消息"),
        ("can_delete_own_message_group", "删除自己的消息"),
    ];

    // 只有组织所有者可以修改（Zulip 12 zerver/views/realm.py 的 OrganizationOwnerRequiredError），对其他管理员隐藏
    private static readonly HashSet<string> OwnerOnly = ["can_create_groups", "can_invite_users_group", "can_manage_all_groups"];

    private static readonly EditLimitOption[] StandardLimits =
    [
        new(null, "不限"),
        new(600, "10 分钟"),
        new(3600, "1 小时"),
        new(86400, "1 天"),
    ];

    private string _originalName = "";
    private bool _originalAllowEditing;
    private int? _originalLimit;
    private bool _originalReadReceipts;

    public OrganizationPageViewModel(SettingsViewModel owner) : base(owner)
    {
        Reload();
        Session.PermissionsChanged += OnPermissionsChanged;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial bool AllowEditing { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<EditLimitOption> LimitOptions { get; private set; } = [];

    [ObservableProperty]
    public partial EditLimitOption? Limit { get; set; }

    [ObservableProperty]
    public partial bool ReadReceipts { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<PermissionRowViewModel> Rows { get; private set; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsBusy { get; private set; }

    private bool IsDirty =>
        Name.Trim() != _originalName || AllowEditing != _originalAllowEditing || Limit?.Seconds != _originalLimit
        || ReadReceipts != _originalReadReceipts || Rows.Any(r => r.IsChanged);

    private bool CanSave => !IsBusy && Name.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var args = new FormBuilder();
        var changed = false;
        var name = Name.Trim();
        if (name != _originalName)
        {
            args.Add("name", name);
            changed = true;
        }
        if (AllowEditing != _originalAllowEditing)
        {
            args.Add("allow_message_editing", AllowEditing);
            changed = true;
        }
        if (Limit is { } limit && limit.Seconds != _originalLimit)
        {
            // 不限以字符串 "unlimited" 提交（JSON 编码）
            args.AddJson("message_content_edit_limit_seconds", limit.Seconds is { } s ? s : "unlimited");
            changed = true;
        }
        if (ReadReceipts != _originalReadReceipts)
        {
            args.Add("enable_read_receipts", ReadReceipts);
            changed = true;
        }
        foreach (var row in Rows.Where(r => r.IsChanged))
        {
            args.AddJson(row.Name, new Dictionary<string, object?> { ["new"] = row.Selected!.GroupId, ["old"] = row.Original });
            changed = true;
        }
        if (!changed)
            return;

        IsBusy = true;
        try
        {
            await Session.Client.UpdateRealmAsync(args, Session.Token);
            if (!Session.IsDisposed)
                Owner.ShowToast("设置已保存");
        }
        catch (ZulipApiException e) when (e.Code == "EXPECTATION_MISMATCH" && !Session.IsDisposed)
        {
            Owner.ShowToast("设置已被他人修改，已重新读取");
            IsBusy = false;
            Reload();
        }
        catch (Exception ex) when (!Session.IsDisposed)
        {
            Log.Warn("修改组织设置失败", ex);
            Owner.ShowToast(ex is ZulipApiException { Message.Length: > 0 } api ? $"保存失败：{api.Message}" : "保存失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>放弃修改，重新读取当前设置。</summary>
    [RelayCommand]
    private void Revert() => Reload();

    public override void Close() => Session.PermissionsChanged -= OnPermissionsChanged;

    // 组织设置变化（包括自己刚保存的结果）后重新读取；有未保存的修改时保留用户的输入
    private void OnPermissionsChanged(object? sender, EventArgs e)
    {
        if (!IsBusy && (!IsDirty || IsSavedState()))
            Reload();
    }

    // 当前输入与服务器的最新状态一致（刚保存成功、事件已到达）
    private bool IsSavedState()
    {
        var realm = Session.Realm;
        return Name.Trim() == realm.Name && AllowEditing == realm.AllowMessageEditing
               && Limit?.Seconds == realm.EditLimitSeconds && ReadReceipts == realm.EnableReadReceipts
               && Rows.All(r => r.IsCustom || r.Selected?.GroupId == Session.RealmGroupSetting(r.Name)?.GroupId);
    }

    private void Reload()
    {
        var realm = Session.Realm;
        Name = _originalName = realm.Name;
        AllowEditing = _originalAllowEditing = realm.AllowMessageEditing;
        ReadReceipts = _originalReadReceipts = realm.EnableReadReceipts;
        _originalLimit = realm.EditLimitSeconds;
        var options = StandardLimits.ToList();
        if (options.All(o => o.Seconds != _originalLimit))
            options.Add(new EditLimitOption(_originalLimit, $"{_originalLimit / 60} 分钟"));
        LimitOptions = options;
        Limit = options.First(o => o.Seconds == _originalLimit);
        var isOwner = Session.Permissions.Role == PermissionEvaluator.RoleOwner;
        Rows = Permissions
            .Where(p => isOwner || !OwnerOnly.Contains(p.Name))
            .Select(p => new PermissionRowViewModel(p.Name, p.Label, Session.RealmGroupSetting(p.Name), ChoicesFor(p.Name)))
            .ToList();
    }

    // 与群资料设置相同：allowed_system_groups 为空表示不限制，只受“无人 / 所有人”开关限制
    private List<GroupChoiceOption> ChoicesFor(string name)
    {
        var info = Session.PermissionSettings.GetValueOrDefault(("realm", name)) ?? PermissionSettingInfo.Unrestricted;
        return SystemGroups.ChoicesFor(info)
            .Select(g => Session.SystemGroupId(g.Name) is { } id ? new GroupChoiceOption(id, g.Text) : null)
            .OfType<GroupChoiceOption>()
            .ToList();
    }
}
