using System;
using Avalonia;
using Avalonia.Controls;
using ZulipQQ.Plugins;
using ZulipQQ.ViewModels.Settings;

namespace ZulipQQ.Views.Settings;

public partial class PluginsPageView : UserControl
{
    private PluginsPageViewModel? _vm;

    public PluginsPageView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
            _vm.LogRequested -= OnLogRequested;
        _vm = DataContext as PluginsPageViewModel;
        if (_vm is not null)
            _vm.LogRequested += OnLogRequested;
    }

    private void OnLogRequested(object? sender, NativePlugin plugin) =>
        PluginLogWindow.ShowFor(plugin, TopLevel.GetTopLevel(this) as Window);
}
