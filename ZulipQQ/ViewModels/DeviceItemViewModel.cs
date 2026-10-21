using ZulipQQ.Models;

namespace ZulipQQ.ViewModels;

public class DeviceItemViewModel(Device device) : ViewModelBase
{
    public Device Device { get; } = device;

    public string Name => Device.Name;

    public bool IsOnline => Device.IsOnline;

    public string StatusText => IsOnline ? "[在线]" : "[离线]";
}
