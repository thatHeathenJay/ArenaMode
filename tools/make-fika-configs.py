"""Writes fika.extract.jsonc (copy of the current fika.jsonc) and fika.arena.jsonc (same file, but the Fika
mod enforcement lists match the arena client) next to the live fika.jsonc. SPT.Server.bat / SPT.ServerArena.bat
copy one of them over fika.jsonc before starting the server. One server, one port, one profile set.

Arena required = every BepInEx plugin of the arena client (from TypeDump --plugins) + Fika + ArenaMode.Fika.
Arena optional = everything the extraction lists know that the arena client does not carry.

Usage: py tools\\make-fika-configs.py
"""
import io, json, re, shutil, sys
sys.stdout.reconfigure(encoding='utf-8', errors='replace')

CFG = r'C:\Games\SPT-4.1\SPT_Runtime\user\mods\fika-server\assets\configs'
PLUGINS = r'C:\Games\SPT-4.1\Tools\arena-src\arena-plugins.txt'


def strip_comments(s):
    out, i, n, instr = [], 0, len(s), False
    while i < n:
        c = s[i]
        if instr:
            out.append(c)
            if c == '\\' and i + 1 < n:
                out.append(s[i + 1]); i += 2; continue
            if c == '"':
                instr = False
            i += 1; continue
        if c == '"':
            instr = True; out.append(c); i += 1; continue
        if s.startswith('//', i):
            j = s.find('\n', i); i = n if j < 0 else j; continue
        if s.startswith('/*', i):
            j = s.find('*/', i + 2); i = n if j < 0 else j + 2; continue
        out.append(c); i += 1
    return ''.join(out)


def mods_node(d):
    return d['client']['mods']


src = io.open(CFG + r'\fika.jsonc', encoding='utf-8-sig').read()
d = json.loads(strip_comments(src))
m = mods_node(d)
req, opt = m['required'], m['optional']
print('extraction: required', len(req), 'optional', len(opt))

arena_plugins = [l.split('|')[0] for l in io.open(PLUGINS, encoding='utf-8') if '|' in l]
arena = [g for g in arena_plugins if not g.startswith('com.SPT.') and g != 'com.bepis.bepinex.configurationmanager']
arena_req = sorted(set(arena) | {'com.fika.core', 'com.maschine.ArenaMode.Fika'})
arena_opt = sorted((set(req) | set(opt)) - set(arena_req))
print('arena: required', len(arena_req), 'optional', len(arena_opt))
print('  required:', ', '.join(arena_req))

shutil.copy(CFG + r'\fika.jsonc', CFG + r'\fika.extract.jsonc')


def replace_array(text, key, values):
    mm = re.search(r'("%s"\s*:\s*\[)(.*?)(\])' % key, text, re.S)
    body = ',\n'.join('        "%s"' % v for v in values)
    return text[:mm.start(2)] + '\n' + body + '\n      ' + text[mm.end(2):]


arena_txt = replace_array(replace_array(src, 'required', arena_req), 'optional', arena_opt)
io.open(CFG + r'\fika.arena.jsonc', 'w', encoding='utf-8').write(arena_txt)
m2 = mods_node(json.loads(strip_comments(arena_txt)))
assert m2['required'] == arena_req and m2['optional'] == arena_opt
print('fika.arena.jsonc written and parses')
