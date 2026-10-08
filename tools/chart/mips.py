"""Minimal R3000 disassembler for tracing chart setup code."""
import struct
R=['zero','at','v0','v1','a0','a1','a2','a3','t0','t1','t2','t3','t4','t5','t6','t7','s0','s1','s2','s3','s4','s5','s6','s7','t8','t9','k0','k1','gp','sp','fp','ra']
I={0x08:'addi',0x09:'addiu',0x0a:'slti',0x0b:'sltiu',0x0c:'andi',0x0d:'ori',0x0e:'xori',0x0f:'lui',0x20:'lb',0x21:'lh',0x23:'lw',0x24:'lbu',0x25:'lhu',0x28:'sb',0x29:'sh',0x2b:'sw',0x04:'beq',0x05:'bne',0x06:'blez',0x07:'bgtz'}
F={0x21:'addu',0x23:'subu',0x24:'and',0x25:'or',0x2a:'slt',0x2b:'sltu',0x00:'sll',0x02:'srl',0x03:'sra',0x08:'jr',0x09:'jalr',0x18:'mult',0x1a:'div',0x10:'mfhi',0x12:'mflo'}
def dis(w,pc):
    op=w>>26; rs=R[(w>>21)&31]; rt=R[(w>>16)&31]; imm=w&0xffff; simm=imm-0x10000 if imm&0x8000 else imm
    if w==0: return 'nop'
    if op==0:
        f=w&63; rd=R[(w>>11)&31]; sa=(w>>6)&31; n=F.get(f,'spec%02x'%f)
        if f in (0,2,3): return f'{n} {rd},{rt},{sa}'
        if f==8: return f'jr {rs}'
        return f'{n} {rd},{rs},{rt}'
    if op in (2,3): return ('j' if op==2 else 'jal')+' %08x'%((pc&0xf0000000)|((w&0x3ffffff)<<2))
    n=I.get(op,'op%02x'%op)
    if op==0x0f: return f'lui {rt},0x{imm:04x}'
    if op>=0x20: return f'{n} {rt},{simm}({rs})'
    if op in (4,5,6,7): return f'{n} {rs},{rt},%08x'%(pc+4+simm*4)
    return f'{n} {rt},{rs},{simm if op in (8,9,0xa,0xb) else hex(imm)}'
def window(data,base,start,end):
    for a in range(start,end,4):
        w=struct.unpack_from('<I',data,a-base)[0]; print('%08x  %08x  %s'%(a,w,dis(w,a)))
