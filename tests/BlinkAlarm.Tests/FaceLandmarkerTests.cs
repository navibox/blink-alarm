using BlinkAlarm.Core.Vision;

namespace BlinkAlarm.Tests;

public class FaceLandmarkerTests
{
    /// <summary>读取 24 位 BMP，返回自上而下排列的 BGR 数据。</summary>
    static (byte[] Bgr, int Width, int Height, int Stride) LoadBmp(string name)
    {
        byte[] file = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", name));
        int offset = BitConverter.ToInt32(file, 10), width = BitConverter.ToInt32(file, 18), height = BitConverter.ToInt32(file, 22);
        int stride = (width * 3 + 3) / 4 * 4;
        var bgr = new byte[stride * height];
        for (int y = 0; y < height; y++)
            Array.Copy(file, offset + (height - 1 - y) * stride, bgr, y * stride, stride);
        return (bgr, width, height, stride);
    }

    [Fact]
    public void FindsEyesOnRealFace()
    {
        var (bgr, width, height, stride) = LoadBmp("face.bmp");
        using var landmarker = new FaceLandmarker();

        // 第一帧走"人脸检测 + 关键点"，之后几帧走"跟踪 + 关键点"，结果应保持一致。
        for (int frame = 0; frame < 4; frame++)
        {
            var face = landmarker.Process(bgr, width, height, stride);
            Assert.NotNull(face);
            Assert.Equal(FaceLandmarks.Count, face.Points.Length);

            // 照片里人物睁着眼：EAR 应处于正常睁眼范围。
            Assert.InRange(face.LeftEar, 0.2, 0.45);
            Assert.InRange(face.RightEar, 0.2, 0.45);

            // 两只外眼角在照片中的大致位置（人物的右眼在画面左侧）。
            Assert.InRange(face.Points[33].X, 120, 142);
            Assert.InRange(face.Points[33].Y, 90, 112);
            Assert.InRange(face.Points[263].X, 180, 202);
            Assert.InRange(face.Points[263].Y, 93, 115);
            Assert.Equal(16, face.LeftEyeContour.Length);
        }
    }

    [Fact]
    public void ReturnsNullWithoutFace()
    {
        const int width = 320, height = 240;
        var gray = Enumerable.Repeat((byte)128, width * height * 3).ToArray();
        using var landmarker = new FaceLandmarker();
        Assert.Null(landmarker.Process(gray, width, height, width * 3));
    }

    [Fact]
    public void RejectsMismatchedBuffer()
    {
        using var landmarker = new FaceLandmarker();
        Assert.Throws<ArgumentException>(() => landmarker.Process(new byte[100], 320, 240, 960));
    }
}
