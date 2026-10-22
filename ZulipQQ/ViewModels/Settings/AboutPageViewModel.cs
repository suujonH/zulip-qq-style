using System.Globalization;
using System.Reflection;

namespace ZulipQQ.ViewModels.Settings;

/// <summary>关于：程序版本与构建、组织与服务器信息。</summary>
public sealed class AboutPageViewModel : SettingsPageViewModel
{
    public AboutPageViewModel(SettingsViewModel owner) : base(owner)
    {
        // CI 通过 -p:Version 注入版本；SDK 会在末尾追加 +{commit}
        var info = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var plus = info.IndexOf('+');
        Version = plus < 0 ? info : info[..plus];
        Build = plus < 0 ? "" : info[(plus + 1)..];
        if (Build.Length > 7)
            Build = Build[..7];
    }

    public string Version { get; }

    public string Build { get; }

    public bool HasBuild => Build.Length > 0;

    public string RealmName => Session.Realm.Name;

    public string RealmUrl => Session.Realm.Url.ToString();

    public string ServerVersion => Session.Realm.ZulipVersion;

    public string FeatureLevel => Session.Realm.FeatureLevel.ToString(CultureInfo.InvariantCulture);
}
