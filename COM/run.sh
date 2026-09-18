#!/usr/bin/env sh
# Linux / macOS 启动脚本：自动检查/安装依赖后启动工具
set -e
cd "$(dirname "$0")"

PY=python3
command -v "$PY" >/dev/null 2>&1 || PY=python
command -v "$PY" >/dev/null 2>&1 || { echo "[错误] 未找到 python3，请先安装 Python 3.8+"; exit 1; }

if ! "$PY" -c "import serial" >/dev/null 2>&1; then
    echo "[提示] 正在安装依赖 pyserial ..."
    "$PY" -m pip install -r requirements.txt
fi

exec "$PY" main.py "$@"
