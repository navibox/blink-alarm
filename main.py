"""眨眼报警助手 —— 程序入口。

用法： python main.py
"""
import sys
import traceback


def check_env():
    missing = []
    for mod, tip in [("numpy", "numpy"), ("PIL", "Pillow"),
                     ("cv2", "opencv-python"), ("mediapipe", "mediapipe")]:
        try:
            __import__(mod)
        except Exception:
            missing.append(tip)
    return missing


def main():
    missing = check_env()
    if missing:
        msg = ("缺少以下依赖，请先安装：\n\n    " + "\n    ".join(missing) +
               "\n\n安装方法（在项目目录下执行）：\n"
               "    pip install -r requirements.txt\n\n"
               "Windows 用户也可直接双击「安装依赖.bat」。")
        print(msg)
        try:
            import tkinter as tk
            from tkinter import messagebox
            r = tk.Tk(); r.withdraw()
            messagebox.showerror("缺少依赖", msg)
            r.destroy()
        except Exception:
            pass
        return 1

    import tkinter as tk
    from app import BlinkAlarmApp
    root = tk.Tk()
    BlinkAlarmApp(root)
    root.mainloop()
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception:
        traceback.print_exc()
        try:
            input("\n程序异常退出，按回车键关闭…")
        except Exception:
            pass
