"""生成 ADB 无线调试助手的应用图标。

设计：Windows 11 风格的圆角方块底 + 白色手机轮廓 + 右上角浅青色无线信号弧 + 屏内终端提示符，
对应这个程序的四件事：手机、无线、ADB、调试。用 4 倍超采样绘制再降采样，保证 16px 下边缘干净。

输出：app-icon.png（1024，预览用）与 app.ico（多尺寸，供 exe 图标）。
"""

from PIL import Image, ImageDraw

SUPERSAMPLE = 4
TARGET = 1024
SCALE = TARGET * SUPERSAMPLE

BACKGROUND_TOP = (74, 160, 255)
BACKGROUND_BOTTOM = (18, 78, 186)
GLYPH = (255, 255, 255, 255)
SIGNAL = (150, 234, 255, 255)


def gradient(size: int) -> Image.Image:
    strip = Image.new("RGB", (1, 256))
    for y in range(256):
        t = y / 255
        strip.putpixel(
            (0, y),
            tuple(round(BACKGROUND_TOP[i] + (BACKGROUND_BOTTOM[i] - BACKGROUND_TOP[i]) * t) for i in range(3)),
        )
    return strip.resize((size, size), Image.BILINEAR).convert("RGBA")


def rounded_mask(size: int, radius_ratio: float) -> Image.Image:
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        [0, 0, size - 1, size - 1],
        radius=int(size * radius_ratio),
        fill=255,
    )
    return mask


def draw_glyph(draw: ImageDraw.ImageDraw, size: float, simplified: bool = False) -> None:
    def p(x: float, y: float) -> tuple[float, float]:
        return (x * size, y * size)

    # 小尺寸下屏内提示符会糊成一团，因此 16/24 px 用简化稿：去掉提示符与听筒，线条加粗。
    stroke = int(size * (0.072 if simplified else 0.050))
    phone_box = [*p(0.300, 0.205), *p(0.650, 0.815)]
    draw.rounded_rectangle(phone_box, radius=int(size * 0.092), outline=GLYPH, width=stroke)

    if not simplified:
        # 听筒条：让外框读起来像手机而不是一般方块
        draw.line([p(0.430, 0.268), p(0.520, 0.268)], fill=GLYPH, width=int(size * 0.020))

        prompt = int(size * 0.038)
        draw.line([p(0.410, 0.480), p(0.478, 0.530)], fill=GLYPH, width=prompt, joint="curve")
        draw.line([p(0.478, 0.530), p(0.410, 0.580)], fill=GLYPH, width=prompt, joint="curve")
        draw.line([p(0.510, 0.580), p(0.578, 0.580)], fill=GLYPH, width=prompt)

    signal_width = int(size * (0.060 if simplified else 0.044))
    center = p(0.712, 0.292)
    for radius_ratio in ((0.135,) if simplified else (0.105, 0.195)):
        radius = size * radius_ratio
        draw.arc(
            [center[0] - radius, center[1] - radius, center[0] + radius, center[1] + radius],
            start=268,
            end=358,
            fill=SIGNAL,
            width=signal_width,
        )
    dot = size * 0.028
    draw.ellipse(
        [center[0] - dot, center[1] - dot, center[0] + dot, center[1] + dot],
        fill=SIGNAL,
    )


def build_icon(simplified: bool = False) -> Image.Image:
    canvas = Image.new("RGBA", (SCALE, SCALE), (0, 0, 0, 0))
    canvas.paste(gradient(SCALE), (0, 0), rounded_mask(SCALE, 0.222))
    draw_glyph(ImageDraw.Draw(canvas), SCALE, simplified)
    return canvas.resize((TARGET, TARGET), Image.LANCZOS)


def write_ico(path: str, frames: list[tuple[int, Image.Image]]) -> None:
    """手写 ICO 容器：每帧一段 PNG 载荷（Vista 以后支持），这样才能给 16/24 px 单独用简化稿。"""
    import io
    import struct

    payloads: list[tuple[int, bytes]] = []
    for size, image in frames:
        buffer = io.BytesIO()
        image.convert("RGBA").resize((size, size), Image.LANCZOS).save(buffer, format="PNG")
        payloads.append((size, buffer.getvalue()))

    header = struct.pack("<HHH", 0, 1, len(payloads))
    offset = len(header) + 16 * len(payloads)
    directory = bytearray()
    body = bytearray()
    for size, payload in payloads:
        directory += struct.pack(
            "<BBBBHHII",
            0 if size >= 256 else size,
            0 if size >= 256 else size,
            0,
            0,
            1,
            32,
            len(payload),
            offset,
        )
        body += payload
        offset += len(payload)

    with open(path, "wb") as handle:
        handle.write(header)
        handle.write(bytes(directory))
        handle.write(bytes(body))


if __name__ == "__main__":
    import os

    here = os.path.dirname(os.path.abspath(__file__))
    project_assets = os.path.join(here, "..", "ADBDevTool", "Assets")
    os.makedirs(project_assets, exist_ok=True)

    icon = build_icon()
    small = build_icon(simplified=True)
    png_path = os.path.join(here, "app-icon.png")
    ico_path = os.path.join(project_assets, "app.ico")
    icon.save(png_path)
    small.resize((256, 256), Image.LANCZOS).save(os.path.join(here, "app-icon-small.png"))
    write_ico(
        ico_path,
        [
            (256, icon),
            (128, icon),
            (64, icon),
            (48, icon),
            (32, icon),
            (24, small),
            (16, small),
        ],
    )
    print("png:", os.path.normpath(png_path))
    print("ico:", os.path.normpath(ico_path))
    print("ico sizes:", sorted(Image.open(ico_path).info.get("sizes", [])))
