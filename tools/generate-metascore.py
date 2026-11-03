"""Builds defaults/metascore.json (schema 2): a live-Arena metascore (MP) for every item the SPT server knows.

Sources, in priority order:
  1. Live values transcribed from the Arena Armory and ammo tables (tools/live-data/codex-2026-11-03), joined to SPT
     template ids by template_mapping.filled.json (tools/map-templates.py). Weapons, body armour, eyewear, meds,
     stims, pills, melee: per item. Ammo: per round.
  2. Category seeds for what live does not publish (helmets, helmet armour attachments, plates, rigs, headsets,
     grenades, magazines, weapon mods), taken from estimated_values.json missingMpCategorySeeds so tuning lives in
     data, applied to SPT item stats (armour class, cells, capacity, mod role, zoom).

Accounting rules written into the output for the server:
  - A weapon's live MP is its Armory default preset (as sold). Mods that are part of that default preset are
    listed in items[weapon].defaultPresetMods and must not be added again; other mods add their own MP.
  - Ammo is per round (perUnit = true): MP x rounds carried.
  - Soft armour inserts and plates that ship inside a live-valued armour are not added; plates bought separately are.
  - Band: total 7,500-10,000; per-bar limits are off (live 0.5.0+).

Usage: py tools\\generate-metascore.py
"""
import glob, io, json, os, re, sys
sys.stdout.reconfigure(encoding='utf-8', errors='replace')

ROOT = r'C:\Games\SPT-4.1'
DB = ROOT + r'\SPT_Runtime\SPT_Data\database'
HERE = os.path.dirname(os.path.abspath(__file__))
PKG = os.path.join(HERE, 'live-data', 'codex-2026-11-03', 'data')
OUT = [os.path.join(HERE, '..', 'ArenaMode.Server', 'defaults', 'metascore.json'),
       ROOT + r'\SPT_Runtime\user\mods\ArenaMode\config\metascore.json']
KITS = ROOT + r'\SPT_Runtime\user\mods\ArenaMode\config\kits.json'

items = json.load(io.open(DB + r'\templates\items.json', encoding='utf-8-sig'))
loc = json.load(io.open(DB + r'\locales\global\en.json', encoding='utf-8-sig'))
glob_ = json.load(io.open(DB + r'\globals.json', encoding='utf-8-sig'))
hb = json.load(io.open(DB + r'\templates\handbook.json', encoding='utf-8-sig'))
price = {h['Id']: h['Price'] for h in hb['Items']}

# WTT Content Backport (1.0 items) join the pool with their own ids, as on the running server.
for cfg in glob.glob(ROOT + r'\SPT_Runtime\user\mods\WTT-ContentBackport\db\CustomItems\*.json'):
    try:
        d = json.load(io.open(cfg, encoding='utf-8-sig'))
    except Exception:
        continue
    if not isinstance(d, dict):
        continue
    for tpl, e in d.items():
        if not isinstance(e, dict) or 'itemTplToClone' not in e or tpl in items or e['itemTplToClone'] not in items:
            continue
        base = items[e['itemTplToClone']]
        props = dict(base['_props']); props.update(e.get('overrideProperties') or {})
        items[tpl] = {'_id': tpl, '_type': 'Item', '_parent': e.get('parentId') or base['_parent'], '_name': tpl, '_props': props}
        en = (e.get('locales') or {}).get('en') or {}
        if en.get('name'):
            loc[tpl + ' Name'] = en['name']
        price[tpl] = e.get('handbookPriceRoubles') or 0

BAND = {'min': 7500, 'max': 10000}


def chain(tpl):
    n, out = tpl, []
    while n in items and len(out) < 12:
        out.append(n); n = items[n].get('_parent')
    return out


def name(tpl):
    return loc.get(tpl + ' Name', items.get(tpl, {}).get('_name', tpl))


def num(v, d=0.0):
    try:
        return float(v)
    except (TypeError, ValueError):
        return d


