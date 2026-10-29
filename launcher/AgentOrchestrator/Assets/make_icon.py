"""Render the Agent Orchestrator icon, "Huddle": three agent robots (a cream leader above two light-clay teammates) on a
clay tile. Usage: python make_icon.py <output folder>; writes app.ico, app.png, title.png and preview.png."""
import sys
from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter

out = Path(sys.argv[1])
out.mkdir(parents=True, exist_ok=True)
S = 1024  # supersampled canvas
CREAM, INK, LIGHT, MID, CLAY, GOLD = (244, 243, 238), (31, 30, 29), (236, 190, 166), (218, 140, 104), (193, 95, 60), (222, 170, 80)
TILE_TOP, TILE_BOTTOM = (206, 110, 74), (172, 80, 48)


def tile():
    img = Image.new("RGBA", (S, S))
    d = ImageDraw.Draw(img)
    for y in range(S):
        t = y / S
        d.line([(0, y), (S, y)], fill=tuple(int(a + (b - a) * t) for a, b in zip(TILE_TOP, TILE_BOTTOM)) + (255,))
    return img


def shadow(img, draw_fn, offset, alpha=80, blur=28):
    layer = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    draw_fn(ImageDraw.Draw(layer), (0, 0, 0, alpha))
    img.alpha_composite(layer.filter(ImageFilter.GaussianBlur(blur)), offset)


def bot(img, x, y, r, body, eyes, antenna):
    """One agent head centred at (x, y): body with side ears, antenna, dark visor with two eyes."""
    def head(d, c):
        d.rounded_rectangle([x - r, y - r * 0.85, x + r, y + r * 0.85], radius=r * 0.45, fill=c)
        for sx in (-1, 1):
            d.rounded_rectangle([x + sx * r - r * 0.16, y - r * 0.3, x + sx * r + r * 0.16, y + r * 0.3], radius=r * 0.12, fill=c)
    shadow(img, head, (0, int(r * 0.11)))
    d = ImageDraw.Draw(img)
    head(d, body + (255,))
    for sx in (-1, 1):  # ears a shade darker than the head
        d.rounded_rectangle([x + sx * r - r * 0.16, y - r * 0.3, x + sx * r + r * 0.16, y + r * 0.3], radius=r * 0.12,
                            fill=tuple(int(c * 0.86) for c in body) + (255,))
    d.line([(x, y - r * 0.85), (x, y - r * 1.2)], fill=body + (255,), width=int(r * 0.12))
    d.ellipse([x - r * 0.15, y - r * 1.35, x + r * 0.15, y - r * 1.05], fill=antenna + (255,))
    d.rounded_rectangle([x - r * 0.72, y - r * 0.38, x + r * 0.72, y + r * 0.3], radius=r * 0.3, fill=INK + (255,))
    for ex in (-0.33, 0.33):
        d.ellipse([x + r * ex - r * 0.13, y - r * 0.17, x + r * ex + r * 0.13, y + r * 0.09], fill=eyes + (255,))


def render():
    img = tile()
    k, up = 1.35, 0.35 * 110  # agent size, and the lift that keeps the lower pair off the bottom edge
    bot(img, 512 - 182 * k, 512 + 190 * k - up, 150 * k, LIGHT, MID, LIGHT)
    bot(img, 512 + 182 * k, 512 + 190 * k - up, 150 * k, LIGHT, MID, LIGHT)
    bot(img, 512, 512 - 82 * k - up, 175 * k, CREAM, CLAY, GOLD)
    mask = Image.new("L", (S, S), 0)  # everything (ears, shadows) stays inside the rounded tile
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, S - 1, S - 1], radius=230, fill=255)
    img.putalpha(mask)
    return img


icon = render()
sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
frames = [icon.resize((s, s), Image.LANCZOS) for s in sizes]
frames[-1].save(out / "app.png")
icon.resize((72, 72), Image.LANCZOS).save(out / "title.png")  # dialogs draw it at 18 px
frames[-1].save(out / "app.ico", sizes=[(s, s) for s in sizes], append_images=frames[:-1])
sheet = Image.new("RGBA", (sum(sizes) + 20 * len(sizes) + 20, 300), (32, 32, 32, 255))
x = 20
for f in frames:
    sheet.alpha_composite(f, (x, 20))
    x += f.width + 20
sheet.save(out / "preview.png")
print("ok", out)
