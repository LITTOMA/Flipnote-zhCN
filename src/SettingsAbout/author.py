"""Author Settings resources from verified originals (development only)."""
from pathlib import Path
import struct, json, hashlib, argparse, tempfile, subprocess, shutil, atexit
R=Path(__file__).resolve().parents[2]
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--input', required=True, type=Path)
parser.add_argument('--clang', default='clang')
parser.add_argument('--linker')
args=parser.parse_args()
if args.linker is None:
 clang=Path(shutil.which(args.clang) or args.clang)
 args.linker=shutil.which("ld.lld") or str(clang.with_name("ld.lld"+clang.suffix))
(R/'.work').mkdir(exist_ok=True)
temporary=tempfile.TemporaryDirectory(prefix='settings-author-',dir=R/'.work')
atexit.register(temporary.cleanup)
P=Path(temporary.name)
cli=['dotnet',str(R/'src/Flipnote.Build/bin/Release/net10.0/Flipnote.Build.dll')]
def read(target,file):
 subprocess.run(cli+['read',target,'--project',str(R/'project.json'),'--input',str(args.input.resolve()),'--output',str(P/file)],check=True,stdout=subprocess.DEVNULL)
for file in ['Config.bclyt']+['Config_'+x+'.bclan' for x in ['Decide','Disable','KeyPush','Push','Scroll','Wait']]:
 read('content/0000/romfs/LayoutArchive.arc!/Config.blz!/'+('blyt/' if file.endswith('bclyt') else 'anim/')+file,file)
read('content/0000/exheader','exheader.bin')
read('content/0000/exefs/.code','code.bin')
texts=json.loads((R/'overlays/zh-CN/settings/texts.json').read_text('utf-8-sig'))
(P/'texts.c').write_text('\n'.join('const unsigned short '+k+'[] = {'+','.join(str(ord(c)) for c in v)+',0};' for k,v in texts.items()),encoding='ascii')
flags=['--target=arm-none-eabi','-mcpu=mpcore','-mfloat-abi=soft','-marm','-Os','-ffreestanding','-fno-builtin','-fno-unwind-tables','-fno-asynchronous-unwind-tables','-fno-stack-protector']
for file in ['about.c','hooks.S','texts.c']:
 source=P/file if file=='texts.c' else R/'src/SettingsAbout'/file
 subprocess.run([args.clang]+flags+['-c',str(source),'-o',str(P/(file+'.o'))],check=True)
subprocess.run([args.linker,'-T',str(R/'src/SettingsAbout/link.ld')]+[str(P/(f+'.o')) for f in ['hooks.S','about.c','texts.c']]+['-o',str(P/'about.elf')],check=True)

def u16(b,o):return struct.unpack_from('<H',b,o)[0]
def u32(b,o):return struct.unpack_from('<I',b,o)[0]
def p16(b,o,v):struct.pack_into('<H',b,o,v)
def p32(b,o,v):struct.pack_into('<I',b,o,v)
def pf(b,o,v):struct.pack_into('<f',b,o,v)
def name(b,o=0):return bytes(b[o:o+20]).split(b'\0')[0].decode('ascii')
def putname(b,o,s):
 assert len(s)<20
 b[o:o+20]=s.encode().ljust(20,b'\0')
def blocks(b):
 o=20;out=[]
 while o<len(b):
  z=u32(b,o+4);assert z>=8 and o+z<=len(b)
  out.append(bytearray(b[o:o+z]));o+=z
 assert o==len(b)
 return out
def rebuild(b,parts):
 h=bytearray(b[:20]);p32(h,12,20+sum(map(len,parts)));p16(h,16,len(parts));return h+b''.join(parts)
N={'AllClear':'About','TexAllClearWin':'TexAboutWin','TexAllClearWinC ':'TexAboutWinC ','TexAllClearWinLT':'TexAboutWinLT','TxtAllClear':'TxtAbout','TexShaL_12':'TexAboutShaL','TexShaR_13':'TexAboutShaR'}
def indexed(b,countoff,tableoff):
 n=u16(b,countoff);offs=[u32(b,tableoff+4*i) for i in range(n)]+[len(b)]
 return [bytearray(b[offs[i]:offs[i+1]]) for i in range(n)]
def table(b,items,countoff,tableoff):
 out=bytearray(b[:tableoff]);p16(out,countoff,len(items));at=tableoff+len(items)*4
 for x in items:out+=struct.pack('<I',at);at+=len(x)
 out+=b''.join(items);p32(out,4,len(out));return out

