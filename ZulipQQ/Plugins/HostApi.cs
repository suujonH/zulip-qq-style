using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        api->GetAccounts = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&GetAccounts;
        api->GetCurrentAccount = (nint)(delegate* unmanaged[Cdecl]<nint, long>)&GetCurrentAccount;
        api->SelectAccount = (nint)(delegate* unmanaged[Cdecl]<nint, long, int>)&SelectAccount;
        api->GetSettings = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&GetSettings;
        api->SetAnnotation = (nint)(delegate* unmanaged[Cdecl]<nint, long, byte*, int>)&SetAnnotation;
        api->Launch = (nint)(delegate* unmanaged[Cdecl]<nint, byte*, byte*, int>)&Launch;
        api->EditMessage = (nint)(delegate* unmanaged[Cdecl]<nint, long, byte*, int>)&EditMessage;
        api->DeleteMessage = (nint)(delegate* unmanaged[Cdecl]<nint, long, int>)&DeleteMessage;
        api->UpdateChannel = (nint)(delegate* unmanaged[Cdecl]<nint, long, byte*, byte*, int>)&UpdateChannel;
        api->UpdateUser = (nint)(delegate* unmanaged[Cdecl]<nint, long, byte*, int, int>)&UpdateUser;
        api->SetSettingsFeedback = (nint)(delegate* unmanaged[Cdecl]<nint, byte*, int>)&SetSettingsFeedback;
        api->PublishResources = (nint)(delegate* unmanaged[Cdecl]<nint, byte*, byte*, int>)&PublishResources;
        api->RequestResource = (nint)(delegate* unmanaged[Cdecl]<nint, byte*, nint, nint, int>)&RequestResource;
        api->ReplyResource = (nint)(delegate* unmanaged[Cdecl]<nint, long, byte*, int>)&ReplyResource;
        api->ReplyPanel = (nint)(delegate* unmanaged[Cdecl]<nint, byte*, int>)&ReplyPanel;
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
    private static int IsLoggedIn(nint ctx) => CallerSession(PluginOf(ctx)) is { IsDisposed: false } ? 1 : 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static long GetLoginUserId(nint ctx) =>
        CallerSession(PluginOf(ctx)) is { IsDisposed: false } session ? session.SelfId : Zq.ErrNotLoggedIn;

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

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetAccounts(nint ctx, nint cb, nint user) => (int)Guard(ctx, null, (_, _) =>
    {
        if (cb == 0)
            return Zq.ErrInvalidArg;
        var accounts = OnUi(() =>
        {
            var sessions = PluginHost.Instance.Sessions.Where(s => !s.IsDisposed).ToList();
            return sessions.Select((s, i) => PluginAccount.From(s, i == 0)).ToList();
        });
        foreach (var account in accounts)
        {
            using var scope = new Utf8Scope();
            var native = account.ToNative(scope);
            ((delegate* unmanaged[Cdecl]<ZqAccount*, nint, void>)cb)(&native, user);
        }
        return accounts.Count;
    }, requireSession: false);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static long GetCurrentAccount(nint ctx) =>
        CallerSession(PluginOf(ctx)) is { IsDisposed: false } session ? session.Account.Id : Zq.ErrNotLoggedIn;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SelectAccount(nint ctx, long accountId) => (int)Guard(ctx, null, (plugin, _) =>
    {
        if (PluginHost.Instance.Sessions.FirstOrDefault(s => s.Account.Id == accountId && !s.IsDisposed) is not { } target)
            return Zq.ErrNotFound;
        plugin.SelectedAccountId = accountId;
        // 回调中调用：本次回调剩余的宿主函数也使用该账号
        if (PluginHost.Instance.IsPluginThread && PluginHost.CallSession is not null)
            PluginHost.CallSession = target;
        return Zq.Ok;
    }, requireSession: false);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetSettings(nint ctx, nint cb, nint user) => (int)Guard(ctx, null, (plugin, _) =>
    {
        if (cb == 0)
            return Zq.ErrInvalidArg;
        InvokeString(cb, plugin.SettingsJson, user);
        return Zq.Ok;
    }, requireSession: false);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SetAnnotation(nint ctx, long messageId, byte* json) => (int)Guard(ctx, "annotate_message", (plugin, session) =>
    {
        if (messageId <= 0)
            return Zq.ErrInvalidArg;
        var text = NativeText.Read((nint)json);
        PluginAnnotation? annotation = null;
        if (!string.IsNullOrWhiteSpace(text))
        {
            annotation = PluginAnnotation.Parse(plugin.Id, text, out var error);
            if (error is not null)
            {
                plugin.AddLog(Zq.LogWarning, HostCategory, $"SetAnnotation：{error}");
                return Zq.ErrInvalidArg;
            }
            var missing = annotation is null ? null
                : annotation.Replace.Length > 0 && !plugin.HasPermission("render_message") ? "render_message"
                : annotation.Actions.Any(a => a.IsFill) && !plugin.HasPermission("fill_composer") ? "fill_composer"
                : annotation.Actions.Any(a => !a.IsFill) && !plugin.HasPermission("launch") ? "launch"
                : null;
            if (missing is not null)
            {
                plugin.AddLog(Zq.LogWarning, HostCategory, $"SetAnnotation：plugin.json 未声明权限 {missing}");
                return Zq.ErrPermission;
            }
        }
        var accountId = session.Account.Id;
        OnUi(() =>
        {
            PluginAnnotationStore.Instance.Set(accountId, messageId, plugin.Id, annotation);
            return true;
        });
        return Zq.Ok;
    });

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Launch(nint ctx, byte* target, byte* args) => (int)Guard(ctx, "launch", (plugin, _) =>
    {
        var targetText = NativeText.Read((nint)target)?.Trim();
        if (string.IsNullOrEmpty(targetText))
            return Zq.ErrInvalidArg;
        // 白名单之外的目标只能放在附加内容的按钮中，由用户点击
        if (!Launcher.IsWhitelisted(plugin, targetText))
        {
            plugin.AddLog(Zq.LogWarning, HostCategory, $"Launch：不在白名单中：{targetText}");
            return Zq.ErrPermission;
        }
        var argsText = NativeText.Read((nint)args) ?? "";
        try
        {
            Launcher.Launch(targetText, argsText);
        }
        catch (ArgumentException)
        {
            return Zq.ErrInvalidArg;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            plugin.AddLog(Zq.LogError, HostCategory, $"Launch：无法打开 {targetText}：{ex.Message}");
            return Zq.ErrNotFound;
        }
        plugin.AddLog(Zq.LogInfo, HostCategory, $"已打开 {Launcher.Describe(targetText, argsText)}");
        return Zq.Ok;
    }, requireSession: false);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int EditMessage(nint ctx, long messageId, byte* markdown) => (int)Guard(ctx, "edit_message", (_, session) =>
    {
        var content = NativeText.Read((nint)markdown);
        if (messageId <= 0 || string.IsNullOrWhiteSpace(content))
            return Zq.ErrInvalidArg;
        session.Client.UpdateMessageAsync(messageId, content: content, ct: session.Token).GetAwaiter().GetResult();
        return Zq.Ok;
    });

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int DeleteMessage(nint ctx, long messageId) => (int)Guard(ctx, "delete_message", (_, session) =>
    {
        if (messageId <= 0)
            return Zq.ErrInvalidArg;
        session.Client.DeleteMessageAsync(messageId, session.Token).GetAwaiter().GetResult();
        return Zq.Ok;
    });

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int UpdateChannel(nint ctx, long streamId, byte* name, byte* description) => (int)Guard(ctx, "manage_channel", (_, session) =>
    {
        var newName = NativeText.Read((nint)name);
        var newDescription = NativeText.Read((nint)description);
        if (streamId <= 0 || (newName is null && newDescription is null) || newName?.Trim().Length == 0)
            return Zq.ErrInvalidArg;
        var args = new FormBuilder();
        if (newName is not null)
            args.Add("new_name", newName.Trim());
        if (newDescription is not null)
            args.Add("description", newDescription);
        session.Client.UpdateChannelAsync(streamId, args, session.Token).GetAwaiter().GetResult();
        return Zq.Ok;
    });

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int UpdateUser(nint ctx, long userId, byte* fullName, int role) => (int)Guard(ctx, "manage_user", (_, session) =>
    {
        var name = NativeText.Read((nint)fullName);
        if (userId <= 0 || (name is null && role == 0) || name?.Trim().Length == 0 || role is not (0 or 100 or 200 or 300 or 400 or 600))
            return Zq.ErrInvalidArg;
        session.Client.UpdateUserAsync(userId, name?.Trim(), role == 0 ? null : role, session.Token).GetAwaiter().GetResult();
        return Zq.Ok;
    });

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SetSettingsFeedback(nint ctx, byte* json) => (int)Guard(ctx, null, (plugin, _) =>
    {
        if (PluginSettingsFeedback.Parse(NativeText.Read((nint)json)) is not { } feedback)
            return Zq.ErrInvalidArg;
        // 不等待：设置窗口可能已经关闭
        Dispatcher.UIThread.Post(() => PluginHost.Instance.RaiseSettingsFeedback(plugin, feedback));
        return Zq.Ok;
    }, requireSession: false);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int PublishResources(nint ctx, byte* type, byte* json) => (int)Guard(ctx, null, (plugin, _) =>
    {
        var typeText = NativeText.Read((nint)type) ?? "";
        if (!PluginResources.IsValidType(typeText))
            return Zq.ErrInvalidArg;
        var text = NativeText.Read((nint)json);
        var resources = string.IsNullOrWhiteSpace(text) ? [] : PluginResources.Parse(plugin.Id, text);
        if (resources is null)
            return Zq.ErrInvalidArg;
        PluginResources.Instance.Publish(plugin.Id, typeText, resources);
        return Zq.Ok;
    }, requireSession: false);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int RequestResource(nint ctx, byte* json, nint cb, nint user) => (int)Guard(ctx, null, (plugin, _) =>
    {
        // 插件线程上等待会卡住发布资源的插件（它的回调也在插件线程上）
        if (PluginHost.Instance.IsPluginThread)
            return Zq.ErrWrongContext;
        JsonObject? obj;
        try
        {
            obj = JsonNode.Parse(NativeText.Read((nint)json) ?? "") as JsonObject;
        }
        catch (JsonException)
        {
            return Zq.ErrInvalidArg;
        }
        if (obj?["resource"] is not JsonObject selection || PluginResourceSelection.From(selection) is not { } target)
            return Zq.ErrInvalidArg;
        if (!plugin.DependsOn(target.PluginId))
        {
            plugin.AddLog(Zq.LogWarning, HostCategory, $"RequestResource：plugin.json 的 dependencies 未声明 {target.PluginId}");
            return Zq.ErrPermission;
        }
        var (code, reply) = PluginResources.Instance.Request(plugin, target, obj["request"]);
        if (code == Zq.Ok && cb != 0)
            InvokeString(cb, reply ?? "", user);
        return code;
    }, requireSession: false);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ReplyResource(nint ctx, long requestId, byte* json) => (int)Guard(ctx, null, (plugin, _) =>
        PluginResources.Instance.Reply(plugin, requestId, NativeText.Read((nint)json) ?? "") ? Zq.Ok : Zq.ErrNotFound,
        requireSession: false);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ReplyPanel(nint ctx, byte* json) => (int)Guard(ctx, null, (_, _) =>
        PluginHost.TryReplyPanel(NativeText.Read((nint)json) ?? "") ? Zq.Ok : Zq.ErrWrongContext, requireSession: false);

    private static NativePlugin? PluginOf(nint ctx) =>
        ctx == 0 ? null : GCHandle.FromIntPtr(ctx).Target as NativePlugin;

    // 统一的检查与异常处理：插件 → UI 线程 → 权限 → 登录状态 → 函数体
    private static long Guard(nint ctx, string? permission, Func<NativePlugin, ZulipSession, long> body,
        bool requireSession = true, [CallerMemberName] string name = "")
    {
        NativePlugin? plugin = null;
        ZulipSession? session = null;
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
            session = CallerSession(plugin);
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
            if (session is not { IsDisposed: false })
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

    // 插件线程上的回调只使用它所属的会话（事件所属的账号）：已退出账号的回调不会用其他账号发送消息；
    // 回调之外（插件自己的线程）使用插件选择的账号，没有选择时为最先登录的账号
    private static ZulipSession? CallerSession(NativePlugin? plugin) =>
        PluginHost.Instance.IsPluginThread && PluginHost.CallSession is { } session
            ? session
            : PluginHost.Instance.DefaultSession(plugin);

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
