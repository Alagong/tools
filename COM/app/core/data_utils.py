"""数据编解码与解析工具（纯函数，便于单元测试）。"""

from __future__ import annotations

import re
from typing import Iterable, List, Sequence

# --------------------------------------------------------------------------- #
# HEX 解析 / 格式化
# --------------------------------------------------------------------------- #

_SEPARATORS = re.compile(r"[\s,;:_\-|]+")
_HEX_PREFIX = re.compile(r"0[xX]")
_HEX_TOKEN = re.compile(r"[0-9a-fA-F]{1,2}")


def strip_comments(text: str) -> str:
    """去掉 ``//`` 与 ``#`` 起始的注释，以及 C 风格块注释。"""
    text = re.sub(r"/\*.*?\*/", " ", text, flags=re.DOTALL)
    lines = []
    for line in text.splitlines():
        line = re.split(r"//|#", line, maxsplit=1)[0]
        lines.append(line)
    return "\n".join(lines)


def parse_hex(text: str) -> bytes:
    """把十六进制文本解析为字节串。

    支持 ``AA BB``、``AABB``、``0xAA,0xBB``、``AA-BB``、``AA:BB`` 等写法，
    忽略注释、空白与常见分隔符。奇数位数字或非法字符会抛出 ``ValueError``。
    """
    cleaned = _SEPARATORS.sub(" ", _HEX_PREFIX.sub(" ", strip_comments(text))).strip()
    if not cleaned:
        return b""

    tokens = cleaned.split(" ")
    out = bytearray()
    for token in tokens:
        if not token:
            continue
        if not _HEX_TOKEN.fullmatch(token):
            raise ValueError(f"非法的十六进制片段: {token!r}")
        if len(token) == 1:
            token = "0" + token
        out.append(int(token, 16))
    return bytes(out)


def format_hex(data: bytes, upper: bool = True, sep: str = " ") -> str:
    """把字节串格式化为 ``AA BB CC`` 形式。"""
    text = sep.join(f"{b:02X}" if upper else f"{b:02x}" for b in data)
    return text


def hex_dump(data: bytes, offset: int = 0, width: int = 16) -> str:
    """生成带偏移与 ASCII 列的经典 hexdump 文本。"""
    lines: List[str] = []
    for start in range(0, len(data), width):
        chunk = data[start:start + width]
        hex_part = " ".join(f"{b:02X}" for b in chunk)
        hex_part = hex_part.ljust(width * 3 - 1)
        ascii_part = "".join(chr(b) if 32 <= b < 127 else "." for b in chunk)
        lines.append(f"{offset + start:08X}  {hex_part}  |{ascii_part}|")
    return "\n".join(lines)


# --------------------------------------------------------------------------- #
# 文本编解码
# --------------------------------------------------------------------------- #

PRINTABLE_KEEP = "\r\n\t"


def decode_bytes(data: bytes, encoding: str = "utf-8", errors: str = "replace") -> str:
    """按指定编码解码，并把不可打印控制字符替换为 ``.``。"""
    text = data.decode(encoding, errors=errors)
    return "".join(ch if (ch in PRINTABLE_KEEP or ch.isprintable()) else "." for ch in text)


def encode_text(text: str, encoding: str = "utf-8") -> bytes:
    return text.encode(encoding, errors="replace")


# --------------------------------------------------------------------------- #
# 校验和
# --------------------------------------------------------------------------- #

CHECKSUM_CHOICES = ("无", "SUM8", "XOR", "CRC16-Modbus")


def checksum_sum8(data: bytes) -> bytes:
    return bytes([sum(data) & 0xFF])


def checksum_xor(data: bytes) -> bytes:
    value = 0
    for b in data:
        value ^= b
    return bytes([value & 0xFF])


def crc16_modbus(data: bytes) -> bytes:
    """Modbus CRC-16，返回小端字节序（低字节在前）。"""
    crc = 0xFFFF
    for b in data:
        crc ^= b
        for _ in range(8):
            if crc & 1:
                crc = (crc >> 1) ^ 0xA001
            else:
                crc >>= 1
    return bytes([crc & 0xFF, (crc >> 8) & 0xFF])


def append_checksum(data: bytes, kind: str) -> bytes:
    """按 ``kind`` 追加校验字节；``无`` 时原样返回。"""
    if kind == "SUM8":
        return data + checksum_sum8(data)
    if kind == "XOR":
        return data + checksum_xor(data)
    if kind == "CRC16-Modbus":
        return data + crc16_modbus(data)
    return data


# --------------------------------------------------------------------------- #
# 数值提取（供波形显示使用）
# --------------------------------------------------------------------------- #

_NUMBER = re.compile(r"[-+]?(?:\d+\.\d*|\.\d+|\d+)(?:[eE][-+]?\d+)?")


def extract_numbers(text: str) -> List[float]:
    """从一行文本中提取所有数字，按出现顺序返回。"""
    values: List[float] = []
    for m in _NUMBER.finditer(text):
        try:
            values.append(float(m.group()))
        except ValueError:  # pragma: no cover - 正则已保证可转换
            continue
    return values


def numbers_to_channels(rows: Iterable[Sequence[float]], max_channels: int = 8) -> List[List[float]]:
    """把多行数值转成按通道分组的序列（不足的通道补 ``None``）。"""
    channels: List[List[float]] = [[] for _ in range(max_channels)]
    for row in rows:
        for idx in range(max_channels):
            channels[idx].append(row[idx] if idx < len(row) else None)
    return channels
