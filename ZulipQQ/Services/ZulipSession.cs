using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ZulipQQ.Core;
using ZulipQQ.Data;
using ZulipQQ.Zulip;
using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.Services;

public enum ConnectionState
{
    Connected,
    Reconnecting,

    /// <summary>API key 失效（401），需要重新登录。</summary>
    AuthFailed,
}

/// <summary>可以直接显示给用户的错误。</summary>
public sealed class UserFacingException(string message) : Exception(message);

/// <summary>
/// 登录后的会话：持有 Zulip 客户端、各 Store 与事件循环。退出账号时整体销毁。
/// 所有成员只在 UI 线程上使用；会话内的后台任务都使用 <see cref="Token"/>，
/// 并在每次 await 返回后检查 <see cref="IsDisposed"/>，为 true 时立即返回，不再修改任何状态。
/// </summary>
public sealed class ZulipSession : IAsyncDisposable
{
    /// <summary>最低支持 Zulip 11.0。</summary>
    public const int MinFeatureLevel = 421;

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    private readonly CancellationTokenSource _cts = new();
    private string _queueId = "";
    private long _lastEventId;
    private bool _started;

    // 权限计算的输入；收到 realm、user_group 事件或自己的角色变化后重建 Permissions
    private int _selfRole;
    private Dictionary<long, UserGroupDto> _groups = [];
    private Dictionary<string, GroupSettingValue> _realmGroupSettings = [];
    private HashSet<string> _realmGroupSettingNames = [];
    private Dictionary<(string Scope, string Name), bool> _allowEveryone = [];

    private ZulipSession(AccountRow account, ZulipClient client, LocalDb db)
    {
        Account = account;
        Client = client;
        Users = new UserStore(this);
        Channels = new ChannelStore(this);
        Messages = new MessageStore(this);
        Unread = new UnreadStore(this);
        Contacts = new ContactStore(new ContactRepository(db, account.Id), Users);
        Drafts = new DraftStore(this, new LocalDraftRepository(db, account.Id));
        Conversations = new ConversationListStore(this, new ConversationStateRepository(db, account.Id));
        Settings = new SettingsStore(this, new SettingRepository(db, account.Id), new SettingRepository(db, null));
        Chat = new ChatService(this);
        Files = new FileService(this, new DownloadRepository(db, account.Id));
        Registry = new ChatRegistry(this);
        Presence = new PresenceService(this);
        Notifications = new NotificationService(this);
        Emoji = new EmojiService(this, new SettingRepository(db, account.Id));
        Scheduled = new ScheduledMessageStore(this);
        Snippets = new SnippetStore(this);

        Messages.IsKeptOpen = Registry.IsOpen;
        Conversations.ConversationRekeyed += (_, e) => Registry.Rekey(e.Old, e.New);
    }

    /// <summary>当前登录的会话；只有 App 和插件宿主读取，其它地方通过构造函数传入。</summary>
    public static ZulipSession? Current { get; private set; }

    public AccountRow Account { get; private set; }
    public ZulipClient Client { get; }
    public long SelfId { get; private set; }
    public RealmInfo Realm { get; private set; } = null!;
    public PermissionEvaluator Permissions { get; private set; } = null!;

    public UserStore Users { get; }
    public ChannelStore Channels { get; }
    public MessageStore Messages { get; }
    public UnreadStore Unread { get; }
    public ContactStore Contacts { get; }
    public DraftStore Drafts { get; }
    public ConversationListStore Conversations { get; }
    public SettingsStore Settings { get; }
    public ChatService Chat { get; }
    public FileService Files { get; }
    public ChatRegistry Registry { get; }
    public PresenceService Presence { get; }
    public NotificationService Notifications { get; }
    public EmojiService Emoji { get; }
    public ScheduledMessageStore Scheduled { get; }
    public SnippetStore Snippets { get; }

    /// <summary>当前事件队列 id；发送消息时带上，服务器会在对应的 message 事件中返回 local_message_id。</summary>
    public string QueueId => _queueId;

    /// <summary>组织的用户组（含系统组）。</summary>
    public IEnumerable<UserGroupDto> UserGroups => _groups.Values;

