@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo ============================================
echo   眨眼报警助手 - 安装依赖
echo ============================================
echo.
where python >nul 2>nul
if errorlevel 1 (
  echo [错误] 未找到 python，请先安装 Python 3.10 ~ 3.12 并勾选 "Add to PATH"。
  pause
  exit /b
)
python -m pip install --upgrade pip
python -m pip install -r requirements.txt
echo.
echo 依赖安装完成。下一步请双击「启动程序.bat」。
pause
