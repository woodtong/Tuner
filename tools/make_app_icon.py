"""生成 Tuner 应用图标 app.ico（需要 Pillow）。

设计：深色圆角底板（与应用主题一致）+ 三路调音台推子。
中间琥珀色是 MASTER（推子最高），左侧 emerald 绿、右侧灰色，高低错落。
大尺寸帧（≥48）带凹槽描边、旋钮渐变、指示线与柔光；小尺寸帧（≤32）
用简化变体（更粗的旋钮、无细节），保证 16px 下仍可辨认。

用法：python tools/make_app_icon.py
产物：src/Tuner.App/app.ico、ui-preview/icon-preview.png
"""

from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageOps

ROOT = Path(__file__).resolve().parents[1]
S = 2048  # 画布边长（超采样，最后缩到各尺寸）

BG_TOP = (0x2C, 0x37, 0x4A)
BG_BOT = (0x0F, 0x14, 0x1D)
GROOVE_FILL = (0x10, 0x15, 0x1F)
GROOVE_EDGE = (0x39, 0x41, 0x4F)
EMERALD = (0x34, 0xD3, 0x99)
AMBER = (0xF5, 0xB9, 0x42)
NEUTRAL = (0x98, 0xA2, 0xB3)
NEUTRAL_SMALL = (0xCB, 0xD3, 0xE0)  # 小尺寸下灰色在深底上发暗，提一档