    /// <summary>系统组（如 <c>role:members</c>）的 id；不存在时为 null。</summary>
    public long? SystemGroupId(string name) =>
        _groups.Values.FirstOrDefault(g => g.IsSystemGroup && g.Name == name)?.Id;

    /// <summary>系统组的名字；不是系统组时为 null。</summary>
    public string? SystemGroupName(long groupId) =>
        _groups.TryGetValue(groupId, out var g) && g.IsSystemGroup ? g.Name : null;

    /// <summary>组织级权限设置的当前值（设置名不带 realm_ 前缀，如 <c>can_invite_users_group</c>）。</summary>
    public GroupSettingValue? RealmGroupSetting(string name) => _realmGroupSettings.GetValueOrDefault(name);

    /// <summary>权限设置可以选择的组的限制，键为 (范围, 设置名)。</summary>
    public IReadOnlyDictionary<(string Scope, string Name), PermissionSettingInfo> PermissionSettings { get; private set; } =
        new Dictionary<(string, string), PermissionSettingInfo>();

    public ConnectionState Connection { get; private set; } = ConnectionState.Connected;

    public CancellationToken Token => _cts.Token;

    public bool IsDisposed { get; private set; }

    public event EventHandler? ConnectionChanged;

    /// <summary>重新 register 后触发，各界面整体刷新。</summary>
    public event EventHandler? Resynced;

    public event EventHandler? PermissionsChanged;

    /// <summary>
    /// 注册事件队列、加载各 Store、预取最近消息。任何一步失败都抛出异常（由登录窗显示）。
    /// </summary>
    public static async Task<ZulipSession> CreateAsync(AccountRow account, CancellationToken ct)
    {
        var db = LocalDb.Instance ?? throw new InvalidOperationException("数据库尚未打开");
        var client = new ZulipClient(new Uri(account.RealmUrl), account.Email, AccountRepository.UnprotectKey(account.ApiKey),
            account.AllowInvalidCert);
        var session = new ZulipSession(account, client, db);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Token);
            var reg = await client.RegisterAsync(linked.Token);
            if (reg.ZulipFeatureLevel < MinFeatureLevel)
                throw new UserFacingException("服务器版本过低，需要 Zulip 11.0 及以上");

            session.SelfId = reg.UserId;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            new AccountRepository(db).UpdateProfile(account.Id, reg.UserId, reg.FullName, reg.AvatarUrl, now);
            session.Account = account with
            {
                UserId = reg.UserId, FullName = reg.FullName, AvatarUrl = reg.AvatarUrl, LastLoginAt = now,
            };