P = dict(AMMO='5485a8684bdc2da71d8b4567', WEAPON='5422acb9af1c889c16000029', ARMOR='5448e54d4bdc2dcc718b4568',
         VEST='5448e5284bdc2dcb718b4567', HEAD='5a341c4086f77401f2541505', PLATE='644120aa86ffbe10ee032b6f',
         ARMORED_EQUIP='57bef4c42459772e8d35a53b', INSERT='65649eb40bf0ed77b8044453', MEDS='543be5664bdc2dd4348b4569',
         STIM='5448f3a64bdc2d60728b456a', DRUGS='5448f3a14bdc2d27728b4569', HEADSET='5645bcb74bdc2ded0b8b4578',
         THROW='543be6564bdc2df4348b4568', MAG='5448bc234bdc2d3c308b4569', MOD='5448fe124bdc2da5018b4567',
         GLASSES='5448e5724bdc2ddf718b4568', KNIFE='5447e1d04bdc2dff2f8b4567',
         BARREL='555ef6e44bdc2de9068b457e', HANDGUARD='55818a104bdc2db9688b4569', STOCK='55818a594bdc2db9688b456a',
         PGRIP='55818a684bdc2ddd698b456d', FOREGRIP='55818af64bdc2d5b648b4570', FLASHHIDER='550aa4bf4bdc2dd6348b456b',
         MUZZLECOMBO='550aa4dd4bdc2dc9348b4569', SUPPRESSOR='550aa4cd4bdc2dd8348b456c', IRONSIGHT='55818ac54bdc2d5b648b456e',
         COLLIMATOR='55818ad54bdc2ddc698b4569', COMPACTCOLL='55818acf4bdc2dde698b456b', SCOPE='55818ae44bdc2dde698b456c',
         SPECIALSCOPE='55818aeb4bdc2ddc698b456a', ASSAULTSCOPE='55818add4bdc2d5b648b456f', LASER='55818b164bdc2ddc698b456c',
         FLASHLIGHT='55818b084bdc2d5b648b4571', TACTICALCOMBO='55818b0e4bdc2dde698b456e', MOUNT='55818b224bdc2dde698b456f',
         RECEIVER='55818a304bdc2db5418b457d', BIPOD='56ea9461d2720b67698b456f')

seeds = json.load(io.open(os.path.join(PKG, 'estimated_values.json'), encoding='utf-8'))['missingMpCategorySeeds']
armory = {r['id']: r for r in json.load(io.open(os.path.join(PKG, 'resolved_armory.json'), encoding='utf-8'))}
ammo = {r['id']: r for r in json.load(io.open(os.path.join(PKG, 'ammo.json'), encoding='utf-8'))}
mapping = json.load(io.open(os.path.join(PKG, 'template_mapping.filled.json'), encoding='utf-8'))['mappings']

CAT_BAR = {'assault_rifle': 'firepower', 'smg': 'firepower', 'shotgun': 'firepower', 'lmg': 'firepower',
           'marksman_rifle': 'firepower', 'bolt_action_rifle': 'firepower', 'assault_carbine': 'firepower',
           'pistol': 'firepower', 'melee': 'firepower', 'body_armor': 'defense', 'eyewear': 'defense',
           'medkit': 'utility', 'injury_treatment': 'utility', 'pills': 'utility', 'stimulant': 'utility'}

table = {}

# ---- 1. live values -------------------------------------------------------------------------------------------
for m in mapping:
    if m['mappingStatus'] != 'mapped' or not m['gameTemplateIds']:
        continue
    tpl = m['gameTemplateIds'][0]
    rid = m['recordId']
    if rid in armory:
        r = armory[rid]
        mp = r.get('effectiveMp')
        if mp is None:
            continue
        table[tpl] = {'bar': CAT_BAR.get(r['category'], 'utility'), 'score': mp, 'source': 'live',
                      'live': {'recordId': rid, 'category': r['category'], 'classes': r.get('allowedClasses'),
                               'unlock': (r.get('unlock') or {}).get('raw'), 'purchase': r.get('effectivePurchase')}}
    elif rid in ammo:
        r = ammo[rid]
        table[tpl] = {'bar': 'firepower', 'score': r['mpPerRound'], 'perUnit': True, 'source': 'live',
                      'live': {'recordId': rid, 'classAccess': {k: v['status'] for k, v in r['classAccess'].items()},
                               'notes': r.get('notesRaw')}}
