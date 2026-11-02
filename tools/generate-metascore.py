"""Builds metascore.json: a live-Arena-style metascore for every item SPT knows.

Live Arena rates each item with one number and shows a preset as Firepower
(weapon, ammo, mods, magazines), Defense (armour, plates, helmets, shields) and
Utility (meds, rigs, backpacks, headsets, grenades) bars; the total must sit in a
band (live: 7,500-10,000). BSG does not publish per-item values, so this derives
them from the item stats and calibrates the scale on two reference builds:
a starter build pinned to the band floor and a meta build pinned to the ceiling.
ponytail: linear weights below are the ceiling; hand-tune outliers in overrides.

Usage: py Tools\\arena\\metascore.py
Writes defaults/metascore.json in the fork and the live server config copy.
"""
import io, json, sys, os
sys.stdout.reconfigure(encoding='utf-8', errors='replace')

ROOT = r'C:\Games\SPT-4.1'
DB = ROOT + r'\SPT_Runtime\SPT_Data\database'
OUT = [ROOT + r'\Tools\src\ArenaMode\ArenaMode.Server\defaults\metascore.json',
       ROOT + r'\SPT_Runtime\user\mods\ArenaMode\config\metascore.json']
KITS = ROOT + r'\SPT_Runtime\user\mods\ArenaMode\config\kits.json'

items = json.load(io.open(DB + r'\templates\items.json', encoding='utf-8-sig'))
loc = json.load(io.open(DB + r'\locales\global\en.json', encoding='utf-8-sig'))

BAND = {'min': 7500, 'max': 10000}          # live band (tarkovbot.eu/arena/metascore, 2026)
SHARE = {'firepower': 0.5, 'defense': 0.3, 'utility': 0.2}   # how the band splits across bars

# Reference builds (template ids). Starter = band floor, Meta = band ceiling.
STARTER = ['5c07c60e0db834002330051f',  # ADAR 2-15 (default preset)
           '54527a984bdc2d4e668b4567',  # M855
           '5c0e446786f7742013381639',  # 6B5-15 Zh-86 Uley armored rig (class 4)
           '5645bc214bdc2d363b8b4571',  # FORT Kiver-M helmet
           '544fb45d4bdc2dee738b4568',  # Salewa
           ]
META = ['5447a9cd4bdc2dbd208b4567',     # M4A1 (default preset)
        '59e690b686f7746c9f75e848',     # M995
        '545cdb794bdc2d3a198b456a',     # 6B43 Zabralo-Sh (class 6)
        '5aa7e276e5b5b000171d0647',     # Altyn helmet
        '590c657e86f77412b013051d',     # Grizzly
        '5e4abb5086f77406975c9342',     # LBT-6094A Slick plate carrier
        '5645bcc04bdc2d363b8b4572',     # Peltor ComTac II
        '558022b54bdc2dac148b458d',     # EOTech EXPS3-0
        '5c0e530286f7747fa1419862',     # Propital
        '5710c24ad2720bc3458b45a3',     # F-1 grenade
        ]
glob = json.load(io.open(DB + r'\globals.json', encoding='utf-8-sig'))


def expand(build, fullest=False):
    """Root template ids -> every template in the item's preset (mods, plates, inserts).
    Plainest preset for the starter, fullest for the meta build (live: mods are up to 40% of a preset)."""
    best = {}
    for p in glob['ItemPresets'].values():
        root = p.get('_encyclopedia') or next((i['_tpl'] for i in p['_items'] if not i.get('parentId')), None)
        if root in build and (root not in best or (len(p['_items']) > len(best[root])) == fullest):
            best[root] = p['_items']
    out = []
    for t in build:
        out += [i['_tpl'] for i in best[t]] if t in best else [t]
    return out



def chain(tpl):
    n, out = tpl, []
    while n in items and len(out) < 12:
        out.append(n)
        n = items[n].get('_parent')
    return out


