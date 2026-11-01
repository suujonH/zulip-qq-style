using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ModelCenterApi;
using PluginShared;

namespace ModelCenter;

/// <summary>provider 的接口类型（设置 providers 每一行的 type），以及每种接口提供的资源类型。</summary>
internal static class ApiTypes
{
    public const string Chat = "chat";
    public const string Responses = "responses";
    public const string Claude = "claude";
    public const string Gemini = "gemini";
    public const string Decisions = "decisions";
    public const string Embeddings = "embeddings";
    public const string Images = "images";
    public const string Rerank = "rerank";
    public const string Speech = "speech";
    public const string Transcription = "transcription";

    public static readonly string[] All = [Chat, Responses, Claude, Gemini, Decisions, Embeddings, Images, Rerank, Speech, Transcription];

    /// <summary>接口类型提供的资源类型（见 <see cref="ModelCenterClient"/>）。</summary>
    public static string Resource(string type) => type switch
    {
        Decisions => ModelCenterClient.Decision,
        Embeddings => ModelCenterClient.Embedding,
        Images => ModelCenterClient.Image,
        Rerank => ModelCenterClient.Rerank,
        Speech => ModelCenterClient.Speech,
        Transcription => ModelCenterClient.Transcription,
        _ => ModelCenterClient.Chat,
    };

    /// <summary>EndPoint 之后的接口路径（Gemini 与 Decisions 另行处理，为空）。</summary>
    public static string Path(string type) => type switch
    {
        Chat => "/chat/completions",
        Responses => "/responses",
        Claude => "/messages",
        Embeddings => "/embeddings",
        Images => "/images/generations",
        Rerank => "/rerank",
        Speech => "/audio/speech",
        Transcription => "/audio/transcriptions",
        _ => "",
    };
}

/// <summary>请求失败的原因；<see cref="Status"/> 为服务器返回的 HTTP 状态码（没有收到回复时为 null）。</summary>
internal sealed record ModelError(string Message, int? Status = null)
{
    public override string ToString() => Status is { } status ? $"{Message}（HTTP {status}）" : Message;
}

/// <summary>
/// 一个 provider：设置 providers 的一行（name、type、endpoint、api_key、models、headers、body）。
/// </summary>
internal sealed record Provider(string Name, string Type, string Endpoint, string ApiKey, IReadOnlyList<string> Models,
    IReadOnlyList<(string Name, string Value)> Headers, JsonObject? Body)
{
    /// <summary>Decisions 类型的 EndPoint 留空或是 OpenRouter 的其他地址时使用的地址。</summary>
    private const string OpenRouterDecisionsUrl = "https://openrouter.ai/api/alpha/decisions";

    /// <summary>OpenRouter 的判断模型列表（默认的模型列表中没有判断模型）。</summary>
    private const string OpenRouterDecisionModelsUrl = "https://openrouter.ai/api/v1/models?output_modalities=decisions";

    /// <summary>从设置的一行读取；接口类型不认识时为 Chat Completions，额外 Body 不是 JSON 对象时忽略并记录日志。</summary>
    public static Provider From(JsonObject row)
    {
        var headers = new List<(string, string)>();
        foreach (var line in JsonUtil.Str(row, "headers").Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim() is { Length: > 0 } name)
                headers.Add((name, line[(colon + 1)..].Trim()));
        }

        JsonObject? body = null;
        if (JsonUtil.Str(row, "body").Trim() is { Length: > 0 } bodyText)
        {
            try
            {
                body = JsonNode.Parse(bodyText) as JsonObject ?? throw new JsonException("不是 JSON 对象");
            }
            catch (JsonException ex)
            {
                Host.Log(Zq.LogWarning, $"{JsonUtil.Str(row, "name")} 的额外 Body 无效，已忽略：{ex.Message}");
            }
        }
        var type = JsonUtil.Str(row, "type");
        return new Provider(JsonUtil.Str(row, "name").Trim(), ApiTypes.All.Contains(type) ? type : ApiTypes.Chat,
            JsonUtil.Str(row, "endpoint").Trim(), JsonUtil.Str(row, "api_key").Trim(), Strings(row, "models"), headers, body);
    }

    private static List<string> Strings(JsonObject row, string key) => (row[key] as JsonArray ?? []).OfType<JsonValue>()
        .Select(v => v.TryGetValue<string>(out var s) ? s : "").Where(s => s.Length > 0).ToList();

    /// <summary>名称和 EndPoint 已填写（Decisions 的 EndPoint 可以留空）。</summary>
    public bool IsConfigured => Name.Length > 0 && (Endpoint.Length > 0 || Type == ApiTypes.Decisions);

    /// <summary>提供的资源类型。</summary>
    public string Resource => ApiTypes.Resource(Type);

    /// <summary>接口地址：去掉末尾的 / 和接口路径（兼容填写完整地址，例如 …/chat/completions）。</summary>
    public string BaseUrl
    {
        get
        {
            var url = Endpoint.TrimEnd('/');
            var path = ApiTypes.Path(Type);
            return path.Length > 0 && url.EndsWith(path, StringComparison.OrdinalIgnoreCase) ? url[..^path.Length] : url;
        }
    }

    /// <summary>请求 <paramref name="model"/> 的地址。</summary>
    public string Url(string model) => Type switch
    {
        ApiTypes.Gemini => $"{BaseUrl}/models/{Uri.EscapeDataString(model)}:generateContent",
        ApiTypes.Decisions => DecisionsUrl,
        _ => BaseUrl + ApiTypes.Path(Type),
    };

    // EndPoint 留空、或是 OpenRouter 但不是 decisions 地址（例如对话的 https://openrouter.ai/api/v1）时自动补全
    private string DecisionsUrl => Endpoint.Length == 0 || (IsOpenRouter && !Endpoint.TrimEnd('/').EndsWith("/decisions", StringComparison.OrdinalIgnoreCase))
        ? OpenRouterDecisionsUrl
        : Endpoint;

    private bool IsOpenRouter => Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri)
        && (uri.Host.Equals("openrouter.ai", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".openrouter.ai", StringComparison.OrdinalIgnoreCase));

    /// <summary>可以获取模型列表：Decisions 只有 OpenRouter 有模型列表（EndPoint 留空或是 openrouter.ai）。</summary>
    public bool CanListModels => Type != ApiTypes.Decisions || Endpoint.Length == 0 || IsOpenRouter;

    public string ModelsUrl => Type switch
    {
        ApiTypes.Gemini => BaseUrl + "/models?pageSize=1000",
        ApiTypes.Decisions => OpenRouterDecisionModelsUrl,
        _ => BaseUrl + "/models",
    };
}

