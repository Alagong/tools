"""接收面板：数据视图（文本 / HEX / HEX Dump）+ 实时波形视图。"""

from __future__ import annotations

import tkinter as tk
from datetime import datetime
from tkinter import filedialog, messagebox, ttk
from typing import Dict, List, Optional

from ..core import config as cfg
from ..core.data_utils import decode_bytes, format_hex, hex_dump, extract_numbers
from . import theme
from .widgets import ScrolledText, ToolTip

DISPLAY_MODES = ("文本", "HEX", "HEX Dump")
ENCODINGS = ("utf-8", "gbk", "ascii", "latin-1")
SAMPLE_CHOICES = ("200", "600", "2000", "5000")


class WaveCanvas(tk.Canvas):
    """从接收数据中提取数值并实时绘制多通道曲线。"""

    MAX_CHANNELS = 8

    def __init__(self, master: tk.Misc, **kwargs) -> None:
        super().__init__(master, bg=theme.BG_INPUT, highlightthickness=1,
                         highlightbackground=theme.BORDER, bd=0, **kwargs)
        self.max_points = 600
        self.auto_scale = True
        self.rows: List[List[float]] = []
        self.channel_names: List[str] = []
        self.bind("<Configure>", lambda _e: self.redraw())

    # ------------------------------------------------------------------ #
    def feed_text(self, text: str) -> None:
        """解析文本中的数值（一行多个数字视为多通道）。"""
        for line in text.splitlines():
            values = extract_numbers(line)
            if values:
                self.rows.append(values[: self.MAX_CHANNELS])
        if len(self.rows) > self.max_points:
            del self.rows[: len(self.rows) - self.max_points]

    def on_theme_changed(self) -> None:
        self.configure(bg=theme.BG_INPUT, highlightbackground=theme.BORDER)
        self.redraw()

    def clear(self) -> None:
        self.rows.clear()
        self.redraw()

    def set_max_points(self, points: int) -> None:
        self.max_points = max(10, points)
        if len(self.rows) > self.max_points:
            del self.rows[: len(self.rows) - self.max_points]
        self.redraw()

    # ------------------------------------------------------------------ #
    def redraw(self) -> None:
        self.delete("all")
        width = self.winfo_width()
        height = self.winfo_height()
        if width < 60 or height < 60:
            return

        left, top, right, bottom = 58, 24, 10, 22
        plot_w = width - left - right
        plot_h = height - top - bottom
        if plot_w <= 10 or plot_h <= 10:
            return

        rows = self.rows
        if not rows:
            self.create_text(width / 2, height / 2,
                             text="等待数据…\n收到的数值将在此绘制曲线（每行多个数字 = 多通道）",
                             fill=theme.FG_DIM, font=theme.ui_font(self, 10), justify="center")
            return

        channels = max(len(r) for r in rows)
        channels = min(channels, self.MAX_CHANNELS)
        series = [[r[i] for r in rows if i < len(r)] for i in range(channels)]

        values = [v for ch in series for v in ch]
        if not values:
            return
        vmin, vmax = min(values), max(values)
        if not self.auto_scale:
            return
        if vmin == vmax:
            pad = abs(vmin) * 0.1 or 1.0
            vmin, vmax = vmin - pad, vmax + pad
        else:
            pad = (vmax - vmin) * 0.08
            vmin, vmax = vmin - pad, vmax + pad

        # 网格与 Y 轴刻度
        for i in range(5):
            y = top + plot_h * i / 4
            self.create_line(left, y, left + plot_w, y, fill=theme.GRID)
            value = vmax - (vmax - vmin) * i / 4
            self.create_text(left - 6, y, text=f"{value:g}", anchor="e",
                             fill=theme.FG_DIM, font=theme.mono_font(self, 8))
        for i in range(5):
            x = left + plot_w * i / 4
            self.create_line(x, top, x, top + plot_h, fill=theme.GRID_MINOR)

        self.create_line(left, top, left, top + plot_h, fill=theme.BORDER)
        self.create_line(left, top + plot_h, left + plot_w, top + plot_h, fill=theme.BORDER)

        points = len(rows)
        for idx in range(channels):
            ch = series[idx]
            if len(ch) < 1:
                continue
            color = theme.WAVE_COLORS[idx % len(theme.WAVE_COLORS)]
            coords: List[float] = []

            # 按整行索引对齐 X 轴，缺失点断开
            for r_index, row in enumerate(rows):
                if idx >= len(row):
                    if coords:
                        self.create_line(*coords, fill=color, width=1.6, smooth=False)
                        coords = []
                    continue
                x = left + (plot_w * r_index / max(points - 1, 1))
                y = top + plot_h * (vmax - row[idx]) / (vmax - vmin)
                coords.extend((x, y))
            if coords:
                self.create_line(*coords, fill=color, width=1.6, smooth=False)

        # 图例
        lx, ly = left + 8, top + 6
        for idx in range(channels):
            ch = series[idx]
            if not ch:
                continue
            color = theme.WAVE_COLORS[idx % len(theme.WAVE_COLORS)]
            self.create_rectangle(lx, ly - 5, lx + 10, ly + 5, fill=color, outline="")
            self.create_text(lx + 16, ly, anchor="w",
                             text=f"CH{idx + 1}={ch[-1]:g}",
                             fill=theme.FG, font=theme.mono_font(self, 8))
            lx += 92


