"""报警模块：本机声音报警 + 外部设备 webhook（Home Assistant / 米家 中转）。"""
import os
import sys
import json
import math
import time
import wave
import struct
import tempfile
import threading
import urllib.request
import urllib.error

IS_WINDOWS = sys.platform.startswith("win")
_SIREN_PATH = None


def _build_siren_wav():
    """生成内置双音警笛 wav（只在首次使用时生成一次）。"""
    global _SIREN_PATH
    if _SIREN_PATH and os.path.exists(_SIREN_PATH):
        return _SIREN_PATH

    path = os.path.join(tempfile.gettempdir(), "blink_alarm_siren.wav")
    rate = 44100
    # 交替 880Hz / 1320Hz，各 0.5s，共 1s 循环
    segments = [(880, 0.5), (1320, 0.5)]
    frames = bytearray()
    for freq, dur in segments:
        n = int(rate * dur)
        for i in range(n):
            # 加淡入淡出避免爆音
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
    """可在后台循环播放的报警声。"""

    def __init__(self):
        self._stop = threading.Event()
        self._thread = None
        self._lock = threading.Lock()

    def start(self, duration, sound_file=""):
        self.stop()
        with self._lock:
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
                winsound.PlaySound(
                    path,
                    winsound.SND_FILENAME | winsound.SND_ASYNC | winsound.SND_LOOP)
                while time.time() < end_time and not self._stop.is_set():
                    time.sleep(0.1)
                winsound.PlaySound(None, winsound.SND_PURGE)
                return
            except Exception:
                pass
        # 非 Windows 回退：终端响铃 + 提示（便于在开发机验证逻辑）
        while time.time() < end_time and not self._stop.is_set():
            sys.stdout.write("\a")
            sys.stdout.flush()
            time.sleep(0.6)


class Alarm:
    """对外统一入口：触发声音 + webhook。"""

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

        req = urllib.request.Request(
            url,
            data=body if method == "POST" else None,
            method=method,
        )
        req.add_header("Content-Type", "application/json")
        try:
            with urllib.request.urlopen(req, timeout=5) as resp:
                return resp.status
        except urllib.error.URLError:
            return None
        except Exception:
            return None

    @staticmethod
    def test_webhook(cfg):
        return Alarm._send_webhook(cfg, {"test": True})
