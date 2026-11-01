using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using PluginShared;

namespace ModelCenter;

/// <summary>
/// 模型中心（ZulipQQ 内置插件）：在设置中添加 provider（接口类型、EndPoint、API Key），获取模型列表并添加模型、测试模型；
/// 按接口类型把添加的模型发布为对应的资源（见 ModelCenterClient），依赖本插件的其他插件在设置中选择后，
/// 通过宿主把请求交给本插件发送（其他插件看不到 API Key）。
/// </summary>
internal static unsafe class Plugin
{
    private const string FetchButton = "fetch";
    private const string ModelsField = "models";

    [UnmanagedCallersOnly(EntryPoint = "zq_plugin_init", CallConvs = [typeof(CallConvCdecl)])]
    public static int Init(ZqHostApi* api)
    {
        if (!Host.Init(api, "模型中心"))
            return Zq.ErrInvalidArg;
        Publish(Host.GetSettings());
        return Zq.Ok;
    }

    [UnmanagedCallersOnly(EntryPoint = "zq_on_settings_changed", CallConvs = [typeof(CallConvCdecl)])]
    public static int OnSettingsChanged()
    {
        Publish(Host.GetSettings());
        return Zq.Ok;
    }

    // 设置中的“获取模型列表”与每个模型旁的“测试”：values 为这一行当前的值（尚未保存）
    [UnmanagedCallersOnly(EntryPoint = "zq_on_settings_button", CallConvs = [typeof(CallConvCdecl)])]
    public static int OnSettingsButton(byte* json)
    {
        if (Parse(Host.Read((nint)json)) is not { } obj)
            return Zq.ErrInvalidArg;
        var key = JsonUtil.Str(obj, "key");
        long? row = obj["row"] is JsonValue r && r.TryGetValue<long>(out var n) ? n : null;
        var item = JsonUtil.Str(obj, "item");
        var provider = Provider.From(obj["values"] as JsonObject ?? new JsonObject());
        var field = key == FetchButton ? FetchButton : ModelsField;
        if (key != FetchButton && (key != ModelsField || item.Length == 0))
            return Zq.Ok;
        // 测试的结果显示在模型旁的按钮上（宿主显示加载中），获取模型列表的显示在按钮下方
        void Fail(string text)
        {
            if (key == FetchButton)
                Host.SetSettingsText(field, row, text);
            else
                Host.SetSettingsItemResult(field, row, item, false, text);
        }

        if (provider.Endpoint.Length == 0 && provider.Type != ApiTypes.Decisions)
        {
            Fail("请先填写 EndPoint");
            return Zq.Ok;
        }
        Action work;
        if (key == FetchButton)
        {
            if (!provider.CanListModels)
            {
                Fail("只有 OpenRouter 提供判断模型的列表，请手动输入模型名称");
                return Zq.Ok;
            }
            Host.SetSettingsText(field, row, "正在获取模型列表…");
            work = () => FetchModels(provider, row);
        }
        else
            work = () => Test(provider, item, row);
        if (!Host.Post(work))
            Fail("请求过多，请稍后再试");
        return Zq.Ok;
    }

    // 其他插件的请求：{"type": 资源类型, "resource": provider 名称, "item": 模型, "request": {...}, "from": 插件 id}
    [UnmanagedCallersOnly(EntryPoint = "zq_on_resource_request", CallConvs = [typeof(CallConvCdecl)])]
    public static int OnResourceRequest(long requestId, byte* json)
    {
        var obj = Parse(Host.Read((nint)json));
        var type = obj is null ? "" : JsonUtil.Str(obj, "type");
        var name = obj is null ? "" : JsonUtil.Str(obj, "resource");
        var provider = Providers(Host.GetSettings()).FirstOrDefault(p => p.Resource == type && p.Name == name);
        if (provider is null || obj!["request"] is not JsonObject request)
        {
            Host.ReplyResource(requestId, new JsonObject { ["error"] = provider is null ? $"模型中心中没有 {name}" : "请求格式错误" });
            return Zq.Ok;
        }
        var model = JsonUtil.Str(obj, "item");
        var from = JsonUtil.Str(obj, "from");
        var copy = (JsonObject)request.DeepClone();
        if (!Host.Post(() => Answer(requestId, provider, model, copy, from)))
            Host.ReplyResource(requestId, new JsonObject { ["error"] = "模型中心的请求过多，请稍后再试" });
        return Zq.Ok;
    }

