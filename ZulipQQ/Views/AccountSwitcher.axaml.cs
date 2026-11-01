using System;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using ZulipQQ.Services;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Views;

/// <summary>
/// 账号切换下拉框：<see cref="Show"/> 设置窗口当前显示的账号（不触发事件），用户选择其他账号时触发 <see cref="AccountSelected"/>。
/// </summary>
public partial class AccountSwitcher : UserControl
{
    // 程序设置选中项时不触发事件
    private bool _updating;
    private ZulipSession? _session;

    public AccountSwitcher()
    {
        InitializeComponent();
        Picker.SelectionChanged += OnSelectionChanged;
    }

    /// <summary>用户选择了另一个在线账号。</summary>
    public event EventHandler<AccountEntryViewModel>? AccountSelected;

    /// <summary>选中窗口当前显示的账号。</summary>
    public void Show(ZulipSession session)
    {
        _session = session;
        Select();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AccountHubViewModel.Instance.OnlineEntries.CollectionChanged += OnOnlineChanged;
        Select();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        AccountHubViewModel.Instance.OnlineEntries.CollectionChanged -= OnOnlineChanged;
    }

    // 在线账号增减时下拉框可能丢掉选中项：重新选中窗口当前的账号
    private void OnOnlineChanged(object? sender, NotifyCollectionChangedEventArgs e) => Select();

    private void Select()
    {
        _updating = true;
        try
        {
            Picker.SelectedItem = _session is null ? null : AccountHubViewModel.Instance.Find(_session);
        }
        finally
        {
            _updating = false;
        }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_updating && Picker.SelectedItem is AccountEntryViewModel { IsOnline: true, Session: { } session } entry && session != _session)
            AccountSelected?.Invoke(this, entry);
    }
}
