#!/usr/bin/env python3
"""Extract the layouts for one client build from a WoWDBDefs checkout into a compact JSON file.

Usage: gen_layouts.py <WoWDBDefs checkout> <out.json> [build]   (build defaults to 3.3.5.12340)

The output is an adaptation of WoWDBDefs definitions (CC BY-SA 4.0); see data/wowdbdefs/ATTRIBUTION.md.
Only table layouts are extracted. It contains no game data.
"""
import json
import os
import re
import subprocess
import sys

COLUMN_RE = re.compile(r'^(?P<type>int|float|string|locstring)(?:<(?P<fk>[^>]+)>)?\s+(?P<name>\w+)(?P<unverified>\?)?')
FIELD_RE = re.compile(r'^(?:\$(?P<ann>[^$]*)\$)?(?P<name>\w+)(?:<(?P<size>u?-?\d+)>)?(?:\[(?P<array>\d+)\])?')


def version(text):
    return tuple(int(p) for p in text.strip().split('.'))


def build_matches(line, target):
    for item in line[len('BUILD '):].split(','):
        item = item.strip()
        if '-' in item:
            lo, hi = item.split('-', 1)
            if version(lo) <= target <= version(hi):
                return True
        elif item and version(item) == target:
            return True
    return False


def parse(path, target):
    with open(path, encoding='utf-8') as f:
        lines = [l.rstrip('\n') for l in f]
    columns = {}
    i = 0
    assert lines[0].strip() == 'COLUMNS', path
    i = 1
    while i < len(lines) and lines[i].strip():
        m = COLUMN_RE.match(lines[i].strip())
        if m:
            columns[m['name']] = {'type': m['type'], 'fk': m['fk'], 'unverified': bool(m['unverified'])}
        i += 1
    blocks, current = [], []
    for line in lines[i:] + ['']:
        if line.strip():
            current.append(line.strip())
        elif current:
            blocks.append(current)
            current = []
    for block in blocks:
        headers = [l for l in block if l.startswith(('BUILD ', 'LAYOUT ', 'COMMENT '))]
        if not any(build_matches(l, target) for l in headers if l.startswith('BUILD ')):
            continue
        fields = []
        for line in block:
            if line.startswith(('BUILD ', 'LAYOUT ', 'COMMENT ')):
                continue
            m = FIELD_RE.match(line.split('//')[0].strip())
            if not m:
                raise ValueError('%s: cannot parse field line %r' % (path, line))
            col = columns[m['name']]
            ann = set((m['ann'] or '').split(',')) - {''}
            size = m['size']
            field = {'name': m['name'], 'type': col['type']}
            if col['type'] == 'int':
                if size is None:
                    raise ValueError('%s: int field %s has no size' % (path, m['name']))
                field['size'] = abs(int(size.lstrip('u')))
                field['signed'] = not size.startswith('u')
            if m['array']:
                field['array'] = int(m['array'])
            if 'id' in ann:
                field['id'] = True
            if 'relation' in ann:
                field['relation'] = True
            if 'noninline' in ann:
                field['noninline'] = True
            if col['fk']:
                field['fk'] = col['fk']
            fields.append(field)
        return fields
    return None


def main():
    root, out = sys.argv[1], sys.argv[2]
    build = sys.argv[3] if len(sys.argv) > 3 else '3.3.5.12340'
    target = version(build)
    defs = os.path.join(root, 'definitions')
    tables = {}
    for name in sorted(os.listdir(defs)):
        if name.endswith('.dbd'):
            fields = parse(os.path.join(defs, name), target)
            if fields is not None:
                tables[name[:-4]] = fields
    commit = subprocess.run(['git', '-C', root, 'rev-parse', 'HEAD'], capture_output=True, text=True).stdout.strip()
    doc = {
        'build': build,
        'source': {'name': 'WoWDBDefs', 'url': 'https://github.com/wowdev/WoWDBDefs', 'commit': commit,
                   'license': 'CC BY-SA 4.0'},
        'tables': tables,
    }
    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    with open(out, 'w', encoding='utf-8') as f:
        json.dump(doc, f, indent=1, sort_keys=False)
        f.write('\n')
    print('%d tables for %s from %s' % (len(tables), build, commit[:12]))


if __name__ == '__main__':
    main()
