"""配置持久化：保存在用户目录下的 JSON 文件，跨平台可用。"""

from __future__ import annotations

import json
import os
from pathlib import Path
from typing import Any, Dict

APP_DIR_NAME = ".com_test_tool"
CONFIG_FILE_NAME = "config.json"

DEFAULTS: Dict[str, Any] = {
    "port": "",
    "baudrate": "115200",
    "bytesize": "8",
    "parity": "无 (None)",
    "stopbits": "1",
    "flow": "无 (None)",
    "display_mode": "文本",
    "show_timestamp": True,
    "auto_scroll": True,
    "send_mode": "文本",
    "send_newline": "\\r\\n",
    "send_checksum": "无",
    "send_encoding": "utf-8",
    "recv_encoding": "utf-8",
    "loop_send": False,
    "loop_interval_ms": "1000",
    "max_lines": 3000,
    "theme": "dark",
    "geometry": "1280x820",
    "shortcuts": [
        {"name": "查询版本", "data": "AT+VER?", "mode": "文本"},
        {"name": "读寄存器(示例)", "data": "01 03 00 00 00 02", "mode": "HEX"},
    ],
}


def config_dir() -> Path:
    """返回配置目录（遵循 ``COM_TOOL_HOME`` 环境变量覆盖）。"""
    override = os.environ.get("COM_TOOL_HOME")
    base = Path(override) if override else Path.home() / APP_DIR_NAME
    return base


def config_path() -> Path:
    return config_dir() / CONFIG_FILE_NAME


def log_dir() -> Path:
    path = config_dir() / "logs"
    path.mkdir(parents=True, exist_ok=True)
    return path


def _merge(defaults: Dict[str, Any], loaded: Dict[str, Any]) -> Dict[str, Any]:
    result = dict(defaults)
    for key, value in (loaded or {}).items():
        if key in result and isinstance(result[key], dict) and isinstance(value, dict):
            result[key] = _merge(result[key], value)
        else:
            result[key] = value
    return result


def load_config() -> Dict[str, Any]:
    """读取配置；文件缺失或损坏时回退到默认值。"""
    path = config_path()
    if not path.exists():
        return dict(DEFAULTS)
    try:
        with path.open("r", encoding="utf-8") as fp:
            loaded = json.load(fp)
        if not isinstance(loaded, dict):
            raise ValueError("配置根节点必须是对象")
        return _merge(DEFAULTS, loaded)
    except (OSError, ValueError):
        return dict(DEFAULTS)


def save_config(config: Dict[str, Any]) -> bool:
    """原子写入配置，失败时返回 ``False``（不阻塞界面）。"""
    path = config_path()
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        tmp = path.with_suffix(".json.tmp")
        with tmp.open("w", encoding="utf-8") as fp:
            json.dump(config, fp, ensure_ascii=False, indent=2)
        os.replace(tmp, path)
        return True
    except OSError:
        return False
