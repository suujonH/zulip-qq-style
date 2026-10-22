using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ZulipQQ.Zulip;
using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.Services;

/// <summary>
/// 常用语（Zulip 的 saved snippets）：register 时载入，saved_snippets 事件增删改。创建与删除等待事件更新。只在 UI 线程上读写。
/// </summary>
public sealed class SnippetStore(ZulipSession session)
{
    private readonly Dictionary<long, SavedSnippetDto> _items = [];

    public event EventHandler? Changed;

    /// <summary>全部常用语，按创建时间先后。</summary>
    public IReadOnlyList<SavedSnippetDto> All => _items.Values.OrderBy(s => s.DateCreated).ThenBy(s => s.Id).ToList();

    public void Load(RegisterResult reg)
    {
        _items.Clear();
        foreach (var s in reg.SavedSnippets)
            _items[s.Id] = s;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void OnEvent(JsonElement ev)
    {
        switch (ev.GetProperty("op").GetString())
        {
            case "add" or "update":
                var snippet = ZulipJson.Deserialize<SavedSnippetDto>(ev.GetProperty("saved_snippet"));
                _items[snippet.Id] = snippet;
                break;
            case "remove":
                _items.Remove(ev.GetProperty("saved_snippet_id").GetInt64());
                break;
            default:
                return;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task CreateAsync(string title, string content) => session.Client.CreateSavedSnippetAsync(title, content, session.Token);

    public Task DeleteAsync(long id) => session.Client.DeleteSavedSnippetAsync(id, session.Token);
}
