"""Independent read-only CBMD/CGFX texture reader for authoring and verification.

Layout reference: https://docs.mikage.app/CGFX/ and https://3dbrew.org/wiki/CBMD
PNG rows follow the raw texture convention; do not add a vertical flip.
"""
from pathlib import Path
import struct,json
from PIL import Image,ImageOps,ImageDraw

def u(b,p): return struct.unpack_from('<I',b,p)[0]
def rel(b,p): return p+struct.unpack_from('<i',b,p)[0]
def string(b,p): return b[p:b.index(0,p)].decode('utf-8')
def unlz(b):
    assert b[0]==0x11
    n=int.from_bytes(b[1:4],'little'); p=4; out=bytearray()
    if n==0:n=u(b,4);p=8
    while len(out)<n:
        flags=b[p];p+=1
        for bit in range(7,-1,-1):
            if len(out)>=n:break
            if flags>>bit&1:
                a=b[p];p+=1;k=a>>4
                if k==0:
                    c,d=b[p:p+2];p+=2;length=((a&15)<<4|c>>4)+17;dist=((c&15)<<8|d)+1
                elif k==1:
                    c,d,e=b[p:p+3];p+=3;length=((a&15)<<12|c<<4|d>>4)+273;dist=((d&15)<<8|e)+1
                else:length=k+1;dist=((a&15)<<8|b[p])+1;p+=1
                for _ in range(length):out.append(out[-dist])
            else:out.append(b[p]);p+=1
    assert len(out)==n
    return bytes(out),p
def textures(b):
    h=struct.unpack_from('<H',b,6)[0]
    count=u(b,h+16);d=rel(b,h+20)
    result=[]
    for i in range(count):
        e=d+28+i*16;t=rel(b,e+12)
        result.append(dict(name=string(b,rel(b,e+8)),offset=t,width=u(b,t+28),height=u(b,t+24),format=u(b,t+52),levels=u(b,t+40),length=u(b,t+68),data=rel(b,t+72)))
    return result
def decode(b,t):
    w,h,f=t['width'],t['height'],t['format'];a=b[t['data']:t['data']+t['length']]
    im=Image.new('RGBA',(w,h))
    for y in range(h):
        for x in range(w):
            i=((y//8)*(w//8)+x//8)*64+sum(((x>>k&1)<<(2*k))|((y>>k&1)<<(2*k+1)) for k in range(3))
            if f==11:v=(a[i//2]>>((i%2)*4))&15;rgba=(255,255,255,v*17)
            elif f==9:v=a[i];rgba=((v>>4)*17,)*3+((v&15)*17,)
            elif f==10:v=(a[i//2]>>((i%2)*4))&15;rgba=(v*17,)*3+(255,)
            elif f==4:v=int.from_bytes(a[i*2:i*2+2],'little');rgba=tuple(((v>>k)&15)*17 for k in [12,8,4,0])
            elif f==0:rgba=tuple(a[i*4+k] for k in [3,2,1,0])
            elif f==1:rgba=tuple(a[i*3+k] for k in [2,1,0])+(255,)
            else:raise ValueError(f)
            im.putpixel((x,y),rgba)
    return im
def regions(b):
    assert b[:4]==b'CBMD'
    result={}
    for slot in range(14):
        o=u(b,8+slot*4)
        if not o:continue
        c,used=unlz(b[o:]);assert c[:4]==b'CGFX' and u(c,12)==len(c)
        result[slot]=(c,textures(c))
    return result
