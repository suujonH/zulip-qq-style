using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using PluginShared;

namespace ModelCenterApi;

/// <summary>模型中心生成的一张图片：<see cref="Url"/> 与 <see cref="Data"/> 有一个不为 null。</summary>
internal sealed record ModelImage(string? Url, byte[]? Data);

/// <summary>
/// 模型中心的客户端：其他插件编译时加入本文件（csproj 中 <c>&lt;Compile Include="..\ModelCenter\Client\*.cs" /&gt;</c>），
/// plugin.json 的 dependencies 声明 <see cref="PluginId"/>，设置项写作
/// <c>{"type": "resource", "plugin": "com.zulipqq.modelcenter", "resource": "model"}</c>，值为选中的 provider 与模型。
/// 请求经宿主交给模型中心发送（本插件看不到 API Key）。所有请求都在工作线程上调用，阻塞到回复或超时；
/// 失败时返回 null，error 为原因（服务器返回错误时含 HTTP 状态码）。
/// </summary>
internal static class ModelCenterClient
{
    public const string PluginId = "com.zulipqq.modelcenter";

    // 资源类型（resource 设置项的 resource），每种对应一组接口类型
    /// <summary>对话模型（Chat Completions、Responses、Claude、Gemini）。请求 {"messages": [{"role", "content"}]}，回复 {"content"}。</summary>
    public const string Chat = "model";

    /// <summary>判断模型（OpenRouter decisions，例如 jev）。请求 {"state", "questions"}，回复 {"answers", "model"}。</summary>
    public const string Decision = "decision_model";

    /// <summary>向量模型。请求 {"input": [文本]}，回复 {"embeddings": [[数字]]}（与 input 顺序相同）。</summary>
    public const string Embedding = "embedding_model";

    /// <summary>图片模型。请求 {"prompt", "size"?}，回复 {"images": [{"url"} 或 {"b64"}]}。</summary>
    public const string Image = "image_model";

    /// <summary>重排序模型。请求 {"query", "documents": [文本], "top_n"?}，回复 {"results": [{"index", "score"}]}（按得分从高到低）。</summary>
    public const string Rerank = "rerank_model";

    /// <summary>语音合成模型。请求 {"text", "voice"?}，回复 {"audio": base64, "content_type"}。</summary>
    public const string Speech = "speech_model";

    /// <summary>语音识别模型。请求 {"audio": base64, "filename"?}，回复 {"text"}。</summary>
    public const string Transcription = "transcription_model";

    /// <summary>设置项 <paramref name="key"/> 中选中的模型；没有选择时为 null。</summary>
    public static JsonObject? Selection(JsonObject settings, string key) =>
        settings[key] is JsonObject obj && JsonUtil.Str(obj, "resource").Length > 0 ? obj : null;

    /// <summary>对话：系统提示词（可以为空）+ 一条用户消息，返回模型回复的文字。</summary>
    public static string? Ask(JsonObject selection, string system, string user, out string? error)
    {
        var messages = new JsonArray();
        if (system.Trim().Length > 0)
            messages.Add((JsonNode)new JsonObject { ["role"] = "system", ["content"] = system });
        messages.Add((JsonNode)new JsonObject { ["role"] = "user", ["content"] = user });
        return Converse(selection, messages, out error);
    }

    /// <summary>对话：messages 为 [{"role": "system" | "user" | "assistant", "content": 文字}]，返回模型回复的文字。</summary>
    public static string? Converse(JsonObject selection, JsonArray messages, out string? error)
    {
        var reply = Request(selection, new JsonObject { ["messages"] = messages.DeepClone() }, out error);
        return reply is null ? null : Field<string>(reply, "content", ref error);
    }

    /// <summary>判断：返回 answers 对象（题目 id → 答案）。</summary>
    public static JsonObject? Decide(JsonObject selection, JsonObject state, JsonObject questions, out string? error)
    {
        var reply = Request(selection, new JsonObject { ["state"] = state.DeepClone(), ["questions"] = questions.DeepClone() }, out error);
        if (reply is null)
            return null;
        if (reply["answers"] is JsonObject answers)
            return answers;
        error = UnknownReply;
        return null;
    }

    /// <summary>向量：返回每段文本的向量（与 <paramref name="input"/> 顺序相同）。</summary>
    public static List<float[]>? Embed(JsonObject selection, IReadOnlyList<string> input, out string? error)
    {
        var request = new JsonObject { ["input"] = new JsonArray([.. input.Select(s => (JsonNode)JsonValue.Create(s))]) };
        var reply = Request(selection, request, out error);
        if (reply?["embeddings"] is not JsonArray embeddings)
        {
            error ??= UnknownReply;
            return null;
        }
        return embeddings.Select(e => (e as JsonArray ?? []).Select(x => x?.GetValue<float>() ?? 0).ToArray()).ToList();
    }

