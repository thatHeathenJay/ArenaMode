"""Maps the Codex live-data records (armory, ammo, BlastGang shop) to SPT item template ids by English name.

Match order: exact normalised name, then alias, then token-set similarity against every item whose
category fits the record (weapon class / ammo caliber / armor), with a review flag below the confidence bar.
Output: tools/live-data/codex-2026-11-03/data/template_mapping.filled.json (+ a review list on stdout).

Usage: py tools/map-templates.py
"""
import io, json, os, re, sys, difflib
sys.stdout.reconfigure(encoding='utf-8', errors='replace')

ROOT = r'C:\Games\SPT-4.1'
DB = ROOT + r'\SPT_Runtime\SPT_Data\database'
PKG = os.path.join(os.path.dirname(__file__), 'live-data', 'codex-2026-11-03', 'data')

items = json.load(io.open(DB + r'\templates\items.json', encoding='utf-8-sig'))
loc = json.load(io.open(DB + r'\locales\global\en.json', encoding='utf-8-sig'))
hb = json.load(io.open(DB + r'\templates\handbook.json', encoding='utf-8-sig'))
price = {h['Id']: h['Price'] for h in hb['Items']}

AMMO = '5485a8684bdc2da71d8b4567'; WEAPON = '5422acb9af1c889c16000029'; ARMOR = '5448e54d4bdc2dcc718b4568'
VEST = '5448e5284bdc2dcb718b4567'; MEDS = '543be5664bdc2dd4348b4569'; GLASSES = '5448e5724bdc2ddf718b4568'
KNIFE = '5447e1d04bdc2dff2f8b4567'; HEADWEAR = '5a341c4086f77401f2541505'; STIM = '5448f3a64bdc2d60728b456a'
DRUGS = '5448f3a14bdc2d27728b4569'; THROW = '543be6564bdc2df4348b4568'


def chain(tpl):
    n, out = tpl, []
    while n in items and len(out) < 12:
        out.append(n); n = items[n].get('_parent')
    return out


def name(tpl):
    return loc.get(tpl + ' Name') or items[tpl].get('_name', tpl)


def short(tpl):
    return loc.get(tpl + ' ShortName') or ''


def norm(s):
    s = s.lower()
    s = re.sub(r'\(.*?\)', ' ', s)                       # colour / variant parentheses
    s = s.replace('х', 'x').replace('×', 'x').replace('"', '').replace("'", '').replace('&#246;', 'o').replace('ö', 'o')
    s = re.sub(r'(\.\d+)(ae|acp)', r' ', s)                # .50AE -> .50 ae
    s = re.sub(r'(hk|mp|pp|sr|ak|rpk|ar|m)\s+(\d)', r'', s)   # HK 416 -> hk416, PP-19 forms stay
    s = re.sub(r'\b(assault rifle|assault carbine|submachine gun|shotgun|marksman rifle|sniper rifle|bolt-action rifle|'
               r'light machine gun|machine gun|pistol|revolver|body armor|armored rig|armor vest|plate carrier|'
               r'first aid kit|individual|tactical|stimulant injector|injector|hemostatic|regenerative|helmet|'
               r'bulletproof|cartridge|round|rounds|ammo pack|pack)\b', ' ', s)
    s = re.sub(r'[^a-z0-9.]+', ' ', s)
    return ' '.join(s.split())


CATEGORY_PARENT = {
    'assault_rifle': WEAPON, 'submachine_gun': WEAPON, 'shotgun': WEAPON, 'light_machine_gun': WEAPON,
    'marksman_rifle': WEAPON, 'bolt_action_rifle': WEAPON, 'assault_carbine': WEAPON, 'pistol': WEAPON,
    'melee': KNIFE, 'body_armor': None, 'eyewear': GLASSES, 'medkit': MEDS, 'injury_treatment': MEDS,
    'pills': MEDS, 'stimulant': STIM,
}
SHOP_PARENT = {'PISTOL': WEAPON, 'COMMON': WEAPON, 'ECO': WEAPON, 'ELITE': WEAPON, 'SPECIAL': WEAPON, 'SNIPER': WEAPON,
               'HELMET': HEADWEAR, '1 ARMOR': None, '2 ARMOR': None, 'GRENADE': THROW}

pool = {}
for tpl, it in items.items():
    if it.get('_type') != 'Item' or not loc.get(tpl + ' Name'):
        continue
    ch = set(chain(tpl)[1:])
    pool[tpl] = ch


