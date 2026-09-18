"""串口引擎：把 pyserial 封装成线程安全的收发对象。

设计要点：
* 读线程只负责收数据并把原始字节抛给回调，界面侧需自行切回主线程；
* ``open/close/write`` 可在任意线程调用；
* 全平台通用：Windows(COMx)、Linux(/dev/tty*) 、macOS(/dev/cu.*)，同时支持
  pyserial 的 URL 端口（``loop://``、``socket://host:port``、``rfc2217://...``）。
"""

from __future__ import annotations

import threading
from typing import Callable, Dict, List, NamedTuple, Optional

try:  # pyserial 缺失时给出友好提示而不是直接崩溃
    import serial
    from serial.tools import list_ports as _list_ports

    SERIAL_AVAILABLE = True
    SERIAL_IMPORT_ERROR = ""
except ImportError as exc:  # pragma: no cover - 取决于运行环境
    serial = None  # type: ignore[assignment]
    _list_ports = None  # type: ignore[assignment]
    SERIAL_AVAILABLE = False
    SERIAL_IMPORT_ERROR = str(exc)

INSTALL_HINT = "未检测到 pyserial，请先执行：pip install pyserial"

# --------------------------------------------------------------------------- #
# 参数选项（界面直接复用，保证映射一致）
# --------------------------------------------------------------------------- #

BAUDRATES = [
    "300", "600", "1200", "2400", "4800", "9600", "14400", "19200", "38400",
    "57600", "115200", "128000", "230400", "256000", "460800", "500000",
    "921600", "1000000", "1500000", "2000000",
]

BYTESIZES: Dict[str, int] = {}
PARITIES: Dict[str, str] = {}
STOPBITS: Dict[str, float] = {}
FLOWS = ("无 (None)", "RTS/CTS", "XON/XOFF", "DTR/DSR")

if SERIAL_AVAILABLE:
    BYTESIZES = {
        "5": serial.FIVEBITS,
        "6": serial.SIXBITS,
        "7": serial.SEVENBITS,
        "8": serial.EIGHTBITS,
    }
    PARITIES = {
        "无 (None)": serial.PARITY_NONE,
        "偶 (Even)": serial.PARITY_EVEN,
        "奇 (Odd)": serial.PARITY_ODD,
        "标记 (Mark)": serial.PARITY_MARK,
        "空格 (Space)": serial.PARITY_SPACE,
    }
    STOPBITS = {
        "1": serial.STOPBITS_ONE,
        "1.5": serial.STOPBITS_ONE_POINT_FIVE,
        "2": serial.STOPBITS_TWO,
    }
else:  # 保持界面可用（仅提示）
    BYTESIZES = {k: int(k) for k in ("5", "6", "7", "8")}
    PARITIES = {k: k for k in ("无 (None)", "偶 (Even)", "奇 (Odd)", "标记 (Mark)", "空格 (Space)")}
    STOPBITS = {"1": 1, "1.5": 1.5, "2": 2}

VIRTUAL_LOOP_URL = "loop://"
VIRTUAL_LOOP_LABEL = "虚拟回环 (loop://) —— 无硬件自测"


class PortInfo(NamedTuple):
    """可用串口描述。"""

    device: str
    description: str
    hwid: str = ""

    @property
    def label(self) -> str:
        if self.description:
            return f"{self.device} — {self.description}"
        return self.device


def list_serial_ports() -> List[PortInfo]:
    """枚举本机串口（跨平台）。"""
    if not SERIAL_AVAILABLE:
        return []
    ports: List[PortInfo] = []
    try:
        for p in sorted(_list_ports.comports(), key=lambda item: item.device):
            ports.append(PortInfo(p.device, p.description or "", p.hwid or ""))
    except Exception:  # pragma: no cover - 底层枚举偶发异常
        return ports
    return ports


class SerialError(RuntimeError):
    """串口操作异常（带用户可读信息）。"""


