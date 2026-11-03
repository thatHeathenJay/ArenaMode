"""Writes the kit catalogue as EFT: Arena starter presets: three per class (Assault, Enforcer, Scout, Marksman),
each around a weapon that is unlocked from the start in live Arena (metascore.json live records), protected by
armour the class rules in classes.json allow, scored against the 7,500-10,000 band.

  py generate-arena-kits.py [--out <kits.json>]  (default: ArenaMode.Server/defaults/kits.json)

Reuses generate-kits.py for the SPT database, preset grafting, plate filling, headsets and medicine."""
import collections, io, json, os, sys, uuid

sys.stdout.reconfigure(encoding='utf-8', errors='replace')
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import generate_kits_shim  # noqa: E402  (see bottom: generate-kits.py has a dash in its name)

gk = generate_kits_shim.module
items, glob, loc = gk.items, gk.glob, gk.loc
META = json.load(io.open(os.path.join(HERE, '..', 'ArenaMode.Server', 'defaults', 'metascore.json'), encoding='utf-8'))
CLASSES = json.load(io.open(os.path.join(HERE, '..', 'ArenaMode.Server', 'defaults', 'classes.json'), encoding='utf-8'))
BAND = CLASSES['band']

SECURED = '544a11ac4bdc2d470e8b456a'   # Alpha container: every stock kit carries one (KitBuilder keeps the real one)
GRENADE = '5448be9a4bdc2dfd2f8b456a'   # RGD-5

# Weapon (SPT preset id) per class tier, light to heavy; all "初期開放" (initially unlocked) in live Arena.
# Ammo per weapon: a round live Arena lists for that class (metascore live classAccess).
STARTERS = {
    'assault': [('5841474424597759ba49be91', '56dff061d2720bb5668b4567', 3),   # AK-74N / 5.45 BT
                ('6193e226449ec003d9127fa6', '54527ac44bdc2d36668b4567', 4),   # SCAR-L / M855A1
                ('5b439b5686f77428bd137424', '58dd3ad986f77403051cba8f', 5)],  # SA-58 / M80
    'enforcer': [('58414907245977598f1ad38d', '5d6e6806a4b936088465b17e', 4),  # MP-153 / 12ga Magnum
                 ('5f06d6bb4010601e3232cd22', '5d6e6911a4b9361bd5780d52', 5),  # M590A1 / Flechette
                 ('64cf7b2d033b747d625b7666', '64b8f7968532cf95ee0a0dbf', 6)], # PKP / 7.62x54R FMJ
    'scout': [('59b81f7386f77421ac688a0a', '5efb0da7a29a85116f6ea05f', 3),     # PP-19-01 Vityaz / PBP
              ('59411aa786f7747aeb37f9a5', '5efb0da7a29a85116f6ea05f', 4),     # MP5 / PBP
              ('63986a38608a960125446db5', '6576f4708ca9c4381d16cd9d', 5)],    # SR-2M / 7N42
    'marksman': [('5b8683a486f77467f2423114', '5887431f2459777e1612938f', 2),  # Mosin / LPS
                 ('5ac4ad3686f774181345c3da', '58dd3ad986f77403051cba8f', 3),  # M1A / M80
                 ('5c98be1e86f7741cc96ffd79', '5887431f2459777e1612938f', 4)], # SVDS / LPS
}


def zones(preset):
    out = set()
    for n in gk.surviving(preset):
        p = items.get(n['_tpl'], {}).get('_props', {})
        out.update(p.get('armorColliders') or [])
        out.update(p.get('armorPlateColliders') or [])
    return out


def plates_filled(preset):
    return any(n.get('slotId', '').lower().startswith('plate') for n in preset['_items'])


def allowed_armor(cls, preset):
    rule = CLASSES['classes'][cls]['armor']['rule']
    z = zones(preset)
    if rule == 'no-neck-protection':
        return not (z & {'NeckFront', 'NeckBack'})
    if rule == 'no-groin-or-stomach-protection':
        return not (z & {'Pelvis', 'PelvisBack', 'RibcageLow'})
    if rule == 'no-additional-armor-elements':
        return not plates_filled(preset)
    if rule == 'minimal-mobility-penalty':
        root = next(i for i in preset['_items'] if not i.get('parentId'))
        p = items[root['_tpl']]['_props']
        return (p.get('speedPenaltyPercent') or 0) >= -8 and (p.get('mousePenalty') or 0) >= -4
    return True


