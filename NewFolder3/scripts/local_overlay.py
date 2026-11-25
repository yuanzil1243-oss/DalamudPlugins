"""Export/apply the extension shell. Always create a new output tree; never mutate the source checkout.

Hook patches use git's context checks. No fuzz, no whole-file replacement of upstream logic.
If a newer upstream changes a hook, application fails with a reviewable conflict.
"""
import argparse
import difflib
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
from local_version import ROOT, check, make_version

HOOKS = {
 'Configuration.cs','FsdWindow.cs','NewFolder3.csproj','NewFolder3.json','Plugin.cs','README.md','global.json',
 'shared/DeepDungeon.Fsd/Dalamud/Fsd/Runtime/Helpers/NativeDeepDungeonLogMessageSource.cs',
 'shared/DeepDungeon.Fsd/Core/EntryUiPolicy.cs','shared/DeepDungeon.Fsd/Core/InteractionTiming.cs',
 'shared/DeepDungeon.Fsd/Core/ExitUiPolicy.cs',
 'shared/DeepDungeon.Fsd/Core/FarmingPlan.cs','shared/DeepDungeon.Fsd/Core/FarmingSessionPolicy.cs',
 'shared/DeepDungeon.Fsd/Dalamud/Fsd/FsdChestInteraction.cs',
 'shared/DeepDungeon.Fsd/Dalamud/Fsd/FsdEngine.Companions.cs',
 'shared/DeepDungeon.Fsd/Dalamud/Fsd/FsdEngine.Farming.cs',
 'shared/DeepDungeon.Fsd/Dalamud/Fsd/FsdEngine.Rotation.cs',
 'shared/DeepDungeon.Fsd/Dalamud/Fsd/FsdEngine.Ui.cs',
 'shared/DeepDungeon.Fsd/Dalamud/Fsd/FsdEngine.Pause.cs',
 'shared/DeepDungeon.Fsd/Dalamud/Fsd/FsdEngine.cs',
 'shared/DeepDungeon.Fsd/Dalamud/Fsd/GameState/DetailedMapCatalogManager.cs',
 'shared/DeepDungeon.Fsd/Dalamud/Fsd/Runtime/FloorWorkPolicy.cs',
 'shared/DeepDungeon.Fsd/Dalamud/Fsd/Runtime/Floor/FloorExplorationController.cs',
 'shared/DeepDungeon.Fsd/Dalamud/Fsd/Runtime/Floor/FloorExplorationController.Magicite.cs',
 'shared/DeepDungeon.Fsd/Dalamud/Fsd/Runtime/RunHost.cs',
 'shared/DeepDungeon.Fsd/Dalamud/Fsd/Runtime/Scenarios/FarmingScenario.cs',
 'shared/DeepDungeon.Fsd/Dalamud/Fsd/Runtime/Entry/DungeonEntryController.cs',
 'shared/DeepDungeon.Fsd/Dalamud/Fsd/Runtime/Entry/DungeonExitController.cs',
 'shared/DeepDungeon.Fsd/Dalamud/Fsd/Runtime/Entry/EntranceWindowCleanup.cs',
 'shared/DeepDungeon.Fsd/Dalamud/Fsd/Runtime/Entry/GenericDeleteSaveFlow.cs',
 'shared/DeepDungeon.Fsd/Dalamud/Fsd/Runtime/Entry/LeaveDutyFlow.cs',
 'shared/DeepDungeon.Fsd/Dalamud/FsdApplication.Commands.cs',
 'shared/DeepDungeon.Fsd/Dalamud/FsdApplication.cs',
 'shared/DeepDungeon.Fsd/Runtime/FarmingSession.cs','shared/DeepDungeon.Fsd/Runtime/FarmingSettings.cs',
 'shared/DeepDungeon.Fsd/Runtime/FsdControlContracts.cs','shared/DeepDungeon.Fsd/Runtime/FsdRotationSettings.cs',
 'shared/DeepDungeon.Fsd/Runtime/RunOptions.cs','tests/NewFolder3.ContractTests/Program.cs',
}

def git(root,*args):
    return subprocess.check_output(['git','-C',str(root),*args],stderr=subprocess.PIPE)

def patch_for_upstream(patch, source_version, target_version):
    """Adapt only unchanged manifest/project version context, never executable code."""
    name=None
    lines=[]
    for line in patch.splitlines(True):
        if line.startswith('+++ b/'):
            name=line[6:].strip()
        if line.startswith(' ') and name in ('NewFolder3.csproj','NewFolder3.json'):
            if re.match(r'^\s*"AssemblyVersion"\s*:',line) or re.match(r'^\s*<(Version|AssemblyVersion|FileVersion)>',line):
                line=line.replace(source_version,target_version)
        lines.append(line)
    return ''.join(lines)

