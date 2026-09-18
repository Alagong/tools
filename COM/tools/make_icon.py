"""生成应用图标 build/app.ico（纯标准库，无需 Pillow）。

图案：圆角深色底 + 两条数字方波（呼应串口通信的波形），多尺寸抗锯齿。
用法：python tools/make_icon.py
"""

from __future__ import annotations

import math
import struct
from pathlib import Path

SIZES = (16, 24, 32, 48, 64, 128, 256)

BG_TOP = (32, 36, 52)
BG_BOTTOM = (16, 18, 26)
BORDER = (66, 74, 104)
WAVE_A = (77, 141, 255)    # 主波形：蓝
WAVE_B = (63, 208, 201)    # 副波形：青


def _lerp(a: float, b: float, t: float) -> float:
    return a + (b - a) * t


def _blend(dst, src, alpha: float):
    return tuple(int(round(_lerp(dst[i], src[i], alpha))) for i in range(3))


def _round_rect_alpha(x: float, y: float, size: float, radius: float) -> float:
    """圆角矩形覆盖度（带 1px 抗锯齿）。返回 0~1。"""
    cx = min(max(x, radius), size - radius)
    cy = min(max(y, radius), size - radius)
    dist = math.hypot(x - cx, y - cy)
    return min(max(radius + 0.5 - dist, 0.0), 1.0)


def _square_level(t: float, duty: float) -> float:
    return 0.0 if (t % 1.0) < duty else 1.0


def _stroke_alpha(dist: float, half: float) -> float:
    return min(max(half + 0.7 - dist, 0.0), 1.0)


def render(size: int) -> bytes:
    """渲染一张 size×size 的 BGRA 位图（top-down，供后续翻转）。"""
    radius = size * 0.2
    pixels = [[(0, 0, 0, 0) for _ in range(size)] for _ in range(size)]

    # 两条方波的参数：base_y、振幅、周期数、占空比、相位、颜色
    waves = (
        (0.36, 0.10, 2.0, 0.58, 0.0, WAVE_A),
        (0.64, 0.10, 2.0, 0.42, 0.5, WAVE_B),
    )
    half = max(size * 0.052, 0.75)
    margin = 0.12                                  # 波形左右留白
    span = 1.0 - margin * 2

    for py in range(size):
        y = py + 0.5
        for px in range(size):
            x = px + 0.5

            cover = _round_rect_alpha(x, y, size, radius)
            if cover <= 0.0:
                continue

            # 背景竖向渐变
            color = tuple(int(round(_lerp(BG_TOP[i], BG_BOTTOM[i], y / size))) for i in range(3))

            # 描边：靠近圆角矩形边缘 1px
            edge = radius + 0.5 - math.hypot(
                x - min(max(x, radius), size - radius),
                y - min(max(y, radius), size - radius),
            )
            if size >= 32 and edge < size * 0.02:
                color = _blend(color, BORDER, 0.9)

            # 方波：对相邻两列取竖直区间，保证跳变沿连线；两侧留白
            ta = ((px + 0.5) / size - margin) / span
            tb = ((px + 1.5) / size - margin) / span
            if 0.0 <= ta <= 1.0 and 0.0 <= tb <= 1.0:
                for base, amp, periods, duty, phase, wave_color in waves:
                    y0 = (base + (_square_level(ta * periods + phase, duty) - 0.5) * 2 * amp) * size
                    y1 = (base + (_square_level(tb * periods + phase, duty) - 0.5) * 2 * amp) * size
                    lo, hi = min(y0, y1), max(y0, y1)
                    if lo - half - 1 <= y <= hi + half + 1:
                        dist = 0.0 if lo <= y <= hi else min(abs(y - lo), abs(y - hi))
                        alpha = _stroke_alpha(dist, half)
                        if alpha > 0:
                            color = _blend(color, wave_color, alpha)

            pixels[py][px] = (color[0], color[1], color[2], int(round(cover * 255)))
    return pixels


def _bmp_bytes(pixels, size: int) -> bytes:
    """把像素转成 ICO 内嵌的 BITMAPINFOHEADER + BGRA（自下而上）+ AND 掩码。"""
    header = struct.pack(
        "<IiiHHIIiiII",
        40,           # biSize
        size,         # biWidth
        size * 2,     # biHeight（XOR + AND 两层）
        1,            # biPlanes
        32,           # biBitCount
        0,            # biCompression
        size * size * 4,
        2835, 2835,   # 分辨率
        0, 0,
    )
    body = bytearray()
    for py in range(size - 1, -1, -1):        # 自下而上
        row = pixels[py]
        for px in range(size):
            r, g, b, a = row[px]
            body += bytes((b, g, r, a))
    mask_row = ((size + 31) // 32) * 4
    body += bytes(mask_row * size)            # AND 掩码全 0：由 alpha 决定透明
    return header + bytes(body)


def build_ico(path: Path) -> None:
    images = [(_bmp_bytes(pixels, size), size) for size in SIZES
              for pixels in (render(size),)]
    path.parent.mkdir(parents=True, exist_ok=True)

    offset = 6 + 16 * len(images)
    directory = bytearray(struct.pack("<HHH", 0, 1, len(images)))
    for data, size in images:
        directory += struct.pack(
            "<BBBBHHII",
            0 if size >= 256 else size,
            0 if size >= 256 else size,
            0, 0, 1, 32, len(data), offset,
        )
        offset += len(data)

    with path.open("wb") as fp:
        fp.write(bytes(directory))
        for data, _ in images:
            fp.write(data)
    print(f"已生成图标：{path}（{', '.join(str(s) for s in SIZES)}）")


if __name__ == "__main__":
    root = Path(__file__).resolve().parent.parent
    build_ico(root / "assets" / "app.ico")
