"""Validate the published install feed, source provenance, and ZIP without executing plugins."""
from pathlib import Path, PurePosixPath
import hashlib, json, re, zipfile
ROOT=Path(__file__).resolve().parents[1]
digest=lambda data:hashlib.sha256(data).hexdigest()
def require(value,message):
    if not value:raise AssertionError(message)
feed=json.loads((ROOT/'pluginmaster.json').read_text(encoding='utf-8-sig'))
require(isinstance(feed,list) and len(feed)==1,'The replaced feed must contain exactly one plugin')
entry=feed[0]
require(entry['InternalName']=='NewFolder3','Plugin identity mismatch')
require(entry['Author']=='John Smith / Yuzi','Author must retain John Smith and append Yuzi')
require(entry['AssemblyVersion']=='0.7.55.35' and entry['DalamudApiLevel']==15,'Version/API mismatch')
require(entry['ApplicableVersion']=='any','Applicable version mismatch')
raw='https://raw.githubusercontent.com/yuanzil1243-oss/DalamudPlugins/main'
for key in ('DownloadLinkInstall','DownloadLinkUpdate'):
    require(entry[key]==raw+'/dist/NewFolder3.zip','Wrong download URL')
require(entry['RepoUrl']=='https://github.com/yuanzil1243-oss/DalamudPlugins','Wrong repository')
require(isinstance(entry['LastUpdate'],int) and entry['LastUpdate']>0,'Invalid LastUpdate')
info=json.loads((ROOT/'dist/BUILDINFO.json').read_text(encoding='utf-8'))
package=ROOT/'dist/NewFolder3.zip'
require(digest(package.read_bytes())==info['package']['sha256'],'ZIP checksum mismatch')
require(package.stat().st_size==info['package']['bytes'],'ZIP length mismatch')
with zipfile.ZipFile(package) as z:
    require(z.testzip() is None,'ZIP CRC check failed')
    require(len(z.namelist())==len(set(z.namelist())),'Duplicate ZIP entries')
    for name in z.namelist():
        path=PurePosixPath(name)
        require(not path.is_absolute() and '..' not in path.parts and ':' not in name and '\\' not in name,'Unsafe ZIP path')
    manifest=json.loads(z.read('NewFolder3.json'))
    source=json.loads((ROOT/'NewFolder3/NewFolder3.json').read_text(encoding='utf-8-sig'))
    require(manifest==source,'ZIP/source manifest mismatch')
    for key in ('Author','Name','Punchline','Description','InternalName','AssemblyVersion','DalamudApiLevel','ApplicableVersion','RepoUrl','IconUrl'):
        require(manifest[key]==entry[key],'Feed/ZIP field mismatch: '+key)
    dlls={name for name in z.namelist() if name.endswith('.dll')}
    require(dlls=={'NewFolder3.dll','DeepDungeon.Fsd.Core.dll','DeepDungeon.Fsd.Runtime.dll','DeepDungeon.Fsd.Dalamud.dll','OmenTools.dll','GuerrillaNtp.dll','TinyPinyin.dll'},'Wrong dependency DLL set')
    for name,sha in info['package']['files'].items():require(digest(z.read(name))==sha,'ZIP member hash mismatch: '+name)
    for line in z.read('SHA256SUMS.txt').decode('utf-8-sig').splitlines():
        sha,name=line.split('  ',1)
        require(digest(z.read(name))==sha,'Internal SHA mismatch: '+name)
    require(not any(name.endswith(('.pdb','.jsonl','.raw')) for name in z.namelist()),'Unexpected private/debug evidence')
for line in (ROOT/'dist/SHA256SUMS.txt').read_text(encoding='utf-8').splitlines():
    sha,name=line.split('  ',1)
    require(digest((ROOT/'dist'/name).read_bytes())==sha,'Distribution hash mismatch: '+name)
require(json.loads((ROOT/'NewFolder3/NewFolder3-release.json').read_text(encoding='utf-8'))==feed,'Source test feed mismatch')
for name,sha in info['sourceFiles'].items():require(digest((ROOT/name).read_bytes())==sha,'Source hash mismatch: '+name)
for name in ('Mr.Bean3-Unrestricted','幻想','DalamudPlugins.slnx'):require(not (ROOT/name).exists(),'Old repository content remains: '+name)
require(set(p.name for p in (ROOT/'dist').iterdir())=={'NewFolder3.zip','BUILDINFO.json','SHA256SUMS.txt'},'Old distribution files remain')
print('PASS: one NewFolder3 feed, author/version/API, seven DLLs, ZIP CRC and hashes, source provenance, old content removal')
