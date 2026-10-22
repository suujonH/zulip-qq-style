using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Data;
using ZulipQQ.Services;
using ZulipQQ.Zulip;

namespace ZulipQQ.ViewModels;

/// <summary>登录窗中的一个历史账号。</summary>
public sealed class SavedAccountViewModel(AccountRow row) : ViewModelBase
{
    public AccountRow Row { get; } = row;

    public string Email => Row.Email;

    /// <summary>头像文字与显示名：有昵称用昵称，否则用邮箱。</summary>
    public string DisplayName => string.IsNullOrEmpty(Row.FullName) ? Row.Email : Row.FullName;

    public string RealmUrl => Row.RealmUrl;
}

/// <summary>
/// 登录窗：邮箱 + 密码换取 API key，或导入 zuliprc，或使用历史账号保存的 key。
/// 取得账号后调用 <c>completeLogin</c>（由 App 创建会话并打开主窗口），期间的错误显示在窗口中。
/// </summary>
public partial class LoginViewModel : ViewModelBase
{
    private readonly AccountRepository _accounts;
    private readonly Func<AccountRow, CancellationToken, Task> _completeLogin;
    private AccountRow? _remembered;
    private CancellationTokenSource? _cts;

    public LoginViewModel(AccountRepository accounts, Func<AccountRow, CancellationToken, Task> completeLogin,
        AccountRow? prefill = null, string? error = null)
    {
        _accounts = accounts;
        _completeLogin = completeLogin;
        foreach (var row in accounts.GetAll())
            Accounts.Add(new SavedAccountViewModel(row));
        if ((prefill ?? Accounts.FirstOrDefault()?.Row) is { } initial)
            Fill(initial);
        ErrorText = error;
    }

    public ObservableCollection<SavedAccountViewModel> Accounts { get; } = [];