def relative_files(root,folder):
    return [p for p in (root/folder).rglob('*') if p.is_file() and
        not any(part in ('bin','obj','artifacts','__pycache__') for part in p.relative_to(root).parts)]

def additions(root):
    files=[]
    for folder in ('LocalExtensions','DetailedMaps','scripts','licenses','tests/NewFolder3.SpeedChecks'):
        files += relative_files(root,folder)
    for name in ('本地扩展壳与更新规则.md','使用与换版说明.md','验收记录.md','聚魔柄循环移植与验收.md'):
        if (root/'docs'/name).is_file():files.append(root/'docs'/name)
    for name in ('Directory.Build.targets','tests/NewFolder3.ContractTests/ContinuationFeatureTests.cs',
        'tests/NewFolder3.ContractTests/FreeExplorationTests.cs','tests/NewFolder3.ContractTests/LocalFeatureTests.cs',
        'tests/NewFolder3.ContractTests/OfflineMapTests.cs'):
        files.append(root/name)
    files.append(root/'tests/NewFolder3.ContractTests/LegacyConfigTests.cs')
    files.append(root/'tests/NewFolder3.ContractTests/TerminalRewardExitTests.cs')
    files.append(root/'tests/NewFolder3.ContractTests/RecoveryAndPauseTests.cs')
    files.append(root/'tests/NewFolder3.ContractTests/RotationDiagnosticTests.cs')
    files.append(root/'tests/NewFolder3.ContractTests/GcdWaitSettingsTests.cs')
    files.append(root/'tests/NewFolder3.ContractTests/AntiKickPulseTests.cs')
    files.append(root/'tests/NewFolder3.ContractTests/FailureRestartAuditTests.cs')
    files.append(root/'tests/NewFolder3.ContractTests/ExitReceiptTests.cs')
    files.append(root/'tests/NewFolder3.ContractTests/PauseCancellationTests.cs')
    files.append(root/'tests/NewFolder3.ContractTests/AetherpoolExchangeTests.cs')
    files.append(root/'tests/NewFolder3.ContractTests/PassageRoutingRegressionTests.cs')
    files.append(root/'tests/NewFolder3.ContractTests/ModeContinuationTests.cs')
    files.append(root/'tests/NewFolder3.ContractTests/FailedAttemptRecoveryTests.cs')
    files.append(root/'tests/Test-VersionSwitch.ps1')
    return sorted(set(files))

