# -*- mode: python ; coding: utf-8 -*-
"""PyInstaller 打包配置：Windows 单文件绿色版 exe。

用法：
    python tools/make_icon.py        # 先生成图标（已生成则可跳过）
    python -m PyInstaller --noconfirm --clean COM-Test-Tool.spec
"""

from pathlib import Path

ROOT = Path(SPECPATH)

# pyserial 通过 importlib 动态加载 URL 处理器，必须显式声明，否则
# 打包后 loop:// / socket:// 等虚拟端口不可用
HIDDEN_IMPORTS = [
    "serial.tools.list_ports_windows",
    "serial.tools.list_ports_linux",
    "serial.tools.list_ports_osx",
    "serial.urlhandler.protocol_loop",
    "serial.urlhandler.protocol_socket",
    "serial.urlhandler.protocol_rfc2217",
    "serial.urlhandler.protocol_spy",
    "serial.urlhandler.protocol_hwgrep",
    "serial.urlhandler.protocol_alt",
]

a = Analysis(
    ["main.py"],
    pathex=[str(ROOT)],
    binaries=[],
    datas=[],
    hiddenimports=HIDDEN_IMPORTS,
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=["pytest", "numpy", "PIL", "matplotlib", "scipy", "pandas", "tkinter.test"],
    noarchive=False,
    optimize=0,
)

pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.datas,
    [],
    name="COM-Test-Tool",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=False,
    runtime_tmpdir=None,
    console=False,                     # GUI 程序：运行时不弹控制台黑框
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
    icon=str(ROOT / "assets" / "app.ico"),
    version=str(ROOT / "packaging" / "version_info.txt"),
)
