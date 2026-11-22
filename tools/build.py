"""Build and probe NodeLock without launching or installing KSA."""
from pathlib import Path
import argparse
import subprocess
import shutil

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--game-dir', default='G:/Program Files/Ahwoo/Games/Kitten Space Agency')
parser.add_argument('--loader-dir', default=str(root.parent/'.tools/starmap-0.5.4/release'))
args = parser.parse_args()
game, loader = Path(args.game_dir).resolve(), Path(args.loader_dir).resolve()
artifacts = root/'artifacts'
artifacts.mkdir(exist_ok=True)
props = [f'-p:GameDir={game}', f'-p:LoaderDir={loader}']
assembly = root/'src/NodeLock.Ksa/bin/Release/net10.0/NodeLock.Ksa.dll'
commands = [
    ['dotnet', 'build', str(root/'src/NodeLock.Ksa'), '-c', 'Release', *props],
    ['dotnet', 'run', '--project', str(root/'tools/Probe'), *props, '--', str(game), str(assembly), str(loader)],
]
for i, command in enumerate(commands):
    result = subprocess.run(command, cwd=root, capture_output=True, text=True)
    (artifacts/f'check-{i}.log').write_text(result.stdout+result.stderr, encoding='utf-8')
    print(result.stdout+result.stderr, end='')
    if result.returncode: raise SystemExit(result.returncode)
package = artifacts/'0.1.0/NodeLock'
package.mkdir(parents=True,exist_ok=True)
for name in ['NodeLock.Ksa.dll','NodeLock.Core.dll','NodeLock.Ksa.deps.json']:
    shutil.copy2(assembly.parent/name,package/name)
for source in [root/'src/NodeLock.Ksa/mod.toml',root/'content/BatteryBayCompat.xml',root/'content/TankBayCompat.xml',root/'LICENSE']:
    shutil.copy2(source,package/source.name)
print(f'Staged {package}; run tools/package_release.py to create the release archive.')
