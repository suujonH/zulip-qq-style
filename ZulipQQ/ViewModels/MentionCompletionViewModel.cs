using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using ZulipQQ.Core;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels;

/// <summary>@ 补全的一个候选：用户、用户组、@全体成员 或 @话题参与者。</summary>
public sealed record MentionCandidate(string Display, string SubText, string Insertion, string? AvatarUrl, string AvatarText, string SortKey);

/// <summary>
/// 输入框上方的 @ 补全列表。话题：频道订阅者、未停用的自定义用户组，有权限时加上 all / topic；
/// 私聊：对话参与者。按显示名、备注、邮箱匹配，前缀匹配在前，最多 8 个。
/// </summary>
public partial class MentionCompletionViewModel(ChatViewModel chat, ZulipSession session) : ViewModelBase
{
    private const int MaxItems = 8;

    private static readonly StringComparer NameComparer =
        StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), CompareOptions.None);

    private int _start = -1;
    private int _version;

    public ObservableCollection<MentionCandidate> Items { get; } = [];

    [ObservableProperty]
    public partial int SelectedIndex { get; set; }

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    /// <summary>文字或光标变化后调用：光标前正在输入提及时更新候选，否则关闭。</summary>
    public async void Update(string text, int caret)
    {
        var version = ++_version;
        if (!MentionText.TryGetQuery(text, caret, out var start, out var query))
        {
            Close();
            return;
        }

        IReadOnlyList<MentionCandidate> candidates;
        try
        {
            candidates = await CandidatesAsync();
        }
        catch (Exception ex)
        {
            if (!session.IsDisposed)
                Log.Warn("获取 @ 候选失败", ex);
            return;
        }
        if (version != _version)
            return;

        _start = start;
        var matched = candidates
            .Select(c => (Candidate: c, Rank: Rank(c, query)))
            .Where(x => x.Rank >= 0)
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Candidate.SortKey, NameComparer)
            .Take(MaxItems)
            .Select(x => x.Candidate)
            .ToList();
        CollectionSync.SyncTo(Items, matched);
        SelectedIndex = 0;
        IsOpen = Items.Count > 0;
    }

    public void Close()
    {
        _version++;
        IsOpen = false;
        _start = -1;
    }

    /// <summary>上下移动选中项（循环）。</summary>
    public void Move(int delta)
    {
        if (Items.Count > 0)
            SelectedIndex = (SelectedIndex + delta + Items.Count) % Items.Count;
    }

    /// <summary>把选中的候选插入文字，返回新文字与光标；没有打开时返回 null。</summary>
    public (string Text, int Caret)? Accept(string text, int caret, MentionCandidate? candidate = null)
    {
        candidate ??= SelectedIndex >= 0 && SelectedIndex < Items.Count ? Items[SelectedIndex] : null;
        if (!IsOpen || candidate is null || _start < 0 || _start > caret || caret > text.Length)
            return null;
        var result = MentionText.Replace(text, _start, caret, candidate.Insertion);
        Close();
        return result;
    }

    private int Rank(MentionCandidate c, string query) =>
        MentionText.MatchRank(query, c.Display, c.SortKey, c.SubText);

    private async Task<IReadOnlyList<MentionCandidate>> CandidatesAsync()
    {
        var result = new List<MentionCandidate>();
        IEnumerable<long> userIds;
        if (chat.Key is TopicKey t)
        {
            userIds = await session.Channels.GetSubscribersAsync(t.StreamId);
            if (session.Permissions.CanMentionEveryone)
            {
                result.Add(new MentionCandidate("全体成员", "all · 通知频道中的所有人", "@**all** ", null, "全", "all"));
                result.Add(new MentionCandidate("话题参与者", "topic · 通知参与过此话题的人", "@**topic** ", null, "话", "topic"));
            }
            foreach (var g in session.UserGroups.Where(g => !g.IsSystemGroup && !g.Deactivated))
                result.Add(new MentionCandidate(g.Name, $"用户组 · {g.Members.Count}人", $"@*{g.Name}* ", null, "组", g.Name));
        }
        else
        {
            userIds = ((DmKey)chat.Key).OtherUserIds;
        }

        foreach (var id in userIds.Where(id => id != session.SelfId))
        {
            if (session.Users.Get(id) is not { IsActive: true } user)
                continue;
            var display = session.Contacts.DisplayName(id);
            var sub = display == user.FullName ? user.Email : $"{user.FullName} · {user.Email}";
            result.Add(new MentionCandidate(display, sub, $"@**{user.FullName}|{id}** ", user.AvatarUrl, display, user.FullName));
        }
        return result;
    }
}
