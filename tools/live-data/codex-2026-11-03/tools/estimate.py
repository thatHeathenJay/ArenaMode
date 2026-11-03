"""Create explicit mod defaults from observed Arena comparators; no external packages.

Run from any directory: python3 tools/estimate.py
This writes estimated_values.json and resolved_armory.json, leaving sources intact.
"""
from pathlib import Path
import json, math, statistics

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'data'
ARMORY = json.loads((DATA/'armory.json').read_text())
MEDICAL = {'medkit','injury_treatment','pills','stimulant'}
FIREARMS = {'assault_rifle','smg','shotgun','lmg','marksman_rifle','bolt_action_rifle','assault_carbine','pistol'}

def rounded(value, step=5000):
    return max(step, int(math.floor(value/step + .5)*step))

def estimate_purchase(row):
    mp = row['mp']
    pool = [r for r in ARMORY if r['purchase']['currency']=='RUB'
            and r['purchase']['amount'] is not None and 0 < r['mp'] <= 10000]
    exact = [r for r in pool if r['category']==row['category']
             and set(r['aliases']) & set(row['aliases'])]
    category = [r for r in pool if r['category']==row['category']]
    confidence = 'medium'; factor = 1.0
    if exact:
        candidates = exact; method = 'same_alias_variant'; exponent = 0
    elif category:
        candidates = category; method = 'same_category_nearest_mp'; exponent = .5
        if row['category']=='melee': exponent = 0; confidence = 'low'
    elif row['category'] in MEDICAL:
        candidates = [r for r in pool if r['category']=='stimulant']
        method = 'medical_family_nearest_mp'; exponent = .5; confidence = 'low'
        # Access rights for common medical supplies receive a deliberate discount
        # relative to stimulant unlocks. This is a mod design choice, not live data.
        factor = {'medkit':.6,'injury_treatment':.4,'pills':.5}.get(row['category'],1)
    else:
        candidates = [r for r in pool if r['category'] in FIREARMS]
        method = 'firearm_family_nearest_mp'; exponent = .5; confidence = 'low'
    assert candidates
    candidates = sorted(candidates,key=lambda r: abs(math.log(max(mp,1)/r['mp'])))[:3]
    estimates=[]
    for other in candidates:
        scale = min(2.0,max(.5,(max(mp,1)/other['mp'])**exponent))
        estimates.append(other['purchase']['amount']*scale*factor)
    amount = rounded(statistics.median(estimates))
    return {'amount':amount,'currency':'RUB','basis':'estimated_mod_default',
            'method':method,'confidence':confidence,'categoryDiscount':factor,
            'mpScalingExponent':exponent,'comparators':[r['id'] for r in candidates],
            'comparatorEstimatesBeforeRounding':estimates,
            'tuningRangeRoubles':[rounded(amount*.7),rounded(amount*1.3)],
            'scope':'one_time_armory_access_price_not_consumable_market_price'}

# These are transparent design seeds constrained to the observed 7,500-10,000
# preset budget. Their category factors are NOT measurements from the live game.
# Richer item attributes can be supplied by the caller after template mapping.
SEEDS = {
 'helmet': {'base':520,'armorClassMp':{'1':80,'2':140,'3':260,'4':520,'5':900,'6':1300}},
 'helmet_attachment_armor': {'base':160,'armorClassMp':{'1':40,'2':80,'3':160,'4':300,'5':500,'6':750}},
 'headset': {'base':180,'tiers':{'basic':120,'standard':180,'premium':250}},
 'earplug': {'base':70},
 'chest_rig': {'base':160,'formula':'40 + 6 * usableCells'},
 'armored_rig_empty_carrier': {'base':180,'formula':'60 + 6 * usableCells; protective components priced separately'},
 'armor_plate': {'base':450,'armorClassMp':{'1':60,'2':150,'3':250,'4':450,'5':800,'6':1200}},
 'grenade': {'base':200,'types':{'flash':60,'smoke':100,'fragmentation':200,'mini_fragmentation':240,'impact':280}},
 'magazine': {'base':80,'formula':'max(20, 20 + 2 * capacity); multiply 1.25 for drum'},
 'weapon_attachment': {'base':120,'roles':{'receiver':0,'barrel':180,'handguard':100,'stock':140,'pistol_grip':60,'foregrip':120,'muzzle_brake':160,'suppressor':300,'iron_sight':20,'reflex_sight':100,'scope_low_power':250,'scope_high_power':450,'laser':90,'flashlight':40,'combo_tactical':120,'mount':20,'adapter':10}}
}

def estimate_missing_mp(category, *, armor_class=None, durability=None,
                        coverage_multiplier=1.0, mobility_penalty_fraction=0.0,
                        usable_cells=None, capacity=None, drum=False,
                        role=None, quality=1.0):
    """Return a design estimate; normalized inputs, not SPT field names.

    armor_class: integer 1..6; durability: combined relevant max durability.
    coverage_multiplier: caller-curated 0.7..1.5; never inferred from text.
    mobility_penalty_fraction: nonnegative fraction (0.1 means 10%).
    quality: caller-curated 0.75..1.5, based on relative item performance.
    Empty carriers exclude plates. Receiver baseline 0 means already priced in
    base weapon, not a free missing item. Never add the same component twice.
    """
    if category not in SEEDS: raise ValueError('Unknown estimate category: '+category)
    seed=SEEDS[category]; value=seed['base']
    if 'armorClassMp' in seed:
        if armor_class is not None:
            if str(armor_class) not in seed['armorClassMp']: raise ValueError('armor_class must be 1..6')
            value=seed['armorClassMp'][str(armor_class)]
        if durability is not None:
            if durability<=0: raise ValueError('durability must be positive')
            value*=min(1.35,max(.7,math.sqrt(durability/40)))
        value*=min(1.5,max(.7,coverage_multiplier))
        value*=max(.8,1-min(.2,max(0,mobility_penalty_fraction)))
    if category in ['chest_rig','armored_rig_empty_carrier'] and usable_cells is not None:
        if usable_cells<0:raise ValueError('usable_cells must be nonnegative')
        value=(40 if category=='chest_rig' else 60)+6*usable_cells
    if category=='magazine' and capacity is not None:
        if capacity<=0:raise ValueError('capacity must be positive')
        value=max(20,20+2*capacity)*(1.25 if drum else 1)
    options=seed.get('roles',seed.get('types',seed.get('tiers',{})))
    if role is not None:
        if role not in options:raise ValueError('Unknown role: '+role)
        value=options[role]
    value*=min(1.5,max(.75,quality))
    return int(math.floor(value/5+.5)*5)

