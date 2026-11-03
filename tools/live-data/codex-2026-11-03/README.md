# EFT Arena reference pack

Start with [CLAUDE_HANDOVER.md](CLAUDE_HANDOVER.md). The complete readable tables are in [VALUES.md](VALUES.md).

Use `data/resolved_armory.json` for the working catalog, including realistic inferred prices. Original source values are in `data/armory.json`. Additional ammo, shop, rules, patch changes, provenance, mappings and estimates are in `data/`.

Run `python3 tools/estimate.py` to regenerate estimated purchase prices and the working catalog. Run `python3 tools/validate.py` to validate the package. Python 3.10+; standard library only.

The tables span several patch versions. Estimates are explicit mod defaults. This is an implementation reference, not a plugin that can be dropped into SPT unchanged.
