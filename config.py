"""配置管理：默认参数、灵敏度映射、读写 config.json。"""
import json
import os

# 配置文件放在程序同目录下，方便整包拷贝
CONFIG_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "config.json")

DEFAULT_CONFIG = {
    # 摄像头
    "camera_index": 0,
    "mirror": True,               # 画面镜像，符合镜子直觉

    # 眨眼检测
    "sensitivity": 60,            # 灵敏度 0-100，越大越容易判定为闭眼
    "target_blinks": 5,           # 连续眨眼多少次触发报警
    "blink_window": 2.0,          # 连续眨眼的累计时间窗口(秒)
    "min_closed_frames": 2,       # 至少连续闭眼多少帧才算一次眨眼(防抖)
    "max_blink_frames": 25,       # 闭眼超过多少帧不算眨眼(排除长时间闭眼)
    "cooldown": 8.0,              # 触发报警后的冷却时间(秒)，防止连续触发

    # 声音报警
    "alarm_sound_enabled": True,
    "alarm_duration": 6.0,        # 声音持续时长(秒)
    "alarm_sound_file": "",       # 自定义 wav 文件路径，留空使用内置警笛声

    # 外部设备报警（Home Assistant / 米家 等 webhook）
    "webhook_enabled": False,
    "webhook_url": "",
    "webhook_method": "POST",
    "webhook_payload": '{"event": "blink_alarm", "target_blinks": 5}',

    # 界面
    "show_landmarks": True,       # 预览画面是否绘制眼睛关键点
}


def sensitivity_to_ear(sensitivity):
    """把 0-100 的灵敏度映射为 EAR 闭眼阈值。

    EAR(眼睛纵横比) 睁眼约 0.28~0.35，闭眼约 0.10~0.15。
    灵敏度越高 -> 阈值越高 -> 越容易判为闭眼。
    """
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