def pick_armor(cls, target):
    """Body armour or armoured rig nearest the target class the class rule admits; most coverage wins."""
    cap = CLASSES['classes'][cls]['armor']['maxClass']
    pool = [('ArmorVest', a) for a in gk.armors] + [('TacticalVest', v) for v in gk.armored_rigs]
    pool = [(slot, a) for slot, a in pool
            if 0 < gk.preset_class(a[1]) <= cap and allowed_armor(cls, a[1])]
    pool.sort(key=lambda e: (abs(gk.preset_class(e[1][1]) - target), -gk.coverage(e[1][1]), len(e[1][1]['_items'])))
    return pool[0] if pool else None


def pick_helmet(cls, target, used):
    cap = CLASSES['classes'][cls]['helmets']['maxClass']
    ranked = sorted((h for h in gk.helmets if 0 < gk.preset_class(h[1]) <= cap),
                    key=lambda h: (abs(gk.preset_class(h[1]) - target) + (0 if gk.open_eared(h[1]) else 1.5),
                                   1 if h[0] in used else 0, -gk.coverage(h[1]), len(h[1]['_items'])))
    if not ranked:
        return None
    used.add(ranked[0][0])
    return ranked[0]


def score_of(out, fill):
    """Metascore per metascore.json accounting: weapon live score covers its default preset mods."""
    table = META['items']
    weapon = next((n for n in out if n.get('slotId') == 'FirstPrimaryWeapon'), None)
    skip = set()
    if weapon and weapon['_tpl'] in table and table[weapon['_tpl']].get('source') == 'live':
        skip = set(table[weapon['_tpl']].get('defaultPresetMods') or [])
        below = {weapon['_id']}
        grew = True
        while grew:
            grew = False
            for n in out:
                if n.get('parentId') in below and n['_id'] not in below and n['_tpl'] in skip:
                    below.add(n['_id']); grew = True
        skip = below
    total = 0.0
    for n in out:
        if n['_id'] in skip:
            continue
        e = table.get(n['_tpl'])
        if not e:
            continue
        count = (n.get('upd') or {}).get('StackObjectsCount', 1) if e.get('perUnit') else 1
        total += e['score'] * count
    mag = next((n for n in out if n.get('slotId') == 'mod_magazine'), None)
    if mag:
        total += (table.get(mag['_tpl'], {}).get('score') or 0) * fill['spareMagazines']
        rounds = next((n for n in out if n.get('parentId') == mag['_id']), None)
        if rounds and table.get(rounds['_tpl'], {}).get('perUnit'):
            total += table[rounds['_tpl']]['score'] * (rounds.get('upd') or {}).get('StackObjectsCount', 0) * fill['spareMagazines']
    for r in fill['rigContents']:
        total += (table.get(r['tpl'], {}).get('score') or 0) * r['count']
    return round(total)


def footprint(tpl):
    p = items.get(tpl, {}).get('_props', {})
    return max(1, p.get('Width', 1) * p.get('Height', 1))


