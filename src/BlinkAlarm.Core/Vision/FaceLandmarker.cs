using System.Numerics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace BlinkAlarm.Core.Vision;

/// <summary>一帧画面的面部关键点结果（MediaPipe Face Mesh，478 点，画面像素坐标）。</summary>
public sealed class FaceLandmarks
{
    public const int Count = 478;

    // MediaPipe Face Mesh 中计算 EAR 用的 6 个点：外眼角、上睑 2 点、内眼角、下睑 2 点。
    static readonly int[] RightEarIndices = { 33, 160, 158, 133, 153, 144 };
    static readonly int[] LeftEarIndices = { 362, 385, 387, 263, 373, 380 };
    // 眼睛轮廓（用于画面叠加显示）。
    static readonly int[] RightContour = { 33, 7, 163, 144, 145, 153, 154, 155, 133, 173, 157, 158, 159, 160, 161, 246 };
    static readonly int[] LeftContour = { 362, 382, 381, 380, 374, 373, 390, 249, 263, 466, 388, 387, 386, 385, 384, 398 };

    public Vector2[] Points { get; }

    public FaceLandmarks(Vector2[] points) => Points = points;

    /// <summary>使用者左眼的纵横比 EAR（Eye Aspect Ratio）。</summary>
    public double LeftEar => Ear(LeftEarIndices);
    /// <summary>使用者右眼的纵横比 EAR。</summary>
    public double RightEar => Ear(RightEarIndices);

    public Vector2[] LeftEyeContour => Select(LeftContour);
    public Vector2[] RightEyeContour => Select(RightContour);

    Vector2[] Select(int[] indices) => Array.ConvertAll(indices, i => Points[i]);

    double Ear(int[] idx)
    {
        float horizontal = Vector2.Distance(Points[idx[0]], Points[idx[3]]);
        if (horizontal < 1e-3f) return double.NaN;
        float vertical = Vector2.Distance(Points[idx[1]], Points[idx[5]]) + Vector2.Distance(Points[idx[2]], Points[idx[4]]);
        return vertical / (2.0 * horizontal);
    }
}

/// <summary>
/// 人脸关键点流水线：检测人脸 → Face Mesh 关键点 → 用上一帧关键点跟踪下一帧的取样区域。
/// 与 MediaPipe 的做法相同：只有在跟丢时才重新运行人脸检测，平时每帧只跑一次关键点模型。
/// 非线程安全，请固定在一个线程上调用。
/// </summary>
public sealed class FaceLandmarker : IDisposable
{
    const int InputSize = 256;
    const float RoiScale = 1.5f;
    const float MinPresence = 0.5f;

    readonly FaceDetector detector = new();
    readonly InferenceSession session = ModelResources.CreateSession("face_landmarks_detector.onnx");
    readonly string inputName;
    readonly float[] input = new float[InputSize * InputSize * 3];
    Roi? tracked;

    public FaceLandmarker() => inputName = session.InputMetadata.Keys.First();

    /// <summary>忘记当前跟踪的人脸，下一帧重新检测。</summary>
    public void Reset() => tracked = null;

    /// <summary>
    /// 处理一帧 BGR24 画面。没有检测到人脸时返回 null。
    /// </summary>
    public FaceLandmarks? Process(ReadOnlySpan<byte> bgr, int width, int height, int stride)
    {
        if (width < 16 || height < 16 || stride < width * 3 || bgr.Length < stride * (height - 1) + width * 3)
            throw new ArgumentException("画面尺寸与数据长度不匹配。");

        bool wasTracking = tracked.HasValue;
        var roi = tracked ?? detector.Detect(bgr, width, height, stride);
        if (roi is null) return null;

        var result = Run(bgr, width, height, stride, roi.Value);
        if (result is null && wasTracking)
        {
            // 跟踪丢失：在同一帧上重新检测一次，避免白白丢一帧。
            roi = detector.Detect(bgr, width, height, stride);
            if (roi is not null) result = Run(bgr, width, height, stride, roi.Value);
        }

        tracked = result is null ? null : NextRoi(result.Points, width, height);
        return result;
    }

    FaceLandmarks? Run(ReadOnlySpan<byte> bgr, int width, int height, int stride, Roi roi)
    {
        ImageSampler.Sample(bgr, width, height, stride, roi, InputSize, input, 1f, 0f);
        var tensor = new DenseTensor<float>(input, new[] { 1, InputSize, InputSize, 3 });
        using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) });

        float presenceLogit = results.First(r => r.Name == "Identity_1").AsEnumerable<float>().First();
        if (!(Sigmoid(presenceLogit) >= MinPresence)) return null;

        var raw = results.First(r => r.Name == "Identity").AsEnumerable<float>().ToArray();
        if (raw.Length != FaceLandmarks.Count * 3)
            throw new InvalidOperationException("关键点模型输出格式不正确。");

        var points = new Vector2[FaceLandmarks.Count];
        for (int i = 0; i < points.Length; i++)
        {
            float u = raw[i * 3] / InputSize - 0.5f, v = raw[i * 3 + 1] / InputSize - 0.5f;
            if (!float.IsFinite(u) || !float.IsFinite(v)) return null;
            var (x, y) = roi.ToFrame(u, v);
            points[i] = new Vector2(x, y);
        }
        return new FaceLandmarks(points);
    }

    /// <summary>由本帧关键点推算下一帧的取样区域（随头部移动、旋转而跟随）。</summary>
    static Roi? NextRoi(Vector2[] points, int width, int height)
    {
        // 以两个外眼角连线的方向作为头部的平面内旋转角。
        var eyeAxis = points[263] - points[33];
        float rotation = MathF.Atan2(eyeAxis.Y, eyeAxis.X);
        float cos = MathF.Cos(rotation), sin = MathF.Sin(rotation);

        float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
        foreach (var p in points)
        {
            float u = p.X * cos + p.Y * sin, v = -p.X * sin + p.Y * cos;
            minU = Math.Min(minU, u); maxU = Math.Max(maxU, u);
            minV = Math.Min(minV, v); maxV = Math.Max(maxV, v);
        }
        float cu = (minU + maxU) / 2, cv = (minV + maxV) / 2;
        float size = Math.Max(maxU - minU, maxV - minV) * RoiScale;
        float cx = cu * cos - cv * sin, cy = cu * sin + cv * cos;

        // 脸太小或中心跑到画面外，说明已经跟丢。
        if (size < 24 || cx < 0 || cy < 0 || cx > width || cy > height) return null;
        return new Roi(cx, cy, size, rotation);
    }

    static float Sigmoid(float x) => 1f / (1f + MathF.Exp(-x));

    public void Dispose()
    {
        detector.Dispose();
        session.Dispose();
    }
}
