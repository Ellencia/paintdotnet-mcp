# Soft edges for a binary object mask: trimap around the mask edge -> closed-form alpha matting -> foreground colors
# with the background bled out of edge pixels. Runs on the Python that runs rembg (pymatting is a rembg dependency).
# Usage: python matte.py image.png mask.png out.png band
import sys
import numpy as np
from PIL import Image
from scipy import ndimage
from pymatting import estimate_alpha_cf, estimate_foreground_ml


def matte(rgb, mask, band):
    fg = ndimage.binary_erosion(mask, iterations=band)
    maybe = ndimage.binary_dilation(mask, iterations=band)
    img = rgb.astype(np.float64) / 255
    if not fg.any() or maybe.all():  # object thinner than the band, or no background to learn from
        return img, mask.astype(np.float64)
    trimap = np.where(fg, 1.0, np.where(maybe, 0.5, 0.0))
    alpha = estimate_alpha_cf(img, trimap).clip(0, 1)
    return estimate_foreground_ml(img, alpha), alpha


def main(image, mask, out, band):
    rgb = np.asarray(Image.open(image).convert("RGB"))
    m = np.asarray(Image.open(mask).convert("L")) > 127
    if m.shape != rgb.shape[:2]:
        sys.exit(f"mask is {m.shape[1]}x{m.shape[0]}, image is {rgb.shape[1]}x{rgb.shape[0]}")
    color, alpha = matte(rgb, m, int(band))
    rgba = np.dstack([color, alpha]) * 255
    Image.fromarray(rgba.round().clip(0, 255).astype(np.uint8), "RGBA").save(out)


if __name__ == "__main__":
    if sys.argv[1:] == ["--selftest"]:
        # A white disc on black with a 1-px grey rim: the rim must come out partly transparent and white, not grey.
        yy, xx = np.mgrid[:64, :64]
        d = np.hypot(xx - 32, yy - 32)
        rgb = np.zeros((64, 64, 3), np.uint8)
        rgb[d < 20] = 255
        rgb[(d >= 20) & (d < 21)] = 128
        color, alpha = matte(rgb, d < 20.5, 4)
        rim = (d >= 20) & (d < 21)
        assert alpha[d < 15].min() > 0.99 and alpha[d > 26].max() < 0.01, "core and background must stay solid"
        assert 0.2 < alpha[rim].mean() < 0.8, f"rim alpha {alpha[rim].mean():.2f}"
        assert color[rim].mean() > 0.8, f"rim color {color[rim].mean():.2f} still carries the black background"
        print("selftest ok")
    else:
        main(*sys.argv[1:5])
