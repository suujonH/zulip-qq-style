using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using ZulipQQ.Controls;
using ZulipQQ.Core;
using ZulipQQ.Services;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Views;

/// <summary>
/// 图片查看器。只有一个窗口：再次打开时替换其中的图片并激活。←/→ 切换图片，Esc 关闭。
/// 打开时窗口按图片大小（不超过所在屏幕工作区的 85%，超过时等比缩小）显示在屏幕中间；←/→ 切换不改变窗口大小。
/// </summary>
public partial class ImageViewerWindow : Window, ISessionWindow
{
    // 标题栏与底部工具栏的高度（与 axaml 中的行高相同）：窗口高度 = 图片高度 + 它们
    private const double ChromeHeight = 30 + 52;
    private const double WorkAreaRatio = 0.85;
    // 新窗口等图片载入后再显示（直接是图片的大小）；载入较慢时先按默认大小显示，载入后再调整
    private static readonly TimeSpan ShowTimeout = TimeSpan.FromMilliseconds(400);

    private static ImageViewerWindow? _current;

    // 还没有显示的新窗口与它的所有者
    private bool _pending;
    private Window? _pendingOwner;
    // 打开了新的图片：载入后按图片调整窗口大小
    private bool _sizeToImage;

    // 调整缩放后在中间显示比例，3 秒后消失（再次调整重新计时）
    private readonly DispatcherTimer _zoomTipTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    public ImageViewerWindow()
    {
        InitializeComponent();
        ZoomInButton.Click += (_, _) => Image.ZoomIn();
        ZoomOutButton.Click += (_, _) => Image.ZoomOut();
        ActualSizeButton.Click += (_, _) => Image.ActualSize();
        RotateLeftButton.Click += (_, _) => Image.RotateLeft();
        CopyMenuItem.CommandParameter = this;
        SaveAsMenuItem.CommandParameter = this;
        // 图片还没有载入时不显示菜单
        ImageMenu.Opening += (_, e) => e.Cancel = (DataContext as ImageViewerViewModel)?.Bitmap is null;
        ImageMenu.Opened += (_, _) =>
        {
            // 系统不支持模糊时换成不透明的底色
            if (GlassPopup.Apply(ImageMenu))
                ImageMenu.ClearValue(BackgroundProperty);
            else
                ImageMenu.Background = new SolidColorBrush(Color.Parse("#F21C1C1C"));
        };
        Image.ScaleChanged += (_, _) => UpdateScaleText();
        Image.Zoomed += (_, _) => ShowZoomTip();
        _zoomTipTimer.Tick += (_, _) =>
        {
            _zoomTipTimer.Stop();
            ZoomTip.Opacity = 0;
        };
        UpdateScaleText();
        // 隧道阶段处理：方向键不被工具栏按钮的焦点导航吃掉
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>打开图片查看器，显示头像的大图。</summary>
    public static void ShowAvatar(Window? owner, ZulipSession session, string avatarUrl) =>
        Show(owner, session, [new InlineImage(AvatarUrls.Large(avatarUrl), "")], 0);

    /// <summary>打开图片查看器，显示 <paramref name="images"/> 中的第 <paramref name="index"/> 张。</summary>
    public static void Show(Window? owner, ZulipSession session, IReadOnlyList<InlineImage> images, int index)
    {
        // 打开中的是其他账号的窗口：关闭后为这个账号重新打开
        if (_current is { } other && SessionWindow.Of(other) != session)
        {
            _current = null;
            other.Close();
        }
        if (_current is null)
        {
            var window = new ImageViewerWindow();
            _current = window;
            SessionWindow.Bind(window, session);
            var vm = new ImageViewerViewModel(session);
            vm.PropertyChanged += window.OnViewModelPropertyChanged;
            vm.Dialogs.CollectionChanged += (_, _) => window.FitDialogs(vm);
            window.DataContext = vm;
            window._pending = true;
            window._pendingOwner = owner;
            window._sizeToImage = true;
            vm.Show(images, index);
            DispatcherTimer.RunOnce(window.ShowPending, ShowTimeout);
            return;
        }
        if (_current.WindowState == WindowState.Minimized)
            _current.WindowState = WindowState.Normal;
        _current._sizeToImage = true;
        ((ImageViewerViewModel)_current.DataContext!).Show(images, index);
        _current.Activate();
    }

    private void ShowPending()
    {
        if (!_pending)
            return;
        _pending = false;
        if (_pendingOwner is { } owner)
            Show(owner);
        else
            Show();
        Activate();
    }

    // 图片载入完成（或失败）：按图片调整窗口，还没有显示的新窗口此时显示
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ImageViewerViewModel vm || e.PropertyName is not (nameof(vm.Bitmap) or nameof(vm.IsFailed))
            || vm.Bitmap is null && !vm.IsFailed)
            return;
        if (_sizeToImage)
        {
            _sizeToImage = false;
            if (vm.Bitmap is { } bitmap)
                SizeToImage(bitmap.Size);
        }
        ShowPending();
    }

    // 对话框（转发，640×500）比窗口大时放大窗口，保持中心不变
    private void FitDialogs(ImageViewerViewModel vm)
    {
        const double dialogWidth = 680, dialogHeight = 560;
        if (vm.Dialogs.Count == 0 || WindowState != WindowState.Normal || Width >= dialogWidth && Height >= dialogHeight)
            return;
        var scaling = RenderScaling;
        var center = new PixelPoint(Position.X + (int)(Width * scaling / 2), Position.Y + (int)(Height * scaling / 2));
        Width = Math.Max(Width, dialogWidth);
        Height = Math.Max(Height, dialogHeight);
        Position = new PixelPoint(center.X - (int)(Width * scaling / 2), center.Y - (int)(Height * scaling / 2));
    }

    // 图片区等于图片大小；超过工作区的 85% 时等比缩小到一边刚好达到。窗口在所在屏幕的工作区居中
    private void SizeToImage(Size image)
    {
        if (WindowState != WindowState.Normal || image.Width <= 0 || image.Height <= 0)
            return;
        var screen = (_pending ? _pendingOwner : this) is { } window ? Screens.ScreenFromWindow(window) : null;
        if ((screen ?? Screens.Primary) is not { } target)
            return;
        var scaling = target.Scaling;
        var area = target.WorkingArea;
        var maxWidth = area.Width / scaling * WorkAreaRatio;
        var maxHeight = area.Height / scaling * WorkAreaRatio - ChromeHeight;
        var ratio = Math.Min(1, Math.Min(maxWidth / image.Width, maxHeight / image.Height));
        Width = Math.Max(MinWidth, Math.Ceiling(image.Width * ratio));
        Height = Math.Max(MinHeight, Math.Ceiling(image.Height * ratio) + ChromeHeight);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(
            area.X + (int)Math.Round((area.Width - Width * scaling) / 2),
            area.Y + (int)Math.Round((area.Height - Height * scaling) / 2));
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // 打开对话框（转发）时按键交给对话框
        if (DataContext is not ImageViewerViewModel vm || e.KeyModifiers != KeyModifiers.None || vm.Dialogs.Count > 0)
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
        _zoomTipTimer.Stop();
        (DataContext as ImageViewerViewModel)?.Close();
        if (_current == this)
            _current = null;
    }

    private void UpdateScaleText()
    {
        var text = Math.Round(Image.Scale * 100).ToString(CultureInfo.InvariantCulture) + "%";
        ScaleText.Text = text;
        ZoomTipText.Text = text;
    }

    private void ShowZoomTip()
    {
        ZoomTip.Opacity = 1;
        _zoomTipTimer.Stop();
        _zoomTipTimer.Start();
    }
}
