"""Offline acceptance against a previous audit. Run with KiCad's bundled Python.

Usage: verify_kicad_acceptance.py NEW_LIBRARY PREVIOUS_AUDIT NEW_REPORT_DIRECTORY
Creates test boards/schematic only in the new report directory; inputs are read-only.
"""
import collections
import json
import math
from pathlib import Path
import re
import sys
import subprocess
import uuid
import pcbnew


class Quoted(str):
    pass


def parse(text):
    stack, roots = [], []
    for token in re.findall(r'"(?:\\.|[^"\\])*"|[()]|[^\s()]+', text):
        if token == '(':
            node = []
            (stack[-1] if stack else roots).append(node)
            stack.append(node)
        elif token == ')':
            stack.pop()
        else:
            stack[-1].append(Quoted(json.loads(token)) if token.startswith('"') else token)
    assert not stack and len(roots) == 1
    return roots[0]


def kids(node, key):
    return [n for n in node if isinstance(n, list) and n and n[0] == key]


def first(node, key):
    return kids(node, key)[0]


def walk(node, key):
    for child in node:
        if isinstance(child, list):
            if child and child[0] == key:
                yield child
            yield from walk(child, key)


def serialize(node):
    return '(' + ' '.join(serialize(v) if isinstance(v, list) else json.dumps(v) if isinstance(v, Quoted) else v for v in node) + ')'


lib, old, report = map(lambda x: Path(x).resolve(), sys.argv[1:])
report.mkdir(exist_ok=False)
manifest = json.loads((old / 'audit-data.json').read_text(encoding='utf8'))
metadata = {r['part']: r for r in manifest['components']}
symbols = {s[1]: s for s in kids(parse((lib / 'youeda.kicad_sym').read_text(encoding='utf8')), 'symbol')}
previous = {s[1]: s for s in kids(parse((old / 'library-copy/youeda.kicad_sym').read_text(encoding='utf8')), 'symbol')}
assert set(symbols) == set(metadata), 'Component inventory changed'
counts = collections.Counter()
failures = []
for part, symbol in symbols.items():
    footprint = parse((lib / 'youeda.pretty' / (part + '.kicad_mod')).read_text(encoding='utf8'))
    pins, oldpins = list(walk(symbol, 'pin')), list(walk(previous[part], 'pin'))
    pads = [p for p in kids(footprint, 'pad') if p[1]]
    assert {first(p, 'number')[1] for p in pins} == {p[1] for p in pads}, part
    assert len(pins) == len(oldpins), part
    expected = []
    native = metadata[part]['decision'] != 'EasyEDA fallback'
    for oldunit in kids(previous[part], 'symbol'):
        positions = []
        for before in walk(oldunit, 'pin'):
            ox, oy, oa = map(float, first(before, 'at')[1:])
            length = float(first(before, 'length')[1])
            positions.append((ox + length * math.cos(math.radians(oa)), oy + length * math.sin(math.radians(oa))) if native else (ox,oy))
        dx = dy = 0
        if len(set(positions)) >= 2:
            x,y = positions[0]
            dx,dy = round(x/1.27)*1.27-x,round(y/1.27)*1.27-y
            if any(abs(v-round(v/1.27)*1.27) > .00001 for x,y in positions for v in (x+dx,y+dy)):
                dx=dy=0
            elif abs(dx) < .00001 and abs(dy) < .00001: dx=dy=0
        expected.extend((x+dx,y+dy) for x,y in positions)
        if abs(dx) > .00001 or abs(dy) > .00001: counts['translatedSymbolUnits'] += 1
    assert len(expected) == len(pins)
    for pin, before, (ex,ey) in zip(pins, oldpins, expected):
        assert first(pin, 'number') == first(before, 'number'), part
        x, y, angle = map(float, first(pin, 'at')[1:])
        ox, oy, oa = map(float, first(before, 'at')[1:])
        length = float(first(before, 'length')[1])
        if metadata[part]['decision'] != 'EasyEDA fallback':
            assert abs(x-ex) < .00001 and abs(y-ey) < .00001 and (angle-oa)%360 == 180, (part, 'native pin anchor')
            counts['nativePinsChecked'] += 1
        else:
            assert abs(x-ex) < .00001 and abs(y-ey) < .00001 and (angle + oa)%360 == 0, (part, 'EasyEDA pin anchor')
            counts['easyEdaPinsChecked'] += 1
    props = {p[1]: p[2] for p in kids(symbol, 'property')}
    assert props['LCSC'] == part and props['Footprint'] == 'youeda:'+part
    counts['pricesPreserved'] += 'JLCPCB Unit Price' in props
    counts['datasheetsAvailable'] += bool(props['Datasheet'])
    arcs = len(kids(footprint, 'fp_arc'))
    assert arcs >= metadata[part]['rawFootprintArcs'], (part, 'missing arc')
    counts['footprintArcs'] += arcs
    counts['partsWithArcs'] += arcs > 0
    courtyard = [n for n in kids(footprint, 'fp_rect') if first(n, 'layer')[1] == 'F.CrtYd']
    assert len(courtyard) == 1, part
    x0,y0 = map(float,first(courtyard[0],'start')[1:])
    x1,y1 = map(float,first(courtyard[0],'end')[1:])
    for pad in pads:
        at = first(pad,'at'); px,py = map(float,at[1:3]); angle = math.radians(float(at[3]) if len(at)>3 else 0)
        w,h = map(float,first(pad,'size')[1:])
        rx=(abs(math.cos(angle))*w+abs(math.sin(angle))*h)/2
        ry=(abs(math.sin(angle))*w+abs(math.cos(angle))*h)/2
        if pad[3] == 'custom':
            pts=[list(map(float,p[1:])) for p in walk(pad,'xy')]
            extent=(px+min(p[0] for p in pts),py+min(p[1] for p in pts),px+max(p[0] for p in pts),py+max(p[1] for p in pts))
        else: extent=(px-rx,py-ry,px+rx,py+ry)
        assert x0 <= extent[0]-.24999 and y0 <= extent[1]-.24999 and x1 >= extent[2]+.24999 and y1 >= extent[3]+.24999, (part,'courtyard clearance')
    assert (lib / 'youeda.3dshapes' / (part+'.step')).is_file(),part
    counts['parts'] += 1

