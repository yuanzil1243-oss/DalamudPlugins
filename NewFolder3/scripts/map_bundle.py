"""Verify signed official map files; optionally refresh the package copy from a read-only user cache."""
import argparse
import base64
import hashlib
import json
from pathlib import Path
import re
import shutil
import tempfile
from local_version import ROOT

SCOPES=('pt-21-30','pt-31-40')
FILES=('catalog.json','catalog.sig.json','hoard-yield-catalog.json')

def verify(root,source_root=ROOT):
    from cryptography.hazmat.primitives import serialization, hashes
    from cryptography.hazmat.primitives.asymmetric import ec
    trust=(source_root/'shared/DeepDungeon.Fsd/Core/DetailedMapCatalogTrust.cs').read_text(encoding='utf-8-sig')
    der=re.search(r'SubjectPublicKeyInfoBase64\s*=\s*"([^"]+)"',trust).group(1)
    key=serialization.load_der_public_key(base64.b64decode(der))
    entries=[]
    for scope in SCOPES:
        pointer=json.loads((root/scope/'installed.json').read_bytes())
        release=pointer['releaseId'];digest=pointer['catalogSha256']
        if not re.fullmatch(r'\d{4}-\d{2}-\d{2}\.\d+',release) or not re.fullmatch('[0-9a-f]{64}',digest):
            raise ValueError('Unexpected release/hash format')
        if pointer['scenarioKey']!=scope: raise ValueError('Pointer scope mismatch')
        directory=root/scope/'releases'/(release+'-'+digest[:12])
        data=(directory/'catalog.json').read_bytes();catalog=json.loads(data)
        signature=json.loads((directory/'catalog.sig.json').read_bytes())
        if hashlib.sha256(data).hexdigest()!=digest or signature['catalogSha256']!=digest:
            raise ValueError('Catalog hash mismatch')
        if signature['keyId']!='catalog-p256-v1' or signature['algorithm']!='ecdsa-p256-sha256':
            raise ValueError('Unknown signing key/algorithm')
        key.verify(base64.b64decode(signature['signature']),data,ec.ECDSA(hashes.SHA256()))
        if catalog['scenarioKey']!=scope or catalog['releaseId']!=release or catalog['modelSha256']!=pointer['modelSha256']:
            raise ValueError('Catalog scope/release/model mismatch')
        if hashlib.sha256((directory/'hoard-yield-catalog.json').read_bytes()).hexdigest()!=catalog['hoardYieldSha256']:
            raise ValueError('Yield data hash mismatch')
        rel=directory.relative_to(root/scope).as_posix()
        paths=['installed.json']+[rel+'/'+x for x in FILES]
        entries.append({'scenario':scope,'release':release,'sha256':{
            name:hashlib.sha256((root/scope/name).read_bytes()).hexdigest() for name in paths}})
    return {'source':'user-local-official-download','bundles':entries}

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-cache',type=Path);args=parser.parse_args()
    if args.source_cache:
        source=args.source_cache.resolve();result=verify(source)
        # Verify every source artifact first, stage only the allowlisted map data, then replace package files.
        with tempfile.TemporaryDirectory(prefix='newfolder3-map-stage-') as temp:
            stage=Path(temp)
            for entry in result['bundles']:
                for name in entry['sha256']:
                    dest=stage/entry['scenario']/name;dest.parent.mkdir(parents=True,exist_ok=True)
                    shutil.copy2(source/entry['scenario']/name,dest)
            verify(stage)
            shutil.copytree(stage,ROOT/'DetailedMaps',dirs_exist_ok=True)
        (ROOT/'DetailedMaps/bundle-manifest.json').write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    else: result=verify(ROOT/'DetailedMaps')
    print(json.dumps(result,ensure_ascii=False,indent=2))
