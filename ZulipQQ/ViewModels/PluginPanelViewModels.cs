using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Plugins;
using ZulipQQ.ViewModels.Settings;

namespace ZulipQQ.ViewModels;

/// <summary>
/// 一个插件在用户资料卡 / 话题资料面板中的分组（标题为插件名）：设置项由插件通过 zq_on_panel 的 get 提供，
/// 修改后立即发送 set，以插件回复的设置项刷新。插件没有回复任何设置项时不显示。
/// </summary>
public sealed partial class PluginPanelGroupViewModel : ObservableObject
{
    private readonly NativePlugin _plugin;
    private readonly PluginTargetKind _kind;

    public PluginPanelGroupViewModel(NativePlugin plugin, PluginTargetKind kind, PluginTarget target)
    {
        _plugin = plugin;
        _kind = kind;
        Target = target;
        Title = plugin.Name;
    }

    public string Title { get; }

    public PluginTarget Target { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFields))]
    public partial IReadOnlyList<PluginPanelFieldViewModel> Fields { get; private set; } = [];

    public bool HasFields => Fields.Count > 0;

    /// <summary>插件保存了修改（UI 线程）。</summary>
    public event EventHandler? Changed;

    /// <summary>开启了面板的插件各一个分组（开始加载设置项）。</summary>
    public static IReadOnlyList<PluginPanelGroupViewModel> For(PluginTargetKind kind, PluginTarget target)
    {
        var groups = PluginHost.Instance.Plugins.Where(p => p.ActivePanels.Any(x => x.Kind == kind))
            .Select(p => new PluginPanelGroupViewModel(p, kind, target)).ToList();
        foreach (var group in groups)
            _ = group.LoadAsync();
        return groups;
    }

    public async Task LoadAsync() =>
        Apply(await PluginHost.Instance.InvokePanelAsync(_plugin, PluginPanel.Request("get", _kind, Target), Target.AccountId));

    internal async void Set(string key, JsonNode? value)
    {
        var reply = await PluginHost.Instance.InvokePanelAsync(_plugin, PluginPanel.Request("set", _kind, Target, key, value), Target.AccountId);
        // 没有回复时（插件已停用或超时）恢复为插件中的值
        if (reply is null)
            await LoadAsync();
        else
            Apply(reply);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Apply(JsonObject? reply) =>
        Fields = PluginPanelField.Parse(reply).Select(f => new PluginPanelFieldViewModel(this, f)).ToList();
}

/// <summary>面板中的一个下拉框或开关。</summary>
public sealed partial class PluginPanelFieldViewModel : ObservableObject
{
    private readonly PluginPanelGroupViewModel _owner;
    private readonly bool _ready;

    public PluginPanelFieldViewModel(PluginPanelGroupViewModel owner, PluginPanelField field)
    {
        _owner = owner;
        Key = field.Key;
        Label = field.Label;
        IsBool = field.IsBool;
        Options = field.Options;
        SelectedOption = field.Options.FirstOrDefault(o => o.Value == field.SelectedValue) ?? field.Options.FirstOrDefault();
        IsChecked = field.IsChecked;
        _ready = true;
    }

    public string Key { get; }

    public string Label { get; }

    public bool IsBool { get; }

    public bool IsSelect => !IsBool;

    public IReadOnlyList<PluginPanelOption> Options { get; }

    [ObservableProperty]
    public partial PluginPanelOption? SelectedOption { get; set; }

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    partial void OnSelectedOptionChanged(PluginPanelOption? value)
    {
        if (_ready && value is not null)
            _owner.Set(Key, value.Value);
    }

    partial void OnIsCheckedChanged(bool value)
    {
        if (_ready && IsBool)
            _owner.Set(Key, value);
    }
}

/// <summary>
/// 插件设置中一个面板的列表（plugin.json 的 panels）：左侧为插件保存了设置的用户 / 话题（zq_on_panel 的 list），
/// 右侧为选中项的设置项（与资料卡中的分组相同）。“添加”从已登录账号的用户 / 话题中选择；修改立即由插件保存，与“保存”按钮无关。
/// </summary>
public sealed partial class PluginPanelSectionViewModel : ObservableObject
{
    private readonly NativePlugin _plugin;
    private readonly PluginPanel _panel;
    private readonly ObservableCollection<ViewModelBase> _dialogs;
    private PluginTargetPickerDialogViewModel? _picker;

