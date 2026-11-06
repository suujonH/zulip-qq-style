namespace ZulipQQ.Update;

/// <summary>更新工作目录中各文件的位置，以及程序目录中与更新有关的文件名。</summary>
public sealed class UpdateLayout(string root)
{
    /// <summary>主程序文件名。</summary>
    public const string AppExeName = "ZulipQQ.exe";

    /// <summary>独立更新器文件名。</summary>
    public const string UpdaterExeName = "ZulipQQ.Updater.exe";

    /// <summary>程序目录中的文件清单（sha256sum 格式，由 tools/pack-release.ps1 生成），更新时只动清单中的文件。</summary>
    public const string FileListName = "ZulipQQ.files.sha256";

    /// <summary>替换时存放旧文件的目录，位于程序目录内：移动文件很快，也不需要上级目录的写权限。</summary>
    public const string BackupDirName = ".update-backup";

    /// <summary>更新器启动新版本时的参数，其后是启动标记的路径；新版本启动成功后把自己的版本号写入该文件。</summary>
    public const string ProbeArg = "--update-probe";

    public string Root { get; } = root;

    public string StatePath => Path.Combine(Root, "state.json");

    /// <summary>已下载、校验并解压好的新版本，等待替换。</summary>
    public string StagedDir => Path.Combine(Root, "staged");

    /// <summary>解压到位后写入的清单；存在即表示 <see cref="StagedDir"/> 完整可用。</summary>
    public string ReadyPath => Path.Combine(Root, "ready.json");

    /// <summary>下载与解压的临时目录。</summary>
    public string WorkDir => Path.Combine(Root, "work");

    /// <summary>更新器运行时的副本所在目录：更新器要替换程序目录，不能在程序目录内运行。</summary>
    public string RunnerDir => Path.Combine(Root, "runner");

    /// <summary>新版本启动成功后写入的标记。</summary>
    public string ProbePath => Path.Combine(Root, "started.txt");
}
