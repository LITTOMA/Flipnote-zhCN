"""Independently decode final A4/LA4 BCLIM pixels and verify the localized CIA.

Uses Pillow and the CLI only for container access, not texture decoding.
"""
from pathlib import Path
import argparse
import hashlib
import io
import json
import struct
import subprocess
import tempfile
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
CLI = ['dotnet', str(ROOT / 'src/Flipnote.Build/bin/Release/net10.0/Flipnote.Build.dll')]
ASSETS = ROOT / 'overlays/zh-CN/images'


def digest(data):
    return hashlib.sha256(data).hexdigest().upper()


def cli(*args):
    result = subprocess.run([*CLI, *map(str, args)], cwd=ROOT, capture_output=True, check=True, encoding='utf-8')
    return json.loads(result.stdout) if result.stdout.strip() else None


def kwz_sections(data):
    """Read only section boundaries; pixel decoding remains the CLI's job."""
    result = {}
    offset = 0
    while offset < len(data) - 256:
        size = struct.unpack_from('<I', data, offset + 4)[0]
        end = offset + 8 + size
        assert end <= len(data) - 256
        name = data[offset:offset + 3].decode('ascii')
        assert name not in result
        result[name] = data[offset:end]
        offset = end
    assert offset == len(data) - 256
    return result


def decode(data):
    footer = data[-40:]
    assert footer[:4] == b'CLIM' and footer[20:24] == b'imag'
    width, height, fmt, size = struct.unpack_from('<HHII', footer, 28)
    assert fmt in (2, 13) and size == len(data) - 40
    # Generate a Morton table independently of the production codec's expression.
    order = [(x, y) for y in range(8) for x in range(8)]
    order.sort(key=lambda xy: sum(((xy[0] >> i) & 1) << (i * 2) |
                                 ((xy[1] >> i) & 1) << (i * 2 + 1) for i in range(3)))
    image = Image.new('RGBA', (width, height))
    index = 0
    for ty in range(0, height, 8):
        for tx in range(0, width, 8):
            for x, y in order:
                if fmt == 13:
                    alpha, light = (data[index // 2] >> (index % 2 * 4)) & 15, 15
                else:
                    alpha, light = data[index] & 15, data[index] >> 4
                image.putpixel((tx + x, ty + y), (light * 17,) * 3 + (alpha * 17,))
                index += 1
    return image, fmt


def main():
    settings = json.loads((ROOT / 'project.json').read_text(encoding='utf-8-sig'))
    parser = argparse.ArgumentParser()
    parser.add_argument('--input', type=Path, default=ROOT / settings['input'])
    parser.add_argument('--output', type=Path, default=ROOT / settings['output'])
    args = parser.parse_args()
    output = args.output.resolve()
    manifest = json.loads((ROOT / settings['overlay']).read_text(encoding='utf-8-sig'))
    changes = [op for op in manifest['operations'] if op['kind'] == 'bclim']
    assert len(changes) == 4
    original_input = args.input.resolve()
    report = dict(schemaVersion=1, output=settings['output'], outputSha256=digest(output.read_bytes()),
                  decoder='Independent Python Morton-table A4/LA4 decoder; CLI used only to access containers',
                  images=[], unchangedTextures=[], runtimeTested=False)
    with tempfile.TemporaryDirectory(prefix='ui-verify-', dir=ROOT / '.work') as temporary:
        target_file = Path(temporary) / 'texture.bclim'

        def resource(path, localized):
            args = ['read', path, '--output', target_file]
            args += ['--input', output if localized else original_input]
            cli(*args)
            return target_file.read_bytes()

        for op in changes:
            name = Path(op['target']).stem
            old, new = resource(op['target'], False), resource(op['target'], True)
            actual, fmt = decode(new)
            source_path = ASSETS / (name + '.png')
            source = Image.open(source_path).convert('RGBA')
            assert source.size == actual.size
            expected = bytes(v for p in source.getdata()
                             for v in ((255, 255, 255) if fmt == 13 else tuple(((c + 8) // 17) * 17 for c in p[:3])) + (((p[3] + 8) // 17) * 17,))
            assert actual.tobytes() == expected, name + ': decoded pixels differ from PNG'
            assert old[-40:] == new[-40:], name + ': original footer changed'
            assert digest(old) == op['baseSha256']
            if name == 'TitleLogo2':
                original, _ = decode(old)
                assert original.crop((30, 0, 128, 128)).tobytes() == actual.crop((30, 0, 128, 128)).tobytes()
                report['original3dAndTmPixelsPreserved'] = True
            report['images'].append(dict(target=op['target'], source=op['source'], originalSha256=digest(old),
                                          outputSha256=digest(new), pngSha256=digest(source_path.read_bytes()),
                                          width=actual.width, height=actual.height, format='A4' if fmt == 13 else 'LA4',
                                          pixelsCompared=actual.width * actual.height, differingPixels=0, originalFooterPreserved=True))
        prefix = 'content/0000/romfs/TexturePack/JP/JPTexturePack.blz!/timg/'
        for name in ('TitleLogo2Sha', 'TitleLogo3', 'TitleLogo3Sha'):
            path = prefix + name + '.bclim'
            old, new = resource(path, False), resource(path, True)
            assert old == new
            report['unchangedTextures'].append(dict(target=path, sha256=digest(new)))

    report['allChecksPassed'] = True
    (ROOT / 'artifacts/ui-images-verification.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(verified=True, textures=len(changes), outputSha256=report['outputSha256'])))


if __name__ == '__main__':
    main()
