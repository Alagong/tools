"""主窗口：串口参数配置、打开/关闭、收发调度与状态栏。"""

from __future__ import annotations

import queue
import re
import tkinter as tk
from datetime import datetime
from tkinter import messagebox, ttk
from typing import Dict, List, Optional, Tuple

from .. import APP_NAME, __version__
from ..core import config as cfg
from ..core.serial_engine import (
    BAUDRATES,
    BYTESIZES,
    FLOWS,
    INSTALL_HINT,
    PARITIES,
    SERIAL_AVAILABLE,
    STOPBITS,
    VIRTUAL_LOOP_LABEL,
    VIRTUAL_LOOP_URL,
    SerialEngine,
    SerialError,
    list_serial_ports,
)
from . import theme
from .receive_panel import ReceivePanel
from .send_panel import SendPanel
from .widgets import StatusLamp, ToolTip

PUMP_INTERVAL_MS = 25
LINE_POLL_MS = 500
MAX_EVENTS_PER_PUMP = 200

PARITY_CODES = {
    "无 (None)": "N",
    "偶 (Even)": "E",
    "奇 (Odd)": "O",
    "标记 (Mark)": "M",
    "空格 (Space)": "S",
}


def format_bytes(count: int) -> str:
    if count < 1024:
        return f"{count} B"
    if count < 1024 * 1024:
        return f"{count / 1024:.1f} KB"
    return f"{count / 1024 / 1024:.2f} MB"


