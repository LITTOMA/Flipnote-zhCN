"""Author fixed KWZ bitmap masks from the original CIA and reviewed text layout.

Requires Python 3, Pillow, and the built Flipnote.Build CLI. Only one decoded frame is
on disk at once; the build pipeline itself consumes the resulting JSON masks and
does not need Python, a font renderer, or this script.
"""
from pathlib import Path
import argparse
import base64,hashlib,json,subprocess,zlib,shutil,tempfile,sys
from PIL import Image,ImageDraw,ImageFont

HERE=Path(__file__).resolve().parent
ROOT=HERE.parents[2]
CLI=['dotnet',str(ROOT/'src/Flipnote.Build/bin/Release/net10.0/Flipnote.Build.dll')]
parser=argparse.ArgumentParser()
parser.add_argument('indices',nargs='*',type=int)
parser.add_argument('--input',type=Path)
args=parser.parse_args()
input_args=['--input',str(args.input.resolve())] if args.input else []
layout=json.loads((HERE/'translation-layout.json').read_text('utf-8'))
FONT=HERE/layout['font']
FONT_HASH=layout['fontSha256']
NATIVE=layout['nativePixelSize']
assert hashlib.sha256(FONT.read_bytes()).hexdigest().upper()==FONT_HASH
font=ImageFont.truetype(str(FONT),NATIVE)
assert set(font.getmask('汉化动画',mode='1'))<={0,255}
inventory=json.loads(subprocess.run([*CLI,'inventory','--filter','.kwz',*input_args],cwd=ROOT,capture_output=True,check=True).stdout)

def digest(data):return hashlib.sha256(data).hexdigest().upper()
def packed(mask):
 out=bytearray((len(mask)+7)//8)
 for i,v in enumerate(mask):
  if v:out[i//8]|=1<<(i%8)
 return base64.b64encode(zlib.compress(out,9)).decode()
def rectangle(data,e):
 x,y,w,h=e['rect'];layer=e['layer']
 return bytes(v for yy in range(y,y+h) for v in data[layer*76800+yy*320+x:layer*76800+yy*320+x+w])
def drawing(e):
 x,y,w,h=e['rect'];scale=e.get('scale',1);lines=e['translation'].split('\n')
 if not e.get('draw',True):return [False]*(w*h)
 if len(lines)!=len(e['lineTops']):raise ValueError(('Line count differs from original layout',e['sourceText']))
 im=Image.new('1',(w,h))
 for n,line in enumerate(lines):
  mask=font.getmask(line,mode='1');glyph=Image.frombytes('L',mask.size,bytes(mask));glyph=glyph.crop(glyph.getbbox())
  assert set(glyph.getdata())<={0,255},('Non-pixel font render',line)
  if scale!=1:glyph=glyph.resize((glyph.width*scale,glyph.height*scale),Image.Resampling.NEAREST)
  anchor=e.get('lineAnchors',[e['anchorX']]*len(lines))[n];alignment=e['alignment']
  left=round(anchor if alignment=='left' else anchor+1-glyph.width if alignment=='right' else anchor-(glyph.width-1)/2)
  x0=left-x;y0=e['lineTops'][n]-y
  if x0<0 or y0<0 or x0+glyph.width>w or y0+glyph.height>h:raise ValueError(('Text does not fit at original anchor',e['sourceText'],line,(left,e['lineTops'][n],glyph.width,glyph.height),e['rect']))
  im.paste(glyph,(x0,y0))
 return [bool(v) for v in im.getdata()]

scratch=Path(tempfile.mkdtemp(prefix='animation-author-',dir=ROOT/'.work'))
try:
 for animation in layout['animations']:
  if args.indices and animation['index'] not in args.indices:continue
  resource=inventory['resources'][animation['index']-1];edits=[];sources={}
  def read(frame):
   prefix=scratch/'frame'
   subprocess.run([*CLI,'animation-frame',resource['path'],str(frame),'--output',str(prefix),*input_args],cwd=ROOT,capture_output=True,check=True)
   return (scratch/'frame.pixels').read_bytes()
  for spec in animation['texts']:
   data=read(spec['referenceFrame']);old=rectangle(data,spec);x,y,w,h=spec['rect'];erase=[bool(v) and any(bx<=x+i%w<bx+bw and by<=y+i//w<by+bh for bx,by,bw,bh in spec['eraseBounds']) for i,v in enumerate(old)]
   if not any(erase):raise ValueError(('Empty reference text',animation['index'],spec))
   paint=drawing(spec);sources[id(spec)]=(old,erase,paint)
  frames=sorted(set(f for e in animation['texts'] for f in range(e['firstFrame'],e['lastFrame']+1)))
  for frame in frames:
   data=read(frame)
   for spec in animation['texts']:
    if not(spec['firstFrame']<=frame<=spec['lastFrame']):continue
    old,erase,paint=sources[id(spec)];original=rectangle(data,spec)
    matching=sum(original[i]==old[i] for i,v in enumerate(erase) if v)
    if matching==0:continue # A deliberately blank caption frame stays blank.
    if matching!=sum(erase):raise ValueError(('Text differs from reviewed reference',animation['index'],frame,spec['sourceText'],matching,sum(erase)))
    for i,v in enumerate(paint):
     if v and original[i] and not erase[i]:raise ValueError(('Chinese would overlap artwork',animation['index'],frame,spec['sourceText'],i))
    x,y,w,h=spec['rect'];ink=spec['ink'];empty=[False]*len(paint)
    edit={'firstFrame':frame,'lastFrame':frame,'layer':spec['layer'],'x':x,'y':y,'width':w,'height':h,'sourceText':spec['sourceText'],'translation':spec['translation'],'originalSha256':digest(original),'erase':packed(erase),'ink1':packed(paint if ink==1 else empty),'ink2':packed(paint if ink==2 else empty)}
    previous=next((e for e in reversed(edits) if e['sourceText']==edit['sourceText'] and e['x']==x and e['y']==y),None)
    if previous and previous['lastFrame']==frame-1 and all(previous[k]==v for k,v in edit.items() if k not in ('firstFrame','lastFrame')):previous['lastFrame']=frame
    else:edits.append(edit)
  recipe={'schemaVersion':1,'baseSha256':resource['sha256'],'reviewNote':'Manually reviewed original indexed layers. Only explicitly identified Japanese text strokes are erased; all other animation pixels, frame metadata, header and audio are preserved. The thumbnail is regenerated separately using libkwz.UpdateThumbnail. Original left/right/center anchors and line positions retained. Raster font: 缝合像素体 / Fusion Pixel; native 12 px, 24 px by exact 2x nearest scaling; no antialiasing; SHA256 '+FONT_HASH+'.','edits':edits,'fontSha256':FONT_HASH,'fontFamily':'Fusion Pixel 12px proportional zh_hans','nativePixelSize':NATIVE}
  (HERE/f"{animation['index']:02}-bitmap.json").write_text(json.dumps(recipe,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
  print(f"{animation['index']:02}: {len(edits)} masks, {len(frames)} inspected frames",flush=True)
finally:
 assert scratch.resolve().parent==(ROOT/'.work').resolve()
 shutil.rmtree(scratch)
