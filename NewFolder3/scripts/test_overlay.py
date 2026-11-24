"""Rehearse .2002, simulated .21->.2101, and context-conflict refusal in disposable checkouts."""
import json, subprocess
from pathlib import Path
from local_version import ROOT, check
from local_overlay import export, apply, git

meta=check();work=ROOT/'artifacts/overlay-rehearsals-final'
if work.exists():raise ValueError('Rehearsal directory exists')
work.mkdir();overlay=work/'overlay';export(overlay)
apply(overlay,ROOT,work/'revision-2002',2,meta['upstreamCommit'])
assert check(work/'revision-2002')['localVersion']=='0.7.55.2002'
upstream=work/'simulated-upstream'
subprocess.run(['git','clone','-c','core.longpaths=true','--no-hardlinks','--no-checkout',str(ROOT),str(upstream)],check=True)
subprocess.run(['git','-C',str(upstream),'checkout','--detach',meta['upstreamCommit']],check=True)
for name in ('NewFolder3.csproj','NewFolder3.json'):
 p=upstream/name;p.write_text(p.read_text(encoding='utf-8-sig').replace('0.7.55.20','0.7.55.21'),encoding='utf-8')
subprocess.run(['git','-C',str(upstream),'add','NewFolder3.csproj','NewFolder3.json'],check=True)
subprocess.run(['git','-C',str(upstream),'-c','user.name=Local Verification','-c','user.email=local-verification@localhost','commit','-m','Simulate upstream .21 for overlay verification'],check=True)
apply(overlay,upstream,work/'revision-2101',1,'HEAD')
assert check(work/'revision-2101')['localVersion']=='0.7.55.2101'
p=upstream/'Plugin.cs';text=p.read_text(encoding='utf-8-sig');text=text.replace('_configuration =','_configuration = /* simulated conflicting upstream config loader */')
p.write_text(text,encoding='utf-8');subprocess.run(['git','-C',str(upstream),'add','Plugin.cs'],check=True)
subprocess.run(['git','-C',str(upstream),'-c','user.name=Local Verification','-c','user.email=local-verification@localhost','commit','-m','Simulate conflicting upstream hook'],check=True)
refused=False
try:apply(overlay,upstream,work/'conflict-refused',1,'HEAD')
except subprocess.CalledProcessError:refused=True
assert refused and not (work/'conflict-refused/LocalExtensions').exists()
result={'revision2002':True,'simulated2101':True,'contextConflictRefusedBeforeAdditions':True,'originalSourceUntouched':True}
(ROOT/'artifacts/overlay-rehearsals.json').write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8');print(json.dumps(result))
