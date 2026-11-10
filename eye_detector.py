"""基于 MediaPipe FaceMesh 的眼睛纵横比(EAR)眨眼检测与计数。"""
import time
import numpy as np
import cv2
import mediapipe as mp

mp_face_mesh = mp.solutions.face_mesh

# MediaPipe FaceMesh 眼部关键点索引（6 点/眼）
RIGHT_EYE = [33, 160, 158, 133, 153, 144]   # 画面左侧（患者右眼）
LEFT_EYE = [362, 385, 387, 263, 373, 380]   # 画面右侧（患者左眼）


class EyeDetector:
    """封装 MediaPipe，输入一帧图像，输出 EAR 等信息。"""

    def __init__(self, min_detection_confidence=0.5, min_tracking_confidence=0.5):
        self.face_mesh = mp_face_mesh.FaceMesh(
            max_num_faces=1,
            refine_landmarks=True,
            min_detection_confidence=min_detection_confidence,
            min_tracking_confidence=min_tracking_confidence,
        )

    @staticmethod
    def _ear(landmarks, indices, w, h):
        pts = np.array([(landmarks[i].x * w, landmarks[i].y * h) for i in indices],
                       dtype=np.float64)
        v1 = np.linalg.norm(pts[1] - pts[5])
        v2 = np.linalg.norm(pts[2] - pts[4])
        hz = np.linalg.norm(pts[0] - pts[3])
        if hz < 1e-6:
            return 0.0
        return (v1 + v2) / (2.0 * hz)

    def process(self, frame_bgr):
        """返回 dict(ear, left, right, landmarks, w, h) 或 None（未检测到人脸）。"""
        h, w = frame_bgr.shape[:2]
        rgb = cv2.cvtColor(frame_bgr, cv2.COLOR_BGR2RGB)
        rgb.flags.writeable = False
        res = self.face_mesh.process(rgb)
        if not res.multi_face_landmarks:
            return None
        lms = res.multi_face_landmarks[0].landmark
        left = self._ear(lms, LEFT_EYE, w, h)
        right = self._ear(lms, RIGHT_EYE, w, h)
        return {
            "ear": (left + right) / 2.0,
            "left": left,
            "right": right,
            "landmarks": lms,
            "w": w,
            "h": h,
        }

    def close(self):
        try:
            self.face_mesh.close()
        except Exception:
            pass


class BlinkCounter:
    """眨眼计数与报警触发状态机。

    逻辑：
      - EAR 低于阈值视为闭眼；
      - 闭眼持续 >= min_closed_frames 且 <= max_blink_frames 帧，再睁眼 => 记一次眨眼；
      - 相邻两次眨眼间隔 > blink_window 秒 => 连续计数清零；
      - 连续眨眼达到 target_blinks => 触发报警并进入冷却。
    """

    def __init__(self):
        self.ear_threshold = 0.21
        self.min_closed_frames = 2
        self.max_blink_frames = 25
        self.blink_window = 2.0
        self.target = 5
        self.cooldown = 8.0

        self.count = 0
        self.closed_frames = 0
        self.last_blink_time = 0.0
        self.cooldown_until = 0.0

    def configure(self, ear_threshold, min_closed_frames, max_blink_frames,
                  blink_window, target, cooldown):
        self.ear_threshold = ear_threshold
        self.min_closed_frames = min_closed_frames
        self.max_blink_frames = max_blink_frames
        self.blink_window = blink_window
        self.target = target
        self.cooldown = cooldown

    def reset(self):
        self.count = 0
        self.closed_frames = 0
        self.last_blink_time = 0.0

    def update(self, ear):
        """ear 为 None 表示未检测到人脸。返回状态 dict。"""
        now = time.time()

        if ear is None:
            # 丢失人脸：保持计数但缓慢清零，避免误触发
            self.closed_frames = 0
            if self.count > 0 and (now - self.last_blink_time) > self.blink_window:
                self.count = 0
            return {"closed": False, "count": self.count, "triggered": False,
                    "face": False}

        closed = ear < self.ear_threshold

        if closed:
            self.closed_frames += 1
        else:
            if self.min_closed_frames <= self.closed_frames <= self.max_blink_frames:
                # 记一次有效眨眼
                if self.count == 0 or (now - self.last_blink_time) <= self.blink_window:
                    self.count += 1
                else:
                    self.count = 1
                self.last_blink_time = now
            self.closed_frames = 0

        triggered = False
        if self.count >= self.target and now >= self.cooldown_until:
            triggered = True
            self.count = 0
            self.cooldown_until = now + self.cooldown

        # 超过窗口未继续眨眼则清零
        if self.count > 0 and (now - self.last_blink_time) > self.blink_window:
            self.count = 0

        return {"closed": closed, "count": self.count, "triggered": triggered,
                "face": True}


def draw_overlay(frame_bgr, info, closed, ear_threshold, show_landmarks=True):
    """在预览画面上绘制眼睛关键点与状态提示。"""
    if info is None:
        cv2.putText(frame_bgr, "未检测到人脸", (20, 50),
                    cv2.FONT_HERSHEY_SIMPLEX, 1.2, (0, 0, 255), 3)
        return frame_bgr

    lms, w, h = info["landmarks"], info["w"], info["h"]
    color = (0, 0, 255) if closed else (0, 255, 0)

    if show_landmarks:
        for idx in (RIGHT_EYE, LEFT_EYE):
            pts = np.array([(int(lms[i].x * w), int(lms[i].y * h)) for i in idx],
                           dtype=np.int32)
            cv2.polylines(frame_bgr, [pts], True, color, 2)
            for p in pts:
                cv2.circle(frame_bgr, tuple(p), 2, (255, 200, 0), -1)

    cv2.putText(frame_bgr, f"EAR {info['ear']:.2f} / 阈值 {ear_threshold:.2f}",
                (20, 40), cv2.FONT_HERSHEY_SIMPLEX, 0.9, (255, 255, 0), 2)
    cv2.putText(frame_bgr, "闭眼" if closed else "睁眼", (20, 80),
                cv2.FONT_HERSHEY_SIMPLEX, 1.1, color, 3)
    return frame_bgr
