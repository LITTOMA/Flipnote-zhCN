"""Verify the actual CIA font independently, including all pixels and dynamic text coverage.

Pillow is used only to read authored font sheets. --before accepts a temporary
copy of the preceding manifest/sheets for additional preservation comparisons.
"""
from pathlib import Path
import argparse
import hashlib
import json
import struct
import subprocess
import tempfile
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
FONT = ROOT / 'overlays/zh-CN/fonts'
CLI = ['dotnet', str(ROOT / 'src/Flipnote.Build/bin/Release/net10.0/Flipnote.Build.dll')]


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def sha(data):
    return hashlib.sha256(data).hexdigest().upper()


def managed(c):
    n = ord(c)
    return n == 0x3007 or 0x3400 <= n <= 0x4dbf or 0x4e00 <= n <= 0x9fff or 0xf900 <= n <= 0xfaff or c.isascii() and c.isalpha() or 0xff21 <= n <= 0xff3a or 0xff41 <= n <= 0xff5a


class Font:
    def __init__(self, directory):
        self.directory = directory
        self.data = read(directory / 'font_CN_manifest.json')
        self.map = self.data['glyphMap']
        self.widths = self.data['glyphWidths']
        self.info = self.data['textureInfo']
        self.grid = self.info['sheetInfo']
        self.pages = [Image.open(directory / f'font_CN_sheet{i}.png').getchannel('A')
                      for i in range(self.info['sheetCount'])]

    def cell(self, c):
        index = self.map[c]
        page, slot = divmod(index, self.grid['cols'] * self.grid['rows'])
        gw, gh = self.info['glyph']['width'], self.info['glyph']['height']
        x, y = slot % self.grid['cols'] * (gw + 1) + 1, slot // self.grid['cols'] * (gh + 1) + 1
        return self.pages[page].crop((x, y, x + gw, y + gh))

    def record(self, c):
        metrics = self.widths[str(self.map[c])]
        return dict(index=self.map[c], left=metrics['left'], glyph=metrics['glyph'], advance=metrics['char'],
                    alphaSha256=sha(self.cell(c).point(lambda a: a // 17 * 17).tobytes()))

    def line(self, text):
        fallback = next(c for c, i in self.map.items() if i == self.data['fontInfo']['alterCharIdx'])
        text = ''.join(c if c in self.map else fallback for c in text)
        width = sum(self.widths[str(self.map[c])]['char'] for c in text)
        canvas = Image.new('RGBA', (width + 4, 30))
        x = 2
        for c in text:
            m = self.widths[str(self.map[c])]
            mask = self.cell(c).point(lambda a: a // 17 * 17)
            # Only the CWDH glyph width is visible to the runtime.
            mask = mask.crop((0, 0, m['glyph'], mask.height))
            ink = Image.new('RGBA', mask.size, '#ff6600')
            ink.putalpha(mask)
            canvas.alpha_composite(ink, (x + m['left'], 0))
            x += m['char']
        return canvas


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--before', type=Path)
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    font = Font(FONT)
    policy = read(FONT / 'glyph-unification.json')
    settings = read(ROOT / 'project.json')
    output = args.output.resolve() if args.output else ROOT / settings['output']
    with tempfile.TemporaryDirectory(prefix='font-verify-', dir=ROOT / '.work') as temporary:
        file = Path(temporary) / 'font.bcfnt'
        subprocess.run([*CLI, 'read', 'content/0000/romfs/font_CN.bcfnt', '--input', str(output), '--output', str(file)],
                       cwd=ROOT, check=True, capture_output=True)
        raw = file.read_bytes()
    u16 = lambda off: struct.unpack_from('<H', raw, off)[0]
    u32 = lambda off: struct.unpack_from('<I', raw, off)[0]
    assert raw[:4] == b'CFNT' and u32(12) == len(raw)
    tglp, cwdh, cmap = (u32(off) - 8 for off in (0x24, 0x28, 0x2c))
    assert raw[tglp:tglp + 4] == b'TGLP' and raw[cwdh:cwdh + 4] == b'CWDH' and raw[cmap:cmap + 4] == b'CMAP'
    assert u16(cmap + 12) == 2 and u32(cmap + 16) == 0
    count = u16(cmap + 20)
    mapping = {chr(u16(cmap + 22 + i * 4)): u16(cmap + 24 + i * 4) for i in range(count)}
    assert mapping == font.map
    assert u16(cwdh + 8) == 0 and u16(cwdh + 10) == len(font.widths) - 1 and u32(cwdh + 12) == 0
    for i in range(len(font.widths)):
        left, glyph, advance = struct.unpack_from('<bBB', raw, cwdh + 16 + i * 3)
        assert font.widths[str(i)] == dict(left=left, glyph=glyph, char=advance)
    assert u16(tglp + 18) == 11
    size, pages = u32(tglp + 12), u16(tglp + 16)
    width, height, offset = u16(tglp + 24), u16(tglp + 26), u32(tglp + 28)
    assert (pages, width, height) == (len(font.pages), font.grid['width'], font.grid['height'])
    assert tuple(raw[tglp + 8:tglp + 11]) == tuple(font.info['glyph'][k] for k in ('width', 'height', 'baseline'))
    assert (u16(tglp + 20), u16(tglp + 22)) == (font.grid['cols'], font.grid['rows'])
    order = [(x, y) for y in range(8) for x in range(8)]
    order.sort(key=lambda p: sum(((p[0] >> b) & 1) << (b * 2) | ((p[1] >> b) & 1) << (b * 2 + 1) for b in range(3)))
    for p in range(pages):
        linear = bytearray(width * height)
        index = 0
        for ty in range(0, height, 8):
            for tx in range(0, width, 8):
                for x, y in order:
                    alpha = (raw[offset + p * size + index // 2] >> (index % 2 * 4) & 15) * 17
                    linear[(ty + y) * width + tx + x] = alpha
                    index += 1
        assert bytes(linear) == font.pages[p].point(lambda a: a // 17 * 17).tobytes(), f'Font texture {p} differs'
    for c in font.map:
        expected = (policy['unified'] if managed(c) else policy['preserved'])[c]
        assert font.record(c) == expected
        if managed(c):
            mask = font.cell(c).point(lambda a: a // 17 * 17)
            assert mask.getbbox(), f'Empty glyph U+{ord(c):04X}'
            assert mask.getbbox()[2] <= font.widths[str(font.map[c])]['glyph'], f'Truncated CWDH width U+{ord(c):04X}'
    common = {bytes((lead, trail)).decode('gb2312') for lead in range(0xb0, 0xf8) for trail in range(0xa1, 0xff)
              if not (lead == 0xd7 and trail >= 0xfa)}
    assert len(common) == 6763 and common <= font.map.keys()
    assert all(chr(c) in font.map for c in range(0x20, 0x7f))
    report = dict(status='passed', output=settings['output'], outputSha256=sha(output.read_bytes()),
                  fontFamily=policy['family'], fontPixelSize=24, sourceFontSha256=policy['sourceFontSha256'],
                  binaryBytes=len(raw), binarySha256=sha(raw), totalGlyphs=len(font.map), unifiedGlyphs=len(policy['unified']),
                  preservedOrNewSymbolGlyphs=len(policy['preserved']), gb2312HanCovered=len(common),
                  asciiPrintableCovered=95, englishLetters=52, binaryGlyphMapExact=True, binaryWidthsExact=True,
                  texturePixelsCompared=pages * width * height, texturePixelsDifferent=0,
                  allUnifiedGlyphsNonempty=True, runtimeTested=False,
                  limitations=['GB2312 covers common Chinese names, not every Unicode character or rare ideograph.'])
    if args.before:
        old = Font(args.before)
        preserved = [c for c in old.map if not managed(c)]
        for c in preserved:
            assert old.record(c) == font.record(c)
            assert old.cell(c).tobytes() == font.cell(c).tobytes()
        assert old.data['fontInfo'] == font.data['fontInfo']
        assert old.info['glyph'] == font.info['glyph'] and old.grid == font.grid
        assert all(old.map[c] == font.map[c] for c in old.map)
        report.update(existingGlyphs=len(old.map), existingGlyphIndicesPreserved=True, existingNonHanNonLatinPreserved=len(preserved),
                      addedGlyphs=len(font.map) - len(old.map), originalFontMetricsPreserved=True,
                      changedExistingHanAndLatin=sum(old.record(c) != font.record(c) for c in old.map if managed(c)))
        samples = ['这个有点特别的绘画本', '还能制作翻页动画哦', '动画记事本', 'ABCDEFGHIJKLMN', 'OPQRSTUVWXYZ', 'abcdefghijklmn', 'opqrstuvwxyz', '0123456789abcdef']
        preview = Image.new('RGBA', (660, len(samples) * 40 + 32), 'white')
        draw = ImageDraw.Draw(preview)
        draw.text((12, 6), 'BEFORE', fill='black'); draw.text((342, 6), 'UNIFIED', fill='black')
        for row, text in enumerate(samples):
            preview.alpha_composite(old.line(text), (10, 30 + row * 40))
            preview.alpha_composite(font.line(text), (340, 30 + row * 40))
        review = ROOT / 'artifacts/font-review'
        review.mkdir(exist_ok=True)
        preview.convert('RGB').save(review / 'unified-comparison.png')
    # Review all unified glyphs as native 24x25 cells, without creating thousands of files.
    review = ROOT / 'artifacts/font-review'
    review.mkdir(exist_ok=True)
    chars = sorted(policy['unified'])
    for page, start in enumerate(range(0, len(chars), 800)):
        subset = chars[start:start + 800]
        sheet = Image.new('RGBA', (800, ((len(subset) + 24) // 25) * 32), 'white')
        for i, c in enumerate(subset):
            ink = Image.new('RGBA', (24, 25), 'black'); ink.putalpha(font.cell(c))
            sheet.alpha_composite(ink, (i % 25 * 32 + 4, i // 25 * 32 + 3))
        sheet.convert('RGB').save(review / f'characters-{page + 1:02}.png')
    (ROOT / 'artifacts/font-verification.json').write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(report, ensure_ascii=True))


if __name__ == '__main__':
    main()
