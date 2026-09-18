#!/usr/bin/env python3
"""跨平台 COM(串口) 通信测试工具 —— 程序入口。

用法：
    python main.py                             # 启动图形界面
    python main.py --selftest                  # 无界面自检（数据工具 + loop:// 回环）
    python main.py --selftest --log out.txt    # 自检结果写入文件（打包为 exe 后无控制台时用）
"""

from __future__ import annotations

import sys
from pathlib import Path

if not getattr(sys, "frozen", False):
    # 源码运行时确保可以 import app 包；打包后由 PyInstaller 处理
    sys.path.insert(0, str(Path(__file__).resolve().parent))


def enable_windows_dpi_awareness() -> None:
    """Windows 高分屏下让界面保持清晰。"""
    if sys.platform != "win32":
        return
    try:
        import ctypes

        try:
            ctypes.windll.shcore.SetProcessDpiAwareness(1)  # type: ignore[attr-defined]
        except Exception:
            ctypes.windll.user32.SetProcessDPIAware()  # type: ignore[attr-defined]
    except Exception:
        pass


def selftest(log_path: str | None = None) -> int:
    """不需要硬件的端到端自检；结果可写入文件（便于验证打包产物）。"""
    import time

    lines: list = []

    def emit(text: str = "") -> None:
        lines.append(text)
        if sys.stdout is not None:  # 打包为 --windowed 时没有控制台
            try:
                print(text)
            except Exception:
                pass

    def finish() -> None:
        if log_path:
            try:
                Path(log_path).write_text("\n".join(lines), encoding="utf-8")
            except OSError as exc:
                emit(f"[WARN] 日志写入失败：{exc}")

    from app.core.data_utils import (
        append_checksum,
        checksum_sum8,
        checksum_xor,
        crc16_modbus,
        extract_numbers,
        format_hex,
        hex_dump,
        parse_hex,
    )
    from app.core.serial_engine import SERIAL_AVAILABLE, SerialEngine, list_serial_ports

    failures: list = []

    def check(name: str, condition: bool, detail: str = "") -> None:
        status = "PASS" if condition else "FAIL"
        emit(f"[{status}] {name}{(' -> ' + detail) if detail else ''}")
        if not condition:
            failures.append(name)

    # 1. 数据工具
    check("parse_hex 空格分隔", parse_hex("AA BB 0c") == b"\xaa\xbb\x0c")
    check("parse_hex 0x 前缀", parse_hex("0x01,0x02;0x03") == b"\x01\x02\x03")
    check("parse_hex 注释忽略", parse_hex("AA BB // 注释\n0xCC") == b"\xaa\xbb\xcc")
    try:
        parse_hex("ZZ")
        check("parse_hex 非法输入报错", False)
    except ValueError:
        check("parse_hex 非法输入报错", True)
    check("format_hex", format_hex(b"\x01\xab") == "01 AB", format_hex(b"\x01\xab"))
    check("hex_dump 结构", hex_dump(b"ABC", 0).endswith("|ABC|"), hex_dump(b"ABC", 0))
    check("SUM8", checksum_sum8(b"\x01\x02\x03") == b"\x06")
    check("XOR", checksum_xor(b"\x0f\xf0") == b"\xff")
    check("CRC16-Modbus 标准向量", crc16_modbus(b"123456789") == b"\x37\x4b",
          crc16_modbus(b"123456789").hex(" "))
    check("append_checksum 无", append_checksum(b"\x01", "无") == b"\x01")
    check("extract_numbers", extract_numbers("t=25.5,h=-3.0 4e2") == [25.5, -3.0, 400.0],
          str(extract_numbers("t=25.5,h=-3.0 4e2")))

    # 2. 串口枚举
    ports = list_serial_ports()
    emit(f"[INFO] 枚举到 {len(ports)} 个串口：{[p.device for p in ports]}")

    # 3. loop:// 回环收发
    if not SERIAL_AVAILABLE:
        check("pyserial 可用", False, "请执行 pip install pyserial")
    else:
        received = bytearray()
        errors: list = []
        engine = SerialEngine(on_rx=received.extend, on_error=errors.append)
        try:
            engine.open("loop://", baudrate=115200)
            engine.write(b"hello-com")
            deadline = time.time() + 2.0
            while time.time() < deadline and len(received) < 9:
                time.sleep(0.02)
            check("loop:// 回环收发", bytes(received) == b"hello-com",
                  f"收到 {bytes(received)!r}")
            check("统计计数", engine.rx_bytes == 9 and engine.tx_bytes == 9,
                  f"RX={engine.rx_bytes} TX={engine.tx_bytes}")
            check("控制线读取不抛异常", isinstance(engine.read_control_lines(), dict))
            check("未打开时发送报错", _expect_serial_error(engine))
        finally:
            engine.close()
        check("关闭后状态正确", not engine.is_open)
        check("读线程无异常", not errors, "; ".join(errors))

    summary = "全部通过" if not failures else f"{len(failures)} 项失败 {failures}"
    emit(f"[INFO] 自检结果：{summary}")
    finish()
    return 0 if not failures else 1


def _expect_serial_error(engine) -> bool:
    from app.core.serial_engine import SerialError

    engine.close()
    try:
        engine.write(b"\x01")
    except SerialError:
        return True
    except Exception:
        return False
    return False


def main(argv: list) -> int:
    if "--selftest" in argv:
        log_path = None
        if "--log" in argv:
            index = argv.index("--log")
            if index + 1 < len(argv):
                log_path = argv[index + 1]
        return selftest(log_path)

    enable_windows_dpi_awareness()

    import tkinter as tk

    from app.core import config as cfg
    from app.ui import theme
    from app.ui.main_window import MainWindow

    # 在创建窗口前按配置选定主题，避免启动时闪一次默认配色
    theme.set_palette(str(cfg.load_config().get("theme", theme.DEFAULT_KEY)))

    root = tk.Tk()
    theme.apply_theme(root)
    MainWindow(root)
    root.mainloop()
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
