using System;
using System.Collections.Generic;
using System.Linq;
using ZulipQQ.Models;

namespace ZulipQQ.Services;

/// <summary>
/// 界面开发阶段使用的假数据。接入 Zulip 后由真实数据源替换。
/// 时间均相对于今天生成，保证“今天 / 昨天 / 今年 / 往年”几种时间格式都能看到。
/// </summary>
public static class MockData
{
    // 静态字段按声明顺序初始化，CreateFriends() 用到的字段必须放在 Friends 之前
    private static readonly DateTime Today = DateTime.Today;

    private static readonly string[] Names =
        ["阿杰", "小雨", "Momo", "老张", "橘子", "Kevin", "小鹿", "大熊", "七七", "Echo", "阿木", "糖糖"];

    private static readonly string[] Signatures =
        ["今天也要加油鸭", "", "忙，勿扰", "生活明朗，万物可爱", "在路上", "Stay hungry, stay foolish"];

    public static User CurrentUser { get; } = new(10000, "豆沙团子", "", true);

    public static IReadOnlyList<Conversation> Conversations { get; } =
    [
        new(1, ConversationType.Group, "鸿蒙s21玩家①群", "八千梦", "[动画表情]", Today.AddHours(2).AddMinutes(14),
            UnreadCount: 326, IsPinned: true, IsMuted: true),
        new(2, ConversationType.Friend, "-", "哈士奇", "[表情]", new DateTime(Today.Year, 7, 6, 21, 30, 0),
            IsPinned: true),
        new(3, ConversationType.Group, "DOTA", null, "", new DateTime(2018, 12, 14, 20, 0, 0),
            IsPinned: true, IsMuted: true),
        new(4, ConversationType.System, "群助手", null, "12个群聊有新消息", Today.AddHours(2).AddMinutes(24),
            Notice: ConversationNotice.AtAll),
        new(5, ConversationType.Group, "MICUAPI 售后一群", "小猫", "[图片]", Today.AddHours(2).AddMinutes(6),
            UnreadCount: 150, IsMuted: true, Notice: ConversationNotice.NewFile),
        new(6, ConversationType.Group, "BetterGI v2", "小椰羊³", "[图片]", Today.AddHours(1).AddMinutes(12),
            UnreadCount: 999, IsMuted: true, Notice: ConversationNotice.NewFile),
        new(7, ConversationType.Group, "肉鸽地牢二群", "小剑心", "[动画表情]", Today.AddHours(1).AddMinutes(3),
            UnreadCount: 63, IsMuted: true),
        new(8, ConversationType.Group, "Mirror酱东大院", "阿镜", "晚上好", Today.AddHours(1),
            UnreadCount: 120, IsMuted: true, Draft: "当我喊你老婆时你应该说好的老婆"),
        new(9, ConversationType.Friend, "卿卿", null, "晚安呀~爱你", Today.AddMinutes(27)),
        new(10, ConversationType.Group, "签到 today", "小助手", "今日签到已完成", Today.AddDays(-1).AddHours(23).AddMinutes(1),
            UnreadCount: 3),
        new(11, ConversationType.Friend, "老王", null, "周末一起打球？", Today.AddDays(-3).AddHours(18),
            Notice: ConversationNotice.AtMe, UnreadCount: 1),
    ];

    public static IReadOnlyList<Device> Devices { get; } =
    [
        new(1, "我的手机", true),
    ];

    // 分组定义：(Id, 名称, 在线数, 总数)
    private static readonly (long Id, string Name, int Online, int Total)[] GroupSpecs =
    [
        (1, "我的好友", 16, 43),
        (2, "", 0, 0),
        (3, "-", 12, 21),
        (4, "中专", 4, 17),
        (5, "大学", 7, 23),
        (6, "魔兽世界~", 4, 10),
        (7, "亲朋", 2, 7),
        (8, "初中", 7, 17),
        (9, "Work", 0, 1),
        (10, "游戏", 16, 31),
    ];

    public static IReadOnlyList<FriendGroup> FriendGroups { get; } =
        GroupSpecs.Select(g => new FriendGroup(g.Id, g.Name)).ToList();

    public static IReadOnlyList<Friend> Friends { get; } =
        GroupSpecs.SelectMany(g => CreateFriends(g.Id, g.Online, g.Total)).ToList();

    public static IReadOnlyList<GroupCategory> GroupCategories { get; } =
    [
        new("置顶群聊", [new(1, "鸿蒙s21玩家①群", 500), new(3, "DOTA", 120)]),
        new("我创建的群聊", [new(20, "周末篮球队", 18)]),
        new("我管理的群聊", [new(21, "MICUAPI 售后一群", 1500)]),
        new("我加入的群聊",
        [
            new(22, "BetterGI v2", 2000),
            new(23, "肉鸽地牢二群", 800),
            new(24, "Mirror酱东大院", 650),
            new(25, "签到 today", 300),
        ]),
    ];

    /// <summary>为一个分组生成 total 个好友，前 online 个在线。</summary>
    private static IEnumerable<Friend> CreateFriends(long groupId, int online, int total) =>
        Enumerable.Range(0, total).Select(i =>
        {
            var userId = groupId * 1000 + i;
            var user = new User(userId, $"{Names[(int)(userId % Names.Length)]}{i + 1}",
                Signatures[(int)(userId % Signatures.Length)], i < online);
            return new Friend(user, groupId, i % 4 == 0 ? $"备注{i + 1}" : null);
        });
}