    public PluginPanelSectionViewModel(NativePlugin plugin, PluginPanel panel, ObservableCollection<ViewModelBase> dialogs)
    {
        _plugin = plugin;
        _panel = panel;
        _dialogs = dialogs;
        IsAvailable = plugin.ActivePanels.Contains(panel);
        if (IsAvailable)
            _ = LoadAsync();
    }

    public string Label => _panel.Label;

    /// <summary>插件已开启（未开启时只显示提示）。</summary>
    public bool IsAvailable { get; }

    public ObservableCollection<PluginPanelEntryViewModel> Entries { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedEntry))]
    public partial PluginPanelEntryViewModel? SelectedEntry { get; set; }

    public bool HasSelectedEntry => SelectedEntry is not null;

    [ObservableProperty]
    public partial PluginPanelGroupViewModel? Group { get; private set; }

    partial void OnSelectedEntryChanged(PluginPanelEntryViewModel? value)
    {
        if (Group is { } old)
            old.Changed -= OnGroupChanged;
        Group = value is null ? null : new PluginPanelGroupViewModel(_plugin, _panel.Kind, value.Target);
        if (Group is { } group)
        {
            group.Changed += OnGroupChanged;
            _ = group.LoadAsync();
        }
    }

    private void OnGroupChanged(object? sender, EventArgs e) => _ = LoadAsync();

    // 重新读取列表，保留选中项（插件还没有保存的新添加项也保留）
    private async Task LoadAsync()
    {
        var reply = await PluginHost.Instance.InvokePanelAsync(_plugin, PluginPanel.Request("list", _panel.Kind), 0);
        var selected = SelectedEntry;
        var entries = PluginPanelEntry.Parse(reply, _panel.Kind);
        foreach (var old in Entries.Where(o => o != selected && !entries.Any(e => e.Target.SameAs(o.Target))).ToList())
            Entries.Remove(old);
        foreach (var entry in entries)
        {
            if (Entries.FirstOrDefault(o => o.Target.SameAs(entry.Target)) is { } existing)
                existing.Text = entry.Text;
            else
                Entries.Add(new PluginPanelEntryViewModel(this, entry.Target, entry.Text));
        }
    }

    [RelayCommand]
    private void Add()
    {
        ClosePicker();
        var title = _panel.Kind == PluginTargetKind.User ? "选择用户" : "选择话题";
        _picker = new PluginTargetPickerDialogViewModel(_panel.Kind, title, target =>
        {
            ClosePicker();
            if (target is null)
                return;
            var entry = Entries.FirstOrDefault(e => e.Target.SameAs(target));
            if (entry is null)
                Entries.Add(entry = new PluginPanelEntryViewModel(this, target, ""));
            SelectedEntry = entry;
        });
        _dialogs.Add(_picker);
    }

    internal async void Remove(PluginPanelEntryViewModel entry)
    {
        await PluginHost.Instance.InvokePanelAsync(_plugin, PluginPanel.Request("remove", _panel.Kind, entry.Target), entry.Target.AccountId);
        if (SelectedEntry == entry)
            SelectedEntry = null;
        Entries.Remove(entry);
        await LoadAsync();
    }

    public void ClosePicker()
    {
        if (_picker is null)
            return;
        var picker = _picker;
        _picker = null;
        _dialogs.Remove(picker);
        picker.Dispose();
    }
}

/// <summary>面板列表的一行：名称与插件给出的说明（例如“工作 / 同事”）。</summary>
public sealed partial class PluginPanelEntryViewModel(PluginPanelSectionViewModel owner, PluginTarget target, string text) : ObservableObject
{
    public PluginTarget Target { get; } = target;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial string Text { get; set; } = text;

    public string Title => Text.Length > 0 ? $"{Target.Name}：{Text}" : Target.Name;

    [RelayCommand]
    private void Remove() => owner.Remove(this);
}
