using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ZulipQQ.Data;
using ZulipQQ.Zulip;
using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.Services;

/// <summary>
/// 用户设置：Zulip 用户设置（修改时先乐观更新本地值，再提交服务器；收到 user_settings 事件时同步）、
/// 提醒词，以及只保存在本机的 <see cref="Local"/>。
/// </summary>
public sealed class SettingsStore(ZulipSession session, SettingRepository localRepository)
{
    public UserSettingsDto Zulip { get; private set; } = new();

    public LocalSettings Local { get; } = new(localRepository);

    /// <summary>提醒词（服务器保存，按添加顺序）。</summary>
    public IReadOnlyList<string> AlertWords { get; private set; } = [];

    /// <summary>某个设置变化，参数为 Zulip 的设置名（如 <c>enter_sends</c>）；空字符串表示全部设置重新加载。</summary>
    public event EventHandler<string>? Changed;

    /// <summary>提醒词列表变化。</summary>
    public event EventHandler? AlertWordsChanged;

    public void Load(RegisterResult reg)
    {
        Zulip = reg.UserSettings;
        AlertWords = reg.AlertWords;
        Changed?.Invoke(this, "");
        AlertWordsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>alert_words 事件：服务器返回完整列表。</summary>
    public void OnAlertWords(List<string> words)
    {
        AlertWords = words;
        AlertWordsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 添加提醒词。结果以按顺序到达的 alert_words 事件为准：响应中的列表可能比之后的事件旧，不使用。
    /// </summary>
    public Task AddAlertWordAsync(string word) => session.Client.AddAlertWordsAsync([word], session.Token);

    /// <summary>删除提醒词；结果同样以 alert_words 事件为准。</summary>
    public Task RemoveAlertWordAsync(string word) => session.Client.RemoveAlertWordsAsync([word], session.Token);

    /// <summary>
    /// 修改 Zulip 设置。<paramref name="value"/> 为 bool、int、long 或 string。
    /// 失败时只恢复这一项的原值（同时进行的其它修改不受影响）并抛出异常。
    /// </summary>
    public async Task SetZulipAsync(string name, object value)
    {
        var old = JsonSerializer.SerializeToNode(Zulip, ZulipJson.Options)?[name]?.DeepClone();
        Apply(name, JsonSerializer.SerializeToNode(value));
        Changed?.Invoke(this, name);

        var args = new FormBuilder();
        switch (value)
        {
            case bool b:
                args.Add(name, b);
                break;
            case int i:
                args.Add(name, i);
                break;
            case long l:
                args.Add(name, l);
                break;
            case string s:
                args.Add(name, s);
                break;
            default:
                throw new ArgumentException($"不支持的设置值类型：{value.GetType().Name}", nameof(value));
        }

        try
        {
            await session.Client.UpdateSettingsAsync(args, session.Token);
        }
        catch
        {
            if (!session.IsDisposed)
            {
                Apply(name, old);
                Changed?.Invoke(this, name);
            }
            throw;
        }
    }

    /// <summary>user_settings update 事件。</summary>
    public void OnZulipSettingChanged(string property, JsonElement value)
    {
        Apply(property, JsonNode.Parse(value.GetRawText()));
        Changed?.Invoke(this, property);
    }

    // 以 JSON 方式替换一个字段：设置名与 DTO 的蛇形属性名一致，不认识的设置不影响结果
    private void Apply(string name, JsonNode? value)
    {
        if (JsonSerializer.SerializeToNode(Zulip, ZulipJson.Options) is not JsonObject node)
            return;
        node[name] = value;
        Zulip = node.Deserialize<UserSettingsDto>(ZulipJson.Options) ?? Zulip;
    }
}
