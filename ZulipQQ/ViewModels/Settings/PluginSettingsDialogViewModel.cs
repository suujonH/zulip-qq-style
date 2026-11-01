using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Plugins;

namespace ZulipQQ.ViewModels.Settings;

/// <summary>设置表单中需要所在对话框处理的操作。</summary>
public interface IPluginFormHost
{
    /// <summary>点击按钮（或 tags 某一项旁边的按钮，<paramref name="item"/> 为该项）。</summary>
    void RunButton(PluginFieldViewModel field, string? item);

    /// <summary>打开 tags 的候选列表。</summary>
    void ShowCandidates(PluginFieldViewModel field);
}

/// <summary>
/// 插件设置：按 plugin.json 的 settings 生成的表单。关闭时调用 onClosed：保存传入新的值，取消或所在窗口关闭传入 null。
/// </summary>
public sealed partial class PluginSettingsDialogViewModel : ViewModelBase, IDisposable, IPluginFormHost
{
    private readonly Action<JsonObject?> _onClosed;
    private readonly NativePlugin _plugin;
    private readonly ObservableCollection<ViewModelBase> _dialogs;
    private PluginTagPickerDialogViewModel? _picker;

    /// <param name="dialogs">所在的对话框层：候选列表在其中打开。</param>
    public PluginSettingsDialogViewModel(NativePlugin plugin, JsonObject values, ObservableCollection<ViewModelBase> dialogs,
        Action<JsonObject?> onClosed)
    {
        _onClosed = onClosed;
        _plugin = plugin;
        _dialogs = dialogs;
        Title = $"{plugin.Name} 设置";
        Scope = new PluginFieldScope(plugin.Manifest?.Settings ?? [], values, this, rowId: null);
        Panels = (plugin.Manifest?.Panels ?? []).Select(p => new PluginPanelSectionViewModel(plugin, p, dialogs)).ToList();
        PluginHost.Instance.SettingsFeedback += OnSettingsFeedback;
    }

    public string Title { get; }

    public PluginFieldScope Scope { get; }

    /// <summary>插件的面板（用户 / 话题的列表，数据由插件保存）。</summary>
    public IReadOnlyList<PluginPanelSectionViewModel> Panels { get; }

    /// <summary>有列表设置项或面板时加宽（左侧列表、右侧内容）。</summary>
    public double DialogWidth => Scope.Fields.Any(f => f.IsList) || Panels.Count > 0 ? 760 : 480;

    [RelayCommand]
    private void Save() => Close(Scope.ToJson());

    [RelayCommand]
    private void Cancel() => Close(null);

    public void Dispose() => Close(null);

    private void Close(JsonObject? values)
    {
        PluginHost.Instance.SettingsFeedback -= OnSettingsFeedback;
        ClosePicker();
        foreach (var panel in Panels)
            panel.ClosePicker();
        _onClosed(values);
    }

    // 以表单当前的值（尚未保存）调用插件；插件的回复由 OnSettingsFeedback 显示在该设置项下方，
    // tags 某一项的按钮显示为加载中，结果显示在按钮上
    public void RunButton(PluginFieldViewModel field, string? item)
    {
        var payload = new JsonObject
        {
            ["key"] = field.Field.Key,
            ["values"] = field.Scope.ToJson(),
            ["settings"] = Scope.ToJson(),
        };
        if (field.Scope.RowId is { } row)
            payload["row"] = row;
        if (item is not null)
            payload["item"] = item;
        var invoked = PluginHost.Instance.InvokeSettingsButton(_plugin, payload.ToJsonString());
        if (item is null)
            field.ResultText = invoked ? "正在执行…" : "请先开启插件";
        else if (field.Tags.FirstOrDefault(t => t.Value == item) is { } tag)
        {
            if (invoked)
                tag.SetRunning();
            else
                tag.SetResult(false, "请先开启插件");
        }
    }

    private void OnSettingsFeedback(object? sender, (NativePlugin Plugin, PluginSettingsFeedback Feedback) e)
    {
        if (e.Plugin != _plugin)
            return;
        var feedback = e.Feedback;
        var scope = feedback.Row is not { } row
            ? Scope
            : Scope.Fields.Where(f => f.IsList).SelectMany(f => f.Rows).FirstOrDefault(r => r.Id == row)?.Scope;
        // 行已删除时忽略
        if (scope?.Fields.FirstOrDefault(f => f.Field.Key == feedback.Key) is not { } field)
            return;
        // tags 某一项的按钮的结果：显示在按钮上（项已删除时忽略）
        if (feedback is { Item: { } item, Ok: { } ok })
        {
            field.Tags.FirstOrDefault(t => t.Value == item)?.SetResult(ok, feedback.Text);
            return;
        }
        if (feedback.Text is not null)
            field.ResultText = feedback.Text;
        if (feedback.Options is not null && field.IsTags)
        {
            field.SetCandidates(feedback.Options);
            // 获取到候选后直接打开列表
            if (feedback.Options.Count > 0)
                ShowCandidates(field);
        }
    }

