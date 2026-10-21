using System;
using System.Globalization;

namespace ZulipQQ.Core;

/// <summary>
/// 界面上各处的时间文字。全部使用 InvariantCulture，避免“/”被替换为系统区域的日期分隔符。
/// </summary>
public static class TimeText
{
    /// <summary>聊天中两条消息间隔达到该秒数（30 分钟）时插入时间分隔条。</summary>
    public const long SeparatorGapSeconds = 1800;

    private static readonly string[] WeekdayNames = ["日", "一", "二", "三", "四", "五", "六"];

    /// <summary>
    /// 会话列表的时间。今天：HH:mm；昨天：昨天HH:mm；今年：MM/dd；往年：yyyy/MM/dd。
    /// </summary>
    public static string ListTime(DateTime time, DateTime now)
    {
        if (time.Date == now.Date)
            return time.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (time.Date == now.Date.AddDays(-1))
            return "昨天" + time.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (time.Year == now.Year)
            return time.ToString("MM/dd", CultureInfo.InvariantCulture);
        return time.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 聊天中的时间分隔条。今天：HH:mm；昨天：昨天 HH:mm；7 天以内：星期X HH:mm；其它：yyyy/MM/dd HH:mm。
    /// </summary>
    public static string SeparatorTime(DateTime time, DateTime now)
    {
        var hm = time.ToString("HH:mm", CultureInfo.InvariantCulture);
        var days = (now.Date - time.Date).TotalDays;
        if (days <= 0)
            return hm;
        if (days == 1)
            return "昨天 " + hm;
        if (days < 7)
            return $"星期{WeekdayNames[(int)time.DayOfWeek]} {hm}";
        return time.ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>点击消息后显示的精确时间。同一年：MM-dd HH:mm:ss；否则：yyyy-MM-dd HH:mm:ss。</summary>
    public static string PreciseTime(DateTime time, DateTime now)
    {
        return time.Year == now.Year
            ? time.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>两条相邻消息（Unix 秒）之间是否需要时间分隔条。</summary>
    public static bool NeedsSeparator(long previousTimestamp, long currentTimestamp) =>
        currentTimestamp - previousTimestamp >= SeparatorGapSeconds;

    /// <summary>Unix 秒转为本地时间。</summary>
    public static DateTime FromUnix(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime;
}
