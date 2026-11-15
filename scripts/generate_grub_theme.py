"""Build the iGloo Community GRUB theme: images, PF2 fonts, theme.txt and the two archives.

Usage:
  py -3 scripts/generate_grub_theme.py                    writes the theme tree and the archives
  py -3 scripts/generate_grub_theme.py --preview <dir>    also draws the menu as theme.txt lays it out

Needs Pillow and the Montserrat files in C:\\Windows\\Fonts (SIL Open Font License 1.1).
"""
from __future__ import annotations

import argparse
import gzip
import io
import struct
import tarfile
from pathlib import Path

from PIL import Image, ImageColor, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
ICON = ROOT / "docs" / "assets" / "iGloo.png"
FONTS = Path(r"C:\Windows\Fonts")
THEMES = ROOT / "distros" / "_shared" / "grub-theme"
OWN = THEMES / "igloo"
VENDORED = THEMES / "stylish"

# Sizes below are for 1080p; a variant scales them in halves. The larger one still fits 1024x768.
VARIANTS = {"1080p": 2, "4k": 3}
TERMINAL_FONT = {"1080p": ("terminus-14.pf2", "Terminus Regular 14"), "4k": ("terminus-18.pf2", "Terminus Regular 18")}
ARCHIVE_TIME = 1767225600  # 2026-01-01 UTC, so a rebuild gives the same bytes

WHITE, INK, SLATE, MUTED, SELECTION = "#FFFFFF", "#101012", "#3A4654", "#5B6876", "#DFEAF5"

FONT_FILE, FONT_WEIGHT = "Montserrat-SemiBold.ttf", "SemiBold"
ITEM_SIZE, LABEL_SIZE = 22, 16
MENU_LEFT, MENU_WIDTH, MENU_TOP = 20, 60, 34  # percent of the screen
ITEM_HEIGHT, ICON_SIZE, PADDING, SPACING, ICON_SPACE, RADIUS = 60, 48, 8, 6, 20, 12
LOGO_TOP, COUNTDOWN_UP, HINTS_UP = 13, 92, 56
HINTS = "Enter: start    E: edit    C: command line"
COUNTDOWN = "Booting in %d seconds"

# Basic Latin, Latin-1, Latin Extended-A and the punctuation menu titles use.
CODEPOINTS = [*range(0x20, 0x7F), *range(0xA0, 0x180),
              0x2013, 0x2014, 0x2018, 0x2019, 0x201C, 0x201D, 0x2022, 0x2026, 0x20AC, 0x2122]


def px(value: int, scale: int) -> int:
    return value * scale // 2


