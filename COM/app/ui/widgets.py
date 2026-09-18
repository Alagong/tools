"""可复用的基础控件：带滚动条的文本区、状态灯、气泡提示。"""

from __future__ import annotations

import tkinter as tk
from tkinter import ttk
from typing import Optional, Sequence

from . import theme


class ScrolledText(ttk.Frame):
    """深色文本区 + 垂直滚动条，支持自动滚动、行数上限与只读模式。"""

    def __init__(
        self,
        master: tk.Misc,
        *,
        wrap: str = "char",
        font: Optional[tuple] = None,
        height: int = 6,
        max_lines: Optional[int] = None,
        readonly: bool = False,
        horizontal: bool = False,
        **kwargs,
    ) -> None:
        super().__init__(master, style="Panel.TFrame")
        self.rowconfigure(0, weight=1)
        self.columnconfigure(0, weight=1)

        self.text = tk.Text(
            self,
            wrap=wrap,
            font=font or theme.mono_font(self, 10),
            height=height,
            bg=theme.BG_INPUT,
            fg=theme.FG,
            insertbackground=theme.FG,
            selectbackground=theme.ACCENT,
            selectforeground=theme.FG_BRIGHT,
            relief="flat",
            borderwidth=0,
            highlightthickness=1,
            highlightbackground=theme.BORDER,
            highlightcolor=theme.ACCENT,
            padx=8,
            pady=6,
            undo=False,
            maxundo=0,
            spacing1=1,
            spacing3=1,
            **kwargs,
        )
        self.text.grid(row=0, column=0, sticky="nsew")

        self.vbar = ttk.Scrollbar(self, orient="vertical", command=self.text.yview)
        self.vbar.grid(row=0, column=1, sticky="ns")
        self._hbar: Optional[ttk.Scrollbar] = None
        if horizontal:
            self._hbar = ttk.Scrollbar(self, orient="horizontal", command=self.text.xview)
            self._hbar.grid(row=1, column=0, sticky="ew")
            self.text.configure(xscrollcommand=self._hbar.set)

        self.text.configure(yscrollcommand=self._on_yscroll)

        self.auto_scroll = True
        self.max_lines = max_lines
        self._readonly = readonly
        if readonly:
            self.text.configure(state="disabled")

        self._build_menu()
        self.text.bind("<Control-a>", self._select_all)
        self.text.bind("<Control-A>", self._select_all)

    # ------------------------------------------------------------------ #
    def _on_yscroll(self, first: str, last: str) -> None:
        self.vbar.set(first, last)
        # 用户手动向上滚动时暂停自动滚动，回到末尾自动恢复
        try:
            self.auto_scroll = float(last) >= 0.999
        except ValueError:  # pragma: no cover
            pass

    def _build_menu(self) -> None:
        self.menu = tk.Menu(self, tearoff=0, bg=theme.BG_PANEL, fg=theme.FG,
                            activebackground=theme.ACCENT, activeforeground=theme.FG_BRIGHT,
                            bd=0)
        self.menu.add_command(label="复制", command=self.copy_selection)
        self.menu.add_command(label="粘贴", command=self.paste)
        self.menu.add_separator()
        self.menu.add_command(label="全选", command=self._select_all)
        self.menu.add_command(label="清空", command=self.clear)

        for widget in (self.text,):
            widget.bind("<Button-3>", self._popup, add="+")
            widget.bind("<Button-2>", self._popup, add="+")

    def _popup(self, event: tk.Event) -> str:
        try:
            self.menu.tk_popup(event.x_root, event.y_root)
        finally:
            self.menu.grab_release()
        return "break"

    def _select_all(self, _event=None) -> str:
        self.text.tag_add("sel", "1.0", "end-1c")
        self.text.mark_set("insert", "1.0")
        return "break"

    # ------------------------------------------------------------------ #
    def set_readonly(self, readonly: bool) -> None:
        self._readonly = readonly
        self.text.configure(state="disabled" if readonly else "normal")

    def append(self, content: str, tags: Sequence[str] = ()) -> None:
        """追加文本并按需裁剪、滚动。"""
        if not content:
            return
        keep_auto = self.auto_scroll
        if self._readonly:
            self.text.configure(state="normal")
        self.text.insert("end", content, tuple(tags))
        self._trim()
        if keep_auto:
            self.text.see("end")
        if self._readonly:
            self.text.configure(state="disabled")

    def _trim(self) -> None:
        if not self.max_lines:
            return
        try:
            total = int(self.text.index("end-1c").split(".")[0])
        except (tk.TclError, ValueError):  # pragma: no cover
            return
        excess = total - self.max_lines
        if excess > 0:
            self.text.delete("1.0", f"{excess + 1}.0")

    def clear(self) -> None:
        was_readonly = self._readonly
        self.text.configure(state="normal")
        self.text.delete("1.0", "end")
        if was_readonly:
            self.text.configure(state="disabled")
        self.auto_scroll = True

    def get_text(self) -> str:
        return self.text.get("1.0", "end-1c")

    def on_theme_changed(self) -> None:
        """主题切换时刷新 tk 原生控件（ttk 由样式自动处理）。"""
        p = theme.current()
        self.text.configure(
            bg=p.bg_input, fg=p.fg, insertbackground=p.fg,
            selectbackground=p.accent, selectforeground=p.on_accent,
            highlightbackground=p.border, highlightcolor=p.accent,
        )
        self.menu.configure(bg=p.bg_panel, fg=p.fg,
                            activebackground=p.accent, activeforeground=p.on_accent)

    def line_count(self) -> int:
        try:
            return int(self.text.index("end-1c").split(".")[0])
        except (tk.TclError, ValueError):  # pragma: no cover
            return 0

    def copy_selection(self) -> None:
        try:
            text = self.text.get("sel.first", "sel.last")
        except tk.TclError:
            return
        self.clipboard_clear()
        self.clipboard_append(text)

    def paste(self) -> None:
        if self._readonly:
            return
        try:
            data = self.clipboard_get()
        except tk.TclError:
            return
        self.text.insert("insert", data)