# A real KiCad netlist, not just a text/coordinate assertion, checks the regression.
import copy
symbol = copy.deepcopy(symbols['C11702'])
symbol[1] = Quoted('youeda:C11702')
grid_symbol = copy.deepcopy(symbols['C6186'])
grid_symbol[1] = Quoted('youeda:C6186')
uid = lambda: str(uuid.uuid4())
root = uid()
sch = f'(kicad_sch (version 20250114) (generator "eeschema") (uuid "{root}") (paper "A4") (lib_symbols {serialize(symbol)} {serialize(grid_symbol)})'
for idx,y,end,label in [(1,100,94.92,'VISIBLE_OUTER_END'),(2,120,97.46,'OLD_BODY_ROOT')]:
    sch += f'''(wire (pts (xy 90 {y}) (xy {end} {y})) (stroke (width 0) (type default)) (uuid "{uid()}"))
    (label "{label}" (at 90 {y} 0) (effects (font (size 1.27 1.27)) (justify left bottom)) (uuid "{uid()}"))
    (symbol (lib_id "youeda:C11702") (at 100 {y} 0) (unit 1) (in_bom yes) (on_board yes) (dnp no) (uuid "{uid()}")
    (property "Reference" "R{idx}" (at 100 {y-5.08} 0) (effects (font (size 1.27 1.27))))
    (property "Value" "C11702" (at 100 {y+5.08} 0) (effects (font (size 1.27 1.27))))
    (instances (project "pin-anchor-test" (path "/{root}" (reference "R{idx}") (unit 1)))))'''
