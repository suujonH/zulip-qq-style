using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ZulipQQ.Core;
using ZulipQQ.Services;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Views;

/// <summary>图片查看器。只有一个窗口：再次打开时替换其中的图片并激活。←/→ 切换图片，Esc 关闭。</summary>
public partial class ImageViewerWindow : Window, ISessionWindow
{
    private const double ButtonZoomStep = 1.25;

    private static ImageViewerWindow? _current;

    public ImageViewerWindow()
    {
        InitializeComponent();
        ZoomInButton.Click += (_, _) => Image.ZoomBy(ButtonZoomStep);
        ZoomOutButton.Click += (_, _) => Image.ZoomBy(1 / ButtonZoomStep);
        ActualSizeButton.Click += (_, _) => Image.ActualSize();
        FitButton.Click += (_, _) => Image.Fit();
        Image.ScaleChanged += (_, _) => UpdateScaleText();
        UpdateScaleText();
        // 隧道阶段处理：方向键不被工具栏按钮的焦点导航吃掉
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>打开图片查看器，显示 <paramref name="images"/> 中的第 <paramref name="index"/> 张。</summary>
    public static void Show(Window? owner, ZulipSession session, IReadOnlyList<InlineImage> images, int index)
    {
        if (_current is null)
        {
            _current = new ImageViewerWindow { DataContext = new ImageViewerViewModel(session) };
            if (owner is not null)
                _current.Show(owner);
            else
                _current.Show();
        }
        else if (_current.WindowState == WindowState.Minimized)
        {
            _current.WindowState = WindowState.Normal;
        }
        ((ImageViewerViewModel)_current.DataContext!).Show(images, index);
        _current.Activate();
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ImageViewerViewModel vm || e.KeyModifiers != KeyModifiers.None)
            return;
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.Left when vm.HasPrevious:
                vm.PreviousCommand.Execute(null);
                break;
            case Key.Right when vm.HasNext:
                vm.NextCommand.Execute(null);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_current == this)
            _current = null;
    }

    private void UpdateScaleText() =>
        ScaleText.Text = Math.Round(Image.Scale * 100).ToString(CultureInfo.InvariantCulture) + "%";
}
