using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Core;
using ZulipQQ.Models;
using ZulipQQ.Services;
using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.ViewModels;

/// <summary>名片上的一个自定义资料字段。</summary>
public sealed record ProfileFieldItem(string Name, string Value);

/// <summary>
/// 用户名片（弹出卡片和右侧资料面板共用）：头像、名称、在线状态、邮箱、签名、当地时间、角色、资料字段，
/// 以及本地的备注和分组。显示期间（<see cref="Attach"/> 到 <see cref="Detach"/>）随数据变化刷新。
/// </summary>
public partial class UserCardViewModel : ViewModelBase
{
    private readonly ZulipSession _session;
    private readonly Action<ConversationKey> _openChat;
    private readonly Action? _onOpened;
    private bool _attached;
    private bool _refreshing;

    /// <param name="openChat">打开会话。</param>
    /// <param name="onOpened">点击“发消息”后调用（关闭弹出卡片）。</param>
    public UserCardViewModel(ZulipSession session, long userId, Action<ConversationKey> openChat, Action? onOpened = null)
    {
        _session = session;
        _openChat = openChat;
        _onOpened = onOpened;
        UserId = userId;
        Refresh();
    }

    public long UserId { get; }

    public bool IsSelf => UserId == _session.SelfId;

    /// <summary>备注和分组只对其他真人用户显示。</summary>
    public bool CanEditContact => !IsSelf && _session.Users.Get(UserId) is { IsBot: false };

    [ObservableProperty]
    public partial string DisplayName { get; private set; } = "";

    /// <summary>有备注时额外显示的 Zulip 昵称；没有备注时为 null。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNickname))]
    public partial string? Nickname { get; private set; }

    public bool HasNickname => Nickname is not null;

    [ObservableProperty]
    public partial string? AvatarUrl { get; private set; }

    [ObservableProperty]
    public partial bool IsOnline { get; private set; }

    [ObservableProperty]
    public partial string PresenceText { get; private set; } = "";

