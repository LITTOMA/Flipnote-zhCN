"""Test native entry points; --input adds full CIA and Pillow pixel checks."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[1]

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--input', type=Path)
    parser.add_argument('--expect-architecture', choices=['x64', 'arm64'])
    args = parser.parse_args()
    original = args.input.resolve() if args.input else None
    work = ROOT / '.work'
    work.mkdir(exist_ok=True)
    entry = [str(ROOT / 'note.cmd')] if os.name == 'nt' else ['sh', str(ROOT / 'note.sh')]
    with tempfile.TemporaryDirectory(prefix='caller 中文 space-', dir=work) as caller:
        def run(*arguments, ok=True):
            result = subprocess.run([*entry, *map(str, arguments)], cwd=caller, capture_output=True, encoding='utf-8')
            if (result.returncode == 0) != ok:
                raise RuntimeError(f'{arguments}: exit {result.returncode}\n{result.stdout}\n{result.stderr}')
            return result.stdout

        platform = json.loads(run('platform-check'))
        assert platform['verified']
        if args.expect_architecture:
            assert platform['architecture'].lower() == args.expect_architecture, 'Wrong native runtime architecture'
        assert 'Flipnote-zhCN CLI' in run('help')
        run('unknown-command', ok=False)
        run('plan', '--input', str(Path(caller) / 'missing 原版.cia'), ok=False)
        if original:
            before = hashlib.sha256(original.read_bytes()).hexdigest()
            json.loads(run('plan', '--input', original))
            first = json.loads(run('build', '--input', original))
            second = json.loads(run('build', '--input', original))
            assert first['outputSha256'] == second['outputSha256']
            verified = json.loads(run('verify', '--input', original))
            assert verified['sha256'] == first['outputSha256']
            tests = json.loads(run('selftest', '--input', original))
            assert tests['outputSha256'] == first['outputSha256']
            for script, extra in [('verify-font.py', []), ('verify-ui-images.py', ['--input', str(original)]), ('verify-banner.py', ['--input', str(original)])]:
                result = subprocess.run([sys.executable, str(ROOT/'scripts'/script), *extra], cwd=ROOT, capture_output=True, encoding='utf-8')
                if result.returncode != 0:
                    raise RuntimeError(script + ': ' + result.stdout + result.stderr)
            assert hashlib.sha256(original.read_bytes()).hexdigest() == before
            platform.update(outputSha256=first['outputSha256'], outputBytes=first['outputBytes'],
                            resources=first['resourceCount'], selftests=tests['passed'],
                            independentPixelChecks=['font','UI textures','banner'], reproducible=True)
    platform.update(entryPointVerified=True, unicodeSpacedCallerDirectory=True, errorExitCodesVerified=True)
    artifact = ROOT/'artifacts/platform-validation.json'
    artifact.parent.mkdir(exist_ok=True)
    artifact.write_text(json.dumps(platform, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(platform, ensure_ascii=True, indent=2))

if __name__ == '__main__':
    main()
