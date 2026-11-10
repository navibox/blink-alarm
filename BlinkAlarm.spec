# -*- mode: python ; coding: utf-8 -*-
"""PyInstaller 打包配置（精简体积版）。"""
from PyInstaller.utils.hooks import collect_all

datas, binaries, hiddenimports = collect_all("mediapipe")

# --- 只保留 face_mesh 相关模型，丢弃其它 mediapipe 模型 ---
keep_keywords = ('face_mesh', 'face_detection', 'face_geometry', 'palm_detection')
model_exts = ('.tflite', '.bytes', '.pb', '.pbtxt')

filtered_datas = []
for item in datas:
    if len(item) == 2:
        src, dst = item[0], item[1]
    elif len(item) >= 3:
        dst, src = item[0], item[1]
    else:
        continue
    if not isinstance(dst, str):
        continue
    lower = dst.lower()
    is_model = any(lower.endswith(ext) for ext in model_exts)
    if is_model and not any(k in lower for k in keep_keywords):
        continue
    filtered_datas.append(item)
datas = filtered_datas

hiddenimports += []

# 把资源目录（图标等）打包进 exe
datas += [("assets", "assets")]

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
    strip=True,                # 去掉 DLL 调试符号
    upx=False,                 # UPX 会破坏 python3XX.dll 导致 "Failed to load Python DLL"，故关闭
    upx_exclude=[],
    runtime_tmpdir=None,
    console=False,
    disable_windowed_traceback=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
     icon="assets/icon.ico",      # 应用图标
)
