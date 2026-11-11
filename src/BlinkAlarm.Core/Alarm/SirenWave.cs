namespace BlinkAlarm.Core.Alarm;

/// <summary>生成内置的双音警笛声（880Hz / 1320Hz 交替，可无缝循环）。</summary>
public static class SirenWave
{
    public static byte[] Create()
    {
        const int rate = 44100, fade = 500;
        var tones = new[] { 880.0, 1320.0 };
        int perTone = rate / 2, total = perTone * tones.Length;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + total * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);      // PCM
        writer.Write((short)1);      // 单声道
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(total * 2);
        foreach (double frequency in tones)
            for (int i = 0; i < perTone; i++)
            {
                // 每段首尾做淡入淡出，避免切换时出现爆音。
                double envelope = Math.Min(1.0, Math.Min(i, perTone - i) / (double)fade);
                writer.Write((short)(short.MaxValue * 0.6 * envelope * Math.Sin(2 * Math.PI * frequency * i / rate)));
            }
        writer.Flush();
        return stream.ToArray();
    }
}