def candidates(parent):
    if parent is None:
        return [t for t, ch in pool.items() if ARMOR in ch or (VEST in ch and items[t]['_props'].get('BlocksArmorVest'))]
    if parent in (STIM, MEDS):
        return [t for t, ch in pool.items() if STIM in ch or MEDS in ch or DRUGS in ch]
    return [t for t, ch in pool.items() if parent in ch]


def best(record_names, cands, extra=None):
    """Returns (tpl, score, how)."""
    targets = {}
    for t in cands:
        targets.setdefault(norm(name(t)), []).append(t)
        if short(t):
            targets.setdefault(norm(short(t)), []).append(t)
    for rn in record_names:
        n = norm(rn)
        if n in targets:
            hits = targets[n]
            hits = sorted(hits, key=lambda t: -price.get(t, 0))
            return hits[0], 1.0, 'exact'
    scored = []
    for rn in record_names:
        n = norm(rn)
        toks = set(n.split())
        for t in cands:
            tn = norm(name(t)); tt = set(tn.split())
            if not tt:
                continue
            jac = len(toks & tt) / len(toks | tt)
            seq = difflib.SequenceMatcher(None, n, tn).ratio()
            sq = difflib.SequenceMatcher(None, n.replace(' ', ''), tn.replace(' ', '')).ratio()
            s = 0.5 * jac + 0.3 * seq + 0.2 * sq
            if n and n in tn.split() or (len(n) >= 4 and n.replace(' ', '') in tn.replace(' ', '')):
                s += 0.25                                      # record name is a token / substring of the item name
            if extra:
                s += extra(t)
            scored.append((s, t))
    if not scored:
        return None, 0, 'none'
    scored.sort(reverse=True)
    return scored[0][1], round(scored[0][0], 3), 'fuzzy'


out = []
review = []


def add(kind, rec, rec_names, cands, extra=None, bar=0.62):
    tpl, score, how = best(rec_names, cands, extra)
    status = 'mapped' if tpl and (how == 'exact' or score >= bar) else 'review' if tpl else 'unmapped'
    row = {'recordId': rec['id'], 'displayName': rec['name'], 'gameTemplateIds': [tpl] if tpl and status != 'unmapped' else None,
           'candidateName': name(tpl) if tpl else None, 'score': score, 'how': how, 'mappingStatus': status}
    out.append(row)
    if status != 'mapped':
        review.append(f"{status:8} {score:5} {rec['name']}  ->  {row['candidateName']}")


armory = json.load(io.open(os.path.join(PKG, 'armory.json'), encoding='utf-8'))
for rec in armory:
    parent = CATEGORY_PARENT.get(rec['category'], WEAPON)
    add('armory', rec, [rec['name']] + rec.get('aliases', []), candidates(parent))

ammo = json.load(io.open(os.path.join(PKG, 'ammo.json'), encoding='utf-8'))
ammo_cands = [t for t, ch in pool.items() if AMMO in ch and items[t]['_props'].get('ammoType') in ('bullet', 'buckshot')]


def cal_bonus(rec):
    cal = norm(rec['caliber']).replace(' ', '')
    pen = rec.get('stats', {}).get('penetration')

    def f(t):
        p = items[t]['_props']
        b = 0.25 if cal and cal.replace('mm', '') in norm(p.get('Caliber', '')).replace('caliber', '').replace(' ', '') else -0.3
        if pen is not None and p.get('PenetrationPower') == pen:
            b += 0.2
        return b
    return f


for rec in ammo:
    add('ammo', rec, [rec['name'], rec['caliber'] + ' ' + rec['name']], ammo_cands, cal_bonus(rec), bar=0.55)

shop = json.load(io.open(os.path.join(PKG, 'blastgang_shop.json'), encoding='utf-8'))
for rec in shop:
    add('blastgang', rec, [rec['name']] + rec.get('variantNames', []), candidates(SHOP_PARENT.get(rec['shopCategory'], WEAPON)))

json.dump({'targetSptVersion': '4.1.6', 'generatedBy': 'tools/map-templates.py', 'mappings': out},
          io.open(os.path.join(PKG, 'template_mapping.filled.json'), 'w', encoding='utf-8'), indent=1, ensure_ascii=False)
mapped = sum(1 for r in out if r['mappingStatus'] == 'mapped')
print(f'{mapped} mapped, {sum(1 for r in out if r["mappingStatus"]=="review")} review, '
      f'{sum(1 for r in out if r["mappingStatus"]=="unmapped")} unmapped of {len(out)}')
print('\n'.join(review))