live_count = len(table)

# ---- 2. seeds for the rest -------------------------------------------------------------------------------------
presets_by_root = {}
for p in glob_['ItemPresets'].values():
    root = p.get('_encyclopedia') or next((i['_tpl'] for i in p['_items'] if not i.get('parentId')), None)
    if not root:
        continue
    cur = presets_by_root.get(root)
    if cur is None or ('default' in p['_name'].lower()) > ('default' in cur['_name'].lower()) \
            or (('default' in p['_name'].lower()) == ('default' in cur['_name'].lower()) and len(p['_items']) < len(cur['_items'])):
        presets_by_root[root] = p


def armor_class(tpl):
    return int(num(items.get(tpl, {}).get('_props', {}).get('armorClass')))


def helmet_class(tpl):
    """Max class across the helmet and its default inserts."""
    best = armor_class(tpl)
    p = presets_by_root.get(tpl)
    for node in (p['_items'] if p else []):
        best = max(best, armor_class(node['_tpl']))
    for sl in items[tpl]['_props'].get('Slots') or []:
        for f in sl.get('_props', {}).get('filters', []):
            for t in f.get('Filter', []):
                if t in items and items[t]['_parent'] == P['INSERT']:
                    best = max(best, armor_class(t))
    return best


def cells(tpl):
    return sum(g['_props']['cellsH'] * g['_props']['cellsV'] for g in (items[tpl]['_props'].get('Grids') or []))


def seed_class(tbl, ac):
    if ac <= 0:
        return None
    return tbl['armorClassMp'].get(str(min(ac, 6)))


def role(tpl, ch):
    p = items[tpl]['_props']
    if P['SUPPRESSOR'] in ch: return 'suppressor'
    if P['FLASHHIDER'] in ch or P['MUZZLECOMBO'] in ch: return 'muzzle_brake'
    if P['SCOPE'] in ch or P['SPECIALSCOPE'] in ch or P['ASSAULTSCOPE'] in ch:
        zooms = p.get('Zooms') or []
        z = max([num(x, 1) for x in zooms[0]] + [1.0]) if zooms else 1.0
        return 'scope_high_power' if z > 4 else 'scope_low_power'
    if P['COLLIMATOR'] in ch or P['COMPACTCOLL'] in ch: return 'reflex_sight'
    if P['IRONSIGHT'] in ch: return 'iron_sight'
    if P['BARREL'] in ch: return 'barrel'
    if P['HANDGUARD'] in ch: return 'handguard'
    if P['STOCK'] in ch: return 'stock'
    if P['PGRIP'] in ch: return 'pistol_grip'
    if P['FOREGRIP'] in ch or P['BIPOD'] in ch: return 'foregrip'
    if P['LASER'] in ch: return 'laser'
    if P['FLASHLIGHT'] in ch: return 'flashlight'
    if P['TACTICALCOMBO'] in ch: return 'combo_tactical'
    if P['MOUNT'] in ch: return 'mount'
    if P['RECEIVER'] in ch: return 'receiver'
    return 'adapter'


headsets = sorted([t for t in items if items[t].get('_type') == 'Item' and P['HEADSET'] in set(chain(t)[1:])],
                  key=lambda t: price.get(t, 0))
headset_tier = {t: ('basic' if i < len(headsets) / 3 else 'standard' if i < 2 * len(headsets) / 3 else 'premium')
                for i, t in enumerate(headsets)}