class ReceivePanel(ttk.Frame):
    """接收区：控制栏 + 数据/波形分页。"""

    def __init__(self, master: tk.Misc, *, max_lines: int = 3000) -> None:
        super().__init__(master, style="Panel.TFrame")
        self.paused = False
        self.auto_scale = tk.BooleanVar(value=True)
        self.show_echo = tk.BooleanVar(value=True)
        self.show_timestamp = tk.BooleanVar(value=True)
        self.auto_scroll = tk.BooleanVar(value=True)
        self.display_mode = tk.StringVar(value="文本")
        self.recv_encoding = tk.StringVar(value="utf-8")
        self.sample_points = tk.StringVar(value="600")
        self.total_rx = 0
        self._dump_offset = 0
        self._at_line_start = True
        self._redraw_job: Optional[str] = None
        self._wave_dirty = False
        # HEX 模式下把短时间内的分片合并成一行显示
        self._pending = bytearray()
        self._pending_time: Optional[datetime] = None
        self._flush_job: Optional[str] = None

        self._build(max_lines)
        self.display_mode.trace_add("write", lambda *_: self._on_mode_changed())

    # ------------------------------------------------------------------ #
    # 构建
    # ------------------------------------------------------------------ #
    def _build(self, max_lines: int) -> None:
        self.columnconfigure(0, weight=1)
        self.rowconfigure(1, weight=1)

        bar = ttk.Frame(self, style="Panel.TFrame")
        bar.grid(row=0, column=0, sticky="ew", padx=8, pady=(8, 4))

        row1 = ttk.Frame(bar, style="Panel.TFrame")
        row1.pack(side="top", fill="x")
        row2 = ttk.Frame(bar, style="Panel.TFrame")
        row2.pack(side="top", fill="x", pady=(6, 0))

        # 按钮先布局：面板变窄时优先压缩左侧选择框，而不是把按钮挤没
        ttk.Button(row1, text="清空", style="Small.TButton",
                   command=self.clear).pack(side="right", padx=(4, 0))
        ttk.Button(row1, text="保存日志", style="Small.TButton",
                   command=self.save_log).pack(side="right", padx=(4, 0))
        self.pause_btn = ttk.Button(row1, text="暂停", style="Small.TButton",
                                    command=self.toggle_pause)
        self.pause_btn.pack(side="right", padx=(4, 0))

        ttk.Label(row1, text="显示", style="Panel.TLabel").pack(side="left", padx=(0, 4))
        mode_box = ttk.Combobox(row1, textvariable=self.display_mode, values=DISPLAY_MODES,
                                width=9, state="readonly")
        mode_box.pack(side="left")
        ToolTip(mode_box, "文本：按编码解析可读字符\nHEX：字节以十六进制显示\nHEX Dump：带偏移与 ASCII 列")

        self.encoding_box = ttk.Combobox(row2, textvariable=self.recv_encoding,
                                         values=ENCODINGS, width=7, state="readonly")
        self.encoding_box.pack(side="left", padx=(0, 14))
        ToolTip(self.encoding_box, "文本模式的解码字符集（HEX 模式无效）")

        ttk.Checkbutton(row2, text="时间戳", variable=self.show_timestamp,
                        style="Bar.TCheckbutton").pack(side="left", padx=(0, 14))
        ttk.Checkbutton(row2, text="自动滚动", variable=self.auto_scroll,
                        style="Bar.TCheckbutton",
                        command=self._on_auto_scroll).pack(side="left", padx=(0, 14))
        ttk.Checkbutton(row2, text="显示发送", variable=self.show_echo,
                        style="Bar.TCheckbutton").pack(side="left")

        self.notebook = ttk.Notebook(self)
        self.notebook.grid(row=1, column=0, sticky="nsew", padx=8, pady=(0, 8))

        # --- 数据页 ---
        data_page = ttk.Frame(self.notebook, style="Panel.TFrame")
        data_page.columnconfigure(0, weight=1)
        data_page.rowconfigure(0, weight=1)
        self.output = ScrolledText(data_page, font=theme.mono_font(self, 10),
                                   max_lines=max_lines, wrap="char")
        self.output.grid(row=0, column=0, sticky="nsew")
        self._apply_tag_colors()
        self.notebook.add(data_page, text="  数据  ")

        # --- 波形页 ---
        wave_page = ttk.Frame(self.notebook, style="Panel.TFrame")
        wave_page.columnconfigure(0, weight=1)
        wave_page.rowconfigure(1, weight=1)

        wave_bar = ttk.Frame(wave_page, style="Panel.TFrame")
        wave_bar.grid(row=0, column=0, sticky="ew", padx=4, pady=(6, 2))
        ttk.Button(wave_bar, text="清空曲线", style="Small.TButton",
                   command=lambda: self.wave.clear()).pack(side="right")
        ttk.Label(wave_bar, text="采样点数", style="Panel.TLabel").pack(side="left", padx=(0, 4))
        pts = ttk.Combobox(wave_bar, textvariable=self.sample_points, values=SAMPLE_CHOICES,
                           width=6, state="readonly")
        pts.pack(side="left", padx=(0, 10))
        pts.bind("<<ComboboxSelected>>",
                 lambda _e: self.wave.set_max_points(int(self.sample_points.get())))
        ttk.Checkbutton(wave_bar, text="自动缩放", variable=self.auto_scale,
                        style="Bar.TCheckbutton",
                        command=self._on_auto_scale).pack(side="left")
        ttk.Label(wave_bar, text="每个数字一个值，一行多个数字 = 多通道",
                  style="Dim.TLabel").pack(side="left", padx=10)

        self.wave = WaveCanvas(wave_page)
        self.wave.grid(row=1, column=0, sticky="nsew", padx=4, pady=(0, 6))
        self.wave.set_max_points(int(self.sample_points.get()))
        self.wave.auto_scale = self.auto_scale.get()
        self.notebook.add(wave_page, text="  波形  ")

    # ------------------------------------------------------------------ #
    # 主题
    # ------------------------------------------------------------------ #
    def _apply_tag_colors(self) -> None:
        text = self.output.text
        text.tag_configure("ts", foreground=theme.TS_COLOR)
        text.tag_configure("rx", foreground=theme.RX_COLOR)
        text.tag_configure("tx", foreground=theme.TX_COLOR)
        text.tag_configure("info", foreground=theme.FG_DIM)
        text.tag_configure("error", foreground=theme.ERR)
        text.tag_configure("warn", foreground=theme.WARN)

    def on_theme_changed(self) -> None:
        self._apply_tag_colors()
        self.wave.redraw()

    # ------------------------------------------------------------------ #
    # 交互
    # ------------------------------------------------------------------ #
    def _on_auto_scroll(self) -> None:
        self.output.auto_scroll = self.auto_scroll.get()
        if self.output.auto_scroll:
            self.output.text.see("end")

    def _on_mode_changed(self) -> None:
        if self._flush_job is not None:
            try:
                self.after_cancel(self._flush_job)
            except tk.TclError:  # pragma: no cover
                pass
            self._flush_job = None
            self._flush_pending()
        self.encoding_box.configure(state="readonly" if self.display_mode.get() == "文本" else "disabled")

    def _on_auto_scale(self) -> None:
        self.wave.auto_scale = self.auto_scale.get()
        self.wave.redraw()

    def toggle_pause(self) -> None:
        self.paused = not self.paused
        self.pause_btn.configure(text="继续" if self.paused else "暂停")

    def clear(self) -> None:
        self.output.clear()
        self.wave.clear()
        self._dump_offset = 0
        self._at_line_start = True
        del self._pending[:]
        self._pending_time = None
        if self._flush_job is not None:
            try:
                self.after_cancel(self._flush_job)
            except tk.TclError:  # pragma: no cover
                pass
            self._flush_job = None

    # ------------------------------------------------------------------ #
    # 数据入口（需在主线程调用）
    # ------------------------------------------------------------------ #
    def feed(self, data: bytes) -> None:
        """接收数据（原始字节）。"""
        if not data:
            return
        self.total_rx += len(data)
        mode = self.display_mode.get()
        if mode in ("HEX", "HEX Dump"):
            # 串口分片很常见，先缓存再合并刷新，避免同一帧被拆成多行
            self._pending.extend(data)
            if self._pending_time is None:
                self._pending_time = datetime.now()
            if self._flush_job is None:
                self._flush_job = self.after(40, self._flush_pending)
        else:
            text = decode_bytes(data, self.recv_encoding.get())
            self._emit(text, "rx")
        self.wave.feed_text(decode_bytes(data, "latin-1"))
        self._wave_dirty = True

    def _flush_pending(self) -> None:
        self._flush_job = None
        if not self._pending:
            self._pending_time = None
            return
        data = bytes(self._pending)
        del self._pending[:]
        when, self._pending_time = self._pending_time or datetime.now(), None
        mode = self.display_mode.get()
        if mode == "HEX":
            self._emit(f"{format_hex(data)}\n", "rx", force_stamp=True, when=when)
        elif mode == "HEX Dump":
            dump = hex_dump(data, self._dump_offset, 16)
            self._dump_offset += len(data)
            for line in dump.splitlines():
                self._emit(line, "rx", force_stamp=True, when=when)

    def feed_echo(self, data: bytes, mode: str, encoding: str = "utf-8") -> None:
        """显示本机发送的内容（回声）。"""
        if not data or not self.show_echo.get():
            return
        if mode == "HEX":
            body = format_hex(data)
        else:
            body = decode_bytes(data, encoding)
        self._emit(f"[TX] {body}\n", "tx", force_stamp=True)

    def info(self, message: str, tag: str = "info") -> None:
        """输出工具自身的提示信息。"""
        self._emit(f"{message}\n", tag, force_stamp=True)

    def _emit(self, text: str, tag: str, force_stamp: bool = False,
              when: Optional[datetime] = None) -> None:
        if self.paused or not text:
            return
        if self.show_timestamp.get():
            text = self._stamp(text, force_stamp, when)
        self.output.auto_scroll = self.auto_scroll.get()
        self.output.append(text, (tag,))
        self._schedule_wave_redraw()

    def _stamp(self, text: str, force: bool, when: Optional[datetime] = None) -> str:
        now = when or datetime.now()
        stamp = f"[{now:%H:%M:%S}.{now.microsecond // 1000:03d}] "
        if force:
            self._at_line_start = True
            return stamp + text
        out: List[str] = []
        cursor = 0
        at_start = self._at_line_start
        while True:
            if at_start:
                out.append(stamp)
            nl = text.find("\n", cursor)
            if nl == -1:
                tail = text[cursor:]
                out.append(tail)
                self._at_line_start = not tail
                break
            out.append(text[cursor:nl + 1])
            cursor = nl + 1
            at_start = True
            if cursor >= len(text):
                self._at_line_start = True
                break
        return "".join(out)

    def _schedule_wave_redraw(self) -> None:
        if self._redraw_job is not None:
            return
        if self.notebook.index("current") != 1:
            return
        self._redraw_job = self.after(150, self._redraw_wave)

    def _redraw_wave(self) -> None:
        self._redraw_job = None
        self.wave.redraw()

    def on_tab_changed(self, index: int) -> None:
        if index == 1:
            self.wave.redraw()

    # ------------------------------------------------------------------ #
    def save_log(self) -> None:
        content = self.output.get_text()
        if not content.strip():
            messagebox.showinfo("保存日志", "当前没有可保存的内容。", parent=self)
            return
        default_name = f"com-log-{datetime.now():%Y%m%d-%H%M%S}.txt"
        path = filedialog.asksaveasfilename(
            parent=self, title="保存接收日志",
            initialdir=str(cfg.log_dir()), initialfile=default_name,
            defaultextension=".txt",
            filetypes=[("文本文件", "*.txt"), ("全部文件", "*.*")])
        if not path:
            return
        try:
            with open(path, "w", encoding="utf-8") as fp:
                fp.write(content)
        except OSError as exc:
            messagebox.showerror("保存日志", f"写入失败：{exc}", parent=self)
            return
        self.info(f"日志已保存：{path}")

    # ------------------------------------------------------------------ #
    def get_config(self) -> Dict[str, object]:
        return {
            "display_mode": self.display_mode.get(),
            "recv_encoding": self.recv_encoding.get(),
            "show_timestamp": bool(self.show_timestamp.get()),
            "auto_scroll": bool(self.auto_scroll.get()),
            "show_echo": bool(self.show_echo.get()),
            "sample_points": self.sample_points.get(),
        }

    def apply_config(self, data: Dict[str, object]) -> None:
        if not data:
            return
        self.display_mode.set(str(data.get("display_mode", "文本")))
        self.recv_encoding.set(str(data.get("recv_encoding", "utf-8")))
        self.show_timestamp.set(bool(data.get("show_timestamp", True)))
        self.auto_scroll.set(bool(data.get("auto_scroll", True)))
        self.output.auto_scroll = self.auto_scroll.get()
        self.show_echo.set(bool(data.get("show_echo", True)))
        self.sample_points.set(str(data.get("sample_points", "600")))
        try:
            self.wave.set_max_points(int(self.sample_points.get()))
        except ValueError:
            self.wave.set_max_points(600)
        self._on_mode_changed()
