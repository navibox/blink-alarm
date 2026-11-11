using BlinkAlarm.Core.Blink;

namespace BlinkAlarm.Tests;

public class BlinkDetectorTests
{
    const double OpenEar = 0.30, ClosedEar = 0.10;
    static readonly BlinkOptions Options = new();

    /// <summary>以 30 帧/秒喂入 EAR 序列的小工具。</summary>
    sealed class Feed
    {
        public readonly BlinkDetector Detector = new();
        public TimeSpan Now = TimeSpan.FromSeconds(100);
        public int Blinks;
        public BlinkSample Last;

        public Feed Run(double seconds, double leftEar, double rightEar)
        {
            int frames = (int)Math.Round(seconds * 30);
            for (int i = 0; i < frames; i++)
            {
                Last = Detector.Update(leftEar, rightEar, Now, Options);
                if (Last.Blink) Blinks++;
                Now += TimeSpan.FromSeconds(1 / 30.0);
            }
            return this;
        }

        public Feed Open(double seconds) => Run(seconds, OpenEar, OpenEar);
        public Feed Closed(double seconds) => Run(seconds, ClosedEar, ClosedEar);
        public static Feed Calibrated() => new Feed().Open(2.5);
    }

    [Fact]
    public void CalibratesFirst()
    {
        var feed = new Feed().Open(1);
        Assert.True(feed.Last.Calibrating);
        Assert.InRange(feed.Last.CalibrationProgress, 0.3, 0.7);
        feed.Open(1.5);
        Assert.False(feed.Last.Calibrating);
        Assert.Equal(1.0, feed.Last.LeftOpenness, 2);
    }

    [Fact]
    public void BlinksDuringCalibrationAreNotCounted()
    {
        var feed = new Feed().Open(0.5).Closed(0.2).Open(0.5);
        Assert.Equal(0, feed.Blinks);
    }

    [Fact]
    public void CountsOneCompleteBlink()
    {
        var feed = Feed.Calibrated().Closed(0.15).Open(0.5);
        Assert.Equal(1, feed.Blinks);
    }

    [Fact]
    public void CountsBlinkOnlyAfterEyeReopens()
    {
        var feed = Feed.Calibrated().Closed(0.15);
        Assert.Equal(0, feed.Blinks);
        Assert.Equal(EyeState.Closed, feed.Last.LeftState);
    }

    [Fact]
    public void CountsFiveSeparateBlinks()
    {
        var feed = Feed.Calibrated();
        for (int i = 0; i < 5; i++) feed.Closed(0.2).Open(0.6);
        Assert.Equal(5, feed.Blinks);
    }

    [Fact]
    public void IgnoresSingleFrameGlitch()
    {
        var feed = Feed.Calibrated().Closed(1 / 30.0).Open(0.5);
        Assert.Equal(0, feed.Blinks);
    }

    [Fact]
    public void IgnoresLongEyeClosure()
    {
        var feed = Feed.Calibrated().Closed(3).Open(0.5);
        Assert.Equal(0, feed.Blinks);
        Assert.Equal(EyeState.Open, feed.Last.LeftState);
    }

    [Fact]
    public void ShallowDipAboveThresholdIsNotABlink()
    {
        // 开合比例 0.8，高于默认阈值（约 0.71）。
        var feed = Feed.Calibrated().Run(0.2, OpenEar * 0.8, OpenEar * 0.8).Open(0.5);
        Assert.Equal(0, feed.Blinks);
    }

    [Fact]
    public void SingleEyeWinkCountsOnce()
    {
        var feed = Feed.Calibrated().Run(0.2, ClosedEar, OpenEar).Open(0.5);
        Assert.Equal(1, feed.Blinks);
    }

    [Fact]
    public void EyesFinishingOnAdjacentFramesCountOnce()
    {
        var feed = Feed.Calibrated().Closed(0.15).Run(1 / 30.0, OpenEar, ClosedEar).Open(0.5);
        Assert.Equal(1, feed.Blinks);
    }

