"""跨平台主题：暗色 / 浅色 / 绚丽（霓虹），支持运行时即时切换。

设计：颜色集中在一组 :class:`Palette` 里，模块级 ``__getattr__`` 把这些颜色
以大写常量（``theme.BG_PANEL`` 等）的形式代理到「当前调色板」，
因此各处业务代码无需改动即可跟随主题变化。
"""

from __future__ import annotations

import tkinter as tk
import tkinter.font as tkfont
from dataclasses import dataclass
from tkinter import ttk
from typing import Dict, Iterator, Tuple


@dataclass(frozen=True)
class Palette:
    """一套完整的配色方案。"""

    key: str
    label: str
    dark: bool
    bg_root: str
    bg_panel: str
    bg_input: str
    bg_hover: str
    bg_active: str
    border: str
    fg: str
    fg_dim: str
    fg_bright: str
    accent: str
    accent_dark: str
    on_accent: str
    ok: str
    warn: str
    err: str
    rx: str
    tx: str
    ts: str
    grid: str
    grid_minor: str
    danger_bg: str
    danger_fg: str
    wave: Tuple[str, ...]


# --------------------------------------------------------------------------- #
# 三套配色
# --------------------------------------------------------------------------- #
DARK = Palette(
    key="dark", label="暗色（默认）", dark=True,
    bg_root="#1b1d21", bg_panel="#24262b", bg_input="#141518",
    bg_hover="#2f323a", bg_active="#3a3d45", border="#3a3d45",
    fg="#d8dae0", fg_dim="#8b9099", fg_bright="#ffffff",
    accent="#3d7eff", accent_dark="#2f63cc", on_accent="#ffffff",
    ok="#3fb950", warn="#d29922", err="#f85149",
    rx="#7ee787", tx="#79c0ff", ts="#6e7681",
    grid="#2b2e34", grid_minor="#23262b",
    danger_bg="#4a2b2b", danger_fg="#ff9a94",
    wave=("#7ee787", "#79c0ff", "#ffa657", "#d2a8ff",
          "#ff7b72", "#56d4dd", "#e3b341", "#a5d6ff"),
)

LIGHT = Palette(
    key="light", label="浅色", dark=False,
    bg_root="#eef0f4", bg_panel="#ffffff", bg_input="#ffffff",
    bg_hover="#e7eaf0", bg_active="#d9dee7", border="#c9cfda",
    fg="#25282e", fg_dim="#6b7280", fg_bright="#000000",
    accent="#2563eb", accent_dark="#1d4ed8", on_accent="#ffffff",
    ok="#15803d", warn="#b45309", err="#dc2626",
    rx="#0f766e", tx="#1d4ed8", ts="#8b93a1",
    grid="#e4e8ef", grid_minor="#eef1f6",
    danger_bg="#fee2e2", danger_fg="#b91c1c",
    wave=("#0f766e", "#2563eb", "#c2410c", "#7c3aed",
          "#dc2626", "#0891b2", "#a16207", "#4d7c0f"),
)

VIVID = Palette(
    key="vivid", label="绚丽（霓虹）", dark=True,
    bg_root="#0a0718", bg_panel="#151033", bg_input="#07051a",
    bg_hover="#241a52", bg_active="#33246e", border="#3b2d80",
    fg="#e6e2ff", fg_dim="#9b93d6", fg_bright="#ffffff",
    accent="#ff2d95", accent_dark="#c9006f", on_accent="#ffffff",
    ok="#00e6a8", warn="#ffc53d", err="#ff4d6d",
    rx="#00ffc8", tx="#ff86e0", ts="#7b6fd0",
    grid="#241a52", grid_minor="#1b1340",
    danger_bg="#4a0b32", danger_fg="#ff8ac2",
    wave=("#00ffc8", "#ff4fd8", "#ffd166", "#8b5cf6",
          "#ff6b6b", "#38bdf8", "#a3e635", "#ff9de2"),
)

