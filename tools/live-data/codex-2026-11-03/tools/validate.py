"""Validate the reference pack and its explicit estimates. No game runtime needed."""
from pathlib import Path
from collections import Counter
import json, sys
from estimate import estimate_missing_mp

ROOT=Path(__file__).resolve().parents[1]
DATA=ROOT/'data'
def read(name):return json.loads((DATA/(name+'.json')).read_text())
checks=0
def check(condition,label):
    global checks
    if not condition:raise AssertionError(label)
    checks+=1

a=read('armory');am=read('ammo');shop=read('blastgang_shop');cos=read('cosmetics')
c=read('coverage');ids=[r['id'] for r in a+am+shop+cos];idset=set(ids)
check(len(ids)==len(idset),'Unique snapshot record IDs')
for name,rows,count in [('Armory',a,224),('ammo',am,70),('shop',shop,95),('cosmetics',cos,32)]:
    check(len(rows)==count,name+' complete table row count')
for r in a+am+shop:
    check(bool(r['source']['url']) and bool(r['source']['sectionPatch']),r['id']+' provenance')
    check(len(r['source']['rowSha256'])==64,r['id']+' source hash')
for r in a:
    check(isinstance(r['mp'],(int,float)),r['id']+' numeric MP')
    check(r['allowedClasses'] is None or set(r['allowedClasses'])<=set(['Assault','Enforcer','Scout','Marksman']),r['id']+' class names')
    check(r['gameTemplateId'] is None,'No fabricated template IDs')
raw={r['id']:r for r in read('source_rows')['rows']}
for r in a:
    check(float(raw[r['id']]['cells'][1])==r['mp'],r['id']+' MP agrees with source cell')
for r in am:
    check(float(raw[r['id']]['cells'][12])==r['mpPerRound'],r['id']+' per-round MP agrees with source cell')
    for cl,v in r['classAccess'].items():
        check(v['status'] in ['allowed','unavailable','conditional'],'Ammo access enum')
        if v['status']=='conditional':check('condition' in v,'Conditional ammo has explicit resolution state')
g36=next(r for r in a if r['name']=='HK G36 5.56x45 assault rifle')
check(g36['purchase']['amount']==650000 and any(h['kind']=='initial_availability' for h in g36['unlock']['hints']),'Initial availability does not erase purchase cost')
check(any(r['mp']==3090.5 for r in a),'Fractional MP retained')
check(any(r['mp']==30093 and 'mp_exceeds_entire_preset_budget' in r['reviewFlags'] for r in a),'AUG source outlier preserved')
m995=next(r for r in am if r['name']=='5.56x45mm M995')
check('Model 1' in m995['classAccess']['Assault']['condition']['aliases'],'Model 1 ammo restriction preserved')
check(m995['stats']['durabilityBurnPercent'] is None,'Blank stats stay null')
check(any(r['price']==0 and r['isInitialWeapon'] for r in shop),'Explicit zero shop price preserved')
check(all(r['currency']=='BLASTGANG_MATCH_DOLLARS' for r in shop),'Shop money namespace')
check(len(read('battlepass_xp')['rows'])==30,'BP XP table coverage')
check(read('battlepoint_exchange')['dailyLimit']['latestSourced']==20,'Later official BP limit takes precedence')
patch=read('later_official_changes')
check(len(patch['events']['Wildcard']['laterAmmoRestrictionRows'])==20,'Wildcard official restriction table coverage')
check(not patch['events']['BloodLust']['applyToOrdinaryCheckpoint'],'Event rewards isolated')
check(not patch['weaponMpDeltaNotice']['applyAutomatically'],'No duplicate patch deltas')
est=read('estimated_values');resolved=read('resolved_armory')
check(len(est['purchaseEstimates'])==91,'Missing purchase prices filled')
check(len(est['suspectMpProposals'])==2,'Suspect MP proposals are explicit')
check(len(est['arenaStashSaleEstimates']['rows'])==8,'Eight published sellable categories receive inferred GP prices')
for sale in est['arenaStashSaleEstimates']['rows']:
    amounts=list(sale['priceByRefLoyalty'].values())
    check(all(v>0 for v in amounts) and amounts==sorted(amounts),'Positive loyalty-scaled GP sale prices')
check(len(resolved)==len(a),'Resolved catalog coverage')
for r in resolved:
    check(r['effectiveMp']>0 and r['effectiveMp']<=10000,'Resolved MP stays within sensible individual-item bounds')
    p=r['effectivePurchase']
    check(p['status']=='multi_token_recipe' if 'status' in p and p['status']=='multi_token_recipe' else p['amount']>0,'Resolved purchase has positive price or token recipe')
for r in est['purchaseEstimates']:
    check(r['recordId'] in idset and all(i in idset for i in r['comparators']),'Estimate comparator references')
    check(r['basis']=='estimated_mod_default' and r['amount']>0,'Estimate labelled and nonzero')
check(estimate_missing_mp('magazine',capacity=60)>estimate_missing_mp('magazine',capacity=30),'Larger magazine costs more')
check(estimate_missing_mp('magazine',capacity=60,drum=True)>estimate_missing_mp('magazine',capacity=60),'Drum premium')
check(estimate_missing_mp('armor_plate',armor_class=6)>estimate_missing_mp('armor_plate',armor_class=4),'Higher protection costs more')
check(estimate_missing_mp('helmet',armor_class=4,durability=20)<estimate_missing_mp('helmet',armor_class=4,durability=60),'Durability affects estimate')
print(json.dumps({'result':'PASS','checks':checks,'armoryRows':len(a),'ammoRows':len(am),'shopRows':len(shop),'estimatedPurchasePrices':len(est['purchaseEstimates']),'validationScope':'Data integrity, transcription and fallback behavior; no mod/gameplay parity testing.'},indent=2))
