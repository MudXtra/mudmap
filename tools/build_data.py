#!/usr/bin/env python3
"""Build mud.json for the Mud Maps apps from the Graphify maps and GitHub pull requests.

Areas come from each repository's real folders (a MudBlazor component with its tests and
docs pages is one area). Rooms are Graphify communities inside an area. Pull requests from
the last N days are matched file by file onto areas and rooms.

Usage: build_data.py [--days 30] [--out src/MudMaps.Shared/wwwroot/mud.json] [--pr-cache DIR]
The clones are read from $MUDMAPS_REPOS (default ./repos), each with graphify-out/graph.json.
"""
import argparse
import collections
import datetime as dt
import json
import pathlib
import re
import os
import subprocess

ROOT = pathlib.Path(os.environ.get('MUDMAPS_REPOS', 'repos'))

# Repository, display name, area budget. Order is drawing order.
REPOS = [
    ('MudBlazor/MudBlazor', 'MudBlazor', 24),
    ('fgilde/MudBlazor.Extensions', 'MudBlazor.Extensions', 14),
    ('CodeBeamOrg/CodeBeam.MudBlazor.Extensions', 'CodeBeam Extensions', 10),
    ('MudXtra/MudX', 'MudX', 6),
    ('MudBlazor/TryMudBlazor', 'TryMudBlazor', 6),
    ('danheron/Heron.MudCalendar', 'Heron Calendar', 5),
    ('MudBlazor/Templates', 'Templates', 4),
    ('0phois/MudBlazor.StaticInput', 'Static Inputs', 4),
    ('MudBlazor/ThemeManager', 'ThemeManager', 3),
    ('CodeBeamOrg/UltimateAuth', 'UltimateAuth', 6),
    ('fgilde/MudExRichTextEditor', 'MudEx RichTextEditor', 4),
    ('MudXtra/MudXThemeCreator', 'ThemeCreator', 4),
    ('MudXtra/MudXtra.CodeAnalysis', 'CodeAnalysis', 3),
    ('MudXtra/LottiePlayer', 'LottiePlayer', 3),
    ('MudBlazor/MudBlazor.Icons', 'Icons', 2),
]

JUNK = re.compile(r'(\.min\.(js|css)$|/lib/|node_modules/|/wwwroot/(?!.*\.(ts|razor)$)|\.resx$|/Translations?/|\.ya?ml$'
                  r'|/bin/|/obj/|\.github/|\.json$|\.md$|\.svg$|\.map$|/dist/)', re.I)
GENERIC = {'Task', 'List', 'IEnumerable', 'IReadOnlyList', 'Action', 'Type', 'Fact', 'TestCase', 'Test',
           'XElement', 'CultureInfo', 'CancellationToken', 'RenderFragment', 'IJSRuntime', 'IRenderedComponent',
           'string', 'int', 'bool', 'object', 'EventCallback', 'Func', 'Dictionary', 'ValueTask'}
PLURALS = {'Charts': 'Chart', 'Dialogs': 'Dialog', 'Pickers': 'Picker', 'Inputs': 'Input', 'Buttons': 'Button', 'Menus': 'Menu', 'Lists': 'List'}
COMPONENT_DIRS = {'components', 'testcomponents'}


def norm(name):
    """'MudDataGridTests.cs' -> 'DataGrid'."""
    name = re.sub(r'\.(razor\.cs|razor|cs|ts|js|scss|css)$', '', name)
    name = re.sub(r'(Tests?|Examples?|Page)$', '', name)
    name = re.sub(r'^(MudEx|MudX|Mud)(?=[A-Z])', '', name)
    if '.' in name:
        return 'Core'
    return PLURALS.get(name, name) or 'Core'


def area_of(path):
    """Folder-based area key for a repository-relative path, or None for noise."""
    if not path or JUNK.search(path):
        return None
    parts = [p for p in path.split('/') if p]
    if len(parts) < 2:
        return 'Core'
    low = [p.lower() for p in parts]
    for i, p in enumerate(low[:-1]):
        if p in COMPONENT_DIRS:
            return norm(parts[i + 1])
    project = parts[1] if parts[0].lower() in ('src', 'source') and len(parts) > 2 else parts[0]
    if 'docs' in project.lower() or 'website' in project.lower() or 'sample' in project.lower():
        return 'Docs'
    if 'analyzer' in project.lower():
        return 'Analyzers'
    idx = parts.index(project)
    rest = parts[idx + 1:]
    if len(rest) >= 2:
        return norm(rest[0])
    return 'Core'


