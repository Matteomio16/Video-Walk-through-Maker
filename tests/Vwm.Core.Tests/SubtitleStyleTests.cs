using Vwm.Core.Render;
using Xunit;

namespace Vwm.Core.Tests;

public class SubtitleStyleTests
{
    [Fact]
    public void Style_reflects_chosen_font_and_size()
    {
        var opt = new RenderOptions { SubtitleFont = "Verdana", SubtitleFontSize = 24 };
        Assert.Contains("FontName=Verdana", opt.SubtitleStyle);
        Assert.Contains("FontSize=24", opt.SubtitleStyle);
    }

    [Theory]
    [InlineData(SubtitlePosition.Bottom, 2)]
    [InlineData(SubtitlePosition.Middle, 10)]
    [InlineData(SubtitlePosition.Top, 6)]
    public void Position_maps_to_libass_alignment(SubtitlePosition pos, int alignment)
    {
        var opt = new RenderOptions { SubtitlePosition = pos };
        Assert.Contains($"Alignment={alignment}", opt.SubtitleStyle);
    }

    [Fact]
    public void Default_style_is_bottom_arial_16_box()
    {
        var opt = new RenderOptions();
        Assert.Contains("FontName=Arial", opt.SubtitleStyle);
        Assert.Contains("FontSize=16", opt.SubtitleStyle);
        Assert.Contains("Alignment=2", opt.SubtitleStyle);
        Assert.Contains("BorderStyle=4", opt.SubtitleStyle); // the grey box
    }

    [Fact]
    public void Box_background_draws_the_opaque_box()
    {
        var opt = new RenderOptions { SubtitleBackground = SubtitleBackgroundStyle.Box };
        Assert.Contains("BorderStyle=4", opt.SubtitleStyle);
        Assert.Contains("BackColour=&H90000000", opt.SubtitleStyle);
    }

    [Fact]
    public void Shadow_background_uses_outline_and_shadow_not_box()
    {
        var opt = new RenderOptions { SubtitleBackground = SubtitleBackgroundStyle.Shadow };
        Assert.Contains("BorderStyle=1", opt.SubtitleStyle);
        Assert.Contains("Shadow=2", opt.SubtitleStyle);
        Assert.DoesNotContain("BorderStyle=4", opt.SubtitleStyle);
    }
}
