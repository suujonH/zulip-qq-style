using System;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels.Settings;

/// <summary>聊天中的文件：默认保存位置、点击文件时的行为。</summary>
public sealed partial class FilesPageViewModel : SettingsPageViewModel
{
    public FilesPageViewModel(SettingsViewModel owner) : base(owner)
    {
        Owner.Local.Changed += OnChanged;
    }

    public string DownloadRoot => Owner.Local.DownloadRoot;

    public bool IsOpenAfterDownload
    {
        get => Owner.Local.FileClickAction == FileClickAction.OpenAfterDownload;
        set
        {
            if (value)
                Owner.Local.FileClickAction = FileClickAction.OpenAfterDownload;
        }
    }

    public bool IsDownloadOnly
    {
        get => Owner.Local.FileClickAction == FileClickAction.DownloadOnly;
        set
        {
            if (value)
                Owner.Local.FileClickAction = FileClickAction.DownloadOnly;
        }
    }

    /// <summary>选择了新的保存位置（由窗口弹出文件夹选择器）。已下载的文件仍按记录中的路径打开。</summary>
    public void SetDownloadRoot(string path) => Owner.Local.DownloadRoot = path;

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(DownloadRoot);
            Process.Start(new ProcessStartInfo(DownloadRoot) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"打开文件夹失败：{DownloadRoot}", ex);
            Owner.ShowToast("无法打开文件夹");
        }
    }

    public override void Close() => Owner.Local.Changed -= OnChanged;

    private void OnChanged(object? sender, string name) => OnPropertyChanged(string.Empty);
}
