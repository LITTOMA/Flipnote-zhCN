"""Verify the final CIA banner with an independent Python CBMD/CGFX decoder."""
from pathlib import Path
import argparse,base64,hashlib,json,subprocess,sys,tempfile,zlib
from PIL import Image
sys.dont_write_bytecode=True
from banner_codec import regions,decode,u

ROOT=Path(__file__).resolve().parents[1]
CLI=['dotnet',str(ROOT/'src/Flipnote.Build/bin/Release/net10.0/Flipnote.Build.dll')]
ASSETS=ROOT/'overlays/zh-CN/images'
def digest(b):return hashlib.sha256(b).hexdigest().upper()
def main():
    parser=argparse.ArgumentParser();parser.add_argument('--input');parser.add_argument('--output',default='artifacts/Flipnote-v7.2.0_zh-CN.cia');args=parser.parse_args()
    output=Path(args.output).resolve();recipe=json.loads((ASSETS/'banner.json').read_text(encoding='utf-8'))
    edits={(e['slot'],e['texture']):e for e in recipe['textures']}
    (ROOT/'.work').mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='banner-verify-',dir=ROOT/'.work') as tmp:
        data=[]
        for label,input_cia in [('original',args.input),('localized',output)]:
            target=Path(tmp)/(label+'.bnr');cmd=[*CLI,'read','content/0000/exefs/banner','--output',str(target)]
            if input_cia:cmd+=['--input',str(Path(input_cia).resolve())]
            subprocess.run(cmd,cwd=ROOT,check=True,capture_output=True);data.append(target.read_bytes())
    old,new=data;a,b=regions(old),regions(new)
    assert a.keys()==b.keys()
    assert old[u(old,0x84):]==new[u(new,0x84):],'Banner sound changed'
    # Only header offsets can change when the two LZ11 streams are repacked.
    header=bytearray(new[:0x88]);header[8:64]=old[8:64];header[0x84:0x88]=old[0x84:0x88]
    assert header==old[:0x88]
    records=[];pixels=changed=0
    for slot in a:
        before,ts=a[slot];after,newts=b[slot];assert ts==newts and len(before)==len(after)<=0x80000
        allowed_bytes=set()
        for t in ts:
            initial=decode(before,t);actual=decode(after,t);count=0;e=edits.get((slot,t['name']))
            if e:
                expected=Image.open(ASSETS/e['image']).convert('RGBA')
                assert actual.tobytes()==expected.tobytes(),(slot,t['name'],'decoded PNG differs')
                mask=zlib.decompress(base64.b64decode(e['allowedMask']));assert len(mask)==t['width']*t['height']//8
                assert digest(before[t['data']:t['data']+t['length']])==e['baseSha256']
                for index,(p,q) in enumerate(zip(initial.getdata(),actual.getdata())):
                    if p!=q:assert mask[index//8]>>(index%8)&1;count+=1
                allowed_bytes.update(range(t['data'],t['data']+t['length']))
            else:assert actual.tobytes()==initial.tobytes()
            pixels+=t['width']*t['height'];changed+=count
            records.append(dict(slot=slot,texture=t['name'],width=t['width'],height=t['height'],changedPixels=count,pixelSha256=digest(actual.tobytes())))
        assert all(x==y or i in allowed_bytes for i,(x,y) in enumerate(zip(before,after))),'CGFX model/animation metadata changed'
    report=dict(verified=True,ciaSha256=digest(output.read_bytes()),originalBannerSha256=digest(old),localizedBannerSha256=digest(new),bannerBytes=len(new),regionSlots=list(a),texturesVerified=len(records),pixelsVerified=pixels,changedPixels=changed,cgfxBytes=[len(a[s][0]) for s in a],modelAnimationMaterialsPreserved=True,soundPreserved=True,soundBytes=len(old)-u(old,0x84),protectedPixelsPreserved=True,runtimeTested=False,textures=records)
    path=ROOT/'artifacts/banner-verification.json';path.write_text(json.dumps(report,indent=2)+'\n')
    print(json.dumps({k:v for k,v in report.items() if k!='textures'}))
if __name__=='__main__':main()
