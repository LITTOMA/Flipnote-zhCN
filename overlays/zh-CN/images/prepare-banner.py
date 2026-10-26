"""Author reviewed banner masks from the original CIA and existing Chinese lettering.

Requires Pillow. Normal builds use the fixed PNG/mask recipe only.
"""
from pathlib import Path
import argparse,base64,hashlib,json,subprocess,sys,tempfile,zlib
from PIL import Image,ImageOps,ImageFilter

HERE=Path(__file__).resolve().parent
ROOT=HERE.parents[2]
sys.path.insert(0,str(ROOT/'scripts'))
sys.dont_write_bytecode=True
from banner_codec import regions,decode
CLI=['dotnet',str(ROOT/'src/Flipnote.Build/bin/Release/net10.0/Flipnote.Build.dll')]
TARGET='content/0000/exefs/banner'

def digest(b):return hashlib.sha256(b).hexdigest().upper()
def lettering():
    im=ImageOps.invert(Image.open(HERE/'title-lettering-source.png').convert('L'))
    im=im.point(lambda a:0 if a<24 else min(255,round((a-24)*255/223)))
    return im.crop(im.getbbox())
def outlined(mask,size):
    mask=mask.copy();mask.thumbnail(size,Image.Resampling.LANCZOS)
    alpha=Image.new('L',(mask.width+8,mask.height+8));alpha.paste(mask,(4,4))
    border=alpha.filter(ImageFilter.MaxFilter(7))
    rgba=Image.new('RGBA',alpha.size,'white');rgba.putalpha(border)
    ink=Image.new('RGBA',alpha.size,'black');ink.putalpha(alpha);rgba.alpha_composite(ink)
    return rgba
def text_mask(im,slot):
    alpha=im.getchannel('A');seen=set();mask=Image.new('L',im.size)
    for y in range(im.height):
        for x in range(im.width):
            if (x,y) in seen or not alpha.getpixel((x,y)):continue
            todo=[(x,y)];seen.add((x,y));points=[]
            while todo:
                xx,yy=todo.pop();points.append((xx,yy))
                for pt in [(xx-1,yy),(xx+1,yy),(xx,yy-1),(xx,yy+1)]:
                    if 0<=pt[0]<im.width and 0<=pt[1]<im.height and pt not in seen and alpha.getpixel(pt):seen.add(pt);todo.append(pt)
            left=min(pt[0] for pt in points);top=min(pt[1] for pt in points)
            selected=(slot==0 and left==28 and top==19) or (slot==9 and top<100 and left<289 and len(points)>100)
            if selected:
                for pt in points:mask.putpixel(pt,255)
    # The small TM touches the title outline; retain its reviewed original pixels.
    box=(245,111,261,123) if slot==0 else (285,84,300,95)
    for y in range(box[1],box[3]):
        for x in range(box[0],box[2]):
            # Preserve dark trademark strokes, not adjacent white title outline.
            if im.getpixel((x,y))[0]==0 and im.getpixel((x,y))[3]:mask.putpixel((x,y),0)
    return mask
def main():
    parser=argparse.ArgumentParser();parser.add_argument('--input');args=parser.parse_args()
    (ROOT/'.work').mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='banner-author-',dir=ROOT/'.work') as tmp:
        out=Path(tmp)/'original.bnr';cmd=[*CLI,'read',TARGET,'--output',str(out)]
        if args.input:cmd+=['--input',str(Path(args.input).resolve())]
        subprocess.run(cmd,cwd=ROOT,check=True,capture_output=True);raw=out.read_bytes()
    assert digest(raw)=='F8D63D5CB021F9027E60E3137943D29DF7BC699FF6D2AB1FE8ABA6C4CCF4C00B'
    source=regions(raw);edits=[];review=ROOT/'artifacts/banner-review';review.mkdir(parents=True,exist_ok=True)
    panels=[];ink=lettering()
    for slot in [0,9]:
        data,ts=source[slot];originals=[decode(data,t) for t in ts[:3]]
        # Authoring coordinates established by matching original overlap texels.
        # Model TitleMat3 is at x=63.3/y=96.1 texels; JP artwork has its own 1px gutter.
        positions=[(0,0),(256,0),(63 if slot==0 else 64,96)]
        atlas=Image.new('RGBA',(384,224))
        for im,xy in zip(originals,positions):atlas.paste(im,xy)
        erase=text_mask(atlas,slot);added=Image.new('RGBA',atlas.size)
        if slot==0:
            first=ink.crop((0,0,786,ink.height));first=first.crop(first.getbbox())
            second=ink.crop((816,0,ink.width,ink.height));second=second.crop(second.getbbox())
            a=outlined(first,(220,46));b=outlined(second,(220,46))
            added.alpha_composite(a,(145-a.width//2,18));added.alpha_composite(b,(159-b.width//2,69))
        else:
            a=outlined(ink,(277,59));added.alpha_composite(a,(2,27))
        modified=[]
        for i,(t,old,xy) in enumerate(zip(ts,originals,positions)):
            rect=(*xy,xy[0]+old.width,xy[1]+old.height)
            er=erase.crop(rect);add=added.crop(rect);im=old.copy()
            im.paste((0,0,0,0),(0,0),er);im.alpha_composite(add)
            im=im.point(lambda v:round(v/17)*17)
            mask=bytearray(old.width*old.height//8);changes=0
            for y in range(old.height):
                for x in range(old.width):
                    if old.getpixel((x,y))!=im.getpixel((x,y)):
                        assert er.getpixel((x,y)) or add.getpixel((x,y))[3]
                        if add.getpixel((x,y))[3] and old.getpixel((x,y))[3]:assert er.getpixel((x,y)),(slot,i,x,y,'new lettering overlaps protected artwork')
                        index=y*old.width+x;mask[index//8]|=1<<(index%8);changes+=1
            modified.append(im)
            if not changes:continue
            name=f'Banner-{slot}-{t["name"]}.png';im.save(HERE/name,optimize=True)
            edits.append(dict(slot=slot,texture=t['name'],baseSha256=digest(data[t['data']:t['data']+t['length']]),image=name,allowedMask=base64.b64encode(zlib.compress(mask,9)).decode()))
        after=Image.new('RGBA',atlas.size)
        for im,xy in zip(modified,positions):after.paste(im,xy)
        for label,im in [('original',atlas),('localized',after)]:
            bg=Image.new('RGBA',im.size,'#ff5500');bg.alpha_composite(im);panels.append(bg.convert('RGB'))
            bg.convert('RGB').save(review/f'{slot}-{label}.png')
    (HERE/'banner.json').write_text(json.dumps(dict(schemaVersion=1,textures=edits),indent=2)+'\n',encoding='utf-8')
    proof=Image.new('RGB',(768,448))
    for i,im in enumerate(panels):proof.paste(im,((i%2)*384,(i//2)*224))
    proof.resize((1536,896),Image.Resampling.NEAREST).save(review/'title-comparison.png')
    print(json.dumps(dict(textureEdits=len(edits),review=str(review/'title-comparison.png'))))
if __name__=='__main__':main()
