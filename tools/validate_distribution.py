"""Validate the active release and the byte-preserved legacy quarantine."""
import hashlib, io, json, re, sys, zipfile
from pathlib import Path, PurePosixPath

ROOT = Path(__file__).resolve().parents[1]
RAW = 'https://raw.githubusercontent.com/yuanzil1243-oss/DalamudPlugins/main/'

def require(value, message):
    if not value: raise AssertionError(message)
def digest(data): return hashlib.sha256(data).hexdigest()
def read(path): return json.loads(path.read_text(encoding='utf-8-sig'))
def safe(name):
    p = PurePosixPath(name)
    require(not p.is_absolute() and '..' not in p.parts and ':' not in name and '\\' not in name,
            'Unsafe path: '+name)
    return p
def zip_files(path):
    with zipfile.ZipFile(path) as z:
        names=z.namelist()
        require(z.testzip() is None and len(names)==len(set(names)), 'Invalid ZIP: '+str(path))
        for name in names: safe(name)
        return {name:z.read(name) for name in names if not name.endswith('/')}

feed = read(ROOT/'pluginmaster.json')
require(isinstance(feed,list) and feed, 'Empty/invalid feed')
identities=[x['InternalName'] for x in feed]
require(len(identities)==len(set(identities)), 'Duplicate plugin identity')
channels=read(ROOT/'dist/CHANNELS.json')
require(set(channels)==set(identities), 'Channel inventory mismatch')
for entry in feed:
    name=entry['InternalName']; safe(name)
    require(entry['RepoUrl']=='https://github.com/yuanzil1243-oss/DalamudPlugins', 'Wrong repository')
    require(entry['ApplicableVersion']=='any' and entry['DalamudApiLevel']==15, 'Host metadata mismatch')
    require(isinstance(entry['LastUpdate'],int) and entry['LastUpdate']>0, 'Invalid timestamp')
    require(set(channels[name])=={'stable'}, 'Unexpected or legacy distribution channel')
    info_path=ROOT/safe(channels[name]['stable']['buildInfo']); info=read(info_path)
    require(info['version']==entry['AssemblyVersion'], 'Wrong current version')
    require(entry['Author']=='John Smith / Yuzi', 'Attribution mismatch')
    package=info_path.parent/safe(info['package']['file'])
    require(entry['DownloadLinkInstall']==entry['DownloadLinkUpdate']==RAW+package.relative_to(ROOT).as_posix(),
            'Wrong active download URL')
    require('archive/' not in entry['DownloadLinkInstall'], 'Active feed references archive')
    require(digest(package.read_bytes())==info['package']['sha256'] and
            package.stat().st_size==info['package']['bytes'], 'Package hash/size mismatch')
    files=zip_files(package)
    require(set(files)==set(info['package']['files']), 'Package inventory mismatch')
    for path,sha in info['package']['files'].items():
        require(digest(files[path])==sha,'Package member mismatch: '+path)
    metadata=json.loads(files[name+'.json'])
    for key in ['Author','Name','Punchline','Description','InternalName','RepoUrl','IconUrl','AssemblyVersion','DalamudApiLevel']:
        require(metadata[key]==entry[key], 'Feed/package metadata differs: '+key)
    dlls={path:digest(value) for path,value in files.items() if path.endswith('.dll')}
    require(dlls==info['testedRuntimeDlls'] and len(dlls)==10, 'Changed or unexpected runtime DLLs')
    expected_dlls={'NewFolder3.dll','DeepDungeon.Fsd.Core.dll','DeepDungeon.Fsd.Runtime.dll',
        'DeepDungeon.Fsd.Dalamud.dll','DeepDungeon.MechanicRuntime.dll','FFXIVClientStructs.Expansion.dll',
        'OmenTools.dll','GuerrillaNtp.dll','TerraFX.Interop.Windows.dll','TinyPinyin.dll'}
    require(set(dlls)==expected_dlls, 'Unexpected DLL dependency set')
    for path in files:
        p=PurePosixPath(path)
        require(not any(part in {'.git','pluginConfigs','bin','obj','__pycache__'} for part in p.parts) and
            not path.endswith(('.pdb','.jsonl','.sqlite3','.db','.pk8','.p8')), 'Private/debug file: '+path)
    internal={line.split('  ',1)[1]:line.split('  ',1)[0] for line in files['SHA256SUMS.txt'].decode().splitlines()}
    require(set(internal)==set(files)-{'SHA256SUMS.txt'}, 'Incomplete package checksums')
    require(all(digest(files[path])==sha for path,sha in internal.items()), 'Invalid internal checksum')
    package_manifest=json.loads(files['package-manifest.json'])
    require(set(package_manifest['files'])==set(files)-{'package-manifest.json','SHA256SUMS.txt'},
            'Package manifest inventory mismatch')
    require(all(digest(files[path])==sha for path,sha in package_manifest['files'].items()),
            'Package manifest checksum mismatch')
    for path,sha in info['sourceFiles'].items():
        require(path.startswith(name+'/') and digest((ROOT/safe(path)).read_bytes())==sha, 'Source mismatch: '+path)
    ignored_source_parts={'.git','artifacts','bin','obj','__pycache__'}
    actual={p.relative_to(ROOT).as_posix() for p in (ROOT/name).rglob('*') if p.is_file() and
        not any(part in ignored_source_parts for part in p.relative_to(ROOT/name).parts)}
    require(actual==set(info['sourceFiles']), 'Unexpected/missing current source file')
    require(read(ROOT/name/(name+'.json'))==metadata,'Source/package manifest mismatch')
    for kind in ['sourcePackage','overlayPackage']:
        artifact=info[kind];path=info_path.parent/safe(artifact['file'])
        require(digest(path.read_bytes())==artifact['sha256'] and path.stat().st_size==artifact['bytes'],
                'Handoff artifact checksum mismatch: '+kind)
        handoff=zip_files(path)
        if kind=='sourcePackage':
            require(set(handoff)=={p[len(name)+1:] for p in info['sourceFiles']}, 'Source ZIP inventory mismatch')
            require(all(digest(handoff[p[len(name)+1:]])==sha for p,sha in info['sourceFiles'].items()),
                    'Source ZIP differs from current source')
        else:
            overlay=json.loads(handoff['overlay.json'])
            require(overlay['upstreamCommit']==info['upstreamCommit'] and
                overlay['localRevision']==info.get('localRevision',1), 'Wrong overlay base')
            require(digest(handoff['hooks.patch'])==overlay['patchSha256'],'Overlay patch hash mismatch')
            require(all(digest(handoff['additions/'+p])==sha for p,sha in overlay['files'].items()),'Overlay hash mismatch')
    sums={line.split('  ',1)[1]:line.split('  ',1)[0]
        for line in (info_path.parent/'SHA256SUMS.txt').read_text().splitlines()}
    expected={info_path.name,info['package']['file'],info['sourcePackage']['file'],info['overlayPackage']['file']}
    require(set(sums)==expected,'Distribution checksum inventory mismatch')
    require(all(digest((info_path.parent/safe(p)).read_bytes())==sha for p,sha in sums.items()),'Distribution checksum mismatch')
    from validate_repository_data import verify
    maps=verify(ROOT/name/'DetailedMaps',ROOT/name)
    require({x['scenario'] for x in maps['bundles']}=={'pt-21-30','pt-31-40'},'Wrong map scopes')
    for bundle in maps['bundles']:
        require(bundle['release']=='2026-11-25.1','Unexpected map release')
        for path,sha in bundle['sha256'].items():
            require(digest(files['DetailedMaps/'+bundle['scenario']+'/'+path])==sha,'Package/source map mismatch')
    require(digest((ROOT/name/'NewFolder3-release.json').read_bytes())==info['upstreamFeedSha256'],
            'Upstream source feed was replaced')
    print(f"PASS {name} {info['version']}: feed, package, 10 tested DLLs, source/overlay, maps and checksums")

legacy=ROOT/'archive/NewFolder3/0.7.55.83-final-local'; sealed=read(legacy/'ARCHIVE-MANIFEST.json')
require(sealed['repositoryCommit']=='b3420b1d88faa13a26e8c7d4f8b4c4ac66cc247c' and len(sealed['files'])==857,
        'Wrong archive baseline/inventory')
require(all(digest((legacy/safe(p)).read_bytes())==sha for p,sha in sealed['files'].items()),'Archived bytes changed')
require({p.relative_to(legacy).as_posix() for p in legacy.rglob('*') if p.is_file()}==
        set(sealed['files'])|{'ARCHIVE-MANIFEST.json'},'Archive inventory drift')
require(not (ROOT/'data').exists() and not (ROOT/'dist/NewFolder3.zip').exists(), 'Legacy active data/package remained')
for doc in [ROOT/'README.md',*(ROOT/'docs').glob('*.md')]:
    for target in re.findall(r'\]\(([^)]+)\)',doc.read_text(encoding='utf-8')):
        if target.startswith(('https:','http:','#')):continue
        require((doc.parent/target.split('#')[0]).exists(),'Broken documentation link: '+target)
print('PASS archive: 857 original files byte-identical; current links and old endpoints isolated')
