"""Builds the arena-ui pack the client HUD renders from: the in-match layouts (tools/arena-ui-export.py JSON),
every texture they reference, and the weapon silhouettes the kill log names by SPT short name.

  py pack-arena-ui.py <arena-ui json dir> <rip Assets dir> <out dir> [--db <SPT database dir>]

The output goes next to the client DLL (BepInEx/plugins/maschine-ArenaMode/arena-ui). It is built from the
player's own EFT: Arena install and is never committed or shipped."""
import io, json, os, re, shutil, sys

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

LAYOUTS = ['ArenaCheckPointWidget', 'ArenaRoundGoalWidget', 'KillLogItemPrefab', 'ArenaTeamTabWidget',
           'ArenaRoundInfoWidget', 'ArenaRoundInfoItem', 'ArenaFinalResultWidget']
WEAPON = '5422acb9af1c889c16000029'


def sprite_refs(node, out):
    for c in node.get('components', []):
        s = c.get('fields', {}).get('m_Sprite')
        if isinstance(s, dict) and s.get('sprite'):
            out.add(s['sprite'])
    for child in node.get('children', []):
        sprite_refs(child, out)


def main():
    args = sys.argv[1:]
    db = None
    if '--db' in args:
        i = args.index('--db'); db = args[i + 1]; del args[i:i + 2]
    if len(args) != 3:
        print(__doc__); sys.exit(2)
    layouts, assets, out = args
    os.makedirs(os.path.join(out, 'layouts'), exist_ok=True)
    refs = set()
    for name in LAYOUTS:
        src = os.path.join(layouts, name + '.json')
        tree = json.load(io.open(src, encoding='utf-8'))
        sprite_refs(tree, refs)
        shutil.copyfile(src, os.path.join(out, 'layouts', name + '.json'))
    missing = []
    for rel in sorted(refs):
        src = os.path.join(assets, rel)
        if not os.path.isfile(src):
            missing.append(rel); continue
        dst = os.path.join(out, rel)
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copyfile(src, dst)
    weapons = 0
    if db:
        items = json.load(io.open(os.path.join(db, 'templates', 'items.json'), encoding='utf-8-sig'))
        loc = json.load(io.open(os.path.join(db, 'locales', 'global', 'en.json'), encoding='utf-8-sig'))

        def is_weapon(tpl):
            n, hops = tpl, 0
            while n in items and hops < 12:
                if n == WEAPON: return True
                n = items[n].get('_parent'); hops += 1
            return False

        bad = re.compile(r'[<>:"/\\|?*]')
        for tpl, it in items.items():
            if it.get('_type') != 'Item' or not is_weapon(tpl): continue
            short = loc.get(tpl + ' ShortName')
            if not short or bad.search(short): continue
            src = os.path.join(assets, 'Texture2D', short + '.png')
            if os.path.isfile(src):
                dst = os.path.join(out, 'Texture2D', short + '.png')
                if not os.path.exists(dst):
                    shutil.copyfile(src, dst); weapons += 1
    print(f'{len(LAYOUTS)} layouts, {len(refs) - len(missing)} textures, {weapons} weapon silhouettes -> {out}')
    if missing:
        print('missing textures:', *missing, sep='\n  ')


if __name__ == '__main__':
    main()
