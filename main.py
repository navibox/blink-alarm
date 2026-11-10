
# -*- coding: utf-8 -*-
"""
眨眼报警助手 (BlinkAlarm) —— 单文件版
=========================================
面向渐冻症（ALS）患者：摄像头监测眼睛，连续眨眼 N 次即触发声响报警，
并可选联动 Home Assistant / 米家等外部设备。
界面采用大字体、大按钮，适配眼控仪操作。

本地运行： python main.py
GitHub 自动编译：推送本文件到仓库后由 .github/workflows/build.yml 打包成 exe。
"""
import os
import sys
import json
import time
import math
import wave
import struct
import queue
import threading
import tempfile
import traceback
import urllib.request
import urllib.error
import tkinter as tk
from tkinter import ttk, messagebox, filedialog
import tkinter.font as tkfont

import numpy as np
from PIL import Image, ImageTk

IS_WINDOWS = sys.platform.startswith("win")

# ============================================================

# 1. 配置管理

# ============================================================
CONFIG_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "config.json")

# PyInstaller 打包后，配置文件放到 exe 同级目录
if getattr(sys, "frozen", False):
    CONFIG_PATH = os.path.join(os.path.dirname(sys.executable), "config.json")

DEFAULT_CONFIG = {
    "camera_index": 0,
    "mirror": True,
    "sensitivity": 60,
    "target_blinks": 5,
    "blink_window": 2.0,
    "min_closed_frames": 2,
    "max_blink_frames": 25,
    "cooldown": 8.0,
    "alarm_sound_enabled": True,
    "alarm_duration": 6.0,
    "alarm_sound_file": "",
    "webhook_enabled": False,
    "webhook_url": "",
    "webhook_method": "POST",
    "webhook_payload": '{"event": "blink_alarm", "target_blinks": 5}',
    "show_landmarks": True,
}

def sensitivity_to_ear(sensitivity):
    sensitivity = max(0, min(100, sensitivity))
    return 0.15 + (sensitivity / 100.0) * 0.15   # 0.15 ~ 0.30

def load_config():
    cfg = dict(DEFAULT_CONFIG)
    if os.path.exists(CONFIG_PATH):
        try:
            with open(CONFIG_PATH, "r", encoding="utf-8") as f:
                saved = json.load(f)
            if isinstance(saved, dict):
                cfg.update(saved)
        except Exception:
            pass
    return cfg

def save_config(cfg):
    try:
        with open(CONFIG_PATH, "w", encoding="utf-8") as f:
            json.dump(cfg, f, ensure_ascii=False, indent=2)
        return True
    except Exception:
        return False

# ============================================================

# 2. 眼睛检测与眨眼计数

# ============================================================
RIGHT_EYE = [33, 160, 158, 133, 153, 144]
LEFT_EYE = [362, 385, 387, 263, 373, 380]

class EyeDetector:
    def __init__(self):
        import mediapipe as mp
        self._mp = mp
        self.face_mesh = mp.solutions.face_mesh.FaceMesh(
            max_num_faces=1, refine_landmarks=True,
            min_detection_confidence=0.5, min_tracking_confidence=0.5)

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
        import cv2
        h, w = frame_bgr.shape[:2]
        rgb = cv2.cvtColor(frame_bgr, cv2.COLOR_BGR2RGB)
        rgb.flags.writeable = False
        res = self.face_mesh.process(rgb)
        if not res.multi_face_landmarks:
            return None
        lms = res.multi_face_landmarks[0].landmark
        left = self._ear(lms, LEFT_EYE, w, h)
        right = self._ear(lms, RIGHT_EYE, w, h)
        return {"ear": (left + right) / 2.0, "landmarks": lms, "w": w, "h": h}

    def close(self):
        try:
            self.face_mesh.close()
        except Exception:
            pass

class BlinkCounter:
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
        now = time.time()
        if ear is None:
            self.closed_frames = 0
            if self.count > 0 and (now - self.last_blink_time) > self.blink_window:
                self.count = 0
            return {"closed": False, "count": self.count, "triggered": False, "face": False}

        closed = ear < self.ear_threshold
        if closed:
            self.closed_frames += 1
        else:
            if self.min_closed_frames <= self.closed_frames <= self.max_blink_frames:
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

        if self.count > 0 and (now - self.last_blink_time) > self.blink_window:
            self.count = 0
        return {"closed": closed, "count": self.count, "triggered": triggered, "face": True}

def draw_overlay(frame_bgr, info, closed, ear_threshold, show_landmarks=True):
    import cv2
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

