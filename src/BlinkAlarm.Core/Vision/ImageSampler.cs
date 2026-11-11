namespace BlinkAlarm.Core.Vision;

/// <summary>
/// 画面中的一个正方形取样区域（可旋转）。坐标单位为像素。
/// </summary>
public readonly record struct Roi(float CenterX, float CenterY, float Size, float Rotation)
{
    /// <summary>把区域内的归一化坐标（-0.5 ~ 0.5）换算回画面像素坐标。</summary>
    public (float X, float Y) ToFrame(float u, float v)
    {
        float cos = MathF.Cos(Rotation), sin = MathF.Sin(Rotation);
        return (CenterX + Size * (u * cos - v * sin), CenterY + Size * (u * sin + v * cos));
    }
}

internal static class ImageSampler
{
    /// <summary>
    /// 从 BGR24 画面中按 <paramref name="roi"/> 双线性取样，写出 NHWC、RGB 顺序的浮点张量。
    /// 画面之外的部分填 0（黑色）。每个通道输出值 = 像素值 / 255 * scale + offset。
    /// </summary>
    public static void Sample(ReadOnlySpan<byte> bgr, int width, int height, int stride,
        Roi roi, int outSize, Span<float> output, float scale, float offset)
    {
        float cos = MathF.Cos(roi.Rotation), sin = MathF.Sin(roi.Rotation);
        float step = roi.Size / outSize;
        float k = scale / 255f;
        int o = 0;
        for (int y = 0; y < outSize; y++)
        {
            float v = (y + 0.5f) * step - roi.Size / 2f;
            for (int x = 0; x < outSize; x++, o += 3)
            {
                float u = (x + 0.5f) * step - roi.Size / 2f;
                float sx = roi.CenterX + u * cos - v * sin - 0.5f;
                float sy = roi.CenterY + u * sin + v * cos - 0.5f;
                int x0 = (int)MathF.Floor(sx), y0 = (int)MathF.Floor(sy);
                if (x0 < 0 || y0 < 0 || x0 >= width - 1 || y0 >= height - 1)
                {
                    output[o] = output[o + 1] = output[o + 2] = offset;
                    continue;
                }
                float fx = sx - x0, fy = sy - y0;
                float w00 = (1 - fx) * (1 - fy), w10 = fx * (1 - fy), w01 = (1 - fx) * fy, w11 = fx * fy;
                int p0 = y0 * stride + x0 * 3, p1 = p0 + stride;
                for (int c = 0; c < 3; c++)
                {
                    float value = bgr[p0 + c] * w00 + bgr[p0 + 3 + c] * w10 + bgr[p1 + c] * w01 + bgr[p1 + 3 + c] * w11;
                    output[o + 2 - c] = value * k + offset; // BGR -> RGB
                }
            }
        }
    }
}
