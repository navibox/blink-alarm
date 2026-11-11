using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace BlinkAlarm.Core.Vision;

/// <summary>
/// MediaPipe BlazeFace（近距离）人脸检测器，经 ONNX Runtime 推理。
/// 只负责"找到脸在哪"，给关键点模型提供初始取样区域。
/// </summary>
internal sealed class FaceDetector : IDisposable
{
    const int InputSize = 128;
    const int AnchorCount = 896;
    const float MinScore = 0.5f;
    /// <summary>检测框放大倍数，使取样区域包含整张脸（与 MediaPipe 一致）。</summary>
    const float RoiScale = 1.5f;

    readonly InferenceSession session = ModelResources.CreateSession("face_detector.onnx");
    readonly string inputName;
    readonly float[] input = new float[InputSize * InputSize * 3];
    readonly float[] anchors = BuildAnchors();

    public FaceDetector() => inputName = session.InputMetadata.Keys.First();

    public Roi? Detect(ReadOnlySpan<byte> bgr, int width, int height, int stride)
    {
        // 保持宽高比：以画面长边为边长的正方形（短边方向补黑）。
        float side = Math.Max(width, height);
        var letterbox = new Roi(width / 2f, height / 2f, side, 0);
        ImageSampler.Sample(bgr, width, height, stride, letterbox, InputSize, input, 2f, -1f);

        var tensor = new DenseTensor<float>(input, new[] { 1, InputSize, InputSize, 3 });
        using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) });
        var boxes = results.First(r => r.Name == "regressors").AsTensor<float>();
        var scores = results.First(r => r.Name == "classificators").AsTensor<float>();

        int best = -1;
        float bestLogit = Logit(MinScore);
        for (int i = 0; i < AnchorCount; i++)
        {
            float logit = scores[0, i, 0];
            if (logit > bestLogit) { bestLogit = logit; best = i; }
        }
        if (best < 0) return null;

        float ax = anchors[best * 2], ay = anchors[best * 2 + 1];
        float Decode(int index, float anchor) => boxes[0, best, index] / InputSize + anchor;

        // 均为取样正方形内的归一化坐标（0~1）。
        float cx = Decode(0, ax), cy = Decode(1, ay);
        float w = boxes[0, best, 2] / InputSize, h = boxes[0, best, 3] / InputSize;
        float rightEyeX = Decode(4, ax), rightEyeY = Decode(5, ay);
        float leftEyeX = Decode(6, ax), leftEyeY = Decode(7, ay);

        float rotation = MathF.Atan2(leftEyeY - rightEyeY, leftEyeX - rightEyeX);
        var (fx, fy) = letterbox.ToFrame(cx - 0.5f, cy - 0.5f);
        float size = Math.Max(w, h) * side * RoiScale;
        return size < 8 ? null : new Roi(fx, fy, size, rotation);
    }

    static float Logit(float p) => MathF.Log(p / (1 - p));

    /// <summary>MediaPipe SSD 锚点：16x16 网格每格 2 个，8x8 网格每格 6 个，共 896 个。</summary>
    static float[] BuildAnchors()
    {
        var list = new List<float>(AnchorCount * 2);
        foreach (var (grid, perCell) in new[] { (16, 2), (8, 6) })
            for (int y = 0; y < grid; y++)
                for (int x = 0; x < grid; x++)
                    for (int n = 0; n < perCell; n++)
                    {
                        list.Add((x + 0.5f) / grid);
                        list.Add((y + 0.5f) / grid);
                    }
        return list.ToArray();
    }

    public void Dispose() => session.Dispose();
}