# ============================================================

# 3. 报警：声音 + Webhook

# ============================================================
_SIREN_PATH = None

def _build_siren_wav():
    global _SIREN_PATH
    if _SIREN_PATH and os.path.exists(_SIREN_PATH):
        return _SIREN_PATH
    path = os.path.join(tempfile.gettempdir(), "blink_alarm_siren.wav")
    rate = 44100
    frames = bytearray()
    for freq, dur in [(880, 0.5), (1320, 0.5)]:
        n = int(rate * dur)
        for i in range(n):
            env = 1.0
            if i < 500:
                env = i / 500.0
            elif i > n - 500:
                env = (n - i) / 500.0
            val = int(32767 * 0.5 * env * math.sin(2 * math.pi * freq * i / rate))
            frames += struct.pack("<h", val)
    with wave.open(path, "w") as wf:
        wf.setnchannels(1)
        wf.setsampwidth(2)
        wf.setframerate(rate)
        wf.writeframes(bytes(frames))
    _SIREN_PATH = path
    return path

class SoundAlarm:
    def __init__(self):
        self._stop = threading.Event()
        self._thread = None

    def start(self, duration, sound_file=""):
        self.stop()
        self._stop.clear()
        self._thread = threading.Thread(
            target=self._play, args=(duration, sound_file), daemon=True)
        self._thread.start()

    def stop(self):
        self._stop.set()
        if IS_WINDOWS:
            try:
                import winsound
                winsound.PlaySound(None, winsound.SND_PURGE)
            except Exception:
                pass

    def _play(self, duration, sound_file):
        path = sound_file if (sound_file and os.path.exists(sound_file)) else _build_siren_wav()
        end_time = time.time() + duration
        if IS_WINDOWS:
            try:
                import winsound
                winsound.PlaySound(path, winsound.SND_FILENAME | winsound.SND_ASYNC | winsound.SND_LOOP)
                while time.time() < end_time and not self._stop.is_set():
                    time.sleep(0.1)
                winsound.PlaySound(None, winsound.SND_PURGE)
                return
            except Exception:
                pass
        while time.time() < end_time and not self._stop.is_set():
            sys.stdout.write("\a")
            sys.stdout.flush()
            time.sleep(0.6)

class Alarm:
    def __init__(self):
        self.sound = SoundAlarm()

    def trigger(self, cfg, extra=None):
        if cfg.get("alarm_sound_enabled", True):
            self.sound.start(float(cfg.get("alarm_duration", 6.0)),
                             cfg.get("alarm_sound_file", "") or "")
        if cfg.get("webhook_enabled") and cfg.get("webhook_url"):
            threading.Thread(target=self._send_webhook,
                             args=(cfg, extra or {}), daemon=True).start()

    def stop_sound(self):
        self.sound.stop()

    @staticmethod
    def _send_webhook(cfg, extra):
        url = cfg.get("webhook_url", "").strip()
        method = (cfg.get("webhook_method") or "POST").upper()
        payload = cfg.get("webhook_payload", "") or "{}"
        try:
            data = json.loads(payload)
            if isinstance(data, dict):
                data.update(extra)
            body = json.dumps(data, ensure_ascii=False).encode("utf-8")
        except Exception:
            body = (payload or "").encode("utf-8")
        req = urllib.request.Request(url, data=body if method == "POST" else None, method=method)
        req.add_header("Content-Type", "application/json")
        try:
            with urllib.request.urlopen(req, timeout=5) as resp:
                return resp.status
        except Exception:
            return None

    @staticmethod
    def test_webhook(cfg):
        return Alarm._send_webhook(cfg, {"test": True})

# ============================================================

# 4. 主界面

# ============================================================
def pick_font_family():
    try:
        families = set(tkfont.families())
    except Exception:
        families = set()
    for name in ["Microsoft YaHei UI", "Microsoft YaHei", "微软雅黑",
                 "PingFang SC", "Noto Sans CJK SC", "SimHei"]:
        if name in families:
            return name
    return "Arial"

