using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
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
/// 组织管理：组织名称、组织级权限、消息编辑、已读回执。服务器把这些设置下发给所有成员，所以所有人都能查看；
/// 组织管理员可以修改（部分权限只有所有者可以修改），其他人只读。服务器没有下发的设置（旧版服务器）不显示。
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

    // 只有组织所有者可以修改（Zulip 12 zerver/views/realm.py 的 OrganizationOwnerRequiredError），其他管理员只读
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

    /// <summary>组织管理员：可以修改（PATCH /realm 要求管理员）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditLimit))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool CanEdit { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditLimit))]
    public partial bool AllowEditing { get; set; }

    public bool CanEditLimit => CanEdit && AllowEditing;

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

    public override bool HasUnsavedChanges => IsDirty;

    public override ICommand SaveChangesCommand => SaveCommand;

    public override ICommand DiscardChangesCommand => RevertCommand;

    private bool IsDirty =>
        Name.Trim() != _originalName || AllowEditing != _originalAllowEditing || Limit?.Seconds != _originalLimit
        || ReadReceipts != _originalReadReceipts || Rows.Any(r => r.IsChanged);

    private bool CanSave => CanEdit && !IsBusy && Name.Trim().Length > 0;

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
        // 开发者模式下非管理员也可以编辑，保存时只模拟
        var isOwner = Session.Permissions.Role == PermissionEvaluator.RoleOwner;
        var allowed = Session.Permissions.IsOrgAdmin && (isOwner || !Rows.Any(r => r.IsChanged && OwnerOnly.Contains(r.Name)));
        if (Session.Mocks(allowed, Owner.ShowToast))
            return;

        IsBusy = true;
        try
        {
            await Session.Client.UpdateRealmAsync(args, Session.Token);
            if (Session.IsDisposed)
                return;
            Owner.ShowToast("设置已保存");
            // 保存期间到达的 realm 事件被跳过：结果已全部到达时在这里重新读取
            IsBusy = false;
            if (IsSavedState())
                Reload();
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

    public override void Close()
    {
        Session.PermissionsChanged -= OnPermissionsChanged;
        foreach (var row in Rows)
            row.PropertyChanged -= OnRowChanged;
    }

    // 输入变化时刷新“未保存”状态（保存 / 撤销提示）
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Name) or nameof(AllowEditing) or nameof(Limit) or nameof(ReadReceipts) or nameof(Rows))
            OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PermissionRowViewModel.Selected))
            OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    // 组织设置变化（包括自己刚保存的结果）后重新读取；有未保存的修改时保留用户的输入，失去管理员身份时放弃修改
    private void OnPermissionsChanged(object? sender, EventArgs e)
    {
        if (!IsBusy && (!IsDirty || IsSavedState() || CanEdit != Session.Shows(Session.Permissions.IsOrgAdmin)))
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
        CanEdit = Session.Shows(Session.Permissions.IsOrgAdmin);
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
        foreach (var row in Rows)
            row.PropertyChanged -= OnRowChanged;
        Rows = Permissions
            .Select(p => (p.Name, p.Label, Value: Session.RealmGroupSetting(p.Name)))
            .Where(p => p.Value is not null)
            .Select(p => new PermissionRowViewModel(p.Name, p.Label, p.Value, ChoicesFor(p.Name),
                CanEdit && Session.Shows(isOwner || !OwnerOnly.Contains(p.Name))))
            .ToList();
        foreach (var row in Rows)
            row.PropertyChanged += OnRowChanged;
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
