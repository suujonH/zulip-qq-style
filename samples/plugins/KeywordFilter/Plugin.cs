using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KeywordFilter;

/// <summary>
/// 关键词过滤（ZulipQQ 内置示例插件）：
/// - 收到的消息包含 数据目录\keywords.txt 中任一关键词（每行一个）时拦截；
/// - 发送时把 {time} 替换为当前时间 HH:mm；
/// - 菜单 1“打开配置目录”。
/// 所有导出函数都由宿主在同一个插件线程上调用，因此不需要加锁。
/// </summary>
internal static unsafe class Plugin
{
    private const string KeywordsFile = "keywords.txt";

    private static ZqHostApi* _api;
    private static string _dataDir = "";
    private static string? _received;
    private static DateTime _keywordsTime;
    private static List<string> _keywords = [];

    [UnmanagedCallersOnly(EntryPoint = "zq_plugin_init", CallConvs = [typeof(CallConvCdecl)])]
    public static int Init(ZqHostApi* api)
    {
        if (api == null || api->ApiVersion < Zq.ApiVersion || api->StructSize < sizeof(ZqHostApi))
            return Zq.ErrInvalidArg;
        _api = api;
        _received = null;
        if (api->GetDataDir(api->Ctx, &OnString, 0) == Zq.Ok && _received is not null)
            _dataDir = _received;
        ReloadKeywords();
        Log(Zq.LogInfo, $"已加载，关键词 {_keywords.Count} 个");
        return Zq.Ok;
    }

    [UnmanagedCallersOnly(EntryPoint = "zq_on_message", CallConvs = [typeof(CallConvCdecl)])]
    public static int OnMessage(ZqMessage* msg)
    {
        if ((msg->Flags & Zq.MsgOutgoing) == 0)
            Log(Zq.LogDebug, $"收到 {Read(msg->SenderName)} 的消息 {msg->MessageId}");
        return Zq.EventIgnore;
    }

    [UnmanagedCallersOnly(EntryPoint = "zq_filter_incoming", CallConvs = [typeof(CallConvCdecl)])]
    public static int FilterIncoming(ZqMessage* msg)
    {
        ReloadKeywords();
        var text = Read(msg->ContentText) ?? "";
        var hit = _keywords.FirstOrDefault(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
        if (hit is null)
            return Zq.EventIgnore;
        Log(Zq.LogInfo, $"已拦截 {Read(msg->SenderName)} 的消息 {msg->MessageId}（关键词：{hit}）");
        return Zq.EventBlock;
    }

    [UnmanagedCallersOnly(EntryPoint = "zq_filter_outgoing", CallConvs = [typeof(CallConvCdecl)])]
    public static int FilterOutgoing(ZqOutgoing* outgoing)
    {
        var content = Read(outgoing->Content);
        if (content is null || !content.Contains("{time}", StringComparison.Ordinal))
            return Zq.EventIgnore;
        var replaced = content.Replace("{time}", DateTime.Now.ToString("HH:mm"), StringComparison.Ordinal);
        var p = Marshal.StringToCoTaskMemUTF8(replaced);
        try
        {
            var result = _api->SetOutgoingContent(_api->Ctx, (byte*)p);
            if (result != Zq.Ok)
                Log(Zq.LogWarning, $"改写发送内容失败：{result}");
        }
        finally
        {
            Marshal.FreeCoTaskMem(p);
        }
        return Zq.EventIgnore;
    }

    [UnmanagedCallersOnly(EntryPoint = "zq_on_menu", CallConvs = [typeof(CallConvCdecl)])]
    public static int OnMenu(int menuId)
    {
        if (menuId != 1 || _dataDir.Length == 0)
            return Zq.Ok;
        try
        {
            Directory.CreateDirectory(_dataDir);
            var path = Path.Combine(_dataDir, KeywordsFile);
            if (!File.Exists(path))
                File.WriteAllText(path, "");
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { _dataDir.TrimEnd('\\') } });
        }
        catch (Exception ex)
        {
            Log(Zq.LogError, $"打开配置目录失败：{ex.Message}");
        }
        return Zq.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnString(byte* s, nint user) => _received = Read((nint)s);

    // keywords.txt 修改后重新读取
    private static void ReloadKeywords()
    {
        var path = Path.Combine(_dataDir, KeywordsFile);
        try
        {
            var time = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
            if (time == _keywordsTime)
                return;
            _keywordsTime = time;
            _keywords = time == default
                ? []
                : File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log(Zq.LogWarning, $"读取关键词失败：{ex.Message}");
        }
    }

    private static string? Read(nint p) => p == 0 ? null : Marshal.PtrToStringUTF8(p);

    private static void Log(int level, string text)
    {
        var category = Marshal.StringToCoTaskMemUTF8("关键词过滤");
        var p = Marshal.StringToCoTaskMemUTF8(text);
        try
        {
            _api->AddLog(_api->Ctx, level, (byte*)category, (byte*)p);
        }
        finally
        {
            Marshal.FreeCoTaskMem(p);
            Marshal.FreeCoTaskMem(category);
        }
    }
}
