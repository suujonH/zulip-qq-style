using System;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Views;

public partial class MessagesPageView : UserControl
{
    private MessagesPageViewModel? _vm;

    public MessagesPageView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm = DataContext as MessagesPageViewModel;
        if (_vm is not null)
            _vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    // 切换主列表 / 群助手子列表时，键盘焦点移到显示中的列表（隐藏的一层已被禁用）
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MessagesPageViewModel.IsHelperOpen) || _vm is null)
            return;
        var target = _vm.IsHelperOpen ? HelperList : MainList;
        // 等样式把目标层重新启用后再移动焦点
        Dispatcher.UIThread.Post(() =>
        {
            // ListBox 本身不接收焦点，焦点放在选中项的容器上；没有选中项时，
            // 子列表取第一项，主列表取群助手条目（刚才点击的位置，避免列表滚动）
            var index = target.SelectedIndex;
            if (index < 0)
                index = target == MainList ? _vm.Conversations.ToList().FindIndex(c => c.IsHelper) : 0;
            if (index >= 0 && target.ContainerFromIndex(index) is { } container)
                container.Focus(NavigationMethod.Directional);
        }, DispatcherPriority.Input);
    }
}