    public void ShowCandidates(PluginFieldViewModel field)
    {
        ClosePicker();
        _picker = new PluginTagPickerDialogViewModel(field, ClosePicker);
        _dialogs.Add(_picker);
    }

    private void ClosePicker()
    {
        if (_picker is null)
            return;
        _dialogs.Remove(_picker);
        _picker = null;
    }
}

/// <summary>同一层的设置项（顶层，或列表的一行）：其中一项变化时重新计算 visible_when。</summary>
public sealed class PluginFieldScope
{
    /// <param name="host">所在的对话框（测试时可以为 null）。</param>
    /// <param name="rowId">列表的行号（本次运行内唯一）；顶层为 null。</param>
    public PluginFieldScope(IReadOnlyList<PluginSettingField> fields, JsonObject values, IPluginFormHost? host, long? rowId)
    {
        Host = host;
        RowId = rowId;
        Fields = fields.Select(f => new PluginFieldViewModel(f, f.Normalize(values[f.Key]), this)).ToList();
        RefreshVisibility();
    }

    // 构造各项期间（设置初始值时）为空
    public IReadOnlyList<PluginFieldViewModel> Fields { get; } = [];

    public long? RowId { get; }

    internal IPluginFormHost? Host { get; }

    /// <summary>其中一项的文字变化。</summary>
    public event Action? TextChanged;

    internal void RaiseTextChanged() => TextChanged?.Invoke();

    public void RefreshVisibility()
    {
        foreach (var f in Fields)
        {
            f.IsVisible = f.Field.VisibleWhen is not { } when
                || (Fields.FirstOrDefault(o => o.Field.Key == when.Key) is { } other && when.Values.Contains(other.ValueText));
        }
    }

    /// <summary>当前的值（按钮没有值，不包含）。</summary>
    public JsonObject ToJson()
    {
        var result = new JsonObject();
        foreach (var f in Fields.Where(f => !f.IsButton))
            result[f.Field.Key] = f.ToJson();
        return result;
    }
}

/// <summary>
/// 一个设置项。按类型使用 <see cref="Text"/>、<see cref="IsChecked"/>、<see cref="SelectedOption"/>、<see cref="Rows"/>、
/// <see cref="Tags"/> 或 <see cref="SelectedResource"/>。
/// </summary>
public sealed partial class PluginFieldViewModel : ObservableObject
{
    public PluginFieldViewModel(PluginSettingField field, JsonNode value, PluginFieldScope scope)
    {
        Field = field;
        Scope = scope;
        switch (field.Type)
        {
            case PluginSettingType.Bool:
                IsChecked = value.GetValue<bool>();
                break;
            case PluginSettingType.Select:
                var selected = value.GetValue<string>();
                SelectedOption = field.Options.FirstOrDefault(o => o.Value == selected) ?? field.Options[0];
                break;
            case PluginSettingType.List:
                foreach (var row in ((JsonArray)value).OfType<JsonObject>())
                    Rows.Add(new PluginListRowViewModel(this, row));
                SelectedRow = Rows.FirstOrDefault();
                break;
            case PluginSettingType.Tags:
                foreach (var tag in ((JsonArray)value).Select(t => t!.GetValue<string>()))
                    Tags.Add(new PluginTagViewModel(this, tag));
                break;
            case PluginSettingType.Resource:
                ResourceOptions = BuildResourceOptions(field.ResourcePlugin, field.ResourceType, PluginResourceSelection.From(value as JsonObject));
                SelectedResource = ResourceOptions.FirstOrDefault(o => o.Selection == PluginResourceSelection.From(value as JsonObject))
                                   ?? ResourceOptions[0];
                break;
            case PluginSettingType.Button:
                break;
            default:
                Text = value.GetValue<string>();
                break;
        }
    }

    public PluginSettingField Field { get; }

    /// <summary>所在的一层。</summary>
    public PluginFieldScope Scope { get; }

    public string Label => Field.Label;

    public string Hint => Field.Hint;

    public bool HasHint => Hint.Length > 0;

    public string Placeholder => Field.Placeholder;

    public bool IsSingleLine => Field.Type is PluginSettingType.Text or PluginSettingType.Password;

    public bool IsPassword => Field.Type == PluginSettingType.Password;

    /// <summary>密码框显示圆点，其余为 \0（不遮挡）。</summary>
    public char PasswordChar => IsPassword ? '●' : '\0';

