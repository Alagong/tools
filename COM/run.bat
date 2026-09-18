@echo off
rem Windows 启动脚本：自动检查/安装依赖后启动工具
setlocal
cd /d "%~dp0"

where python >nul 2>nul
if errorlevel 1 (
    echo [错误] 未找到 python，请先安装 Python 3.8+ 并加入 PATH。
    pause
    exit /b 1
)

python -c "import serial" >nul 2>nul
if errorlevel 1 (
    echo [提示] 正在安装依赖 pyserial ...
    python -m pip install -r requirements.txt
)

python main.py %*
if errorlevel 1 pause
endlocal