            session.Contacts.Load();
            session.Conversations.LoadFromDb();
            await session.LoadStateAsync(reg, linked.Token);
            Current = session;
            ImageCache.Shared.Attach(client);
            return session;
        }
        catch
        {
            session.IsDisposed = true;
            session._cts.Cancel();
            client.Dispose();
            throw;
        }
    }

    /// <summary>开始事件循环、在线状态心跳与后台的预览补取。</summary>
    public void Start()
    {
        if (_started || IsDisposed)
            return;
        _started = true;
        _ = RunEventLoopAsync();
        Presence.Start();
        Forget(Conversations.MarkBlockedReadAsync(), "屏蔽会话标记已读");
        Conversations.StartPreviewBackfill();
    }

    /// <summary>
    /// 结束会话：取消所有后台任务，删除服务器上的事件队列（最多等 3 秒，忽略错误），释放客户端。
    /// 插件的会话结束通知由调用方在此之前发出。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (IsDisposed)
            return;
        IsDisposed = true;
        _cts.Cancel();
        Presence.Stop();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await Client.DeleteQueueAsync(_queueId, timeout.Token);
        }
        catch (Exception ex)
        {
            Log.Debug("删除事件队列失败", ex);
        }
        if (Current == this)
        {
            Current = null;
            ImageCache.Shared.Attach(null);
        }
        Client.Dispose();
    }

    /// <summary>在后台执行任务，只记录异常（会话结束后的取消不记录）。</summary>
    public async void Forget(Task task, string description)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
                Log.Warn($"{description}失败", ex);
        }
    }

    internal void OnSelfRoleChanged()
    {
        _selfRole = Users.Get(SelfId)?.Role ?? _selfRole;
        RebuildPermissions();
    }

    /// <summary>user_group 事件：维护用户组成员与子组，然后重建权限。</summary>
    internal void OnUserGroupEvent(JsonElement ev)
    {
        var op = ev.GetProperty("op").GetString();
        if (op == "add")
        {
            var group = ZulipJson.Deserialize<UserGroupDto>(ev.GetProperty("group"));
            _groups[group.Id] = group;
            RebuildPermissions();
            return;
        }

        var id = ev.GetProperty("group_id").GetInt64();
        if (op == "remove")
        {
            _groups.Remove(id);
            RebuildPermissions();
            return;
        }
        if (!_groups.TryGetValue(id, out var g))
            return;

        _groups[id] = op switch
        {
            "update" => ApplyGroupData(g, ev.GetProperty("data")),
            "add_members" => g with { Members = [.. g.Members.Union(Ids(ev, "user_ids"))] },
            "remove_members" => g with { Members = [.. g.Members.Except(Ids(ev, "user_ids"))] },
            "add_subgroups" => g with { DirectSubgroupIds = [.. g.DirectSubgroupIds.Union(Ids(ev, "direct_subgroup_ids"))] },
            "remove_subgroups" => g with { DirectSubgroupIds = [.. g.DirectSubgroupIds.Except(Ids(ev, "direct_subgroup_ids"))] },
            _ => g,
        };
        RebuildPermissions();
    }

    /// <summary>realm 事件：更新组织信息与组织级权限设置。</summary>
    internal void OnRealmEvent(JsonElement ev)
    {
        switch (ev.GetProperty("op").GetString())
        {
            case "update":
                ApplyRealmSetting(ev.GetProperty("property").GetString() ?? "", ev.GetProperty("value"));
                break;
            case "update_dict":
                foreach (var p in ev.GetProperty("data").EnumerateObject())
                    ApplyRealmSetting(p.Name, p.Value);
                break;
            default:
                return;
        }
        RebuildPermissions();
    }

    private void ApplyRealmSetting(string name, JsonElement value)
    {
        Realm.Apply(name, value);
        if (_realmGroupSettingNames.Contains(name) && value.Deserialize<GroupSettingValue>(ZulipJson.Options) is { } setting)
            _realmGroupSettings[name] = setting;
    }

    private static UserGroupDto ApplyGroupData(UserGroupDto g, JsonElement data)
    {
        if (data.TryGetProperty("name", out var name))
            g = g with { Name = name.GetString() ?? g.Name };
        if (data.TryGetProperty("description", out var description))
            g = g with { Description = description.GetString() ?? g.Description };
        if (data.TryGetProperty("deactivated", out var deactivated))
            g = g with { Deactivated = deactivated.GetBoolean() };
        return g;
    }

    private static IEnumerable<long> Ids(JsonElement ev, string name) =>
        ev.GetProperty(name).EnumerateArray().Select(e => e.GetInt64()).ToList();

    private void RebuildPermissions()
    {
        Permissions = new PermissionEvaluator(new PermissionInputs(
            SelfId, _selfRole, _groups, _realmGroupSettings, _allowEveryone, Realm.AllowMessageEditing, Realm.EditLimitSeconds));
        PermissionsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 用 register 的结果加载各 Store，再预取最近消息与未读详情建立会话列表。
    /// 创建会话和重新 register（事件队列失效）时都调用。
    /// </summary>
    private async Task LoadStateAsync(RegisterResult reg, CancellationToken ct)
    {
        _queueId = reg.QueueId;
        _lastEventId = reg.LastEventId;
        Realm = RealmInfo.FromRegister(Client.RealmUrl, reg);
        Users.Load(reg);
        Channels.Load(reg);

        _selfRole = reg.SelfRole();
        _groups = reg.RealmUserGroups.ToDictionary(g => g.Id);
        _realmGroupSettings = reg.RealmGroupSettings();
        _realmGroupSettingNames = reg.PermissionSettingNames("realm").ToHashSet();
        _allowEveryone = reg.AllowEveryoneGroup();
        PermissionSettings = reg.PermissionSettingInfos();
        RebuildPermissions();

        Settings.Load(reg);
        Emoji.LoadRealm(reg.RealmEmoji);
        Drafts.Load(reg);
        Scheduled.Load(reg);
        Snippets.Load(reg);
        Unread.Load(reg.UnreadMsgs);
        Messages.Clear();

        // 最近 1000 条消息：建立预览与会话列表
        var recent = await Client.GetMessagesAsync(new MessageQuery([], "newest", 1000, 0), ct);
        if (IsDisposed)
            return;
        Messages.Seed(recent.Messages);
        Conversations.Bootstrap(recent.Messages);

        // 未读消息详情：区分 @我 与 @全体，找出含文件的消息
        var unread = await Client.GetMessagesAsync(new MessageQuery([Narrow.Is("unread")], "newest", 2000, 0), ct);
        if (IsDisposed)
            return;
        Unread.ApplyDetails(unread.Messages);

        // 不在最近消息中的私聊和未读会话：只知道最大 id，预览稍后补取
        var covered = recent.Messages.Select(m => ConversationKey.FromMessage(m, SelfId)).ToHashSet();
        foreach (var pm in reg.RecentPrivateConversations)
        {
            var key = DmKey.Create(pm.UserIds, SelfId);
            if (!covered.Contains(key))
                Conversations.BootstrapKnown(key, pm.MaxMessageId, "", isExactMax: true);
        }
        foreach (var key in Unread.Keys.Where(k => !covered.Contains(k)).ToList())
            Conversations.BootstrapKnown(key, Unread.MaxId(key), key is TopicKey t ? t.Topic : "");
    }

    private async Task RunEventLoopAsync()
    {
        var backoff = TimeSpan.FromSeconds(1);
        var needResync = false;
        while (!IsDisposed)
        {
            try
            {
                if (needResync)
                {
                    await ResyncAsync();
                    needResync = false;
                }

                var timeout = TimeSpan.FromSeconds(Realm.LongpollTimeoutSeconds + 30);
                var events = await Client.GetEventsAsync(_queueId, _lastEventId, timeout, Token);
                if (IsDisposed)
                    return;
                SetConnection(ConnectionState.Connected);
                backoff = TimeSpan.FromSeconds(1);

                // 必须按顺序逐个应用；单个事件失败不影响后续事件
                foreach (var ev in events)
                {
                    if (IsDisposed)
                        return;
                    _lastEventId = Math.Max(_lastEventId, ev.GetProperty("id").GetInt64());
                    try
                    {
                        await EventRouter.ApplyAsync(this, ev);
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"处理事件失败：{ev.GetRawText()}", ex);
                    }
                }
            }
            catch (OperationCanceledException) when (IsDisposed)
            {
                return;
            }
            catch (ZulipApiException e) when (e.IsBadEventQueue)
            {
                Log.Info("事件队列已失效，重新注册");
                needResync = true;
            }
            catch (ZulipApiException e) when (e.IsAuthError)
            {
                Log.Warn("登录已失效", e);
                SetConnection(ConnectionState.AuthFailed);
                return;
            }
            catch (Exception ex)
            {
                if (IsDisposed)
                    return;
                Log.Warn("事件队列连接失败，稍后重连", ex);
                SetConnection(ConnectionState.Reconnecting);
                try
                {
                    await Task.Delay(backoff, Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
            }
        }
    }

    // 重新 register，各 Store 整体重新加载（会话列表保留内存中的状态，只用新数据补充）
    private async Task ResyncAsync()
    {
        var reg = await Client.RegisterAsync(Token);
        if (IsDisposed)
            return;
        await LoadStateAsync(reg, Token);
        if (IsDisposed)
            return;
        Resynced?.Invoke(this, EventArgs.Empty);
        Forget(Conversations.MarkBlockedReadAsync(), "屏蔽会话标记已读");
    }

    private void SetConnection(ConnectionState state)
    {
        if (Connection == state)
            return;
        Connection = state;
        ConnectionChanged?.Invoke(this, EventArgs.Empty);
    }
}
