using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using ZulipQQ.Core;
using ZulipQQ.Models;
using ZulipQQ.Services;
using ZulipQQ.Zulip;
using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.Plugins;

/// <summary>
/// 宿主函数（插件通过 <see cref="ZqHostApi"/> 调用）。插件可以在任何线程上调用，函数同步阻塞：
/// 读取 Store 时在 UI 线程上取快照，网络请求直接同步等待（客户端使用 ConfigureAwait(false)，不会死锁）。
/// 在 UI 线程上调用返回 ZQ_ERR_WRONG_CONTEXT（同步等待 UI 线程会死锁）。.NET 异常全部转换为错误码并写入插件日志。
/// </summary>
internal static unsafe class HostApi
{
    private const string HostCategory = "宿主";

    public static void Fill(ZqHostApi* api, nint ctx)
    {
        api->StructSize = (uint)sizeof(ZqHostApi);
        api->ApiVersion = Zq.ApiVersion;
        api->Ctx = ctx;
        api->AddLog = (nint)(delegate* unmanaged[Cdecl]<nint, int, byte*, byte*, void>)&AddLog;
        api->IsLoggedIn = (nint)(delegate* unmanaged[Cdecl]<nint, int>)&IsLoggedIn;
        api->GetLoginUserId = (nint)(delegate* unmanaged[Cdecl]<nint, long>)&GetLoginUserId;
        api->GetLoginInfo = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&GetLoginInfo;
        api->GetRealmUrl = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&GetRealmUrl;
        api->GetDataDir = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&GetDataDir;
        api->SendChannelMessage = (nint)(delegate* unmanaged[Cdecl]<nint, long, byte*, byte*, long>)&SendChannelMessage;
        api->SendDirectMessage = (nint)(delegate* unmanaged[Cdecl]<nint, long*, int, byte*, long>)&SendDirectMessage;
        api->GetMessage = (nint)(delegate* unmanaged[Cdecl]<nint, long, nint, nint, int>)&GetMessage;
        api->GetMessages = (nint)(delegate* unmanaged[Cdecl]<nint, ZqConversation*, long, int, int, nint, nint, int>)&GetMessages;
        api->GetUsers = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&GetUsers;
        api->GetUser = (nint)(delegate* unmanaged[Cdecl]<nint, long, nint, nint, int>)&GetUser;
        api->GetChannels = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&GetChannels;
        api->GetTopics = (nint)(delegate* unmanaged[Cdecl]<nint, long, nint, nint, int>)&GetTopics;
        api->AddReaction = (nint)(delegate* unmanaged[Cdecl]<nint, long, byte*, int>)&AddReaction;
        api->SetStarred = (nint)(delegate* unmanaged[Cdecl]<nint, long, int, int>)&SetStarred;
        api->MarkRead = (nint)(delegate* unmanaged[Cdecl]<nint, long, int>)&MarkRead;
        api->UploadFile = (nint)(delegate* unmanaged[Cdecl]<nint, byte*, nint, nint, int>)&UploadFile;
        api->SetOutgoingContent = (nint)(delegate* unmanaged[Cdecl]<nint, byte*, int>)&SetOutgoingContent;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void AddLog(nint ctx, int level, byte* category, byte* text)
    {
        try
        {
            PluginOf(ctx)?.AddLog(level, NativeText.Read((nint)category) ?? "", NativeText.Read((nint)text) ?? "");
        }
        catch (Exception ex)
        {
            Log.Warn("插件日志写入失败", ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int IsLoggedIn(nint ctx) => ZulipSession.Current is { IsDisposed: false } ? 1 : 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static long GetLoginUserId(nint ctx) =>
        ZulipSession.Current is { IsDisposed: false } session ? session.SelfId : Zq.ErrNotLoggedIn;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetLoginInfo(nint ctx, nint cb, nint user) => (int)Guard(ctx, null, (_, session) =>
    {
        if (cb == 0)
            return Zq.ErrInvalidArg;
        var self = OnUi(() => session.Users.Get(session.SelfId) is { } u ? PluginUser.From(u, session) : null);
        if (self is null)
            return Zq.ErrNotFound;
        InvokeUser(cb, self, user);
        return Zq.Ok;
    });

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetRealmUrl(nint ctx, nint cb, nint user) => (int)Guard(ctx, null, (_, session) =>
    {
        if (cb == 0)
            return Zq.ErrInvalidArg;
        InvokeString(cb, session.Account.RealmUrl, user);
        return Zq.Ok;
    });

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetDataDir(nint ctx, nint cb, nint user) => (int)Guard(ctx, null, (plugin, _) =>
    {
        if (cb == 0)
            return Zq.ErrInvalidArg;
        var dir = AppPaths.PluginDataDir(plugin.Id);
        Directory.CreateDirectory(dir);
        InvokeString(cb, dir.EndsWith('\\') ? dir : dir + "\\", user);
        return Zq.Ok;
    }, requireSession: false);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static long SendChannelMessage(nint ctx, long streamId, byte* topic, byte* markdown) => Guard(ctx, "send_message", (_, session) =>
    {
        var topicText = NativeText.Read((nint)topic);
        var content = NativeText.Read((nint)markdown);
        if (streamId <= 0 || topicText is null || string.IsNullOrWhiteSpace(content))
            return Zq.ErrInvalidArg;
        return session.Client.SendMessageAsync(new OutgoingMessage(streamId, topicText, null, content), session.Token)
            .GetAwaiter().GetResult().Id;
    });

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static long SendDirectMessage(nint ctx, long* userIds, int count, byte* markdown) => Guard(ctx, "send_message", (_, session) =>
    {
        var ids = NativeText.ReadLongs(userIds, count);
        var content = NativeText.Read((nint)markdown);
        if (ids.Length == 0 || ids.Any(id => id <= 0) || string.IsNullOrWhiteSpace(content))
            return Zq.ErrInvalidArg;
        return session.Client.SendMessageAsync(new OutgoingMessage(null, null, ids.Distinct().ToList(), content), session.Token)
            .GetAwaiter().GetResult().Id;
    });

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetMessage(nint ctx, long messageId, nint cb, nint user) => (int)Guard(ctx, "read_message", (_, session) =>
    {
        if (cb == 0 || messageId <= 0)
            return Zq.ErrInvalidArg;
        var dto = session.Client.GetMessageAsync(messageId, ct: session.Token).GetAwaiter().GetResult();
        var message = OnUi(() => ToPluginMessage(dto, session));
        InvokeMessage(cb, message, user);
        return Zq.Ok;
    });

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetMessages(nint ctx, ZqConversation* conv, long anchor, int numBefore, int numAfter, nint cb, nint user) =>
        (int)Guard(ctx, "read_message", (_, session) =>
        {
            if (cb == 0)
                return Zq.ErrInvalidArg;
            var narrow = new System.Collections.Generic.List<NarrowTerm>();
            if (conv != null)
            {
                if (PluginConversation.FromNative(conv) is not { } c)
                    return Zq.ErrInvalidArg;
                narrow = Narrow.For(c.ToKey(session.SelfId), session.SelfId, c.Topic);
            }
            var anchorText = anchor switch
            {
                0 => "newest",
                -1 => "oldest",
                > 0 => anchor.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => null,
            };
            if (anchorText is null)
                return Zq.ErrInvalidArg;
            var query = new MessageQuery(narrow, anchorText, Math.Clamp(numBefore, 0, 1000), Math.Clamp(numAfter, 0, 1000));
            var result = session.Client.GetMessagesAsync(query, session.Token).GetAwaiter().GetResult();
            var messages = OnUi(() => result.Messages.Select(m => ToPluginMessage(m, session)).ToList());
            foreach (var m in messages)
                InvokeMessage(cb, m, user);
            return messages.Count;
        });

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetUsers(nint ctx, nint cb, nint user) => (int)Guard(ctx, "read_contacts", (_, session) =>
    {
        if (cb == 0)
            return Zq.ErrInvalidArg;
        var users = OnUi(() => session.Users.ActiveHumans(excludeSelf: false).Concat(session.Users.ActiveBots())
            .Select(u => PluginUser.From(u, session)).ToList());
        foreach (var u in users)
            InvokeUser(cb, u, user);
        return users.Count;
    });

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetUser(nint ctx, long userId, nint cb, nint user) => (int)Guard(ctx, "read_contacts", (_, session) =>
    {
        if (cb == 0)
            return Zq.ErrInvalidArg;
        var found = OnUi(() => session.Users.Get(userId) is { } u ? PluginUser.From(u, session) : null);
        if (found is null)
            return Zq.ErrNotFound;
        InvokeUser(cb, found, user);
        return Zq.Ok;
    });

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetChannels(nint ctx, nint cb, nint user) => (int)Guard(ctx, "read_contacts", (_, session) =>
    {
        if (cb == 0)
            return Zq.ErrInvalidArg;
        var channels = OnUi(() => session.Channels.Subscribed().Select(c => (c.StreamId, c.Name, c.Description, c.InviteOnly)).ToList());
        foreach (var c in channels)
        {
            using var scope = new Utf8Scope();
            var native = new ZqChannel
            {
                StructSize = (uint)sizeof(ZqChannel),
                IsPrivate = c.InviteOnly ? 1 : 0,
                StreamId = c.StreamId,
                Name = scope.Str(c.Name),
                Description = scope.Str(c.Description),
            };
            ((delegate* unmanaged[Cdecl]<ZqChannel*, nint, void>)cb)(&native, user);
        }
        return channels.Count;
    });

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetTopics(nint ctx, long streamId, nint cb, nint user) => (int)Guard(ctx, "read_contacts", (_, session) =>
    {
        if (cb == 0)
            return Zq.ErrInvalidArg;
        if (!OnUi(() => session.Channels.Get(streamId) is { IsSubscribed: true }))
            return Zq.ErrNotFound;
        var cached = Dispatcher.UIThread.InvokeAsync(() => session.Channels.GetTopicsAsync(streamId)).GetAwaiter().GetResult();
        // 缓存的列表会在 UI 线程上随新消息修改：先在 UI 线程上复制
        var topics = OnUi(() => cached.Select(t => (t.Name, t.MaxId)).ToList());
        foreach (var (name, maxId) in topics)
        {
            using var scope = new Utf8Scope();
            var native = new ZqTopic { StructSize = (uint)sizeof(ZqTopic), StreamId = streamId, MaxMessageId = maxId, Name = scope.Str(name) };
            ((delegate* unmanaged[Cdecl]<ZqTopic*, nint, void>)cb)(&native, user);
        }
        return topics.Count;
    });

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int AddReaction(nint ctx, long messageId, byte* emojiName) => (int)Guard(ctx, "modify_message", (_, session) =>
    {
        var name = NativeText.Read((nint)emojiName);
        if (messageId <= 0 || string.IsNullOrEmpty(name))
            return Zq.ErrInvalidArg;
        Dispatcher.UIThread.InvokeAsync(session.Emoji.EnsureLoadedAsync).GetAwaiter().GetResult();
        var emoji = OnUi(() => session.Emoji.Catalog.TryGet(name));
        if (emoji is null)
            return Zq.ErrNotFound;
        session.Client.AddReactionAsync(messageId, emoji.Name, emoji.Code, emoji.Type, session.Token).GetAwaiter().GetResult();
        return Zq.Ok;
    });

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SetStarred(nint ctx, long messageId, int starred) => (int)Guard(ctx, "modify_message", (_, session) =>
    {
        if (messageId <= 0)
            return Zq.ErrInvalidArg;
        session.Client.UpdateFlagsAsync([messageId], starred != 0 ? "add" : "remove", "starred", session.Token).GetAwaiter().GetResult();
        return Zq.Ok;
    });

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int MarkRead(nint ctx, long messageId) => (int)Guard(ctx, "modify_message", (_, session) =>
    {
        if (messageId <= 0)
            return Zq.ErrInvalidArg;
        session.Client.UpdateFlagsAsync([messageId], "add", "read", session.Token).GetAwaiter().GetResult();
        return Zq.Ok;
    });

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int UploadFile(nint ctx, byte* localPath, nint cb, nint user) => (int)Guard(ctx, "upload_file", (_, session) =>
    {
        var path = NativeText.Read((nint)localPath);
        if (cb == 0 || string.IsNullOrEmpty(path))
            return Zq.ErrInvalidArg;
        if (!File.Exists(path))
            return Zq.ErrNotFound;
        var result = session.Chat.UploadAsync(path).GetAwaiter().GetResult();
        InvokeString(cb, result.Url, user);
        return Zq.Ok;
    });

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SetOutgoingContent(nint ctx, byte* markdown) => (int)Guard(ctx, "modify_message", (_, _) =>
    {
        var content = NativeText.Read((nint)markdown);
        if (content is null)
            return Zq.ErrInvalidArg;
        return PluginHost.TrySetOutgoingContent(content) ? Zq.Ok : Zq.ErrWrongContext;
    }, requireSession: false);

    private static NativePlugin? PluginOf(nint ctx) =>
        ctx == 0 ? null : GCHandle.FromIntPtr(ctx).Target as NativePlugin;

    // 统一的检查与异常处理：插件 → UI 线程 → 权限 → 登录状态 → 函数体
    private static long Guard(nint ctx, string? permission, Func<NativePlugin, ZulipSession, long> body,
        bool requireSession = true, [CallerMemberName] string name = "")
    {
        NativePlugin? plugin = null;
        try
        {
            plugin = PluginOf(ctx);
            if (plugin is null)
                return Zq.ErrInvalidArg;
            if (Dispatcher.UIThread.CheckAccess())
                return Zq.ErrWrongContext;
            if (permission is not null && !plugin.HasPermission(permission))
            {
                plugin.AddLog(Zq.LogWarning, HostCategory, $"{name}：plugin.json 未声明权限 {permission}");
                return Zq.ErrPermission;
            }
            var session = ZulipSession.Current;
            if (requireSession && session is not { IsDisposed: false })
                return Zq.ErrNotLoggedIn;
            return body(plugin, session!);
        }
        catch (ZulipApiException ex)
        {
            plugin?.AddLog(Zq.LogError, HostCategory, $"{name}：服务器返回错误 {ex.Code}：{ex.Message}");
            return Zq.ErrServer;
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or OperationCanceledException)
        {
            if (ZulipSession.Current is not { IsDisposed: false })
                return Zq.ErrNotLoggedIn;
            plugin?.AddLog(Zq.LogError, HostCategory, $"{name}：网络错误：{ex.Message}");
            return Zq.ErrNetwork;
        }
        catch (Exception ex)
        {
            plugin?.AddLog(Zq.LogError, HostCategory, $"{name}：{ex.Message}");
            Log.Warn($"插件调用宿主函数失败：{name}", ex);
            return Zq.ErrUnknown;
        }
    }

    private static T OnUi<T>(Func<T> f) => Dispatcher.UIThread.Invoke(f);

    // 在 UI 线程上调用：消息快照中的会话名称取自 Store
    private static PluginMessage ToPluginMessage(MessageDto dto, ZulipSession session) =>
        PluginMessage.From(ChatMessage.FromDto(dto, ConversationKey.FromMessage(dto, session.SelfId)), session, historical: true);

    private static void InvokeMessage(nint cb, PluginMessage message, nint user)
    {
        using var scope = new Utf8Scope();
        var native = message.ToNative(scope);
        ((delegate* unmanaged[Cdecl]<ZqMessage*, nint, void>)cb)(&native, user);
    }

    private static void InvokeUser(nint cb, PluginUser u, nint user)
    {
        using var scope = new Utf8Scope();
        var native = u.ToNative(scope);
        ((delegate* unmanaged[Cdecl]<ZqUser*, nint, void>)cb)(&native, user);
    }

    private static void InvokeString(nint cb, string s, nint user)
    {
        using var scope = new Utf8Scope();
        ((delegate* unmanaged[Cdecl]<byte*, nint, void>)cb)((byte*)scope.Str(s), user);
    }
}