def name(tpl):
    return loc.get(tpl + ' Name', items.get(tpl, {}).get('_name', tpl))


def num(v, d=0.0):
    try:
        return float(v)
    except (TypeError, ValueError):
        return d


AMMO = '5485a8684bdc2da71d8b4567'; WEAPON = '5422acb9af1c889c16000029'
ARMOR = '5448e54d4bdc2dcc718b4568'; VEST = '5448e5284bdc2dcb718b4567'; HEAD = '5a341c4086f77401f2541505'
PLATE = '644120aa86ffbe10ee032b6f'; SHIELD = '5a341c4686f77469e155819e'; MEDS = '543be5664bdc2dd4348b4569'
BACKPACK = '5448e53e4bdc2d60728b4567'; HEADSET = '5645bcb74bdc2ded0b8b4578'; THROW = '543be6564bdc2df4348b4568'
MAG = '5448bc234bdc2d3c308b4569'; MOD = '5448fe124bdc2da5018b4567'; SIGHT = '55818ad54bdc2ddc698b4569'

WEAPON_BASE = {'assaultRifle': 400, 'assaultCarbine': 350, 'marksmanRifle': 420, 'sniperRifle': 380,
               'smg': 300, 'shotgun': 260, 'machinegun': 450, 'pistol': 90, 'revolver': 90, 'grenadeLauncher': 300}
EFFECT = {'HeavyBleeding': 150, 'LightBleeding': 50, 'Fracture': 100, 'Pain': 40, 'DestroyedPart': 200, 'Contusion': 20}


def raw(tpl):
    """(bar, raw points) or None for items with no combat value."""
    it = items.get(tpl)
    if not it or it['_type'] != 'Item':
        return None
    p = it['_props']
    ch = set(chain(tpl)[1:])
    if AMMO in ch:
        if p.get('ammoType') not in ('bullet', 'buckshot'):
            return None
        proj = max(1, int(p.get('ProjectileCount') or 1))
        dmg = min(num(p.get('Damage')) * proj, 180)
        return 'firepower', num(p.get('PenetrationPower')) * 9 + dmg * 1.5 + num(p.get('ArmorDamage')) * 1.0
    if WEAPON in ch:
        rec = max(0.0, 800 - num(p.get('RecoilForceUp')) - num(p.get('RecoilForceBack')))
        modes = p.get('weapFireType') or []
        auto = 120 if 'fullauto' in modes else (60 if 'burst' in modes else 0)
        return 'firepower', (WEAPON_BASE.get(p.get('weapClass'), 150) + num(p.get('Ergonomics')) * 1.5
                             + rec * 0.4 + auto + min(num(p.get('bFirerate')), 900) / 12)
    if PLATE in ch or ARMOR in ch or (VEST in ch and p.get('BlocksArmorVest')):
        ac = num(p.get('armorClass'))
        zones = len(p.get('armorColliders') or []) + len(p.get('armorPlateColliders') or [])
        if PLATE in ch:
            return 'defense', ac * ac * 25 + num(p.get('Durability')) * 1.5 + zones * 15
        return 'defense', ac * ac * 40 + num(p.get('Durability')) * 1.0 + zones * 20 + (60 if VEST in ch else 0)
    if HEAD in ch or SHIELD in ch:
        ac = num(p.get('armorClass'))
        if ac <= 0 and not p.get('armorColliders'):
            return None
        zones = len(p.get('armorColliders') or [])
        return 'defense', ac * ac * 30 + zones * 25 + num(p.get('Durability')) * 0.8
    if VEST in ch or BACKPACK in ch:
        cells = sum(g['_props']['cellsH'] * g['_props']['cellsV'] for g in (p.get('Grids') or []))
        return 'utility', cells * (10 if VEST in ch else 3)
    if MEDS in ch:
        hp = num(p.get('MaxHpResource')); rate = num(p.get('hpResourceRate'))
        eff = sum(EFFECT.get(k, 0) for k in (p.get('effects_damage') or {}))
        fast = max(0.0, 8 - num(p.get('medUseTime'), 8)) * 10
        return 'utility', min(hp, 400) * 0.5 + rate * 1.0 + eff + fast
    if HEADSET in ch:
        return 'utility', 250 - num(p.get('Distortion')) * 600 + num(p.get('CompressorGain')) * 8
    if THROW in ch:
        return 'utility', 160 if 'frag' in (p.get('ThrowType') or '') else 80
    if MAG in ch:
        cap = sum(int(c.get('_max_count') or 0) for c in (p.get('Cartridges') or []))
        return 'firepower', min(cap, 60) * 2 + num(p.get('Ergonomics')) * 1.0
    if MOD in ch:
        v = num(p.get('Ergonomics')) * 2.5 - num(p.get('Recoil')) * 25 + num(p.get('Accuracy')) * 2
        if SIGHT in ch:
            zooms = p.get('Zooms') or []
            rng = max([num(z, 1) for z in zooms[0]] + [1.0]) if zooms else 1.0
            v += 60 + min(rng, 8) * 20
        if v <= 0:
            return None
        return 'firepower', v
    return None