    [ObservableProperty]
    public partial string Email { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string? StatusText { get; private set; }

    public bool HasStatus => !string.IsNullOrEmpty(StatusText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLocalTime))]
    public partial string? LocalTimeText { get; private set; }

    public bool HasLocalTime => LocalTimeText is not null;

    /// <summary>所有者、管理员、版主、访客、机器人；普通成员为 null（不显示）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRole))]
    public partial string? RoleText { get; private set; }

    public bool HasRole => RoleText is not null;

    [ObservableProperty]
    public partial IReadOnlyList<ProfileFieldItem> ProfileFields { get; private set; } = [];

    [ObservableProperty]
    public partial string RemarkText { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsEditingRemark { get; private set; }

    [ObservableProperty]
    public partial string RemarkDraft { get; set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<GroupChoice> GroupChoices { get; private set; } = [];

    [ObservableProperty]
    public partial GroupChoice? SelectedGroup { get; set; }

    /// <summary>界面显示时调用，开始随数据变化刷新。</summary>
    public void Attach()
    {
        if (_attached)
            return;
        _attached = true;
        _session.Users.UserChanged += OnUserChanged;
        _session.Users.PresenceChanged += OnChanged;
        _session.Contacts.Changed += OnChanged;
        Refresh();
    }

    public void Detach()
    {
        if (!_attached)
            return;
        _attached = false;
        _session.Users.UserChanged -= OnUserChanged;
        _session.Users.PresenceChanged -= OnChanged;
        _session.Contacts.Changed -= OnChanged;
    }

    private void OnUserChanged(object? sender, long id)
    {
        if (id == UserId)
            Refresh();
    }

    private void OnChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        var user = _session.Users.Get(UserId);
        var contacts = _session.Contacts;
        var remark = contacts.GetRemark(UserId);
        DisplayName = contacts.DisplayName(UserId);
        Nickname = remark is null ? null : user?.FullName;
        AvatarUrl = user?.AvatarUrl;
        var presence = _session.Users.GetPresence(UserId, DateTimeOffset.UtcNow);
        IsOnline = presence == PresenceState.Online;
        PresenceText = presence switch
        {
            PresenceState.Online => "在线",
            PresenceState.Idle => "离开",
            _ => "离线",
        };
        Email = user is null ? "" : user.DeliveryEmail ?? user.Email;
        StatusText = _session.Users.GetStatusText(UserId);
        LocalTimeText = user is null ? null : LocalTime(user.Timezone, _session.Settings.Zulip.TwentyFourHourTime);
        RoleText = user switch
        {
            { IsBot: true } => "机器人",
            { Role: PermissionEvaluator.RoleOwner } => "所有者",
            { Role: PermissionEvaluator.RoleAdministrator } => "管理员",
            { Role: PermissionEvaluator.RoleModerator } => "版主",
            { Role: >= PermissionEvaluator.RoleGuest } => "访客",
            _ => null,
        };
        ProfileFields = user is null ? [] : Fields(user);
        RemarkText = remark ?? "";

        _refreshing = true;
        try
        {
            GroupChoices = [new GroupChoice(GroupChoiceKind.Default), .. contacts.Groups.Select(g => new GroupChoice(GroupChoiceKind.Group, g))];
            var groupId = contacts.GetGroupId(UserId);
            SelectedGroup = GroupChoices.FirstOrDefault(c => c.Group?.Id == groupId);
        }
        finally
        {
            _refreshing = false;
        }
    }

    partial void OnSelectedGroupChanged(GroupChoice? value)
    {
        if (_refreshing || value is null || !CanEditContact)
            return;
        _session.Contacts.MoveUsers([UserId], value.Group?.Id);
    }

    [RelayCommand]
    private void SendMessage()
    {
        _openChat(DmKey.Create([UserId], _session.SelfId));
        _onOpened?.Invoke();
    }

    [RelayCommand]
    private void BeginEditRemark()
    {
        RemarkDraft = RemarkText;
        IsEditingRemark = true;
    }

    // 回车或失去焦点时提交（可能执行两次）；空白表示清除备注
    [RelayCommand]
    private void CommitRemark()
    {
        if (!IsEditingRemark)
            return;
        IsEditingRemark = false;
        _session.Contacts.SetRemark(UserId, RemarkDraft);
    }

    [RelayCommand]
    private void CancelRemark() => IsEditingRemark = false;

    // 时区为空或无法识别时不显示
    private static string? LocalTime(string timezone, bool use24h)
    {
        if (string.IsNullOrEmpty(timezone))
            return null;
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(timezone);
            var time = TimeZoneInfo.ConvertTime(DateTimeOffset.Now, zone);
            return $"{TimeText.ClockTime(time.DateTime, use24h)}（{timezone}）";
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    // 只显示组织设置为在资料摘要中显示的字段，按字段顺序
    private List<ProfileFieldItem> Fields(ZulipUser user)
    {
        List<ProfileFieldItem> items = [];
        foreach (var field in _session.Realm.ProfileFields.Where(f => f.DisplayInProfileSummary).OrderBy(f => f.Order))
        {
            if (!user.ProfileData.TryGetValue(field.Id.ToString(CultureInfo.InvariantCulture), out var value))
                continue;
            if (FieldText(field, value.Value) is { Length: > 0 } text)
                items.Add(new ProfileFieldItem(field.Name, text));
        }
        return items;
    }

    // 选项字段（3）显示选项文字，用户字段（6）显示用户名，其余显示原值
    private string? FieldText(CustomProfileFieldDto field, JsonElement value)
    {
        switch (field.Type)
        {
            case 3 when value.ValueKind == JsonValueKind.String:
                return OptionText(field.FieldData, value.GetString()!) ?? value.GetString();
            case 6 when value.ValueKind == JsonValueKind.Array:
                return string.Join("、", value.EnumerateArray().Select(v => _session.Contacts.DisplayName(v.GetInt64())));
            case 6 when value.ValueKind == JsonValueKind.String:
                try
                {
                    var ids = JsonSerializer.Deserialize<long[]>(value.GetString()!) ?? [];
                    return string.Join("、", ids.Select(_session.Contacts.DisplayName));
                }
                catch (JsonException)
                {
                    return value.GetString();
                }
            default:
                return value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Number => value.GetRawText(),
                    _ => null,
                };
        }
    }

    /// <summary>选项字段的选项文字；field_data 可能是对象，也可能是 JSON 编码后的字符串。找不到时返回 null。</summary>
    private static string? OptionText(JsonElement fieldData, string optionId)
    {
        if (fieldData.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var doc = JsonDocument.Parse(fieldData.GetString()!);
                return OptionText(doc.RootElement, optionId);
            }
            catch (JsonException)
            {
                return null;
            }
        }
        return fieldData.ValueKind == JsonValueKind.Object
               && fieldData.TryGetProperty(optionId, out var option)
               && option.TryGetProperty("text", out var text)
            ? text.GetString()
            : null;
    }
}
