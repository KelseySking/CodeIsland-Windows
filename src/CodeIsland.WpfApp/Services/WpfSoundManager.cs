using System.IO;
using NAudio.Wave;

namespace CodeIsland.WpfApp.Services;

public sealed class WpfSoundManager : IDisposable
{
    private readonly string _assetsDir;
    private float _volume = 0.7f;
    private bool _enabled = true;

    private static readonly Dictionary<string, string> SoundFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["start"] = "8bit_start.wav",
        ["complete"] = "8bit_complete.wav",
        ["approval"] = "8bit_approval.wav",
        ["error"] = "8bit_error.wav",
        ["boot"] = "8bit_boot.wav",
        ["submit"] = "8bit_submit.wav"
    };

    public WpfSoundManager(string? assetsDir = null)
    {
        _assetsDir = assetsDir ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "sounds");
    }

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0f, 1f);
    }

    public void Play(string soundName)
    {
        if (!_enabled || !SoundFiles.TryGetValue(soundName, out var fileName))
            return;

        var path = Path.Combine(_assetsDir, fileName);
        if (!File.Exists(path))
            return;

        Task.Run(() =>
        {
            try
            {
                using var audioFile = new AudioFileReader(path) { Volume = _volume };
                using var outputDevice = new WaveOutEvent();
                outputDevice.Init(audioFile);
                outputDevice.Play();
                while (outputDevice.PlaybackState == PlaybackState.Playing)
                    Thread.Sleep(80);
            }
            catch
            {
                // 音效播放失败不应影响 Hook 响应或 HUD 状态。
            }
        });
    }

    public void Dispose()
    {
    }
}
