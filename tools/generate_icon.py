"""Rebuild the Luma app icon. Requires Pillow; not needed to build/run Luma."""
from pathlib import Path
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parents[1]
out = root / "Luma" / "Assets"
out.mkdir(parents=True, exist_ok=True)
im = Image.new("RGBA", (256, 256), (0, 0, 0, 0))
d = ImageDraw.Draw(im)
d.rounded_rectangle((6, 6, 250, 250), radius=58, fill="#171a22")
d.ellipse((48, 48, 207, 207), fill="#ffc681")
d.ellipse((98, 20, 220, 142), fill="#171a22")
d.ellipse((189, 48, 215, 74), fill="#ffe6c1")
im.save(out / "luma.ico", sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)])
im.save(out / "luma.png")
print(out / "luma.ico")