def layout(b):
 parts=blocks(b);mat=next(x for x in parts if x[:4]==b'mat1');items=indexed(mat,8,12);remap={}
 for i,x in enumerate(list(items)):
  if name(x) in N:
   y=bytearray(x);putname(y,0,N[name(x)]);remap[i]=len(items);items.append(y)
 parts[parts.index(mat)]=table(mat,items,8,12)
 start=next(i for i,x in enumerate(parts) if x[:4]==b'bnd1' and name(x,12)=='AllClear')
 depth=0;end=start+1
 assert parts[end][:4]==b'pas1'
 while end<len(parts):
  t=parts[end][:4]
  if t==b'pas1':depth+=1
  if t==b'pae1':
   depth-=1
   if depth==0:break
  end+=1
 cloned=[bytearray(x) for x in parts[start:end+1]]
 for x in cloned:
  if x[:4] in (b'bnd1',b'pan1',b'pic1',b'wnd1',b'txt1'):
   old=name(x,12);putname(x,12,N[old])
   if x[:4]==b'pic1':p16(x,92,remap[u16(x,92)])
   if x[:4]==b'txt1':
    p16(x,80,remap[u16(x,80)])
    # Stock title replacement is also set through the native text interface.
    at=u32(x,88);x[at:]=('汉化信息\0'.encode('utf-16-le')).ljust(len(x)-at,b'\0')
   if x[:4]==b'wnd1':
    at=u32(x,96);p16(x,at+16,remap[u16(x,at+16)])
    ft=u32(x,100)
    for i in range(x[92]):
     f=u32(x,ft+i*4);p16(x,f,remap[u16(x,f)])
 pf(cloned[0],40,-288);pf(parts[start],40,-368)
 parts[start:start]=cloned
 group=next(x for x in parts if x[:4]==b'grp1' and name(x,8)=='AllClear')
 new=bytearray(group);new[8:24]=b'About'.ljust(16,b'\0');new[28:44]=b'About'.ljust(16,b'\0')
 parts.insert(len(parts)-1,new)
 return rebuild(b,parts)

def animation(b,scroll=False):
 parts=blocks(b);pat,pai=parts
 off=u32(pat,16);n=u16(pat,10);groups=[name(pat,off+20*i) for i in range(n)]
 if 'AllClear' in groups:
  groups.append('About');pat=pat[:off]+b''.join(x.encode().ljust(20,b'\0') for x in groups);p16(pat,10,len(groups));p32(pat,4,len(pat))
 items=indexed(pai,14,20)
 for x in list(items):
  if name(x) in N:
   y=bytearray(x);putname(y,0,N[name(x)]);items.append(y)
 if scroll:
  assert u16(pai,8)==289;p16(pai,8,351);p16(pat,22,u16(pat,22)+62)
  for x in items:
   if name(x)=='MoveScroll':
    assert len(x)==76;pf(x,64,350);pf(x,68,350)
   if name(x)=='TexScrollBar':
    pf(x,64,350);pf(x,60,-134/350);pf(x,72,-134/350)
 return rebuild(b,[pat,table(pai,items,14,20)])

def sha(b):return hashlib.sha256(b).hexdigest().upper()
def recipe(before,after):
 after=bytes(after)
 # Copy runs from the user's original, including cloned rows. Literal data
 # only describes changed/new bytes; no original complete resource is shipped.
 index={}
 for i in range(len(before)-7):index.setdefault(before[i:i+8],[]).append(i)
 seg=[];lit=bytearray();at=0
 def flush():
  if lit:seg.append({'data':lit.hex().upper()});lit.clear()
 while at<len(after):
  best=0;src=0
  candidates=index.get(after[at:at+8],[])
  for off in candidates[:64]+candidates[-64:]:
   n=8
   while off+n<len(before) and at+n<len(after) and before[off+n]==after[at+n]:n+=1
   if n>best:best=n;src=off
  if best>=8:
   flush();seg.append({'copyOffset':src,'length':best});at+=best
  else:lit.append(after[at]);at+=1
 flush()
 return {'baseSha256':sha(before),'resultSha256':sha(after),'resultLength':len(after),'segments':seg}