    public bool HasAccounts => Accounts.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSavedKey), nameof(PasswordPlaceholder))]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    public partial string ServerUrl { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSavedKey), nameof(PasswordPlaceholder))]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    public partial string Email { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    public partial string Password { get; set; } = "";

    [ObservableProperty]
    public partial bool AutoLogin { get; set; }

    [ObservableProperty]
    public partial bool AllowInvalidCert { get; set; }

    [ObservableProperty]
    public partial bool IsAdvancedOpen { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand), nameof(LoginWithZuliprcCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string BusyText { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    /// <summary>当前服务器和邮箱对应的历史账号保存了 key，可以不输入密码直接登录。</summary>
    public bool HasSavedKey =>
        _remembered is { } r && r.RealmUrl == NormalizeRealm(ServerUrl)
                             && string.Equals(r.Email, Email.Trim(), StringComparison.OrdinalIgnoreCase);

    public string PasswordPlaceholder => HasSavedKey ? "已记住密码" : "密码";

    private bool CanLogin =>
        !IsBusy && !string.IsNullOrWhiteSpace(ServerUrl) && !string.IsNullOrWhiteSpace(Email)
        && (Password.Length > 0 || HasSavedKey);

    private bool CanUseZuliprc => !IsBusy;

    /// <summary>取消进行中的登录（登录窗被关闭时调用）。</summary>
    public void Cancel() => _cts?.Cancel();

    /// <summary>用保存的 key 自动登录（启动时）。</summary>
    public Task AutoLoginAsync(AccountRow account)
    {
        Fill(account);
        return RunAsync(_ => Task.FromResult(account));
    }

    [RelayCommand(CanExecute = nameof(CanLogin))]
    private Task LoginAsync()
    {
        // 输入了密码就用密码登录；否则使用保存的 key
        if (Password.Length == 0 && HasSavedKey)
        {
            var saved = _remembered! with { AutoLogin = AutoLogin, AllowInvalidCert = AllowInvalidCert };
            return RunAsync(_ =>
            {
                _accounts.Upsert(saved);
                return Task.FromResult(saved);
            });
        }
        return RunAsync(LoginWithPasswordAsync);
    }

    /// <summary>用 zuliprc 文件中的 email、key、site 登录，不需要密码。</summary>
    [RelayCommand(CanExecute = nameof(CanUseZuliprc))]
    public Task LoginWithZuliprcAsync(string path) => RunAsync(async _ =>
    {
        var config = ParseZuliprc(await File.ReadAllTextAsync(path))
                     ?? throw new UserFacingException("zuliprc 中缺少 [api] 段的 email、key 或 site");
        var realm = NormalizeRealm(config.Site) ?? throw new UserFacingException("zuliprc 中的 site 无效");
        ServerUrl = realm;
        Email = config.Email;
        return SaveAccount(realm, config.Email, _accounts.Find(realm, config.Email)?.UserId ?? 0, config.Key);
    });

    [RelayCommand]
    private void SelectAccount(SavedAccountViewModel account) => Fill(account.Row);

    /// <summary>从历史账号中删除（同时删除保存的 key）。</summary>
    [RelayCommand]
    private void RemoveAccount(SavedAccountViewModel account)
    {
        _accounts.Delete(account.Row.Id);
        Accounts.Remove(account);
        OnPropertyChanged(nameof(HasAccounts));
        if (_remembered?.Id == account.Row.Id)
        {
            _remembered = null;
            OnPropertyChanged(nameof(HasSavedKey));
            OnPropertyChanged(nameof(PasswordPlaceholder));
            LoginCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task<AccountRow> LoginWithPasswordAsync(CancellationToken ct)
    {
        var realm = NormalizeRealm(ServerUrl) ?? throw new UserFacingException("服务器地址无效");
        var realmUri = new Uri(realm);
        var settings = await ZulipClient.GetServerSettingsAsync(realmUri, AllowInvalidCert, ct);
        if (settings.ZulipFeatureLevel < ZulipSession.MinFeatureLevel)
            throw new UserFacingException("服务器版本过低，需要 Zulip 11.0 及以上");
        if (!settings.EmailAuthEnabled)
            throw new UserFacingException("该组织未开启邮箱密码登录，请使用 zuliprc 登录");

        var result = await ZulipClient.FetchApiKeyAsync(realmUri, Email.Trim(), Password, AllowInvalidCert, ct);
        return SaveAccount(realm, result.Email, result.UserId, result.ApiKey);
    }

    // 保存账号（key 用 DPAPI 加密）；昵称和头像在会话创建后用 register 的结果更新
    private AccountRow SaveAccount(string realm, string email, long userId, string apiKey)
    {
        var existing = _accounts.Find(realm, email);
        var row = new AccountRow(0, realm, email, userId, existing?.FullName ?? "", existing?.AvatarUrl,
            AccountRepository.ProtectKey(apiKey), AllowInvalidCert, AutoLogin, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return row with { Id = _accounts.Upsert(row) };
    }

    private async Task RunAsync(Func<CancellationToken, Task<AccountRow>> obtainAccount)
    {
        if (IsBusy)
            return;
        ErrorText = null;
        IsBusy = true;
        BusyText = "正在登录…";
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            var account = await obtainAccount(ct);
            BusyText = "正在加载…";
            await _completeLogin(account, ct);
            Password = "";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warn("登录失败", ex);
            ErrorText = DescribeError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Fill(AccountRow account)
    {
        _remembered = account;
        ServerUrl = account.RealmUrl;
        Email = account.Email;
        Password = "";
        AutoLogin = account.AutoLogin;
        AllowInvalidCert = account.AllowInvalidCert;
        OnPropertyChanged(nameof(HasSavedKey));
        OnPropertyChanged(nameof(PasswordPlaceholder));
        LoginCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 规范化服务器地址：去掉首尾空白，缺少 scheme 时补 https://，去掉末尾的 / 以及查询串。
    /// 不是 http / https 地址时返回 null。
    /// </summary>
    public static string? NormalizeRealm(string? input)
    {
        var text = input?.Trim() ?? "";
        if (text.Length == 0)
            return null;
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || uri.Host.Length == 0)
            return null;
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    /// <summary>解析 zuliprc（INI 格式）中 [api] 段的 email、key、site；缺少任何一项时返回 null。</summary>
    public static (string Email, string Key, string Site)? ParseZuliprc(string text)
    {
        string? email = null, key = null, site = null;
        var inApi = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
                continue;
            if (line[0] == '[')
            {
                inApi = line.Equals("[api]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inApi)
                continue;

            var sep = line.IndexOfAny(['=', ':']);
            if (sep <= 0)
                continue;
            var name = line[..sep].Trim().ToLowerInvariant();
            var value = line[(sep + 1)..].Trim();
            switch (name)
            {
                case "email":
                    email = value;
                    break;
                case "key":
                    key = value;
                    break;
                case "site":
                    site = value;
                    break;
            }
        }
        return string.IsNullOrEmpty(email) || string.IsNullOrEmpty(key) || string.IsNullOrEmpty(site)
            ? null
            : (email, key, site);
    }

    /// <summary>把登录过程中的异常转成显示给用户的文字。</summary>
    public static string DescribeError(Exception ex)
    {
        switch (ex)
        {
            case UserFacingException:
                return ex.Message;
            case ZulipApiException { Code: "AUTHENTICATION_FAILED" }:
                return "邮箱或密码错误";
            case ZulipApiException { Code: "PASSWORD_AUTH_DISABLED" }:
                return "该组织未开启密码登录";
            case ZulipApiException { Code: "USER_DEACTIVATED" }:
                return "该账号已被停用";
            case ZulipApiException { Code: "REALM_DEACTIVATED" }:
                return "该组织已被停用";
            case ZulipApiException { IsAuthError: true }:
                return "登录信息已失效，请重新输入密码";
            case ZulipApiException api:
                return $"服务器返回错误：{api.Message}";
            case CryptographicException:
                return "保存的登录信息已失效，请重新输入密码";
            case TimeoutException:
                return "连接服务器超时";
            case HttpRequestException http:
                for (Exception? inner = http.InnerException; inner is not null; inner = inner.InnerException)
                {
                    if (inner is AuthenticationException)
                        return "证书无效，若为测试服务器请在高级中允许不受信任的证书";
                }
                return $"无法连接服务器：{http.Message}";
            default:
                return $"登录失败：{ex.Message}";
        }
    }
}
