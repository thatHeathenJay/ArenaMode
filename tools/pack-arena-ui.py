"""Builds the arena-ui pack the client HUD renders from: the in-match layouts (tools/arena-ui-export.py JSON),
every texture they reference, and the weapon silhouettes the kill log names by SPT short name.

  py pack-arena-ui.py <arena-ui json dir> <rip Assets dir> <out dir> [--db <SPT database dir>] [--bundles <bundle rip project dir>]

The output goes next to the client DLL (BepInEx/plugins/maschine-ArenaMode/arena-ui). It is built from the
player's own EFT: Arena install and is never committed or shipped."""
import io, json, os, re, shutil, struct, sys

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

LAYOUTS = ['ArenaCheckPointWidget', 'ArenaRoundGoalWidget', 'KillLogItemPrefab', 'ArenaTeamTabWidget', 'Arena Final Countdown', 'ArenaBattleBackgroundPanel', 'ArenaMainMenu', 'DeathInfoPanel', 'ArenaPresetCustomizationScreen', 'ArenaBuildEditorScreen', 'ArenaResultScreen', 'ArenaPresetViewScreen',
           'ArenaRoundInfoWidget', 'ArenaRoundInfoItem', 'ArenaFinalResultWidget', 'APCItemViewBig', 'APCItemViewSmall', 'PresetsScreen', 'EquipmentPresetsPanel', 'ArenaGameModeSelectionScreen', 'ArenaRankingModeSelectionScreen', 'ArenaMapSelectionScreen', 'ArenaMatchingScreen', 'ArenaMatchReadyPopup', 'ArenaGameReadyPopup', 'PresetViewCard', 'PresetItems', 'Header', 'Frame', 'Footer', 'AdditionalVisual', 'BigCustomCreateCard', 'BigCustomPurchaseInfo', 'ArenaExitGamePopup', 'ArenaBattleMenuScreen', 'ArenaPostMatchScreen', 'ArenaSettingsScreen']
WEAPON = '5422acb9af1c889c16000029'

# In-match sounds. Hit marker clips are downmixed to mono and cut to one second for the client's PCM loader
# (hitmarker_<cue>_NN.wav); the rest are copied as exported.
HITMARKER = {'headshot': 'ui_fight_hitmarker_enemy_headshot_', 'kill': 'ui_fight_hitmarker_enemy_kill_'}
SOUNDS = ['arena_button_click.wav', 'result_music_start.wav', 'result_music_loop.wav', 'result_music_end_winner.wav', 'result_music_end_looser.wav', 'arena_button_over.wav', 'arena_menu_escape.wav', 'ui_postmatch_end.wav', 'matchend_music.wav', 'arena_tick_timer.wav', 'arena_mvp_widget_02.ogg',
          'arena_match_ready_popup.ogg', 'arena_wait_match_background_music.ogg']


def read_pcm(path):
    """AssetRipper writes RIFF/data chunk sizes as zero: parse the chunks by hand and take data to end of file."""
    b = io.open(path, 'rb').read()
    if b[:4] != b'RIFF' or b[8:12] != b'WAVE':
        raise ValueError(f'{path}: not a RIFF/WAVE file')
    pos, fmt, data = 12, None, None
    while pos + 8 <= len(b):
        tag, size = b[pos:pos + 4], struct.unpack('<I', b[pos + 4:pos + 8])[0]
        body = b[pos + 8:pos + 8 + size] if size else b[pos + 8:]
        if tag == b'fmt ':
            fmt = struct.unpack('<HHIIHH', body[:16])
        elif tag == b'data':
            data = body
            if not size:
                break
        pos += 8 + size + (size & 1)
    if fmt is None or data is None or fmt[0] != 1 or fmt[5] != 16:
        raise ValueError(f'{path}: expected 16-bit PCM')
    return fmt[1], fmt[2], data


def write_wav(path, rate, samples):
    data = struct.pack('<%dh' % len(samples), *samples)
    with io.open(path, 'wb') as f:
        f.write(b'RIFF' + struct.pack('<I', 36 + len(data)) + b'WAVE')
        f.write(b'fmt ' + struct.pack('<IHHIIHH', 16, 1, 1, rate, rate * 2, 2, 16))
        f.write(b'data' + struct.pack('<I', len(data)) + data)


