"""Independent read-only comparison with the previous 1.3.8 export.
Usage (KiCad Python): verify_kicad_family_grid.py NEW OLD NEW_REPORT.json
"""
import collections
import hashlib
import json
from pathlib import Path
import re
import sys

def parse(text):
    roots,stack=[],[]
    for t in re.findall(r'"(?:\\.|[^"\\])*"|[()]|[^\s()]+',text):
        if t=='(':
            n=[]; (stack[-1] if stack else roots).append(n); stack.append(n)
        elif t==')': stack.pop()
        else: stack[-1].append(json.loads(t) if t.startswith('"') else t)
    assert not stack and len(roots)==1
    return roots[0]

def kids(n,k): return [v for v in n if isinstance(v,list) and v and v[0]==k]
def walk(n,k):
    for v in n:
        if isinstance(v,list):
            if v and v[0]==k: yield v
            yield from walk(v,k)
def first(n,k): return kids(n,k)[0]
def read(p): return parse(p.read_text(encoding='utf8'))
def syms(p): return {s[1]:s for s in kids(read(p),'symbol')}
def props(n): return {p[1]:p[2] for p in kids(n,'property')}
def digest(p): return hashlib.sha256(p.read_bytes()).hexdigest()

new,old,report=map(Path,sys.argv[1:])
assert not report.exists()
current=syms(new/'youeda.kicad_sym'); previous=syms(old/'youeda.kicad_sym')
assert current.keys()==previous.keys()
counts=collections.Counter(); shifted=[]; offgrid=[]; prices=[]; family=[]; seen=collections.Counter()
for part,symbol in current.items():
    before=previous[part]
    oldunits=kids(before,'symbol'); units=kids(symbol,'symbol')
    assert len(units)==len(oldunits)
    for i,(a,b) in enumerate(zip(oldunits,units)):
        # Compare every emitted coordinate and every non-coordinate token recursively.
        # Only a single XY translation per whole unit is allowed.
        pairs=[]
        def compare(a,b):
            assert type(a)==type(b),(part,'node type')
            if not isinstance(a,list):
                assert a==b,(part,a,b); return
            assert len(a)==len(b),(part,'shape length')
            if a and a[0] in ('at','xy','start','end','center'):
                pairs.append((float(b[1])-float(a[1]),float(b[2])-float(a[2])))
                assert a[3:]==b[3:],(part,'angle changed')
            else:
                for av,bv in zip(a,b): compare(av,bv)
        compare(a,b)
        if pairs:
            dx,dy=pairs[0]
            assert all(abs(x-dx)<.000002 and abs(y-dy)<.000002 for x,y in pairs),(part,'not rigid')
            if abs(dx)>.00001 or abs(dy)>.00001:
                shifted.append(dict(part=part,unit=i+1,dx=dx,dy=dy))
        pins=list(walk(b,'pin'))
        bad=[first(p,'number')[1] for p in pins if any(abs(float(v)-round(float(v)/1.27)*1.27)>.00001 for v in first(p,'at')[1:3])]
        if bad: offgrid.append(dict(part=part,unit=i+1,pins=bad))
    p=props(symbol)
    tiers=[(int(q),float(v)) for q,v in re.findall(r'(\d+)\+: ([\d.]+)',p.get('JLCPCB Price Tiers',''))]
    if tiers:
        assert float(p['JLCPCB Unit Price'].split()[0])==tiers[0][1],part
        assert int(p['JLCPCB Price Tier Quantity'])==tiers[0][0],part
        assert p['JLCPCB Price Quantity']=='1' and p['JLCPCB Price Basis'],part
        prices.append(part)
    fp=read(new/'youeda.pretty'/(part+'.kicad_mod')); oldfp=read(old/'youeda.pretty'/(part+'.kicad_mod'))
    model=first(fp,'model'); oldmodel=first(oldfp,'model')
    assert Path(model[1]).is_absolute() and Path(model[1]).is_file(),part
    assert model[2:]==oldmodel[2:],(part,'unreviewed model pose changed')
    oldmodel[1]=model[1]
    assert fp==oldfp,(part,'footprint geometry changed')
    assert digest(new/'youeda.3dshapes'/(part+'.step'))==digest(old/'youeda.3dshapes'/(part+'.step')),part
    counts['footprintsAndModelPosesPreserved']+=1

for path in sorted((new/'Family Libraries').rglob('*.kicad_sym')):
    f=path.stem; contents=syms(path)
    for part,symbol in contents.items():
        seen[part]+=1
        p=props(symbol); assert p['Footprint']==f+':'+part
        for property in kids(symbol,'property'):
            if property[1]=='Footprint': property[2]='youeda:'+part
        assert symbol==current[part],(part,'family symbol differs beyond nickname')
        fp=read(path.parent/(f+'.pretty')/(part+'.kicad_mod'))
        combined=read(new/'youeda.pretty'/(part+'.kicad_mod'))
        model=first(fp,'model'); modelpath=Path(model[1]); assert modelpath.is_file()
        assert digest(modelpath)==digest(new/'youeda.3dshapes'/(part+'.step'))
        model[1]=first(combined,'model')[1]
        assert fp==combined,part
    family.append(dict(family=f,parts=len(contents)))
assert set(seen)==set(current) and all(n==1 for n in seen.values())
for table in (new/'Family Libraries').rglob('*-lib-table'):
    aliases=[]
    for entry in kids(read(table),'lib'):
        aliases.append(first(entry,'name')[1])
        assert Path(first(entry,'uri')[1]).exists(),str(table)
        counts['validFamilyTableEntries']+=1
    assert len(aliases)==len(set(aliases)),str(table)
result=dict(counts,parts=len(current),priceTiersCorrect=len(prices),shiftedUnits=shifted,
            shiftedComponents=len({r['part'] for r in shifted}),offGridUnits=offgrid,
            offGridComponents=len({r['part'] for r in offgrid}),families=family)
report.write_text(json.dumps(result,indent=2),encoding='utf8')
print(json.dumps(result,indent=2))