for tpl, it in items.items():
    if it.get('_type') != 'Item' or tpl in table:
        continue
    ch = set(chain(tpl)[1:])
    p = it['_props']
    s = None; bar = None; basis = None
    if P['HEAD'] in ch:
        hc = helmet_class(tpl)
        if hc > 0:
            s = seed_class(seeds['helmet'], hc); bar = 'defense'; basis = f'helmet class {hc}'
    elif P['ARMORED_EQUIP'] in ch and armor_class(tpl) > 0:
        s = seed_class(seeds['helmet_attachment_armor'], armor_class(tpl)); bar = 'defense'; basis = f'helmet attachment class {armor_class(tpl)}'
    elif P['PLATE'] in ch:
        s = seed_class(seeds['armor_plate'], armor_class(tpl)); bar = 'defense'; basis = f'plate class {armor_class(tpl)}'
    elif P['INSERT'] in ch:
        s = 0; bar = 'defense'; basis = 'built-in insert, priced with its carrier'
    elif P['ARMOR'] in ch:
        # armour not in the live table: price like a plate carrier of its class
        s = seed_class(seeds['armor_plate'], armor_class(tpl)) or seeds['armored_rig_empty_carrier']['base']; bar = 'defense'; basis = 'armor not in live table'
    elif P['VEST'] in ch:
        c = cells(tpl)
        if p.get('BlocksArmorVest'):
            s = 60 + 6 * c; basis = f'armored rig carrier, {c} cells'
        else:
            s = 40 + 6 * c; basis = f'chest rig, {c} cells'
        bar = 'utility'
    elif P['HEADSET'] in ch:
        s = seeds['headset']['tiers'][headset_tier[tpl]]; bar = 'utility'; basis = f'headset {headset_tier[tpl]}'
    elif P['THROW'] in ch:
        tt = (p.get('ThrowType') or '').lower(); nm = name(tpl).lower()
        kind = 'flash' if 'flash' in tt or 'flash' in nm else 'smoke' if 'smoke' in tt or 'smoke' in nm \
            else 'impact' if 'rgn' in nm or 'rgo' in nm or 'impact' in nm else 'mini_fragmentation' if 'v40' in nm or 'mini' in nm else 'fragmentation'
        s = seeds['grenade']['types'][kind]; bar = 'utility'; basis = f'grenade {kind}'
    elif P['MAG'] in ch:
        cap = sum(int(c.get('_max_count') or 0) for c in (p.get('Cartridges') or []))
        s = max(20, 20 + 2 * cap) * (1.25 if cap >= 50 or 'drum' in name(tpl).lower() else 1); bar = 'firepower'; basis = f'magazine {cap}'
    elif P['MOD'] in ch:
        rl = role(tpl, ch)
        s = seeds['weapon_attachment']['roles'][rl]; bar = 'firepower'; basis = f'mod {rl}'
    elif P['AMMO'] in ch and p.get('ammoType') in ('bullet', 'buckshot'):
        # ammo missing from the live table: scale by penetration against live rounds of the same caliber
        same = [v for t, v in table.items() if v.get('perUnit') and items[t]['_props'].get('Caliber') == p.get('Caliber')]
        if same:
            ref = max(same, key=lambda v: v['score'])
            s = round(ref['score'] * max(0.3, num(p.get('PenetrationPower')) / max(1.0, num(items[[t for t, v in table.items() if v is ref][0]]['_props'].get('PenetrationPower')))), 1)
        else:
            s = round(num(p.get('PenetrationPower')) * 0.3 + 1, 1)
        table[tpl] = {'bar': 'firepower', 'score': s, 'perUnit': True, 'source': 'derived', 'basis': 'pen-scaled from live caliber'}
        continue
    elif P['WEAPON'] in ch or P['KNIFE'] in ch or P['GLASSES'] in ch or P['MEDS'] in ch or P['STIM'] in ch or P['DRUGS'] in ch:
        # not in the live Armory: not offered in Arena. Keep a derived number so a modded kit still scores.
        wc = p.get('weapClass')
        s = {'assaultRifle': 2700, 'assaultCarbine': 3500, 'marksmanRifle': 3900, 'sniperRifle': 1900, 'smg': 2700,
             'shotgun': 2000, 'machinegun': 1850, 'pistol': 380, 'revolver': 380}.get(wc, 0 if P['KNIFE'] in ch else 25 if P['GLASSES'] in ch else 150)
        bar = CAT_BAR['melee'] if P['KNIFE'] in ch else 'defense' if P['GLASSES'] in ch else 'utility' if (P['MEDS'] in ch or P['STIM'] in ch or P['DRUGS'] in ch) else 'firepower'
        basis = 'category median, not offered in Arena'
    if s is not None:
        table[tpl] = {'bar': bar, 'score': round(s, 1), 'source': 'derived', 'basis': basis}

