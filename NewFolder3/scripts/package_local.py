"""Create local binary, source and reusable overlay archives. Never install or publish."""
import argparse, hashlib, json, shutil, subprocess, tempfile, zipfile
from pathlib import Path
from local_version import ROOT, check
from local_overlay import additions, export
from verify_local import audit, PAYLOAD

DOCS={'本地扩展壳与更新规则.md','使用与换版说明.md','验收记录.md'}
def zip_tree(folder,destination):
    with zipfile.ZipFile(destination,'x',zipfile.ZIP_DEFLATED,compresslevel=6) as z:
        for p in sorted(folder.rglob('*')):
            if p.is_file():z.write(p,p.relative_to(folder).as_posix())

def package(destination):
    report=audit();meta=check();version=meta['localVersion'];destination.mkdir(parents=True,exist_ok=True)
    output=destination/('NewFolder3-'+version+'-local')
    if output.exists():raise ValueError('Version destination exists; refusing to overwrite')
    output.mkdir()
    build=ROOT/'artifacts/bin/NewFolder3/release'
    for name in PAYLOAD:shutil.copy2(build/name,output/name)
    shutil.copytree(build/'DetailedMaps',output/'DetailedMaps')
    if (ROOT/'licenses').exists():shutil.copytree(ROOT/'licenses',output/'licenses')
    for name in ('LICENSE','LICENSE.txt'):
        if (ROOT/name).exists():shutil.copy2(ROOT/name,output/name)
    for name in DOCS:
        p=ROOT/'docs'/name
        if p.exists():shutil.copy2(p,output/name)
    (output/'tools').mkdir()
    shutil.copy2(ROOT/'scripts/Repair-NewFolder3.ps1',output/'tools/Repair-NewFolder3.ps1')
    manifest={'schemaVersion':1,'version':version,'upstreamVersion':meta['upstreamVersion'],
        'upstreamCommit':meta['upstreamCommit'],'files':{p.relative_to(output).as_posix():hashlib.sha256(p.read_bytes()).hexdigest()
        for p in sorted(output.rglob('*')) if p.is_file()}}
    (output/'package-manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    zip_tree(output,destination/('NewFolder3-'+version+'-local.zip'))
    handoff=destination/'源码交接';handoff.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='nf3-source-',dir=ROOT/'artifacts') as temporary:
        stage=Path(temporary)
        tracked=subprocess.check_output(['git','-C',str(ROOT),'ls-files','-z']).decode().split('\0')
        paths=[ROOT/p for p in tracked if p and (ROOT/p).is_file()]
        # Include the pinned, initialized submodule's tracked source; never its .git/config or outputs.
        sub=ROOT/'third_party/OmenTools'
        paths += [sub/p for p in subprocess.check_output(['git','-C',str(sub),'ls-files','-z']).decode().split('\0') if p and (sub/p).is_file()]
        paths += additions(ROOT)
        for p in sorted(set(paths)):
            name=p.relative_to(ROOT)
            if any(part in ('artifacts','bin','obj','binrelease','__pycache__','.git') for part in name.parts):continue
            if name.parts[0]=='docs' and p.name not in DOCS:continue
            if name.as_posix()=='NewFolder3.zip':continue # Upstream's prebuilt release is not source.
            if 'OfficialBuildProfile' in p.name or p.suffix in ('.dll','.pdb','.zip','.pk8','.p8'):raise ValueError('Unexpected source payload: '+str(name))
            target=stage/name;target.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(p,target)
        zip_tree(stage,handoff/('NewFolder3-'+version+'-source.zip'))
    overlay=ROOT/'artifacts'/('overlay-final-'+version)
    export(overlay)
    zip_tree(overlay,handoff/('NewFolder3-'+version+'-overlay.zip'))
    shutil.copy2(ROOT/'artifacts/source-payload-audit.json',handoff/('audit-'+version+'.json'))
    result={'version':version,'pluginDirectory':str(output),'sourceDirectory':str(handoff),
        'installed':False,'published':False,'personalConfigurationIncluded':False}
    (destination/('delivery-'+version+'.json')).write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(result,ensure_ascii=False,indent=2))

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--output',type=Path,default=ROOT/'artifacts/packages');args=p.parse_args();package(args.output.resolve())