    /// <summary>生成图片；<paramref name="size"/> 为 null 时使用服务器的默认尺寸。</summary>
    public static List<ModelImage>? GenerateImage(JsonObject selection, string prompt, string? size, out string? error)
    {
        var request = new JsonObject { ["prompt"] = prompt };
        if (size is not null)
            request["size"] = size;
        var reply = Request(selection, request, out error);
        if (reply?["images"] is not JsonArray images)
        {
            error ??= UnknownReply;
            return null;
        }
        return images.OfType<JsonObject>().Select(i => new ModelImage(
            JsonUtil.Str(i, "url") is { Length: > 0 } url ? url : null,
            JsonUtil.Str(i, "b64") is { Length: > 0 } b64 ? Convert.FromBase64String(b64) : null)).ToList();
    }

    /// <summary>重排序：返回 (文档序号, 得分)，按得分从高到低。</summary>
    public static List<(int Index, double Score)>? Rank(JsonObject selection, string query, IReadOnlyList<string> documents, int? topN,
        out string? error)
    {
        var request = new JsonObject
        {
            ["query"] = query, ["documents"] = new JsonArray([.. documents.Select(s => (JsonNode)JsonValue.Create(s))]),
        };
        if (topN is { } n)
            request["top_n"] = n;
        var reply = Request(selection, request, out error);
        if (reply?["results"] is not JsonArray results)
        {
            error ??= UnknownReply;
            return null;
        }
        return results.OfType<JsonObject>().Select(r => (r["index"]?.GetValue<int>() ?? -1, r["score"]?.GetValue<double>() ?? 0)).ToList();
    }

    /// <summary>语音合成：返回音频数据与类型（例如 audio/mpeg）；<paramref name="voice"/> 为 null 时使用默认音色。</summary>
    public static (byte[] Audio, string ContentType)? Speak(JsonObject selection, string text, string? voice, out string? error)
    {
        var request = new JsonObject { ["text"] = text };
        if (voice is not null)
            request["voice"] = voice;
        var reply = Request(selection, request, out error);
        if (reply is null || Field<string>(reply, "audio", ref error) is not { } audio)
            return null;
        return (Convert.FromBase64String(audio), JsonUtil.Str(reply, "content_type"));
    }

    /// <summary>语音识别：返回识别出的文字；<paramref name="fileName"/> 的扩展名表示音频格式（例如 a.wav）。</summary>
    public static string? Transcribe(JsonObject selection, byte[] audio, string fileName, out string? error)
    {
        var reply = Request(selection, new JsonObject { ["audio"] = Convert.ToBase64String(audio), ["filename"] = fileName }, out error);
        return reply is null ? null : Field<string>(reply, "text", ref error);
    }

    private const string UnknownReply = "模型中心返回的格式无法识别";

    private static T? Field<T>(JsonObject reply, string name, ref string? error) where T : class
    {
        if (reply[name] is JsonValue v && v.TryGetValue<T>(out var value))
            return value;
        error = UnknownReply;
        return null;
    }

    // 通过宿主交给模型中心；返回回复的 JSON 对象，失败（含回复中的 error）时返回 null
    private static JsonObject? Request(JsonObject selection, JsonObject request, out string? error)
    {
        var (code, reply) = Host.RequestResource(new JsonObject { ["resource"] = selection.DeepClone(), ["request"] = request });
        if (code != Zq.Ok)
        {
            error = code switch
            {
                Zq.ErrNotFound => "选择的模型不可用：请检查模型中心是否开启、模型是否仍在其中",
                Zq.ErrNetwork => "等待模型中心回复超时",
                Zq.ErrPermission => $"plugin.json 的 dependencies 未声明 {PluginId}",
                _ => $"请求失败（{code}）",
            };
            return null;
        }
        JsonObject? obj;
        try
        {
            obj = JsonNode.Parse(reply ?? "") as JsonObject;
        }
        catch (JsonException)
        {
            obj = null;
        }
        if (obj is null)
        {
            error = UnknownReply;
            return null;
        }
        if (JsonUtil.Str(obj, "error") is { Length: > 0 } e)
        {
            error = e;
            return null;
        }
        error = null;
        return obj;
    }
}
