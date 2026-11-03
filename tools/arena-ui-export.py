"""Layout transpiler: ripped Arena UI prefabs (Unity YAML from AssetRipper) -> per-screen JSON the arena client
rebuilds at runtime with the ripped sprites and fonts. Same tree, same RectTransform numbers, same component
fields; behaviour is attached later by object name.

Usage:
  py arena-ui-export.py <rip Assets dir> <out dir> [PrefabName ...]
  (no names = every prefab whose name starts with "Arena" plus the shared widgets they reference)

Output: <out>/<PrefabName>.json, one node tree:
  {name, active, layer, rect:{anchorMin,anchorMax,pos,size,pivot,scale,rot}, components:[{type, fields}], children:[...]}
Field values keep Unity's names (m_Sprite, m_Color, m_text ...). References are resolved to
  {"sprite": "<png path>", "rect": [...], "border": [...], "pivot": [...]} for sprites,
  {"texture": path}, {"font": name}, {"prefab": name}, {"ref": "<path/inside/this/prefab>", "type": "Image"} for
  internal component links, and {"external": guid} when nothing else matches.
Nested prefab instances become {"prefabInstance": name, "modifications": [...]} nodes.
"""
import io, json, os, re, sys
import yaml

Loader = yaml.CSafeLoader if hasattr(yaml, 'CSafeLoader') else yaml.SafeLoader
Loader.add_multi_constructor('tag:unity3d.com,2011:', lambda loader, suffix, node: loader.construct_mapping(node, deep=True))
HEADER = re.compile(r'^--- !u!(\d+) &(-?\d+)( stripped)?\s*$', re.M)
BAR_SCALAR = re.compile(r'^(\s*m_\w+): \|(\S.*)$', re.M)
GUID = re.compile(r'^guid: ([0-9a-f]{32})', re.M)
SKIP_FIELDS = {'m_ObjectHideFlags', 'm_CorrespondingSourceObject', 'm_PrefabInstance', 'm_PrefabAsset',
               'm_GameObject', 'm_EditorHideFlags', 'm_EditorClassIdentifier', 'm_Script', 'm_Name', 'serializedVersion'}


def load_docs(path):
    text = io.open(path, encoding='utf-8').read()
    heads = list(HEADER.finditer(text))
    docs = {}
    for i, h in enumerate(heads):
        body = text[h.end():heads[i + 1].start() if i + 1 < len(heads) else len(text)]
        # AssetRipper writes a text value that starts with '|' unquoted (e.g. "m_text: ||"), which YAML reads as a
        # block scalar. Quote such values.
        body = BAR_SCALAR.sub(lambda m: m.group(1) + ': ' + repr(m.group(2)), body)
        data = yaml.load(body, Loader=Loader) or {}
        cls, payload = next(iter(data.items()))
        docs[int(h.group(2))] = (int(h.group(1)), cls, payload)
    return docs


class Rip:
    """guid -> what it is, over the whole exported project."""

    def __init__(self, assets):
        self.assets = assets
        self.by_guid = {}
        for root, _, files in os.walk(assets):
            for f in files:
                if not f.endswith('.meta'):
                    continue
                full = os.path.join(root, f)
                m = GUID.search(io.open(full, encoding='utf-8', errors='replace').read(4096))
                if m:
                    self.by_guid[m.group(1)] = os.path.relpath(full[:-5], assets).replace('\\', '/')
        self.sprites = {}

    def script_name(self, guid):
        p = self.by_guid.get(guid, '')
        if not p.startswith('Scripts/'):
            return None
        parts = p[len('Scripts/'):].split('/')
        return '.'.join(parts[1:])[:-3] if len(parts) > 1 else parts[0][:-3]   # drop assembly folder and .cs

    def sprite(self, guid):
        if guid in self.sprites:
            return self.sprites[guid]
        p = self.by_guid.get(guid)
        out = None
        if p and p.endswith('.asset'):
            docs = load_docs(os.path.join(self.assets, p))
            for _, cls, s in docs.values():
                if cls != 'Sprite':
                    continue
                tex = s.get('m_RD', {}).get('texture', {}).get('guid')
                r, b, pv = s.get('m_Rect', {}), s.get('m_Border', {}), s.get('m_Pivot', {})
                out = {'sprite': self.by_guid.get(tex), 'name': s.get('m_Name'),
                       'rect': [r.get('x'), r.get('y'), r.get('width'), r.get('height')],
                       'border': [b.get('x'), b.get('y'), b.get('z'), b.get('w')],
                       'pivot': [pv.get('x'), pv.get('y')], 'ppu': s.get('m_PixelsToUnits')}
        self.sprites[guid] = out
        return out

    def resolve_external(self, ref):
        guid = ref.get('guid')
        p = self.by_guid.get(guid)
        if not p:
            return {'external': guid}
        if p.endswith('.asset'):
            sp = self.sprite(guid)
            if sp:
                return sp
            return {'asset': p}
        if p.endswith('.png'):
            return {'texture': p}
        if p.endswith('.prefab'):
            return {'prefab': os.path.basename(p)[:-7]}
        if p.endswith(('.ttf', '.otf')):
            return {'font': os.path.basename(p)}
        if p.endswith('.cs'):
            return {'script': self.script_name(guid)}
        return {'asset': p}


