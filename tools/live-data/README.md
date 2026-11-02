# Live Arena data (source tables)

Transcribed from the Japanese EFT wiki (wikiwiki.jp/eft/Arena/*), Armory page at patch 0.6.0.0.47752,
ammo page at 0.6.0.0.47588, and BSG patch notes. These are the live metascore (MP) values, class
availability, prices and unlock conditions that drive presets, the Armory and bot kit generation.

- armory_weapons.csv: table, name, MP, classes, unlock, RUB, GP/LEGA/BP
- armory_body_armor.csv: name, MP, classes, unlock, RUB, GP
- armory_meds.csv: medkits, treatment, stims
- ammo.csv: per round: class access (o = yes, ^ = limited, see note), damage, pen, armor damage, velocity, MP per round
- classes.json (../../ArenaMode.Server/defaults): rule-based class eligibility from patch notes

Still missing live MP values: helmets, helmet attachments, headsets, tactical rigs, armor plates, grenades,
weapon mods, magazines. Those fall back to generate-metascore.py (derived) until a source is found.
