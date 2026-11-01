using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace ZulipQQ.Plugins;

/// <summary>
/// plugin.json 中 launch 白名单的一项：开启时告知用户，之后插件可以不经用户点击直接打开。三种写法：
/// <list type="bullet">
/// <item>协议：<c>todo:</c>，匹配该协议的任意 URI（由系统交给注册的应用）。http、https、file 等不能这样写。</item>
/// <item>网址：<c>https://google.com/maps</c>，协议、主机、端口相同且路径在其下；主机可以写成 <c>*.google.com</c>（含 google.com 本身）。</item>
/// <item>程序：完整路径（<c>C:\Tools\todo.exe</c>）只匹配该路径；只写文件名（<c>notepad.exe</c>）只匹配同样只写文件名的目标。</item>
/// </list>
/// </summary>
public sealed partial class LaunchRule
{
    private static readonly HashSet<string> ForbiddenSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https", "file", "javascript", "vbscript", "data",
    };

    private enum RuleKind
    {
        Scheme,
        Url,
        Program,
    }

    private readonly RuleKind _kind;
    private readonly string _scheme = "";
    private readonly string _host = "";
    private readonly bool _wildcard;
    private readonly int _port;
    private readonly string _path = "";
    private readonly string _program = "";

    private LaunchRule(string pattern, RuleKind kind)
    {
        Pattern = pattern;
        _kind = kind;
    }

    private LaunchRule(string pattern, RuleKind kind, string scheme, string host, bool wildcard, int port, string path) : this(pattern, kind)
    {
        _scheme = scheme;
        _host = host;
        _wildcard = wildcard;
        _port = port;
        _path = path;
    }

    private LaunchRule(string pattern, string program) : this(pattern, RuleKind.Program) => _program = program;

    /// <summary>plugin.json 中的原文（开启确认时显示）。</summary>
    public string Pattern { get; }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9+.-]+:$")]
    private static partial Regex SchemePattern();

    /// <summary>解析一项；格式错误时返回 null 并给出原因。</summary>
    public static LaunchRule? TryParse(string pattern, out string? error)
    {
        error = null;
        pattern = pattern.Trim();
        if (SchemePattern().IsMatch(pattern))
        {
            var scheme = pattern[..^1];
            if (ForbiddenSchemes.Contains(scheme))
            {
                error = $"不能把整个 {scheme} 协议加入白名单：{pattern}";
                return null;
            }
            return new LaunchRule(pattern, RuleKind.Scheme, scheme, "", false, 0, "");
        }

        if (pattern.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || pattern.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var wildcard = pattern.Contains("://*.", StringComparison.Ordinal);
            if (!Uri.TryCreate(wildcard ? pattern.Replace("://*.", "://", StringComparison.Ordinal) : pattern, UriKind.Absolute, out var uri)
                || uri.Host.Length == 0 || uri.Host.Contains('*') || !string.IsNullOrEmpty(uri.UserInfo))
            {
                error = $"网址格式错误：{pattern}";
                return null;
            }
            return new LaunchRule(pattern, RuleKind.Url, uri.Scheme, uri.Host, wildcard, uri.Port, uri.AbsolutePath);
        }

        if (Path.IsPathFullyQualified(pattern))
            return new LaunchRule(pattern, Path.GetFullPath(pattern));
        if (pattern.Length > 0 && Path.GetFileName(pattern) == pattern && pattern.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
            return new LaunchRule(pattern, pattern);

        error = $"无法识别的白名单项：{pattern}";
        return null;
    }

    /// <summary>目标是 URI（协议至少两个字符，<c>C:\…</c> 是程序路径）。file: 不算 URI，也不能作为程序打开。</summary>
    public static bool IsUri(string target, out Uri uri) =>
        Uri.TryCreate(target, UriKind.Absolute, out uri!) && uri.Scheme.Length > 1 && !uri.IsFile;

    public bool Matches(string target)
    {
        target = target.Trim();
        if (target.Length == 0)
            return false;
        var isUri = IsUri(target, out var uri);
        switch (_kind)
        {
            case RuleKind.Scheme:
                return isUri && string.Equals(uri.Scheme, _scheme, StringComparison.OrdinalIgnoreCase);
            case RuleKind.Url:
                if (!isUri || !string.Equals(uri.Scheme, _scheme, StringComparison.OrdinalIgnoreCase) || uri.Port != _port
                    || !string.IsNullOrEmpty(uri.UserInfo))
                    return false;
                var hostMatches = string.Equals(uri.Host, _host, StringComparison.OrdinalIgnoreCase)
                    || (_wildcard && uri.Host.EndsWith("." + _host, StringComparison.OrdinalIgnoreCase));
                return hostMatches && PathMatches(uri.AbsolutePath);
            default:
                if (isUri || target.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                    return false;
                if (Path.IsPathFullyQualified(_program))
                    return Path.IsPathFullyQualified(target) && string.Equals(Path.GetFullPath(target), _program, StringComparison.OrdinalIgnoreCase);
                return string.Equals(target, _program, StringComparison.OrdinalIgnoreCase);
        }
    }

    // “/maps”匹配“/maps”和“/maps/…”，不匹配“/mapsx”
    private bool PathMatches(string path)
    {
        if (_path is "" or "/")
            return true;
        if (!path.StartsWith(_path, StringComparison.Ordinal))
            return false;
        return _path.EndsWith('/') || path.Length == _path.Length || path[_path.Length] == '/';
    }
}