def mono_second(src, dst):
    ch, rate, data = read_pcm(src)
    samples = struct.unpack('<%dh' % (len(data) // 2), data[:len(data) // 2 * 2])
    if ch > 1:
        samples = [int(sum(samples[i:i + ch]) / ch) for i in range(0, len(samples) - ch + 1, ch)]
    write_wav(dst, rate, samples[:rate])


def fix_wav(src, dst):
    """Copy with real chunk sizes so Unity's loader accepts it."""
    ch, rate, data = read_pcm(src)
    with io.open(dst, 'wb') as f:
        f.write(b'RIFF' + struct.pack('<I', 36 + len(data)) + b'WAVE')
        f.write(b'fmt ' + struct.pack('<IHHIIHH', 16, 1, ch, rate, rate * 2 * ch, 2 * ch, 16))
        f.write(b'data' + struct.pack('<I', len(data)) + data)


# Live commentator (arenaspeakertagbankmanagereng.bundle via Rip-ArenaBundles.ps1): the families Team Fight uses,
# capped per family so the pack stays small (~500 KB per line).
COMMENTATOR = ['start', 'first_blood', 'player_death', 'tm_player_death_red', 'tm_player_death_blue', 'killing_streak',
               'tm_sweep_team', 'blue_team_lead_match_start', 'blue_team_lead_match_middle', 'blue_team_lead_match_end',
               'red_team_lead_match_start', 'red_team_lead_match_middle', 'red_team_lead_match_end',
               '3_min_left', '1_min_left', 'end_of_time', 'eng_final_battle', 'eng_match_end_blue', 'eng_match_end_red',
               'low_death_intensity', 'high_death_intensity', 'count_down']
COMMENTATOR_VARIANTS = 6
STINGS = {'arenasoundeffectswrapper': ['win_shield.ogg', 'loose_shield.ogg', 'draw_shield.wav', 'big_score.wav',
                                       'small_score_2.wav', 'result_music_start.wav', 'result_music_loop.wav',
                                       'result_music_end_winner.wav', 'result_music_end_looser.wav', 'final_screen.ogg'],
          'arenasoundeffectstagbankmanager': ['asrena_start_signal_dry.wav']}


def pack_bundles(bundles, out):
    """Commentator lines and result stings from the bundle rip into sounds/commentator and sounds/."""
    sounds = os.path.join(out, 'sounds')
    voice = os.path.join(sounds, 'commentator')
    os.makedirs(voice, exist_ok=True)
    count = 0
    clips = os.path.join(bundles, 'arenaspeakertagbankmanagereng', 'ExportedProject', 'Assets', 'AudioClip')
    names = sorted(n for n in os.listdir(clips) if n.endswith('.wav')) if os.path.isdir(clips) else []
    for family in COMMENTATOR:
        prefix = 'eng_commentator_' + family + '_'
        picked = [n for n in names if n.startswith(prefix) and n[len(prefix):-4].isdigit()][:COMMENTATOR_VARIANTS]
        if not picked and family == 'count_down':
            picked = [n for n in names if n.startswith('eng_commentator_count_down')]
        for i, n in enumerate(picked, start=1):
            # every file ends in _NN so the client can split family and variant
            name = n if n[len(prefix):-4].isdigit() else f'eng_commentator_{family}_{i:02d}.wav'
            fix_wav(os.path.join(clips, n), os.path.join(voice, name)); count += 1
    draws = [n for n in names if n.startswith('eng_clash_draw_')][:COMMENTATOR_VARIANTS]
    for n in draws:
        fix_wav(os.path.join(clips, n), os.path.join(voice, 'eng_commentator_draw_' + n[len('eng_clash_draw_'):])); count += 1
    for bundle, files in STINGS.items():
        folder = os.path.join(bundles, bundle, 'ExportedProject', 'Assets', 'AudioClip')
        for name in files:
            src = os.path.join(folder, name)
            if not os.path.isfile(src):
                continue
            if name.endswith('.wav'):
                fix_wav(src, os.path.join(sounds, name))
            else:
                shutil.copyfile(src, os.path.join(sounds, name))
            count += 1
    return count


def pack_sounds(assets, out):
    clips = os.path.join(assets, 'AudioClip')
    sounds = os.path.join(out, 'sounds')
    os.makedirs(sounds, exist_ok=True)
    count = 0
    for cue, prefix in HITMARKER.items():
        for name in sorted(os.listdir(clips)):
            if name.startswith(prefix) and name.endswith('.wav'):
                mono_second(os.path.join(clips, name), os.path.join(sounds, f'hitmarker_{cue}_{name[len(prefix):]}'))
                count += 1
    for name in SOUNDS:
        src = os.path.join(clips, name)
        if os.path.isfile(src):
            if name.endswith('.wav'):
                fix_wav(src, os.path.join(sounds, name))
            else:
                shutil.copyfile(src, os.path.join(sounds, name))
            count += 1
    return count


def sprite_refs(node, out):
    for c in node.get('components', []):
        s = c.get('fields', {}).get('m_Sprite')
        if isinstance(s, dict) and s.get('sprite'):
            out.add(s['sprite'])
    for child in node.get('children', []):
        sprite_refs(child, out)


def main():
    args = sys.argv[1:]
    db = bundles = None
    if '--db' in args:
        i = args.index('--db'); db = args[i + 1]; del args[i:i + 2]
    if '--bundles' in args:
        i = args.index('--bundles'); bundles = args[i + 1]; del args[i:i + 2]
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
    sounds = pack_sounds(assets, out)
    voice = pack_bundles(bundles, out) if bundles else 0
    print(f'{len(LAYOUTS)} layouts, {len(refs) - len(missing)} textures, {weapons} weapon silhouettes, {sounds} sounds, {voice} bundle clips -> {out}')
    if missing:
        print('missing textures:', *missing, sep='\n  ')


if __name__ == '__main__':
    main()
