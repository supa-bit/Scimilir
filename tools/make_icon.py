"""Draws Scimilir/icon.ico (the app and tray icon), matching wwwroot/icon.svg.
Needs: pip install pillow. Run: python tools/make_icon.py
"""
from PIL import Image, ImageDraw

S = 1024  # draw big, then shrink, for smooth edges
green, cream = (47, 111, 98, 255), (246, 244, 239, 255)
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)
u = S / 64  # the SVG is 64 units wide
d.rounded_rectangle([0, 0, S - 1, S - 1], radius=14 * u, fill=green)
d.rounded_rectangle([14 * u, 20 * u, 50 * u, 42 * u], radius=4 * u, fill=cream)
d.polygon([(21 * u, 41 * u), (30 * u, 41 * u), (21 * u, 49 * u)], fill=cream)
for x in (24, 32, 40):
    d.ellipse([(x - 2.6) * u, (31 - 2.6) * u, (x + 2.6) * u, (31 + 2.6) * u], fill=green)
img.save("Scimilir/icon.ico", sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)])
img.resize((256, 256), Image.LANCZOS).save("icon-preview.png")
print("wrote Scimilir/icon.ico")
