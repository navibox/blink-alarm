# 模型文件

| 文件 | 来源 | 输入 | 输出 |
| --- | --- | --- | --- |
| `face_detector.onnx` | MediaPipe BlazeFace short-range | 1×128×128×3，RGB，-1~1 | `regressors` 1×896×16，`classificators` 1×896×1 |
| `face_landmarks_detector.onnx` | MediaPipe Face Mesh（带虹膜，478 点） | N×256×256×3，RGB，0~1 | `Identity` 478×3 关键点，`Identity_1` 人脸存在分数（logit） |

## 重新生成

```bash
curl -LO https://storage.googleapis.com/mediapipe-models/face_landmarker/face_landmarker/float16/1/face_landmarker.task
unzip face_landmarker.task          # 得到 face_detector.tflite、face_landmarks_detector.tflite
pip install tensorflow tf2onnx
python -m tf2onnx.convert --tflite face_detector.tflite --output face_detector.onnx --opset 13
python -m tf2onnx.convert --tflite face_landmarks_detector.tflite --output face_landmarks_detector.onnx --opset 13
```

模型采用 Apache License 2.0，见仓库根目录的 `THIRD-PARTY-NOTICES.md`。
