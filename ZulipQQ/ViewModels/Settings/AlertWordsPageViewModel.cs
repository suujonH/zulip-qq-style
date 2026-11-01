using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels.Settings;

/// <summary>提醒词：消息中出现这些词时按“@我”的规则提醒。保存在服务器。</summary>
public sealed partial class AlertWordsPageViewModel : SettingsPageViewModel
{
    public AlertWordsPageViewModel(SettingsViewModel owner) : base(owner)
    {
        Session.Settings.AlertWordsChanged += OnWordsChanged;
        Build();
    }

    public ObservableCollection<string> Words { get; } = [];

    public bool HasWords => Words.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial string NewWord { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial bool IsBusy { get; private set; }

    private bool CanAdd => !IsBusy && NewWord.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddAsync()
    {
        var word = NewWord.Trim();
        if (Words.Contains(word, StringComparer.OrdinalIgnoreCase))
        {
            NewWord = "";
            return;
        }
        IsBusy = true;
        try
        {
            await Session.Settings.AddAlertWordAsync(word);
            NewWord = "";
        }
        catch (Exception ex) when (!Session.IsDisposed)
        {
            Log.Warn("添加提醒词失败", ex);
            Owner.ShowToast("添加失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RemoveAsync(string word)
    {
        try
        {
            await Session.Settings.RemoveAlertWordAsync(word);
        }
        catch (Exception ex) when (!Session.IsDisposed)
        {
            Log.Warn("删除提醒词失败", ex);
            Owner.ShowToast("删除失败");
        }
    }

    public override void Close() => Session.Settings.AlertWordsChanged -= OnWordsChanged;

    private void Build()
    {
        Words.Clear();
        foreach (var word in Session.Settings.AlertWords)
            Words.Add(word);
        OnPropertyChanged(nameof(HasWords));
    }

    private void OnWordsChanged(object? sender, EventArgs e) => Build();
}
