using Microsoft.ML.OnnxRuntime;

namespace BlinkAlarm.Core.Vision;

internal static class ModelResources
{
    public static InferenceSession CreateSession(string resourceName)
    {
        using var stream = typeof(ModelResources).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"程序内缺少模型文件：{resourceName}");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            // 模型很小，两个线程已足够；避免占满患者电脑上眼控仪等其它软件需要的 CPU。
            IntraOpNumThreads = 2,
            InterOpNumThreads = 1,
        };
        return new InferenceSession(memory.ToArray(), options);
    }
}
