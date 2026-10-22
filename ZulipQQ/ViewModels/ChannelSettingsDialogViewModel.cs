using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Core;
using ZulipQQ.Models;
using ZulipQQ.Services;
using ZulipQQ.Zulip;

namespace ZulipQQ.ViewModels;

/// <summary>权限下拉框的一个选项：系统组 id 与中文名。</summary>
public sealed record GroupChoiceOption(long? GroupId, string Text)
{
    public override string ToString() => Text;
}

/// <summary>
/// 群资料设置中的一项权限。当前值不是单个可选的系统组时为“自定义”，下拉框禁用，只能在网页端修改。
/// </summary>
public partial class PermissionRowViewModel : ObservableObject
{
    public PermissionRowViewModel(string name, string label, GroupSettingValue? original, IReadOnlyList<GroupChoiceOption> choices)
    {
        Name = name;
        Label = label;
        Original = original;
        var current = original?.GroupId is { } id ? choices.FirstOrDefault(c => c.GroupId == id) : null;
        IsCustom = current is null;
        Choices = IsCustom ? [new GroupChoiceOption(null, "自定义（请在网页端修改）")] : choices;
        Selected = IsCustom ? Choices[0] : current;
    }

    /// <summary>Zulip 的设置名，例如 can_send_message_group。</summary>
    public string Name { get; }

    public string Label { get; }

    public GroupSettingValue? Original { get; }

    public IReadOnlyList<GroupChoiceOption> Choices { get; }

    public bool IsCustom { get; }

    public bool IsEditable => !IsCustom;

    [ObservableProperty]
    public partial GroupChoiceOption? Selected { get; set; }

    public bool IsChanged => !IsCustom && Selected?.GroupId is { } id && Original?.GroupId != id;
}

/// <summary>
/// 群资料设置（仅频道管理员可打开）：名称、描述、是否私有、新成员可否查看历史，以及频道的各项权限。
/// 保存时只提交改动过的项；权限以 {"new","old"} 提交，服务器发现已被他人修改时提示重新打开。
/// </summary>
public partial class ChannelSettingsDialogViewModel : ViewModelBase
{
    private static readonly (string Name, string Label, Func<Channel, GroupSettingValue?> Get)[] Permissions =
    [
        ("can_send_message_group", "谁可以发言", c => c.CanSendMessageGroup),
        ("can_add_subscribers_group", "谁可以邀请成员", c => c.CanAddSubscribersGroup),
        ("can_remove_subscribers_group", "谁可以移出成员", c => c.CanRemoveSubscribersGroup),
        ("can_administer_channel_group", "频道管理员", c => c.CanAdministerChannelGroup),
        ("can_move_messages_within_channel_group", "谁可以在频道内移动话题", c => c.CanMoveMessagesWithinChannelGroup),
        ("can_move_messages_out_of_channel_group", "谁可以把话题移出频道", c => c.CanMoveMessagesOutOfChannelGroup),
        ("can_resolve_topics_group", "谁可以标记已解决", c => c.CanResolveTopicsGroup),
        ("can_create_topic_group", "谁可以新建话题", c => c.CanCreateTopicGroup),
        // 本客户端不提供删除消息，这两项只是显示和修改服务器的配置
        ("can_delete_any_message_group", "谁可以删除任何消息", c => c.CanDeleteAnyMessageGroup),
        ("can_delete_own_message_group", "谁可以删除自己的消息", c => c.CanDeleteOwnMessageGroup),
    ];

    private readonly ZulipSession _session;
    private readonly MainViewModel _main;
    private readonly long _streamId;
    private readonly Action _onClosed;
    private readonly string _originalName;
    private readonly string _originalDescription;
    private readonly bool _originalPrivate;
    private readonly bool _originalHistoryPublic;

