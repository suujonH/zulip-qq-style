namespace ZulipQQ.Core;

/// <summary>
/// 本地备注（用户、频道、话题）的显示格式。联系人页、群消息设置等管理用的列表同时显示备注和原名，
/// 便于对照；消息、会话标题等只显示备注。
/// </summary>
public static class RemarkFormat
{
    /// <summary>
    /// “备注(原名称)”；没有备注时只显示原名称。备注与原名称相同时不重复显示。
    /// </summary>
    public static string WithOriginal(string? remark, string original) =>
        string.IsNullOrWhiteSpace(remark) || remark == original ? original : $"{remark}({original})";

    /// <summary>去掉首尾空白；空白视为清除（null）。</summary>
    public static string? Normalize(string? remark) =>
        string.IsNullOrWhiteSpace(remark) ? null : remark.Trim();
}
