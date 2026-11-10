# -*- mode: python ; coding: utf-8 -*-
"""PyInstaller 打包配置：把程序打包成单个 Windows exe。

本地打包： pip install pyinstaller 然后执行  pyinstaller BlinkAlarm.spec
GitHub 打包：推送代码后由 .github/workflows/build.yml 自动完成。
"""
from PyInstaller.utils.hooks import collect_all

# mediapipe 内含模型/资源文件，必须完整收集，否则运行时会报找不到文件
datas, binaries, hiddenimports = collect_all("mediapipe")

# 全部逻辑已合并进 main.py，无需再声明子模块
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
    excludes=[],
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
    strip=False,
    upx=False,
    upx_exclude=[],
    runtime_tmpdir=None,
    console=False,          # 不弹出黑色命令行窗口
    disable_windowed_traceback=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
)