    [Fact]
    public void FaceLostCancelsBlinkInProgress()
    {
        var feed = Feed.Calibrated().Closed(0.15);
        feed.Detector.FaceLost();
        feed.Open(0.5);
        Assert.Equal(0, feed.Blinks);
    }

    [Fact]
    public void InvalidEarIsTreatedAsFaceLost()
    {
        var feed = Feed.Calibrated().Closed(0.15).Run(0.1, double.NaN, double.NaN).Open(0.5);
        Assert.Equal(0, feed.Blinks);
    }

    [Fact]
    public void ClosedEyesDuringCalibrationForceRecalibration()
    {
        var feed = new Feed().Run(2.5, 0.05, 0.05);
        Assert.True(feed.Last.Calibrating);
        feed.Open(2.5);
        Assert.False(feed.Last.Calibrating);
    }

    [Fact]
    public void BaselineFollowsSlowDrift()
    {
        // 摄像头角度变化使睁眼 EAR 逐渐降到 0.27，之后的眨眼仍应正常识别。
        var feed = Feed.Calibrated();
        for (int i = 0; i < 30; i++) feed.Run(1, OpenEar - 0.001 * i, OpenEar - 0.001 * i);
        feed.Run(20, 0.27, 0.27);
        Assert.Equal(0, feed.Blinks);
        Assert.InRange(feed.Last.LeftOpenness, 0.97, 1.03);
        feed.Closed(0.2).Run(0.5, 0.27, 0.27);
        Assert.Equal(1, feed.Blinks);
    }

    [Theory]
    [InlineData(0, 0.50)]
    [InlineData(60, 0.71)]
    [InlineData(100, 0.85)]
    public void SensitivityMapsToCloseRatio(double sensitivity, double expected)
    {
        var options = new BlinkOptions(sensitivity);
        Assert.Equal(expected, options.CloseRatio, 3);
        Assert.True(options.OpenRatio > options.CloseRatio);
    }
}

public class BlinkCounterTests
{
    static TimeSpan T(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void TriggersAtTarget()
    {
        var counter = new BlinkCounter { Target = 3 };
        Assert.False(counter.Register(T(0)));
        Assert.False(counter.Register(T(1)));
        Assert.Equal(2, counter.Count);
        Assert.True(counter.Register(T(2)));
        Assert.Equal(0, counter.Count);
    }

    [Fact]
    public void OldBlinksFallOutOfWindow()
    {
        var counter = new BlinkCounter { Target = 3, Window = T(10) };
        counter.Register(T(0));
        counter.Register(T(8));
        Assert.False(counter.Register(T(11)));   // 第 0 秒那次已超出窗口
        Assert.Equal(2, counter.Count);
        Assert.True(counter.Register(T(12)));
    }

    [Fact]
    public void ExpireDropsCountWithoutNewBlinks()
    {
        var counter = new BlinkCounter { Window = T(10) };
        counter.Register(T(0));
        counter.Expire(T(5));
        Assert.Equal(1, counter.Count);
        Assert.Equal(0.5, counter.WindowRemaining(T(5)), 3);
        counter.Expire(T(10.5));
        Assert.Equal(0, counter.Count);
        Assert.Equal(0, counter.WindowRemaining(T(10.5)));
    }

    [Fact]
    public void CooldownSuppressesBlinks()
    {
        var counter = new BlinkCounter { Target = 2, Cooldown = T(8) };
        counter.Register(T(0));
        Assert.True(counter.Register(T(1)));
        Assert.True(counter.InCooldown(T(5)));
        Assert.False(counter.Register(T(5)));
        Assert.False(counter.Register(T(6)));
        Assert.Equal(0, counter.Count);

        Assert.False(counter.InCooldown(T(9)));
        counter.Register(T(9));
        Assert.True(counter.Register(T(10)));
    }

    [Fact]
    public void ResetClearsCooldown()
    {
        var counter = new BlinkCounter { Target = 2 };
        counter.Register(T(0));
        counter.Register(T(1));
        counter.Reset();
        Assert.False(counter.InCooldown(T(2)));
    }
}
