"""Bounded full-layout parser for the pinned DRS FRM2 serialization contract.

Separates storage identity from acoustic meaning. Fixed research blocks remain
explicitly unnamed bytes. No native calls, allocation from unchecked counts,
automatic synthesis or interpretation as an approved final voicebank frame.
"""
from __future__ import annotations

from dataclasses import dataclass
import struct

from probe_frm2 import Cursor, ProbeError, vqm_feature_payload_size


@dataclass(frozen=True)
class Field:
    name: str
    mask: int
    offset: int
    payload: bytes
    encoding: str


@dataclass(frozen=True)
class Frame:
    kind: int
    time: float
    mask: int
    fields: tuple[Field, ...]


# Bits 3/15/28 are preserved flags with no payload in this writer. Bits29/30
# select packed spectra; bit31 selects pitch units. Other unsupported bits fail.
KNOWN_MASK = sum(1 << bit for bit in range(38) if bit != 36)


def parse(data: bytes, *, sms2_context: bool = True) -> Frame:
    cur=Cursor(data)
    magic,size=cur.unpack('<4sI','DRS frame header')
    if magic!=b'FRM2' or size!=len(data):raise ProbeError('invalid DRS frame magic/length')
    kind,time,mask=cur.unpack('<IdQ','DRS frame prefix')
    mask=int(mask)
    if mask & ~KNOWN_MASK:raise ProbeError(f'unsupported DRS frame bits: {mask & ~KNOWN_MASK:#x}')
    fields=[]

    def take(name,group,length,encoding):
        start=cur.position;payload=cur.take(length,name)
        fields.append(Field(name,group,start,payload,encoding))
        return payload

    def count(name,group):
        raw=take(name,group,4,'u32')
        value=struct.unpack('<I',raw)[0]
        if value>len(data)//2:raise ProbeError(f'excessive {name}')
        return value

    def envelope(bit):
        start=cur.position
        magic,size=cur.unpack('<4sI',f'bit{bit} ENV header')
        if magic!=b'ENV ' or size<28:raise ProbeError(f'invalid bit{bit} ENV')
        flags,bytes_count=cur.unpack('<II',f'bit{bit} ENV flags/bytes')
        if size!=28+bytes_count:raise ProbeError(f'bit{bit} ENV length mismatch')
        cur.take(12+int(bytes_count),f'bit{bit} ENV bounds/payload')
        fields.append(Field(f'bit{bit}.env',1<<bit,start,data[start:cur.position],'ENV chunk'))

    for group,bits,packed in ((7,(1,0,2),30),(0x30,(4,5),29)):
        if mask & group:
            n=count('harmonic_count' if group==7 else 'spectrum_count',group)
            for bit in bits:
                if mask & (1<<bit):take(f'bit{bit}.array',1<<bit,n*(1 if mask&(1<<packed) else 4),
                    'packed byte' if mask&(1<<packed) else 'f32[]')
    if mask & 64:
        n=count('bit6.count',64);take('bit6.pcm',64,n*2,'i16[]')
    for bit in (9,20,10,11,12):
        if mask & (1<<bit):take(f'bit{bit}.scalar',1<<bit,4,
            'f32' if bit in (9,10,11) else '4-byte scalar (meaning/type not assigned)')
    if mask & 8192:
        flags_raw=take('bit13.flags',8192,4,'u32')
        flags=struct.unpack('<I',flags_raw)[0]
        if flags & ~0x1fffff:raise ProbeError('unsupported inner feature bits')
        take('bit13.features',8192,vqm_feature_payload_size(flags),'sparse feature payload')
        take('bit13.trailer',8192,1,'u8')
    if mask & (1<<35):take('bit35.block',1<<35,52,'unnamed fixed bytes')
    if mask & (1<<37):envelope(37)
    if mask & (1<<27):
        for index,size in enumerate((372,76,76)):take(f'bit27.block{index}',1<<27,size,'unnamed fixed bytes')
    if mask & (1<<14):take('bit14.block',1<<14,35,'unnamed fixed bytes (memory allocation 36)')
    if mask & (1<<32):
        n=count('bit32.count',1<<32);take('bit32.array',1<<32,n*4,'4-byte slots (type not assigned)')
    if mask & (1<<33):
        n=count('bit33.count',1<<33)
        for name,size in (('array0',n*4),('array1',n*4),('block',96),('scalar0',4),('scalar1',4)):
            take('bit33.'+name,1<<33,size,'4-byte slots (type not assigned)' if name.startswith('array') else 'unnamed bytes')
    if mask & (1<<34):
        n=count('bit34.count',1<<34);take('bit34.array',1<<34,n*4,'4-byte slots (type not assigned)')
    if mask & (1<<16):
        n=count('bit16.count',1<<16);take('bit16.array',1<<16,n*4,'4-byte slots (type not assigned)')
    if mask & (1<<18):take('bit18.window_id',1<<18,1,'u8')
    if mask & (1<<19):take('bit19.sample_length',1<<19,4,'u32')
    for primary,secondary in ((21,22),(24,25)):
        if mask & (1<<primary):
            n=count(f'bit{primary}.count',1<<primary)
            take(f'bit{primary}.resonances',1<<primary,n*24,'f64 triples')
        if mask & (1<<secondary):
            take(f'bit{secondary}.ABC',1<<secondary,12 if sms2_context else 8,
                 '3f32' if sms2_context else '2f32 (B absent)')
            n=count(f'bit{secondary}.count',1<<secondary)
            take(f'bit{secondary}.resonances',1<<secondary,n*24,'f64 triples')
    for bit in (7,8,17,23,26):
        if mask & (1<<bit):envelope(bit)
    if cur.position!=len(data):raise ProbeError(f'{len(data)-cur.position} unexplained DRS tail bytes')
    return Frame(int(kind),float(time),mask,tuple(fields))


def serialize(frame: Frame, selection: int | None = None) -> bytes:
    """Preserve parsed storage bytes; optionally apply uncompressed writer selection.

    This is a field-boundary/selection rewriter, not an acoustic encoder or a
    reconstruction of values omitted by the source stream.
    """
    mask=frame.mask
    if selection is not None:
        if mask & ((1<<29)|(1<<30)):
            raise ProbeError('selection of packed spectra requires native repacking')
        mask=(mask & selection)|(mask & (0xf<<28))
        if not mask & (1<<9):mask &= ~(1<<30)
    payload=struct.pack('<IdQ',frame.kind,frame.time,mask)
    payload+=b''.join(field.payload for field in frame.fields if field.mask & mask)
    return struct.pack('<4sI',b'FRM2',8+len(payload))+payload
