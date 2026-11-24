"""Verify only the current bundled maps; the old HTTP data tree is sealed in archive."""
import base64, hashlib, json, re, subprocess, tempfile
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
def require(value,message):
    if not value:raise AssertionError(message)
def digest(data):return hashlib.sha256(data).hexdigest()
def verify_signature(key,data,signature,path):
    try:
        from cryptography.hazmat.primitives import hashes,serialization
        from cryptography.hazmat.primitives.asymmetric import ec
    except ImportError:
        with tempfile.TemporaryDirectory(prefix='nf3-map-signature-') as temp:
            temp=Path(temp);(temp/'public.der').write_bytes(key);(temp/'signature.der').write_bytes(signature)
            result=subprocess.run(['openssl','dgst','-sha256','-verify',str(temp/'public.der'),'-keyform','DER',
                '-signature',str(temp/'signature.der'),str(path)],capture_output=True,text=True)
            require(result.returncode==0,'Original map signature rejected: '+result.stderr)
    else:
        serialization.load_der_public_key(key).verify(signature,data,ec.ECDSA(hashes.SHA256()))

def verify(root,source_root):
    trust=(source_root/'shared/DeepDungeon.Fsd/Core/DetailedMapCatalogTrust.cs').read_text(encoding='utf-8-sig')
    key=base64.b64decode(re.search(r'SubjectPublicKeyInfoBase64\s*=\s*"([^"]+)"',trust)[1],validate=True)
    entries=[];allowed={'bundle-manifest.json'}
    for scope,territory,first in [('pt-21-30',1283,21),('pt-31-40',1284,31)]:
        pointer=json.loads((root/scope/'installed.json').read_bytes())
        release=pointer['releaseId'];sha=pointer['catalogSha256']
        require(re.fullmatch(r'\d{4}-\d{2}-\d{2}\.\d+',release) and re.fullmatch('[0-9a-f]{64}',sha),'Invalid map version/hash')
        require(pointer['scenarioKey']==scope,'Wrong pointer scope')
        directory=root/scope/'releases'/(release+'-'+sha[:12])
        path=directory/'catalog.json';data=path.read_bytes();catalog=json.loads(data)
        envelope=json.loads((directory/'catalog.sig.json').read_bytes())
        require(digest(data)==sha==envelope['catalogSha256'],'Map hash mismatch')
        require(envelope['algorithm']=='ecdsa-p256-sha256' and envelope['keyId']=='catalog-p256-v1','Wrong map key/algorithm')
        verify_signature(key,data,base64.b64decode(envelope['signature'],validate=True),path)
        require(catalog['scenarioKey']==scope and catalog['releaseId']==release and catalog['territoryId']==territory and
                catalog['floors']==list(range(first,first+9)) and catalog['modelSha256']==pointer['modelSha256'],
                'Map scope/model/release mismatch')
        yield_bytes=(directory/'hoard-yield-catalog.json').read_bytes();yields=json.loads(yield_bytes)
        require(digest(yield_bytes)==catalog['hoardYieldSha256'],'Map yield hash mismatch')
        require(yields['scenarioKey']==scope and yields['territoryId']==territory and yields['floors']==catalog['floors'],
                'Yield scope mismatch')
        names=['installed.json']+[p.relative_to(root/scope).as_posix() for p in
            [directory/'catalog.json',directory/'catalog.sig.json',directory/'hoard-yield-catalog.json']]
        hashes={name:digest((root/scope/name).read_bytes()) for name in names}
        entries.append({'scenario':scope,'release':release,'sha256':hashes})
        allowed|={scope+'/'+name for name in names}
    require({p.relative_to(root).as_posix() for p in root.rglob('*') if p.is_file()}==allowed,'Unexpected map payload')
    bundled=json.loads((root/'bundle-manifest.json').read_bytes())
    require(bundled['bundles']==entries,'Bundle inventory/hash mismatch')
    return {'bundles':entries}

if __name__=='__main__':
    report=verify(ROOT/'NewFolder3/DetailedMaps',ROOT/'NewFolder3')
    print('PASS current bundled maps: 2 original ECDSA signatures, scopes, yields and exact file inventory')