class StatusLamp(ttk.Frame):
    """状态指示灯：小圆点 + 文本，颜色始终跟随当前主题。"""

    _LEVELS = {"idle": "FG_DIM", "ok": "OK", "warn": "WARN", "err": "ERR"}

    def __init__(self, master: tk.Misc, text: str = "未连接", style: str = "Panel.TFrame") -> None:
        super().__init__(master, style=style)
        self.canvas = tk.Canvas(self, width=14, height=14, bg=theme.BG_PANEL,
                                highlightthickness=0, bd=0)
        self.canvas.pack(side="left", padx=(0, 6))
        self._dot = self.canvas.create_oval(3, 3, 12, 12, fill=theme.FG_DIM, outline="")
        self.label = ttk.Label(self, text=text, style="Panel.TLabel")
        self.label.pack(side="left")
        self._level = "idle"

    def set_state(self, text: str, level: str = "idle") -> None:
        self._level = level if level in self._LEVELS else "idle"
        self.label.configure(text=text)
        self._paint()

    def _paint(self) -> None:
        self.canvas.itemconfigure(self._dot, fill=getattr(theme, self._LEVELS[self._level]))

    def on_theme_changed(self) -> None:
        self.canvas.configure(bg=theme.BG_PANEL)
        self._paint()


class ToolTip:
    """鼠标悬停气泡提示。"""

    def __init__(self, widget: tk.Misc, text: str, delay: int = 450) -> None:
        self.widget = widget
        self.text = text
        self.delay = delay
        self._after_id: Optional[str] = None
        self._tip: Optional[tk.Toplevel] = None
        widget.bind("<Enter>", self._schedule, add="+")
        widget.bind("<Leave>", self._hide, add="+")
        widget.bind("<ButtonPress>", self._hide, add="+")

    def _schedule(self, _event=None) -> None:
        self._cancel()
        self._after_id = self.widget.after(self.delay, self._show)

    def _cancel(self) -> None:
        if self._after_id:
            self.widget.after_cancel(self._after_id)
            self._after_id = None

    def _show(self) -> None:
        if self._tip or not self.text:
            return
        x = self.widget.winfo_rootx() + 12
        y = self.widget.winfo_rooty() + self.widget.winfo_height() + 6
        self._tip = tk.Toplevel(self.widget)
        self._tip.wm_overrideredirect(True)
        self._tip.wm_geometry(f"+{x}+{y}")
        tk.Label(self._tip, text=self.text, justify="left", bg="#2f323a", fg=theme.FG,
                 relief="solid", borderwidth=1, padx=8, pady=4,
                 font=theme.ui_font(self.widget, 9)).pack()

    def _hide(self, _event=None) -> None:
        self._cancel()
        if self._tip is not None:
            self._tip.destroy()
            self._tip = None


def labeled_row(parent: tk.Misc, label: str, widget: tk.Widget, label_width: int = 8) -> None:
    """把 ``标签 + 控件`` 放在同一行（Grid）。"""
    ttk.Label(parent, text=label, style="Panel.TLabel", width=label_width).grid(
        sticky="w", padx=(0, 4))
    widget.grid(sticky="ew")


def separator(parent: tk.Misc, orient: str = "horizontal") -> ttk.Separator:
    sep = ttk.Separator(parent, orient=orient)
    return sep
