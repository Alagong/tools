"""发送面板：文本/HEX 发送、定时循环发送、快捷指令列表。"""

from __future__ import annotations

import tkinter as tk
from tkinter import messagebox, simpledialog, ttk
from typing import Callable, Dict, List, Optional

from ..core.data_utils import CHECKSUM_CHOICES, append_checksum, encode_text, parse_hex
from . import theme
from .widgets import ScrolledText, ToolTip

SEND_MODES = ("文本", "HEX")
ENCODINGS = ("utf-8", "gbk", "ascii", "latin-1")
NEWLINES = ("无", "\\n", "\\r", "\\r\\n")
_NEWLINE_BYTES = {"无": b"", "\\n": b"\n", "\\r": b"\r", "\\r\\n": b"\r\n"}

SendCallback = Callable[[bytes, str, str], None]


class SendPanel(ttk.Frame):
    """发送区 + 快捷指令管理。"""

    def __init__(self, master: tk.Misc, send_fn: SendCallback,
                 shortcuts: Optional[List[Dict[str, str]]] = None) -> None:
        super().__init__(master, style="Panel.TFrame")
        self.send_fn = send_fn
        self.send_mode = tk.StringVar(value="文本")
        self.encoding = tk.StringVar(value="utf-8")
        self.newline = tk.StringVar(value="\\r\\n")
        self.checksum = tk.StringVar(value="无")
        self.loop_send = tk.BooleanVar(value=False)
        self.interval_ms = tk.StringVar(value="1000")
        self.loop_count = tk.StringVar(value="0")
        self._loop_job: Optional[str] = None
        self._sent_in_loop = 0
        self._loop_limit = 0
        self._last_payload: Optional[bytes] = None
        self._shortcuts: List[Dict[str, str]] = list(shortcuts or [])
        self.total_tx = 0
        self._text_newline = "\\r\\n"
        self._suspend_mode_hook = False

        self._build()
        self._reload_shortcuts()
        self.send_mode.trace_add("write", lambda *_: self._on_mode_changed())

    # ------------------------------------------------------------------ #
    # 构建
    # ------------------------------------------------------------------ #
    def _build(self) -> None:
        self.columnconfigure(0, weight=1)
        self.rowconfigure(1, weight=1)
        self.rowconfigure(3, weight=1)

        # ---- 选项栏（两行，窄面板下不被挤压） ----
        opts = ttk.Frame(self, style="Panel.TFrame")
        opts.grid(row=0, column=0, sticky="ew", padx=8, pady=(8, 4))

        opt_row1 = ttk.Frame(opts, style="Panel.TFrame")
        opt_row1.pack(side="top", fill="x")
        opt_row2 = ttk.Frame(opts, style="Panel.TFrame")
        opt_row2.pack(side="top", fill="x", pady=(6, 0))

        ttk.Label(opt_row1, text="模式", style="Panel.TLabel").pack(side="left", padx=(0, 4))
        mode_box = ttk.Combobox(opt_row1, textvariable=self.send_mode, values=SEND_MODES,
                                width=6, state="readonly")
        mode_box.pack(side="left", padx=(0, 10))
        ToolTip(mode_box, "文本：按字符集编码发送\nHEX：输入 AA BB CC 形式")

        self.encoding_box = ttk.Combobox(opt_row1, textvariable=self.encoding,
                                         values=ENCODINGS, width=7, state="readonly")
        self.encoding_box.pack(side="left")

        ttk.Label(opt_row2, text="行尾", style="Panel.TLabel").pack(side="left", padx=(0, 4))
        nl_box = ttk.Combobox(opt_row2, textvariable=self.newline, values=NEWLINES,
                              width=6, state="readonly")
        nl_box.pack(side="left", padx=(0, 10))
        ToolTip(nl_box, "发送内容末尾自动追加的换行符")

        ttk.Label(opt_row2, text="校验", style="Panel.TLabel").pack(side="left", padx=(0, 4))
        ck_box = ttk.Combobox(opt_row2, textvariable=self.checksum, values=CHECKSUM_CHOICES,
                              width=13, state="readonly")
        ck_box.pack(side="left")
        ToolTip(ck_box, "在数据末尾追加校验字节\nSUM8=累加和低8位\nXOR=逐字节异或\nCRC16-Modbus=低字节在前")

        # ---- 输入区 ----
        editor = ttk.Frame(self, style="Panel.TFrame")
        editor.grid(row=1, column=0, sticky="nsew", padx=8, pady=(0, 4))
        editor.columnconfigure(0, weight=1)
        editor.rowconfigure(0, weight=1)
        self.input = ScrolledText(editor, font=theme.mono_font(self, 10), height=6, wrap="word")
        self.input.grid(row=0, column=0, sticky="nsew")
        self.input.text.bind("<Control-Return>", self._on_ctrl_enter)
        self.input.text.bind("<Control-KP_Enter>", self._on_ctrl_enter)
        self.input.text.bind("<Control-l>", self._on_ctrl_l)

        btn_bar = ttk.Frame(self, style="Panel.TFrame")
        btn_bar.grid(row=2, column=0, sticky="ew", padx=8, pady=(0, 6))

        row_a = ttk.Frame(btn_bar, style="Panel.TFrame")
        row_a.pack(side="top", fill="x")
        row_b = ttk.Frame(btn_bar, style="Panel.TFrame")
        row_b.pack(side="top", fill="x", pady=(6, 0))

        self.hint = ttk.Label(row_a, text="", style="Dim.TLabel")
        self.hint.pack(side="right")

        self.send_btn = ttk.Button(row_a, text="发送 (Ctrl+Enter)", style="Accent.TButton",
                                   command=self.send_once)
        self.send_btn.pack(side="left")
        ttk.Button(row_a, text="清空", style="Small.TButton",
                   command=self.input.clear).pack(side="left", padx=6)

        ttk.Label(row_b, text="间隔", style="Panel.TLabel").pack(side="left", padx=(0, 4))
        interval_entry = ttk.Entry(row_b, textvariable=self.interval_ms, width=7)
        interval_entry.pack(side="left")
        ToolTip(interval_entry, "定时发送间隔，单位毫秒（最小 20ms）")

        self.loop_btn = ttk.Button(row_b, text="开始定时", style="Toolbar.TButton",
                                   command=self.toggle_loop)
        self.loop_btn.pack(side="left", padx=6)
        ToolTip(self.loop_btn, "按设定间隔重复发送输入框内容")

        ttk.Label(row_b, text="次数", style="Panel.TLabel").pack(side="left", padx=(8, 4))
        count_entry = ttk.Entry(row_b, textvariable=self.loop_count, width=6)
        count_entry.pack(side="left")
        ToolTip(count_entry, "0 表示无限循环")

        # ---- 快捷指令 ----
        box = ttk.Labelframe(self, text=" 快捷指令（双击发送） ", padding=(6, 4))
        box.grid(row=3, column=0, sticky="nsew", padx=8, pady=(0, 8))
        box.columnconfigure(0, weight=1)
        box.rowconfigure(0, weight=1)

        self.tree = ttk.Treeview(box, columns=("name", "data", "mode"), show="headings",
                                 height=7, selectmode="browse")
        self.tree.heading("name", text="名称")
        self.tree.heading("data", text="内容")
        self.tree.heading("mode", text="类型")
        self.tree.column("name", width=100, anchor="w", stretch=False)
        self.tree.column("data", width=190, anchor="w")
        self.tree.column("mode", width=50, anchor="center", stretch=False)
        self.tree.grid(row=0, column=0, sticky="nsew")
        bar = ttk.Scrollbar(box, orient="vertical", command=self.tree.yview)
        bar.grid(row=0, column=1, sticky="ns")
        self.tree.configure(yscrollcommand=bar.set)
        self.tree.bind("<Double-1>", self._on_tree_double_click)
        self.tree.bind("<Return>", lambda _e: self._send_selected())

        row = ttk.Frame(box, style="Panel.TFrame")
        row.grid(row=1, column=0, columnspan=2, sticky="ew", pady=(6, 0))
        ttk.Button(row, text="添加", style="Small.TButton",
                   command=self.add_shortcut).pack(side="left")
        ttk.Button(row, text="载入", style="Small.TButton",
                   command=self.load_selected).pack(side="left", padx=4)
        ttk.Button(row, text="删除", style="Small.TButton",
                   command=self.remove_shortcut).pack(side="left")
        ttk.Button(row, text="上移", style="Small.TButton",
                   command=lambda: self.move_shortcut(-1)).pack(side="left", padx=4)
        ttk.Button(row, text="下移", style="Small.TButton",
                   command=lambda: self.move_shortcut(1)).pack(side="left")

        self._update_mode_hint()

    def _on_mode_changed(self) -> None:
        """HEX 模式下不自动追加行尾（需要时手动写成 0D 0A 更直观）。"""
        mode = self.send_mode.get()
        if not self._suspend_mode_hook:
            if mode == "HEX":
                if self.newline.get() != "无":
                    self._text_newline = self.newline.get()
                    self.newline.set("无")
            elif self.newline.get() == "无" and self._text_newline != "无":
                self.newline.set(self._text_newline)
        self._update_mode_hint()

    def _update_mode_hint(self) -> None:
        mode = self.send_mode.get()
        self.encoding_box.configure(state="readonly" if mode == "文本" else "disabled")
        if mode == "HEX":
            self.hint.configure(text="HEX 示例：AA BB 0x0D 0x0A")
        else:
            self.hint.configure(text="")

    # ------------------------------------------------------------------ #
    # 发送
    # ------------------------------------------------------------------ #
    def build_payload(self) -> bytes:
        """把输入框内容按当前选项组装为待发送字节串。"""
        raw = self.input.get_text()
        if self.send_mode.get() == "HEX":
            data = parse_hex(raw)
        else:
            data = encode_text(raw, self.encoding.get())
        data += _NEWLINE_BYTES.get(self.newline.get(), b"")
        return append_checksum(data, self.checksum.get())

    def send_once(self) -> None:
        try:
            payload = self.build_payload()
        except ValueError as exc:
            messagebox.showwarning("发送", f"{exc}\n\n请检查 HEX 内容。", parent=self)
            return
        self._dispatch(payload)

    def send_bytes(self, payload: bytes, mode: str) -> None:
        """直接发送一段数据（快捷指令使用）。"""
        if not payload:
            return
        self._dispatch(append_checksum(payload, self.checksum.get()), mode)

    def _dispatch(self, payload: bytes, mode: Optional[str] = None) -> None:
        if not payload:
            messagebox.showinfo("发送", "内容为空，没有可发送的数据。", parent=self)
            return
        self._last_payload = payload
        self.send_fn(payload, mode or self.send_mode.get(), self.encoding.get())
        self.total_tx += len(payload)

    def resend(self) -> None:
        """重发上一次的内容。"""
        if self._last_payload:
            self._dispatch(self._last_payload)

    def _on_ctrl_enter(self, _event=None) -> str:
        self.send_once()
        return "break"

    def _on_ctrl_l(self, _event=None) -> str:
        self.input.clear()
        return "break"

    # ------------------------------------------------------------------ #
    # 定时发送
    # ------------------------------------------------------------------ #
    def toggle_loop(self) -> None:
        if self.loop_send.get():
            self.stop_loop()
        else:
            self.start_loop()

    def start_loop(self) -> None:
        try:
            interval = max(20, int(float(self.interval_ms.get())))
        except ValueError:
            messagebox.showwarning("定时发送", "间隔必须是数字（毫秒），最小 20。", parent=self)
            return
        try:
            limit = max(0, int(self.loop_count.get()))
        except ValueError:
            limit = 0
        self.interval_ms.set(str(interval))
        self.loop_send.set(True)
        self._sent_in_loop = 0
        self._loop_limit = limit
        self.loop_btn.configure(text="停止定时", style="Danger.TButton")
        self._tick()

    def stop_loop(self) -> None:
        self.loop_send.set(False)
        if self._loop_job is not None:
            try:
                self.after_cancel(self._loop_job)
            except tk.TclError:  # pragma: no cover
                pass
            self._loop_job = None
        self.loop_btn.configure(text="开始定时", style="Toolbar.TButton")

    def _tick(self) -> None:
        if not self.loop_send.get():
            return
        self.send_once()
        self._sent_in_loop += 1
        limit = getattr(self, "_loop_limit", 0)
        if limit and self._sent_in_loop >= limit:
            self.stop_loop()
            return
        try:
            interval = max(20, int(float(self.interval_ms.get())))
        except ValueError:
            interval = 1000
        self._loop_job = self.after(interval, self._tick)

    # ------------------------------------------------------------------ #
    # 快捷指令
    # ------------------------------------------------------------------ #
    def _reload_shortcuts(self) -> None:
        self.tree.delete(*self.tree.get_children())
        for item in self._shortcuts:
            self.tree.insert("", "end", values=(item.get("name", ""),
                                                item.get("data", ""),
                                                item.get("mode", "文本")))

    def _selected_index(self) -> Optional[int]:
        selection = self.tree.selection()
        if not selection:
            return None
        return self.tree.index(selection[0])

    def add_shortcut(self) -> None:
        content = self.input.get_text().strip()
        if not content:
            messagebox.showinfo("添加指令", "请先在输入框填写要保存的内容。", parent=self)
            return
        name = simpledialog.askstring("添加指令", "指令名称：", parent=self,
                                      initialvalue=f"指令{len(self._shortcuts) + 1}")
        if not name:
            return
        self._shortcuts.append({"name": name, "data": content, "mode": self.send_mode.get()})
        self._reload_shortcuts()
        self.tree.selection_set(self.tree.get_children()[-1])

    def load_selected(self) -> None:
        index = self._selected_index()
        if index is None:
            return
        item = self._shortcuts[index]
        self.send_mode.set(item.get("mode", "文本"))
        self.input.text.delete("1.0", "end")
        self.input.text.insert("1.0", item.get("data", ""))

    def remove_shortcut(self) -> None:
        index = self._selected_index()
        if index is None:
            return
        item = self._shortcuts[index]
        if not messagebox.askyesno("删除指令", f"删除「{item.get('name', '')}」？", parent=self):
            return
        del self._shortcuts[index]
        self._reload_shortcuts()

    def move_shortcut(self, delta: int) -> None:
        index = self._selected_index()
        if index is None:
            return
        target = index + delta
        if not 0 <= target < len(self._shortcuts):
            return
        self._shortcuts[index], self._shortcuts[target] = self._shortcuts[target], self._shortcuts[index]
        self._reload_shortcuts()
        self.tree.selection_set(self.tree.get_children()[target])

    def _on_tree_double_click(self, _event) -> None:
        self._send_selected()

    def _send_selected(self) -> None:
        index = self._selected_index()
        if index is None:
            return
        item = self._shortcuts[index]
        mode = item.get("mode", "文本")
        raw = item.get("data", "")
        try:
            payload = parse_hex(raw) if mode == "HEX" else encode_text(raw, self.encoding.get())
        except ValueError as exc:
            messagebox.showwarning("发送指令", f"{exc}", parent=self)
            return
        self.send_bytes(payload, mode)

    # ------------------------------------------------------------------ #
    def set_connected(self, connected: bool) -> None:
        """串口关闭时停止定时发送。"""
        if not connected:
            self.stop_loop()

    def get_config(self) -> Dict[str, object]:
        return {
            "send_mode": self.send_mode.get(),
            "send_encoding": self.encoding.get(),
            "send_newline": self.newline.get(),
            "send_checksum": self.checksum.get(),
            "loop_interval_ms": self.interval_ms.get(),
            "loop_count": self.loop_count.get(),
            "shortcuts": list(self._shortcuts),
        }

    def apply_config(self, data: Dict[str, object]) -> None:
        if not data:
            return
        self._suspend_mode_hook = True
        try:
            self.send_mode.set(str(data.get("send_mode", "文本")))
            self.encoding.set(str(data.get("send_encoding", "utf-8")))
            self.newline.set(str(data.get("send_newline", "\\r\\n")))
            self.checksum.set(str(data.get("send_checksum", "无")))
            self.interval_ms.set(str(data.get("loop_interval_ms", "1000")))
            self.loop_count.set(str(data.get("loop_count", "0")))
            shortcuts = data.get("shortcuts")
            if isinstance(shortcuts, list) and shortcuts:
                self._shortcuts = [dict(s) for s in shortcuts if isinstance(s, dict)]
                self._reload_shortcuts()
        finally:
            self._suspend_mode_hook = False
        self._update_mode_hint()
