"""One numbering rule for source, manifest, assembly and package names."""
import argparse
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def make_version(official, revision):
    parts = [int(x) for x in official.split('.')]
    if len(parts) != 4 or any(x < 0 or x > 65535 for x in parts) or not 1 <= revision <= 99:
        raise ValueError('Expected a four-part official version and local revision 1..99')
    parts[-1] = parts[-1] * 100 + revision
    if parts[-1] > 65535:
        raise ValueError('Local version exceeds the .NET assembly component limit')
    return '.'.join(map(str, parts))

def check(root=ROOT):
    meta = json.loads((root/'LocalExtensions/extension-manifest.json').read_text(encoding='utf-8'))
    expected = make_version(meta['upstreamVersion'], meta['localRevision'])
    project = (root/'NewFolder3.csproj').read_text(encoding='utf-8-sig')
    manifest = json.loads((root/'NewFolder3.json').read_text(encoding='utf-8-sig'))
    for tag in ('Version', 'AssemblyVersion', 'FileVersion'):
        actual = re.search(fr'<{tag}>([^<]+)</{tag}>', project).group(1)
        if actual != expected: raise ValueError(f'{tag}: {actual} != {expected}')
    if manifest['AssemblyVersion'] != expected or meta['localVersion'] != expected:
        raise ValueError('Extension/project/plugin manifest versions disagree')
    return meta

if __name__ == '__main__':
    parser=argparse.ArgumentParser()
    parser.add_argument('--official'); parser.add_argument('--revision',type=int,default=1)
    args=parser.parse_args()
    print(make_version(args.official,args.revision) if args.official else check()['localVersion'])