    public bool IsMultiline => Field.Type == PluginSettingType.Multiline;

    public bool IsSelect => Field.Type == PluginSettingType.Select;

    public bool IsBool => Field.Type == PluginSettingType.Bool;

    public bool IsList => Field.Type == PluginSettingType.List;

    public bool IsButton => Field.Type == PluginSettingType.Button;

    public bool IsTags => Field.Type == PluginSettingType.Tags;

    public bool IsResource => Field.Type == PluginSettingType.Resource;

    /// <summary>标题显示在控件上方（复选框、按钮自带文字）。</summary>
    public bool ShowsLabel => !IsBool && !IsButton;

    public IReadOnlyList<PluginSettingOption> Options => Field.Options;

    public ObservableCollection<PluginListRowViewModel> Rows { get; } = [];

    public ObservableCollection<PluginTagViewModel> Tags { get; } = [];

    /// <summary>tags 的候选（插件通过 set_settings_feedback 提供），在候选列表中添加 / 删除。</summary>
    public ObservableCollection<string> Candidates { get; } = [];

    public bool HasCandidates => Candidates.Count > 0;

    /// <summary>resource 的选项：第一项为“不使用”。</summary>
    public IReadOnlyList<PluginResourceOption> ResourceOptions { get; } = [];

    /// <summary>依赖的插件没有发布该类资源（未开启或没有添加）。</summary>
    public bool HasNoResources => IsResource && ResourceOptions.All(o => o.Selection is null || !o.IsAvailable);

    public string NoResourcesText => IsResource
        ? $"没有可选项：请先开启插件“{PluginHost.Instance.Plugins.FirstOrDefault(p => p.Id == Field.ResourcePlugin)?.Manifest?.Name ?? Field.ResourcePlugin}”并在其中添加"
        : "";

    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;

    [ObservableProperty]
    public partial string Text { get; set; } = "";

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    [ObservableProperty]
    public partial PluginSettingOption? SelectedOption { get; set; }

    [ObservableProperty]
    public partial PluginResourceOption? SelectedResource { get; set; }

    /// <summary>列表中选中的行：右侧显示它的设置项。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedRow))]
    public partial PluginListRowViewModel? SelectedRow { get; set; }

    public bool HasSelectedRow => SelectedRow is not null;

    /// <summary>tags 的输入框。</summary>
    [ObservableProperty]
    public partial string? TagInput { get; set; }

    /// <summary>设置项下方的文字：插件通过 set_settings_feedback 返回的结果。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    public partial string ResultText { get; set; } = "";

    public bool HasResult => ResultText.Length > 0;

    /// <summary>visible_when 比较用的值：下拉框为选项值，复选框为 true / false，其余为文字。</summary>
    public string ValueText => Field.Type switch
    {
        PluginSettingType.Select => SelectedOption?.Value ?? "",
        PluginSettingType.Bool => IsChecked ? "true" : "false",
        _ => Text,
    };

    partial void OnTextChanged(string value)
    {
        Scope.RefreshVisibility();
        Scope.RaiseTextChanged();
    }

    partial void OnIsCheckedChanged(bool value) => Scope.RefreshVisibility();

    partial void OnSelectedOptionChanged(PluginSettingOption? value) => Scope.RefreshVisibility();

    [RelayCommand]
    private void Run() => Scope.Host?.RunButton(this, null);

    /// <summary>新建一行并选中。</summary>
    [RelayCommand]
    private void AddRow()
    {
        var row = new PluginListRowViewModel(this, []);
        Rows.Add(row);
        SelectedRow = row;
    }

    /// <summary>删除一行；删除的是选中的行时选中原位置的下一行（没有时为上一行）。</summary>
    internal void RemoveRow(PluginListRowViewModel row)
    {
        var index = Rows.IndexOf(row);
        if (index < 0)
            return;
        Rows.RemoveAt(index);
        if (SelectedRow == row)
            SelectedRow = Rows.Count == 0 ? null : Rows[Math.Min(index, Rows.Count - 1)];
    }

    /// <summary>把输入框中的文字添加为一项（已有的忽略）。</summary>
    [RelayCommand]
    private void AddTag()
    {
        AddTagValue(TagInput ?? "");
        TagInput = "";
    }

    internal bool HasTag(string value) => Tags.Any(t => t.Value == value);

    /// <summary>添加一项（去掉首尾空白；空的、已有的忽略）。</summary>
    internal void AddTagValue(string value)
    {
        value = value.Trim();
        if (value.Length > 0 && !HasTag(value))
            Tags.Add(new PluginTagViewModel(this, value));
    }

    internal void RemoveTagValue(string value)
    {
        if (Tags.FirstOrDefault(t => t.Value == value) is { } tag)
            Tags.Remove(tag);
    }

