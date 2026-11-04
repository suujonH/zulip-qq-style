using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels.Settings;

/// <summary>聊天中的文件：保存文件的位置、聊天信息（数据目录）的位置。</summary>
public sealed partial class FilesPageViewModel : SettingsPageViewModel
{
    public FilesPageViewModel(SettingsViewModel owner) : base(owner)
    {
        Owner.Local.Changed += OnChanged;
    }

    /// <summary>“将接收或下载的文件保存到”。</summary>
    public string SaveRoot => Owner.Local.SaveRoot;

    /// <summary>“聊天信息默认保存到”：当前的数据目录。</summary>
    public string DataRoot => AppPaths.DataRoot;

    /// <summary>正在检查新的聊天信息位置（统计大小可能需要一会儿）。</summary>
    [ObservableProperty]
    public partial bool IsMoving { get; private set; }

    /// <summary>选择了新的保存位置（由窗口弹出文件夹选择器）。以后保存的文件存到这里，已保存的文件不移动。</summary>
    public void SetSaveRoot(string path) => Owner.Local.SaveRoot = path;

    /// <summary>
    /// 选择了新的聊天信息位置 <paramref name="parent"/>（由窗口弹出文件夹选择器）：数据目录将是其中的 ZulipQQ 文件夹。
    /// 确认并检查后记下搬迁目标、重启程序，新进程启动时搬迁（见 <see cref="DataLocation"/>）。
    /// </summary>
    public async Task MoveDataRootAsync(string parent)
    {
        if (IsMoving)
            return;
        var target = Path.Combine(parent, DataLocation.FolderName);
        if (!await Owner.ConfirmAsync($"将把聊天信息移动到“{target}”。ZulipQQ 会先退出，移动完成后自动启动并重新登录。", "移动并重启"))
            return;
        IsMoving = true;
        try
        {
            if (await Task.Run(() => DataLocation.Validate(target)) is { } reason)
            {
                Owner.ShowToast(reason);
                return;
            }
            DataLocation.RequestMove(target);
        }
        catch (Exception ex)
        {
            Log.Warn($"准备移动数据目录失败：{target}", ex);
            Owner.ShowToast("无法移动到该位置");
            return;
        }
        finally
        {
            IsMoving = false;
        }
        await App.Current.RestartAsync();
    }

    [RelayCommand]
    private void OpenSaveFolder() => OpenFolder(SaveRoot);

    [RelayCommand]
    private void OpenDataFolder() => OpenFolder(DataRoot);

    public override void Close() => Owner.Local.Changed -= OnChanged;

    private void OpenFolder(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"打开文件夹失败：{dir}", ex);
            Owner.ShowToast("无法打开文件夹");
        }
    }

    private void OnChanged(object? sender, string name) => OnPropertyChanged(string.Empty);
}
