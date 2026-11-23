import sys,struct
path=sys.argv[1]; targets=[int(t,16) for t in sys.argv[2:]]
d=open(path,'rb').read()
# __TEXT,__text bounds from load commands
ncmds,=struct.unpack_from('<I',d,16); o=32; text=None
for _ in range(ncmds):
    cmd,cs=struct.unpack_from('<II',d,o)
    if cmd==0x19:
        segname=d[o+8:o+24].rstrip(b'\0'); nsects,=struct.unpack_from('<I',d,o+64)
        for i in range(nsects):
            so=o+72+i*80; sect=d[so:so+16].rstrip(b'\0'); addr,size,off=struct.unpack_from('<QQI',d,so+32)
            if sect==b'__text': text=(addr,size,off)
    o+=cs
addr,size,off=text; base=addr-off
for t in targets:
    tv=t+base; hits=[]
    for i in range(off,off+size-8,4):
        ins,=struct.unpack_from('<I',d,i)
        if ins&0x9F000000!=0x90000000: continue
        rd=ins&31; immlo=(ins>>29)&3; immhi=(ins>>5)&0x7FFFF; imm=(immhi<<2)|immlo
        if imm&0x100000: imm-=0x200000
        pc=i+base; page=(pc&~0xFFF)+(imm<<12)
        if page!=(tv&~0xFFF): continue
        for j in range(i+4,min(i+40,off+size),4):
            a,=struct.unpack_from('<I',d,j)
            if a&0xFF800000==0x91000000 and (a>>5)&31==rd and page+((a>>10)&0xFFF)==tv:
                hits.append((hex(i),hex(j)))
    print(hex(t),'xrefs',hits[:10])
