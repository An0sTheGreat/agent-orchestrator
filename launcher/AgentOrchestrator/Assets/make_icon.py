"""Build the Agent Orchestrator icons: the robot on a purple tile (icon-source.png is the reference artwork). The icon is
drawn as shapes at each size rather than scaled from the artwork, so it stays sharp; the small sizes (24 px and below)
enlarge the robot and thicken the antenna so it still reads. Usage: python make_icon.py <output folder>; writes app.ico,
app.png, title.png and preview.png."""
import sys
from pathlib import Path
from PIL import Image, ImageDraw

TILE, EAR, HEAD, VISOR = (160, 80, 248, 255), (212, 161, 249, 255), (253, 248, 238, 255), (42, 38, 49, 255)


def draw(size: int) -> Image.Image:
    """The icon at `size` px, drawn 16x larger and scaled down (anti-aliased edges)."""
    k = 16
    big = size * k
    im = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    small = size <= 24
    # Shapes in units of the tile (0..1), measured from icon-source.png; small sizes zoom the robot about its centre.
    zoom, cx, cy = (1.14, 0.5, 0.58) if small else (1.0, 0.5, 0.58)

    def box(x0, y0, x1, y1):
        f = lambda x, c: (c + (x - c) * zoom) * big
        return [f(x0, cx), f(y0, cy), f(x1, cx), f(y1, cy)]

    def r(v):
        return v * zoom * big

    d.rounded_rectangle([0, 0, big - 1, big - 1], radius=0.24 * big, fill=TILE)
    d.rounded_rectangle(box(0.096, 0.505, 0.901, 0.714), radius=r(0.09), fill=EAR)
    stem = 0.07 if small else 0.05
    d.rectangle(box(0.504 - stem / 2, 0.20, 0.504 + stem / 2, 0.32), fill=HEAD)
    ball = 0.085 if small else 0.07
    d.ellipse(box(0.504 - ball, 0.173 - ball, 0.504 + ball, 0.173 + ball), fill=HEAD)
    d.rounded_rectangle(box(0.168, 0.304, 0.829, 0.853), radius=r(0.25), fill=HEAD)
    d.rounded_rectangle(box(0.231, 0.451, 0.774, 0.728), radius=r(0.13), fill=VISOR)
    eye = 0.058 if small else 0.047
    for ex in (0.386, 0.619):
        d.ellipse(box(ex - eye, 0.585 - eye, ex + eye, 0.585 + eye), fill=HEAD)
    return im.resize((size, size), Image.LANCZOS)


if __name__ == "__main__":
    out = Path(sys.argv[1])
    out.mkdir(parents=True, exist_ok=True)
    sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
    frames = [draw(s) for s in sizes]
    frames[-1].save(out / "app.png")
    draw(72).save(out / "title.png")  # dialogs draw it at 18 px
    frames[-1].save(out / "app.ico", sizes=[(s, s) for s in sizes], append_images=frames[:-1])
    sheet = Image.new("RGBA", (sum(sizes) + 20 * len(sizes) + 20, 300), (32, 32, 32, 255))
    x = 20
    for f in frames:
        sheet.alpha_composite(f, (x, 20))
        x += f.width + 20
    sheet.save(out / "preview.png")
    print("ok", out)
