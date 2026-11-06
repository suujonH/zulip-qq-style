using System.Text.Json;

namespace ZulipQQ.Update;

/// <summary>
/// 更新的结果和失败记录（保存在 <see cref="UpdateLayout.StatePath"/>），防止更新失败后每次启动都重试。
/// 只记录最近尝试的一个版本；发布了更高的版本后记录自然失效。
/// </summary>
public sealed record UpdateState
{
    /// <summary>同一版本的下载、校验、替换累计失败这么多次后，自动更新不再尝试它。</summary>
    public const int MaxFailures = 3;

    /// <summary>失败记录所属的版本。</summary>
    public string? Version { get; init; }

    public int Failures { get; init; }

    /// <summary>新版本替换后无法启动：重试也不会成功，第一次失败就不再自动尝试。</summary>
    public bool Blocked { get; init; }

    public string? LastError { get; init; }

    /// <summary>更新器上次运行的结果，主程序下次启动时写入日志后清除。</summary>
    public string? PendingReport { get; init; }

    /// <summary>自动更新是否可以尝试 <paramref name="version"/>。用户手动检查时不受此限制。</summary>
    public bool AllowsAutoAttempt(string version) =>
        !IsSameVersion(version) || (!Blocked && Failures < MaxFailures);

    /// <summary>记录一次失败；<paramref name="block"/> 为 true 时不再自动尝试该版本。</summary>
    public UpdateState WithFailure(string version, string error, bool block = false)
    {
        var same = IsSameVersion(version);
        return this with
        {
            Version = version,
            Failures = (same ? Failures : 0) + 1,
            Blocked = block || (same && Blocked),
            LastError = error,
        };
    }

    /// <summary>更新成功或用户手动重试：清除该版本的失败记录。</summary>
    public UpdateState Cleared() => this with { Version = null, Failures = 0, Blocked = false, LastError = null };

    public static UpdateState Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize(File.ReadAllText(path), UpdateJsonContext.Default.UpdateState) ?? new UpdateState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 文件损坏时视为没有记录
        }
        return new UpdateState();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, UpdateJsonContext.Default.UpdateState));
        File.Move(temp, path, overwrite: true);
    }

    private bool IsSameVersion(string version) =>
        AppVersion.TryParse(version) is { } a && AppVersion.TryParse(Version) is { } b && a.CompareTo(b) == 0;
}
