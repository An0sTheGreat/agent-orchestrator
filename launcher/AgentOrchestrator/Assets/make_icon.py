"""Build the Agent Orchestrator icons from the artwork in icon-source.png (the clay tile with three agents).
The white margin around the rounded tile becomes transparent. Usage: python make_icon.py <output folder>;
writes app.ico, app.png, title.png and preview.png."""
import sys
from pathlib import Path
from PIL import Image, ImageDraw

here = Path(__file__).parent
out = Path(sys.argv[1])
out.mkdir(parents=True, exist_ok=True)

art = Image.open(here / "icon-source.png").convert("RGBA")
# Clear the near-white surround by flooding from each corner, then crop to the tile.
for corner in [(0, 0), (art.width - 1, 0), (0, art.height - 1), (art.width - 1, art.height - 1)]:
    ImageDraw.floodfill(art, corner, (255, 255, 255, 0), thresh=60)
art = art.crop(art.getchannel("A").getbbox())
side = max(art.size)
icon = Image.new("RGBA", (side, side), (0, 0, 0, 0))
icon.paste(art, ((side - art.width) // 2, (side - art.height) // 2))

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