PALETTES: Dict[str, Palette] = {p.key: p for p in (DARK, LIGHT, VIVID)}
DEFAULT_KEY = DARK.key

_current: Palette = DARK

# 大写常量名 -> Palette 字段名，保证 `theme.BG_PANEL` 这类旧写法继续可用
_ALIASES = {
    "BG_ROOT": "bg_root",
    "BG_PANEL": "bg_panel",
    "BG_INPUT": "bg_input",
    "BG_HOVER": "bg_hover",
    "BG_ACTIVE": "bg_active",
    "BORDER": "border",
    "FG": "fg",
    "FG_DIM": "fg_dim",
    "FG_BRIGHT": "fg_bright",
    "ACCENT": "accent",
    "ACCENT_DARK": "accent_dark",
    "ON_ACCENT": "on_accent",
    "OK": "ok",
    "WARN": "warn",
    "ERR": "err",
    "RX_COLOR": "rx",
    "TX_COLOR": "tx",
    "TS_COLOR": "ts",
    "GRID": "grid",
    "GRID_MINOR": "grid_minor",
    "DANGER_BG": "danger_bg",
    "DANGER_FG": "danger_fg",
    "WAVE_COLORS": "wave",
    "IS_DARK": "dark",
}


def __getattr__(name: str):
    field = _ALIASES.get(name)
    if field is not None:
        return getattr(_current, field)
    raise AttributeError(f"module {__name__!r} has no attribute {name!r}")


def current() -> Palette:
    return _current


def current_key() -> str:
    return _current.key


def set_palette(key: str) -> Palette:
    """切换当前调色板（不会自动刷新已有控件，请配合 :func:`refresh`）。"""
    global _current
    _current = PALETTES.get(str(key), DARK)
    return _current


# --------------------------------------------------------------------------- #
# 字体
# --------------------------------------------------------------------------- #
_MONO_CANDIDATES = (
    "Cascadia Mono", "Consolas", "JetBrains Mono", "Fira Code", "Source Code Pro",
    "Menlo", "Monaco", "DejaVu Sans Mono", "Liberation Mono", "Courier New",
)
_UI_CANDIDATES = (
    "Microsoft YaHei UI", "Microsoft YaHei", "PingFang SC", "Hiragino Sans GB",
    "Noto Sans CJK SC", "Source Han Sans SC", "WenQuanYi Micro Hei",
    "Segoe UI", "Helvetica Neue", "DejaVu Sans", "Arial",
)

_cache: dict = {}


def _family(root: tk.Misc, candidates, fallback_font: str) -> str:
    key = (fallback_font, candidates[0])
    if key in _cache:
        return _cache[key]
    try:
        available = set(tkfont.families(root))
    except tk.TclError:  # pragma: no cover
        available = set()
    family = next((c for c in candidates if c in available), "")
    if not family:
        try:
            family = tkfont.nametofont(fallback_font).cget("family")
        except tk.TclError:  # pragma: no cover
            family = candidates[-1]
    _cache[key] = family
    return family


def mono_font(root: tk.Misc, size: int = 10, bold: bool = False) -> tuple:
    """等宽字体（数据区、HEX 区）。"""
    return (_family(root, _MONO_CANDIDATES, "TkFixedFont"), size, "bold" if bold else "normal")


def ui_font(root: tk.Misc, size: int = 10, bold: bool = False) -> tuple:
    """界面字体（支持中文显示）。"""
    return (_family(root, _UI_CANDIDATES, "TkDefaultFont"), size, "bold" if bold else "normal")


def base_ui_font(root: tk.Misc) -> tuple:
    return ui_font(root, 10)


