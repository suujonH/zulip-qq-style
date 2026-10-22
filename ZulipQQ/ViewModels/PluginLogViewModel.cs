using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Plugins;

namespace ZulipQQ.ViewModels;

/// <summary>插件日志的一行。</summary>
public sealed class PluginLogRowViewModel(PluginLogEntry entry)
{
    public string TimeText { get; } = entry.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public string LevelText { get; } = LevelName(entry.Level);

    public string Category { get; } = entry.Category;

    public string Text { get; } = entry.Text;

    public bool IsDebug => entry.Level < Zq.LogInfo;
    public bool IsSuccess => entry.Level == Zq.LogInfoSuccess;
    public bool IsIo => entry.Level is Zq.LogInfoRecv or Zq.LogInfoSend;
    public bool IsWarning => entry.Level is >= Zq.LogWarning and < Zq.LogError;
    public bool IsError => entry.Level >= Zq.LogError;

    /// <summary>复制时的一行文字。</summary>
    public string ToLine() => $"{TimeText} [{LevelText}] {Category}: {Text}";

    private static string LevelName(int level) => level switch
    {
        < Zq.LogInfo => "调试",
        Zq.LogInfoSuccess => "成功",
        Zq.LogInfoRecv => "接收",
        Zq.LogInfoSend => "发送",
        < Zq.LogWarning => "信息",
        < Zq.LogError => "警告",
        < Zq.LogFatal => "错误",
        _ => "致命",
    };
}

/// <summary>插件日志窗口：插件写日志时（任意线程）合并刷新。</summary>
public sealed partial class PluginLogViewModel : ViewModelBase
{
    private readonly NativePlugin _plugin;
    private bool _reloadPending;

    public PluginLogViewModel(NativePlugin plugin)
    {
        _plugin = plugin;
        plugin.LogsChanged += OnLogsChanged;
        Reload();
    }

    public string Title => $"插件日志 - {_plugin.Name}";

    public ObservableCollection<PluginLogRowViewModel> Rows { get; } = [];

    /// <summary>新的日志行加入后触发（视图滚动到底部）。</summary>
    public event EventHandler? Reloaded;

    /// <summary>全部日志的文字（复制用）。</summary>
    public string AllText => string.Join(Environment.NewLine, Rows.Select(r => r.ToLine()));

    [RelayCommand]
    private void Clear() => _plugin.ClearLogs();

    public void Close() => _plugin.LogsChanged -= OnLogsChanged;

    private void OnLogsChanged(object? sender, EventArgs e)
    {
        if (_reloadPending)
            return;
        _reloadPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _reloadPending = false;
            Reload();
        }, DispatcherPriority.Background);
    }

    private void Reload()
    {
        Rows.Clear();
        foreach (var entry in _plugin.GetLogs())
            Rows.Add(new PluginLogRowViewModel(entry));
        Reloaded?.Invoke(this, EventArgs.Empty);
    }
}
