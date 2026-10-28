"""Render the Agent Orchestrator icon: three agent nodes on an orbit around a diamond hub."""
import math
import sys
from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter

out = Path(sys.argv[1])
out.mkdir(parents=True, exist_ok=True)
S = 1024  # supersampled canvas


def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(len(a)))


def render(detail: bool) -> Image.Image:
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    # background: rounded square, vertical navy gradient
    grad = Image.new("RGBA", (S, S))
    gd = ImageDraw.Draw(grad)
    for y in range(S):
        gd.line([(0, y), (S, y)], fill=lerp((24, 38, 78, 255), (9, 13, 28, 255), y / S))
    mask = Image.new("L", (S, S), 0)
    pad = 40 if detail else 0
    ImageDraw.Draw(mask).rounded_rectangle([pad, pad, S - pad, S - pad], radius=210, fill=255)
    img.paste(grad, (0, 0), mask)
    d = ImageDraw.Draw(img)
    if detail:
        d.rounded_rectangle([pad, pad, S - pad, S - pad], radius=210, outline=(76, 141, 255, 110), width=10)

    cx, cy, r = S / 2, S / 2 + (18 if detail else 30), 300 if detail else 320
    angles = [-90, 30, 150]  # planner (top), implementer (bottom right), reviewer (bottom left)
    colors = [(76, 141, 255), (100, 216, 196), (134, 179, 255)]
    pts = [(cx + r * math.cos(math.radians(a)), cy + r * math.sin(math.radians(a))) for a in angles]

    # glow layer
    glow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    g = ImageDraw.Draw(glow)
    for (x, y), c in zip(pts, colors):
        g.ellipse([x - 150, y - 150, x + 150, y + 150], fill=c + (90,))
    g.ellipse([cx - 170, cy - 170, cx + 170, cy + 170], fill=(76, 141, 255, 70))
    img.alpha_composite(glow.filter(ImageFilter.GaussianBlur(60)))
    d = ImageDraw.Draw(img)

    # orbit arcs between nodes (gaps around nodes), with a direction chevron mid-arc
    w = 34 if detail else 46
    for i in range(3):
        a0, a1 = angles[i] + 24, angles[i] + 120 - 24
        d.arc([cx - r, cy - r, cx + r, cy + r], a0, a1, fill=colors[i] + (235,), width=w)
        if detail:
            m = math.radians((a0 + a1) / 2)
            mx, my = cx + r * math.cos(m), cy + r * math.sin(m)
            tx, ty = -math.sin(m), math.cos(m)  # tangent (clockwise)
            nx, ny = math.cos(m), math.sin(m)
            k = 46
            tip = (mx + tx * k, my + ty * k)
            d.polygon([tip, (mx - tx * k * 0.4 + nx * k, my - ty * k * 0.4 + ny * k),
                       (mx - tx * k * 0.4 - nx * k, my - ty * k * 0.4 - ny * k)], fill=colors[i] + (255,))

    # spokes to hub
    for (x, y) in pts:
        d.line([(cx, cy), (x, y)], fill=(134, 179, 255, 120), width=18 if detail else 0)

    # nodes
    nr = 92 if detail else 140
    for (x, y), c in zip(pts, colors):
        if detail:
            d.ellipse([x - nr, y - nr, x + nr, y + nr], fill=(12, 18, 38, 255), outline=c + (255,), width=30)
            ir = nr * 0.42
            d.ellipse([x - ir, y - ir, x + ir, y + ir], fill=c + (255,))
        else:  # solid dots stay readable at 16-32 px
            d.ellipse([x - nr, y - nr, x + nr, y + nr], fill=c + (255,))

    # hub diamond
    h = 120 if detail else 150
    d.polygon([(cx, cy - h), (cx + h, cy), (cx, cy + h), (cx - h, cy)], fill=(76, 141, 255, 255))
    h2 = h * 0.5
    d.polygon([(cx, cy - h2), (cx + h2, cy), (cx, cy + h2), (cx - h2, cy)], fill=(225, 236, 255, 255))
    return img


big = render(True)
small = render(False)
sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
frames = [(small if s <= 32 else big).resize((s, s), Image.LANCZOS) for s in sizes]
big.resize((256, 256), Image.LANCZOS).save(out / "app.png")
small.resize((72, 72), Image.LANCZOS).save(out / "title.png")  # title bars draw it at 18 px
frames[-1].save(out / "app.ico", sizes=[(s, s) for s in sizes], append_images=frames[:-1])
# preview sheet
sheet = Image.new("RGBA", (sum(sizes) + 20 * len(sizes) + 20, 300), (32, 32, 32, 255))
x = 20
for f in frames:
    sheet.alpha_composite(f, (x, 20))
    x += f.width + 20
sheet.save(out / "preview.png")
print("ok", out)
