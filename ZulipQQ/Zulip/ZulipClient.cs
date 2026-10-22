using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.Zulip;

/// <summary>
/// Zulip REST 客户端。纯网络层，不引用界面；所有 await 都使用 ConfigureAwait(false)，可以在任意线程同步等待。
/// </summary>
public sealed class ZulipClient : IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private const int MaxRateLimitRetries = 3;

    private static readonly string UserAgent = "ZulipQQ/" + (
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "0.0.0");

    private readonly HttpClient _http;
    // 取文件大小专用：不自动解压。否则 nginx 会对文本类附件 gzip，响应不再带 Content-Length，Range 也被忽略
    private readonly HttpClient _sizeHttp;

    public ZulipClient(Uri realmUrl, string email, string apiKey, bool allowInvalidCertificate)
    {
        RealmUrl = NormalizeRealm(realmUrl);
        Email = email;
        var auth = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(email + ":" + apiKey)));
        _http = CreateHttpClient(RealmUrl, allowInvalidCertificate);
        _http.DefaultRequestHeaders.Authorization = auth;
        _sizeHttp = CreateHttpClient(RealmUrl, allowInvalidCertificate, decompress: false);
        _sizeHttp.DefaultRequestHeaders.Authorization = auth;
    }

    /// <summary>组织地址，以 “/” 结尾。</summary>
    public Uri RealmUrl { get; }

    public string Email { get; }

    public void Dispose()
    {
        _http.Dispose();
        _sizeHttp.Dispose();
    }

    // ---- 登录前（不带认证） ----

    public static async Task<ServerSettingsDto> GetServerSettingsAsync(Uri realm, bool allowInvalidCert, CancellationToken ct = default)
    {
        using var http = CreateHttpClient(NormalizeRealm(realm), allowInvalidCert);
        var root = await SendCoreAsync(http, HttpMethod.Get, "api/v1/server_settings", null, DefaultTimeout, ct).ConfigureAwait(false);
        return ZulipJson.Deserialize<ServerSettingsDto>(root);
    }

    public static async Task<ApiKeyResultDto> FetchApiKeyAsync(Uri realm, string username, string password, bool allowInvalidCert, CancellationToken ct = default)
    {
        using var http = CreateHttpClient(NormalizeRealm(realm), allowInvalidCert);
        var args = new FormBuilder().Add("username", username).Add("password", password);
        var root = await SendCoreAsync(http, HttpMethod.Post, "api/v1/fetch_api_key", args, DefaultTimeout, ct).ConfigureAwait(false);
        return ZulipJson.Deserialize<ApiKeyResultDto>(root);
    }

    // ---- 事件队列 ----

    /// <summary>注册事件队列并取得初始状态。不传 event_types，接收全部事件。</summary>
    public async Task<RegisterResult> RegisterAsync(CancellationToken ct = default)
    {
        var args = new FormBuilder()
            .Add("apply_markdown", true)
            .Add("client_gravatar", false)
            .Add("slim_presence", true)
            .Add("include_subscribers", false)
            .AddJson("client_capabilities", new Dictionary<string, bool>
            {
                ["notification_settings_null"] = true,
                ["bulk_message_deletion"] = true,
                ["user_avatar_url_field_optional"] = false,
                ["stream_typing_notifications"] = true,
                ["user_settings_object"] = true,
                ["linkifier_url_template"] = true,
                ["include_deactivated_groups"] = false,
                ["archived_channels"] = true,
                ["empty_topic_name"] = true,
                ["simplified_presence_events"] = true,
            });
        var root = await SendAsync(HttpMethod.Post, "api/v1/register", args, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        return ZulipJson.Deserialize<RegisterResult>(root);
    }

    /// <summary>长轮询取事件。<paramref name="timeout"/> 应大于服务器的长轮询超时。</summary>
    public async Task<IReadOnlyList<JsonElement>> GetEventsAsync(string queueId, long lastEventId, TimeSpan timeout, CancellationToken ct = default)
    {
        var args = new FormBuilder().Add("queue_id", queueId).Add("last_event_id", lastEventId);
        var root = await SendAsync(HttpMethod.Get, "api/v1/events", args, timeout, ct).ConfigureAwait(false);
        return root.TryGetProperty("events", out var events) ? events.EnumerateArray().ToList() : [];
    }

    public Task DeleteQueueAsync(string queueId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, "api/v1/events", new FormBuilder().Add("queue_id", queueId), TimeSpan.FromSeconds(3), ct);

    // ---- 消息 ----

    public async Task<GetMessagesResult> GetMessagesAsync(MessageQuery q, CancellationToken ct = default)
    {
        var args = new FormBuilder()
            .Add("anchor", q.Anchor)
            .Add("num_before", q.NumBefore)
            .Add("num_after", q.NumAfter)
            .AddJson("narrow", q.Narrow)
            .Add("include_anchor", q.IncludeAnchor)
            .Add("apply_markdown", q.ApplyMarkdown)
            .Add("client_gravatar", false)
            .Add("allow_empty_topic_name", true);
        if (q.AnchorDate is not null)
            args.Add("anchor_date", q.AnchorDate);
        var root = await SendAsync(HttpMethod.Get, "api/v1/messages", args, DefaultTimeout, ct).ConfigureAwait(false);
        return ZulipJson.Deserialize<GetMessagesResult>(root);
    }

    // REST 读取默认把空话题替换为 realm_empty_topic_display_name，而事件队列返回空字符串；
    // 读取消息和话题时都传 allow_empty_topic_name=true，保证同一会话的键一致。

    /// <summary>取单条消息。<paramref name="applyMarkdown"/> 为 false 时 <see cref="MessageDto.Content"/> 是 Markdown 原文。</summary>
    public async Task<MessageDto> GetMessageAsync(long id, bool applyMarkdown = true, CancellationToken ct = default)
    {
        var args = new FormBuilder().Add("apply_markdown", applyMarkdown).Add("allow_empty_topic_name", true);
        var root = await SendAsync(HttpMethod.Get, $"api/v1/messages/{Id(id)}", args, DefaultTimeout, ct).ConfigureAwait(false);
        return ZulipJson.Deserialize<MessageDto>(root.GetProperty("message"));
    }

    public async Task<SendMessageResult> SendMessageAsync(OutgoingMessage m, CancellationToken ct = default)
    {
        var args = new FormBuilder();
        if (m.StreamId is { } streamId)
        {
            args.Add("type", "channel").Add("to", streamId).Add("topic", m.Topic ?? "");
        }
        else
        {
            args.Add("type", "direct").AddJson("to", m.UserIds ?? []);
        }
        args.Add("content", m.Content).Add("read_by_sender", true);
        if (m.QueueId is not null && m.LocalId is not null)
            args.Add("queue_id", m.QueueId).Add("local_id", m.LocalId);

        var root = await SendAsync(HttpMethod.Post, "api/v1/messages", args, DefaultTimeout, ct).ConfigureAwait(false);
        return ZulipJson.Deserialize<SendMessageResult>(root);
    }

    /// <summary>修改消息标志。<paramref name="op"/> 为 add / remove，<paramref name="flag"/> 如 read、starred。</summary>
    public Task UpdateFlagsAsync(IReadOnlyCollection<long> ids, string op, string flag, CancellationToken ct = default)
    {
        var args = new FormBuilder().AddJson("messages", ids).Add("op", op).Add("flag", flag);
        return SendAsync(HttpMethod.Post, "api/v1/messages/flags", args, DefaultTimeout, ct);
    }

    public async Task<FlagsNarrowResult> UpdateFlagsForNarrowAsync(
        IReadOnlyList<NarrowTerm> narrow, string anchor, bool includeAnchor, int numBefore, int numAfter,
        string op, string flag, CancellationToken ct = default)
    {
        var args = new FormBuilder()
            .AddJson("narrow", narrow)
            .Add("anchor", anchor)
            .Add("include_anchor", includeAnchor)
            .Add("num_before", numBefore)
            .Add("num_after", numAfter)
            .Add("op", op)
            .Add("flag", flag);
        var root = await SendAsync(HttpMethod.Post, "api/v1/messages/flags/narrow", args, DefaultTimeout, ct).ConfigureAwait(false);
        return ZulipJson.Deserialize<FlagsNarrowResult>(root);
    }

    /// <summary>添加表情回应。<paramref name="reactionType"/> 为 unicode_emoji / realm_emoji / zulip_extra_emoji。</summary>
    public Task AddReactionAsync(long messageId, string emojiName, string emojiCode, string reactionType, CancellationToken ct = default)
    {
        var args = new FormBuilder().Add("emoji_name", emojiName).Add("emoji_code", emojiCode).Add("reaction_type", reactionType);
        return SendAsync(HttpMethod.Post, $"api/v1/messages/{Id(messageId)}/reactions", args, DefaultTimeout, ct);
    }

    public Task RemoveReactionAsync(long messageId, string emojiName, string emojiCode, string reactionType, CancellationToken ct = default)
    {
        var args = new FormBuilder().Add("emoji_name", emojiName).Add("emoji_code", emojiCode).Add("reaction_type", reactionType);
        return SendAsync(HttpMethod.Delete, $"api/v1/messages/{Id(messageId)}/reactions", args, DefaultTimeout, ct);
    }

    // ---- 文件 ----

    /// <summary>上传文件，不设超时。返回的地址形如 <c>/user_uploads/…</c>。</summary>
    public async Task<UploadResult> UploadFileAsync(Stream stream, string fileName, string contentType, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        var file = new ProgressStreamContent(stream, progress);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(file, "file", fileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/user_uploads") { Content = content };
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var root = await ReadResultAsync(response, ct).ConfigureAwait(false);
        return ZulipJson.Deserialize<UploadResult>(root);
    }

    /// <summary>下载组织内的文件（带认证），写入 <paramref name="target"/>。不设超时。</summary>
    public async Task DownloadAsync(string relativePath, Stream target, IProgress<(long Done, long? Total)>? progress = null, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(RelativeUri(relativePath), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new ZulipApiException((int)response.StatusCode, "HTTP_" + (int)response.StatusCode, $"下载失败：{response.StatusCode}", null);

        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            progress?.Report((done, total));
        }
    }

    /// <summary>
    /// 文件大小。先用 HEAD 读 Content-Length；不支持时改用 <c>Range: bytes=0-0</c> 从 Content-Range 读取总大小；都失败时返回 null。
    /// 已在 Zulip 12.3 上验证：HEAD 返回 Content-Length，Range 请求返回 206 与 Content-Range。
    /// </summary>
    public async Task<long?> HeadSizeAsync(string relativePath, CancellationToken ct = default)
    {
        try
        {
            using (var head = new HttpRequestMessage(HttpMethod.Head, RelativeUri(relativePath)))
            using (var response = await _sizeHttp.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength is { } length)
                    return length;
            }

            using var get = new HttpRequestMessage(HttpMethod.Get, RelativeUri(relativePath));
            get.Headers.Range = new RangeHeaderValue(0, 0);
            using var ranged = await _sizeHttp.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (ranged.Content.Headers.ContentRange?.Length is { } total)
                return total;
            if (ranged.StatusCode == HttpStatusCode.OK)
                return ranged.Content.Headers.ContentLength;
        }
        catch (HttpRequestException)
        {
        }
        return null;
    }

    // ---- 频道与话题 ----

    public async Task<IReadOnlyList<long>> GetSubscribersAsync(long streamId, CancellationToken ct = default)
    {
        var root = await SendAsync(HttpMethod.Get, $"api/v1/streams/{Id(streamId)}/members", null, DefaultTimeout, ct).ConfigureAwait(false);
        return ZulipJson.Deserialize<List<long>>(root.GetProperty("subscribers"));
    }

    /// <summary>频道的话题列表（按最新消息 id 降序）。</summary>
    public async Task<IReadOnlyList<TopicDto>> GetTopicsAsync(long streamId, CancellationToken ct = default)
    {
        var args = new FormBuilder().Add("allow_empty_topic_name", true);
        var root = await SendAsync(HttpMethod.Get, $"api/v1/users/me/{Id(streamId)}/topics", args, DefaultTimeout, ct).ConfigureAwait(false);
        return ZulipJson.Deserialize<List<TopicDto>>(root.GetProperty("topics"));
    }

    public Task SetTopicVisibilityAsync(long streamId, string topic, int policy, CancellationToken ct = default)
    {
        var args = new FormBuilder().Add("stream_id", streamId).Add("topic", topic).Add("visibility_policy", policy);
        return SendAsync(HttpMethod.Post, "api/v1/user_topics", args, DefaultTimeout, ct);
    }

    public Task UpdateSubscriptionPropertiesAsync(IReadOnlyList<SubscriptionPropertyChange> changes, CancellationToken ct = default)
    {
        var data = changes.Select(c => new Dictionary<string, object>
        {
            ["stream_id"] = c.StreamId,
            ["property"] = c.Property,
            ["value"] = c.Value,
        }).ToList();
        var args = new FormBuilder().AddJson("subscription_data", data);
        return SendAsync(HttpMethod.Post, "api/v1/users/me/subscriptions/properties", args, DefaultTimeout, ct);
    }

    /// <summary>
    /// 移动消息、修改话题名或编辑内容（PATCH /messages/{id}）。<paramref name="propagateMode"/> 为 change_one / change_later / change_all。
    /// 超出移动时限时抛出 code 为 MOVE_MESSAGES_TIME_LIMIT_EXCEEDED 的 <see cref="ZulipApiException"/>（一条都没有移动）。
    /// </summary>
    public Task UpdateMessageAsync(long messageId, string? topic = null, long? streamId = null, string? propagateMode = null,
        string? content = null, CancellationToken ct = default)
    {
        var args = new FormBuilder();
        if (topic is not null)
            args.Add("topic", topic);
        if (streamId is { } s)
            args.Add("stream_id", s);
        if (propagateMode is not null)
            args.Add("propagate_mode", propagateMode);
        if (content is not null)
            args.Add("content", content);
        return SendAsync(HttpMethod.Patch, $"api/v1/messages/{Id(messageId)}", args, DefaultTimeout, ct);
    }

    /// <summary>消息的编辑历史，第一项为原始版本。</summary>
    public async Task<IReadOnlyList<MessageHistoryDto>> GetMessageHistoryAsync(long messageId, CancellationToken ct = default)
    {
        var args = new FormBuilder().Add("allow_empty_topic_name", true);
        var root = await SendAsync(HttpMethod.Get, $"api/v1/messages/{Id(messageId)}/history", args, DefaultTimeout, ct).ConfigureAwait(false);
        return ZulipJson.Deserialize<List<MessageHistoryDto>>(root.GetProperty("message_history"));
    }

    /// <summary>已读这条消息、且允许查看已读状态的用户 id（关闭了已读回执的用户不会出现）。</summary>
    public async Task<IReadOnlyList<long>> GetReadReceiptsAsync(long messageId, CancellationToken ct = default)
    {
        var root = await SendAsync(HttpMethod.Get, $"api/v1/messages/{Id(messageId)}/read_receipts", null, DefaultTimeout, ct).ConfigureAwait(false);
        return ZulipJson.Deserialize<List<long>>(root.GetProperty("user_ids"));
    }

    /// <summary>
    /// 正在输入通知。<paramref name="op"/> 为 start / stop；频道话题传 <paramref name="streamId"/> 与 <paramref name="topic"/>，
    /// 私聊传 <paramref name="userIds"/>（收件人，不含自己）。
    /// </summary>
    public Task SendTypingAsync(string op, long? streamId, string? topic, IReadOnlyList<long>? userIds, CancellationToken ct = default)
    {
        var args = new FormBuilder().Add("op", op);
        if (streamId is { } s)
            args.Add("type", "stream").Add("stream_id", s).Add("topic", topic ?? "");
        else
            args.Add("type", "direct").AddJson("to", userIds ?? []);
        return SendAsync(HttpMethod.Post, "api/v1/typing", args, DefaultTimeout, ct);
    }

    /// <summary>删除话题中的消息（一次可能只删除一批），返回是否已全部删除。</summary>
    public async Task<bool> DeleteTopicAsync(long streamId, string topic, CancellationToken ct = default)
    {
        var args = new FormBuilder().Add("topic_name", topic);
        var root = await SendAsync(HttpMethod.Post, $"api/v1/streams/{Id(streamId)}/delete_topic", args, DefaultTimeout, ct)
            .ConfigureAwait(false);
        return !root.TryGetProperty("complete", out var complete) || complete.GetBoolean();
    }

    /// <summary>订阅频道；<paramref name="principals"/> 不为 null 时为这些用户订阅。</summary>
    public Task SubscribeAsync(IReadOnlyList<string> channelNames, IReadOnlyList<long>? principals = null, CancellationToken ct = default)
    {
        var args = new FormBuilder().AddJson("subscriptions", channelNames.Select(n => new Dictionary<string, string> { ["name"] = n }).ToList());
        if (principals is not null)
            args.AddJson("principals", principals);
        return SendAsync(HttpMethod.Post, "api/v1/users/me/subscriptions", args, DefaultTimeout, ct);
    }

    /// <summary>退订频道；<paramref name="principals"/> 不为 null 时为这些用户退订。</summary>
    public Task UnsubscribeAsync(IReadOnlyList<string> channelNames, IReadOnlyList<long>? principals = null, CancellationToken ct = default)
    {
        var args = new FormBuilder().AddJson("subscriptions", channelNames);
        if (principals is not null)
            args.AddJson("principals", principals);
        return SendAsync(HttpMethod.Delete, "api/v1/users/me/subscriptions", args, DefaultTimeout, ct);
    }

    /// <summary>创建频道（Zulip 11 起的 POST /channels/create），返回频道 id。</summary>
    public async Task<long> CreateChannelAsync(string name, string description, bool isPrivate, IReadOnlyList<long> subscribers,
        CancellationToken ct = default)
    {
        var args = new FormBuilder()
            .Add("name", name)
            .Add("description", description)
            .Add("invite_only", isPrivate)
            .AddJson("subscribers", subscribers);
        var root = await SendAsync(HttpMethod.Post, "api/v1/channels/create", args, DefaultTimeout, ct).ConfigureAwait(false);
        return root.GetProperty("id").GetInt64();
    }

    /// <summary>
    /// 修改频道（PATCH /streams/{id}）：<c>new_name</c>、<c>description</c>、<c>is_private</c>、
    /// <c>history_public_to_subscribers</c>，以及 <c>{"new": …, "old": …}</c> 形式的各个 <c>can_*_group</c>。
    /// </summary>
    public Task UpdateChannelAsync(long streamId, FormBuilder args, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Patch, $"api/v1/streams/{Id(streamId)}", args, DefaultTimeout, ct);

    // ---- 定时消息、提醒、常用语、邀请 ----

    /// <summary>创建定时消息（<paramref name="m"/> 的 QueueId、LocalId 不使用），返回定时消息 id。</summary>
    public async Task<long> CreateScheduledMessageAsync(OutgoingMessage m, long deliverAt, CancellationToken ct = default)
    {
        var args = new FormBuilder();
        if (m.StreamId is { } streamId)
            args.Add("type", "stream").Add("to", streamId).Add("topic", m.Topic ?? "");
        else
            args.Add("type", "direct").AddJson("to", m.UserIds ?? []);
        args.Add("content", m.Content).Add("scheduled_delivery_timestamp", deliverAt);
        var root = await SendAsync(HttpMethod.Post, "api/v1/scheduled_messages", args, DefaultTimeout, ct).ConfigureAwait(false);
        return root.GetProperty("scheduled_message_id").GetInt64();
    }

    public Task DeleteScheduledMessageAsync(long id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"api/v1/scheduled_messages/{Id(id)}", null, DefaultTimeout, ct);

    /// <summary>在指定时间提醒自己这条消息（服务器到时发一条私信给自己），返回提醒 id。</summary>
    public async Task<long> CreateReminderAsync(long messageId, long deliverAt, CancellationToken ct = default)
    {
        var args = new FormBuilder().Add("message_id", messageId).Add("scheduled_delivery_timestamp", deliverAt);
        var root = await SendAsync(HttpMethod.Post, "api/v1/reminders", args, DefaultTimeout, ct).ConfigureAwait(false);
        return root.GetProperty("reminder_id").GetInt64();
    }

    /// <summary>新建常用语，返回 id。</summary>
    public async Task<long> CreateSavedSnippetAsync(string title, string content, CancellationToken ct = default)
    {
        var args = new FormBuilder().Add("title", title).Add("content", content);
        var root = await SendAsync(HttpMethod.Post, "api/v1/saved_snippets", args, DefaultTimeout, ct).ConfigureAwait(false);
        return root.GetProperty("saved_snippet_id").GetInt64();
    }

    public Task DeleteSavedSnippetAsync(long id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"api/v1/saved_snippets/{Id(id)}", null, DefaultTimeout, ct);

    /// <summary>
    /// 邀请新成员加入组织（角色为成员）。<paramref name="emails"/> 用逗号或换行分隔；
    /// 有邮箱无效时服务器不发送任何邀请，抛出 code 为 INVITATION_FAILED 的 <see cref="ZulipApiException"/>。
    /// </summary>
    public Task SendInvitesAsync(string emails, IReadOnlyList<long> streamIds, CancellationToken ct = default)
    {
        var args = new FormBuilder()
            .Add("invitee_emails", emails)
            .AddJson("stream_ids", streamIds)
            .Add("invite_as", 400)
            .Add("include_realm_default_subscriptions", false);
        return SendAsync(HttpMethod.Post, "api/v1/invites", args, DefaultTimeout, ct);
    }

    // ---- 用户 ----

    /// <summary>屏蔽（静音）用户。</summary>
    public Task MuteUserAsync(long userId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"api/v1/users/me/muted_users/{Id(userId)}", null, DefaultTimeout, ct);

    public Task UnmuteUserAsync(long userId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"api/v1/users/me/muted_users/{Id(userId)}", null, DefaultTimeout, ct);

    // ---- 草稿 ----

    public async Task<IReadOnlyList<DraftDto>> GetDraftsAsync(CancellationToken ct = default)
    {
        var root = await SendAsync(HttpMethod.Get, "api/v1/drafts", null, DefaultTimeout, ct).ConfigureAwait(false);
        return ZulipJson.Deserialize<List<DraftDto>>(root.GetProperty("drafts"));
    }

    /// <summary>创建草稿，返回服务器分配的 id。</summary>
    public async Task<long> CreateDraftAsync(DraftDto draft, CancellationToken ct = default)
    {
        var args = new FormBuilder().AddJson("drafts", new[] { DraftPayload(draft) });
        var root = await SendAsync(HttpMethod.Post, "api/v1/drafts", args, DefaultTimeout, ct).ConfigureAwait(false);
        return root.GetProperty("ids")[0].GetInt64();
    }

    public Task EditDraftAsync(long id, DraftDto draft, CancellationToken ct = default)
    {
        var args = new FormBuilder().AddJson("draft", DraftPayload(draft));
        return SendAsync(HttpMethod.Patch, $"api/v1/drafts/{Id(id)}", args, DefaultTimeout, ct);
    }

    public Task DeleteDraftAsync(long id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"api/v1/drafts/{Id(id)}", null, DefaultTimeout, ct);

    // ---- 在线状态与设置 ----

    public async Task<PresenceUpdateResult> UpdatePresenceAsync(string status, long lastUpdateId, bool newUserInput, CancellationToken ct = default)
    {
        var args = new FormBuilder()
            .Add("status", status)
            .Add("last_update_id", lastUpdateId)
            .Add("new_user_input", newUserInput)
            .Add("slim_presence", true);
        var root = await SendAsync(HttpMethod.Post, "api/v1/users/me/presence", args, DefaultTimeout, ct).ConfigureAwait(false);
        return ZulipJson.Deserialize<PresenceUpdateResult>(root);
    }

    public Task UpdateSettingsAsync(FormBuilder args, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Patch, "api/v1/settings", args, DefaultTimeout, ct);

    /// <summary>设置个性签名；空字符串表示清除。</summary>
    public Task SetStatusAsync(string text, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "api/v1/users/me/status", new FormBuilder().Add("status_text", text), DefaultTimeout, ct);

    /// <summary>更换自己的头像（multipart）。组织禁止更换时服务器返回错误。</summary>
    public async Task UploadAvatarAsync(Stream stream, string fileName, string contentType, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        var file = new StreamContent(stream);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(file, "file", fileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/users/me/avatar") { Content = content };
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await ReadResultAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>添加提醒词，返回服务器上的完整列表。</summary>
    public async Task<List<string>> AddAlertWordsAsync(IEnumerable<string> words, CancellationToken ct = default)
    {
        var root = await SendAsync(HttpMethod.Post, "api/v1/users/me/alert_words", new FormBuilder().AddJson("alert_words", words.ToList()), DefaultTimeout, ct).ConfigureAwait(false);
        return ZulipJson.Deserialize<List<string>>(root.GetProperty("alert_words"));
    }

    /// <summary>删除提醒词，返回服务器上的完整列表。</summary>
    public async Task<List<string>> RemoveAlertWordsAsync(IEnumerable<string> words, CancellationToken ct = default)
    {
        var root = await SendAsync(HttpMethod.Delete, "api/v1/users/me/alert_words", new FormBuilder().AddJson("alert_words", words.ToList()), DefaultTimeout, ct).ConfigureAwait(false);
        return ZulipJson.Deserialize<List<string>>(root.GetProperty("alert_words"));
    }

    /// <summary>修改组织设置（仅管理员）。</summary>
    public Task UpdateRealmAsync(FormBuilder args, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Patch, "api/v1/realm", args, DefaultTimeout, ct);

    // ---- 内部实现 ----

    /// <summary>发送请求并返回 JSON 根对象。失败时抛出 <see cref="ZulipApiException"/>、<see cref="TimeoutException"/> 或 <see cref="HttpRequestException"/>。</summary>
    public Task<JsonElement> SendAsync(HttpMethod method, string path, FormBuilder? args, TimeSpan timeout, CancellationToken ct) =>
        SendCoreAsync(_http, method, path, args, timeout, ct);

    private static async Task<JsonElement> SendCoreAsync(HttpClient http, HttpMethod method, string path, FormBuilder? args, TimeSpan timeout, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (timeout != Timeout.InfiniteTimeSpan)
                cts.CancelAfter(timeout);

            using var request = BuildRequest(method, path, args);
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"请求超时：{method} {path}");
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxRateLimitRetries)
                {
                    var wait = await RetryAfterAsync(response, ct).ConfigureAwait(false);
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    return await ReadResultAsync(response, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"请求超时：{method} {path}");
                }
            }
        }
    }

    private static HttpRequestMessage BuildRequest(HttpMethod method, string path, FormBuilder? args)
    {
        if (method == HttpMethod.Get || method == HttpMethod.Head)
        {
            var query = args?.ToQueryString();
            return new HttpRequestMessage(method, string.IsNullOrEmpty(query) ? path : path + "?" + query);
        }

        var request = new HttpRequestMessage(method, path);
        if (args is not null)
            request.Content = new FormUrlEncodedContent(args.Pairs);
        return request;
    }

    private static async Task<JsonElement> ReadResultAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(text);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            // 例如 nginx 在 502/504 时返回 HTML
            throw new ZulipApiException(status, "HTTP_" + status, text.Length > 200 ? text[..200] : text, null);
        }

        var isError = root.ValueKind != JsonValueKind.Object
                      || (root.TryGetProperty("result", out var result) && result.GetString() == "error");
        if (!response.IsSuccessStatusCode || isError)
        {
            var code = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
            var msg = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("msg", out var m) ? m.GetString() ?? "" : "";
            throw new ZulipApiException(status, code, msg, root);
        }
        return root;
    }

    private static async Task<TimeSpan> RetryAfterAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
            return delta;
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("retry-after", out var ra) && ra.TryGetDouble(out var seconds))
                return TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 60));
        }
        catch (JsonException)
        {
        }
        return TimeSpan.FromSeconds(1);
    }

    private static HttpClient CreateHttpClient(Uri realm, bool allowInvalidCertificate, bool decompress = true)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = decompress ? DecompressionMethods.All : DecompressionMethods.None,
        };
        if (allowInvalidCertificate)
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        var http = new HttpClient(handler) { BaseAddress = realm, Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return http;
    }

    private static Uri NormalizeRealm(Uri realm)
    {
        var s = realm.GetLeftPart(UriPartial.Path);
        return new Uri(s.EndsWith('/') ? s : s + "/");
    }

    /// <summary><c>/user_uploads/…</c> 转为相对组织地址的路径。</summary>
    private static string RelativeUri(string relativePath) => relativePath.TrimStart('/');

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    private static Dictionary<string, object> DraftPayload(DraftDto d)
    {
        var payload = new Dictionary<string, object>
        {
            ["type"] = d.Type,
            ["to"] = d.To,
            ["topic"] = d.Topic,
            ["content"] = d.Content,
        };
        if (d.Timestamp is { } ts)
            payload["timestamp"] = ts;
        return payload;
    }
}
