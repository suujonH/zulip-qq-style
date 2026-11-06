using System;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels.Settings;

/// <summary>关于：程序版本与构建、手动检查更新。组织与服务器信息在各账号的“服务器”页中。</summary>
public sealed partial class AboutPageViewModel : SettingsPageViewModel
{
    private readonly UpdateService _updates = UpdateService.Instance;

    public AboutPageViewModel(SettingsViewModel owner) : base(owner)
    {
        // CI 通过 -p:Version 注入版本；SDK 会在末尾追加 +{commit}
        var info = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var plus = info.IndexOf('+');
        Version = plus < 0 ? info : info[..plus];
        Build = plus < 0 ? "" : info[(plus + 1)..];
        if (Build.Length > 7)
            Build = Build[..7];
        Refresh();
    }

    public string Version { get; }

    public string Build { get; }

    public bool HasBuild => Build.Length > 0;

    /// <summary>检查的进度和结果；上次更新失败时显示失败原因。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string Status { get; private set; } = "";

    public bool HasStatus => Status.Length > 0;

    [ObservableProperty]
    public partial bool IsChecking { get; private set; }

    /// <summary>页面显示期间跟随后台的检查和下载进度。</summary>
    public void Attach()
    {
        _updates.StatusChanged += OnStatusChanged;
        Refresh();
    }

    public void Detach() => _updates.StatusChanged -= OnStatusChanged;

    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        await _updates.CheckAsync(manual: true);
        Refresh();
    }

    private void OnStatusChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Refresh);

    private void Refresh()
    {
        IsChecking = _updates.IsBusy;
        Status = _updates.StatusText.Length > 0
            ? _updates.StatusText
            : _updates.LastError is { } error ? $"上次更新失败：{error}" : "";
    }
}