class MainWindow:
    """应用主窗口。"""

    def __init__(self, root: tk.Tk) -> None:
        self.root = root
        self.config: Dict[str, object] = cfg.load_config()
        self.engine = SerialEngine(
            on_rx=lambda data: self._post("rx", data),
            on_state=lambda opened, msg: self._post("state", opened, msg),
            on_error=lambda msg: self._post("error", msg),
        )
        self.events: "queue.Queue[Tuple]" = queue.Queue()
        self._pump_job: Optional[str] = None
        self._line_job: Optional[str] = None
        self._port_map: Dict[str, str] = {}
        self._locked: List[Tuple[tk.Widget, str]] = []
        self._connected = False
        self.theme_var = tk.StringVar(value=theme.current_key())

        self._build()
        self._apply_config(self.config)
        self.root.protocol("WM_DELETE_WINDOW", self.on_close)
        self.root.after(120, self.refresh_ports)
        self._pump_job = self.root.after(PUMP_INTERVAL_MS, self._pump_events)
        self._line_job = self.root.after(LINE_POLL_MS, self._poll_lines)

        if not SERIAL_AVAILABLE:
            self.receive.info(INSTALL_HINT, "error")
            self.status_label.configure(text=INSTALL_HINT)
            self.open_btn.configure(state="disabled")

    # ------------------------------------------------------------------ #
    # 界面构建
    # ------------------------------------------------------------------ #
    def _build(self) -> None:
        root = self.root
        self._build_menu()
        self._build_toolbar()
        self._build_statusbar()
        self._build_body()
        root.title(f"{APP_NAME} v{__version__} — 未连接")
        root.minsize(1040, 680)

    def _new_menu(self, parent: tk.Misc) -> tk.Menu:
        p = theme.current()
        return tk.Menu(parent, tearoff=0, bg=p.bg_panel, fg=p.fg,
                       activebackground=p.accent, activeforeground=p.on_accent, bd=0)

    def _build_menu(self) -> None:
        menubar = tk.Menu(self.root)
        file_menu = self._new_menu(menubar)
        file_menu.add_command(label="保存接收日志…", command=lambda: self.receive.save_log())
        file_menu.add_command(label="打开配置目录", command=self.open_config_dir)
        file_menu.add_separator()
        file_menu.add_command(label="退出", command=self.on_close)
        menubar.add_cascade(label="文件", menu=file_menu)

        tools = self._new_menu(menubar)
        tools.add_command(label="清空接收区", accelerator="Ctrl+D", command=self.clear_receive)
        tools.add_command(label="重发上次数据", accelerator="Ctrl+R", command=lambda: self.send.resend())
        tools.add_separator()
        theme_menu = self._new_menu(tools)
        for key, palette in theme.PALETTES.items():
            theme_menu.add_radiobutton(label=palette.label, value=key,
                                       variable=self.theme_var,
                                       command=lambda k=key: self.set_theme(k))
        tools.add_cascade(label="界面主题", menu=theme_menu)
        tools.add_separator()
        tools.add_command(label="重置计数器", command=self.reset_counters)
        tools.add_command(label="恢复默认配置", command=self.reset_config)
        menubar.add_cascade(label="工具", menu=tools)

        help_menu = self._new_menu(menubar)
        help_menu.add_command(label="使用说明", command=self.show_help)
        help_menu.add_command(label="关于", command=self.show_about)
        menubar.add_cascade(label="帮助", menu=help_menu)

        self.root.configure(menu=menubar)
        self.root.bind_all("<Control-d>", lambda _e: self.clear_receive())
        self.root.bind_all("<Control-r>", lambda _e: self.send.resend())

    def _build_toolbar(self) -> None:
        bar = ttk.Frame(self.root, style="Bar.TFrame")
        bar.pack(side="top", fill="x")

        row1 = ttk.Frame(bar, style="Bar.TFrame")
        row1.pack(side="top", fill="x", padx=10, pady=(8, 4))
        ttk.Label(row1, text="串口", style="Panel.TLabel").pack(side="left", padx=(0, 6))
        self.port_var = tk.StringVar()
        self.port_box = ttk.Combobox(row1, textvariable=self.port_var, width=34)
        self.port_box.pack(side="left")
        ToolTip(self.port_box, "可直接输入端口，如 COM7、/dev/ttyUSB0、socket://192.168.1.10:8080")

        self.refresh_btn = ttk.Button(row1, text="刷新", style="Toolbar.TButton",
                                      command=self.refresh_ports)
        self.refresh_btn.pack(side="left", padx=6)

        self.open_btn = ttk.Button(row1, text="打开串口", style="Accent.TButton",
                                   command=self.toggle_connection)
        self.open_btn.pack(side="left", padx=(6, 12))

        self.lamp = StatusLamp(row1)
        self.lamp.pack(side="right")

        row2 = ttk.Frame(bar, style="Bar.TFrame")
        row2.pack(side="top", fill="x", padx=10, pady=(0, 8))

        self.baud_var = tk.StringVar()
        self.bytesize_var = tk.StringVar()
        self.parity_var = tk.StringVar()
        self.stopbits_var = tk.StringVar()
        self.flow_var = tk.StringVar()

        def combo(label: str, var: tk.StringVar, values, width: int, readonly: bool,
                  tip: str = "") -> ttk.Combobox:
            ttk.Label(row2, text=label, style="Panel.TLabel").pack(side="left", padx=(0, 4))
            box = ttk.Combobox(row2, textvariable=var, values=list(values), width=width,
                               state="readonly" if readonly else "normal")
            box.pack(side="left", padx=(0, 12))
            if tip:
                ToolTip(box, tip)
            self._locked.append((box, "readonly" if readonly else "normal"))
            return box

        combo("波特率", self.baud_var, BAUDRATES, 9, False, "可手动输入任意波特率")
        combo("数据位", self.bytesize_var, tuple(BYTESIZES.keys()), 4, True)
        combo("校验位", self.parity_var, tuple(PARITIES.keys()), 12, True)
        combo("停止位", self.stopbits_var, tuple(STOPBITS.keys()), 4, True)
        combo("流控", self.flow_var, FLOWS, 12, True, "硬件/软件流控")
        self._locked.append((self.port_box, "normal"))

        ttk.Separator(bar, orient="horizontal").pack(side="top", fill="x")

    def _build_body(self) -> None:
        paned = ttk.PanedWindow(self.root, orient="horizontal")
        paned.pack(side="top", fill="both", expand=True, padx=8, pady=8)

        left = ttk.Frame(paned, style="Panel.TFrame")
        left.columnconfigure(0, weight=1)
        left.rowconfigure(0, weight=1)
        self.receive = ReceivePanel(left, max_lines=int(self.config.get("max_lines", 3000) or 3000))
        self.receive.grid(row=0, column=0, sticky="nsew")
        self.receive.notebook.bind("<<NotebookTabChanged>>", lambda _e: self.receive.on_tab_changed(
            self.receive.notebook.index("current")))
        paned.add(left, weight=3)

        right = ttk.Frame(paned, style="Panel.TFrame")
        right.columnconfigure(0, weight=1)
        right.rowconfigure(0, weight=1)
        self.send = SendPanel(right, send_fn=self.on_send,
                              shortcuts=self.config.get("shortcuts"))
        self.send.grid(row=0, column=0, sticky="nsew")

        self._build_lines_panel(right)
        paned.add(right, weight=2)

    def _build_lines_panel(self, parent: tk.Misc) -> None:
        box = ttk.Labelframe(parent, text=" 控制线 ", padding=(8, 6))
        box.grid(row=1, column=0, sticky="ew", padx=8, pady=(0, 8))

        self.dtr_var = tk.BooleanVar(value=True)
        self.rts_var = tk.BooleanVar(value=True)
        ttk.Checkbutton(box, text="DTR", variable=self.dtr_var, style="Bar.TCheckbutton",
                        command=lambda: self.engine.set_dtr(self.dtr_var.get())).grid(
            row=0, column=0, sticky="w", padx=(0, 10))
        ttk.Checkbutton(box, text="RTS", variable=self.rts_var, style="Bar.TCheckbutton",
                        command=lambda: self.engine.set_rts(self.rts_var.get())).grid(
            row=0, column=1, sticky="w", padx=(0, 10))
        self.line_labels: Dict[str, ttk.Label] = {}
        for idx, (key, name) in enumerate((("cts", "CTS"), ("dsr", "DSR"), ("ri", "RI"), ("cd", "CD"))):
            ttk.Label(box, text=f"{name}", style="Dim.TLabel").grid(
                row=0, column=2 + idx * 2, sticky="e", padx=(8, 2))
            label = ttk.Label(box, text="-", style="Panel.TLabel", width=3)
            label.grid(row=0, column=3 + idx * 2, sticky="w")
            self.line_labels[key] = label

    def _build_statusbar(self) -> None:
        bar = ttk.Frame(self.root, style="Bar.TFrame")
        bar.pack(side="bottom", fill="x")
        ttk.Separator(self.root, orient="horizontal").pack(side="bottom", fill="x")

        self.status_label = ttk.Label(bar, text="就绪", style="Status.TLabel")
        self.status_label.pack(side="left", padx=10, pady=4)

        self.counter_label = ttk.Label(bar, text="RX 0 B / TX 0 B", style="Status.TLabel")
        self.counter_label.pack(side="right", padx=10)
        ttk.Button(bar, text="清零", style="Small.TButton",
                   command=self.reset_counters).pack(side="right", padx=(0, 6), pady=2)
        self.time_label = ttk.Label(bar, text="", style="Status.TLabel")
        self.time_label.pack(side="right", padx=10)

    # ------------------------------------------------------------------ #
    # 配置
    # ------------------------------------------------------------------ #
    def _apply_config(self, data: Dict[str, object]) -> None:
        self.baud_var.set(str(data.get("baudrate", "115200")))
        self.bytesize_var.set(str(data.get("bytesize", "8")))
        self.parity_var.set(str(data.get("parity", "无 (None)")))
        self.stopbits_var.set(str(data.get("stopbits", "1")))
        self.flow_var.set(str(data.get("flow", "无 (None)")))
        match = re.match(r"(\d+)x(\d+)", str(data.get("geometry", "1280x820")))
        if match:
            # 保证启动尺寸足够容纳两行控制栏（高 DPI 下控件按键更宽）
            width = max(int(match.group(1)), 1180)
            height = max(int(match.group(2)), 760)
            self.root.geometry(f"{width}x{height}")

        self.set_theme(str(data.get("theme", theme.DEFAULT_KEY)))

        recv_cfg = {k: data.get(k) for k in
                    ("display_mode", "recv_encoding", "show_timestamp", "auto_scroll", "show_echo", "sample_points")}
        self.receive.apply_config(recv_cfg)
        send_cfg = {k: data.get(k) for k in
                    ("send_mode", "send_encoding", "send_newline", "send_checksum",
                     "loop_interval_ms", "loop_count", "shortcuts")}
        self.send.apply_config(send_cfg)

    def _collect_config(self) -> Dict[str, object]:
        data = dict(self.config)
        data.update({
            "port": self.port_box.get().strip(),
            "baudrate": self.baud_var.get().strip(),
            "bytesize": self.bytesize_var.get(),
            "parity": self.parity_var.get(),
            "stopbits": self.stopbits_var.get(),
            "flow": self.flow_var.get(),
            "geometry": self.root.geometry(),
            "theme": theme.current_key(),
        })
        data.update(self.receive.get_config())
        data.update(self.send.get_config())
        return data

    def open_config_dir(self) -> None:
        path = cfg.config_dir()
        path.mkdir(parents=True, exist_ok=True)
        import os
        import subprocess
        import sys
        try:
            if hasattr(os, "startfile"):  # Windows
                os.startfile(str(path))  # type: ignore[attr-defined]
            elif sys.platform == "darwin":
                subprocess.Popen(["open", str(path)])
            elif sys.platform.startswith("linux"):
                subprocess.Popen(["xdg-open", str(path)])
            else:
                messagebox.showinfo("配置目录", str(path), parent=self.root)
        except Exception:  # pragma: no cover
            messagebox.showinfo("配置目录", str(path), parent=self.root)

    def reset_config(self) -> None:
        if not messagebox.askyesno("恢复默认配置", "将恢复默认参数并清空快捷指令，继续？",
                                   parent=self.root):
            return
        self.config = dict(cfg.DEFAULTS)
        self._apply_config(self.config)
        self.receive.info("已恢复默认配置")

    # ------------------------------------------------------------------ #
    # 端口与连接
    # ------------------------------------------------------------------ #
    def refresh_ports(self) -> None:
        ports = list_serial_ports()
        values = [p.label for p in ports]
        self._port_map = {p.label: p.device for p in ports}
        values.append(VIRTUAL_LOOP_LABEL)
        self._port_map[VIRTUAL_LOOP_LABEL] = VIRTUAL_LOOP_URL
        self.port_box.configure(values=values)

        current = self.port_box.get().strip()
        if not current:
            saved = str(self.config.get("port", "")).strip()
            match = next((label for label, dev in self._port_map.items() if dev == saved), None)
            self.port_var.set(match or (values[0] if ports else VIRTUAL_LOOP_LABEL))

        if ports:
            self.status_label.configure(text=f"发现 {len(ports)} 个串口")
        else:
            self.status_label.configure(text="未发现物理串口，可用「虚拟回环」自测收发")
        if not self._connected:
            self.lamp.set_state("未连接", "idle")

    def _current_port(self) -> str:
        raw = self.port_box.get().strip()
        if raw in self._port_map:
            return self._port_map[raw]
        if "—" in raw:
            return raw.split("—")[0].strip()
        return raw

    def _read_port_options(self) -> Tuple[str, int, str, str, str, str]:
        port = self._current_port()
        try:
            baud = int(float(self.baud_var.get()))
            if baud <= 0:
                raise ValueError
        except ValueError:
            raise SerialError(f"波特率无效：{self.baud_var.get()!r}")
        return (port, baud, self.bytesize_var.get(), self.parity_var.get(),
                self.stopbits_var.get(), self.flow_var.get())

    def toggle_connection(self) -> None:
        if self.engine.is_open:
            self.engine.close()
            return
        try:
            port, baud, bytesize, parity, stopbits, flow = self._read_port_options()
        except SerialError as exc:
            messagebox.showwarning("打开串口", str(exc), parent=self.root)
            return
        if not port:
            messagebox.showwarning("打开串口", "请先选择或输入串口。", parent=self.root)
            return
        try:
            self.engine.open(port, baud, bytesize, parity, stopbits, flow,
                             dtr=self.dtr_var.get(), rts=self.rts_var.get())
        except SerialError as exc:
            self.lamp.set_state("打开失败", "err")
            self.receive.info(str(exc), "error")
            messagebox.showerror("打开串口", str(exc), parent=self.root)
            return
        frame = f"{bytesize}{PARITY_CODES.get(parity, '?')}{stopbits}"
        self.receive.info(f"已打开 {port} @ {baud}，{frame}，流控：{flow}")

    def _set_connected(self, connected: bool) -> None:
        self._connected = connected
        for widget, origin in self._locked:
            try:
                widget.configure(state="disabled" if connected else origin)
            except tk.TclError:  # pragma: no cover
                pass
        self.refresh_btn.configure(state="disabled" if connected else "normal")
        self.open_btn.configure(text="关闭串口" if connected else "打开串口",
                                style="Danger.TButton" if connected else "Accent.TButton")
        self.send.set_connected(connected)
        if connected:
            self.lamp.set_state("已连接", "ok")
            self.root.title(f"{APP_NAME} v{__version__} — {self.engine.port_name}")
        else:
            self.lamp.set_state("未连接", "idle")
            self.root.title(f"{APP_NAME} v{__version__} — 未连接")
            for label in self.line_labels.values():
                label.configure(text="-")

    # ------------------------------------------------------------------ #
    # 收发
    # ------------------------------------------------------------------ #
    def on_send(self, payload: bytes, mode: str, encoding: str) -> None:
        try:
            self.engine.write(payload)
        except SerialError as exc:
            self.receive.info(str(exc), "error")
            self.status_label.configure(text=str(exc))
            messagebox.showwarning("发送失败", str(exc), parent=self.root)
            return
        self.receive.feed_echo(payload, mode, encoding)
        self._update_counters()

    def _post(self, kind: str, *args) -> None:
        """线程安全地把事件投递到界面队列。"""
        self.events.put((kind, args))

    def _pump_events(self) -> None:
        processed = 0
        while processed < MAX_EVENTS_PER_PUMP:
            try:
                kind, args = self.events.get_nowait()
            except queue.Empty:
                break
            processed += 1
            if kind == "rx":
                self.receive.feed(args[0])
            elif kind == "state":
                opened, message = args
                self._set_connected(bool(opened))
                if message:
                    self.status_label.configure(text=message)
            elif kind == "error":
                self.receive.info(str(args[0]), "error")
                self.status_label.configure(text=str(args[0]))

        if processed:
            self._update_counters()
        if self.events.qsize() > MAX_EVENTS_PER_PUMP * 4:
            self.receive.info("接收速度超出界面刷新能力，已丢弃部分显示数据", "warn")
            pending: List[Tuple] = []
            while not self.events.empty():
                try:
                    item = self.events.get_nowait()
                except queue.Empty:  # pragma: no cover
                    break
                if item[0] != "rx":  # 状态/错误事件必须保留
                    pending.append(item)
            for item in pending:
                self.events.put(item)
        self._pump_job = self.root.after(PUMP_INTERVAL_MS, self._pump_events)

    def _update_counters(self) -> None:
        self.counter_label.configure(
            text=f"RX {format_bytes(self.engine.rx_bytes)} / TX {format_bytes(self.engine.tx_bytes)}")
        self.time_label.configure(text=f"{datetime.now():%H:%M:%S}")

    def _poll_lines(self) -> None:
        if self.engine.is_open:
            lines = self.engine.read_control_lines()
            for key, label in self.line_labels.items():
                value = lines.get(key)
                label.configure(text="-" if value is None else ("1" if value else "0"),
                                foreground=theme.FG if value else theme.FG_DIM)
        self._line_job = self.root.after(LINE_POLL_MS, self._poll_lines)

    # ------------------------------------------------------------------ #
    # 菜单动作
    # ------------------------------------------------------------------ #
    def set_theme(self, key: str) -> None:
        """切换界面主题并即时刷新（不影响串口连接与接收内容）。"""
        if key == theme.current_key() or key not in theme.PALETTES:
            return
        theme.set_palette(key)
        self.theme_var.set(theme.current_key())
        theme.refresh(self.root)
        self._build_menu()  # 菜单不属于控件树，需要重建才能跟随配色
        self.receive.info(f"已切换主题：{theme.current().label}")

    def clear_receive(self) -> None:
        self.receive.clear()
        self.receive.info("接收区已清空")

    def reset_counters(self) -> None:
        self.engine.reset_counters()
        self.receive.total_rx = 0
        self.send.total_tx = 0
        self._update_counters()

    def show_help(self) -> None:
        messagebox.showinfo(
            "使用说明",
            "1. 选择串口（或「虚拟回环」自测），设置波特率等参数后点「打开串口」。\n"
            "2. 下方输入内容，Ctrl+Enter 发送；HEX 模式下输入 AA BB 0x0D 这类文本。\n"
            "3. 「定时」可循环发送，次数填 0 表示不限。\n"
            "4. 接收区支持文本/HEX/HEX Dump 切换与时间戳，「波形」页会把收到的数字画成曲线。\n"
            "5. 快捷指令支持双击发送，内容来自输入框，配置会保存在用户目录。\n"
            "6. 界面主题在「工具 → 界面主题」切换（暗色 / 浅色 / 绚丽霓虹），即时生效并记住选择。\n"
            "快捷键：Ctrl+Enter 发送、Ctrl+D 清空接收、Ctrl+R 重发、Ctrl+L 清空输入。",
            parent=self.root)

    def show_about(self) -> None:
        messagebox.showinfo(
            "关于",
            f"{APP_NAME} v{__version__}\n\n"
            "跨平台串口(COM)通信测试工具\n"
            f"运行环境：Python + tkinter + pyserial\n"
            f"配置文件：{cfg.config_path()}",
            parent=self.root)

    # ------------------------------------------------------------------ #
    # 关闭
    # ------------------------------------------------------------------ #
    def on_close(self) -> None:
        try:
            cfg.save_config(self._collect_config())
        except Exception:
            pass
        self.send.stop_loop()
        self.engine.close()
        for job in (self._pump_job, self._line_job):
            if job is not None:
                try:
                    self.root.after_cancel(job)
                except tk.TclError:  # pragma: no cover
                    pass
        self.root.destroy()
