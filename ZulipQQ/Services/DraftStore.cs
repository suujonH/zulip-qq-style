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

/// <summary>
/// 草稿。Zulip 开启草稿同步时保存在服务器（网页端可能为同一会话产生多个草稿，取最新的一个），
/// 关闭时保存在本地 SQLite。同一会话的操作串行执行。只在 UI 线程上读写。
/// </summary>
public sealed class DraftStore(ZulipSession session, LocalDraftRepository localRepo)
{
    /// <summary>服务器草稿；<see cref="Id"/> 为 0 表示创建请求尚未成功。</summary>
    private sealed class DraftEntry(long id, long timestamp, string content)
    {
        public long Id { get; set; } = id;
        public long Timestamp { get; set; } = timestamp;
        public string Content { get; set; } = content;
    }

    private readonly Dictionary<ConversationKey, List<DraftEntry>> _server = [];
    private readonly Dictionary<ConversationKey, string> _local = [];
    private readonly Dictionary<ConversationKey, SemaphoreSlim> _locks = [];

    /// <summary>当前是否使用服务器草稿（Zulip 的 enable_drafts_synchronization）。</summary>
    public bool UseServer { get; private set; }

    public event EventHandler<ConversationKey>? DraftChanged;

    public void Load(RegisterResult reg)
    {
        _server.Clear();
        _local.Clear();
        UseServer = reg.UserSettings.EnableDraftsSynchronization;
        if (UseServer)
            LoadServer(reg.Drafts);
        else
            foreach (var (key, content) in localRepo.LoadAll())
                _local[key] = content;
    }

    /// <summary>会话的草稿；没有时为 null。</summary>
    public string? Get(ConversationKey key)
    {
        if (!UseServer)
            return _local.GetValueOrDefault(key);
        return _server.TryGetValue(key, out var list) && list.Count > 0 ? list.MaxBy(d => d.Timestamp)!.Content : null;
    }

    /// <summary>保存草稿；去掉首尾空白后为空等同于删除。网络失败只记日志，内存中保留新值。</summary>
    public async Task SetAsync(ConversationKey key, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            await DeleteAsync(key);
            return;
        }

        var gate = Lock(key);
        await gate.WaitAsync();
        try
        {
            if (session.IsDisposed)
                return;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!UseServer)
            {
                _local[key] = text;
                localRepo.Set(key, text, now);
                DraftChanged?.Invoke(this, key);
                return;
            }

            if (!_server.TryGetValue(key, out var list))
                _server[key] = list = [];
            var latest = list.MaxBy(d => d.Timestamp);
            if (latest is null)
                list.Add(latest = new DraftEntry(0, now, text));
            latest.Content = text;
            latest.Timestamp = now;
            DraftChanged?.Invoke(this, key);

