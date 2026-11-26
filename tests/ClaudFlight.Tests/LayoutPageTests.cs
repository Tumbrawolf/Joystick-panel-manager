using ClaudFlight.Core.Models;
using Xunit;

namespace ClaudFlight.Tests;

public sealed class LayoutPageTests
{
    private static LayoutMarker Marker(double x, double y) =>
        new() { DeviceGuid = Guid.NewGuid(), Kind = ControlKind.Button, Index = 0, X = x, Y = y };

    [Fact]
    public void Empty_page_puts_text_in_the_middle()
    {
        Assert.Equal((0.5, 0.5), new LayoutPage().FreeSpotForText());
    }

    [Fact]
    public void Text_avoids_markers_and_other_labels()
    {
        var page = new LayoutPage
        {
            Markers = [Marker(0.5, 0.5), Marker(0.45, 0.5), Marker(0.55, 0.5)],
            Texts = [new LayoutText { X = 0.5, Y = 0.45 }],
        };

        var (x, y) = page.FreeSpotForText();

        Assert.DoesNotContain(page.Markers, m => Math.Abs(m.X - x) < 0.09 && Math.Abs(m.Y - y) < 0.05);
        Assert.DoesNotContain(page.Texts, t => Math.Abs(t.X - x) < 0.08 && Math.Abs(t.Y - y) < 0.04);
    }

    [Fact]
    public void Labels_added_one_after_another_never_stack()
    {
        var page = new LayoutPage { Markers = [Marker(0.5, 0.5)] };
        for (var i = 0; i < 10; i++)
        {
            var (x, y) = page.FreeSpotForText();
            page.Texts.Add(new LayoutText { X = x, Y = y });
        }

        Assert.Equal(10, page.Texts.Select(t => (t.X, t.Y)).Distinct().Count());
    }
}
