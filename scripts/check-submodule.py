"""Exercise dependency initialization and failure paths using disposable local Git repositories."""
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]


def main():
    work = ROOT / '.work'
    work.mkdir(exist_ok=True)
    env = dict(os.environ, GIT_ALLOW_PROTOCOL='file', GIT_TERMINAL_PROMPT='0')
    checks = []

    def run(cwd, *args, ok=True):
        result = subprocess.run(list(map(str, args)), cwd=cwd, env=env,
                                capture_output=True, encoding='utf-8')
        if (result.returncode == 0) != ok:
            raise RuntimeError(f'{args}: {result.returncode}\n{result.stdout}\n{result.stderr}')
        return result.stdout + result.stderr

    def init(path):
        path.mkdir()
        run(path, 'git', 'init', '-b', 'main')
        run(path, 'git', 'config', 'user.name', 'Dependency test')
        run(path, 'git', 'config', 'user.email', 'test@example.invalid')
        run(path, 'git', 'config', 'core.autocrlf', 'false')

    def commit(path):
        run(path, 'git', 'add', '.')
        run(path, 'git', 'commit', '-m', 'Test fixture')
        return run(path, 'git', 'rev-parse', 'HEAD').strip()

    scratch = Path(tempfile.mkdtemp(prefix='submodule 中文 space-', dir=work))
    try:
        lib = scratch / 'library'
        init(lib)
        (lib / 'src').mkdir()
        (lib / '.gitignore').write_text('bin/\nobj/\n', encoding='utf-8')
        (lib / 'src/libkwz.csproj').write_text('<Project />\n', encoding='utf-8')
        key = lib / 'src/resource.bin'
        key.write_bytes(b'fixture v1')
        first = commit(lib)
        key.write_bytes(b'fixture v2')
        pinned = commit(lib)

        seed = scratch / 'parent'
        init(seed)
        (seed / 'project.json').write_text('{}\n', encoding='utf-8')
        (seed / '.gitignore').write_text('bin/\n', encoding='utf-8')
        run(seed, 'git', 'submodule', 'add', lib.as_uri(), 'external/libkwz')
        commit(seed)

        bootstrap = ROOT / 'src/Flipnote.Bootstrap/bin/Release/net10.0'
        if not (bootstrap / 'Flipnote.Bootstrap.dll').is_file():
            run(ROOT, 'dotnet', 'build', ROOT / 'src/Flipnote.Bootstrap', '-c', 'Release', '--nologo')

        def clone(name):
            project = scratch / name
            run(scratch, 'git', 'clone', str(seed), str(project))
            shutil.copytree(bootstrap, project / 'bin')
            return project

        def boot(project, ok=True, mode='dependencies', message=''):
            output = run(scratch, 'dotnet', project / 'bin/Flipnote.Bootstrap.dll', mode, ok=ok)
            if message and message not in output:
                raise AssertionError(output)

        project = clone('clone 中文')
        sub = project / 'external/libkwz'
        boot(project)
        assert run(sub, 'git', 'rev-parse', 'HEAD').strip() == pinned
        checks.append('Uninitialized submodule restores the indexed commit from a Unicode/spaced path')
        boot(project)
        checks.append('Clean pinned submodule can be reused')
        run(sub, 'git', 'checkout', '--detach', first)
        boot(project, ok=False, message='expected ' + pinned)
        assert run(sub, 'git', 'rev-parse', 'HEAD').strip() == first
        checks.append('Wrong revision is rejected without changing HEAD')
        run(sub, 'git', 'checkout', '--detach', pinned)
        resource = sub / 'src/resource.bin'
        resource.write_bytes(b'user edits')
        boot(project, ok=False, message='local changes')
        assert resource.read_bytes() == b'user edits'
        checks.append('Modified tracked resource is rejected and preserved')
        boot(project, ok=False, mode='note', message='local changes')
        checks.append('Normal CLI entry checks dependency before tool build or cache reuse')
        resource.write_bytes(b'fixture v2')
        untracked = sub / 'src/new.cs'
        untracked.write_text('// unsaved source', encoding='utf-8')
        boot(project, ok=False, message='untracked files')
        assert untracked.exists()
        checks.append('Untracked source is rejected and preserved')
        untracked.unlink()
        (sub / 'bin').mkdir()
        (sub / 'bin/build.tmp').write_bytes(b'ignored output')
        boot(project)
        checks.append('Ignored build outputs do not mark dependency dirty')
        run(project, 'git', 'update-index', '--force-remove', 'external/libkwz')
        boot(project, ok=False, message='gitlink')
        checks.append('Missing gitlink is rejected')
        run(project, 'git', 'update-index', '--add', '--cacheinfo', f'160000,{first},external/libkwz')
        boot(project, ok=False, message='expected ' + first)
        checks.append('Staged gitlink is authoritative rather than branch tip or duplicate lock')
        run(sub, 'git', 'checkout', '--detach', first)
        boot(project)
        checks.append('Explicit dependency update is accepted after HEAD and gitlink agree')
        occupied = clone('occupied')
        sentinel = occupied / 'external/libkwz/user.txt'
        sentinel.parent.mkdir(parents=True, exist_ok=True)
        sentinel.write_bytes(b'preserve me')
        boot(occupied, ok=False, message='contains files')
        assert sentinel.read_bytes() == b'preserve me'
        checks.append('Nonempty uninitialized directory is preserved')
        archive = scratch / 'source zip'
        archive.mkdir()
        for file in ['project.json', '.gitmodules']:
            shutil.copy2(project / file, archive / file)
        shutil.copytree(bootstrap, archive / 'bin')
        boot(archive, ok=False, message='Git clone with submodules')
        checks.append('Source archive without Git metadata gives actionable error')
    finally:
        assert scratch.resolve().parent == work.resolve()
        def writable_retry(function, path, error):
            os.chmod(path, stat.S_IWRITE | stat.S_IREAD)
            function(path)
        shutil.rmtree(scratch, onerror=writable_retry)
    checks.append('Disposable repositories and Git objects are removed')
    report = {'passed': len(checks), 'checks': checks}
    output = ROOT / 'artifacts/submodule-checks.json'
    output.parent.mkdir(exist_ok=True)
    output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
