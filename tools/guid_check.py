"""Validates the AssetRipper script-GUID port (MD5 of assembly+namespace+class, nibble-swapped) against
the .cs.meta files of the rip, and reports the top-level script folders."""
import glob, hashlib, io, os, sys
sys.stdout.reconfigure(encoding='utf-8', errors='replace')
root = r'C:\Games\SPT-4.1\Tools\arena-rip\project\ExportedProject\Assets'


def guid(asm, ns, cls):
    # AssetRipper: MD5(assembly + namespace + class) laid out in System.Guid byte order, printed as plain hex.
    h = hashlib.md5((asm + ns + cls).encode('utf-8')).digest()
    return (h[3::-1] + h[5:3:-1] + h[7:5:-1] + h[8:]).hex()


ok = bad = 0
for top in ('Scripts', 'Plugins'):
    for m in glob.glob(os.path.join(root, top, '**', '*.cs.meta'), recursive=True)[:400]:
        rel = os.path.relpath(m, os.path.join(root, top)).replace(os.sep, '/')
        parts = rel.split('/')
        asm, cls, ns = parts[0], parts[-1][:-8], '.'.join(parts[1:-1])
        g = io.open(m, encoding='utf-8').read().split('guid: ')[1][:32]
        if guid(asm, ns, cls) == g:
            ok += 1
        else:
            bad += 1
            if bad <= 3:
                print('mismatch', rel, guid(asm, ns, cls), g)
print('guid port: ok', ok, 'bad', bad)
print('Image', guid('UnityEngine.UI', 'UnityEngine.UI', 'Image'))
print('TextMeshProUGUI', guid('Unity.TextMeshPro', 'TMPro', 'TextMeshProUGUI'))
print('LocalizedText', guid('Assembly-CSharp', 'EFT.UI', 'LocalizedText'))
