"""Rehearse the current base, the next official version, and code-conflict refusal."""
import json, subprocess, tempfile
from pathlib import Path
from local_version import ROOT, check, make_version
from local_overlay import export, apply, git, patch_for_upstream

meta=check();work=Path(tempfile.mkdtemp(prefix='overlay-rehearsals-',dir=ROOT/'artifacts'))
overlay=work/'overlay';export(overlay)
apply(overlay,ROOT,work/'current-revision',meta['localRevision'],meta['upstreamCommit'])
assert check(work/'current-revision')['localVersion']==meta['localVersion']
parts=meta['upstreamVersion'].split('.');parts[-1]=str(int(parts[-1])+1)
next_official='.'.join(parts);next_local=make_version(next_official,1)
upstream=work/'simulated-upstream'
subprocess.run(['git','clone','-c','core.longpaths=true','--no-hardlinks','--no-checkout',str(ROOT),str(upstream)],check=True)
subprocess.run(['git','-C',str(upstream),'checkout','--detach',meta['upstreamCommit']],check=True)
for name in ('NewFolder3.csproj','NewFolder3.json'):
 p=upstream/name;p.write_text(p.read_text(encoding='utf-8-sig').replace(meta['upstreamVersion'],next_official),encoding='utf-8')
subprocess.run(['git','-C',str(upstream),'add','NewFolder3.csproj','NewFolder3.json'],check=True)
subprocess.run(['git','-C',str(upstream),'-c','user.name=Local Verification','-c','user.email=local-verification@localhost','commit','-m','Simulate next upstream for overlay verification'],check=True)
apply(overlay,upstream,work/'next-revision',1,'HEAD')
assert check(work/'next-revision')['localVersion']==next_local
plugin=json.loads((work/'next-revision/NewFolder3.json').read_text(encoding='utf-8'))
assert plugin['Name']=='NewFolder3'
assert next_official in plugin['Description'] and next_local in plugin['Description']
# A version string in executable-code context must never be rewritten.
probe='+++ b/Plugin.cs\n Version='+meta['upstreamVersion']+'\n'
assert patch_for_upstream(probe,meta['upstreamVersion'],next_official)==probe
p=upstream/'Plugin.cs';text=p.read_text(encoding='utf-8-sig');text=text.replace('_configuration =','_configuration = /* simulated conflicting upstream config loader */')
p.write_text(text,encoding='utf-8');subprocess.run(['git','-C',str(upstream),'add','Plugin.cs'],check=True)
subprocess.run(['git','-C',str(upstream),'-c','user.name=Local Verification','-c','user.email=local-verification@localhost','commit','-m','Simulate conflicting upstream hook'],check=True)
refused=False
try:apply(overlay,upstream,work/'conflict-refused',1,'HEAD')
except subprocess.CalledProcessError:refused=True
assert refused and not (work/'conflict-refused/LocalExtensions').exists()
result={'currentVersion':meta['localVersion'],'currentReapplied':True,'currentSource':str(work/'current-revision'),'simulatedNextVersion':next_local,
 'nextReapplied':True,'versionAdaptationLimitedToMetadata':True,
 'contextConflictRefusedBeforeAdditions':True,'originalSourceUntouched':True}
(ROOT/'artifacts/overlay-rehearsals.json').write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8');print(json.dumps(result))
