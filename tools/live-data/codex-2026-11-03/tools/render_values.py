"""Render compact review tables from the JSON data. Standard library only."""
from pathlib import Path
import json
ROOT=Path(__file__).resolve().parents[1]
def read(name):return json.loads((ROOT/'data'/(name+'.json')).read_text())
def cell(v):
    if v is None:return 'Unknown'
    return str(v).replace('|',' / ').replace('\n',' ')
def table(headers,rows):
    return '\n'.join(['| '+' | '.join(headers)+' |','|'+'|'.join(['---']*len(headers))+'|']+['| '+' | '.join(cell(v) for v in row)+' |' for row in rows])+'\n'
def price(p):
    if p.get('status')=='multi_token_recipe':return p['raw']
    s=f"{p['amount']:,} {p['currency']}" if p.get('amount') is not None else 'Unknown'
    if p.get('basis')=='estimated_mod_default':s+=' (estimate)'
    return s
def condition(v):
    if v['status']!='conditional':return {'allowed':'Yes','unavailable':'No'}[v['status']]
    c=v['condition'];k=c['kind']
    if k=='unresolved':return 'Conditional; unresolved'
    if k=='allow_weapons':return 'Only '+', '.join(c['aliases'])
    if k=='deny_weapons':return 'Except '+', '.join(c['aliases'])
    return 'Bolt-action only'
parts=['# Arena values and categories\n',
       'Compiled 2026-11-03. Read `CLAUDE_HANDOVER.md` for version selection, estimates and integration. These tables are a review view of the JSON files. “Estimate” means a deliberate mod default. Unknown permissions remain separate from inferred prices.\n']
a=read('resolved_armory')
for category in dict.fromkeys(r['category'] for r in a):
    rows=[r for r in a if r['category']==category]
    parts.append('## '+category.replace('_',' ').title()+'\n')
    parts.append('Source section: '+rows[0]['source']['sectionPatch']+'. Unlock text is retained in the source language.\n')
    parts.append(table(['Item','MP','Class permission','Purchase/access price','Unlock requirement'],[
      [r['name']+((' ['+r['sourceVariantLabel']+']') if 'duplicate_display_name_check_variant_label_and_image' in r['reviewFlags'] and r['sourceVariantLabel'] else ''),
       str(r['effectiveMp'])+(f" (estimate; source {r['mp']})" if r['effectiveMpBasis']!='wiki_transcribed' else ''),
       ', '.join(r['allowedClasses']) if r['allowedClasses'] else 'Not listed',price(r['effectivePurchase']),r['unlock']['raw'] or 'Not listed'] for r in rows]))
parts.append('## Ammunition\n')
parts.append('MP is per round. Damage preserves pellet notation. Other statistics, source notes and complete conditions are in `data/ammo.json`. Source section version: 0.6.0.0.47588.\n')
parts.append(table(['Ammo','MP/round','Damage','Pen.','Armor damage %','Velocity m/s','Assault','Enforcer','Scout','Marksman'],[
 [r['name'],r['mpPerRound'],r['stats']['damageRaw'],r['stats']['penetration'],r['stats']['armorDamagePercent'],r['stats']['velocityMetersPerSecond']]+[condition(r['classAccess'][c]) for c in ['Assault','Enforcer','Scout','Marksman']] for r in read('ammo')]))
parts.append('## BlastGang round shop\n')
parts.append('All prices are in-match dollars. Grouped cosmetic variants share one source row. Magazine counts are transcribed, including flagged anomalies. These are not MP or persistent RUB prices.\n')
parts.append(table(['Side','Category','Item/variant group','Match dollars','Ammo','Damage / penetration','Magazine capacity × count'],[
 [r['side'],r['shopCategory'],r['name'],r['price'],r['ammoNameRaw'],str(r['ammoDamageRaw'] or '—')+' / '+str(r['ammoPenetration'] if r['ammoPenetration'] is not None else '—'),str(r['magazineCapacity'])+' × '+str(r['magazineCount']) if r['magazineCapacity'] is not None else 'Not listed'] for r in read('blastgang_shop')]))
parts.append('## BlastGang money awards\n')
parts.append(table(['Event','Match dollars'],[[r['event'],r['amount']] for r in read('blastgang_economy')['events']]))
parts.append('## Battle Pass XP\n')
parts.append('The source labels this table as under verification. These are neither PMC XP nor spendable Battle Points. ShootOut values were not supplied.\n')
parts.append(table(['Mode','Event','BP XP'],[[r['mode'],r['event'],r['amount']] for r in read('battlepass_xp')['rows']]))
parts.append('## Weapon camouflage\n')
parts.append('No source patch version or MP is assigned to these rows.\n')
parts.append(table(['Name','Unlock','Price'],[[r['name'],r['unlock']['raw'],price(r['purchase'])] for r in read('cosmetics')]))
parts.append('## Missing-category MP design defaults\n')
parts.append('These are explicit inferred defaults, not observed live scores. `tools/estimate.py` adjusts them using available attributes.\n')
parts.append(table(['Category','Base MP','Adjustments'],[[k,v['base'],v.get('formula') or json.dumps({a:b for a,b in v.items() if a!='base'},ensure_ascii=False)] for k,v in read('estimated_values')['missingMpCategorySeeds'].items()]))
parts.append('## Later official Wildcard restrictions\n')
parts.append('Event-specific, patch 0.6.1.5. These rows do not automatically override base-class permissions.\n')
parts.append(table(['Caliber','Ammo','Weapons excluded'],[[r['caliber'],', '.join(r['ammoLabels']),', '.join(r['deniedWeaponAliases'])] for r in read('later_official_changes')['events']['Wildcard']['laterAmmoRestrictionRows']]))
parts.append('## Arena-stash GP sale estimates\n')
parts.append('The sellable categories and dependence on Ref loyalty are official; every amount below is an inferred mod default. See `estimated_values.json` for the GP comparator pool and explicit multipliers.\n')
parts.append(table(['Item','Ref LL1','Ref LL2','Ref LL3','Ref LL4'],[[r['itemLabel']]+[r['priceByRefLoyalty'][str(ll)] for ll in range(1,5)] for r in read('estimated_values')['arenaStashSaleEstimates']['rows']]))
(ROOT/'VALUES.md').write_text('\n'.join(parts))
print('Wrote VALUES.md')