class BlinkAlarmApp:
    def __init__(self, root):
        self.root = root
        self.cfg = load_config()
        self.alarm = Alarm()
        self.monitoring = False
        self.stop_event = threading.Event()
        self.frame_queue = queue.Queue(maxsize=1)
        self.status_queue = queue.Queue()
        self._last_frame = None
        self._trigger_count = 0
        self.live = {}

        family = pick_font_family()
        self.f_title = tkfont.Font(family=family, size=24, weight="bold")
        self.f_section = tkfont.Font(family=family, size=17, weight="bold")
        self.f_label = tkfont.Font(family=family, size=16)
        self.f_value = tkfont.Font(family=family, size=17, weight="bold")
        self.f_button = tkfont.Font(family=family, size=18, weight="bold")
        self.f_status = tkfont.Font(family=family, size=30, weight="bold")
        self.f_small = tkfont.Font(family=family, size=13)

        self._build_ui()
        self._sync_settings()
        self.root.protocol("WM_DELETE_WINDOW", self._on_close)

    def _build_ui(self):
        self.root.title("眨眼报警助手")
        self.root.configure(bg="#f2f4f7")
        self.root.minsize(1080, 760)

        style = ttk.Style()
        try:
            style.theme_use("clam")
        except Exception:
            pass
        style.configure("TLabelframe.Label", font=self.f_section)
        style.configure("TRadiobutton", font=self.f_label)
        style.configure("TCheckbutton", font=self.f_label)

        outer = ttk.Frame(self.root, padding=14)
        outer.pack(fill="both", expand=True)

        top = tk.Frame(outer, bg="#1f6feb")
        top.pack(fill="x", pady=(0, 12))
        self.lbl_status = tk.Label(top, text="就绪 · 点击「开始监测」",
                                   font=self.f_status, bg="#1f6feb", fg="white", pady=14)
        self.lbl_status.pack(side="left", padx=20)
        self.lbl_count = tk.Label(top, text="连续眨眼 0 / 5", font=self.f_status,
                                  bg="#1f6feb", fg="#ffe600", pady=14)
        self.lbl_count.pack(side="right", padx=20)

        body = ttk.Frame(outer)
        body.pack(fill="both", expand=True)

        left = ttk.LabelFrame(body, text="摄像头预览")
        left.pack(side="left", fill="both", expand=True, padx=(0, 12))
        self.preview_label = tk.Label(left, bg="black", width=70, height=24)
        self.preview_label.pack(fill="both", expand=True, padx=8, pady=8)
        tk.Label(left, text="提示：正对摄像头，保持面部光线充足",
                 font=self.f_small, fg="#666").pack(pady=(0, 8))

        right = ttk.Frame(body)
        right.pack(side="right", fill="y")

        c = self.cfg
        self.var_cam = tk.IntVar(value=int(c.get("camera_index", 0)))
        self.var_mirror = tk.BooleanVar(value=bool(c.get("mirror", True)))
        self.var_sens = tk.IntVar(value=int(c.get("sensitivity", 60)))
        self.var_target = tk.IntVar(value=int(c.get("target_blinks", 5)))
        self.var_window = tk.DoubleVar(value=float(c.get("blink_window", 2.0)))
        self.var_cooldown = tk.DoubleVar(value=float(c.get("cooldown", 8.0)))
        self.var_min_closed = tk.IntVar(value=int(c.get("min_closed_frames", 2)))
        self.var_max_closed = tk.IntVar(value=int(c.get("max_blink_frames", 25)))
        self.var_show = tk.BooleanVar(value=bool(c.get("show_landmarks", True)))
        self.var_sound = tk.BooleanVar(value=bool(c.get("alarm_sound_enabled", True)))
        self.var_duration = tk.DoubleVar(value=float(c.get("alarm_duration", 6.0)))
        self.var_sound_file = tk.StringVar(value=c.get("alarm_sound_file", ""))
        self.var_webhook = tk.BooleanVar(value=bool(c.get("webhook_enabled", False)))
        self.var_url = tk.StringVar(value=c.get("webhook_url", ""))
        self.var_method = tk.StringVar(value=c.get("webhook_method", "POST"))
        self.var_payload = tk.StringVar(value=c.get("webhook_payload", ""))

        grp_run = ttk.LabelFrame(right, text="运行控制")
        grp_run.pack(fill="x", pady=(0, 10))
        row = ttk.Frame(grp_run); row.pack(fill="x", padx=10, pady=6)
        ttk.Label(row, text="摄像头编号", font=self.f_label).pack(side="left")
        ttk.Spinbox(row, from_=0, to=9, width=4, textvariable=self.var_cam,
                    font=self.f_value, command=self._sync_settings).pack(side="right")
        row = ttk.Frame(grp_run); row.pack(fill="x", padx=10, pady=4)
        ttk.Checkbutton(row, text="画面镜像", variable=self.var_mirror,
                        command=self._sync_settings).pack(side="left")
        ttk.Checkbutton(row, text="显示眼睛关键点", variable=self.var_show,
                        command=self._sync_settings).pack(side="left", padx=(20, 0))
        self.btn_toggle = tk.Button(grp_run, text="▶  开始监测", font=self.f_button,
                                    bg="#2ea043", fg="white", activebackground="#238636",
                                    activeforeground="white", relief="flat", cursor="hand2",
                                    command=self.toggle_monitor)
        self.btn_toggle.pack(fill="x", padx=10, pady=(6, 12), ipady=10)

        grp_trig = ttk.LabelFrame(right, text="触发设置")
        grp_trig.pack(fill="x", pady=(0, 10))
        row = ttk.Frame(grp_trig); row.pack(fill="x", padx=10, pady=(8, 0))
        ttk.Label(row, text="灵敏度", font=self.f_label).pack(side="left")
        self.lbl_sens = ttk.Label(row, text="60", font=self.f_value, foreground="#1f6feb")
        self.lbl_sens.pack(side="right")
        ttk.Scale(grp_trig, from_=0, to=100, orient="horizontal",
                  variable=self.var_sens, command=lambda e: self._sync_settings()).pack(fill="x", padx=10)
        self.lbl_ear = ttk.Label(grp_trig, text="", font=self.f_small, foreground="#888")
        self.lbl_ear.pack(anchor="w", padx=10, pady=(0, 6))
        row = ttk.Frame(grp_trig); row.pack(fill="x", padx=10, pady=4)
        ttk.Label(row, text="连续眨眼次数", font=self.f_label).pack(side="left")
        ttk.Spinbox(row, from_=1, to=10, width=4, textvariable=self.var_target,
                    font=self.f_value, command=self._sync_settings).pack(side="right")
        row = ttk.Frame(grp_trig); row.pack(fill="x", padx=10, pady=4)
        ttk.Label(row, text="累计时间窗口(秒)", font=self.f_label).pack(side="left")
        ttk.Spinbox(row, from_=0.5, to=10.0, increment=0.5, width=4,
                    textvariable=self.var_window, font=self.f_value,
                    command=self._sync_settings).pack(side="right")
        row = ttk.Frame(grp_trig); row.pack(fill="x", padx=10, pady=(4, 10))
        ttk.Label(row, text="报警冷却(秒)", font=self.f_label).pack(side="left")
        ttk.Spinbox(row, from_=1.0, to=60.0, increment=1.0, width=4,
                    textvariable=self.var_cooldown, font=self.f_value,
                    command=self._sync_settings).pack(side="right")

        grp_sound = ttk.LabelFrame(right, text="声音报警")
        grp_sound.pack(fill="x", pady=(0, 10))
        ttk.Checkbutton(grp_sound, text="启用本机声音报警", variable=self.var_sound,
                        command=self._sync_settings).pack(anchor="w", padx=10, pady=(8, 2))
        row = ttk.Frame(grp_sound); row.pack(fill="x", padx=10, pady=4)
        ttk.Label(row, text="响铃时长(秒)", font=self.f_label).pack(side="left")
        ttk.Spinbox(row, from_=1.0, to=60.0, increment=1.0, width=4,
                    textvariable=self.var_duration, font=self.f_value).pack(side="right")
        row = ttk.Frame(grp_sound); row.pack(fill="x", padx=10, pady=(0, 4))
        ttk.Entry(row, textvariable=self.var_sound_file, font=self.f_small).pack(side="left", fill="x", expand=True)
        ttk.Button(row, text="选择wav", command=self._choose_sound).pack(side="right", padx=(6, 0))
        row = ttk.Frame(grp_sound); row.pack(fill="x", padx=10, pady=(4, 10))
        ttk.Button(row, text="测试声音", command=self._test_sound).pack(side="left")
        ttk.Button(row, text="停止", command=self.alarm.stop_sound).pack(side="left", padx=8)

        grp_hook = ttk.LabelFrame(right, text="外部设备报警（Home Assistant / 米家等）")
        grp_hook.pack(fill="x", pady=(0, 10))
        ttk.Checkbutton(grp_hook, text="启用外部报警", variable=self.var_webhook,
                        command=self._sync_settings).pack(anchor="w", padx=10, pady=(8, 2))
        row = ttk.Frame(grp_hook); row.pack(fill="x", padx=10, pady=4)
        ttk.Label(row, text="地址", font=self.f_label).pack(side="left")
        ttk.Combobox(row, textvariable=self.var_method, values=["POST", "GET"],
                     width=6, state="readonly", font=self.f_value).pack(side="right")
        ttk.Entry(grp_hook, textvariable=self.var_url, font=self.f_small).pack(fill="x", padx=10)
        ttk.Label(grp_hook, text="载荷(JSON)", font=self.f_small).pack(anchor="w", padx=10, pady=(6, 0))
        ttk.Entry(grp_hook, textvariable=self.var_payload, font=self.f_small).pack(fill="x", padx=10)
        row = ttk.Frame(grp_hook); row.pack(fill="x", padx=10, pady=(6, 10))
        ttk.Button(row, text="测试外部报警", command=self._test_webhook).pack(side="left")

        grp_adv = ttk.LabelFrame(right, text="高级（一般无需修改）")
        grp_adv.pack(fill="x", pady=(0, 10))
        row = ttk.Frame(grp_adv); row.pack(fill="x", padx=10, pady=(8, 4))
        ttk.Label(row, text="最少闭眼帧数", font=self.f_label).pack(side="left")
        ttk.Spinbox(row, from_=1, to=10, width=4, textvariable=self.var_min_closed,
                    font=self.f_value, command=self._sync_settings).pack(side="right")
        row = ttk.Frame(grp_adv); row.pack(fill="x", padx=10, pady=(4, 10))
        ttk.Label(row, text="最长闭眼帧数", font=self.f_label).pack(side="left")
        ttk.Spinbox(row, from_=5, to=100, width=4, textvariable=self.var_max_closed,
                    font=self.f_value, command=self._sync_settings).pack(side="right")

        ttk.Button(right, text="保存设置", command=self._save).pack(fill="x", pady=(0, 6))
        self.lbl_trigcount = ttk.Label(right, text="已触发报警：0 次", font=self.f_small)
        self.lbl_trigcount.pack(anchor="w")

    def _sync_settings(self):
        try:
            sens = int(float(self.var_sens.get()))
            target = int(float(self.var_target.get()))
            window = float(self.var_window.get())
            cooldown = float(self.var_cooldown.get())
            min_closed = int(float(self.var_min_closed.get()))
            max_closed = int(float(self.var_max_closed.get()))
        except (tk.TclError, ValueError):
            return
        ear_thr = sensitivity_to_ear(sens)
        self.live.update({
            "ear_threshold": ear_thr, "target": target, "blink_window": window,
            "cooldown": cooldown, "min_closed_frames": min_closed,
            "max_blink_frames": max_closed,
            "show_landmarks": bool(self.var_show.get()),
            "mirror": bool(self.var_mirror.get()),
        })
        self.cfg.update({
            "alarm_sound_enabled": bool(self.var_sound.get()),
            "alarm_duration": float(self.var_duration.get() or 6.0),
            "alarm_sound_file": self.var_sound_file.get(),
            "webhook_enabled": bool(self.var_webhook.get()),
            "webhook_url": self.var_url.get(),
            "webhook_method": self.var_method.get(),
            "webhook_payload": self.var_payload.get(),
        })
        self.lbl_sens.config(text=str(sens))
        self.lbl_ear.config(text=f"闭眼阈值 EAR = {ear_thr:.3f}（越高越灵敏）")
        self.lbl_count.config(text=f"连续眨眼 0 / {target}")

    def _collect_cfg(self):
        self._sync_settings()
        self.cfg.update({
            "camera_index": int(float(self.var_cam.get())),
            "mirror": bool(self.var_mirror.get()),
            "sensitivity": int(float(self.var_sens.get())),
            "target_blinks": int(float(self.var_target.get())),
            "blink_window": float(self.var_window.get()),
            "cooldown": float(self.var_cooldown.get()),
            "min_closed_frames": int(float(self.var_min_closed.get())),
            "max_blink_frames": int(float(self.var_max_closed.get())),
            "show_landmarks": bool(self.var_show.get()),
        })
        return self.cfg

    def _save(self):
        if save_config(self._collect_cfg()):
            messagebox.showinfo("保存成功", "设置已保存到 config.json")
        else:
            messagebox.showerror("保存失败", "无法写入 config.json")

    def toggle_monitor(self):
        if self.monitoring:
            self._stop_monitor()
        else:
            self._start_monitor()

    def _start_monitor(self):
        try:
            import cv2  # noqa: F401
        except Exception as e:
            messagebox.showerror("缺少依赖", f"无法加载检测依赖：{e}")
            return
        self._collect_cfg()
        self.stop_event.clear()
        self._trigger_count = 0
        self.frame_queue = queue.Queue(maxsize=1)
        self.status_queue = queue.Queue()
        self.monitoring = True
