using System.Collections.Generic;
using System.Linq;

namespace ZulipQQ.Plugins;

/// <summary>权限的类别：开启确认时分组显示。</summary>
public enum PermissionKind
{
    /// <summary>可以读取的数据。</summary>
    Read,

    /// <summary>可以执行的操作。</summary>
    Act,

    /// <summary>只是声明（宿主无法限制，例如插件自己访问网络），开启时告知用户。</summary>
    Declared,
}

/// <summary>一个权限：id 写在 plugin.json 中，名称显示给用户（只写简略的行为；宿主保证的限制属于内部约定，不在名称中说明）。</summary>
public sealed record PermissionInfo(string Id, string Name, PermissionKind Kind);

/// <summary>
/// 宿主认识的全部权限。插件只能调用声明过的权限对应的宿主函数；以后的 MCP 服务也使用同一套权限与开启确认。
/// </summary>
public static class PermissionCatalog
{
    public static IReadOnlyList<PermissionInfo> All { get; } =
    [
        new("read_message", "读取消息", PermissionKind.Read),
        new("read_contacts", "读取联系人和频道", PermissionKind.Read),
        new("send_message", "发送消息", PermissionKind.Act),
        new("modify_message", "改写发送的消息、回应和标记消息", PermissionKind.Act),
        new("upload_file", "上传文件", PermissionKind.Act),
        new("annotate_message", "在消息下方显示内容", PermissionKind.Act),
        new("render_message", "改写气泡信息", PermissionKind.Act),
        new("fill_composer", "填入输入框", PermissionKind.Act),
        new("edit_message", "编辑消息", PermissionKind.Act),
        new("delete_message", "删除消息", PermissionKind.Act),
        new("manage_channel", "修改频道", PermissionKind.Act),
        new("manage_user", "修改用户", PermissionKind.Act),
        new("launch", "打开链接和程序", PermissionKind.Act),
        new("network", "访问网络", PermissionKind.Declared),
    ];

    private static readonly Dictionary<string, PermissionInfo> ById = All.ToDictionary(p => p.Id);

    public static bool IsKnown(string id) => ById.ContainsKey(id);

    public static PermissionInfo? Get(string id) => ById.GetValueOrDefault(id);

    /// <summary>显示名称；不认识的权限返回 id。</summary>
    public static string NameOf(string id) => ById.TryGetValue(id, out var p) ? p.Name : id;
}
