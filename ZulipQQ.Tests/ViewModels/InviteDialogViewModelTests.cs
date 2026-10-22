using ZulipQQ.ViewModels;

namespace ZulipQQ.Tests.ViewModels;

public class InviteDialogViewModelTests
{
    [Fact]
    public void ParseEmails_SplitsOnCommasSemicolonsAndWhitespace() =>
        Assert.Equal(["a@x.test", "b@x.test", "c@x.test", "d@x.test"],
            InviteDialogViewModel.ParseEmails(" a@x.test, b@x.test；c@x.test\r\n\nd@x.test，"));

    [Fact]
    public void ParseEmails_DedupesIgnoringCase() =>
        Assert.Equal(["A@x.test"], InviteDialogViewModel.ParseEmails("A@x.test a@X.test"));

    [Fact]
    public void ParseEmails_Empty() => Assert.Empty(InviteDialogViewModel.ParseEmails(" ,\n "));
}
