"""Read KiCad's tessellated STEP placements. No external Python dependencies.
Usage: inspect_glb_placement.py file.glb ...
Output is diagnostic, not proof of package/pin identity or datasheet compliance.
"""
import json
import math
from pathlib import Path
import struct
import sys
import argparse


def transform(p, node):
    p = [a*b for a,b in zip(p,node.get('scale',[1,1,1]))]
    x,y,z,w = node.get('rotation',[0,0,0,1])
    # Quaternion vector rotation, glTF coordinates (metres; Y is height).
    uv = [y*p[2]-z*p[1], z*p[0]-x*p[2], x*p[1]-y*p[0]]
    uuv = [y*uv[2]-z*uv[1],z*uv[0]-x*uv[2],x*uv[1]-y*uv[0]]
    p = [p[i]+2*(w*uv[i]+uuv[i]) for i in range(3)]
    if 'matrix' in node:
        m=node['matrix']; p=[sum(m[j*4+i]*p[j] for j in range(3))+m[12+i] for i in range(3)]
    return [p[i]+node.get('translation',[0,0,0])[i] for i in range(3)]


parser=argparse.ArgumentParser()
parser.add_argument('--report')
parser.add_argument('--manifest')
parser.add_argument('files',nargs='+')
args=parser.parse_args()
groups=json.loads(Path(args.manifest).read_text(encoding='utf8'))['footprintGroups'] if args.manifest else []
all_results=[]
for filename in args.files:
    b=Path(filename).read_bytes()
    size=struct.unpack_from('<I',b,12)[0]
    doc=json.loads(b[20:20+size]); binary=b[28+size:]
    def vertices(index, parents):
        node=doc['nodes'][index]; chain=[node]+parents
        if 'mesh' in node:
            for prim in doc['meshes'][node['mesh']]['primitives']:
                acc=doc['accessors'][prim['attributes']['POSITION']]
                assert acc['componentType']==5126 and acc['type']=='VEC3'
                view=doc['bufferViews'][acc['bufferView']]
                offset=view.get('byteOffset',0)+acc.get('byteOffset',0)
                for i in range(acc['count']):
                    p=struct.unpack_from('<fff',binary,offset+i*view.get('byteStride',12))
                    for n in chain: p=transform(p,n)
                    yield [v*1000 for v in p]
        for child in node.get('children',[]):
            yield from vertices(child,chain)
    result=[]
    for i,node in enumerate(doc['nodes']):
        name=node.get('name','')
        if not name.startswith('C') or not name[1:].isdigit(): continue
        pts=list(vertices(i,[]))
        lo=[min(p[j] for p in pts) for j in range(3)]
        hi=[max(p[j] for p in pts) for j in range(3)]
        result.append(dict(part=name,centerXY=[round((lo[j]+hi[j])/2,4) for j in [0,2]],
            sizeXYZ=[round(hi[j]-lo[j],4) for j in [0,2,1]],
            zMin=round(lo[1],4),zMax=round(hi[1],4)))
    for row in result:
        if groups:
            index=next(i for i,g in enumerate(groups) if g[0]==row['part'])
            row['partsSharingGeometry']=groups[index]
            expected=[25+(index%20)%5*30,25+(index%20)//5*30]
            row['xyOffsetFromFootprintOrigin']=[round(row['centerXY'][j]-expected[j],4) for j in range(2)]
            row['reviewXY']=math.dist(row['centerXY'],expected)>.5
            row['reviewZ']=abs(row['zMin']-1.595)>.05
        all_results.append(row)
if args.report:
    target=Path(args.report)
    if target.exists(): raise FileExistsError(target)
    target.write_text(json.dumps({'models':all_results,'note':'XY >0.5 mm from footprint origin and Z >0.05 mm from nominal top are review flags, not automatic alignment fixes. Model identity and lead-to-pad orientation still need visual/datasheet review.'},indent=2),encoding='utf8')
print(json.dumps(all_results,indent=2))
