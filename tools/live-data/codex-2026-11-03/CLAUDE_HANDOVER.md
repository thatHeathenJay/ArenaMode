# ArenaMode live-reference and integration handover

Compiled 2026-11-03. This package supplies observed values, explicit estimates, source provenance, and implementation guidance for a heavily modified ArenaMode fork. No mod repository or live client database was available for inspection.

**Use realistic inferred values when a needed price is missing.** The package implements that policy: 91 missing Armory purchase prices now have positive RUB estimates backed by comparable items. Ten category models cover missing MP data. Source values remain available for audit; estimates are usable defaults, not blockers and not claims of exact BSG values.

## Start here

1. Read `data/coverage.json`, `data/later_official_changes.json`, and the caveats below.
2. Use `data/resolved_armory.json` for the working catalog. `effectivePurchase` and `effectiveMp` select the supplied value or an explicitly identified estimate. Original fields are retained.
3. Read `VALUES.md` for the complete human-readable tables. Use the JSON files for implementation.
4. Map record IDs to the installed game's item templates using `data/template_mapping.json`. Compare against the fork's existing `tools/live-data` before replacing anything.
5. Use `tools/estimate.py` for missing-category MP calculations and reproducible purchase estimates. Run `python3 tools/validate.py` after changes.

## What is compiled

| Dataset | Rows | Contents |
|---|---:|---|
| Armory | 224 | MP, class list, unlock text/hints, purchase cost, variants and source version |
| Ammunition | 70, across 25 calibers | Per-round MP, ballistics, four-class permissions and weapon conditions |
| BlastGang shop | 95 | Side, shop category, match-dollar price, listed ammo and magazine configuration |
| BlastGang economy | 15 | Kill, objective, round-win and loss-streak rewards |
| Battle Pass XP | 30 | Four modes; source table explicitly marked under verification |
| Weapon camouflage | 32 | Names, unlocks and prices; not combat MP values |
| Later Wildcard restrictions | 20 | Official event-specific ammo/weapon exclusion groups |
| Purchase estimates | 91 | Nonzero fallback RUB prices with comparator IDs |

Armory category counts: assault rifles 35; SMGs 17; shotguns 12; LMGs 5; marksman rifles 11; bolt-action rifles 8; assault carbines 12; pistols 23; melee 14; body armor 32; eyewear 28; medkits 5; injury treatment 7; pills 2; stimulants 13. There are **294 transcribed numeric MP entries** across Armory and ammo. Counts include unavailable, old, and suspect rows; they are not a count of approved live items. Twenty commented-out Armory rows are explicitly excluded.

## Version selection comes before importing

The Armory page is not uniformly 0.6.0. Its section footers identify:

| Armory rows | Source section version |
|---:|---|
| 52 | 0.6.0.0.47752 — assault rifles and SMGs |
| 85 | 0.6.0.0.47588 — other weapons and melee |
| 75 | 0.5.0.0.44689 — armor, eyewear, pills, stimulants |
| 12 | 0.4.2.5.42886 — medkits and injury treatment |

All 70 ammo rows are labelled 0.6.0.0.47588. The latest official update announcement found is **0.6.2, installed October 29, 2026**. This compilation is therefore a versioned reference with later changes, not a complete 0.6.2 item database. A wiki footer is the contributor's version label, not independent proof of every row's freshness.

