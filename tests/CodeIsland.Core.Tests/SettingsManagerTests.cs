using System.Text.Json;
using CodeIsland.Core.Services;
using Xunit;

namespace CodeIsland.Core.Tests;

public class SettingsManagerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "CodeIslandSettingsTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Set_RaisesSettingChangedWithKeyAndValues()
    {
        var settings = new SettingsManager(_tempDir);
        settings.Set("volume", 0.3);

        SettingChangedEventArgs? observed = null;
        settings.SettingChanged += (_, args) => observed = args;

        settings.Set("volume", 0.8);

        Assert.NotNull(observed);
        Assert.Equal("volume", observed.Key);
        Assert.NotNull(observed.OldValue);
        Assert.Equal(0.3, observed.OldValue.Value.GetDouble(), precision: 3);
        Assert.Equal(0.8, observed.NewValue.GetDouble(), precision: 3);
    }

    [Fact]
    public void Set_RaisesSettingChangedForNewKeyWithNullOldValue()
    {
        var settings = new SettingsManager(_tempDir);
        SettingChangedEventArgs? observed = null;
        settings.SettingChanged += (_, args) => observed = args;

        settings.Set("sound_enabled", false);

        Assert.NotNull(observed);
        Assert.Equal("sound_enabled", observed.Key);
        Assert.Null(observed.OldValue);
        Assert.Equal(JsonValueKind.False, observed.NewValue.ValueKind);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
