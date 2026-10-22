using ZulipQQ.Core;

namespace ZulipQQ.Tests.Core;

public class EdgeDockTests
{
    private static readonly PixelBox Work = new(0, 0, 1920, 1040);

    [Theory]
    [InlineData(100, 0, DockEdge.Top)]
    [InlineData(100, -20, DockEdge.Top)]
    [InlineData(0, 0, DockEdge.Top)]
    [InlineData(0, 100, DockEdge.Left)]
    [InlineData(-30, 100, DockEdge.Left)]
    [InlineData(1546, 100, DockEdge.Right)]
    [InlineData(1600, 100, DockEdge.Right)]
    [InlineData(100, 100, DockEdge.None)]
    public void Detect(int x, int y, DockEdge expected) =>
        Assert.Equal(expected, EdgeDock.Detect(new PixelBox(x, y, 374, 700), Work));

    [Fact]
    public void DetectOnSecondaryScreen()
    {
        var work = new PixelBox(-1280, 0, 1280, 984);
        Assert.Equal(DockEdge.Left, EdgeDock.Detect(new PixelBox(-1280, 100, 374, 700), work));
        Assert.Equal(DockEdge.Right, EdgeDock.Detect(new PixelBox(-374, 100, 374, 700), work));
        Assert.Equal(DockEdge.None, EdgeDock.Detect(new PixelBox(-1000, 100, 374, 700), work));
    }

    [Fact]
    public void Positions()
    {
        var w = new PixelBox(100, -20, 374, 700);
        Assert.Equal((100, 0), EdgeDock.ShownPosition(DockEdge.Top, w, Work));
        Assert.Equal((100, -696), EdgeDock.HiddenPosition(DockEdge.Top, w, Work));
        Assert.Equal((-370, -20), EdgeDock.HiddenPosition(DockEdge.Left, w, Work));
        Assert.Equal((1916, -20), EdgeDock.HiddenPosition(DockEdge.Right, w, Work));
        Assert.Equal((1546, -20), EdgeDock.ShownPosition(DockEdge.Right, w, Work));
    }

    [Fact]
    public void ContainsWithMargin()
    {
        var box = new PixelBox(10, 10, 100, 100);
        Assert.True(box.Contains(109, 109));
        Assert.False(box.Contains(110, 50));
        Assert.True(box.Contains(115, 50, margin: 8));
        Assert.False(box.Contains(2, 50, margin: 7));
    }
}