The [official 0.6.0 notes](https://telegra.ph/Patch-notes-060-09-17) rebalanced weapons, ammo, magazines, modifications, protective equipment and medicine. Older medical/armor scores need tuning or newer observations. Its 40% modification-cost statement does not establish a tested hard validation cap; keep that cap disabled unless the client confirms it.

The [0.6.1 notes](https://telegra.ph/Patch-Notes-061-10-15) set the daily Battle Point exchange cap to **20**, superseding the wiki's 8. They also add GP sales for certain Arena-stash cases/ornaments, with prices depending on Ref loyalty; those exact sale prices were not published here. Do not confuse these selling prices with Armory purchase estimates.

The [recent official channel](https://t.me/s/escapefromtarkovEN?q=%23tarkovarena) announces four weapon MP increases. They are stored as deltas; whether the wiki already includes them is unknown. Do not apply them twice. Wildcard and BloodLust belong in event configuration. Wildcard had ended by this compilation; BloodLust was announced through November 5. Ordinary CheckPoint must not inherit its melee-only rule or XP bonuses.

## How the estimates work

**Purchase prices:** prefer a priced variant with the same alias, then up to three nearby-MP items in the same category. Scale prices gently by the square root of the MP ratio, bounded to 0.5–2 times, take the median, and round to 5,000 RUB. When no medical-category RUB anchors exist, use stimulant comparators with explicit discounts for medkits, treatment items and pills. Melee uses category prices without MP scaling because its source MP values do not distinguish value. Each estimate exposes comparators, method, confidence, and a tuning range.

These are **one-time Armory access prices**, not flea-market prices or a per-match charge for consuming supplies. Keep starter grants, quest rewards, event rewards and already-owned access free to claim. Use the fallback price when the item is actually offered for purchase; do not add a second charge to a reward grant. Listed GP, Lega, Battle Point and multi-token costs retain their currencies.

The eight newly sellable Arena-stash categories also receive provisional GP prices by Ref loyalty in `arenaStashSaleEstimates`. Their anchor is the 72.5 GP median of 32 observed GP Armory prices, with explicit category discounts and loyalty multipliers. These are low-confidence economy seeds: selling inventory and buying unlocks are different markets. They provide working prices while the exact sale table remains unavailable.

**Missing MP:** `estimate_missing_mp()` provides deterministic scores for helmets, protective helmet attachments, headsets, earplugs, rigs, empty armored-rig carriers, plates, grenades, magazines and weapon modifications. Armor uses class, durability, coverage and mobility penalties; magazines use capacity and drum type; modifications use role and relative quality. Inputs are normalized parameters supplied by the adapter, not guessed SPT property names.

Examples of design defaults: class-4 helmet 520 MP; class-4 plate 450; standard headset 180; 30-round magazine 80; 60-round drum 175; fragmentation grenade 200; suppressor 300; low-power optic 250. These are provisional balance choices sized against the observed preset/item budgets. **They are not statistically fitted live scores:** the source lacks paired attributes and live MP for these categories. Replace or tune them as better data arrives. The estimator is supplied so missing data still produces a plausible playable result.

Two suspect source values also have explicit working proposals: AUG A3 30,093 becomes 3,093 based on its black variant; P22's listed zero becomes a nonzero estimate from similarly priced stimulants. Original values and the reasons are preserved. Neither proposal is presented as a verified correction.

## Important source issues

| Issue | Required treatment |
|---|---|
| G36 is initially available but costs 650,000 RUB | Availability, ownership and purchase price must be separate fields. |
| PPSh-41 costs 3,090.5 MP | Preserve decimals; do not truncate per-round or item values. |
| Blank or dash in Armory class field | Unknown permission; do not infer that every class can equip it. |
| Blank ammo class cell | The ammo-page legend explicitly defines this as unavailable. |
| Duplicate armor/eyewear display names | Preserve separate rows; use source variant labels/image references during template mapping. |
| BlastGang attacker Desert Eagle lists 54 magazines | Keep source evidence; five on the opposing side is a sensible proposed correction to verify. |
| Defender shop says PP-91-01 but its icon/ammo point toward PP-19-01 | Resolve identity before creating the preset. |
| BlastGang ammo damage differs across some rows/tables | Preserve mode-specific observations; do not silently overwrite global ballistics. |
| Battle Point exchange | Direction is RUB/GP **into** Battle Points. It is not a guaranteed BP-to-cash conversion. |
| TeamFight phase durations conflict with patch history | Implement a versioned timeline once event anchors are reconciled. |

## Ammo permissions need both class and weapon checks

The base wiki table includes these special cases:

| Ammo | Conditional access |
|---|---|
| 5.56 M995 | Assault excludes M4A1, HK416, **Model 1**, G36 |
| .300 AP | Assault: Velociraptor .300 BLK only |
| 7.62x39 MAI AP | Assault excludes RD-704 |
| 9x39 SP-6 | Assault: VSK-94 only |
| 9x39 PAB-9 | Assault excludes AS VAL MOD.4; Scout permits 9A-91 only |
| 7.62x51 Ball 11 LR | Marksman bolt-actions only; DMR chamber/magazine loading prohibited |
| 7.62x54R Tungsten Carbide AP | Marksman bolt-actions only |
| .338 AP | Marksman excludes Mk-18 |
| 5.45 BS / 5.45 PP | Conditional symbols lack complete weapon conditions in the base table |

The [later official Wildcard table](https://telegra.ph/Patch-Notes-065-10-22) supplies additional exclusions, including M16A1 for M995 and NL545 variants for BS. Its explicit scope is the Wildcard class. Preserve that scope rather than silently treating it as a complete replacement for all ordinary-class rules.

Names in these conditions are aliases awaiting template mapping. A round must pass class permission, weapon conditions, chamber compatibility, and magazine compatibility. Arena-specific ammunition—including the source's TT M995 / TT M855A1 labels—must not be renamed or discarded merely because it differs from Normal mode.

## Implementation boundaries

Use one server-authoritative equipment validator for players and bots. Generate bots from resolved eligibility and cost data, then validate the complete item tree. If a kit exceeds the MP budget, rebuild it; do not clamp its reported score. For missing prices, use the supplied estimates. For missing permission rules, use an explicit mod rule or exclude that combination until resolved: a price estimate does not prove compatibility.

Resolve the MP accounting baseline before adding components. A quoted weapon score might describe a stock configuration rather than a stripped receiver. Carrier and plate costs must not overlap, and a stack of rounds must be counted once. Maintain an item-instance ledger for cost calculation. The attachment estimator includes a zero incremental receiver role only for a receiver already accounted for in the base weapon price.

The 7,500–10,000 total preset band is sourced from the older preset page. Historical Firepower/Defense/Utility limits must not be resurrected from its folded tables. Armor coverage descriptions are stored as research leads; terms such as “minimal mobility penalties” and “additional armor” need precise predicates or allowlists. The older universal armor-class caps are not enabled.

BlastGang uses its own round shop and match wallet. Its dollar amounts are not RUB values or MP. Round-win reward stacking, grenade limits, tie behavior, and the wiki/official activation-versus-defuse terminology still need targeted confirmation. The package records the known amounts without inventing those semantics.

## Arena ↔ Normal integration

Keep MP, class unlocks, weapon progression, BP XP, spendable Battle Points, Tactical Map tokens and BlastGang match money in distinct namespaces. PMC XP, weapon XP and BP XP are different counters. Official XP percentage adjustments are not complete payout formulas and must not be applied to an already adjusted base twice.

Build integration around explicit, idempotent grants and transfers rather than copying an Arena profile or match inventory onto Normal mode:

| Operation | Integration requirement |
|---|---|
| Match completion | Commit one reward transaction per match/player; separate progression counters and currency amounts. |
| Arena-stash item sold for GP | Consume the item once and credit the Arena GP balance; any later transfer follows the transfer policy. |
| Authorized currency/item transfer | Debit the source and deliver to the intended Normal profile once; retain a transaction receipt. |
| Ref/task progress | Resolve real task IDs and deduplicate eligible events; do not invent task completion from Armory unlock hints. |
| Arena equipment/stat overrides | Apply to Arena instances; restore or isolate Normal-mode templates and profile inventory. |
| Crate/cosmetic entitlement | Track reward type, season, destination and claim state explicitly; do not assume every Arena unlock is an extractable item. |

The full Normal-mode reward manifests, crate contents/odds, transfer caps and live synchronization formula are outside this item compilation. Preserve the existing integration README and verify those policies independently. No such values have been fabricated here.

## Work order and acceptance

1. Diff the fork's data against this pack, keeping its valid newer evidence. Record the selected patch/event profile.
2. Fill template mappings from the installed SPT database. Report missing templates; create Arena-only replacements where needed instead of substituting unrelated items by name.
3. Import source-backed values plus the supplied estimated defaults. Carry provenance through to debug output and future updates.
4. Add complete-tree MP accounting, unlock/ownership separation, class/weapon ammo rules, and shared bot/player validation.
5. Implement BlastGang economy and event modifiers independently of persistent wallets and base-mode rewards.
6. Test representative full kits, weighted bot generation, purchase/grant flows, match-reward retries, reconnect recovery and Normal-profile isolation.

The included validator passes transcription, row coverage, provenance, conditional-access, estimate-comparator and fallback-behavior checks. **No SPT build, game launch, multiplayer session or live-client parity test was performed.** Once integrated, report which behaviors match a live observation, which use a documented estimate, and which remain unimplemented.

## Source index

Primary patch links are above. Item tables: [Armory](https://wikiwiki.jp/eft/Arena/Armory), [Ammo](https://wikiwiki.jp/eft/Arena/弾薬), [BlastGang](https://wikiwiki.jp/eft/Arena/BLASTGANG), [Battle Pass](https://wikiwiki.jp/eft/Arena/バトルパス), [Presets](https://wikiwiki.jp/eft/Arena/プリセット). Additional mode URLs and source-line locators are in `data/sources.json`. The community [Arena changelog](https://escapefromtarkov.fandom.com/wiki/Arena_Changelog) is used only for labelled historical research leads where a primary version was not obtained.