def label(key):
    return {'Docs': 'Docs & samples', 'Core': 'Core', 'Unit': 'Unit tests', 'TScripts': 'TypeScript'}.get(key, re.sub(r'(?<=[a-z0-9])(?=[A-Z])', ' ', key))


def prs_for(repo, days, cache):
    path = cache / (repo.replace('/', '_') + '.json')
    if path.exists():
        return json.loads(path.read_text())
    since = (dt.date.today() - dt.timedelta(days=days)).isoformat()
    out = subprocess.run(['gh', 'pr', 'list', '-R', repo, '--state', 'all', '--search', f'created:>={since}',
                          '--limit', '400', '--json', 'number,title,mergedAt,files,url'],
                         capture_output=True, text=True, timeout=300, check=True).stdout
    path.write_text(out)
    return json.loads(out)


def repo_meta(repo):
    out = subprocess.run(['gh', 'api', f'repos/{repo}', '--jq', '{stars: .stargazers_count, url: .html_url}'],
                         capture_output=True, text=True, timeout=60, check=True).stdout
    return json.loads(out)


def build(args):
    cache = pathlib.Path(args.pr_cache)
    cache.mkdir(parents=True, exist_ok=True)
    hoods, clusters, roads, repos = [], [], collections.Counter(), []
    type_index = {}  # type label -> hood key, for cross-repo roads (MudBlazor first)
    pending_external = []  # (hood key, external label)

    for full, name, budget in REPOS:
        graph = ROOT / full / 'graphify-out/graph.json'
        g = json.loads(graph.read_text())
        nodes = {n['id']: n for n in g['nodes']}
        node_area, size, canon = {}, collections.Counter(), {}
        for n in g['nodes']:
            a = area_of(n.get('source_file'))
            if a:
                a = canon.setdefault(a.lower(), a)
            if a:
                node_area[n['id']] = a
                size[a] += 1
        keep = [a for a, _ in size.most_common(budget)]
        other = len(size) > budget
        slug = re.sub(r'[^a-z0-9]+', '-', name.lower()).strip('-')
        key_of = {a: f'{slug}/{a.lower()}' for a in keep}
        other_key = f'{slug}/other'

        def hood_key(a):
            return key_of.get(a, other_key if other else None)

        degree = collections.Counter()
        for link in g['links']:
            degree[link['source']] += 1
            degree[link['target']] += 1
            ha, hb = hood_key(node_area.get(link['source'])), hood_key(node_area.get(link['target']))
            if ha and hb and ha != hb:
                roads[tuple(sorted((ha, hb)))] += 1
            for mine, theirs in ((link['source'], link['target']), (link['target'], link['source'])):
                if mine in node_area and theirs not in node_area and theirs in nodes and not nodes[theirs].get('source_file'):
                    pending_external.append((hood_key(node_area[mine]), nodes[theirs].get('label', '')))

        # Rooms: communities assigned to the area most of their nodes live in.
        comm_nodes = collections.defaultdict(list)
        for nid, a in node_area.items():
            comm_nodes[nodes[nid].get('community')].append(nid)
        rooms_by_hood = collections.defaultdict(list)
        file_rooms = collections.defaultdict(set)
        for cid, members in comm_nodes.items():
            if cid is None or len(members) < 3:
                continue
            hk = hood_key(collections.Counter(node_area[m] for m in members).most_common(1)[0][0])
            if not hk:
                continue
            top = sorted(members, key=lambda m: -degree[m])
            labels = []
            for m in top:
                lab = nodes[m].get('label', '')
                if lab and lab not in labels:
                    labels.append(lab)
                if len(labels) == 8:
                    break
            dirs = collections.Counter('/'.join(nodes[m]['source_file'].split('/')[-3:-1]) for m in members)
            stems = collections.Counter(re.sub(r'\..*$', '', nodes[m]['source_file'].split('/')[-1]) for m in members)
            stem = stems.most_common(1)[0][0]
            typed = next((l for l in labels if re.fullmatch(r'[A-Z][A-Za-z0-9]+', l) and l not in GENERIC), None)
            room_name = stem if re.fullmatch(r'[A-Z][A-Za-z0-9]+', stem or '') else (typed or stem or f'Cluster {cid}')
            room = {'id': f'{slug}:{cid}', 'hood': hk, 'n': len(members), 'dir': dirs.most_common(1)[0][0], 'top': labels, 'prs': 0,
                    'name': room_name}
            rooms_by_hood[hk].append(room)
            for m in members:
                file_rooms[nodes[m]['source_file']].add(room['id'])
        room_index = {}
        for hk, rs in rooms_by_hood.items():
            rs.sort(key=lambda r: -r['n'])
            seen = collections.Counter(r['name'] for r in rs)
            used = set()
            for r in rs:
                if seen[r['name']] > 1:
                    alt = next((l.split('.')[-1].rstrip('()') for l in r['top']
                                if re.match(r'[A-Z]', l.split('.')[-1]) and l.split('.')[-1].rstrip('()') not in GENERIC
                                and l.split('.')[-1].rstrip('()') != r['name']
                                and f"{r['name']} · {l.split('.')[-1].rstrip('()')}" not in used), None)
                    r['name'] = f"{r['name']} · {alt}" if alt else r['name']
                used.add(r['name'])
            for r in rs[:16]:
                room_index[r['id']] = r
                clusters.append(r)

        # Types for cross-repo roads.
        if full == 'MudBlazor/MudBlazor':
            for nid, a in node_area.items():
                lab = nodes[nid].get('label', '')
                if re.fullmatch(r'Mud[A-Z]\w+', lab) and hood_key(a):
                    type_index.setdefault(lab, hood_key(a))

        # Pull requests onto areas and rooms.
        prs = prs_for(full, args.days, cache)
        hood_prs, merged = collections.Counter(), 0
        for p in prs:
            merged += bool(p.get('mergedAt'))
            hit_hoods, hit_rooms = set(), set()
            for f in p.get('files') or []:
                a = area_of(f['path'])
                a = canon.get(a.lower(), a) if a else None
                hk = hood_key(a) if a else None
                if hk:
                    hit_hoods.add(hk)
                for rid in file_rooms.get(f['path'], ()):
                    if rid in room_index:
                        hit_rooms.add(rid)
            for hk in hit_hoods:
                hood_prs[hk] += 1
            for rid in hit_rooms:
                room_index[rid]['prs'] += 1

        for a in keep:
            hoods.append({'key': key_of[a], 'label': label(a), 'repo': name, 'size': size[a], 'prs': hood_prs[key_of[a]]})
        if other:
            rest = sum(v for a, v in size.items() if a not in keep)
            hoods.append({'key': other_key, 'label': 'Everything else', 'repo': name, 'size': rest, 'prs': hood_prs[other_key]})

        meta = repo_meta(full)
        repos.append({'name': name, 'fullName': full, 'url': meta['url'], 'stars': meta['stars'],
                      'prs': len(prs), 'merged': merged, 'nodes': sum(size.values()), 'built': g.get('built_at_commit')})
        print(f'{name}: {sum(size.values())} items, {len(keep)} areas, {len(prs)} PRs')

    for hk, lab in pending_external:
        target = type_index.get(lab)
        if hk and target and not hk.startswith('mudblazor/'):
            roads[tuple(sorted((hk, target)))] += 1

    mb = next(r for r in repos if r['name'] == 'MudBlazor')
    ext = next(r for r in repos if r['name'] == 'MudBlazor.Extensions')
    data = {
        'headline': {'prs': mb['prs'], 'merged': mb['merged']},
        'extensionsPrs': ext['prs'],
        'others': [{'name': r['name'], 'fullName': r['fullName'], 'prs': r['prs'], 'stars': r['stars']}
                   for r in repos if r['name'] not in ('MudBlazor', 'MudBlazor.Extensions')],
        'generatedAt': dt.datetime.now(dt.timezone.utc).isoformat(timespec='minutes'),
        'windowDays': args.days,
        'repos': repos,
        'hoods': hoods,
        'roads': [{'a': a, 'b': b, 'w': w} for (a, b), w in roads.most_common(220)],
        'clusters': clusters,
    }
    pathlib.Path(args.out).write_text(json.dumps(data, separators=(',', ':')))
    print(f'wrote {args.out}: {len(hoods)} areas, {len(clusters)} rooms, {len(data["roads"])} roads')


if __name__ == '__main__':
    ap = argparse.ArgumentParser()
    ap.add_argument('--days', type=int, default=30)
    ap.add_argument('--out', default='src/MudMaps.Shared/wwwroot/mud.json')
    ap.add_argument('--pr-cache', default=os.path.join(os.environ.get('XDG_CACHE_HOME', os.path.expanduser('~/.cache')), 'mudmaps-prs'))
    build(ap.parse_args())
