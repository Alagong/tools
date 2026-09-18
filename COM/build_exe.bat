@echo off
rem 一键打包 Windows 单文件 exe（产物：dist\COM-Test-Tool.exe）
setlocal
cd /d "%~dp0"

where python >nul 2>nul
if errorlevel 1 (
    echo [错误] 未找到 python，请先安装 Python 3.8+ 并加入 PATH。
    pause
    exit /b 1
)

python -c "import PyInstaller" >nul 2>nul
if errorlevel 1 (
    echo [提示] 正在安装打包依赖 PyInstaller ...
    python -m pip install pyinstaller
)

echo [1/3] 生成应用图标 ...
python tools\make_icon.py
if errorlevel 1 goto error

echo [2/3] 运行自检，确认源码可正常工作 ...
python main.py --selftest >nul 2>nul
if errorlevel 1 (
    echo [警告] 源码自检未通过，仍继续打包。
)

echo [3/3] 调用 PyInstaller 打包 ...
python -m PyInstaller --noconfirm --clean COM-Test-Tool.spec
if errorlevel 1 goto error

echo.
echo [完成] 产物：dist\COM-Test-Tool.exe
echo        验证：dist\COM-Test-Tool.exe --selftest --log selftest.txt
pause
exit /b 0

:error
echo.
echo [失败] 打包过程出错，请查看上方日志。
pause
exit /b 1
