"""Read-only PE audit. No process handles, runtime memory, hook installation, or game operation.

Usage: python audit_native_start.py GAME_EXE OUTPUT_JSON
Requires pefile and capstone. Emits disassembly/evidence, never executable file fragments.
The same bounded-body and relative-CALL contract is enforced by production initialization.
"""
import hashlib
import json
from pathlib import Path
import re
import struct
import sys

import capstone
import pefile

source = Path(__file__).resolve().parents[2] / 'LocalExtensions/Host/Movement'
contract = (source / 'NativeSpeedContract.cs').read_text(encoding='utf-8')
backend = (source / 'NativeSpeedBackend.cs').read_text(encoding='utf-8')
binary, output = map(Path, sys.argv[1:])
data = binary.read_bytes()
pe = pefile.PE(data=data, fast_load=True)
text = next(section for section in pe.sections if section.Name.rstrip(b'\0') == b'.text')
code = text.get_data()
base = pe.OPTIONAL_HEADER.ImageBase


def constant(name, body):
    return re.search(r'const string ' + name + r' = "([^"]+)"', body)[1]


def scan(signature):
    pattern = b''.join(b'.' if t == '??' else re.escape(bytes([int(t, 16)])) for t in signature.split())
    return [text.VirtualAddress + match.start() for match in re.finditer(pattern, code, re.DOTALL)]


signatures = {'getter': constant('TargetSpeedSignature', backend),
              'instance': constant('WalkInstanceSignature', backend),
              'updater': constant('UpdateSignature', contract)}
matches = {key: scan(value) for key, value in signatures.items()}
assert all(len(value) == 1 for value in matches.values()), matches
call = matches['getter'][0]
getter = call + 5 + struct.unpack('<i', pe.get_data(call + 1, 4))[0]
updater = matches['updater'][0]
length = int(re.search(r'const int UpdateLength = (\d+)', contract)[1])
body = pe.get_data(updater, length)
digest = hashlib.sha256(body).hexdigest()
assert digest == constant('UpdateSha256', contract), 'Updater fingerprint drift'
for offset in [0x84, 0xfc]:
    assert body[offset] == 0xe8
    assert updater + offset + 5 + struct.unpack_from('<i', body, offset + 1)[0] == getter
instance_site = matches['instance'][0]
instance = instance_site + 7 + struct.unpack('<i', pe.get_data(instance_site + 3, 4))[0]
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)


def disassemble(rva, size):
    return [f'{i.address - base:08X} {i.mnemonic:8} {i.op_str}'
            for i in md.disasm(pe.get_data(rva, size), base + rva)]


report = {'kind': 'offline-disk-only-no-process-access', 'binary': str(binary),
          'binarySha256': hashlib.sha256(data).hexdigest(), 'binaryBytes': len(data),
          'signatureCounts': {key: len(value) for key, value in matches.items()},
          'getterRva': hex(getter), 'updaterRva': hex(updater), 'walkRva': hex(instance),
          'runtimeUsesFixedRvas': False, 'updaterLength': length, 'updaterSha256': digest,
          'bothCallsResolveToGetter': True, 'updaterDisassembly': disassemble(updater, length),
          'getterDisassembly': disassemble(getter, 0x194),
          'gameRuntimeVerified': False,
          'semantics': ['CurrentSpeed+0x44 loaded before getter: changing getter alone cannot bypass ramp.',
                        'Native increment (delta * rate) then min(target, accumulated speed), then store +0x44.',
                        'Managed post-update correction runs only after a getter observed in that same native update.',
                        'No-getter/forced branch keeps sole ownership of its native result.']}
output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(json.dumps({key: value for key, value in report.items() if 'Disassembly' not in key}, ensure_ascii=False))
