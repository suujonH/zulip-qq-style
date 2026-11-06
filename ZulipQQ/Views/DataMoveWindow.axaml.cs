using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using ZulipQQ.Controls;
using ZulipQQ.Services;

namespace ZulipQQ.Views;

/// <summary>
/// 启动时搬迁数据目录的进度窗口（在打开数据库与其它窗口之前显示，搬迁期间不能做其它事情）。
/// 搬迁中不能关闭；失败时显示原因，点“继续”后使用原来的位置启动。
/// </summary>
public partial class DataMoveWindow : Window
{
    private bool _canClose;

    public DataMoveWindow()
    {
        InitializeComponent();
        ContinueButton.Click += (_, _) => Close();
    }

    /// <summary>显示窗口并把数据目录搬到 <paramref name="target"/>；成功，或失败后用户点“继续”时关闭窗口并返回。</summary>
    public static async Task RunAsync(string target)
    {
        var window = new DataMoveWindow();
        var closed = new TaskCompletionSource();
        window.Closed += (_, _) => closed.TrySetResult();
        window.DetailText.Text = $"正在移动到“{target}”，完成后 ZulipQQ 将自动启动。";
        window.Show();

        var progress = new Progress<(long Done, long Total)>(p =>
        {
            window.Bar.Value = p.Total > 0 ? (double)p.Done / p.Total : 1;
            window.ProgressText.Text = $"{FileCard.FormatSize(p.Done)} / {FileCard.FormatSize(p.Total)}";
        });
        try
        {
            await Task.Run(() => DataLocation.Move(target, progress));
            window._canClose = true;
            window.Close();
        }
        catch (Exception ex)
        {
            Log.Error($"移动数据目录失败：{target}", ex);
            window.TitleText.Text = "移动失败";
            window.DetailText.Text = $"{ex.Message}\n将继续使用原来的位置：{AppPaths.DataRoot}";
            window.Bar.IsVisible = false;
            window.ProgressText.IsVisible = false;
            window.ContinueButton.IsVisible = true;
            window._canClose = true;
        }
        await closed.Task;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_canClose)
            e.Cancel = true;
        base.OnClosing(e);
    }
}
