using System.Collections.Generic;
using System.Linq;

namespace ZulipQQ.Core;

/// <summary>
/// <c>server_supported_permission_settings</c> 中一个设置的限制：是否允许“无人”、“所有人”组，
/// 以及允许的系统组（空表示不限制）。
/// </summary>
public sealed record PermissionSettingInfo(bool AllowNobody, bool AllowEveryone, IReadOnlyList<string> AllowedSystemGroups)
{
    /// <summary>服务器没有给出该设置的信息时使用：不限制。</summary>
    public static readonly PermissionSettingInfo Unrestricted = new(true, true, []);
}

/// <summary>权限设置下拉框中可选的系统组，按范围从大到小排列。</summary>
public static class SystemGroups
{
    public const string Everyone = "role:everyone";
    public const string Nobody = "role:nobody";

    /// <summary>系统组名与中文显示名。不包含 role:internet（只用于网页公开频道）。</summary>
    public static readonly IReadOnlyList<(string Name, string Text)> All =
    [
        (Everyone, "所有人"),
        ("role:members", "成员"),
        ("role:fullmembers", "正式成员"),
        ("role:moderators", "版主"),
        ("role:administrators", "管理员"),
        ("role:owners", "所有者"),
        (Nobody, "无人"),
    ];

    /// <summary>该设置可以选择的系统组：受“无人 / 所有人”开关限制；AllowedSystemGroups 不为空时只保留其中的组。</summary>
    public static IReadOnlyList<(string Name, string Text)> ChoicesFor(PermissionSettingInfo info) =>
        All.Where(g => (g.Name != Everyone || info.AllowEveryone)
                       && (g.Name != Nobody || info.AllowNobody)
                       && (info.AllowedSystemGroups.Count == 0 || info.AllowedSystemGroups.Contains(g.Name)))
            .ToList();
}