    internal void RemoveTag(PluginTagViewModel tag) => Tags.Remove(tag);

    internal void RunTag(PluginTagViewModel tag) => Scope.Host?.RunButton(this, tag.Value);

    internal void SetCandidates(IReadOnlyList<string> candidates)
    {
        Candidates.Clear();
        foreach (var c in candidates)
            Candidates.Add(c);
        OnPropertyChanged(nameof(HasCandidates));
    }

    // 已保存的选择不在可选项中时（发布者未开启或已删除）仍保留，标为不可用
    private static List<PluginResourceOption> BuildResourceOptions(string pluginId, string type, PluginResourceSelection? saved)
    {
        var options = new List<PluginResourceOption> { new(null, "不使用", true) };
        foreach (var r in PluginResources.Instance.Available(pluginId, type))
        {
            foreach (var item in r.Items)
                options.Add(new PluginResourceOption(new PluginResourceSelection(type, r.PluginId, r.Id, item), $"{r.Name} / {item}", true));
        }
        if (saved is not null && options.All(o => o.Selection != saved))
            options.Add(new PluginResourceOption(saved, $"{saved.ResourceId} / {saved.Item}（不可用）", false));
        return options;
    }

    public JsonNode ToJson() => Field.Type switch
    {
        PluginSettingType.Bool => JsonValue.Create(IsChecked),
        PluginSettingType.Select => JsonValue.Create(SelectedOption?.Value ?? ""),
        PluginSettingType.List => new JsonArray([.. Rows.Select(r => (JsonNode)r.Scope.ToJson())]),
        PluginSettingType.Tags => new JsonArray([.. Tags.Select(t => (JsonNode)JsonValue.Create(t.Value))]),
        PluginSettingType.Resource => SelectedResource?.Selection?.ToJson() ?? new JsonObject(),
        _ => JsonValue.Create(Text),
    };
}

/// <summary>列表设置项的一行。</summary>
public sealed partial class PluginListRowViewModel : ObservableObject
{
    private static long _nextId;
    private readonly PluginFieldViewModel _owner;

    public PluginListRowViewModel(PluginFieldViewModel owner, JsonObject values)
    {
        _owner = owner;
        Id = Interlocked.Increment(ref _nextId);
        Scope = new PluginFieldScope(owner.Field.Fields, values, owner.Scope.Host, Id);
        Scope.TextChanged += () => OnPropertyChanged(nameof(Title));
    }

    /// <summary>行号：本次运行内唯一，插件回复设置窗口时用于定位这一行（删除、增加其他行后不变）。</summary>
    public long Id { get; }

    public PluginFieldScope Scope { get; }

    /// <summary>左侧列表中显示的名称：第一个文本设置项的值，空时为“未命名”。</summary>
    public string Title => Scope.Fields.FirstOrDefault(f => f.Field.Type == PluginSettingType.Text)?.Text.Trim() is { Length: > 0 } t
        ? t
        : "未命名";

    [RelayCommand]
    private void Remove() => _owner.RemoveRow(this);
}

/// <summary>tags 设置项的一项。</summary>
public sealed partial class PluginTagViewModel(PluginFieldViewModel owner, string value) : ObservableObject
{
    public string Value { get; } = value;

    public string ItemButton => owner.Field.ItemButton;

    public bool HasItemButton => ItemButton.Length > 0;

    /// <summary>按钮的状态：null 为未运行，Running 时显示加载中。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsOk), nameof(IsFailed))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial bool IsRunning { get; private set; }

    /// <summary>上一次的结果：null 为没有结果。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsOk), nameof(IsFailed))]
    public partial bool? Result { get; private set; }

    /// <summary>失败的原因（按钮的提示）。</summary>
    [ObservableProperty]
    public partial string? ErrorText { get; private set; }

    public bool IsIdle => !IsRunning && Result is null;

    public bool IsOk => !IsRunning && Result == true;

    public bool IsFailed => !IsRunning && Result == false;

    internal void SetRunning()
    {
        Result = null;
        ErrorText = null;
        IsRunning = true;
    }

    internal void SetResult(bool ok, string? error)
    {
        IsRunning = false;
        Result = ok;
        ErrorText = ok ? null : string.IsNullOrEmpty(error) ? "失败" : error;
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void Run() => owner.RunTag(this);

    private bool CanRun() => !IsRunning;

    [RelayCommand]
    private void Remove() => owner.RemoveTag(this);
}

/// <summary>resource 设置项的一个选项；<see cref="Selection"/> 为 null 表示不使用。</summary>
public sealed record PluginResourceOption(PluginResourceSelection? Selection, string Label, bool IsAvailable)
{
    public override string ToString() => Label;
}
