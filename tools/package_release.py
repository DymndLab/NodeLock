"""Package only authored runtime files under one NodeLock root."""
from pathlib import Path
import hashlib, zipfile

root=Path(__file__).resolve().parents[1]
stage=root/'artifacts/0.1.1/NodeLock'
release=root/'release/0.1.1'
release.mkdir(parents=True, exist_ok=True)
names=['NodeLock.Ksa.dll','NodeLock.Core.dll','NodeLock.Ksa.deps.json','mod.toml','BatteryBayCompat.xml','TankBayCompat.xml','LICENSE']
archive=release/'NodeLock-0.1.1.zip'
with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED) as z:
    for name in names:
        z.write(stage/name,'NodeLock/'+name)
    z.write(root/'README.md','NodeLock/README.md')
with zipfile.ZipFile(archive) as z:
    assert {p.split('/')[0] for p in z.namelist()} == {'NodeLock'}
    assert {Path(p).name for p in z.namelist() if p.endswith('.dll')} == {'NodeLock.Core.dll','NodeLock.Ksa.dll'}
    assert z.testzip() is None
(release/'SHA256SUMS.txt').write_text(hashlib.sha256(archive.read_bytes()).hexdigest()+'  '+archive.name+'\n',encoding='utf-8')
print(archive)
