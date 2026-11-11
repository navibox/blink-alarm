using System.IO;
using System.Media;
using BlinkAlarm.Core.Alarm;

namespace BlinkAlarm.App.Services;

/// <summary>循环播放报警声：优先用用户选择的 wav，失败则退回内置警笛。</summary>
public sealed class SoundAlarm : IDisposable
{
    readonly byte[] siren = SirenWave.Create();
    SoundPlayer? player;

    /// <returns>是否使用了用户自选的声音文件。</returns>
    public bool Play(string? customFile)
    {
        Stop();
        if (!string.IsNullOrWhiteSpace(customFile) && File.Exists(customFile))
        {
            try
            {
                var custom = new SoundPlayer(customFile);
                custom.Load();
                custom.PlayLooping();
                player = custom;
                return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or UnauthorizedAccessException)
            {
                // 文件不是有效的 PCM wav：改用内置警笛，保证一定有声音。
            }
        }

        try
        {
            player = new SoundPlayer(new MemoryStream(siren));
            player.PlayLooping();
        }
        catch (InvalidOperationException)
        {
            SystemSounds.Hand.Play();
        }
        return false;
    }

    public void Stop()
    {
        var current = player;
        player = null;
        if (current is null) return;
        try { current.Stop(); } catch (InvalidOperationException) { }
        current.Dispose();
    }

    public void Dispose() => Stop();
}
