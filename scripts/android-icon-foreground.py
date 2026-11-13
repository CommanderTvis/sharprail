# Regenerates the adaptive launcher icon layers (mipmap-*/icon_foreground.png and icon_monochrome.png) from the round
# src/Tereditor.Android/icon-source.png: keys the frog out of its navy-to-teal gradient and fits it into the 66dp safe zone.
# Run from the repo root: uv run --with pillow --with numpy --with scipy python3 -I scripts/android-icon-foreground.py
import numpy as np
from PIL import Image
from scipy import ndimage as ndi

SOURCE = 'src/Tereditor.Android/icon-source.png'
RES = 'src/Tereditor.Android/Resources'
FROG_DP = 52  # frog width on the 108dp canvas (safe zone is a 66dp circle)

im = np.array(Image.open(SOURCE).convert('RGBA')).astype(float)
rgb = im[..., :3]
h, w = rgb.shape[:2]
t = np.linspace(0, 1, w)[:, None]
bg = np.zeros_like(rgb)
for y in range(h):  # background of each row: linear blend of its left and right margin
    if im[y, 60:90, 3].min() > 250 and im[y, 455:485, 3].min() > 250:
        bg[y] = rgb[y, 60:90].mean(0) * (1 - t) + rgb[y, 455:485].mean(0) * t
    else:
        bg[y] = rgb[y, w // 2]
m = np.abs(rgb - bg).max(2) > 28
m[:, :95] = m[:, 450:] = False
m[:130] = m[400:] = False
m = ndi.binary_fill_holes(ndi.binary_closing(m, iterations=2))
lab, n = ndi.label(m)
m = lab == 1 + np.argmax(ndi.sum(m, lab, range(1, n + 1)))
alpha = np.clip((ndi.gaussian_filter(m.astype(float), 1.0) - 0.5) * 2 + 0.5, 0, 1)
frog = Image.fromarray(np.dstack([rgb, alpha * 255]).astype(np.uint8))
frog = frog.crop(frog.getchannel('A').point(lambda v: 255 if v > 8 else 0).getbbox())

# monochrome layer for themed icons: the silhouette without the dark eye
lum = np.array(frog.convert('L')).astype(float)
mono_a = np.array(frog.getchannel('A')).astype(float) * np.clip((lum - 60) / 40, 0, 1)
mono = Image.fromarray(np.dstack([np.zeros((*mono_a.shape, 3)), mono_a]).astype(np.uint8))

for name, px in (('mdpi', 108), ('hdpi', 162), ('xhdpi', 216), ('xxhdpi', 324), ('xxxhdpi', 432)):
    fw = round(FROG_DP / 108 * px)
    fh = round(frog.height * fw / frog.width)
    for layer, src in (('icon_foreground', frog), ('icon_monochrome', mono)):
        canvas = Image.new('RGBA', (px, px), (0, 0, 0, 0))
        canvas.alpha_composite(src.resize((fw, fh), Image.LANCZOS), ((px - fw) // 2, (px - fh) // 2))
        canvas.save(f'{RES}/mipmap-{name}/{layer}.png')
