using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ZulipQQ.Zulip;
using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.Services;

/// <summary>Zulip 用户设置。修改时先乐观更新本地值，再提交服务器；收到 user_settings 事件时同步。</summary>
public sealed class SettingsStore(ZulipSession session)
{
    public UserSettingsDto Zulip { get; private set; } = new();

    /// <summary>某个设置变化，参数为 Zulip 的设置名（如 <c>enter_sends</c>）。</summary>
    public event EventHandler<string>? Changed;

    public void Load(RegisterResult reg) => Zulip = reg.UserSettings;

    /// <summary>修改 Zulip 设置。<paramref name="value"/> 为 bool、int、long 或 string。失败时恢复原值并抛出异常。</summary>
    public async Task SetZulipAsync(string name, object value)
    {
        var old = Zulip;
        Apply(name, JsonSerializer.SerializeToElement(value));
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
                Zulip = old;
                Changed?.Invoke(this, name);
            }
            throw;
        }
    }

    /// <summary>user_settings update 事件。</summary>
    public void OnZulipSettingChanged(string property, JsonElement value)
    {
        Apply(property, value);
        Changed?.Invoke(this, property);
    }

    // 以 JSON 方式替换一个字段：设置名与 DTO 的蛇形属性名一致，不认识的设置不影响结果
    private void Apply(string name, JsonElement value)
    {
        if (JsonSerializer.SerializeToNode(Zulip, ZulipJson.Options) is not JsonObject node)
            return;
        node[name] = JsonNode.Parse(value.GetRawText());
        Zulip = node.Deserialize<UserSettingsDto>(ZulipJson.Options) ?? Zulip;
    }
}