scores = {}
for tpl in items:
    r = raw(tpl)
    if r:
        scores[tpl] = r


def raw_total(build):
    t = {'firepower': 0.0, 'defense': 0.0, 'utility': 0.0}
    for tpl in build:
        r = scores.get(tpl)
        if r:
            t[r[0]] += r[1]
    return t


STARTER, META = expand(STARTER), expand(META, fullest=True)
st, me = raw_total(STARTER), raw_total(META)
for cat in SHARE:
    assert me[cat] > st[cat], f'meta build must beat starter on {cat}: {me[cat]} vs {st[cat]}'
# Affine map per bar: the starter build lands on the floor share, the meta build on the ceiling share.
scale = {}
for cat in SHARE:
    lo, hi = BAND['min'] * SHARE[cat], BAND['max'] * SHARE[cat]
    a = (hi - lo) / max(1e-6, me[cat] - st[cat])
    scale[cat] = (a, lo - a * st[cat])


def build_bars(build):
    bars = {cat: scale[cat][1] + sum(scale[cat][0] * scores[t][1] for t in build
                                     if t in scores and scores[t][0] == cat) for cat in SHARE}
    return bars, sum(bars.values())


out = {
    'schema': 1,
    'band': BAND,
    'bars': {cat: {'base': round(scale[cat][1]), 'share': SHARE[cat]} for cat in SHARE},
    'items': {tpl: {'bar': r[0], 'score': round(scale[r[0]][0] * r[1])} for tpl, r in scores.items()},
}
for path in OUT:
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with io.open(path, 'w', encoding='utf-8') as f:
        json.dump(out, f, indent=1)

print('items scored', len(scores))
for lbl, b in (('starter', STARTER), ('meta', META)):
    bars, tot = build_bars(b)
    print(f'{lbl:8} total {tot:6.0f}', {k: round(v) for k, v in bars.items()})
    for t in b:
        if t not in items:
            print('   MISSING tpl', t)
        elif t not in scores:
            print('   unscored', name(t))

kits = json.load(io.open(KITS, encoding='utf-8-sig'))
print('stock kits:')
for k in kits:
    tpls = [n['_tpl'] for n in k['items']] + [s['tpl'] for s in k.get('fill', {}).get('rigContents', [])]
    bars, tot = build_bars(tpls)
    flag = '' if BAND['min'] <= tot <= BAND['max'] else '  <- outside band'
    print(f"  {k['name']:14} {tot:6.0f}  fp {bars['firepower']:5.0f} def {bars['defense']:5.0f} util {bars['utility']:5.0f}{flag}")

assert abs(build_bars(STARTER)[1] - BAND['min']) < 1 and abs(build_bars(META)[1] - BAND['max']) < 1