def export(output):
    meta=check();base=meta['upstreamCommit']
    changed=set(git(ROOT,'diff',base,'--name-only','--ignore-submodules=all').decode().splitlines())
    if changed-HOOKS: raise ValueError('Out-of-scope upstream edits: '+str(sorted(changed-HOOKS)))
    if output.exists(): raise ValueError('Output already exists: '+str(output))
    output.mkdir(parents=True)
    patch=[]
    for name in sorted(changed):
        old=git(ROOT,'show',base+':'+name).decode('utf-8-sig').replace('\r\n','\n')
        new=(ROOT/name).read_text(encoding='utf-8-sig')
        # Official version changes are deliberately outside the hook patch.
        if name in ('NewFolder3.csproj','NewFolder3.json'):
            new=new.replace(meta['localVersion'],meta['upstreamVersion'])
        if name == 'NewFolder3.csproj':
            for tag in ('AssemblyVersion','FileVersion'):
                if f'<{tag}>' not in old:
                    new=re.sub(rf'^.*<{tag}>[^<]+</{tag}>\n','',new,flags=re.M)
        patch += difflib.unified_diff(old.splitlines(True),new.splitlines(True),'a/'+name,'b/'+name,n=3)
    (output/'hooks.patch').write_text(''.join(patch),encoding='utf-8',newline='\n')
    hashes={}
    for source in additions(ROOT):
        name=source.relative_to(ROOT).as_posix();dest=output/'additions'/name
        dest.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(source,dest)
        hashes[name]=hashlib.sha256(source.read_bytes()).hexdigest()
    manifest={'schemaVersion':1,'upstreamCommit':base,'upstreamVersion':meta['upstreamVersion'],
        'localRevision':meta['localRevision'],'hooks':sorted(changed),'files':hashes,
        'patchSha256':hashlib.sha256((output/'hooks.patch').read_bytes()).hexdigest()}
    (output/'overlay.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({'overlay':str(output),'hooks':len(changed),'extensionFiles':len(hashes)}))

def apply(overlay,upstream,output,revision,ref):
    manifest=json.loads((overlay/'overlay.json').read_text(encoding='utf-8'))
    if set(manifest['hooks'])-HOOKS: raise ValueError('Unexpected upstream hook paths')
    if hashlib.sha256((overlay/'hooks.patch').read_bytes()).hexdigest()!=manifest['patchSha256']:
        raise ValueError('Hook patch hash mismatch')
    for name,digest in manifest['files'].items():
        path=overlay/'additions'/name
        if Path(name).is_absolute() or '..' in Path(name).parts or not path.resolve().is_relative_to((overlay/'additions').resolve()):
            raise ValueError('Unsafe extension path')
        if hashlib.sha256(path.read_bytes()).hexdigest()!=digest: raise ValueError('Extension hash mismatch: '+name)
    commit=git(upstream,'rev-parse',ref).decode().strip()
    project=git(upstream,'show',commit+':NewFolder3.csproj').decode('utf-8-sig')
    official=re.search(r'<Version>([^<]+)</Version>',project).group(1)
    version=make_version(official,revision)
    if output.exists(): raise ValueError('Output already exists; source and prior builds must remain untouched')
    subprocess.run(['git','clone','-c','core.longpaths=true','--no-hardlinks','--no-checkout',str(upstream),str(output)],check=True)
    branch='codex/local-extensions-'+version
    existing=set(git(output,'for-each-ref','--format=%(refname:short)','refs/heads/').decode().splitlines())
    suffix=2
    while branch in existing:
        branch='codex/local-extensions-'+version+'-'+str(suffix);suffix+=1
    subprocess.run(['git','-C',str(output),'checkout','-b',branch,commit],check=True)
    if git(output,'status','--porcelain','--untracked-files=no').strip():
        raise ValueError('Official checkout is incomplete or dirty; refusing to apply extensions')
    # Version-only context naturally changes on each official release. Keep all
    # other context strict, and check every patch before writing any extension.
    patch=patch_for_upstream((overlay/'hooks.patch').read_text(encoding='utf-8'),
        manifest['upstreamVersion'],official)
    with tempfile.TemporaryDirectory(prefix='nf3-overlay-context-') as temporary:
        patch_path=Path(temporary)/'hooks.patch'
        patch_path.write_text(patch,encoding='utf-8',newline='\n')
        subprocess.run(['git','-C',str(output),'apply','--check','--whitespace=nowarn',str(patch_path)],check=True)
        for name in manifest['files']:
            if (output/name).exists(): raise ValueError('New upstream path collides with extension: '+name)
        subprocess.run(['git','-C',str(output),'apply','--whitespace=nowarn',str(patch_path)],check=True)
    for name in manifest['files']:
        target=output/name;target.parent.mkdir(parents=True,exist_ok=True)
        shutil.copy2(overlay/'additions'/name,target)
    path=output/'NewFolder3.csproj';text=path.read_text(encoding='utf-8-sig')
    for tag in ('Version','AssemblyVersion','FileVersion'):
        text=re.sub(rf'<{tag}>[^<]+</{tag}>',f'<{tag}>{version}</{tag}>',text)
    extra=''.join(f'\n    <{tag}>{version}</{tag}>' for tag in ('AssemblyVersion','FileVersion') if f'<{tag}>' not in text)
    text=text.replace(f'<Version>{version}</Version>',f'<Version>{version}</Version>'+extra)
    path.write_text(text,encoding='utf-8')
    path=output/'NewFolder3.json';plugin=json.loads(path.read_text(encoding='utf-8-sig'))
    plugin['AssemblyVersion']=version
    plugin['Name']='NewFolder3'
    plugin['Punchline']=re.sub(r'基于官方 \.\d+',f'基于官方 .{official.split(".")[-1]}',plugin.get('Punchline',''))
    plugin['Description']=re.sub(r'^基于原作者 [\d.]+ 的[^。]+。',
        f'基于原作者 {official} 的本地修改版 {version}。',plugin.get('Description',''))
    path.write_text(json.dumps(plugin,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    path=output/'LocalExtensions/extension-manifest.json';meta=json.loads(path.read_text(encoding='utf-8'))
    meta.update(upstreamVersion=official,upstreamCommit=commit,localRevision=revision,localVersion=version)
    path.write_text(json.dumps(meta,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    remote=git(upstream,'remote','get-url','origin').decode().strip()
    subprocess.run(['git','-C',str(output),'remote','set-url','origin',remote],check=True)
    check(output)
    print(json.dumps({'result':str(output),'version':version,'upstream':commit,
        'status':'source-composed-not-built-or-installed','next':'git submodule update --init --recursive; run scripts/Build-Local.ps1'}))

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);subs=parser.add_subparsers(dest='command',required=True)
    ex=subs.add_parser('export');ex.add_argument('--output',type=Path,required=True)
    ap=subs.add_parser('apply');ap.add_argument('--overlay',type=Path,required=True)
    ap.add_argument('--upstream',type=Path,required=True);ap.add_argument('--output',type=Path,required=True)
    ap.add_argument('--revision',type=int,default=1);ap.add_argument('--ref',default='HEAD')
    args=parser.parse_args()
    if args.command=='export': export(args.output.resolve())
    else: apply(args.overlay.resolve(),args.upstream.resolve(),args.output.resolve(),args.revision,args.ref)
