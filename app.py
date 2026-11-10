"""眨眼报警助手 —— 主界面（Tkinter，大字体，适配眼控仪操作）。"""
import os
import sys
import time
import queue
import threading
import tkinter as tk
from tkinter import ttk, messagebox, filedialog
import tkinter.font as tkfont

import numpy as np
from PIL import Image, ImageTk

import config as cfgmod
from alarm import Alarm

IS_WINDOWS = sys.platform.startswith("win")


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
        self.cfg = cfgmod.load_config()
        self.alarm = Alarm()

        # 运行时状态
        self.monitoring = False
        self.stop_event = threading.Event()
        self.frame_queue = queue.Queue(maxsize=1)
        self.status_queue = queue.Queue()
        self._last_frame = None
        self._trigger_count = 0
        self._cam_ok = False

        # 供检测线程实时读取的参数（由主线程更新）
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
        self._load_into_ui()
        self._sync_settings()

        self.root.protocol("WM_DELETE_WINDOW", self._on_close)

    # ------------------------------------------------------------------ UI
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

        # ---- 顶部大状态条 ----
        top = tk.Frame(outer, bg="#1f6feb", bd=0)
        top.pack(fill="x", pady=(0, 12))
        self.lbl_status = tk.Label(
            top, text="就绪 · 点击「开始监测」", font=self.f_status,
            bg="#1f6feb", fg="white", pady=14)
        self.lbl_status.pack(side="left", padx=20)

        self.lbl_count = tk.Label(
            top, text="连续眨眼 0 / 5", font=self.f_status,
            bg="#1f6feb", fg="#ffe600", pady=14)
        self.lbl_count.pack(side="right", padx=20)

        body = ttk.Frame(outer)
        body.pack(fill="both", expand=True)

        # ===== 左：预览 =====
        left = ttk.LabelFrame(body, text="摄像头预览")
        left.pack(side="left", fill="both", expand=True, padx=(0, 12))

        self.preview_label = tk.Label(left, bg="black", width=70, height=24)
        self.preview_label.pack(fill="both", expand=True, padx=8, pady=8)

        self.lbl_tip = tk.Label(left, text="提示：正对摄像头，保持面部光线充足",
                                font=self.f_small, fg="#666")
        self.lbl_tip.pack(pady=(0, 8))

        # ===== 右：控制面板 =====
        right = ttk.Frame(body)
        right.pack(side="right", fill="y")

        # 变量
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

        # 运行控制
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

        self.btn_toggle = tk.Button(
            grp_run, text="▶  开始监测", font=self.f_button,
            bg="#2ea043", fg="white", activebackground="#238636",
            activeforeground="white", relief="flat", cursor="hand2",
            command=self.toggle_monitor)
        self.btn_toggle.pack(fill="x", padx=10, pady=(6, 12), ipady=10)

        # 触发设置
        grp_trig = ttk.LabelFrame(right, text="触发设置")
        grp_trig.pack(fill="x", pady=(0, 10))

        row = ttk.Frame(grp_trig); row.pack(fill="x", padx=10, pady=(8, 0))
        ttk.Label(row, text="灵敏度", font=self.f_label).pack(side="left")
        self.lbl_sens = ttk.Label(row, text="60", font=self.f_value, foreground="#1f6feb")
        self.lbl_sens.pack(side="right")
        self.scale_sens = ttk.Scale(grp_trig, from_=0, to=100, orient="horizontal",
                                    variable=self.var_sens, command=lambda e: self._sync_settings())
        self.scale_sens.pack(fill="x", padx=10)
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

        # 声音报警
        grp_sound = ttk.LabelFrame(right, text="声音报警")
        grp_sound.pack(fill="x", pady=(0, 10))
        ttk.Checkbutton(grp_sound, text="启用本机声音报警", variable=self.var_sound,
                        command=self._sync_settings).pack(anchor="w", padx=10, pady=(8, 2))
        row = ttk.Frame(grp_sound); row.pack(fill="x", padx=10, pady=4)
        ttk.Label(row, text="响铃时长(秒)", font=self.f_label).pack(side="left")
        ttk.Spinbox(row, from_=1.0, to=60.0, increment=1.0, width=4,
                    textvariable=self.var_duration, font=self.f_value).pack(side="right")

        row = ttk.Frame(grp_sound); row.pack(fill="x", padx=10, pady=(0, 4))
        ttk.Entry(row, textvariable=self.var_sound_file, font=self.f_small).pack(
            side="left", fill="x", expand=True)
        ttk.Button(row, text="选择wav", command=self._choose_sound).pack(side="right", padx=(6, 0))

        row = ttk.Frame(grp_sound); row.pack(fill="x", padx=10, pady=(4, 10))
        ttk.Button(row, text="测试声音", command=self._test_sound).pack(side="left")
        ttk.Button(row, text="停止", command=self.alarm.stop_sound).pack(side="left", padx=8)

        # 外部设备
        grp_hook = ttk.LabelFrame(right, text="外部设备报警（Home Assistant / 米家等）")
        grp_hook.pack(fill="x", pady=(0, 10))
        ttk.Checkbutton(grp_hook, text="启用外部报警", variable=self.var_webhook,
                        command=self._sync_settings).pack(anchor="w", padx=10, pady=(8, 2))

        row = ttk.Frame(grp_hook); row.pack(fill="x", padx=10, pady=4)
        ttk.Label(row, text="地址", font=self.f_label).pack(side="left")
        ttk.Combobox(row, textvariable=self.var_method, values=["POST", "GET"],
                     width=6, state="readonly", font=self.f_value).pack(side="right")
        ttk.Entry(grp_hook, textvariable=self.var_url, font=self.f_small).pack(
            fill="x", padx=10)
        ttk.Label(grp_hook, text="载荷(JSON)", font=self.f_small).pack(anchor="w", padx=10, pady=(6, 0))
        ttk.Entry(grp_hook, textvariable=self.var_payload, font=self.f_small).pack(
            fill="x", padx=10)
        row = ttk.Frame(grp_hook); row.pack(fill="x", padx=10, pady=(6, 10))
        ttk.Button(row, text="测试外部报警", command=self._test_webhook).pack(side="left")

        # 高级
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

    # -------------------------------------------------------------- 参数同步
    def _load_into_ui(self):
        # 变量已在创建时用 cfg 初始化，这里只需保证界面刷新一次
        pass

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
        ear_thr = cfgmod.sensitivity_to_ear(sens)

        self.live.update({
            "ear_threshold": ear_thr,
            "target": target,
            "blink_window": window,
            "cooldown": cooldown,
            "min_closed_frames": min_closed,
            "max_blink_frames": max_closed,
            "show_landmarks": bool(self.var_show.get()),
            "mirror": bool(self.var_mirror.get()),
        })
        # 报警相关同步进 cfg，供报警触发时读取
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
        self._collect_cfg()
        if cfgmod.save_config(self.cfg):
            messagebox.showinfo("保存成功", "设置已保存到 config.json")
        else:
            messagebox.showerror("保存失败", "无法写入 config.json")

    # ------------------------------------------------------------ 监测流程
    def toggle_monitor(self):
        if self.monitoring:
            self._stop_monitor()
        else:
            self._start_monitor()

    def _start_monitor(self):
        try:
            import cv2  # noqa: F401
            from eye_detector import EyeDetector  # noqa: F401
        except Exception as e:
            messagebox.showerror(
                "缺少依赖",
                f"无法加载检测依赖：{e}\n\n请先双击运行「安装依赖.bat」。")
            return

        self._collect_cfg()
        self.stop_event.clear()
        self._trigger_count = 0
        self._last_frame = None
        self.frame_queue = queue.Queue(maxsize=1)
        self.status_queue = queue.Queue()
        self.monitoring = True

        self.btn_toggle.config(text="■  停止监测", bg="#d1242f",
                               activebackground="#a40e26")
        self._set_status("监测中 · 请眨眼", "#2ea043")

        self.worker = threading.Thread(
            target=self._run_camera,
            args=(int(self.cfg["camera_index"]),),
            daemon=True)
        self.worker.start()
        self.root.after(30, self._poll)

    def _stop_monitor(self):
        self.monitoring = False
        self.stop_event.set()
        self.alarm.stop_sound()
        self.btn_toggle.config(text="▶  开始监测", bg="#2ea043",
                               activebackground="#238636")
        self._set_status("已停止", "#1f6feb")
        self.preview_label.config(image="")
        self.preview_label.imgtk = None
        self._last_frame = None

    def _run_camera(self, camera_index):
        import cv2
        from eye_detector import EyeDetector, BlinkCounter, draw_overlay

        backends = [cv2.CAP_DSHOW, cv2.CAP_MSMF, cv2.CAP_ANY] if IS_WINDOWS else [cv2.CAP_ANY]
        cap = None
        for b in backends:
            try:
                cap = cv2.VideoCapture(camera_index, b)
            except Exception:
                cap = None
            if cap is not None and cap.isOpened():
                break
            if cap is not None:
                cap.release()
                cap = None
        if cap is None:
            self.status_queue.put({"error": f"无法打开摄像头 {camera_index}，请检查设备或换编号。"})
            return

        try:
            cap.set(cv2.CAP_PROP_FRAME_WIDTH, 960)
            cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 720)
        except Exception:
            pass

        detector = EyeDetector()
        counter = BlinkCounter()

        while not self.stop_event.is_set():
            ok, frame = cap.read()
            if not ok:
                self.status_queue.put({"error": "读取摄像头画面失败，监测已停止。"})
                break

            counter.configure(
                self.live.get("ear_threshold", 0.21),
                self.live.get("min_closed_frames", 2),
                self.live.get("max_blink_frames", 25),
                self.live.get("blink_window", 2.0),
                self.live.get("target", 5),
                self.live.get("cooldown", 8.0),
            )

            info = detector.process(frame)
            ear = info["ear"] if info else None
            st = counter.update(ear)

            if st["triggered"]:
                self.alarm.trigger(self.cfg, {"blinks": self.live.get("target", 5)})

            # 先绘制关键点，再整体镜像，保证关键点与画面方向一致
            frame = draw_overlay(frame, info, st["closed"],
                                 self.live.get("ear_threshold", 0.21),
                                 self.live.get("show_landmarks", True))
            if self.live.get("mirror", True):
                frame = cv2.flip(frame, 1)

            rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
            try:
                self.frame_queue.put_nowait(rgb)
            except queue.Full:
                pass

            self.status_queue.put({
                "count": st["count"], "target": self.live.get("target", 5),
                "face": st["face"], "triggered": st["triggered"],
                "closed": st["closed"],
            })

        cap.release()
        detector.close()
        self.status_queue.put({"stopped": True})

    def _poll(self):
        # 最新画面
        try:
            while True:
                self._last_frame = self.frame_queue.get_nowait()
        except queue.Empty:
            pass
        if self._last_frame is not None:
            try:
                img = Image.fromarray(self._last_frame)
                imgtk = ImageTk.PhotoImage(image=img)
                self.preview_label.configure(image=imgtk)
                self.preview_label.imgtk = imgtk
            except Exception:
                pass

        # 状态
        try:
            while True:
                st = self.status_queue.get_nowait()
                self._handle_status(st)
        except queue.Empty:
            pass

        if self.monitoring:
            self.root.after(33, self._poll)

    def _handle_status(self, st):
        if "error" in st:
            self._set_status("错误：" + st["error"], "#d1242f")
            self._stop_monitor()
            messagebox.showerror("摄像头错误", st["error"])
            return
        if st.get("stopped"):
            if self.monitoring:
                self._stop_monitor()
            return
        if not st.get("face", True):
            self._set_status("未检测到人脸 · 请正对摄像头", "#bf8700")
            self.lbl_count.config(text="连续眨眼 - / %d" % st.get("target", 5))
        else:
            self._set_status("监测中 · 请眨眼", "#2ea043")
            self.lbl_count.config(
                text=f"连续眨眼 {st['count']} / {st['target']}")
        if st.get("triggered"):
            self._trigger_count += 1
            self.lbl_trigcount.config(text=f"已触发报警：{self._trigger_count} 次")
            self._set_status("🔔 已触发报警！", "#d1242f")

    def _set_status(self, text, color):
        self.lbl_status.config(text=text)
        self.lbl_status.master.config(bg=color)
        self.lbl_status.config(bg=color)
        self.lbl_count.config(bg=color)

    # ------------------------------------------------------------ 测试按钮
    def _choose_sound(self):
        path = filedialog.askopenfilename(
            title="选择报警声音文件", filetypes=[("WAV 音频", "*.wav"), ("所有文件", "*.*")])
        if path:
            self.var_sound_file.set(path)
            self._sync_settings()

    def _test_sound(self):
        self._sync_settings()
        self.alarm.sound.start(float(self.var_duration.get() or 3.0),
                               self.var_sound_file.get())

    def _test_webhook(self):
        self._sync_settings()
        if not self.var_url.get().strip():
            messagebox.showwarning("提示", "请先填写外部报警地址")
            return
        self._set_status("正在测试外部报警…", "#1f6feb")

        def run():
            code = Alarm.test_webhook(self.cfg)
            self.root.after(0, lambda: self._webhook_result(code))
        threading.Thread(target=run, daemon=True).start()

    def _webhook_result(self, code):
        if code and 200 <= int(code) < 300:
            messagebox.showinfo("测试成功", f"外部报警已发送，返回状态码：{code}")
        elif code:
            messagebox.showwarning("已发送", f"请求已发出，但返回状态码：{code}")
        else:
            messagebox.showerror("测试失败", "无法连接外部地址，请检查地址与网络。")

    def _on_close(self):
        self.monitoring = False
        self.stop_event.set()
        self.alarm.stop_sound()
        try:
            self.root.destroy()
        except Exception:
            pass


def main():
    root = tk.Tk()
    BlinkAlarmApp(root)
    root.mainloop()


if __name__ == "__main__":
    main()