# --------------------------------------------------------------------------- #
# ttk 样式
# --------------------------------------------------------------------------- #
def apply_theme(root: tk.Tk, key: str | None = None) -> ttk.Style:
    """应用当前（或指定）主题，返回配置好的 ``ttk.Style``。"""
    if key:
        set_palette(key)
    p = _current
    style = ttk.Style(root)
    if "clam" in style.theme_names():
        style.theme_use("clam")

    base_ui = ui_font(root, 10)
    root.configure(background=p.bg_root)
    root.option_add("*Font", base_ui)
    root.option_add("*TCombobox*Listbox.background", p.bg_input)
    root.option_add("*TCombobox*Listbox.foreground", p.fg)
    root.option_add("*TCombobox*Listbox.selectBackground", p.accent)
    root.option_add("*TCombobox*Listbox.selectForeground", p.on_accent)
    root.option_add("*TCombobox*Listbox.font", base_ui)
    root.option_add("*Text.selectBackground", p.accent)
    root.option_add("*Text.selectForeground", p.on_accent)
    root.option_add("*Menu.background", p.bg_panel)
    root.option_add("*Menu.foreground", p.fg)
    root.option_add("*Menu.activeBackground", p.accent)
    root.option_add("*Menu.activeForeground", p.on_accent)

    style.configure(".", background=p.bg_root, foreground=p.fg, fieldbackground=p.bg_input,
                    bordercolor=p.border, lightcolor=p.bg_panel, darkcolor=p.bg_panel,
                    troughcolor=p.bg_input, focuscolor=p.accent)

    style.configure("TFrame", background=p.bg_root)
    style.configure("Panel.TFrame", background=p.bg_panel)
    style.configure("Bar.TFrame", background=p.bg_panel)

    style.configure("TLabel", background=p.bg_root, foreground=p.fg)
    style.configure("Panel.TLabel", background=p.bg_panel, foreground=p.fg)
    style.configure("Dim.TLabel", background=p.bg_panel, foreground=p.fg_dim)
    style.configure("Title.TLabel", background=p.bg_panel, foreground=p.fg_bright,
                    font=ui_font(root, 11, bold=True))
    style.configure("Status.TLabel", background=p.bg_panel, foreground=p.fg_dim)

    style.configure("TLabelframe", background=p.bg_panel, bordercolor=p.border,
                    relief="solid", borderwidth=1)
    style.configure("TLabelframe.Label", background=p.bg_panel, foreground=p.fg_dim,
                    font=ui_font(root, 10, bold=True))

    style.configure("TButton", background=p.bg_hover, foreground=p.fg, borderwidth=1,
                    bordercolor=p.border, focuscolor=p.bg_hover, padding=(10, 5), relief="flat")
    style.map("TButton",
              background=[("pressed", p.bg_active), ("active", p.bg_active),
                          ("disabled", p.bg_panel)],
              foreground=[("disabled", p.fg_dim)])

    style.configure("Accent.TButton", background=p.accent, foreground=p.on_accent,
                    borderwidth=0, padding=(14, 5))
    style.map("Accent.TButton",
              background=[("pressed", p.accent_dark), ("active", p.accent_dark),
                          ("disabled", p.bg_hover)],
              foreground=[("disabled", p.fg_dim)])

    style.configure("Danger.TButton", background=p.danger_bg, foreground=p.danger_fg,
                    borderwidth=0)
    style.map("Danger.TButton",
              background=[("active", p.danger_bg), ("pressed", p.danger_bg)])

    style.configure("Small.TButton", padding=(6, 2))
    style.configure("Toolbar.TButton", padding=(8, 4))

    style.configure("TCheckbutton", background=p.bg_panel, foreground=p.fg, focuscolor=p.bg_panel)
    style.map("TCheckbutton",
              background=[("active", p.bg_panel)],
              foreground=[("disabled", p.fg_dim)])
    style.configure("Bar.TCheckbutton", background=p.bg_panel, foreground=p.fg)
    style.map("Bar.TCheckbutton", background=[("active", p.bg_panel)])

    style.configure("TEntry", fieldbackground=p.bg_input, foreground=p.fg, bordercolor=p.border,
                    insertcolor=p.fg, padding=3)
    style.map("TEntry", bordercolor=[("focus", p.accent)])

    style.configure("TCombobox", fieldbackground=p.bg_input, background=p.bg_hover,
                    foreground=p.fg, bordercolor=p.border, arrowcolor=p.fg, padding=3)
    style.map("TCombobox",
              fieldbackground=[("readonly", p.bg_input), ("disabled", p.bg_root)],
              foreground=[("disabled", p.fg_dim)],
              bordercolor=[("focus", p.accent), ("hover", p.accent_dark)],
              arrowcolor=[("disabled", p.fg_dim)])

    style.configure("TSpinbox", fieldbackground=p.bg_input, foreground=p.fg, arrowcolor=p.fg,
                    bordercolor=p.border, padding=2)

    style.configure("TSeparator", background=p.border)

    style.configure("TNotebook", background=p.bg_panel, bordercolor=p.border, tabmargins=(4, 3, 4, 0))
    style.configure("TNotebook.Tab", background=p.bg_root, foreground=p.fg_dim,
                    padding=(14, 6), borderwidth=0)
    style.map("TNotebook.Tab",
              background=[("selected", p.bg_panel), ("active", p.bg_hover)],
              foreground=[("selected", p.accent if p.key == "vivid" else p.fg_bright)])

    style.configure("TPanedwindow", background=p.bg_root)
    style.configure("Sash", sashthickness=6, gripcount=0, background=p.bg_root)

    style.configure("Vertical.TScrollbar", background=p.bg_hover, troughcolor=p.bg_root,
                    bordercolor=p.bg_root, arrowcolor=p.fg_dim, relief="flat", width=12)
    style.map("Vertical.TScrollbar", background=[("active", p.bg_active), ("pressed", p.bg_active)])
    style.configure("Horizontal.TScrollbar", background=p.bg_hover, troughcolor=p.bg_root,
                    bordercolor=p.bg_root, arrowcolor=p.fg_dim, relief="flat", width=12)
    style.map("Horizontal.TScrollbar", background=[("active", p.bg_active), ("pressed", p.bg_active)])

    style.configure("Treeview", background=p.bg_input, fieldbackground=p.bg_input, foreground=p.fg,
                    bordercolor=p.border, rowheight=24, relief="flat")
    style.map("Treeview", background=[("selected", p.accent)],
              foreground=[("selected", p.on_accent)])
    style.configure("Treeview.Heading", background=p.bg_hover, foreground=p.fg_dim,
                    relief="flat", font=ui_font(root, 9, bold=True))
    style.map("Treeview.Heading", background=[("active", p.bg_active)])

    style.configure("TProgressbar", background=p.accent, troughcolor=p.bg_input,
                    bordercolor=p.bg_input, lightcolor=p.accent, darkcolor=p.accent)

    return style


# --------------------------------------------------------------------------- #
# 运行时刷新
# --------------------------------------------------------------------------- #
def _walk(widget: tk.Misc) -> Iterator[tk.Misc]:
    yield widget
    try:
        children = widget.winfo_children()
    except tk.TclError:  # pragma: no cover - 控件已销毁
        return
    for child in children:
        yield from _walk(child)


def refresh(root: tk.Misc) -> None:
    """重新应用样式，并通知所有实现了 ``on_theme_changed`` 的自定义控件刷新。

    ttk 控件由样式自动更新；tk 原生控件（Text/Canvas/Menu 等）需要显式通知。
    """
    top = root.winfo_toplevel()
    apply_theme(top)  # type: ignore[arg-type]
    for widget in _walk(top):
        hook = getattr(widget, "on_theme_changed", None)
        if callable(hook):
            try:
                hook()
            except tk.TclError:  # pragma: no cover - 控件已销毁
                continue
