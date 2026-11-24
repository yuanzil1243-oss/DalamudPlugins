"""Fail closed if extensions touch undeclared upstream code or ship unverified maps."""
import hashlib, json, subprocess
from pathlib import Path
from local_version import ROOT, check
from local_overlay import HOOKS
from map_bundle import verify

PAYLOAD = {'NewFolder3.dll','NewFolder3.json','DeepDungeon.Fsd.Core.dll','DeepDungeon.Fsd.Runtime.dll',
    'DeepDungeon.Fsd.Dalamud.dll','DeepDungeon.MechanicRuntime.dll','FFXIVClientStructs.Expansion.dll',
    'OmenTools.dll','GuerrillaNtp.dll','TerraFX.Interop.Windows.dll','TinyPinyin.dll'}

def audit():
    meta=check();base=meta['upstreamCommit']
    names=subprocess.check_output(['git','-C',str(ROOT),'diff',base,'--name-only','--ignore-submodules=all']).decode().splitlines()
    if set(names)-HOOKS:raise ValueError('Undeclared upstream modifications: '+str(set(names)-HOOKS))
    feed='NewFolder3-release.json'
    original=subprocess.check_output(['git','-C',str(ROOT),'show',base+':'+feed])
    if original.replace(b'\r\n',b'\n')!=(ROOT/feed).read_bytes().replace(b'\r\n',b'\n'):
        raise ValueError('Release feed changed')
    maps=verify(ROOT/'DetailedMaps')
    output=ROOT/'artifacts/bin/NewFolder3/release'
    actual={p.name for p in output.iterdir() if p.is_file()}
    if actual!=PAYLOAD:raise ValueError('Unexpected or missing build payload: '+str(actual^PAYLOAD))
    if json.loads((output/'NewFolder3.json').read_bytes())['AssemblyVersion']!=meta['localVersion']:
        raise ValueError('Built plugin manifest version mismatch')
    if verify(output/'DetailedMaps')!=maps:raise ValueError('Built maps differ from verified source')
    protected=['FarmingScenario.cs','Encounter','Navigation','MechanicRuntime']
    report={'version':meta['localVersion'],'upstream':base,'declaredHooks':sorted(names),
        'allOtherTrackedUpstreamFilesUnchanged':True,'releaseFeedUnchanged':True,
        'maps':maps,'payload':{p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in output.iterdir() if p.is_file()}}
    (ROOT/'artifacts/source-payload-audit.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    return report

if __name__=='__main__':
    r=audit();print(json.dumps({'version':r['version'],'hooks':len(r['declaredHooks']),'payloadFiles':len(r['payload']),
        'mapScopes':len(r['maps']['bundles']),'status':'passed'}))
