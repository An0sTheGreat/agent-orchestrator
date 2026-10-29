"""Render the Agent Orchestrator icon: a flat clay tile (the Parchment theme's accent) with three cream agent nodes joined
in a loop around a hub. Usage: python make_icon.py <output folder>; writes app.ico, app.png, title.png and preview.png."""
import math
import sys
from pathlib import Path
from PIL import Image, ImageDraw

out = Path(sys.argv[1])
out.mkdir(parents=True, exist_ok=True)
S = 1024  # supersampled canvas
CLAY_TOP, CLAY_BOTTOM = (205, 112, 76), (178, 84, 50)  # around #C15F3C
CREAM = (244, 243, 238)  # #F4F3EE, the Parchment background


def render(small: bool) -> Image.Image:
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    grad = Image.new("RGBA", (S, S))
    gd = ImageDraw.Draw(grad)
    for y in range(S):
        t = y / S
        gd.line([(0, y), (S, y)], fill=tuple(int(a + (b - a) * t) for a, b in zip(CLAY_TOP, CLAY_BOTTOM)) + (255,))
    mask = Image.new("L", (S, S), 0)
    pad = 0 if small else 40
    ImageDraw.Draw(mask).rounded_rectangle([pad, pad, S - pad, S - pad], radius=220, fill=255)
    img.paste(grad, (0, 0), mask)
    d = ImageDraw.Draw(img)

    cx, cy, r = S / 2, S / 2 + 24, 300 if not small else 320
    pts = [(cx + r * math.cos(math.radians(a)), cy + r * math.sin(math.radians(a))) for a in (-90, 30, 150)]
    # the loop: thick rounded strokes between the three agents, and spokes to the hub
    w = 120 if small else 64
    for i in range(3):
        d.line([pts[i], pts[(i + 1) % 3]], fill=CREAM + (255,), width=w)
    for x, y in pts:
        d.ellipse([x - w / 2, y - w / 2, x + w / 2, y + w / 2], fill=CREAM + (255,))  # round the joints
    if not small:
        for x, y in pts:
            d.line([(cx, cy), (x, y)], fill=(232, 196, 176, 255), width=26)  # cream blended into clay (drawing does not blend alpha)
    # agents: cream discs with a clay core (solid at small sizes, where the core would blur away)
    nr = 150 if small else 118
    for x, y in pts:
        d.ellipse([x - nr, y - nr, x + nr, y + nr], fill=CREAM + (255,))
        if not small:
            ir = nr * 0.45
            d.ellipse([x - ir, y - ir, x + ir, y + ir], fill=CLAY_BOTTOM + (255,))
    # hub
    hr = 0 if small else 70
    if hr:
        d.ellipse([cx - hr, cy - hr, cx + hr, cy + hr], fill=CREAM + (255,))
    return img


big = render(False)
small = render(True)
sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
frames = [(small if s <= 32 else big).resize((s, s), Image.LANCZOS) for s in sizes]
big.resize((256, 256), Image.LANCZOS).save(out / "app.png")
small.resize((72, 72), Image.LANCZOS).save(out / "title.png")  # dialogs draw it at 18 px
frames[-1].save(out / "app.ico", sizes=[(s, s) for s in sizes], append_images=frames[:-1])
sheet = Image.new("RGBA", (sum(sizes) + 20 * len(sizes) + 20, 300), (32, 32, 32, 255))
x = 20
for f in frames:
    sheet.alpha_composite(f, (x, 20))
    x += f.width + 20
sheet.save(out / "preview.png")
print("ok", out)