sch += f'''(wire (pts (xy 135.89 99.06) (xy 140.97 99.06)) (stroke (width 0) (type default)) (uuid "{uid()}"))
    (label "GRID_REGULATOR" (at 135.89 99.06 0) (effects (font (size 1.27 1.27)) (justify left bottom)) (uuid "{uid()}"))
    (symbol (lib_id "youeda:C6186") (at 152.4 101.6 0) (unit 1) (in_bom yes) (on_board yes) (dnp no) (uuid "{uid()}")
    (property "Reference" "U1" (at 152.4 90 0) (effects (font (size 1.27 1.27))))
    (property "Value" "C6186" (at 152.4 112 0) (effects (font (size 1.27 1.27))))
    (instances (project "pin-anchor-test" (path "/{root}" (reference "U1") (unit 1)))))'''
(report/'pin-anchor-test.kicad_sch').write_text(sch+')',encoding='utf8')
cli=Path(sys.executable).with_name('kicad-cli.exe')
subprocess.run([str(cli),'sch','export','netlist','--output',str(report/'pin-anchor-test.net'),str(report/'pin-anchor-test.kicad_sch')],check=True)
netlist=parse((report/'pin-anchor-test.net').read_text(encoding='utf8'))
visible=next(net for net in walk(netlist,'net') if first(net,'name')[1].lstrip('/')=='VISIBLE_OUTER_END')
assert any(first(n,'ref')[1]=='R1' and first(n,'pin')[1]=='1' for n in kids(visible,'node')), 'Outer pin tip is electrically disconnected'
counts['outerPinNetlistConnected']=1
grid_net=next(net for net in walk(netlist,'net') if first(net,'name')[1].lstrip('/')=='GRID_REGULATOR')
assert any(first(n,'ref')[1]=='U1' and first(n,'pin')[1]=='1' for n in kids(grid_net,'node')), 'Regulator is disconnected from grid-aligned wire'
counts['gridRegulatorNetlistConnected']=1

# One instance of every distinct footprint/model/pose from the earlier audit.
reps = [g[0] for g in manifest['footprintGroups']]
for page in range(math.ceil(len(reps)/20)):
    board = pcbnew.BOARD()
    for i,part in enumerate(reps[page*20:(page+1)*20]):
        fp = pcbnew.FootprintLoad(str(lib/'youeda.pretty'),part)
        assert fp, part
        fp.SetPosition(pcbnew.VECTOR2I(pcbnew.FromMM(25+i%5*30),pcbnew.FromMM(25+i//5*30)))
        fp.SetReference(part); fp.Value().SetVisible(False)
        board.Add(fp)
    for a,b in [((10,10),(160,10)),((160,10),(160,130)),((160,130),(10,130)),((10,130),(10,10))]:
        edge=pcbnew.PCB_SHAPE(); edge.SetShape(pcbnew.SHAPE_T_SEGMENT); edge.SetLayer(pcbnew.Edge_Cuts)
        edge.SetStart(pcbnew.VECTOR2I(pcbnew.FromMM(a[0]),pcbnew.FromMM(a[1])))
        edge.SetEnd(pcbnew.VECTOR2I(pcbnew.FromMM(b[0]),pcbnew.FromMM(b[1])))
        edge.SetWidth(pcbnew.FromMM(.05)); board.Add(edge)
    path=report/f'inspection-{page+1}.kicad_pcb'
    pcbnew.SaveBoard(str(path),board)
    path.write_text(path.read_text(encoding='utf8').replace('${KIPRJMOD}/youeda.3dshapes/',(lib/'youeda.3dshapes').as_posix()+'/'),encoding='utf8')
counts['nativeFootprintsLoaded'] = len(reps)
(report/'acceptance.json').write_text(json.dumps(dict(counts),indent=2),encoding='utf8')
print(json.dumps(dict(counts),indent=2))