class Prefab:
    def __init__(self, rip, path):
        self.rip = rip
        self.name = os.path.basename(path)[:-7]
        self.docs = load_docs(path)
        self.go_of = {}            # component fileID -> GameObject fileID
        self.paths = {}            # GameObject fileID -> "Root/Child/..."
        for fid, (cid, cls, d) in self.docs.items():
            go = d.get('m_GameObject', {}).get('fileID')
            if go:
                self.go_of[fid] = go

    def transform_of(self, go_id):
        for fid in (c['component']['fileID'] for c in self.docs[go_id][2].get('m_Component', [])):
            if fid in self.docs and self.docs[fid][0] in (4, 224):
                return fid
        return None

    def roots(self):
        return [fid for fid, (cid, cls, d) in self.docs.items()
                if cid in (4, 224) and d.get('m_Father', {}).get('fileID', 0) == 0]

    def build(self):
        trees = [self.node(t, '') for t in self.roots()]
        return trees[0] if len(trees) == 1 else {'name': self.name, 'children': trees}

    def node(self, tf_id, parent_path):
        cid, cls, tf = self.docs[tf_id]
        go_id = tf['m_GameObject']['fileID']
        go = self.docs[go_id][2]
        go_name = str(go.get('m_Name', ''))
        path = (parent_path + '/' if parent_path else '') + go_name
        self.paths[go_id] = path
        n = {'name': go_name, 'active': bool(go.get('m_IsActive', 1)), 'layer': go.get('m_Layer', 0),
             'rect': self.rect(tf) if cid == 224 else None, 'components': [], 'children': []}
        for c in go.get('m_Component', []):
            fid = c['component']['fileID']
            if fid == tf_id or fid not in self.docs:
                continue
            ccid, ccls, cd = self.docs[fid]
            if ccid == 222:
                continue    # CanvasRenderer: implied by any graphic
            n['components'].append(self.component(ccid, ccls, cd))
        for ch in tf.get('m_Children', []):
            cfid = ch['fileID']
            if cfid in self.docs:
                n['children'].append(self.node(cfid, path))
            else:
                inst = self.prefab_instance_for_child(cfid)
                if inst:
                    n['children'].append(inst)
        return n

    @staticmethod
    def rect(tf):
        v = lambda k: [tf[k]['x'], tf[k]['y']] if k in tf else None
        return {'anchorMin': v('m_AnchorMin'), 'anchorMax': v('m_AnchorMax'), 'pos': v('m_AnchoredPosition'),
                'size': v('m_SizeDelta'), 'pivot': v('m_Pivot'),
                'scale': [tf['m_LocalScale']['x'], tf['m_LocalScale']['y'], tf['m_LocalScale']['z']],
                'rot': tf.get('m_LocalEulerAnglesHint', {}).get('z', 0)}

    def component(self, cid, cls, d):
        if cid == 114:
            t = self.rip.script_name(d.get('m_Script', {}).get('guid', '')) or 'MonoBehaviour'
        else:
            t = cls
        fields = {k: self.value(v) for k, v in d.items() if k not in SKIP_FIELDS}
        if cid == 114:
            fields['m_Enabled'] = bool(d.get('m_Enabled', 1))
        return {'type': t, 'fields': fields}

    def value(self, v):
        if isinstance(v, dict):
            if 'fileID' in v and len(v) <= 3:
                return self.ref(v)
            return {k: self.value(x) for k, x in v.items()}
        if isinstance(v, list):
            return [self.value(x) for x in v]
        return v

    def ref(self, r):
        fid = r.get('fileID', 0)
        if fid == 0:
            return None
        if r.get('guid'):
            return self.rip.resolve_external(r)
        if fid in self.docs:
            cid, cls, d = self.docs[fid]
            go = fid if cid == 1 else d.get('m_GameObject', {}).get('fileID')
            t = cls if cid != 114 else (self.rip.script_name(d.get('m_Script', {}).get('guid', '')) or 'MonoBehaviour')
            return {'ref': self.paths.get(go, str(go)), 'type': t, 'id': fid}
        return {'ref': str(fid)}

    def prefab_instance_for_child(self, stripped_tf_id):
        # A child transform that is not a document here belongs to a nested prefab instance (!u!1001).
        for fid, (cid, cls, d) in self.docs.items():
            if cid != 1001:
                continue
            src = d.get('m_SourcePrefab', {})
            mods = d.get('m_Modification', {}).get('m_Modifications', [])
            return {'prefabInstance': self.rip.resolve_external(src).get('prefab', src.get('guid')),
                    'modifications': [{'target': m.get('target', {}).get('fileID'), 'property': m.get('propertyPath'),
                                       'value': m.get('value'), 'objectReference': self.value(m.get('objectReference'))}
                                      for m in mods]}
        return None


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(2)
    assets, out = sys.argv[1], sys.argv[2]
    names = sys.argv[3:]
    rip = Rip(assets)
    go_dir = os.path.join(assets, 'GameObject')
    todo = names or sorted(f[:-7] for f in os.listdir(go_dir) if f.endswith('.prefab') and f.startswith('Arena'))
    os.makedirs(out, exist_ok=True)
    done = set()
    while todo:
        name = todo.pop(0)
        if name in done:
            continue
        done.add(name)
        path = os.path.join(go_dir, name + '.prefab')
        if not os.path.exists(path):
            print('missing prefab', name)
            continue
        try:
            tree = Prefab(rip, path).build()
        except Exception as e:
            print('FAILED', name, type(e).__name__, str(e).replace(chr(10), ' | ')[:300])
            continue
        with io.open(os.path.join(out, name + '.json'), 'w', encoding='utf-8') as f:
            json.dump(tree, f, indent=1, ensure_ascii=False)
        # Pull in prefabs this one instantiates so the runtime has them too.
        for m in re.finditer(r'"prefab(?:Instance)?": "([^"]+)"', json.dumps(tree)):
            if m.group(1) not in done:
                todo.append(m.group(1))
        print('ok', name)
    print(len(done), 'screens ->', out)


if __name__ == '__main__':
    main()
