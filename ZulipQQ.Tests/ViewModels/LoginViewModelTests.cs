using System.Net.Http;
using System.Security.Authentication;
using ZulipQQ.Services;
using ZulipQQ.ViewModels;
using ZulipQQ.Zulip;

namespace ZulipQQ.Tests.ViewModels;

public class LoginViewModelTests
{
    [Theory]
    [InlineData("chat.example.com", "https://chat.example.com")]
    [InlineData("  https://chat.example.com/  ", "https://chat.example.com")]
    [InlineData("https://localhost:8443/", "https://localhost:8443")]
    [InlineData("http://10.0.0.2:9991", "http://10.0.0.2:9991")]
    [InlineData("https://chat.example.com/zulip/?x=1", "https://chat.example.com/zulip")]
    [InlineData("", null)]
    [InlineData("ftp://chat.example.com", null)]
    [InlineData("https://", null)]
    public void NormalizeRealm(string input, string? expected) =>
        Assert.Equal(expected, LoginViewModel.NormalizeRealm(input));

    [Fact]
    public void ParseZuliprc_ReadsApiSection()
    {
        const string text = "# comment\r\n[other]\r\nemail=wrong@x\r\n[api]\r\nemail = a@b.test\r\nkey=abc123\r\nsite=https://chat.b.test\r\n";
        Assert.Equal(("a@b.test", "abc123", "https://chat.b.test"), LoginViewModel.ParseZuliprc(text));
    }

    [Fact]
    public void ParseZuliprc_MissingField_ReturnsNull()
    {
        Assert.Null(LoginViewModel.ParseZuliprc("[api]\nemail=a@b\nkey=k\n"));
        Assert.Null(LoginViewModel.ParseZuliprc("email=a@b\nkey=k\nsite=s\n"));
    }

    [Fact]
    public void DescribeError_MapsKnownErrors()
    {
        Assert.Equal("邮箱或密码错误",
            LoginViewModel.DescribeError(new ZulipApiException(403, "AUTHENTICATION_FAILED", "x", null)));
        Assert.Equal("该组织未开启密码登录",
            LoginViewModel.DescribeError(new ZulipApiException(403, "PASSWORD_AUTH_DISABLED", "x", null)));
        Assert.Equal("自定义", LoginViewModel.DescribeError(new UserFacingException("自定义")));
        Assert.Equal("证书无效，若为测试服务器请在高级中允许不受信任的证书",
            LoginViewModel.DescribeError(new HttpRequestException("ssl", new AuthenticationException("bad"))));
        Assert.StartsWith("无法连接服务器：", LoginViewModel.DescribeError(new HttpRequestException("refused")));
    }
}
