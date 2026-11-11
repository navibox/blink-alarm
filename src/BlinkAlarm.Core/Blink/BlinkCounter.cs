namespace BlinkAlarm.Core.Blink;

/// <summary>
/// 眨眼计数：在"累计时间窗口"内眨眼达到目标次数即触发报警；
/// 触发后进入冷却期，冷却期内的眨眼不计数，避免连续重复报警。
/// </summary>
public sealed class BlinkCounter
{
    readonly Queue<TimeSpan> blinks = new();
    TimeSpan cooldownUntil = TimeSpan.MinValue;

    public int Target { get; set; } = 5;
    public TimeSpan Window { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromSeconds(8);

    public int Count => blinks.Count;

    public bool InCooldown(TimeSpan now) => now < cooldownUntil;

    /// <summary>当前这轮计数距离作废还剩多少时间占窗口的比例（1 = 刚开始，0 = 无计数）。</summary>
    public double WindowRemaining(TimeSpan now)
    {
        Expire(now);
        if (blinks.Count == 0 || Window <= TimeSpan.Zero) return 0;
        return Math.Clamp(1 - (now - blinks.Peek()) / Window, 0, 1);
    }

    /// <summary>让超出时间窗口的眨眼过期。每帧调用，计数才会按时回落。</summary>
    public void Expire(TimeSpan now)
    {
        while (blinks.Count > 0 && now - blinks.Peek() > Window) blinks.Dequeue();
    }

    /// <summary>登记一次眨眼。返回 true 表示达到目标次数，应触发报警。</summary>
    public bool Register(TimeSpan now)
    {
        if (InCooldown(now)) return false;
        Expire(now);
        blinks.Enqueue(now);
        if (blinks.Count < Math.Max(1, Target)) return false;

        blinks.Clear();
        cooldownUntil = now + Cooldown;
        return true;
    }

    public void Reset()
    {
        blinks.Clear();
        cooldownUntil = TimeSpan.MinValue;
    }
}
