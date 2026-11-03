# Data dictionary

All files are UTF-8 JSON. Amounts are numeric; absence is `null`. Decimal MP is intentional. All record IDs identify **this snapshot's rows**; they are not game template IDs, and can change when a source row changes.

| File | Purpose |
|---|---|
| `armory.json` | 224 source-transcribed combat equipment rows |
| `resolved_armory.json` | Same rows plus usable `effectiveMp`, `effectiveMpBasis` and `effectivePurchase` |
| `estimated_values.json` | 91 inferred purchase prices, eight GP sale price schedules, two suspect-MP proposals, ten missing-category MP models |
| `ammo.json` | 70 per-round costs, ballistics and class/weapon constraints |
| `blastgang_shop.json` | 95 shop groups; variant arrays do not imply separate source prices |
| `blastgang_economy.json` | Match-dollar reward events; stacking unknown |
| `battlepass_xp.json` | BP XP rewards, with explicit source uncertainty |
| `battlepoint_exchange.json` | Currency-to-points rates and separately versioned official daily limit |
| `later_official_changes.json` | MP delta notice, GP selling categories, starter delta and event restrictions |
| `class_rules.json` | Budget reference, class-policy leads and disabled historical constraints |
| `mode_rules.json` | Known mode parameters and explicitly unresolved rules |
| `cosmetics.json` | 32 camouflage rows without inferred combat cost |
| `template_mapping.json` | Mapping worksheet for 421 source records, including shop groups |
| `source_rows.json` | Cleaned numeric/categorical source cells for transcription checks |
| `sources.json` | Source URLs, retrieval date and evidence roles |
| `coverage.json` | Counts, missing categories and unresolved areas |
| `review_queue.json` | Records with source/version/identity/value issues |
| `excluded_source_rows.json` | 20 commented-out source rows, excluded from active data |

`source.sectionPatch` records the wiki section's label. It is not a claim of independent verification. `source.line` refers to the L0-based web extraction, which can shift on a later revision. `rowSha256` hashes the original stripped table row. No source images or full source articles are redistributed.

`name` is the display label; `sourceVariantLabel` and `sourceImageRef` help disambiguate variants. Do not merge records on name alone. `gameTemplateId`/`gameTemplateIds` remain null until mapped to the installed game version.

`unlock.raw` preserves the source requirement; `hints` are only partial parsing aids. They do not implement quest logic, AND/OR groups, task IDs, ownership, or seasonal eligibility. `hintsAreExhaustive` is false. Purchase costs can require RUB, GP, Lega Medals, Battle Points, or multiple map tokens.

`allowedClasses: null` in Armory means permission was not listed. In ammo, `classAccess` uses allowed/conditional/unavailable according to that page's explicit symbol legend. A conditional entry includes either a structured alias-based condition or `kind: unresolved`.

Ballistic percentages preserve the source's displayed numbers, not normalized multipliers. Blank accuracy/recoil/bleed/heat/wear fields remain null. Pellet damage is represented by `damageRaw`, `damagePerPellet` and `pelletCount`; a blank scalar `damage` must not become zero damage. Arena and Normal template stats may differ.

Price basis and currency are mandatory distinctions. `estimated_mod_default` values are proposed prices backed by listed comparator IDs; `wiki_transcribed` values are observations with their source version. A missing Armory price is filled in the resolved catalog. Explicit free BlastGang starting weapons retain a source price of zero.

MP estimates use normalized features passed to `estimate_missing_mp()`. Category base defaults work even before richer attributes are mapped. The caller must price an empty carrier and its protective components without double counting, and establish whether a weapon's source MP already includes stock attachments.

Validation concerns this dataset and estimator only. `VALIDATION.json` is the recorded result; rerun the supplied script after changes.