/// <summary>
/// 按接口类型发送请求（同步，在工作线程上调用）。<see cref="Handle"/> 接收 <see cref="ModelCenterClient"/> 约定的请求，
/// 转换为对应接口的格式，再把服务器的回复转换为约定的回复。
/// </summary>
internal static class ModelClient
{
    // 短于宿主等待回复的 120 秒，超时时其他插件能收到原因
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(100) };

    private static readonly ModelError BadRequest = new("请求格式错误");

    /// <summary>处理一个请求：返回约定格式的回复；失败时返回 null，<paramref name="error"/> 为原因。</summary>
    public static JsonObject? Handle(Provider provider, string model, JsonObject request, out ModelError? error) => provider.Type switch
    {
        ApiTypes.Chat => Converse(provider, model, request, ChatBody, ChatText, out error),
        ApiTypes.Responses => Converse(provider, model, request, ResponsesBody, ResponsesText, out error),
        ApiTypes.Claude => Converse(provider, model, request, ClaudeBody, ClaudeText, out error),
        ApiTypes.Gemini => Converse(provider, model, request, GeminiBody, GeminiText, out error),
        ApiTypes.Decisions => Decide(provider, model, request, out error),
        ApiTypes.Embeddings => Embed(provider, model, request, out error),
        ApiTypes.Images => GenerateImage(provider, model, request, out error),
        ApiTypes.Rerank => Rerank(provider, model, request, out error),
        ApiTypes.Speech => Speak(provider, model, request, out error),
        ApiTypes.Transcription => Transcribe(provider, model, request, out error),
        _ => Fail(BadRequest, out error),
    };

    /// <summary>测试用的请求：对话类发送“你好”，其他类型各发送一个最简单的请求。</summary>
    public static JsonObject TestRequest(string type) => ApiTypes.Resource(type) switch
    {
        ModelCenterClient.Decision => new JsonObject
        {
            ["state"] = new JsonObject
            {
                ["latest_messages"] = new JsonArray((JsonNode)new JsonObject { ["from"] = "other", ["text"] = "Could you send me the report by Friday?" }),
            },
            ["questions"] = new JsonObject
            {
                ["test"] = new JsonObject
                {
                    ["type"] = "noul",
                    ["instructions"] = "In latest_messages, does the other person ask the user to do something?",
                    ["criteria"] = new JsonObject { ["true"] = "They ask the user to do something.", ["false"] = "They ask for nothing." },
                },
            },
        },
        ModelCenterClient.Embedding => new JsonObject { ["input"] = new JsonArray("你好") },
        ModelCenterClient.Image => new JsonObject { ["prompt"] = "一只坐在窗台上的橘猫" },
        ModelCenterClient.Rerank => new JsonObject { ["query"] = "水果", ["documents"] = new JsonArray("苹果是一种水果", "今天天气很好") },
        ModelCenterClient.Speech => new JsonObject { ["text"] = "你好" },
        ModelCenterClient.Transcription => new JsonObject { ["audio"] = Convert.ToBase64String(TestWav()), ["filename"] = "test.wav" },
        _ => new JsonObject { ["messages"] = new JsonArray((JsonNode)new JsonObject { ["role"] = "user", ["content"] = "你好" }) },
    };

    /// <summary>获取模型列表（按名称排序）；失败时返回 null，<paramref name="error"/> 为原因。</summary>
    public static List<string>? ListModels(Provider provider, out ModelError? error)
    {
        using var request = Create(HttpMethod.Get, provider.ModelsUrl, provider);
        AddHeaders(request, provider);
        var json = SendJson(request, out error);
        if (json is null)
            return null;
        // OpenAI 兼容与 Claude 为 data[].id；Gemini 为 models[].name（“models/gemini-…”）；
        // Decisions 只取 architecture.output_modalities 含 decisions 的（只有它们能调用 decisions 接口）
        List<string>? ids = json["data"] is JsonArray data
            ? data.OfType<JsonObject>()
                .Where(m => provider.Type != ApiTypes.Decisions
                            || (m["architecture"]?["output_modalities"] as JsonArray ?? []).Any(o => o?.ToString() == "decisions"))
                .Select(m => JsonUtil.Str(m, "id")).ToList()
            : json["models"] is JsonArray models
                ? models.OfType<JsonObject>().Select(m => JsonUtil.Str(m, "name")).Select(n => n.StartsWith("models/") ? n[7..] : n).ToList()
                : null;
        if (ids is null)
            return Fail<List<string>>(new ModelError($"无法识别模型列表：{Truncate(json.ToJsonString())}"), out error);
        return ids.Where(id => id.Length > 0).Distinct().Order(StringComparer.Ordinal).ToList();
    }

    // ---------- 对话 ----------

    // messages 转换为接口的请求体、从回复中取出文字
    private static JsonObject? Converse(Provider provider, string model, JsonObject request, Func<string, JsonArray, JsonObject> body,
        Func<JsonObject, string?> text, out ModelError? error)
    {
        if (request["messages"] is not JsonArray messages)
            return Fail(BadRequest, out error);
        var json = PostJson(provider, provider.Url(model), body(model, messages), out error);
        if (json is null)
            return null;
        if (text(json) is { } content)
            return new JsonObject { ["content"] = content };
        return Fail(new ModelError($"无法识别模型的回复：{Truncate(json.ToJsonString())}"), out error);
    }

    private static JsonObject ChatBody(string model, JsonArray messages)
    {
        var body = new JsonObject { ["messages"] = messages.DeepClone() };
        if (model.Length > 0)
            body["model"] = model;
        return body;
    }

    private static string? ChatText(JsonObject json) =>
        json["choices"]?[0]?["message"]?["content"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static JsonObject ResponsesBody(string model, JsonArray messages) =>
        new() { ["model"] = model, ["input"] = messages.DeepClone() };

    // output[] 中 message 的 output_text
    private static string? ResponsesText(JsonObject json)
    {
        if (JsonUtil.Str(json, "output_text") is { Length: > 0 } s)
            return s;
        var parts = (json["output"] as JsonArray ?? []).OfType<JsonObject>()
            .SelectMany(o => (o["content"] as JsonArray ?? []).OfType<JsonObject>())
            .Where(c => JsonUtil.Str(c, "type") == "output_text").Select(c => JsonUtil.Str(c, "text")).ToList();
        return parts.Count > 0 ? string.Concat(parts) : null;
    }

    // system 消息放到 system 字段；max_tokens 是必填项（可以用额外 Body 覆盖）
    private static JsonObject ClaudeBody(string model, JsonArray messages)
    {
        var body = new JsonObject { ["model"] = model, ["max_tokens"] = 4096 };
        var system = string.Join("\n\n", Messages(messages).Where(m => m.Role == "system").Select(m => m.Content));
        if (system.Length > 0)
            body["system"] = system;
        body["messages"] = new JsonArray([.. Messages(messages).Where(m => m.Role != "system")
            .Select(m => (JsonNode)new JsonObject { ["role"] = m.Role == "assistant" ? "assistant" : "user", ["content"] = m.Content })]);
        return body;
    }

    private static string? ClaudeText(JsonObject json)
    {
        var parts = (json["content"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(c => JsonUtil.Str(c, "type") == "text").Select(c => JsonUtil.Str(c, "text")).ToList();
        return parts.Count > 0 ? string.Concat(parts) : null;
    }

    // system 消息放到 systemInstruction；assistant 对应 model
    private static JsonObject GeminiBody(string model, JsonArray messages)
    {
        var body = new JsonObject();
        var system = string.Join("\n\n", Messages(messages).Where(m => m.Role == "system").Select(m => m.Content));
        if (system.Length > 0)
            body["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray((JsonNode)new JsonObject { ["text"] = system }) };
        body["contents"] = new JsonArray([.. Messages(messages).Where(m => m.Role != "system").Select(m => (JsonNode)new JsonObject
        {
            ["role"] = m.Role == "assistant" ? "model" : "user",
            ["parts"] = new JsonArray((JsonNode)new JsonObject { ["text"] = m.Content }),
        })]);
        return body;
    }

    private static string? GeminiText(JsonObject json)
    {
        var parts = (json["candidates"]?[0]?["content"]?["parts"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(p => JsonUtil.Str(p, "text")).ToList();
        return parts.Count > 0 ? string.Concat(parts) : null;
    }

    private static IEnumerable<(string Role, string Content)> Messages(JsonArray messages) =>
        messages.OfType<JsonObject>().Select(m => (JsonUtil.Str(m, "role"), JsonUtil.Str(m, "content")));

    // ---------- 其他类型 ----------

    // OpenRouter decisions：POST {"model", "state", "questions"}
    private static JsonObject? Decide(Provider provider, string model, JsonObject request, out ModelError? error)
    {
        if (request["state"] is not JsonObject state || request["questions"] is not JsonObject questions)
            return Fail(BadRequest, out error);
        var body = new JsonObject { ["model"] = model, ["state"] = state.DeepClone(), ["questions"] = questions.DeepClone() };
        var json = PostJson(provider, provider.Url(model), body, out error);
        if (json is null)
            return null;
        if (json["answers"] is JsonObject answers)
            return new JsonObject { ["answers"] = answers.DeepClone(), ["model"] = json["model"]?.DeepClone() };
        return Fail(new ModelError($"无法识别分析模型的回复：{Truncate(json.ToJsonString())}"), out error);
    }

    // POST /embeddings {"model", "input"} → data[].embedding（按 index 排序）
    private static JsonObject? Embed(Provider provider, string model, JsonObject request, out ModelError? error)
    {
        if (request["input"] is not JsonArray input || input.Count == 0)
            return Fail(BadRequest, out error);
        var json = PostJson(provider, provider.Url(model), new JsonObject { ["model"] = model, ["input"] = input.DeepClone() }, out error);
        if (json is null)
            return null;
        if (json["data"] is not JsonArray data)
            return Fail(new ModelError($"无法识别向量模型的回复：{Truncate(json.ToJsonString())}"), out error);
        var embeddings = data.OfType<JsonObject>().OrderBy(d => d["index"]?.GetValue<int>() ?? 0)
            .Select(d => d["embedding"]?.DeepClone()).ToArray();
        return new JsonObject { ["embeddings"] = new JsonArray(embeddings) };
    }

    // POST /images/generations {"model", "prompt", "n", "size"?} → data[].url 或 data[].b64_json
    private static JsonObject? GenerateImage(Provider provider, string model, JsonObject request, out ModelError? error)
    {
        if (JsonUtil.Str(request, "prompt") is not { Length: > 0 } prompt)
            return Fail(BadRequest, out error);
        var body = new JsonObject { ["model"] = model, ["prompt"] = prompt, ["n"] = 1 };
        if (JsonUtil.Str(request, "size") is { Length: > 0 } size)
            body["size"] = size;
        var json = PostJson(provider, provider.Url(model), body, out error);
        if (json is null)
            return null;
        if (json["data"] is not JsonArray data)
            return Fail(new ModelError($"无法识别图片模型的回复：{Truncate(json.ToJsonString())}"), out error);
        var images = data.OfType<JsonObject>().Select(d => (JsonNode)(JsonUtil.Str(d, "url") is { Length: > 0 } url
            ? new JsonObject { ["url"] = url }
            : new JsonObject { ["b64"] = JsonUtil.Str(d, "b64_json") })).ToArray();
        return new JsonObject { ["images"] = new JsonArray(images) };
    }

    // POST /rerank {"model", "query", "documents", "top_n"?} → results[]{index, relevance_score}（Jina、Cohere、SiliconFlow 等）
    private static JsonObject? Rerank(Provider provider, string model, JsonObject request, out ModelError? error)
    {
        if (JsonUtil.Str(request, "query") is not { Length: > 0 } query || request["documents"] is not JsonArray documents)
            return Fail(BadRequest, out error);
        var body = new JsonObject { ["model"] = model, ["query"] = query, ["documents"] = documents.DeepClone() };
        if (request["top_n"] is JsonValue topN)
            body["top_n"] = topN.DeepClone();
        var json = PostJson(provider, provider.Url(model), body, out error);
        if (json is null)
            return null;
        if (json["results"] is not JsonArray results)
            return Fail(new ModelError($"无法识别重排序模型的回复：{Truncate(json.ToJsonString())}"), out error);
        var items = results.OfType<JsonObject>()
            .Select(r => (Index: r["index"]?.GetValue<int>() ?? -1, Score: (r["relevance_score"] ?? r["score"])?.GetValue<double>() ?? 0))
            .OrderByDescending(r => r.Score)
            .Select(r => (JsonNode)new JsonObject { ["index"] = r.Index, ["score"] = r.Score }).ToArray();
        return new JsonObject { ["results"] = new JsonArray(items) };
    }

    // POST /audio/speech {"model", "input", "voice"} → 音频数据
    private static JsonObject? Speak(Provider provider, string model, JsonObject request, out ModelError? error)
    {
        if (JsonUtil.Str(request, "text") is not { Length: > 0 } text)
            return Fail(BadRequest, out error);
        var voice = JsonUtil.Str(request, "voice") is { Length: > 0 } v ? v : "alloy";
        using var http = Create(HttpMethod.Post, provider.Url(model), provider);
        http.Content = JsonContent(Merge(new JsonObject { ["model"] = model, ["input"] = text, ["voice"] = voice }, provider));
        AddHeaders(http, provider);
        if (Send(http, out var contentType, out error) is not { } audio)
            return null;
        return new JsonObject { ["audio"] = Convert.ToBase64String(audio), ["content_type"] = contentType };
    }

    // POST /audio/transcriptions（multipart：file、model）→ {"text"}
    private static JsonObject? Transcribe(Provider provider, string model, JsonObject request, out ModelError? error)
    {
        byte[] audio;
        try
        {
            audio = Convert.FromBase64String(JsonUtil.Str(request, "audio"));
        }
        catch (FormatException)
        {
            return Fail(BadRequest, out error);
        }
        if (audio.Length == 0)
            return Fail(BadRequest, out error);
        var fileName = JsonUtil.Str(request, "filename") is { Length: > 0 } f ? f : "audio.wav";
        var form = new MultipartFormDataContent { { new ByteArrayContent(audio), "file", fileName }, { new StringContent(model), "model" } };
        using var http = Create(HttpMethod.Post, provider.Url(model), provider);
        http.Content = form;
        AddHeaders(http, provider);
        var json = SendJson(http, out error);
        if (json is null)
            return null;
        if (json["text"] is JsonValue t && t.TryGetValue<string>(out var text))
            return new JsonObject { ["text"] = text };
        return Fail(new ModelError($"无法识别语音识别模型的回复：{Truncate(json.ToJsonString())}"), out error);
    }

    // 1 秒、16 kHz、16 位单声道的 440 Hz 提示音（WAV）
    private static byte[] TestWav()
    {
        const int rate = 16000;
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write("RIFF"u8);
        w.Write(36 + rate * 2);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(rate);
        w.Write(rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(rate * 2);
        for (var i = 0; i < rate; i++)
            w.Write((short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 3000));
        w.Flush();
        return stream.ToArray();
    }

    // ---------- HTTP ----------

    // POST JSON（合并额外 Body）并解析回复
    private static JsonObject? PostJson(Provider provider, string url, JsonObject body, out ModelError? error)
    {
        using var request = Create(HttpMethod.Post, url, provider);
        request.Content = JsonContent(Merge(body, provider));
        AddHeaders(request, provider);
        return SendJson(request, out error);
    }

    // 额外 Body 合并到请求体（同名字段覆盖）
    private static JsonObject Merge(JsonObject body, Provider provider)
    {
        if (provider.Body is { } extra)
        {
            foreach (var (key, value) in extra)
                body[key] = value?.DeepClone();
        }
        return body;
    }

    private static StringContent JsonContent(JsonObject body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    // API Key：Claude 为 x-api-key（并补上 anthropic-version），Gemini 为 x-goog-api-key，其他为 Authorization: Bearer
    private static HttpRequestMessage Create(HttpMethod method, string url, Provider provider)
    {
        var request = new HttpRequestMessage(method, url);
        if (provider.Type == ApiTypes.Claude
            && !provider.Headers.Any(h => h.Name.Equals("anthropic-version", StringComparison.OrdinalIgnoreCase)))
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        if (provider.ApiKey.Length > 0)
        {
            var (name, value) = provider.Type switch
            {
                ApiTypes.Claude => ("x-api-key", provider.ApiKey),
                ApiTypes.Gemini => ("x-goog-api-key", provider.ApiKey),
                _ => ("Authorization", "Bearer " + provider.ApiKey),
            };
            request.Headers.TryAddWithoutValidation(name, value);
        }
        return request;
    }

    // 额外 Header：请求头放不下的（例如 Content-Type）放到内容头
    private static void AddHeaders(HttpRequestMessage request, Provider provider)
    {
        foreach (var (name, value) in provider.Headers)
        {
            if (!request.Headers.TryAddWithoutValidation(name, value))
                request.Content?.Headers.TryAddWithoutValidation(name, value);
        }
    }

    // 发送并解析为 JSON 对象；失败时返回 null
    private static JsonObject? SendJson(HttpRequestMessage request, out ModelError? error)
    {
        if (Send(request, out _, out error) is not { } data)
            return null;
        var text = Encoding.UTF8.GetString(data);
        try
        {
            if (JsonNode.Parse(text) is JsonObject json)
                return json;
        }
        catch (JsonException)
        {
        }
        return Fail(new ModelError($"无法识别服务器的回复：{Truncate(text)}"), out error);
    }

    // 发送并返回回复的内容；服务器返回错误时 error 含 HTTP 状态码和服务器给出的原因
    private static byte[]? Send(HttpRequestMessage request, out string contentType, out ModelError? error)
    {
        contentType = "";
        error = null;
        try
        {
            using var response = Http.Send(request);
            using var stream = new MemoryStream();
            response.Content.ReadAsStream().CopyTo(stream);
            var data = stream.ToArray();
            if (!response.IsSuccessStatusCode)
                return Fail<byte[]>(new ModelError(ErrorMessage(Encoding.UTF8.GetString(data)), (int)response.StatusCode), out error);
            contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            return data;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            return Fail<byte[]>(new ModelError($"请求失败：{ex.Message}"), out error);
        }
    }

    // 错误回复中的原因：{"error": {"message"}}、{"error": "…"}、{"message"}，否则为原文
    private static string ErrorMessage(string text)
    {
        try
        {
            if (JsonNode.Parse(text) is JsonObject json)
            {
                var message = json["error"] is JsonObject e ? JsonUtil.Str(e, "message") : JsonUtil.Str(json, "error");
                if ((message.Length > 0 ? message : JsonUtil.Str(json, "message")) is { Length: > 0 } m)
                    return Truncate(m);
            }
        }
        catch (JsonException)
        {
        }
        return text.Trim().Length > 0 ? Truncate(text.Trim()) : "（服务器没有说明原因）";
    }

    private static JsonObject? Fail(ModelError reason, out ModelError? error) => Fail<JsonObject>(reason, out error);

    private static T? Fail<T>(ModelError reason, out ModelError? error) where T : class
    {
        error = reason;
        return null;
    }

    private static string Truncate(string s) => s.Length > 300 ? s[..300] + "…" : s;
}
