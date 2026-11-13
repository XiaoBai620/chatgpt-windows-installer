"""Build self-contained Windows JScript launchers without third-party dependencies."""
import argparse
import hashlib
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile

VERSION = '1.1.0'
ROOT = Path(__file__).resolve().parent
HEADER = '''@if (@Batch)==(@JScript) @end /*
@echo off
setlocal DisableDelayedExpansion
chcp 65001 >nul
set "CHATGPT_CSCRIPT=%SystemRoot%\\System32\\cscript.exe"
if exist "%SystemRoot%\\Sysnative\\cscript.exe" set "CHATGPT_CSCRIPT=%SystemRoot%\\Sysnative\\cscript.exe"
if not exist "%CHATGPT_CSCRIPT%" goto missing_host
"%CHATGPT_CSCRIPT%" //nologo //E:JScript "%~f0" %*
exit /b %errorlevel%
:missing_host
echo Windows Script Host is unavailable. Ask your system administrator for help.
echo This tool does not change Windows policies or require Python or Node.js.
pause
exit /b 2
*/
'''


def build(destination):
    destination.mkdir(parents=True, exist_ok=True)
    body = (ROOT / 'installer.js').read_text(encoding='utf-8')
    # JScript source is UTF-8 for maintainers; generated scripts use ASCII Unicode
    # escapes so Windows Script Host does not depend on the machine's code page.
    body = ''.join(c if ord(c) < 128 else '\\u%04x' % ord(c) for c in body)
    payload = (HEADER + body).replace('\r\n', '\n').replace('\n', '\r\n').encode('ascii')
    files = []
    for name in ('ChatGPT-Install.bat', 'ChatGPT-Download.bat'):
        target = destination / name
        target.write_bytes(payload)
        files.append(target)
    for name in ('README.md', 'LICENSE'):
        target = destination / name
        target.write_bytes((ROOT / name).read_bytes())
        files.append(target)
    for source in sorted((ROOT / 'docs').glob('*.md')):
        target = destination / 'docs' / source.name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(source.read_bytes())
        files.append(target)
    checksums = ''.join(f'{hashlib.sha256(p.read_bytes()).hexdigest()}  {p.relative_to(destination).as_posix()}\n' for p in files)
    checksum_path = destination / 'SHA256SUMS.txt'
    checksum_path.write_text(checksums, encoding='ascii')
    archive = destination / f'ChatGPT-Offline-Helper-v{VERSION}.zip'
    with ZipFile(archive, 'w', compression=ZIP_DEFLATED) as bundle:
        for path in files + [checksum_path]:
            bundle.write(path, path.relative_to(destination).as_posix())
    print(f'Built {archive.name}: {archive.stat().st_size} bytes; no application package included.')
    return destination / 'ChatGPT-Install.bat'


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=ROOT / 'dist')
    args = parser.parse_args()
    build(args.output.resolve())
