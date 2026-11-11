namespace BlinkAlarm.Core.Blink;

/// <summary>眨眼判定参数。</summary>
/// <param name="Sensitivity">灵敏度 0~100，越高越容易判定为闭眼。</param>
/// <param name="MinClosedMs">闭眼短于该时长视为抖动，不计数。</param>
/// <param name="MaxClosedMs">闭眼长于该时长视为休息/睡眠，不计数。</param>
public sealed record BlinkOptions(double Sensitivity = 60, int MinClosedMs = 60, int MaxClosedMs = 900)
{
    /// <summary>眼睛开合比例（当前 EAR ÷ 睁眼基准 EAR）低于该值即判定为闭眼。</summary>
    public double CloseRatio => 0.50 + Math.Clamp(Sensitivity, 0, 100) / 100.0 * 0.35;

    /// <summary>开合比例回升到该值才算重新睁眼（留出回差，防止在阈值附近抖动）。</summary>
    public double OpenRatio => Math.Min(0.93, CloseRatio + 0.08);
}

public enum EyeState
{
    Open,
    Closed,
    /// <summary>闭眼时间过长，等待重新睁眼，本次不计数。</summary>
    Held,
}

/// <summary>单帧的眨眼分析结果。</summary>
public readonly record struct BlinkSample(
    bool Blink,
    bool Calibrating,
    double CalibrationProgress,
    double LeftOpenness,
    double RightOpenness,
    EyeState LeftState,
    EyeState RightState);

/// <summary>
/// 根据左右眼 EAR 判定眨眼。
/// 先用约 2 秒采集每只眼睛的睁眼基准，此后以"开合比例 = EAR ÷ 基准"为信号，
/// 经"睁眼 → 闭眼 → 重新睁眼"状态机确认一次完整眨眼。
/// </summary>
public sealed class BlinkDetector
{
    static readonly TimeSpan CalibrationTime = TimeSpan.FromSeconds(2);
    const int MinCalibrationSamples = 20;
    /// <summary>低于此值的基准不可信（多半是校准时闭着眼），需要重新校准。</summary>
    const double MinBaseline = 0.10;
    /// <summary>两只眼睛通常前后一两帧内完成同一次眨眼，这段时间内只计一次。</summary>
    static readonly TimeSpan MergeWindow = TimeSpan.FromMilliseconds(250);

    readonly List<double> leftSamples = new(), rightSamples = new();
    readonly EyeTracker left = new(), right = new();
    TimeSpan? calibrationStart;
    TimeSpan lastBlink = TimeSpan.MinValue;
    double leftBaseline, rightBaseline;

    public bool IsCalibrated { get; private set; }

    /// <summary>丢弃睁眼基准，重新校准。</summary>
    public void Recalibrate()
    {
        IsCalibrated = false;
        calibrationStart = null;
        leftSamples.Clear();
        rightSamples.Clear();
        FaceLost();
    }

    /// <summary>人脸丢失时调用：正在进行中的闭眼动作作废。</summary>
    public void FaceLost()
    {
        left.Reset();
        right.Reset();
    }

    public BlinkSample Update(double leftEar, double rightEar, TimeSpan now, BlinkOptions options)
    {
        if (!IsPlausible(leftEar) || !IsPlausible(rightEar))
        {
            FaceLost();
            return new BlinkSample(false, !IsCalibrated, CalibrationProgress(now), 0, 0, EyeState.Open, EyeState.Open);
        }

        if (!IsCalibrated)
        {
            calibrationStart ??= now;
            leftSamples.Add(leftEar);
            rightSamples.Add(rightEar);
            if (now - calibrationStart >= CalibrationTime && leftSamples.Count >= MinCalibrationSamples)
            {
                leftBaseline = UpperPercentile(leftSamples, 0.75);
                rightBaseline = UpperPercentile(rightSamples, 0.75);
                IsCalibrated = leftBaseline >= MinBaseline && rightBaseline >= MinBaseline;
                calibrationStart = null;
                leftSamples.Clear();
                rightSamples.Clear();
            }
            if (!IsCalibrated)
                return new BlinkSample(false, true, CalibrationProgress(now), 1, 1, EyeState.Open, EyeState.Open);
        }

        // 只在双眼都睁开时缓慢修正基准，跟随坐姿、光线、摄像头角度的小变化。
        if (left.State == EyeState.Open && right.State == EyeState.Open)
        {
            leftBaseline = Adapt(leftBaseline, leftEar);
            rightBaseline = Adapt(rightBaseline, rightEar);
        }

        double leftOpenness = leftEar / leftBaseline, rightOpenness = rightEar / rightBaseline;
        bool leftBlink = left.Update(leftOpenness, now, options);
        bool rightBlink = right.Update(rightOpenness, now, options);

        bool blink = (leftBlink || rightBlink) && (lastBlink == TimeSpan.MinValue || now - lastBlink >= MergeWindow);
        if (blink) lastBlink = now;
        return new BlinkSample(blink, false, 1, leftOpenness, rightOpenness, left.State, right.State);
    }

    double CalibrationProgress(TimeSpan now)
    {
        if (IsCalibrated) return 1;
        if (calibrationStart is null) return 0;
        return Math.Clamp((now - calibrationStart.Value) / CalibrationTime, 0, 0.99);
    }

    static bool IsPlausible(double ear) => double.IsFinite(ear) && ear is > 0.005 and < 1.0;

    static double Adapt(double baseline, double ear)
    {
        // 比基准更大：说明基准偏低（例如校准时眼睛没完全睁开），较快上调。
        if (ear > baseline) return baseline + (ear - baseline) * 0.01;
        // 略低于基准：缓慢下调。明显低于基准的值可能是正在闭眼，不参与修正。
        if (ear > baseline * 0.88) return baseline + (ear - baseline) * 0.002;
        return baseline;
    }

    static double UpperPercentile(List<double> values, double percentile)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        int index = Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    sealed class EyeTracker
    {
        TimeSpan closedAt;
        public EyeState State { get; private set; } = EyeState.Open;

        public void Reset() => State = EyeState.Open;

        /// <returns>本帧是否完成了一次有效眨眼。</returns>
        public bool Update(double openness, TimeSpan now, BlinkOptions options)
        {
            switch (State)
            {
                case EyeState.Open:
                    if (openness <= options.CloseRatio)
                    {
                        closedAt = now;
                        State = EyeState.Closed;
                    }
                    return false;

                case EyeState.Closed:
                    double closedMs = (now - closedAt).TotalMilliseconds;
                    if (openness >= options.OpenRatio)
                    {
                        State = EyeState.Open;
                        return closedMs >= options.MinClosedMs && closedMs <= options.MaxClosedMs;
                    }
                    if (closedMs > options.MaxClosedMs) State = EyeState.Held;
                    return false;

                default:
                    if (openness >= options.OpenRatio) State = EyeState.Open;
                    return false;
            }
        }
    }
}
