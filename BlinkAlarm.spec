# -*- mode: python ; coding: utf-8 -*-
"""PyInstaller 打包配置（精简体积版）。

优化点：
  1. opencv-python-headless 去掉 Qt 界面库（requirements.txt 已切换）
  2. 只保留 mediapipe 的 face_mesh 模型，丢掉 hand/pose/holistic 等不用的模型
  3. strip=True 去掉调试符号
  4. upx=True 加 UPX 压缩（需 CI 中安装 upx）
  5. excludes 排除大量无关大依赖
"""
from PyInstaller.utils.hooks import collect_all

datas, binaries, hiddenimports = collect_all("mediapipe")

# --- 只保留 face_mesh 相关模型，丢弃其它 mediapipe 模型 ---
keep_keywords = ('face_mesh', 'face_detection', 'face_geometry', 'palm_detection')
filtered_datas = []
for src, dst in datas:
    lower = dst.lower()
    is_model = any(lower.endswith(ext) for ext in ('.tflite', '.bytes', '.pb', '.pbtxt'))
    if is_model:
        if any(k in lower for k in keep_keywords):
            filtered_datas.append((src, dst))
    else:
        filtered_datas.append((src, dst))
datas = filtered_datas

hiddenimports += []

a = Analysis(
    ["main.py"],
    pathex=[],
    binaries=binaries,
    datas=datas,
    hiddenimports=hiddenimports,
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[
        'scipy', 'matplotlib', 'pandas', 'sklearn', 'scikit-learn', 'sympy',
        'torch', 'torchvision', 'tensorflow', 'keras', 'jax',
        'PyQt5', 'PyQt6', 'PySide2', 'PySide6', 'wx',
        'unittest', 'pytest', 'setuptools', 'distutils',
        'numpy.tests', 'numpy.distutils', 'numpy.f2py',
    ],
    noarchive=False,
)

pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.datas,
    [],
    name="BlinkAlarm",
    debug=False,
    bootloader_ignore_signals=False,
    strip=True,
    upx=True,
    upx_exclude=[],
    runtime_tmpdir=None,
    console=False,
    disable_windowed_traceback=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
)
