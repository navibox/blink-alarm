# 第三方组件与模型

本软件发布包中包含以下第三方组件。

| 组件 | 用途 | 许可证 |
| --- | --- | --- |
| [MediaPipe](https://github.com/google-ai-edge/mediapipe) Face Detector（BlazeFace short-range）与 Face Landmarks Detector（Face Mesh，478 点）模型 | 人脸检测、面部关键点 | Apache License 2.0 |
| [ONNX Runtime](https://github.com/microsoft/onnxruntime) | 模型推理 | MIT License |
| [OpenCvSharp](https://github.com/shimat/opencvsharp) / [OpenCV](https://github.com/opencv/opencv) | 摄像头采集 | Apache License 2.0 |
| [.NET](https://github.com/dotnet/runtime) / WPF | 运行库与界面 | MIT License |

两个模型文件（`src/BlinkAlarm.Core/Models/*.onnx`）取自 Google 发布的
`face_landmarker.task`（float16，version 1），仅做了 TFLite → ONNX 的格式转换，未改动权重。
Apache License 2.0 全文见 `ThirdPartyLicenses/Apache-2.0.txt`。
