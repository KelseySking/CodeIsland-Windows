using CodeIsland.Core.Services;
using Xunit;

namespace CodeIsland.Core.Tests;

public class EventLoggerTests : IDisposable
{
    private readonly string _tempDir;

    public EventLoggerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "codeisland-eventlogger-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Write_CreatesLogFile_AndAppendsLine()
    {
        var logger = new EventLogger(_tempDir);

        logger.Write("Cat", "hello", new Dictionary<string, string?> { ["k"] = "v" });

        var content = File.ReadAllText(logger.LogPath);
        Assert.Contains("Cat|hello|k=v", content);
        Assert.EndsWith("\n", content);
    }

    [Fact]
    public void Write_EscapesPipeAndNewline()
    {
        var logger = new EventLogger(_tempDir);

        logger.Write("Cat", "a|b\nc", new Dictionary<string, string?> { ["k"] = "x\r\ny" });

        var content = File.ReadAllText(logger.LogPath);
        Assert.DoesNotContain("a|b\nc", content);
        Assert.Contains("a\\|b\\nc", content);
        Assert.Contains("x\\r\\ny", content);
    }

    [Fact]
    public void Write_RotatesWhenExceedingMaxBytes()
    {
        var logger = new EventLogger(_tempDir, maxBytes: 200);

        for (var i = 0; i < 20; i++)
            logger.Write("Cat", "filler-line-with-some-padding-" + i, null);

        Assert.True(File.Exists(Path.Combine(_tempDir, "hook.log")));
        Assert.True(File.Exists(Path.Combine(_tempDir, "hook.log.1")));
    }

    [Fact]
    public void Write_IsThreadSafe()
    {
        var logger = new EventLogger(_tempDir);

        Parallel.For(0, 200, i =>
        {
            logger.Write("Cat", "msg-" + i, new Dictionary<string, string?> { ["i"] = i.ToString() });
        });

        var lines = File.ReadAllLines(logger.LogPath);
        Assert.Equal(200, lines.Length);
        foreach (var line in lines)
        {
            Assert.Contains("Cat|msg-", line);
            Assert.DoesNotContain("\r", line);
        }
    }

    [Fact]
    public void Write_NullFields_StillWritesBaseLine()
    {
        var logger = new EventLogger(_tempDir);

        logger.Write("Cat", "no-fields", null);

        var content = File.ReadAllText(logger.LogPath);
        Assert.Contains("|Cat|no-fields", content);
    }
}