    private ChannelSettingsDialogViewModel(MainViewModel main, Channel channel, Action onClosed)
    {
        _session = main.Session;
        _main = main;
        _streamId = channel.StreamId;
        _onClosed = onClosed;
        Title = $"群资料设置 - #{channel.Name}";
        Name = _originalName = channel.Name;
        Description = _originalDescription = channel.Description;
        IsPrivate = _originalPrivate = channel.InviteOnly;
        IsHistoryPublic = _originalHistoryPublic = channel.HistoryPublicToSubscribers;
        Rows = Permissions.Select(p => new PermissionRowViewModel(p.Name, p.Label, p.Get(channel), ChoicesFor(p.Name))).ToList();
    }

    /// <summary>在主窗口的对话框层中打开；频道不存在时不做任何事。</summary>
    public static void Show(MainViewModel main, long streamId)
    {
        if (main.Session.Channels.Get(streamId) is not { } channel)
            return;
        ChannelSettingsDialogViewModel dialog = null!;
        dialog = new ChannelSettingsDialogViewModel(main, channel, () => main.Dialogs.Remove(dialog));
        main.Dialogs.Add(dialog);
    }

    public string Title { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string Description { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeHistory))]
    public partial bool IsPrivate { get; set; }

    /// <summary>公开频道的历史总是对新成员可见，只有私有频道可以修改。</summary>
    public bool CanChangeHistory => IsPrivate;

    [ObservableProperty]
    public partial bool IsHistoryPublic { get; set; }

    public IReadOnlyList<PermissionRowViewModel> Rows { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsBusy { get; private set; }

    partial void OnIsPrivateChanged(bool value)
    {
        if (!value)
            IsHistoryPublic = true;
    }

    /// <summary>
    /// 该设置可选的系统组。server_supported_permission_settings 中 allowed_system_groups 为空表示不限制
    /// （测试服务器 Zulip 12 的频道设置全部为空），此时列出全部系统组，只受“无人 / 所有人”开关限制。
    /// </summary>
    private List<GroupChoiceOption> ChoicesFor(string name)
    {
        var info = _session.PermissionSettings.GetValueOrDefault(("stream", name)) ?? PermissionSettingInfo.Unrestricted;
        return SystemGroups.ChoicesFor(info)
            .Select(g => _session.SystemGroupId(g.Name) is { } id ? new GroupChoiceOption(id, g.Text) : null)
            .OfType<GroupChoiceOption>()
            .ToList();
    }

    private bool CanSave => !IsBusy && Name.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var args = new FormBuilder();
        var changed = false;
        var name = Name.Trim();
        if (name != _originalName)
        {
            args.Add("new_name", name);
            changed = true;
        }
        var description = Description.Trim();
        if (description != _originalDescription.Trim())
        {
            args.Add("description", description);
            changed = true;
        }
        if (IsPrivate != _originalPrivate)
        {
            args.Add("is_private", IsPrivate);
            changed = true;
        }
        if (IsHistoryPublic != _originalHistoryPublic)
        {
            args.Add("history_public_to_subscribers", IsHistoryPublic);
            changed = true;
        }
        foreach (var row in Rows.Where(r => r.IsChanged))
        {
            args.AddJson(row.Name, new Dictionary<string, object?> { ["new"] = row.Selected!.GroupId, ["old"] = row.Original });
            changed = true;
        }
        if (!changed)
        {
            _onClosed();
            return;
        }

        IsBusy = true;
        try
        {
            await _session.Client.UpdateChannelAsync(_streamId, args, _session.Token);
            _onClosed();
            _main.ShowToast("设置已保存");
        }
        catch (ZulipApiException e) when (e.Code == "EXPECTATION_MISMATCH" && !_session.IsDisposed)
        {
            _main.ShowToast("设置已被他人修改，请重新打开");
        }
        catch (Exception ex) when (!_session.IsDisposed)
        {
            Log.Warn($"修改频道设置失败：{_streamId}", ex);
            _main.ShowToast(ex is ZulipApiException { Message.Length: > 0 } api ? $"保存失败：{api.Message}" : "保存失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _onClosed();
}