# A root item's MP covers its default preset: weapon mods on an Armory weapon, plates and inserts inside an armour,
# a helmet's inserts. Those children are listed so the server does not add them again.
for tpl, v in table.items():
    ch = set(chain(tpl)[1:])
    if P['WEAPON'] in ch or P['ARMOR'] in ch or P['VEST'] in ch or P['HEAD'] in ch:
        p = presets_by_root.get(tpl)
        if p:
            v['defaultPresetMods'] = sorted({n['_tpl'] for n in p['_items'] if n['_tpl'] != tpl})

out = {'schema': 2, 'band': BAND, 'bars': ['firepower', 'defense', 'utility'],
       'accounting': ['weapon live MP = Armory default preset; skip defaultPresetMods when summing',
                      'perUnit items (ammo) = score x rounds carried',
                      'built-in inserts score 0; plates inside a live-valued armour are not added'],
       'items': table}
for path in OUT:
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with io.open(path, 'w', encoding='utf-8') as f:
        json.dump(out, f, indent=1, ensure_ascii=False)

derived = sum(1 for v in table.values() if v['source'] == 'derived')
print(f'live {live_count}  derived {derived}  total {len(table)} scored items')


# ---- sanity: stock Arena Mode kits ------------------------------------------------------------------------------
def kit_total(kit):
    bars = {'firepower': 0.0, 'defense': 0.0, 'utility': 0.0}
    nodes = kit['items']
    by_id = {n['_id']: n for n in nodes}
    skip = set()
    for n in nodes:
        v = table.get(n['_tpl'])
        if v and v.get('defaultPresetMods'):
            skip |= set(v['defaultPresetMods'])
    lines = []
    for n in nodes:
        v = table.get(n['_tpl'])
        if not v:
            continue
        if n['_tpl'] in skip and n['_tpl'] != kit['items'][0]['_tpl']:
            continue
        cnt = (n.get('upd') or {}).get('StackObjectsCount', 1) if v.get('perUnit') else 1
        bars[v['bar']] += v['score'] * cnt
        lines.append((v['score'] * cnt, v['bar'], name(n['_tpl']), cnt, v['source']))
    for sitem in kit.get('fill', {}).get('rigContents', []):
        v = table.get(sitem['tpl'])
        if v:
            c = sitem.get('count', 1) if v.get('perUnit') else 1
            bars[v['bar']] += v['score'] * c
            lines.append((v['score'] * c, v['bar'], name(sitem['tpl']), c, v['source']))
    return bars, sum(bars.values()), lines


if os.path.exists(KITS):
    kits = json.load(io.open(KITS, encoding='utf-8-sig'))
    print('stock kits under live MP:')
    for k in kits:
        bars, tot, lines = kit_total(k)
        flag = '' if BAND['min'] <= tot <= BAND['max'] else '  <- outside band'
        print(f"  {k['name']:14} {tot:7.0f}  fp {bars['firepower']:6.0f} def {bars['defense']:5.0f} util {bars['utility']:5.0f}{flag}")
    show = os.environ.get('KIT')
    if show:
        k = next(k for k in kits if k['name'].lower() == show.lower())
        for sc, bar, nm, cnt, src in sorted(kit_total(k)[2], reverse=True)[:25]:
            print(f'    {sc:7.1f} {bar:9} x{cnt:<3} {src:7} {nm}')
