using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using ZulipQQ.Models;
using ZulipQQ.Zulip;
using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.Services;

/// <summary>用户、在线状态、个性签名（status_text）与静音用户。只在 UI 线程上读写。</summary>
public sealed class UserStore(ZulipSession session)
{
    private readonly Dictionary<long, ZulipUser> _users = [];
    private readonly Dictionary<long, PresenceDto> _presence = [];
    private readonly Dictionary<long, string> _statusText = [];
    private readonly HashSet<long> _muted = [];

    /// <summary>某个用户的资料或签名变化。</summary>
    public event EventHandler<long>? UserChanged;

    /// <summary>一个或多个用户的在线状态变化。</summary>
    public event EventHandler? PresenceChanged;

    public event EventHandler? MutedChanged;

    /// <summary>重新 register 后数据整体替换。</summary>
    public event EventHandler? Reset;

    public void Load(RegisterResult reg)
    {
        _users.Clear();
        _presence.Clear();
        _statusText.Clear();
        _muted.Clear();

        foreach (var u in reg.RealmUsers)
            _users[u.UserId] = ZulipUser.FromDto(u, isActive: true);
        // realm_non_active_users 中没有 is_active 字段，需显式设为停用
        foreach (var u in reg.RealmNonActiveUsers)
            _users[u.UserId] = ZulipUser.FromDto(u, isActive: false);
        foreach (var u in reg.CrossRealmBots)
            _users.TryAdd(u.UserId, ZulipUser.FromDto(u, isActive: true));

        foreach (var (id, p) in reg.Presences)
            _presence[id] = p;
        foreach (var (id, s) in reg.UserStatus)
        {
            if (!string.IsNullOrEmpty(s.StatusText))
                _statusText[id] = s.StatusText;
        }
        foreach (var m in reg.MutedUsers)
            _muted.Add(m.Id);

        Reset?.Invoke(this, EventArgs.Empty);
    }

    public ZulipUser? Get(long id) => _users.GetValueOrDefault(id);

    /// <summary>未停用的真人用户。</summary>
    public IEnumerable<ZulipUser> ActiveHumans(bool excludeSelf) =>
        _users.Values.Where(u => u.IsActive && !u.IsBot && !(excludeSelf && u.Id == session.SelfId));

    public IEnumerable<ZulipUser> ActiveBots() => _users.Values.Where(u => u.IsActive && u.IsBot);

    /// <summary>
    /// 在线状态：最近活跃时间在离线阈值内为在线；否则最近空闲时间在阈值内为离开；都不满足为离线。
    /// </summary>
    public PresenceState GetPresence(long id, DateTimeOffset now)
    {
        if (!_presence.TryGetValue(id, out var p))
            return PresenceState.Offline;
        var threshold = now.ToUnixTimeSeconds() - session.Realm.PresenceOfflineThresholdSeconds;
        if (p.ActiveTimestamp >= threshold)
            return PresenceState.Online;
        return p.IdleTimestamp >= threshold ? PresenceState.Idle : PresenceState.Offline;
    }

    public string? GetStatusText(long id) => _statusText.GetValueOrDefault(id);

    public bool IsMuted(long id) => _muted.Contains(id);

    /// <summary>realm_user 事件。返回值表示自己的角色是否变化（需要重建权限）。</summary>
    public bool OnRealmUserEvent(JsonElement ev)
    {
        var person = ev.GetProperty("person");
        var id = person.GetProperty("user_id").GetInt64();
        var roleChanged = false;
        switch (ev.GetProperty("op").GetString())
        {
            case "add":
                _users[id] = ZulipUser.FromDto(ZulipJson.Deserialize<UserDto>(person), isActive: true);
                break;
            case "update":
                if (!_users.TryGetValue(id, out var user))
                    return false;
                user.ApplyUpdate(person);
                roleChanged = id == session.SelfId && person.TryGetProperty("role", out _);
                break;
            // 旧服务器停用用户时发送 remove
            case "remove":
                if (_users.TryGetValue(id, out var removed))
                    removed.IsActive = false;
                break;
            default:
                return false;
        }

        UserChanged?.Invoke(this, id);
        return roleChanged;
    }

    /// <summary>presence 事件（simplified_presence_events）：<c>presences</c> 以用户 id 为键。</summary>
    public void OnPresenceEvent(JsonElement ev)
    {
        if (ev.TryGetProperty("presences", out var presences))
            ApplyPresences(presences.Deserialize<Dictionary<long, PresenceDto>>(ZulipJson.Options) ?? []);
    }

    /// <summary>合并在线状态（事件或心跳响应）。</summary>
    public void ApplyPresences(IReadOnlyDictionary<long, PresenceDto> presences)
    {
        if (presences.Count == 0)
            return;
        foreach (var (id, p) in presences)
            _presence[id] = p;
        PresenceChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>user_status 事件。只包含变化的字段；status_text 为空字符串表示清除。</summary>
    public void OnStatusEvent(JsonElement ev)
    {
        if (!ev.TryGetProperty("status_text", out var text))
            return;
        var id = ev.GetProperty("user_id").GetInt64();
        var value = text.GetString();
        if (string.IsNullOrEmpty(value))
            _statusText.Remove(id);
        else
            _statusText[id] = value;
        UserChanged?.Invoke(this, id);
    }

    /// <summary>muted_users 事件：完整的静音列表。</summary>
    public void OnMutedUsersEvent(JsonElement ev)
    {
        _muted.Clear();
        foreach (var m in ev.GetProperty("muted_users").EnumerateArray())
            _muted.Add(m.GetProperty("id").GetInt64());
        MutedChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>不认识的用户（例如已被删除）显示消息中带的名字。</summary>
    public string NameOf(long id, string? fallback = null) =>
        _users.TryGetValue(id, out var u) ? u.FullName : fallback ?? id.ToString(CultureInfo.InvariantCulture);
}
