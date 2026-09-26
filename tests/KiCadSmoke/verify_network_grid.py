"""Run with KiCad Python: verify_network_grid.py LIBRARY NEW_OUTPUT_DIRECTORY.
Checks all pin grids, then proves all seven bridge/TVS wire-tip connections via KiCad.
"""
import copy
import json
import math
from pathlib import Path
import re
import subprocess
import sys
import uuid

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

def kids(n, k):
    return [c for c in n if isinstance(c, list) and c and c[0] == k]

def first(n, k):
    return kids(n, k)[0]

def walk(n, k):
    for c in n:
        if isinstance(c, list):
            if c and c[0] == k:
                yield c
            yield from walk(c, k)

def serialize(n):
    return '('+' '.join(serialize(c) if isinstance(c,list) else json.dumps(c) if isinstance(c,Quoted) else c for c in n)+')'

def main():
    library, output = map(Path, sys.argv[1:3])
    output.mkdir(parents=True, exist_ok=False)
    symbols = {s[1]:s for s in kids(parse(library.read_text(encoding='utf8')),'symbol')}
    offgrid = [(name,first(p,'number')[1]) for name,s in symbols.items() for p in walk(s,'pin')
               if any(abs(float(v)/1.27-round(float(v)/1.27)) > 1e-5 for v in first(p,'at')[1:3])]
    assert not offgrid, offgrid
    uid=lambda:str(uuid.uuid4())
    root=uid(); embedded=[]; body=[]; expected=[]
    roles={'C2488': {'1':'+','2':'-','3':'AC2','4':'AC1'}, 'C32677': {'1':'IO1','2':'IO2','3':'GND'}}
    for index,(part,names) in enumerate(roles.items()):
        s=copy.deepcopy(symbols[part]); s[1]=Quoted('youeda:'+part); embedded.append(serialize(s))
        ref=f'D{index+1}'; cx,cy=76.2+index*76.2,76.2
        pins=list(walk(s,'pin'))
        assert {first(p,'number')[1]:first(p,'name')[1] for p in pins}==names
        texts=sorted(t[1] for t in walk(s,'text'))
        assert texts==sorted(['+','-','~','~'] if part=='C2488' else ['12V','12V','7V','7V']), (part,texts)
        for p in pins:
            assert p[1]=='passive'
            px,py,angle=map(float,first(p,'at')[1:]); angle=math.radians(angle)
            x,y=cx+px,cy-py; ex,ey=x-5.08*math.cos(angle),y+5.08*math.sin(angle)
            pin=first(p,'number')[1]; name=f'{part}_PIN_{pin}'; expected.append((name,ref,pin))
            body.append(f'''(wire (pts (xy {x:.6f} {y:.6f}) (xy {ex:.6f} {ey:.6f})) (stroke (width 0) (type default)) (uuid "{uid()}"))
            (label "{name}" (at {ex:.6f} {ey:.6f} 0) (effects (font (size 1.27 1.27)) (justify left bottom)) (uuid "{uid()}"))''')
        body.append(f'''(symbol (lib_id "youeda:{part}") (at {cx} {cy} 0) (unit 1) (in_bom yes) (on_board yes) (dnp no) (uuid "{uid()}")
        (property "Reference" "{ref}" (at {cx} {cy-15.24} 0) (effects (font (size 1.27 1.27))))
        (property "Value" "{part}" (at {cx} {cy+15.24} 0) (effects (font (size 1.27 1.27))))
        (instances (project "network-grid" (path "/{root}" (reference "{ref}") (unit 1)))))''')
    sch=f'(kicad_sch (version 20250114) (generator "eeschema") (uuid "{root}") (paper "A4") (lib_symbols '+' '.join(embedded)+') '+' '.join(body)+')'
    (output/'network-grid.kicad_sch').write_text(sch,encoding='utf8')
    cli=Path(sys.executable).with_name('kicad-cli.exe')
    run=subprocess.run([str(cli),'sch','export','netlist','--output',str(output/'network-grid.net'),str(output/'network-grid.kicad_sch')],capture_output=True)
    (output/'netlist.log').write_bytes(run.stdout+run.stderr); run.check_returncode()
    nets=list(walk(parse((output/'network-grid.net').read_text(encoding='utf8')),'net'))
    for name,ref,pin in expected:
        net=next(n for n in nets if first(n,'name')[1].lstrip('/')==name)
        assert [(first(n,'ref')[1], first(n,'pin')[1]) for n in kids(net,'node')]==[(ref,pin)], name
    result={'symbolsChecked':len(symbols),'offGridPins':offgrid,'verifiedWireConnections':len(expected),'verifiedPinFunctions':roles}
    if len(sys.argv) > 3:
        previous=Path(sys.argv[3])
        old={s[1]:s for s in kids(parse((previous/'youeda.kicad_sym').read_text(encoding='utf8')),'symbol')}
        assert symbols.keys()==old.keys()
        for part in symbols.keys()-roles.keys():
            assert symbols[part]==old[part], (part,'unrelated symbol changed')
        for part in symbols:
            before=parse((previous/'youeda.pretty'/f'{part}.kicad_mod').read_text(encoding='utf8'))
            after=parse((library.parent/'youeda.pretty'/f'{part}.kicad_mod').read_text(encoding='utf8'))
            for fp in (before,after):
                for model in kids(fp,'model'):
                    model[1]=Quoted(Path(model[1]).name)
            assert before==after,(part,'footprint geometry, courtyard or model pose changed')
        result['unchangedOtherSymbols']=len(symbols)-len(roles)
        result['unchangedFootprintsCourtyardsAndModelPoses']=len(symbols)
    (output/'result.json').write_text(json.dumps(result,indent=2),encoding='utf8')
    print(json.dumps(result))

if __name__=='__main__':
    main()