    // 有效的 provider（名称与 EndPoint 已填写；同一资源类型中重名的只取第一个）
    private static List<Provider> Providers(JsonObject settings)
    {
        var result = new List<Provider>();
        foreach (var row in (settings["providers"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var provider = Provider.From(row);
            if (provider.IsConfigured && result.All(p => p.Resource != provider.Resource || p.Name != provider.Name))
                result.Add(provider);
        }
        return result;
    }

    // 按资源类型发布添加了模型的 provider：[{"id": 名称, "name": 名称, "items": [模型]}]；没有的类型发布空列表（清除）
    private static void Publish(JsonObject settings)
    {
        var providers = Providers(settings);
        foreach (var resource in ApiTypes.All.Select(ApiTypes.Resource).Distinct())
        {
            var resources = new JsonArray();
            foreach (var p in providers.Where(p => p.Resource == resource && p.Models.Count > 0))
            {
                resources.Add((JsonNode)new JsonObject
                {
                    ["id"] = p.Name, ["name"] = p.Name, ["items"] = new JsonArray([.. p.Models.Select(m => (JsonNode)JsonValue.Create(m))]),
                });
            }
            var code = Host.PublishResources(resource, resources);
            if (code != Zq.Ok)
                Host.Log(Zq.LogWarning, $"发布 {resource} 失败：{code}");
            else if (resources.Count > 0)
                Host.Log(Zq.LogInfo, $"已提供 {resources.Count} 个 provider 的 {resource}");
        }
    }

    // 工作线程
    private static void FetchModels(Provider provider, long? row)
    {
        var models = ModelClient.ListModels(provider, out var error);
        if (models is null)
        {
            Host.SetSettingsText(FetchButton, row, $"获取失败：{error}");
            return;
        }
        var feedback = new JsonObject
        {
            ["key"] = ModelsField, ["options"] = new JsonArray([.. models.Select(m => (JsonNode)JsonValue.Create(m))]),
        };
        if (row is { } r)
            feedback["row"] = r;
        Host.SetSettingsFeedback(feedback);
        Host.SetSettingsText(FetchButton, row, models.Count == 0
            ? "没有获取到模型，可以在下方手动输入模型名称"
            : $"获取到 {models.Count} 个模型");
    }

    // 工作线程：按接口类型发送测试请求，结果显示在模型旁的按钮上（失败的原因含 HTTP 状态码）
    private static void Test(Provider provider, string model, long? row)
    {
        ModelClient.Handle(provider, model, ModelClient.TestRequest(provider.Type), out var error);
        Host.SetSettingsItemResult(ModelsField, row, model, error is null, error switch
        {
            null => null,
            { Status: { } status } => $"HTTP {status}：{error.Message}",
            _ => error.Message,
        });
    }

    // 工作线程
    private static void Answer(long requestId, Provider provider, string model, JsonObject request, string from)
    {
        var reply = ModelClient.Handle(provider, model, request, out var error);
        if (reply is null)
        {
            var failure = new JsonObject { ["error"] = error!.ToString() };
            if (error.Status is { } status)
                failure["status"] = status;
            Host.ReplyResource(requestId, failure);
            Host.Log(Zq.LogError, $"{from} 使用 {provider.Name} / {model} 失败：{error}");
            return;
        }
        Host.ReplyResource(requestId, reply);
        Host.Log(Zq.LogInfo, $"{from} 使用了 {provider.Name} / {model}");
    }

    private static JsonObject? Parse(string? json)
    {
        try
        {
            return string.IsNullOrEmpty(json) ? null : JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
