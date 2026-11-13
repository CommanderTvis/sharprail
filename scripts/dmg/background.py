# /// script
# requires-python = ">=3.11"
# dependencies = ["pillow==13.0.0"]
# ///
# Draws the disk image's window background, background.png (540x380) and background@2x.png, into this directory:
# `uv run --script scripts/dmg/background.py`. The images are committed; rerun this after changing the layout here
# or the icon positions in settings.py, which must agree (icons centred at y=170, x=140 and x=400).
# The background stays light because Finder draws icon labels in the viewer's text colour (black in light mode),
# which no .DS_Store setting changes.
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

WIDTH, HEIGHT = 540, 380
ICON_Y, APP_X, APPLICATIONS_X = 170, 140, 400
TOP, BOTTOM = (246, 249, 252), (226, 237, 242)
# The app icon's navy and teal.
NAVY, TEAL = (36, 42, 104), (31, 167, 196)
HINT = (74, 88, 120)
HERE = Path(__file__).resolve().parent
FONT = HERE.parent.parent / "src/Tereditor.Plugins.UI.Kit/Assets/Fonts/Geist-Medium.ttf"
SUPERSAMPLE = 4


def mix(a, b, t):
    return tuple(round(x + (y - x) * t) for x, y in zip(a, b))


def draw(scale):
    s = scale * SUPERSAMPLE
    image = Image.new("RGB", (WIDTH * s, HEIGHT * s))
    pen = ImageDraw.Draw(image)
    for y in range(HEIGHT * s):
        pen.line([(0, y), (WIDTH * s, y)], fill=mix(TOP, BOTTOM, y / (HEIGHT * s - 1)))
    # The arrow runs between the two icons' edges (128 pt icons), shaft then head, shading from navy to teal.
    start, end, head = APP_X + 90, APPLICATIONS_X - 90, 16
    thickness = 5
    for x in range(start * s, (end - head + 2) * s):
        t = (x - start * s) / ((end - start) * s)
        pen.line([(x, (ICON_Y - thickness / 2) * s), (x, (ICON_Y + thickness / 2) * s)], fill=mix(NAVY, TEAL, t))
    pen.polygon([((end - head) * s, (ICON_Y - 11) * s), (end * s, ICON_Y * s), ((end - head) * s, (ICON_Y + 11) * s)], fill=TEAL)
    font = ImageFont.truetype(str(FONT), 13 * s)
    pen.text((WIDTH * s / 2, 300 * s), "Drag tereditor to Applications to install it", font=font, fill=HINT, anchor="mm")
    return image.resize((WIDTH * scale, HEIGHT * scale), Image.Resampling.LANCZOS)


for scale, name in ((1, "background.png"), (2, "background@2x.png")):
    draw(scale).save(HERE / name, optimize=True, dpi=(72 * scale, 72 * scale))