def diag_gradient():
    """左上亮 → 右下暗的对角渐变（小图算好再放大，避免旋转补角硬边）。"""
    n = 256
    g = Image.new("L", (n, n))
    g.putdata([min(255, (x + y) * 255 // (2 * n - 2)) for y in range(n) for x in range(n)])
    return g.resize((S, S), Image.BILINEAR)

# (颜色, 旋钮中心 y 占比, 指示线颜色)：中间 MASTER 最高
FADERS = [
    (EMERALD, 0.455, (0xE9, 0xFF, 0xF4)),  # 左
    (AMBER, 0.335, (0xFF, 0xF3, 0xD6)),    # 中（MASTER）
    (NEUTRAL, 0.595, (0xF4, 0xF7, 0xFC)),  # 右
]
TRACK_XS = (0.30, 0.50, 0.70)
TRACK_TOP, TRACK_BOT = 0.264, 0.736


def lighten(c, f):
    return tuple(min(255, round(v + (255 - v) * f)) for v in c)


def darken(c, f):
    return tuple(round(v * (1 - f)) for v in c)


def rounded(draw, box, radius, **kw):
    draw.rounded_rectangle(box, radius=radius, **kw)


def tile_mask(inset_f, radius_f):
    m = Image.new("L", (S, S), 0)
    i = round(inset_f * S)
    ImageDraw.Draw(m).rounded_rectangle(
        [i, i, S - i, S - i], radius=round(radius_f * S), fill=255)
    return m


def draw(variant: str) -> Image.Image:
    """variant: 'rich'（大尺寸帧）或 'small'（小尺寸帧）。"""
    rich = variant == "rich"
    inset_f, radius_f = (0.020, 0.210) if rich else (0.034, 0.215)

    # 底板：对角渐变
    grad = ImageOps.colorize(diag_gradient(), black=BG_BOT, white=BG_TOP).convert("RGBA")
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    mask = tile_mask(inset_f, radius_f)
    img.paste(grad, (0, 0), mask)
    d = ImageDraw.Draw(img)
    i = round(inset_f * S)
    box = [i, i, S - i, S - i]
    r = round(radius_f * S)
    d.rounded_rectangle(box, radius=r, outline=(255, 255, 255, 14), width=6)

    knob_w = round((0.150 if rich else 0.280) * S)
    knob_h = round((0.066 if rich else 0.130) * S)
    knob_r = round(knob_h * 0.30)
    groove_w = round((0.014 if rich else 0.034) * S)
    edge_w = round((0.005 if rich else 0.011) * S)

    # 凹槽
    for xf in TRACK_XS:
        cx = round(xf * S)
        rounded(d, [cx - groove_w, round(TRACK_TOP * S), cx + groove_w,
                    round(TRACK_BOT * S)], groove_w,
                fill=GROOVE_FILL, outline=GROOVE_EDGE, width=edge_w)

    if rich:
        # 旋钮投影（轻，仅托起层次；不加拉光，保持干净）
        shadow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
        ds = ImageDraw.Draw(shadow)
        for color, yf, _ in FADERS:
            for xf in TRACK_XS:
                cx, cy = round(xf * S), round(yf * S)
                rounded(ds, [cx - knob_w // 2, cy - knob_h // 2 + 20,
                             cx + knob_w // 2, cy + knob_h // 2 + 20],
                        knob_r, fill=(0, 0, 0, 70))
        img.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(26)))
        d = ImageDraw.Draw(img)

    # 旋钮：竖向微渐变 + 顶部亮线（rich 才有指示线）
    for (color, yf, line_color), xf in zip(FADERS, TRACK_XS):
        if not rich and color is NEUTRAL:
            color = NEUTRAL_SMALL
        cx, cy = round(xf * S), round(yf * S)
        kbox = [cx - knob_w // 2, cy - knob_h // 2, cx + knob_w // 2, cy + knob_h // 2]
        kg = Image.linear_gradient("L").resize((knob_w, knob_h))
        knob = ImageOps.colorize(kg, black=darken(color, 0.12),
                                 white=lighten(color, 0.22)).convert("RGBA")
        kmask = Image.new("L", (knob_w, knob_h), 0)
        ImageDraw.Draw(kmask).rounded_rectangle([0, 0, knob_w - 1, knob_h - 1],
                                                radius=knob_r, fill=255)
        img.paste(knob, (kbox[0], kbox[1]), kmask)
        if rich:
            lw, lh = round(knob_w * 0.57), round(knob_h * 0.155)
            rounded(d, [cx - lw // 2, cy - lh // 2, cx + lw // 2, cy + lh // 2],
                    lh // 2, fill=line_color + (238,))

    img.putalpha(mask)
    return img


def main():
    master = draw("rich")
    small = draw("small")

    ico_path = ROOT / "src" / "Tuner.App" / "app.ico"
    # 每个尺寸都显式给帧：大中尺寸用 rich 变体、小尺寸用 small 变体。
    # 不能让 Pillow 自行缩放补帧（会把未提供的尺寸错缩成最小帧）。
    rich_frames = {s: master.resize((s, s), Image.LANCZOS) for s in (256, 128, 64, 48, 40)}
    small_frames = {s: small.resize((s, s), Image.LANCZOS) for s in (32, 24, 20, 16)}
    sizes = [(256, 256), (128, 128), (64, 64), (48, 48), (40, 40),
             (32, 32), (24, 24), (20, 20), (16, 16)]
    master.save(ico_path, format="ICO", sizes=sizes,
                append_images=[rich_frames[s] for s in (256, 128, 64, 48, 40)] +
                              [small_frames[s] for s in (32, 24, 20, 16)])
    downscaled = {**rich_frames, **small_frames}
    print(f"已写入 {ico_path} ({ico_path.stat().st_size} 字节)")

    # 预览图：深/浅两种底色，检查托盘与浅色标题栏下的观感
    dark, light = (18, 21, 27), (232, 236, 244)
    sheet = Image.new("RGBA", (1120, 480), dark + (255,))
    sheet.paste(master.resize((256, 256), Image.LANCZOS), (48, 48))
    sheet.paste(master.resize((128, 128), Image.LANCZOS), (360, 48))
    sheet.paste(master.resize((64, 64), Image.LANCZOS), (540, 48))
    row = [(48, 0), (32, 96), (24, 176), (16, 248)]
    sheet.paste(Image.new("RGBA", (400, 120), light + (255,)), (700, 48))
    for size, x in row:
        icon = downscaled.get(size) or master.resize((size, size), Image.LANCZOS)
        sheet.paste(icon, (700 + 40 + x, 70))
    sheet.paste(Image.new("RGBA", (1120, 140), (40, 44, 52, 255)), (0, 340))
    for size, x in row + [(64, 540)]:
        sheet.paste(downscaled.get(size) or
                    (master if size >= 48 else small).resize((size, size), Image.LANCZOS),
                    (48 + x, 380))
    out = ROOT / "ui-preview" / "icon-preview.png"
    sheet.save(out)
    print(f"已写入 {out}")


if __name__ == "__main__":
    main()
