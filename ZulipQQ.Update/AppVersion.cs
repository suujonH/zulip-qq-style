using System.Globalization;

namespace ZulipQQ.Update;

/// <summary>
/// 语义版本（<c>主.次.修订[-预发布][+构建]</c>，可带前缀 v）。比较规则与 SemVer 2.0 相同：预发布版低于对应的正式版，构建信息不参与比较。
/// </summary>
public sealed class AppVersion : IComparable<AppVersion>
{
    private readonly string[] _prerelease;

    private AppVersion(int major, int minor, int patch, string[] prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        _prerelease = prerelease;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    public bool IsPrerelease => _prerelease.Length > 0;

    /// <summary>解析版本号；格式不合法（包括 <c>0.01</c> 这样有前导零的数字）时返回 null。</summary>
    public static AppVersion? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var value = text.Trim();
        if (value[0] is 'v' or 'V')
            value = value[1..];
        var plus = value.IndexOf('+');
        if (plus >= 0)
        {
            if (!IsIdentifiers(value[(plus + 1)..]))
                return null;
            value = value[..plus];
        }
        string[] prerelease = [];
        var dash = value.IndexOf('-');
        if (dash >= 0)
        {
            var tail = value[(dash + 1)..];
            if (!IsIdentifiers(tail))
                return null;
            prerelease = tail.Split('.');
            // 数字标识符不能有前导零
            if (prerelease.Any(p => p.All(char.IsAsciiDigit) && p.Length > 1 && p[0] == '0'))
                return null;
            value = value[..dash];
        }
        var parts = value.Split('.');
        if (parts.Length != 3
            || !TryParseNumber(parts[0], out var major)
            || !TryParseNumber(parts[1], out var minor)
            || !TryParseNumber(parts[2], out var patch))
            return null;
        return new AppVersion(major, minor, patch, prerelease);
    }

    public int CompareTo(AppVersion? other)
    {
        if (other is null)
            return 1;
        var result = Major.CompareTo(other.Major);
        if (result == 0)
            result = Minor.CompareTo(other.Minor);
        if (result == 0)
            result = Patch.CompareTo(other.Patch);
        return result != 0 ? result : ComparePrerelease(_prerelease, other._prerelease);
    }

    public bool IsNewerThan(AppVersion other) => CompareTo(other) > 0;

    public override string ToString()
    {
        var core = string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
        return IsPrerelease ? core + "-" + string.Join('.', _prerelease) : core;
    }

    private static int ComparePrerelease(string[] a, string[] b)
    {
        // 没有预发布标识的是正式版，高于同号的预发布版
        if (a.Length == 0 || b.Length == 0)
            return b.Length.CompareTo(a.Length);
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var result = CompareIdentifier(a[i], b[i]);
            if (result != 0)
                return result;
        }
        return a.Length.CompareTo(b.Length);
    }

    private static int CompareIdentifier(string a, string b)
    {
        var aNumeric = a.All(char.IsAsciiDigit);
        var bNumeric = b.All(char.IsAsciiDigit);
        if (aNumeric && bNumeric)
        {
            // 数字按长度再按字典序比较，不会溢出（已排除前导零）
            var length = a.Length.CompareTo(b.Length);
            return length != 0 ? length : string.CompareOrdinal(a, b);
        }
        // 数字标识符低于字母标识符
        if (aNumeric != bNumeric)
            return aNumeric ? -1 : 1;
        return string.CompareOrdinal(a, b);
    }

    private static bool TryParseNumber(string text, out int value)
    {
        value = 0;
        if (text.Length == 0 || !text.All(char.IsAsciiDigit) || (text.Length > 1 && text[0] == '0'))
            return false;
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    // 以点分隔的非空标识符，只含字母、数字和连字符
    private static bool IsIdentifiers(string text) =>
        text.Split('.').All(p => p.Length > 0 && p.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
}
