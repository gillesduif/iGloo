"""The boot menu theme in the agent payload: what GRUB needs to load it is in each archive."""

import io
from pathlib import Path
import re
import struct
import tarfile
import unittest
import zlib


THEMES = Path(__file__).resolve().parents[2] / 'distros' / '_shared' / 'grub-theme'
VARIANTS = ('1080p', '4k')


def members(variant):
    with tarfile.open(THEMES / f'grub-theme-stylish-{variant}.tar.gz') as archive:
        return {member.name: (member, archive.extractfile(member).read() if member.isfile() else b'')
                for member in archive.getmembers()}


def sections(data):
    """The sections of a PF2 file in order, as GRUB's loader walks them."""
    position, found = 0, []
    while True:
        tag = data[position:position + 4].decode('ascii')
        length = struct.unpack('>I', data[position + 4:position + 8])[0]
        if tag == 'DATA':
            return found, position + 8
        found.append((tag, data[position + 8:position + 8 + length]))
        position += 8 + length


def pixels(data):
    """The rows of an 8-bit RGBA PNG without interlacing."""
    width, height, depth, colour = struct.unpack('>IIBB', data[16:26])
    assert (depth, colour) == (8, 6), (depth, colour)
    position, packed = 8, b''
    while position < len(data):
        length, tag = struct.unpack('>I4s', data[position:position + 8])
        if tag == b'IDAT':
            packed += data[position + 8:position + 8 + length]
        position += 12 + length
    raw, stride, rows, previous = zlib.decompress(packed), 4 * width, [], bytes(4 * width)
    for y in range(height):
        kind, line = raw[y * (stride + 1)], bytearray(raw[y * (stride + 1) + 1:(y + 1) * (stride + 1)])
        for x in range(stride):
            left = line[x - 4] if x >= 4 else 0
            up, corner = previous[x], previous[x - 4] if x >= 4 else 0
            nearest = min((left, up, corner), key=lambda value: abs(left + up - corner - value))
            line[x] = (line[x] + (0, left, up, (left + up) // 2, nearest)[kind]) & 0xFF
        rows.append([tuple(line[x:x + 4]) for x in range(0, stride, 4)])
        previous = bytes(line)
    return rows


def runs(filled):
    """How many unbroken stretches of filled rows or columns there are."""
    return sum(1 for index, value in enumerate(filled) if value and (index == 0 or not filled[index - 1]))


class GrubThemeFilesTest(unittest.TestCase):
    def test_the_archive_holds_the_checked_in_files_and_nothing_else(self):
        for variant in VARIANTS:
            with self.subTest(variant=variant):
                packed = members(variant)
                folder = THEMES / 'igloo' / variant
                own = {f'./{path.name}': path for path in folder.iterdir() if path.is_file()}
                own['./montserrat-OFL.txt'] = THEMES / 'igloo' / 'OFL.txt'
                vendored = THEMES / 'stylish' / variant
                own.update({f'./{path.name}': path for path in vendored.glob('terminus-*.pf2')})
                own.update({f'./icons/{path.name}': path for path in (vendored / 'icons').iterdir()})
                # An own icon takes the place of the vendored one with the same name.
                own.update({f'./icons/{path.name}': path for path in (folder / 'icons').iterdir()})
                self.assertEqual(set(own) | {'.', './icons'}, set(packed))
                for name, path in own.items():
                    expected = path.read_bytes()
                    if name.endswith('.txt'):
                        # A Windows checkout may hold the text files with CRLF; the archive never does.
                        expected = expected.replace(b'\r\n', b'\n')
                        self.assertNotIn(b'\r', packed[name][1], name)
                    self.assertEqual(expected, packed[name][1], name)
                for name, (member, _) in packed.items():
                    self.assertTrue(member.isfile() or member.isdir(), name)
                    self.assertNotIn('..', name.split('/'))

    def test_the_menu_is_white_and_shows_the_logo(self):
        for variant in VARIANTS:
            with self.subTest(variant=variant):
                theme = members(variant)['./theme.txt'][1].decode('ascii')
                self.assertIn('desktop-color: "#FFFFFF"', theme)
                self.assertNotIn('desktop-image', theme)
                self.assertIn('file = "logo.png"', theme)

    def test_every_file_and_font_the_theme_names_is_in_the_archive(self):
        for variant in VARIANTS:
            with self.subTest(variant=variant):
                packed = members(variant)
                theme = packed['./theme.txt'][1].decode('ascii')
                for name in re.findall(r'file = "([^"]+)"', theme):
                    self.assertIn(f'./{name}', packed)
                for pattern in re.findall(r'(?:pixmap_style = |terminal-box: )"([^"]+)"', theme):
                    prefix, suffix = pattern.split('*')
                    slices = [name for name in packed if name.startswith(f'./{prefix}') and name.endswith(suffix)]
                    self.assertIn(f'./{prefix}c{suffix}', slices, pattern)
                loaded = {dict(sections(data)[0])['NAME'].rstrip(b'\0').decode('ascii')
                          for name, (_, data) in packed.items() if name.endswith('.pf2')}
                wanted = set(re.findall(r'(?:item_font|font|terminal-font)(?: = |: )"([^"]+)"', theme))
                self.assertTrue(wanted)
                self.assertLessEqual(wanted, loaded)

    def test_the_fonts_pass_the_checks_of_the_grub_loader(self):
        for variant in VARIANTS:
            for name, (_, data) in members(variant).items():
                if not name.startswith('./montserrat-') or not name.endswith('.pf2'):
                    continue
                with self.subTest(variant=variant, font=name):
                    found, glyphs_start = sections(data)
                    self.assertEqual(('FILE', b'PFF2'), found[0])
                    fields = dict(found)
                    numbers = {tag: struct.unpack('>H', fields[tag])[0] for tag in ('MAXW', 'MAXH', 'ASCE', 'DESC')}
                    self.assertTrue(all(numbers.values()), numbers)
                    index = fields['CHIX']
                    self.assertEqual(0, len(index) % 9)
                    codes = []
                    for start in range(0, len(index), 9):
                        code, flags, offset = struct.unpack('>IBI', index[start:start + 9])
                        codes.append(code)
                        self.assertEqual(0, flags)
                        self.assertGreaterEqual(offset, glyphs_start)
                        width, height, _, _, advance = struct.unpack('>HHhhh', data[offset:offset + 10])
                        self.assertLessEqual(offset + 10 + (width * height + 7) // 8, len(data))
                        # GRUB 2.12 refuses a glyph larger than the font declares.
                        self.assertLessEqual(width, numbers['MAXW'])
                        self.assertLessEqual(height, numbers['MAXH'])
                        self.assertGreaterEqual(advance, 0)
                    self.assertEqual(sorted(set(codes)), codes)
                    self.assertLessEqual(set(range(0x20, 0x7F)), set(codes))

    def test_the_windows_icon_is_four_ink_squares_at_the_size_of_the_menu(self):
        for variant in VARIANTS:
            with self.subTest(variant=variant):
                packed = members(variant)
                theme = packed['./theme.txt'][1].decode('ascii')
                size = int(re.search(r'icon_width = (\d+)', theme).group(1))
                rows = pixels(packed['./icons/windows.png'][1])
                self.assertEqual((size, size), (len(rows[0]), len(rows)))
                # Every pixel is the ink of the menu text or fully transparent; no other colour is in it.
                self.assertEqual({(0x10, 0x10, 0x12, 255), (0, 0, 0, 0)},
                                 {pixel for row in rows for pixel in row})
                self.assertEqual([2, 2], [runs([any(pixel[3] for pixel in row) for row in rows]),
                                          runs([any(row[x][3] for row in rows) for x in range(size)])])

    def test_the_images_are_in_a_form_grub_decodes(self):
        for variant in VARIANTS:
            for name, (_, data) in members(variant).items():
                if not name.endswith('.png') or (name.startswith('./icons/') and name != './icons/windows.png'):
                    continue
                with self.subTest(variant=variant, image=name):
                    self.assertEqual(b'\x89PNG\r\n\x1a\n', data[:8])
                    width, height, depth, colour, _, _, interlace = struct.unpack('>IIBBBBB', data[16:29])
                    self.assertEqual((8, 0), (depth, interlace))
                    self.assertIn(colour, (2, 6))
                    self.assertTrue(width and height)

    def test_the_logo_has_the_size_the_theme_gives_it_and_fits_a_small_firmware_mode(self):
        # The variant follows the panel; the firmware may still give GRUB no more than this.
        for variant, smallest in (('1080p', (640, 480)), ('4k', (1024, 768))):
            with self.subTest(variant=variant):
                packed = members(variant)
                theme = packed['./theme.txt'][1].decode('ascii')
                image = re.search(r'\+ image \{(.*?)\}', theme, re.DOTALL).group(1)
                declared = tuple(int(re.search(rf'{key} = (\d+)', image).group(1)) for key in ('width', 'height'))
                self.assertEqual(declared, struct.unpack('>II', packed['./logo.png'][1][16:24]))
                self.assertLessEqual(declared[0], smallest[0])
                self.assertIn(f'left = 50%-{declared[0] // 2}', image)
                menu = re.search(r'\+ boot_menu \{(.*?)\}', theme, re.DOTALL).group(1)
                # Three entries, their spacing and the padding fit the height the menu gets.
                numbers = {key: int(re.search(rf'{key} = (\d+)', menu).group(1))
                           for key in ('item_height', 'item_spacing', 'item_padding')}
                share = int(re.search(r'height = (\d+)%', menu).group(1))
                needed = 3 * numbers['item_height'] + 2 * numbers['item_spacing'] + 2 * numbers['item_padding']
                self.assertLessEqual(needed, smallest[1] * share // 100)
                self.assertRegex(menu, r'width = \d+%')

    def test_the_terminal_covers_the_whole_screen(self):
        for variant in VARIANTS:
            with self.subTest(variant=variant):
                theme = members(variant)['./theme.txt'][1].decode('ascii')
                for line in ('terminal-left: "0"', 'terminal-top: "0"',
                             'terminal-width: "100%"', 'terminal-height: "100%"'):
                    self.assertIn(line, theme)
                self.assertNotIn('terminal-box', theme)


if __name__ == '__main__':
    unittest.main()
