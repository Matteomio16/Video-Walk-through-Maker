using System.Runtime.InteropServices;
using Vwm.Core.Tools;
using Xunit;

namespace Vwm.Core.Tests;

public class LocalPathTests
{
    [Theory]
    [InlineData("http://evil.example/video.mp4")]
    [InlineData("https://evil.example/video.mp4")]
    [InlineData("rtsp://cam.local/stream")]
    [InlineData("smb://server/share/clip.mp4")]
    [InlineData("data:video/mp4;base64,AAAA")]
    public void RequireInputFile_rejects_urls(string url)
    {
        var ex = Assert.Throws<ArgumentException>(() => LocalPath.RequireInputFile(url, "Input video"));
        Assert.Contains("local file", ex.Message);
    }

    [Fact]
    public void RequireInputFile_rejects_unc_share_on_windows()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;
        var ex = Assert.Throws<ArgumentException>(
            () => LocalPath.RequireInputFile(@"\\server\share\clip.mp4", "Input video"));
        Assert.Contains("network share", ex.Message);
    }

    [Fact]
    public void RequireInputFile_accepts_existing_local_file()
    {
        var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".mp4");
        File.WriteAllText(tmp, "x");
        try
        {
            var resolved = LocalPath.RequireInputFile(tmp, "Input video");
            Assert.Equal(Path.GetFullPath(tmp), resolved);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void RequireInputFile_rejects_missing_local_file()
    {
        var missing = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".mp4");
        Assert.Throws<ArgumentException>(() => LocalPath.RequireInputFile(missing, "Input video"));
    }

    [Fact]
    public void RequireOutputFile_rejects_url()
    {
        Assert.Throws<ArgumentException>(
            () => LocalPath.RequireOutputFile("https://evil.example/out.mp4", "Output video"));
    }
}