def fit_rig(rig_tpl, mag_tpl, meds):
    """Spare magazines and rig medicine that actually fit the rig's cells (the entry kit refuses to start when a
    required supply has no cell). Order: grenade cell, medicine, then as many spare magazines as remain (at least
    one); medicine is trimmed from the back when even that does not fit."""
    cells = gk.cells(rig_tpl)
    meds = list(meds)
    mag_fp = footprint(mag_tpl)
    while meds and cells - 1 - sum(footprint(m) for m in meds) < mag_fp:
        if len(meds) == 1:
            break
        meds.pop()
    room = cells - 1 - sum(footprint(m) for m in meds)
    spares = max(1, min(5, room // mag_fp)) if room >= mag_fp else 1
    return spares, meds


def build(cls, tier, preset_id, ammo, armor_target, used_helmets, used_ears):
    preset = glob['ItemPresets'][preset_id]
    root_id = uuid.uuid4().hex[:24]
    out = [dict(_id=root_id, _tpl=gk.ROOT_TPL)]
    gk.graft(out, preset, root_id, 'FirstPrimaryWeapon')
    weapon = next(n for n in out if n.get('slotId') == 'FirstPrimaryWeapon')
    weapon['upd'] = {'Repairable': {'Durability': 100, 'MaxDurability': 100}, 'FireMode': {'FireMode': 'single'}}
    mag = next((n for n in out if n.get('slotId') == 'mod_magazine' and n.get('parentId') == weapon['_id']), None)
    if mag is None:
        slot = next(s for s in items[weapon['_tpl']]['_props']['Slots'] if s['_name'] == 'mod_magazine')
        mag = dict(_id=uuid.uuid4().hex[:24], _tpl=slot['_props']['filters'][0]['Filter'][0],
                   parentId=weapon['_id'], slotId='mod_magazine')
        out.append(mag)
    capacity = items[mag['_tpl']]['_props']['Cartridges'][0]['_max_count']
    out.append(dict(_id=uuid.uuid4().hex[:24], _tpl=ammo, parentId=mag['_id'], slotId='cartridges',
                    upd={'StackObjectsCount': capacity}))
    chambers = items[weapon['_tpl']]['_props'].get('Chambers') or []
    if chambers:
        out.append(dict(_id=uuid.uuid4().hex[:24], _tpl=ammo, parentId=weapon['_id'], slotId=chambers[0]['_name']))

    armor = pick_armor(cls, armor_target)
    rig_tpl = None
    if armor:
        slot, (tpl, apreset) = armor
        gk.graft(out, apreset, root_id, slot)
        if slot == 'TacticalVest':
            rig_tpl = tpl
    if rig_tpl is None:
        # Plain rig beside body armour: near the wanted size, never below what the supplies need.
        want = 20 - (armor_target - 2) * 2
        need = 1 + sum(footprint(m) for m in gk.tier_list(gk.RIG_MEDICAL, armor_target)) + 3 * footprint(mag['_tpl'])
        candidates = [t for t in gk.plain_rigs if gk.cells(t) >= need] or gk.plain_rigs
        rig_tpl = min(candidates, key=lambda t: abs(gk.cells(t) - max(want, need)))
        out.append(dict(_id=uuid.uuid4().hex[:24], _tpl=rig_tpl, parentId=root_id, slotId='TacticalVest'))
    helmet = pick_helmet(cls, armor_target, used_helmets)
    if helmet:
        gk.graft(out, helmet[1], root_id, 'Headwear')
    gk.fill_empty_plates(out, root_id)
    ear = gk.pick_earpiece(out, used_ears)
    if ear:
        out.append(dict(_id=uuid.uuid4().hex[:24], _tpl=ear, parentId=root_id, slotId='Earpiece'))
    out.append(dict(_id=uuid.uuid4().hex[:24], _tpl=SECURED, parentId=root_id, slotId='SecuredContainer'))
    out.append(dict(_id=uuid.uuid4().hex[:24], _tpl=gk.POCKETS, parentId=root_id, slotId='Pockets'))
    gk.stock_medicine(out, armor_target)

    spares, meds = fit_rig(rig_tpl, mag['_tpl'], gk.tier_list(gk.RIG_MEDICAL, armor_target))
    fill = dict(spareMagazines=spares,
                rigContents=[dict(tpl=t, count=1) for t in meds]
                + [dict(tpl=GRENADE, count=1, optionalQuickUseOnly=True)])
    label = CLASSES['classes'][cls]['label']
    short = loc.get(weapon['_tpl'] + ' ShortName', gk.nm(weapon['_tpl']))
    kit = dict(id=f'{cls}-{tier + 1}', name=f'{label} · {short}', order=list(STARTERS).index(cls) * 3 + tier,
               items=out, fill=fill)
    armour_name = gk.nm(armor[1][0]) if armor else '-'
    print(f"{kit['id']:12} {short:14} {armour_name[:30]:30} {gk.nm(rig_tpl)[:24]:24} "
          f"{(gk.nm(helmet[0]) if helmet else '-')[:22]:22} cells {gk.cells(rig_tpl):>2} mags {fill['spareMagazines']} meds {len(meds)}  MP {score_of(out, fill):>6}")
    return kit


def main():
    out_path = os.path.join(HERE, '..', 'ArenaMode.Server', 'defaults', 'kits.json')
    if '--out' in sys.argv:
        out_path = sys.argv[sys.argv.index('--out') + 1]
    used_helmets, used_ears = set(), collections.Counter()
    kits = []
    print(f"band {BAND['min']}-{BAND['max']}")
    for cls, tiers in STARTERS.items():
        for tier, (preset_id, ammo, armor_target) in enumerate(tiers):
            kits.append(build(cls, tier, preset_id, ammo, armor_target, used_helmets, used_ears))
    io.open(out_path, 'w', encoding='utf-8').write(json.dumps(kits, indent=2))
    print('written:', os.path.abspath(out_path))


if __name__ == '__main__':
    main()
