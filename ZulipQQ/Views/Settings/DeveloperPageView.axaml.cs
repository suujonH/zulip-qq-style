using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using ZulipQQ.Services;
using ZulipQQ.ViewModels.Settings;

namespace ZulipQQ.Views.Settings;

public partial class DeveloperPageView : UserControl
{
    private DeveloperPageViewModel? _vm;

    public DeveloperPageView()
    {
        InitializeComponent();
        // 点击（包括再次点击已选中的项）打开详情
        RequestList.Tapped += OnRequestTapped;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
            _vm.DetailRequested -= OnDetailRequested;
        _vm = DataContext as DeveloperPageViewModel;
        if (_vm is not null)
            _vm.DetailRequested += OnDetailRequested;
    }

    private void OnRequestTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as StyledElement)?.DataContext is RequestLogEntry entry)
            _vm?.OpenDetailCommand.Execute(entry);
    }

    private void OnDetailRequested(object? sender, RequestLogEntry entry) =>
        RequestDetailWindow.ShowFor(entry, TopLevel.GetTopLevel(this) as Window);
}