def glyph(font: ImageFont.FreeTypeFont, char: str) -> tuple[int, int, int, int, int, bytes]:
    """Width, height, left offset, offset of the bottom row above the baseline, advance, packed 1-bit rows."""
    mask, (left, top) = font.getmask2(char, mode="1", anchor="ls")
    width, height = mask.size
    bits = bytearray((width * height + 7) // 8)
    for y in range(height):
        for x in range(width):
            if mask.getpixel((x, y)):
                index = y * width + x
                bits[index // 8] |= 0x80 >> (index % 8)
    return width, height, left, -(top + height), round(font.getlength(char)), bytes(bits)


def pf2(size: int) -> tuple[str, bytes]:
    """A GRUB PF2 font; GRUB draws it without anti-aliasing, so the glyphs are rendered 1-bit here too."""
    font = ImageFont.truetype(str(FONTS / FONT_FILE), size)
    family = font.getname()[0]
    name = f"{family} {FONT_WEIGHT} {size}"
    ascent, descent = font.getmetrics()
    missing = glyph(font, "\uffff")
    glyphs = [(code, glyph(font, chr(code))) for code in CODEPOINTS]
    glyphs = [(code, g) for code, g in glyphs if g != missing or code == 0x20]

    def section(tag: str, body: bytes) -> bytes:
        return tag.encode("ascii") + struct.pack(">I", len(body)) + body

    def text(tag: str, value: str) -> bytes:
        return section(tag, value.encode("ascii") + b"\0")

    head = b"".join([
        section("FILE", b"PFF2"), text("NAME", name), text("FAMI", family),
        text("WEIG", "bold"), text("SLAN", "normal"),
        section("PTSZ", struct.pack(">H", size)),
        section("MAXW", struct.pack(">H", max(max(g[0], g[4]) for _, g in glyphs))),
        section("MAXH", struct.pack(">H", max(ascent + descent, max(g[1] for _, g in glyphs)))),
        section("ASCE", struct.pack(">H", ascent)), section("DESC", struct.pack(">H", descent)),
    ])
    offset = len(head) + 8 + 9 * len(glyphs) + 8
    index, data = bytearray(), bytearray()
    for code, (width, height, left, bottom, advance, bits) in glyphs:
        index += struct.pack(">IBI", code, 0, offset + len(data))
        data += struct.pack(">HHhhh", width, height, left, bottom, advance) + bits
    return name, head + section("CHIX", bytes(index)) + b"DATA" + struct.pack(">I", 0xFFFFFFFF) + bytes(data)


def lockup(scale: int) -> Image.Image:
    """The igloo with the product name beside it, on the white of the menu."""
    icon = Image.open(ICON).convert("RGBA")
    icon = icon.crop(icon.getchannel("A").getbbox())
    icon = icon.resize((round(icon.width * px(76, scale) / icon.height), px(76, scale)), Image.LANCZOS)
    brand = ImageFont.truetype(str(FONTS / "Montserrat-Bold.ttf"), px(50, scale))
    product = ImageFont.truetype(str(FONTS / "Montserrat-Medium.ttf"), px(50, scale))
    margin, gap, space = px(4, scale), px(18, scale), px(14, scale)
    brand_width = round(brand.getlength("iGloo"))
    width = icon.width + gap + brand_width + space + round(product.getlength("Community"))
    image = Image.new("RGBA", (width + 2 * margin, px(96, scale)), WHITE)
    image.alpha_composite(icon, (margin, px(8, scale)))
    draw = ImageDraw.Draw(image)
    baseline, left = px(76, scale), margin + icon.width + gap
    draw.text((left, baseline), "iGloo", font=brand, fill=INK, anchor="ls")
    draw.text((left + brand_width + space, baseline), "Community", font=product, fill=SLATE, anchor="ls")
    return image.convert("RGB")


def selection(scale: int) -> dict[str, Image.Image]:
    """The three slices GRUB stretches into the bar behind the selected entry."""
    radius, height = px(RADIUS, scale), px(ITEM_HEIGHT, scale)
    big = Image.new("RGBA", (24 * radius, 4 * height), (0, 0, 0, 0))
    ImageDraw.Draw(big).rounded_rectangle((0, 0, big.width - 1, big.height - 1), 4 * radius, fill=SELECTION)
    bar = big.resize((6 * radius, height), Image.LANCZOS)
    return {"select_w.png": bar.crop((0, 0, radius, height)),
            "select_c.png": bar.crop((2 * radius, 0, 4 * radius, height)),
            "select_e.png": bar.crop((5 * radius, 0, 6 * radius, height))}


def theme_txt(variant: str, scale: int, logo: Image.Image, item_font: str, label_font: str) -> str:
    return f"""# iGloo Community boot menu. Generated by scripts/generate_grub_theme.py; edit that, not this file.

title-text: ""
desktop-color: "{WHITE}"
terminal-font: "{TERMINAL_FONT[variant][1]}"
# The terminal takes the whole screen, so nothing of the menu stays behind its text.
terminal-left: "0"
terminal-top: "0"
terminal-width: "100%"
terminal-height: "100%"
terminal-border: "0"

+ image {{
  left = 50%-{logo.width // 2}
  top = {LOGO_TOP}%
  width = {logo.width}
  height = {logo.height}
  file = "logo.png"
}}

+ boot_menu {{
  left = {MENU_LEFT}%
  top = {MENU_TOP}%
  width = {MENU_WIDTH}%
  height = 46%
  item_font = "{item_font}"
  item_color = "{SLATE}"
  selected_item_color = "{INK}"
  icon_width = {px(ICON_SIZE, scale)}
  icon_height = {px(ICON_SIZE, scale)}
  item_icon_space = {px(ICON_SPACE, scale)}
  item_height = {px(ITEM_HEIGHT, scale)}
  item_padding = {px(PADDING, scale)}
  item_spacing = {px(SPACING, scale)}
  selected_item_pixmap_style = "select_*.png"
  scrollbar = false
}}

+ label {{
  left = 0
  top = 100%-{px(COUNTDOWN_UP, scale)}
  width = 100%
  align = "center"
  id = "__timeout__"
  text = "{COUNTDOWN}"
  color = "{MUTED}"
  font = "{label_font}"
}}

+ label {{
  left = 0
  top = 100%-{px(HINTS_UP, scale)}
  width = 100%
  align = "center"
  text = "{HINTS}"
  color = "{MUTED}"
  font = "{label_font}"
}}
"""


def write_archive(variant: str) -> Path:
    """Own files, the vendored icons and terminal font, packed with fixed times and owners."""
    files = {f"./{path.name}": path for path in sorted((OWN / variant).iterdir())}
    files["./montserrat-OFL.txt"] = OWN / "OFL.txt"
    files[f"./{TERMINAL_FONT[variant][0]}"] = VENDORED / variant / TERMINAL_FONT[variant][0]
    files.update({f"./icons/{path.name}": path for path in sorted((VENDORED / variant / "icons").iterdir())})
    raw = io.BytesIO()
    with tarfile.open(fileobj=raw, mode="w", format=tarfile.GNU_FORMAT) as tar:
        for name in ("./", "./icons/"):
            folder = tarfile.TarInfo(name)
            folder.type, folder.mode, folder.mtime = tarfile.DIRTYPE, 0o755, ARCHIVE_TIME
            tar.addfile(folder)
        for name in sorted(files):
            body = files[name].read_bytes()
            if name.endswith(".txt"):
                # A Windows checkout may hold these with CRLF; GRUB reads the theme byte for byte.
                body = body.replace(b"\r\n", b"\n")
            info = tarfile.TarInfo(name)
            info.size, info.mode, info.mtime = len(body), 0o644, ARCHIVE_TIME
            tar.addfile(info, io.BytesIO(body))
    target = THEMES / f"grub-theme-stylish-{variant}.tar.gz"
    with target.open("wb") as out, gzip.GzipFile(fileobj=out, mode="wb", filename="", mtime=0) as packed:
        packed.write(raw.getvalue())
    return target


def read_pf2(data: bytes) -> dict:
    """The parts of a PF2 file the preview draws with."""
    position, font = 0, {"glyphs": {}}
    while data[position:position + 4] != b"DATA":
        tag = data[position:position + 4].decode("ascii")
        length = struct.unpack(">I", data[position + 4:position + 8])[0]
        body = data[position + 8:position + 8 + length]
        if tag in ("ASCE", "DESC"):
            font[tag] = struct.unpack(">H", body)[0]
        elif tag == "CHIX":
            for start in range(0, length, 9):
                code, _, offset = struct.unpack(">IBI", body[start:start + 9])
                font["glyphs"][code] = struct.unpack(">HHhhh", data[offset:offset + 10]) + (offset + 10,)
        position += 8 + length
    return font


def draw_text(image: Image.Image, font: dict, data: bytes, text: str, left: int, baseline: int, color: str) -> int:
    """Draw with the PF2 bitmaps, placed as GRUB places them; returns the width used."""
    pen, color = left, ImageColor.getrgb(color)
    for char in text:
        width, height, x_offset, y_offset, advance, start = font["glyphs"].get(ord(char), font["glyphs"][0x3F])
        for row in range(height):
            for column in range(width):
                index = row * width + column
                x, y = pen + x_offset + column, baseline - (height + y_offset) + row
                if data[start + index // 8] & (0x80 >> (index % 8)) and 0 <= x < image.width and 0 <= y < image.height:
                    image.putpixel((x, y), color)
        pen += advance
    return pen - left


def preview(variant: str, scale: int, screen: tuple[int, int], entries: list[tuple[str, str | None]]) -> Image.Image:
    """The menu at one screen size, with the positions theme.txt gives; the first entry is selected."""
    folder = OWN / variant
    item_data = (folder / f"montserrat-semibold-{px(ITEM_SIZE, scale)}.pf2").read_bytes()
    label_data = (folder / f"montserrat-semibold-{px(LABEL_SIZE, scale)}.pf2").read_bytes()
    item_font, label_font = read_pf2(item_data), read_pf2(label_data)
    image = Image.new("RGB", screen, WHITE)
    logo = Image.open(folder / "logo.png")
    image.paste(logo, (screen[0] // 2 - logo.width // 2, screen[1] * LOGO_TOP // 100))
    left, top, menu = screen[0] * MENU_LEFT // 100, screen[1] * MENU_TOP // 100, screen[0] * MENU_WIDTH // 100
    padding, height, icon_size = px(PADDING, scale), px(ITEM_HEIGHT, scale), px(ICON_SIZE, scale)
    caps = [Image.open(folder / f"select_{part}.png") for part in "wce"]
    for number, (title, icon) in enumerate(entries):
        y = top + padding + number * (height + px(SPACING, scale))
        if number == 0:
            inner = menu - 2 * padding - caps[0].width - caps[2].width
            image.paste(caps[0], (left + padding, y), caps[0])
            image.paste(caps[1].resize((inner, height)), (left + padding + caps[0].width, y))
            image.paste(caps[2], (left + padding + caps[0].width + inner, y), caps[2])
        x = left + padding + caps[0].width
        if icon:
            picture = Image.open(VENDORED / variant / "icons" / icon).convert("RGBA")
            picture = picture.resize((icon_size, icon_size), Image.LANCZOS)
            image.paste(picture, (x, y + (height - icon_size) // 2), picture)
        baseline = y + (height - (item_font["ASCE"] + item_font["DESC"])) // 2 + item_font["ASCE"]
        draw_text(image, item_font, item_data, title, x + icon_size + px(ICON_SPACE, scale), baseline,
                  INK if number == 0 else SLATE)
    for text, up in ((COUNTDOWN.replace("%d", "8"), COUNTDOWN_UP), (HINTS, HINTS_UP)):
        width = draw_text(Image.new("RGB", (1, 1)), label_font, label_data, text, 0, 0, MUTED)
        draw_text(image, label_font, label_data, text, screen[0] // 2 - width // 2,
                  screen[1] - px(up, scale) + label_font["ASCE"], MUTED)
    return image


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--preview", type=Path)
    args = parser.parse_args()
    for variant, scale in VARIANTS.items():
        folder = OWN / variant
        folder.mkdir(parents=True, exist_ok=True)
        for stale in folder.iterdir():
            stale.unlink()
        logo = lockup(scale)
        logo.save(folder / "logo.png", optimize=True)
        for name, image in selection(scale).items():
            image.save(folder / name, optimize=True)
        names = []
        for size in (px(ITEM_SIZE, scale), px(LABEL_SIZE, scale)):
            name, data = pf2(size)
            (folder / f"montserrat-semibold-{size}.pf2").write_bytes(data)
            names.append(name)
        (folder / "theme.txt").write_text(theme_txt(variant, scale, logo, *names), encoding="ascii", newline="\n")
        print(write_archive(variant).relative_to(ROOT))
    if args.preview:
        args.preview.mkdir(parents=True, exist_ok=True)
        entries = [("Debian GNU/Linux", "debian.png"), ("Windows 11", "windows.png"),
                   ("UEFI Firmware Settings", None)]
        for variant, screen in (("1080p", (1024, 768)), ("1080p", (1920, 1080)),
                                ("4k", (1024, 768)), ("4k", (3840, 2160))):
            preview(variant, VARIANTS[variant], screen, entries).save(
                args.preview / f"grub-{variant}-{screen[0]}x{screen[1]}.png")


if __name__ == "__main__":
    main()