def main():
    purchase=[]; resolved=[]; repairs=[]
    for row in ARMORY:
        r=dict(row)
        r['effectiveMp']=row['mp'];r['effectiveMpBasis']='wiki_transcribed'
        if 'mp_exceeds_entire_preset_budget' in row['reviewFlags'] and 'AUG A3' in row['name']:
            comp=next(x for x in ARMORY if 'AUG A3' in x['name'] and '(Black)' in x['name'])
            r['effectiveMp']=comp['mp'];r['effectiveMpBasis']='estimated_from_same_weapon_variant'
            repairs.append({'recordId':row['id'],'field':'mp','sourceValue':row['mp'],'estimate':comp['mp'],'comparators':[comp['id']],'confidence':'medium','rationale':'Same base rifle variant; source outlier is not treated as a verified typo correction.'})
        if row['mp']==0 and row['category']=='stimulant':
            comps=[x for x in ARMORY if x['category']=='stimulant' and x['mp']>0 and x['purchase']['amount']==150000]
            r['effectiveMp']=rounded(statistics.median(x['mp'] for x in comps),5)
            r['effectiveMpBasis']='estimated_from_same_price_stimulants'
            repairs.append({'recordId':row['id'],'field':'mp','sourceValue':0,'estimate':r['effectiveMp'],'comparators':[x['id'] for x in comps],'confidence':'low','rationale':'Provisional nonzero value from comparable 150,000 RUB stimulant unlocks; effect strength needs playtesting.'})
        if row['purchase']['amount'] is None and row['purchase']['status']!='multi_token_recipe':
            estimate=estimate_purchase(r);purchase.append({'recordId':row['id'],'name':row['name'],**estimate})
            r['effectivePurchase']=estimate
        else:r['effectivePurchase']={**row['purchase'],'basis':'wiki_transcribed'}
        r['grantPolicy']='Keep starter, quest, event and already-owned grants free; fallback price applies when this item is offered for purchase.'
        resolved.append(r)
    gp_anchors=[r for r in ARMORY if r['purchase']['currency']=='GP']
    median_gp=statistics.median(r['purchase']['amount'] for r in gp_anchors)
    sale_labels=json.loads((DATA/'later_official_changes.json').read_text())['arenaStashGpSale']['allowedItemLabels']
    sale_factors=[.70,.60,.80,.40,.70,.14,.07,.21]
    sales=[]
    for label,factor in zip(sale_labels,sale_factors):
        base=rounded(median_gp*factor,5)
        sales.append({'itemLabel':label,'currency':'GP','priceByRefLoyalty':{str(ll):rounded(base*mult,1) for ll,mult in [(1,1),(2,1.1),(3,1.25),(4,1.5)]},'basis':'estimated_mod_default','confidence':'low','categoryFactor':factor})
    payload={'policy':'Use reasonable estimates where a needed price is absent; never substitute missing with zero.',
             'purchaseEstimates':purchase,'suspectMpProposals':repairs,
             'arenaStashSaleEstimates':{'rows':sales,'anchorMedianGpArmoryPrice':median_gp,'anchorRecordIds':[r['id'] for r in gp_anchors],'method':'Median observed GP access price times an explicit item-category factor; Ref multipliers 1/1.1/1.25/1.5. Selling versus unlocking are different markets; this is a conservative mod economy seed, not a live conversion formula.'},
             'missingMpCategorySeeds':SEEDS,
             'seedBasis':{'type':'engineering_design_estimate','measuredCategoryFit':False,
                          'anchors':{'presetMpRange':[7500,10000],
                                     'medianObservedMpByCategory':{c:statistics.median(r['mp'] for r in ARMORY if r['category']==c and 0<r['mp']<=10000) for c in sorted({r['category'] for r in ARMORY})}},
                          'rationale':'Keep individual utilities below major medical/weapon costs; armor scales nonlinearly; mods consume hundreds to low thousands per build. Grenade relative ordering follows the shop but conversion scale is a mod choice.'},
             'notMeasured':'Missing category scores cannot be statistically fitted from other categories without their live scores and item attributes.',
             'ownership':'These estimates are mod defaults, not claims of current BSG values.'}
    for filename,data in [('estimated_values.json',payload),('resolved_armory.json',resolved)]:
        (DATA/filename).write_text(json.dumps(data,ensure_ascii=False,indent=2)+'\n')
    print(json.dumps({'estimatedPurchasePrices':len(purchase),'suspectMpProposals':len(repairs),'missingMpCategories':len(SEEDS)}))

if __name__=='__main__':main()