            var dto = ToDto(key, text, now);
            if (latest.Id > 0)
            {
                await session.Client.EditDraftAsync(latest.Id, dto, session.Token);
            }
            else
            {
                var id = await session.Client.CreateDraftAsync(dto, session.Token);
                if (session.IsDisposed)
                    return;
                // drafts add 事件可能先于响应到达，已按 id 加入过一次
                list.RemoveAll(d => d != latest && d.Id == id);
                latest.Id = id;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !session.IsDisposed)
        {
            Log.Warn("保存草稿失败", ex);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>删除会话的全部草稿。</summary>
    public async Task DeleteAsync(ConversationKey key)
    {
        var gate = Lock(key);
        await gate.WaitAsync();
        try
        {
            if (session.IsDisposed)
                return;
            if (!UseServer)
            {
                if (_local.Remove(key))
                {
                    localRepo.Delete(key);
                    DraftChanged?.Invoke(this, key);
                }
                return;
            }

            if (!_server.Remove(key, out var list))
                return;
            DraftChanged?.Invoke(this, key);
            foreach (var d in list.Where(d => d.Id > 0))
                await session.Client.DeleteDraftAsync(d.Id, session.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !session.IsDisposed)
        {
            Log.Warn("删除草稿失败", ex);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>drafts 事件（只在服务器模式下处理）。</summary>
    public void OnDraftsEvent(JsonElement ev)
    {
        if (!UseServer)
            return;
        switch (ev.GetProperty("op").GetString())
        {
            case "add":
                foreach (var d in ZulipJson.Deserialize<List<DraftDto>>(ev.GetProperty("drafts")))
                    Upsert(d);
                break;
            case "update":
                Upsert(ZulipJson.Deserialize<DraftDto>(ev.GetProperty("draft")));
                break;
            case "remove":
                if (RemoveById(ev.GetProperty("draft_id").GetInt64()) is { } key)
                    DraftChanged?.Invoke(this, key);
                break;
        }
    }

    /// <summary>
    /// 草稿同步开关变化（设置页修改前调用，或收到 user_settings 事件时调用）。
    /// 关闭同步时 Zulip 会删除服务器上的全部草稿，因此先把每个会话的最新草稿存到本地；
    /// 开启同步时把本地草稿上传，再从服务器重新加载，最后清空本地草稿。
    /// </summary>
    public async Task OnSyncSettingChangedAsync(bool enabled)
    {
        if (enabled == UseServer)
            return;

        HashSet<ConversationKey> affected = [.. _server.Keys, .. _local.Keys];
        if (!enabled)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var key in _server.Keys.ToList())
            {
                if (Get(key) is { } content)
                {
                    _local[key] = content;
                    localRepo.Set(key, content, now);
                }
            }
            _server.Clear();
            UseServer = false;
        }
        else
        {
            foreach (var (key, content) in _local.ToList())
            {
                try
                {
                    await session.Client.CreateDraftAsync(ToDto(key, content, DateTimeOffset.UtcNow.ToUnixTimeSeconds()), session.Token);
                }
                catch (Exception ex) when (!session.IsDisposed)
                {
                    Log.Warn("上传本地草稿失败", ex);
                }
                if (session.IsDisposed)
                    return;
            }
            var drafts = await session.Client.GetDraftsAsync(session.Token);
            if (session.IsDisposed)
                return;
            _server.Clear();
            LoadServer(drafts);
            localRepo.DeleteAll();
            _local.Clear();
            UseServer = true;
            affected.UnionWith(_server.Keys);
        }

        foreach (var key in affected)
            DraftChanged?.Invoke(this, key);
    }

    private void LoadServer(IEnumerable<DraftDto> drafts)
    {
        foreach (var d in drafts)
        {
            if (d.Id is { } id && KeyOf(d) is { } key)
                Entries(key).Add(new DraftEntry(id, d.Timestamp ?? 0, d.Content));
        }
    }

    private void Upsert(DraftDto d)
    {
        if (d.Id is not { } id)
            return;
        // 收件人可能被改变：先从原来的会话中移除
        var oldKey = RemoveById(id);
        if (KeyOf(d) is { } key)
        {
            Entries(key).Add(new DraftEntry(id, d.Timestamp ?? 0, d.Content));
            DraftChanged?.Invoke(this, key);
        }
        if (oldKey is not null && !oldKey.Equals(KeyOf(d)))
            DraftChanged?.Invoke(this, oldKey);
    }

    private ConversationKey? RemoveById(long id)
    {
        foreach (var (key, list) in _server)
        {
            if (list.RemoveAll(d => d.Id == id) == 0)
                continue;
            if (list.Count == 0)
                _server.Remove(key);
            return key;
        }
        return null;
    }

    private List<DraftEntry> Entries(ConversationKey key)
    {
        if (!_server.TryGetValue(key, out var list))
            _server[key] = list = [];
        return list;
    }

    private SemaphoreSlim Lock(ConversationKey key)
    {
        if (!_locks.TryGetValue(key, out var gate))
            _locks[key] = gate = new SemaphoreSlim(1, 1);
        return gate;
    }

    private ConversationKey? KeyOf(DraftDto d) => d.Type switch
    {
        "stream" when d.To.Count > 0 => new TopicKey(d.To[0], d.Topic),
        "private" => DmKey.Create(d.To, session.SelfId),
        _ => null,
    };

    private DraftDto ToDto(ConversationKey key, string content, long timestamp) => key switch
    {
        TopicKey t => new DraftDto
        {
            Type = "stream", To = [t.StreamId], Topic = session.Conversations.GetTopicName(t), Content = content, Timestamp = timestamp,
        },
        DmKey d => new DraftDto
        {
            Type = "private", To = d.IsSelfDm ? [session.SelfId] : [.. d.OtherUserIds], Content = content, Timestamp = timestamp,
        },
        _ => throw new ArgumentException("不支持的会话", nameof(key)),
    };
}