def save(path,j):path.parent.mkdir(parents=True,exist_ok=True);path.write_text(json.dumps(j,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
for f in ['Config.bclyt']+['Config_'+x+'.bclan' for x in ['Decide','Disable','KeyPush','Push','Scroll','Wait']]:
 b=(P/f).read_bytes();out=layout(b) if f.endswith('bclyt') else animation(b,'Scroll' in f)
 assert len(blocks(out))==u16(out,16)
 (P/(f+'.new')).write_bytes(out);save(R/'overlays/zh-CN/settings'/(f+'.json'),recipe(b,out));print(f,len(b),'->',len(out))
# Read ELF32 (no host-specific objcopy dependency).
e=(P/'about.elf').read_bytes();sh=u32(e,32);ents=u16(e,46);count=u16(e,48);sections=[struct.unpack_from('<10I',e,sh+i*ents) for i in range(count)]
strings=sections[u16(e,50)];names=e[strings[4]:strings[4]+strings[5]]
symbols={};blob=None
for s in sections:
 nm=names[s[0]:].split(b'\0')[0]
 if nm==b'.text':blob=e[s[4]:s[4]+s[5]];addr=s[3]
 if s[1]==2:
  st=sections[s[6]];st=e[st[4]:st[4]+st[5]]
  for o in range(s[4],s[4]+s[5],s[9]):
   n,v,sz,info,other,ndx=struct.unpack_from('<IIIBBH',e,o);symbols[st[n:].split(b'\0')[0].decode()]=v
assert addr==0x373300 and blob is not None and addr+len(blob)<=0x374000
assert symbols['lastPage']==0x448b1c
b=(P/'code.bin').read_bytes();out=bytearray(b)
p=R/'overlays/zh-CN/patches/font-loader-code.json';j=json.loads(p.read_text())
for x in j['ranges']:
 d=bytes.fromhex(x['after']);out[x['offset']:x['offset']+len(d)]=d
# Reserved settings page tail only; the font loader ends at 0x3732b8.
out[0x273300:0x274000]=b'\0'*(0xd00);out[addr-0x100000:addr-0x100000+len(blob)]=blob
for a,target,link in [(0x186730,symbols['event_hook'],False),(0x186164,symbols['poll_hook'],False),(0x185edc,symbols['about_labels'],True)]:
 delta=target-a-8;assert delta%4==0 and -0x2000000<=delta<0x2000000;p32(out,a-0x100000,(0xeb000000 if link else 0xea000000)|((delta>>2)&0xffffff))
# Bounds, selection sentinel and table are scoped to ConfigMenu only.
patched=[]
for at in range(0x1be364,0x1bea04,4):
 w=u32(b,at-0x100000)
 if (w & 0xfff)==9 and (w & 0xfff00000) in (0xe3500000,0xe3a00000):
  p32(out,at-0x100000,(w&~0xff)|10);patched.append(hex(at))
assert len(patched)==7, 'Unexpected ConfigMenu selection bounds'
for o in range(0xbe000,0xbea24,4):
 if u32(b,o)==0x3d2fb8:p32(out,o,symbols['targets'])
assert u32(b,0xbe1d4)==0x43900000;pf(out,0xbe1d4,350)
def fixed(before,after):
 rs=[];i=0
 while i<len(before):
  if before[i]==after[i]:i+=1;continue
  start=i
  while i<len(before) and before[i]!=after[i]:i+=1
  rs.append({'offset':start,'before':before[start:i].hex().upper(),'after':after[start:i].hex().upper()})
 return {'baseSha256':sha(before),'resultSha256':sha(after),'length':len(before),'ranges':rs}
save(p,fixed(b,out));print('hook',hex(addr),len(blob),'sentinels',patched)
assert not any(b[0x273300:0x274000]), 'Hook region is not original padding'
# Existing header patch declares font text and BSS; extend within those same pages.
p=R/'overlays/zh-CN/patches/font-loader-exheader.json';j=json.loads(p.read_text());ex=bytearray((P/'exheader.bin').read_bytes());out=bytearray(ex)
for x in j['ranges']:out[x['offset']:x['offset']+len(bytes.fromhex(x['after']))]=bytes.fromhex(x['after'])
p32(out,24,addr+len(blob)-0x100000);p32(out,60,u32(ex,60)+24)
save(p,fixed(ex,out));(P/'exheader.new').write_bytes(out)
# Register leaf resource recipes, avoiding parent/child overlay conflicts.
p=R/'overlays/zh-CN/manifest.json';j=json.loads(p.read_text())
for f in ['Config.bclyt']+['Config_'+x+'.bclan' for x in ['Decide','Disable','KeyPush','Push','Scroll','Wait']]:
 target='content/0000/romfs/LayoutArchive.arc!/Config.blz!/'+('blyt/' if f.endswith('bclyt') else 'anim/')+f
 op={'kind':'resource-patch','target':target,'source':'assets:settings/'+f+'.json','baseSha256':sha((P/f).read_bytes())}
 j['operations']=[x for x in j['operations'] if x['target']!=target]+[op]
save(p,j)


save(R/'overlays/zh-CN/settings/source-hashes.json', {name:sha((R/'src/SettingsAbout'/name).read_bytes()) for name in ['about.c','hooks.S','link.ld','author.py']})