class SerialEngine:
    """线程安全的串口收发引擎。"""

    def __init__(
        self,
        on_rx: Optional[Callable[[bytes], None]] = None,
        on_state: Optional[Callable[[bool, str], None]] = None,
        on_error: Optional[Callable[[str], None]] = None,
    ) -> None:
        self._on_rx = on_rx
        self._on_state = on_state
        self._on_error = on_error

        self._serial = None
        self._reader: Optional[threading.Thread] = None
        self._stop_event = threading.Event()
        self._write_lock = threading.Lock()
        self._state_lock = threading.Lock()

        self.rx_bytes = 0
        self.tx_bytes = 0
        self.port_name = ""

    # ------------------------------------------------------------------ #
    # 状态
    # ------------------------------------------------------------------ #
    @property
    def is_open(self) -> bool:
        ser = self._serial
        return bool(ser is not None and ser.is_open)

    # ------------------------------------------------------------------ #
    # 打开 / 关闭
    # ------------------------------------------------------------------ #
    def open(
        self,
        port: str,
        baudrate: int = 115200,
        bytesize: str = "8",
        parity: str = "无 (None)",
        stopbits: str = "1",
        flow: str = "无 (None)",
        *,
        dtr: bool = True,
        rts: bool = True,
        read_timeout: float = 0.05,
    ) -> None:
        """打开串口，失败抛出 :class:`SerialError`。"""
        if not SERIAL_AVAILABLE:
            raise SerialError(INSTALL_HINT)
        if self.is_open:
            raise SerialError("串口已打开，请先关闭")

        port = (port or "").strip()
        if not port:
            raise SerialError("请先选择或输入串口")

        kwargs: Dict[str, object] = {
            "baudrate": int(baudrate),
            "bytesize": BYTESIZES.get(bytesize, serial.EIGHTBITS),
            "parity": PARITIES.get(parity, serial.PARITY_NONE),
            "stopbits": STOPBITS.get(stopbits, serial.STOPBITS_ONE),
            "timeout": read_timeout,
            "write_timeout": 2.0,
            "xonxoff": flow == "XON/XOFF",
            "rtscts": flow == "RTS/CTS",
            "dsrdtr": flow == "DTR/DSR",
        }

        try:
            if "://" in port:
                ser = serial.serial_for_url(port, **kwargs)  # type: ignore[arg-type]
            else:
                ser = serial.Serial(port=port, **kwargs)  # type: ignore[arg-type]
        except Exception as exc:  # pyserial 抛出的异常类型较杂，统一包装
            raise SerialError(f"打开 {port} 失败：{exc}") from exc

        try:
            ser.reset_input_buffer()
            ser.reset_output_buffer()
        except Exception:
            pass

        # 初始控制线电平（部分平台/虚拟端口不支持，忽略异常）
        try:
            ser.dtr = bool(dtr)
        except Exception:
            pass
        try:
            ser.rts = bool(rts)
        except Exception:
            pass

        self._serial = ser
        self.port_name = port
        self._stop_event.clear()
        self._reader = threading.Thread(target=self._read_loop, name="serial-reader", daemon=True)
        self._reader.start()
        self._emit_state(True, f"已打开 {port}")

    def close(self) -> None:
        """关闭串口并停止读线程（可重复调用）。"""
        with self._state_lock:
            ser, self._serial = self._serial, None
        self._stop_event.set()

        reader, self._reader = self._reader, None
        if reader is not None and reader.is_alive() and reader is not threading.current_thread():
            reader.join(timeout=1.0)

        if ser is not None:
            try:
                if ser.is_open:
                    ser.close()
            except Exception:
                pass
        self.port_name = ""
        self._emit_state(False, "串口已关闭")

    # ------------------------------------------------------------------ #
    # 收发
    # ------------------------------------------------------------------ #
    def write(self, data: bytes) -> int:
        """发送数据，返回实际写入字节数。"""
        if not data:
            return 0
        ser = self._serial
        if ser is None or not ser.is_open:
            raise SerialError("串口未打开，无法发送")
        with self._write_lock:
            try:
                written = ser.write(data)
                ser.flush()
            except Exception as exc:
                raise SerialError(f"发送失败：{exc}") from exc
        self.tx_bytes += written or 0
        return written or 0

    def _read_loop(self) -> None:
        ser = self._serial
        while not self._stop_event.is_set() and ser is not None:
            try:
                waiting = ser.in_waiting
                data = ser.read(waiting if waiting else 1)
            except Exception as exc:  # 设备被拔出 / 端口异常
                if not self._stop_event.is_set():
                    self._emit_error(f"串口读取异常：{exc}")
                    self.close()
                return
            if data:
                self.rx_bytes += len(data)
                if self._on_rx is not None:
                    try:
                        self._on_rx(data)
                    except Exception as exc:  # 回调异常不应终止读线程
                        self._emit_error(f"接收回调异常：{exc}")

    # ------------------------------------------------------------------ #
    # 控制线
    # ------------------------------------------------------------------ #
    def set_dtr(self, value: bool) -> bool:
        return self._set_line("dtr", value)

    def set_rts(self, value: bool) -> bool:
        return self._set_line("rts", value)

    def _set_line(self, name: str, value: bool) -> bool:
        ser = self._serial
        if ser is None or not ser.is_open:
            return False
        try:
            setattr(ser, name, bool(value))
            return True
        except Exception:
            return False

    def read_control_lines(self) -> Dict[str, Optional[bool]]:
        """读取 CTS/DSR/RI/CD 状态；不支持时值为 ``None``。"""
        result: Dict[str, Optional[bool]] = {"cts": None, "dsr": None, "ri": None, "cd": None}
        ser = self._serial
        if ser is None or not ser.is_open:
            return result
        for name in result:
            try:
                result[name] = bool(getattr(ser, name))
            except Exception:
                result[name] = None
        return result

    def reset_counters(self) -> None:
        self.rx_bytes = 0
        self.tx_bytes = 0

    # ------------------------------------------------------------------ #
    # 回调派发
    # ------------------------------------------------------------------ #
    def _emit_state(self, opened: bool, message: str) -> None:
        if self._on_state is not None:
            try:
                self._on_state(opened, message)
            except Exception:
                pass

    def _emit_error(self, message: str) -> None:
        if self._on_error is not None:
            try:
                self._on_error(message)
            except Exception:
                pass
