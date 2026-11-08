"""Render the README hero lockup (icon + "iGloo" wordmark) from docs/assets/iGloo.png.

Usage:
  py -3 scripts/generate_readme_hero.py --sheet <out.png> [--reference <lockup.png>]
  py -3 scripts/generate_readme_hero.py --final --font <label> [--out-dir docs/assets]
"""
from __future__ import annotations

import argparse
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
ICON = ROOT / "docs" / "assets" / "iGloo.png"
FONTS = Path(r"C:\Windows\Fonts")

# Geometry is the 222x56 Interoperable Europe lockup scaled by 8.
SCALE = 8
CANVAS = (222 * SCALE, 56 * SCALE)
CARD_RADIUS = 4 * SCALE
ICON_LEFT, ICON_TOP, ICON_WIDTH = 40 * SCALE, 14 * SCALE, 45 * SCALE
TEXT_LEFT, TEXT_BASELINE, TEXT_WIDTH = 85 * SCALE, 42 * SCALE, 98 * SCALE
WORD = "iGloo"

THEMES = {
    "light": {"card": "#F7F7F7", "text": "#101012"},
    "dark": {"card": "#171B22", "text": "#F0F6FC"},
}

# label -> (font file, named variation or None)
CANDIDATES = {
    "montserrat-semibold": ("Montserrat-SemiBold.ttf", None),
    "montserrat-bold": ("Montserrat-Bold.ttf", None),
    "segoe-variable-display-semibold": ("SegUIVar.ttf", b"Semibold Display"),
    "segoe-variable-display-bold": ("SegUIVar.ttf", b"Bold Display"),
    "segoe-ui-bold": ("segoeuib.ttf", None),
}


def load_font(label: str, size: int) -> ImageFont.FreeTypeFont:
    file, variation = CANDIDATES[label]
    font = ImageFont.truetype(str(FONTS / file), size)
    if variation is not None:
        font.set_variation_by_name(variation)
    return font


def fit_font(label: str) -> ImageFont.FreeTypeFont:
    # Bisect the size until the wordmark spans TEXT_WIDTH pixels.
    low, high = 50, 600
    while high - low > 1:
        mid = (low + high) // 2
        bbox = load_font(label, mid).getbbox(WORD, anchor="ls")
        if bbox[2] - bbox[0] > TEXT_WIDTH:
            high = mid
        else:
            low = mid
    return load_font(label, low)


def icon_image() -> Image.Image:
    icon = Image.open(ICON).convert("RGBA")
    icon = icon.crop(icon.getchannel("A").getbbox())
    height = round(icon.height * ICON_WIDTH / icon.width)
    return icon.resize((ICON_WIDTH, height), Image.LANCZOS)


def render(label: str, theme: str) -> Image.Image:
    colors = THEMES[theme]
    image = Image.new("RGBA", CANVAS, (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    draw.rounded_rectangle((0, 0, CANVAS[0] - 1, CANVAS[1] - 1), CARD_RADIUS, fill=colors["card"])
    icon = icon_image()
    image.alpha_composite(icon, (ICON_LEFT, ICON_TOP))
    draw.text((TEXT_LEFT, TEXT_BASELINE), WORD, font=fit_font(label), fill=colors["text"], anchor="ls")
    return image


def sheet(out: Path, reference: Path | None) -> None:
    rows: list[tuple[str, Image.Image]] = []
    if reference is not None:
        rows.append(("reference (upscaled)", Image.open(reference).convert("RGBA").resize(CANVAS, Image.BICUBIC)))
    for label in CANDIDATES:
        font = fit_font(label)
        ascender = -font.getbbox("l", anchor="ls")[1]
        rows.append((f"{label}  size={font.size}  l-height={ascender}px", render(label, "light")))
    gap, strip = 24, 40
    page = Image.new("RGBA", (CANVAS[0], len(rows) * (CANVAS[1] + strip + gap)), "white")
    draw = ImageDraw.Draw(page)
    caption = ImageFont.truetype(str(FONTS / "segoeui.ttf"), 28)
    y = 0
    for label, row in rows:
        draw.text((8, y + 6), label, font=caption, fill="#333333")
        page.alpha_composite(row, (0, y + strip))
        y += CANVAS[1] + strip + gap
    page.save(out)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--sheet", type=Path)
    parser.add_argument("--reference", type=Path)
    parser.add_argument("--final", action="store_true")
    parser.add_argument("--font", choices=sorted(CANDIDATES))
    parser.add_argument("--out-dir", type=Path, default=ROOT / "docs" / "assets")
    args = parser.parse_args()
    if args.sheet:
        sheet(args.sheet, args.reference)
    if args.final:
        if not args.font:
            parser.error("--final needs --font")
        for theme in THEMES:
            render(args.font, theme).save(args.out_dir / f"readme-hero-{theme}.png")


if __name__ == "__main__":
    main()
