"""Fit the selected generated lettering to the game's original texture canvases.

Requires Pillow and the built Flipnote.Build CLI. This is an authoring step only;
normal CIA builds consume the four checked-in PNG files directly.
"""
from pathlib import Path
import argparse
import hashlib
import json
import subprocess
import tempfile
from PIL import Image, ImageOps

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
CLI = ['dotnet', str(ROOT / 'src/Flipnote.Build/bin/Release/net10.0/Flipnote.Build.dll')]
PACK = 'content/0000/romfs/TexturePack/JP/JPTexturePack.blz!/timg/'
NAMES = ('TitleLogo1', 'TitleLogo2', 'TitleLogo2Sha', 'TitleLogo3', 'TitleLogo3Sha', 'TheaterTitleLogo')


def ink(name):
    # Discard nearly white background variation; keep antialiased lettering edges.
    source = Image.open(HERE / name).convert('L')
    mask = ImageOps.invert(source).point(lambda a: 0 if a < 24 else min(255, round((a - 24) * 255 / 223)))
    return mask.crop(mask.getbbox())


def fitted(mask, bounds):
    result = mask.copy()
    result.thumbnail(bounds, Image.Resampling.LANCZOS)
    return result.point(lambda a: ((a + 8) // 17) * 17)


def white(mask):
    result = Image.new('RGBA', mask.size, 'white')
    result.putalpha(mask)
    return result


def gallery(mask, top, height):
    small = fitted(mask, (227, height))
    canvas = Image.new('L', (256, 128))
    canvas.paste(small, ((256 - small.width) // 2, top + (height - small.height) // 2))
    return white(canvas)


def composite(first, second, originals):
    canvas = Image.new('RGBA', (384, 240), '#ff5500')
    shadow = Image.new('RGBA', originals['TitleLogo2Sha'].size, '#c84000')
    shadow.putalpha(originals['TitleLogo2Sha'].getchannel('A'))
    canvas.alpha_composite(shadow, (256, 0))
    canvas.alpha_composite(first)
    canvas.alpha_composite(second, (256, 0))
    shadow = Image.new('RGBA', originals['TitleLogo3Sha'].size, '#c84000')
    shadow.putalpha(originals['TitleLogo3Sha'].getchannel('A'))
    canvas.alpha_composite(shadow, (64, 100))
    canvas.alpha_composite(originals['TitleLogo3'], (64, 100))
    return canvas.convert('RGB')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", type=Path)
    args = parser.parse_args()
    input_args = ["--input", str(args.input.resolve())] if args.input else []
    scratch = ROOT / '.work'
    scratch.mkdir(exist_ok=True)
    originals = {}
    with tempfile.TemporaryDirectory(prefix='ui-author-', dir=scratch) as temporary:
        for name in NAMES:
            path = Path(temporary) / (name + '.png')
            subprocess.run([*CLI, 'texture', PACK + name + '.bclim', '--output', str(path), *input_args], cwd=ROOT, check=True, capture_output=True)
            with Image.open(path) as source:
                originals[name] = source.convert('RGBA')

    title = Image.new('RGBA', (384, 128), (255, 255, 255, 0))
    title.alpha_composite(originals['TitleLogo2'], (256, 0))
    # Only the Japanese tail occupies x < 30 here; original 3D and TM lie to its right.
    title.paste((255, 255, 255, 0), (256, 0, 286, 128))
    lettering = fitted(ink('title-lettering-source.png'), (276, 61))
    title.alpha_composite(white(lettering), (3, 29 + (61 - lettering.height) // 2))
    outputs = {
        'TitleLogo1': title.crop((0, 0, 256, 128)),
        'TitleLogo2': title.crop((256, 0, 384, 128)),
        'TheaterTitleLogo': gallery(ink('gallery-lettering-source.png'), 30, 74),
        'TheaterTitleLogoJp': gallery(ink('gallery-lettering-source.png'), 16, 88),
    }
    for name, image in outputs.items():
        image.save(HERE / (name + '.png'), optimize=True)

    review = ROOT / 'artifacts/ui-review'
    review.mkdir(parents=True, exist_ok=True)
    comparison = Image.new('RGB', (768, 368), '#64c800')
    comparison.paste(composite(originals['TitleLogo1'], originals['TitleLogo2'], originals), (0, 0))
    comparison.paste(composite(outputs['TitleLogo1'], outputs['TitleLogo2'], originals), (384, 0))
    comparison.paste(originals['TheaterTitleLogo'], (64, 240), originals['TheaterTitleLogo'])
    comparison.paste(outputs['TheaterTitleLogo'], (448, 240), outputs['TheaterTitleLogo'])
    comparison.save(review / 'logo-comparison.png')
    comparison.resize((1536, 736), Image.Resampling.NEAREST).save(review / 'logo-comparison-2x.png')
    records = [dict(file=p.name, sha256=hashlib.sha256(p.read_bytes()).hexdigest().upper())
               for p in sorted(HERE.glob('*.png'))]
    (HERE / 'asset-hashes.json').write_text(json.dumps(records, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(outputs=list(outputs), review=str(review / 'logo-comparison.png'))))


if __name__ == '__main__':
    main()
