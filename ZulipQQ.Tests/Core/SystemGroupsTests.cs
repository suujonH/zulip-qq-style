using System.Linq;
using Xunit;
using ZulipQQ.Core;
using ZulipQQ.Zulip;
using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.Tests.Core;

public class SystemGroupsTests
{
    [Fact]
    public void ChoicesFor_Unrestricted_ListsAll()
    {
        var names = SystemGroups.ChoicesFor(PermissionSettingInfo.Unrestricted).Select(c => c.Name).ToList();
        Assert.Equal(SystemGroups.All.Select(g => g.Name), names);
    }

    [Fact]
    public void ChoicesFor_HonorsNobodyAndEveryone()
    {
        var names = SystemGroups.ChoicesFor(new PermissionSettingInfo(false, false, [])).Select(c => c.Name).ToList();
        Assert.DoesNotContain(SystemGroups.Everyone, names);
        Assert.DoesNotContain(SystemGroups.Nobody, names);
        Assert.Contains("role:members", names);
    }

    [Fact]
    public void ChoicesFor_AllowedSystemGroupsRestricts()
    {
        var info = new PermissionSettingInfo(true, true, ["role:administrators", "role:owners"]);
        Assert.Equal(["role:administrators", "role:owners"], SystemGroups.ChoicesFor(info).Select(c => c.Name));
    }

    [Fact]
    public void PermissionSettingInfos_FromFixture()
    {
        var reg = ZulipJson.Deserialize<RegisterResult>(Fixture.Json("register.json"));
        var infos = reg.PermissionSettingInfos();
        Assert.True(infos.TryGetValue(("stream", "can_subscribe_group"), out var subscribe));
        Assert.False(subscribe!.AllowEveryone);
        Assert.True(subscribe.AllowNobody);
    }
}
