"""Writes the run object golden vectors (archive-lsm.md §7, CLAR version 1) and the hand-made fuzz corpus from an
independent reading of the spec. Run from the repository root:

    python -m pip install -r tests/shared-vectors/archive/requirements.txt
    python tests/shared-vectors/archive/generate_run_objects.py
"""
import hashlib
import json
import math
import os
import shutil
import struct

import xxhash

HERE = os.path.dirname(os.path.abspath(__file__))
CORPUS = os.path.join(HERE, '..', '..', '..', 'fuzz', 'corpus', 'archive-run-object')

# ---- primitives ----------------------------------------------------------------------------------------------------

_T = []
for _i in range(256):
    _c = _i
    for _ in range(8):
        _c = (_c >> 1) ^ 0x82F63B78 if _c & 1 else _c >> 1
    _T.append(_c)


def crc32c(data):
    c = 0xFFFFFFFF
    for b in data:
        c = _T[(c ^ b) & 0xFF] ^ (c >> 8)
    return c ^ 0xFFFFFFFF


def u8(v): return struct.pack('<B', v)
def u16(v): return struct.pack('<H', v)
def u32(v): return struct.pack('<I', v)
def u64(v): return struct.pack('<Q', v)


def seal(body, crc=None):
    return body + u32(crc32c(body) if crc is None else crc)


def key_bytes(k): return u16(len(k)) + k
def tag_bytes(t): return u64(t[0]) + u64(t[1]) + u32(t[2])
def partition_bytes(p): return u32(p[1]) + p[0]


KINDS = {'Append': 0, 'Replace': 1, 'Delete': 2}


def row_bytes(r):
    out = key_bytes(r['key']) + tag_bytes(r['tag']) + u8(r.get('kindByte', KINDS[r['kind']]))
    return out + u32(len(r['payload'])) + r['payload']


def hash_count(bits_per_key):
    return max(1, round(bits_per_key * math.log(2)))


def bloom(keys, bits_per_key, k=None):
    nbytes = (len(keys) * bits_per_key + 7) // 8
    m = nbytes * 8
    k = hash_count(bits_per_key) if k is None else k
    bits = bytearray(nbytes)
    for key in keys:
        h = xxhash.xxh3_64_intdigest(key)
        h1, h2 = h & 0xFFFFFFFF, h >> 32
        for i in range(k):
            b = (h1 + i * h2) % m
            bits[b // 8] |= 1 << (b % 8)
    return k, bytes(bits)


# ---- the writer's structure -----------------------------------------------------------------------------------------

def natural_section(partition, rows, block_bytes, bits_per_key, k=None):
    # A block starts empty but for its CRC-32C, and a new block starts before a row which would make a non-empty block
    # longer than the block size.
    blocks, cur, cur_len = [], [], 4
    for r in rows:
        rl = len(row_bytes(r))
        if cur and cur_len + rl > block_bytes:
            blocks.append(cur)
            cur, cur_len = [], 4
        cur.append(r)
        cur_len += rl
    blocks.append(cur)
    entries, previous = [], None
    for b in blocks:
        entries.append([None, 1 if previous is not None and b[0]['key'] == previous['key'] else 0, b[0]['key']])
        previous = b[-1]
    distinct = []
    for r in rows:
        if not distinct or distinct[-1] != r['key']:
            distinct.append(r['key'])
    hashes, bits = bloom(distinct, bits_per_key, k)
    tags = [r['tag'] for r in rows]
    return {
        'partition': partition,
        'blocks': [{'rows': b} for b in blocks],
        'index': {'entries': entries},
        'filter': {'hashCount': hashes, 'bits': bits},
        'entry': {},
        'natural': {'minTag': min(tags), 'maxTag': max(tags), 'rowCount': len(rows)},
    }


def natural(sections, condition, block_bytes, bits_per_key, k=None):
    return {'sections': [natural_section(s['partition'], s['rows'], block_bytes, bits_per_key, k) for s in sections],
            'condition': condition, 'trailer': {}}


# ---- serialization, with overrides ----------------------------------------------------------------------------------

def ser_block(b):
    body = b''.join(row_bytes(r) for r in b['rows']) + b.get('extra', b'')
    body = b.get('transform', lambda x: x)(body)
    return seal(body, b.get('crc'))


def ser_index(s, block_lengths):
    ix = s['index']
    entries = ix.get('override', [[block_lengths[i], flag, key] for i, (_, flag, key) in enumerate(ix['entries'])])
    body = u32(ix.get('count', len(entries)))
    for (length, flag, key) in entries:
        body += u32(length) + u8(flag) + key_bytes(key)
    return seal(body + ix.get('extra', b''), ix.get('crc'))


def ser_filter(s):
    f = s['filter']
    return seal(u8(f['hashCount']) + f['bits'], f.get('crc'))


def ser_condition(c):
    if c['kind'] == 'drain':
        return u8(c.get('kindByte', 0)) + u64(c['ledger']) + u64(c['from'])
    out = u8(c.get('kindByte', 1)) + u32(c.get('count', len(c['inputs'])))
    for (name, off) in c['inputs']:
        nb = name.encode('latin-1')
        out += u8(len(nb)) + nb + u64(off)
    return out


def serialize(o):
    blocks, block_lengths, starts, offset = b'', [], [], 0
    for s in o['sections']:
        starts.append(offset)
        data = b''.join(ser_block(b) for b in s['blocks']) + s.get('padding', b'')
        block_lengths.append([len(ser_block(b)) for b in s['blocks']])
        blocks += data
        offset += len(data)
    blocks += o.get('padding', b'')
    metadata_offset = len(blocks)
    metadata, entries = b'', []
    for i, s in enumerate(o['sections']):
        index = ser_index(s, block_lengths[i])
        filt = ser_filter(s)
        n = s['natural']
        e = {
            'offset': starts[i],
            'length': (starts[i + 1] if i + 1 < len(starts) else len(blocks) - len(o.get('padding', b''))) - starts[i],
            'partition': s['partition'],
            'minTag': n['minTag'], 'maxTag': n['maxTag'], 'rowCount': n['rowCount'],
            'indexOffset': metadata_offset + len(metadata),
            'filterOffset': metadata_offset + len(metadata) + len(index),
        }
        natural_e = dict(e)
        for key, value in s['entry'].items():
            e[key] = value(natural_e) if callable(value) else value
        entries.append(e)
        metadata += index + filt
    t = o['trailer']
    body = u32(t.get('count', len(entries))) + u64(t.get('metadataOffset', metadata_offset))
    for e in entries:
        body += u64(e['offset']) + u64(e['length']) + partition_bytes(e['partition']) + tag_bytes(e['minTag'])
        body += tag_bytes(e['maxTag']) + u64(e['rowCount']) + u64(e['indexOffset']) + u64(e['filterOffset'])
    body += ser_condition(o['condition']) + t.get('extra', b'')
    body = t.get('transform', lambda x: x)(body)
    pre = t.get('magic', b'CLAR') + u32(t.get('version', 1)) + body
    trailer = seal(pre + u32(t.get('length', len(pre) + 8)), t.get('crc'))
    return blocks + metadata + trailer


# ---- values ---------------------------------------------------------------------------------------------------------

def row(key, tag, kind, payload=b''):
    return {'key': key, 'tag': tag, 'kind': kind, 'payload': payload}


def tag_json(t): return {'ledger': str(t[0]), 'slot': str(t[1]), 'index': t[2]}


def row_json(r):
    return {
        'key': r['key'].hex(),
        'tag': tag_json(r['tag']),
        'kind': r['kind'],
        'payload': r['payload'].hex(),
    }


def condition_json(c):
    if c['kind'] == 'drain':
        return {'kind': 'drain', 'ledger': str(c['ledger']), 'from': str(c['from'])}
    return {'kind': 'compaction', 'inputs': [{'object': n, 'offset': str(o)} for (n, o) in c['inputs']]}


ROOT = (b'', 0)
P0011 = (bytes([0x30]), 4)


accepted = []


def accept(name, sections, condition, block_bytes, bits_per_key, k=None):
    o = natural(sections, condition, block_bytes, bits_per_key, k)
    case = {
        'name': name,
        'encoded': k is None,
        'blockBytes': block_bytes,
        'bloomBitsPerKey': bits_per_key,
        'condition': condition_json(condition),
        'sections': [{'partition': {'prefix': s['partition'][0].hex(), 'depth': s['partition'][1]},
                      'hashCount': o['sections'][i]['filter']['hashCount'],
                      'blocks': len(o['sections'][i]['blocks']),
                      'rows': [row_json(r) for r in s['rows']]} for i, s in enumerate(sections)],
    }
    case['hex'] = serialize(o).hex()
    accepted.append(case)


accept(
    'A drain of ledger 3 from slot 0: one section over the whole keyspace with one row, at the default settings.',
    [{'partition': ROOT, 'rows': [row(b'\x01', (3, 0, 0), 'Append', b'\xaa')]}],
    {'kind': 'drain', 'ledger': 3, 'from': 0}, 256 * 1024, 14)

accept(
    'A drain of ledger 3 from slot 812 into partition 0011/4 in 64-byte blocks: a key whose rows span blocks, so the '
    'second block continues the first, a row longer than the block size in a block of its own, a Replace and a Delete, '
    'and empty payloads.',
    [{'partition': P0011, 'rows': [
        row(b'\x31\x01', (3, 812, 0), 'Append', b'\xaa'),
        row(b'\x31\x01', (3, 812, 1), 'Replace', b''),
        row(b'\x31\x01', (3, 813, 0), 'Append', b'\xbb\xcc'),
        row(b'\x32', (3, 812, 2), 'Delete'),
        row(b'\x3f\xff\xff', (3, 900, 0), 'Append', bytes(range(80))),
    ]}],
    {'kind': 'drain', 'ledger': 3, 'from': 812}, 64, 14)

LONG_NAME = 'c/' + 'a' * 198
accept(
    'A compaction output of two sections, the children 0/1 and 1/1 of the whole keyspace, from inputs of three objects, '
    'one with a name of 200 characters and one at offset 2^63 - 1, with one row per block (block size 1) and 1 bit per '
    'key; its rows span ledgers 9 and 10.',
    [
        {'partition': (bytes([0x00]), 1), 'rows': [
            row(b'\x10', (9, 4, 0), 'Replace', b'\x01'),
            row(b'\x10', (10, 0, 0), 'Append', b'\x02'),
            row(b'\x7f\x00', (10, 1, 3), 'Append', b'\x03'),
        ]},
        {'partition': (bytes([0x80]), 1), 'rows': [
            row(b'\x80', (9, 4, 1), 'Delete'),
            row(b'\xff' * 3, (10, 2, 0), 'Replace', b'\x04'),
        ]},
    ],
    {'kind': 'compaction', 'inputs': [(LONG_NAME, 7),
                                      ('d/10/0/0123456789abcdef0123456789abcdef', 0),
                                      ('d/10/0/0123456789abcdef0123456789abcdef', 4096),
                                      ('d/9/0/fedcba9876543210fedcba9876543210', 2**63 - 1)]},
    1, 1)

accept(
    'A drain of ledger 7 at 64 bits per key, the most a writer takes: 44 hashes per key.',
    [{'partition': ROOT, 'rows': [row(bytes([i, i]), (7, i, 0), 'Append', bytes([i])) for i in range(1, 6)]}],
    {'kind': 'drain', 'ledger': 7, 'from': 1}, 4096, 64)


# Two rows of 32 bytes each: 4 + 32 + 32 = 68 bytes fill a block of 68, and a block size of 67 splits them.
PACKED = [row(b'\x41\x01', (2, 0, 0), 'Append', b'\x01\x01\x01'), row(b'\x41\x02', (2, 1, 0), 'Append', b'\x02\x02\x02')]
assert all(len(row_bytes(r)) == 32 for r in PACKED)
accept('Two rows which fill a block of exactly 68 bytes, the block size, in one block.',
       [{'partition': ROOT, 'rows': PACKED}], {'kind': 'drain', 'ledger': 2, 'from': 0}, 68, 14)
accept('The same two rows under a block size of 67, one byte short of both, in two blocks.',
       [{'partition': ROOT, 'rows': PACKED}], {'kind': 'drain', 'ledger': 2, 'from': 0}, 67, 14)

MAX_KEY = b'\xff' * 65535
accept(
    'The largest values: a key of 65,535 bytes in a partition of its full 524,280 bits, the key as its prefix; a tag of '
    'ledger 2^64 - 1, slot 2^63 - 2, and index 2^31 - 1; and a drain from that slot.',
    [{'partition': (MAX_KEY, 524280), 'rows': [
        row(MAX_KEY, (2**64 - 1, 2**63 - 2, 2**31 - 1), 'Delete', b'\x00')]}],
    {'kind': 'drain', 'ledger': 2**64 - 1, 'from': 2**63 - 2}, 256 * 1024, 14)

accept(
    'A filter recording 3 hashes at 14 bits per key, where the writer records 10: the decoder takes the recorded count.',
    [{'partition': ROOT, 'rows': [row(bytes([i]), (6, i, 0), 'Append') for i in range(1, 4)]}],
    {'kind': 'drain', 'ledger': 6, 'from': 0}, 4096, 14, k=3)

accept(
    'A filter recording 64 hashes, the most the format records, which no writer setting gives.',
    [{'partition': ROOT, 'rows': [row(bytes([i]), (6, i, 0), 'Append') for i in range(1, 4)]}],
    {'kind': 'drain', 'ledger': 6, 'from': 0}, 4096, 64, k=64)

# ---- rejections -----------------------------------------------------------------------------------------------------

rejections = []


def base_rows():
    return [
        row(b'\x31\x01', (3, 0, 0), 'Append', b'\xaa'),
        row(b'\x31\x01', (3, 1, 0), 'Replace', b'\xdd\xee'),
        row(b'\x32', (3, 1, 1), 'Delete', b'\xbb\xcc'),
    ]


def base(rows=None, condition=None, block_bytes=64, partition=P0011):
    return natural([{'partition': partition, 'rows': rows or base_rows()}],
                   condition or {'kind': 'drain', 'ledger': 3, 'from': 0}, block_bytes, 14)


def reject(name, reason, o_or_bytes):
    data = o_or_bytes if isinstance(o_or_bytes, (bytes, bytearray)) else serialize(o_or_bytes)
    rejections.append({'name': name, 'reason': reason, 'hex': bytes(data).hex()})


def with_(o, *fns):
    for fn in fns:
        fn(o)
    return o


def S0(o): return o['sections'][0]
def B(o, i): return o['sections'][0]['blocks'][i]
def entry(o, **kv): S0(o)['entry'].update(kv)
def trailer(o, **kv): o['trailer'].update(kv)
def index(o, **kv): S0(o)['index'].update(kv)
def filt(o, **kv): S0(o)['filter'].update(kv)
def block(i, **kv): return lambda o: B(o, i).update(kv)


def index_entries(o, fn):
    entries = [list(e) for e in S0(o)['index']['entries']]
    lengths = [len(ser_block(b)) for b in S0(o)['blocks']]
    for e, length in zip(entries, lengths):
        e[0] = length
    fn(entries)
    S0(o)['index']['override'] = entries


valid = serialize(base())
assert [len(b['rows']) for b in base()['sections'][0]['blocks']] == [1, 1, 1]
assert [e[1] for e in base()['sections'][0]['index']['entries']] == [0, 1, 0]

reject('No bytes.', 'truncated', b'')
reject('Fifteen bytes, one short of the smallest trailer.', 'truncated', valid[-15:])
reject('Trailer magic XLAR.', 'bad-magic', with_(base(), lambda o: trailer(o, magic=b'XLAR')))
reject('Trailer version 2.', 'bad-version', with_(base(), lambda o: trailer(o, version=2)))
reject("A trailer length of 15, below the trailer's 16 fixed bytes.", 'length-mismatch',
       with_(base(), lambda o: trailer(o, length=15)))
reject("A trailer length one past the object's.", 'length-mismatch',
       with_(base(), lambda o: trailer(o, length=len(valid) + 1)))
reject('A trailer whose CRC-32C does not match its bytes.', 'crc-mismatch', with_(base(), lambda o: trailer(o, crc=0)))
reject('A trailer of only its fixed bytes, whose body ends before the section count.', 'body-truncated',
       seal(b'CLAR' + u32(1) + u32(16)))
reject('A trailer listing no sections.', 'no-sections', with_(base(), lambda o: trailer(o, count=0)))
reject('A metadata offset past the trailer.', 'bad-section-layout',
       with_(base(), lambda o: trailer(o, metadataOffset=10**6)))
reject('A section at offset 1, not 0.', 'sections-not-contiguous', with_(base(), lambda o: entry(o, offset=1)))
reject('A byte between the last section and the metadata.', 'sections-not-contiguous',
       with_(base(), lambda o: o.update(padding=b'\x00')))
reject('A section of length 0.', 'bad-section-range', with_(base(), lambda o: entry(o, length=0)))
reject('A section whose blocks run past the metadata.', 'bad-section-range',
       with_(base(), lambda o: entry(o, length=lambda e: e['length'] + 1)))
reject('A partition deeper than 524,280 bits.', 'bad-partition', with_(base(), lambda o: entry(o, partition=(b'', 524281))))
reject('A partition of depth 4 which sets a bit past its depth.', 'bad-partition',
       with_(base(), lambda o: entry(o, partition=(bytes([0x38]), 4))))
reject("A section's lowest tag of ledger 0.", 'bad-tag', with_(base(), lambda o: entry(o, minTag=(0, 0, 0))))
reject("A section's highest tag at slot 2^63 - 1.", 'bad-tag', with_(base(), lambda o: entry(o, maxTag=(3, 2**63 - 1, 0))))
reject("A section's highest tag at index 2^31.", 'bad-tag', with_(base(), lambda o: entry(o, maxTag=(3, 1, 2**31))))
reject("A section's lowest tag above its highest.", 'tags-unordered', with_(base(), lambda o: entry(o, minTag=(3, 2, 0))))
reject('A section of 0 rows.', 'no-rows', with_(base(), lambda o: entry(o, rowCount=0)))
reject("A block index which does not start the metadata.", 'bad-section-layout',
       with_(base(), lambda o: entry(o, indexOffset=lambda e: e['indexOffset'] + 1)))
reject('A Bloom filter 3 bytes after the block index.', 'bad-section-layout',
       with_(base(), lambda o: entry(o, filterOffset=lambda e: e['indexOffset'] + 3)))
reject('A Bloom filter past the trailer.', 'bad-section-layout',
       with_(base(), lambda o: entry(o, filterOffset=lambda e: e['filterOffset'] + 100)))

reject('A condition of kind 2.', 'bad-condition', base(condition={'kind': 'drain', 'kindByte': 2, 'ledger': 3, 'from': 0}))
reject('A drain of ledger 0.', 'bad-condition', base(condition={'kind': 'drain', 'ledger': 0, 'from': 0}))
reject('A drain from watermark 2^63.', 'bad-condition', base(condition={'kind': 'drain', 'ledger': 3, 'from': 2**63}))
reject('A compaction of no inputs.', 'bad-condition', base(condition={'kind': 'compaction', 'inputs': []}))
reject('A compaction input at offset 2^63.', 'bad-condition',
       base(condition={'kind': 'compaction', 'inputs': [('d/3/0/a', 2**63)]}))
reject('A compaction input named D/1.', 'bad-object-name', base(condition={'kind': 'compaction', 'inputs': [('D/1', 0)]}))
reject('A compaction input with an empty name.', 'bad-object-name',
       base(condition={'kind': 'compaction', 'inputs': [('', 0)]}))
reject('A compaction input with a name of 201 characters.', 'bad-object-name',
       base(condition={'kind': 'compaction', 'inputs': [('c/' + 'a' * 199, 0)]}))
reject('A compaction listing one input twice.', 'inputs-unordered',
       base(condition={'kind': 'compaction', 'inputs': [('d/3/0/a', 5), ('d/3/0/a', 5)]}))
reject('A compaction listing d/2/0/a after d/3/0/a.', 'inputs-unordered',
       base(condition={'kind': 'compaction', 'inputs': [('d/3/0/a', 0), ('d/2/0/a', 9)]}))
reject('A drain of ledger 4 whose rows are of ledger 3.', 'condition-mismatch',
       base(condition={'kind': 'drain', 'ledger': 4, 'from': 0}))
reject('A drain from slot 1 whose lowest row lies at slot 0.', 'condition-mismatch',
       base(condition={'kind': 'drain', 'ledger': 3, 'from': 1}))
reject('A drain from slot 2^63 - 1, past the last slot a row has.', 'condition-mismatch',
       base(condition={'kind': 'drain', 'ledger': 3, 'from': 2**63 - 1}))
reject("A byte after the trailer's condition.", 'trailing-bytes', with_(base(), lambda o: trailer(o, extra=b'\x00')))
reject("A trailer whose body ends inside the first section's entry.", 'body-truncated',
       with_(base(), lambda o: trailer(o, transform=lambda body: body[:14])))
reject('A block index whose CRC-32C does not match its bytes.', 'crc-mismatch', with_(base(), lambda o: index(o, crc=0)))
reject('A block index of no blocks.', 'no-blocks', with_(base(), lambda o: index(o, override=[])))
reject('A block of 4 bytes, its CRC-32C alone.', 'blocks-mismatch',
       with_(base(), lambda o: index_entries(o, lambda es: es[0].__setitem__(0, 4))))
reject('A block index which lists two of three blocks.', 'blocks-mismatch',
       with_(base(), lambda o: index_entries(o, lambda es: es.pop())))
reject('Block lengths whose sum runs past the section.', 'blocks-mismatch',
       with_(base(), lambda o: index_entries(o, lambda es: es[0].__setitem__(0, es[0][0] + 1))))
reject('A continuation flag of 2.', 'bad-continuation',
       with_(base(), lambda o: index_entries(o, lambda es: es[1].__setitem__(1, 2))))
reject("The first block's continuation flag set.", 'bad-continuation',
       with_(base(), lambda o: index_entries(o, lambda es: es[0].__setitem__(1, 1))))
reject("A block index whose first block's first key is empty.", 'empty-key',
       with_(base(), lambda o: index_entries(o, lambda es: es[0].__setitem__(2, b''))))
reject("A block index whose third block's first key is below the second's.", 'keys-unordered',
       with_(base(), lambda o: index_entries(o, lambda es: es[2].__setitem__(2, b'\x31\x00'))))
reject("A block index whose third block's first key 40 lies outside the section's partition 0011/4.",
       'first-key-outside-partition', with_(base(), lambda o: index_entries(o, lambda es: es[2].__setitem__(2, b'\x40'))))
reject("A byte after the block index's last first key.", 'trailing-bytes', with_(base(), lambda o: index(o, extra=b'\x00')))
reject('A Bloom filter whose CRC-32C does not match its bytes.', 'crc-mismatch', with_(base(), lambda o: filt(o, crc=0)))
reject('A Bloom filter of 0 hashes.', 'bad-filter', with_(base(), lambda o: filt(o, hashCount=0)))
reject('A Bloom filter of 65 hashes.', 'bad-filter', with_(base(), lambda o: filt(o, hashCount=65)))
reject('A Bloom filter of no bytes.', 'bad-filter', with_(base(), lambda o: filt(o, bits=b'')))
reject('A block whose CRC-32C does not match its bytes.', 'crc-mismatch', with_(base(), block(1, crc=0)))
reject('A block of 5 bytes, one byte of a row and its CRC-32C.', 'body-truncated',
       with_(base(), block(0, transform=lambda body: body[:1])))
reject("A block whose row's payload ends past the block.", 'body-truncated',
       with_(base(), block(0, transform=lambda body: body[:-1])))
reject("A row whose key is empty.", 'empty-key', with_(base(), lambda o: B(o, 2)['rows'][0].update(key=b'')))
reject("A row whose tag is of ledger 0.", 'bad-tag', with_(base(), lambda o: B(o, 1)['rows'][0].update(tag=(0, 1, 0))))
reject("A row whose tag lies at slot 2^63 - 1.", 'bad-tag',
       with_(base(), lambda o: B(o, 1)['rows'][0].update(tag=(3, 2**63 - 1, 0))))
reject("A row whose tag has index 2^31.", 'bad-tag', with_(base(), lambda o: B(o, 1)['rows'][0].update(tag=(3, 1, 2**31))))
reject('A row of kind 3.', 'unknown-kind', with_(base(), lambda o: B(o, 2)['rows'][0].update(kindByte=3)))

reject("A block whose first row's key is not its first key in the index.", 'index-mismatch',
       with_(base(), lambda o: index_entries(o, lambda es: es[2].__setitem__(2, b'\x31\xff'))))
reject("A block which continues the previous block's key without its continuation flag.", 'index-mismatch',
       with_(base(), lambda o: index_entries(o, lambda es: es[1].__setitem__(1, 0))))
reject("A block whose continuation flag is set although its key differs from the previous block's.", 'index-mismatch',
       with_(base(), lambda o: index_entries(o, lambda es: es[2].__setitem__(1, 1))))
reject('A block whose second row precedes its first in tag order.', 'rows-unordered',
       natural([{'partition': P0011, 'rows': [base_rows()[1], base_rows()[0], base_rows()[2]]}],
               {'kind': 'drain', 'ledger': 3, 'from': 0}, 4096, 14))
reject("Rows of one key in two blocks, the second block's below the first's in tag order.", 'rows-unordered',
       natural([{'partition': P0011, 'rows': [base_rows()[1], base_rows()[0], base_rows()[2]]}],
               {'kind': 'drain', 'ledger': 3, 'from': 0}, 64, 14))
reject("A row whose key 40, not its block's first, lies outside the section's partition 0011/4.", 'key-outside-partition',
       base([base_rows()[0], base_rows()[1], row(b'\x40', (3, 1, 1), 'Delete', b'\xbb\xcc')], block_bytes=4096))
reject("A row whose tag lies above the section's highest.", 'tag-outside-span',
       with_(base(), lambda o: entry(o, maxTag=(3, 1, 0))))

reject("A Bloom filter which admits none of its section's keys.", 'filter-miss',
       with_(base(), lambda o: filt(o, bits=bytes(len(S0(o)['filter']['bits'])))))
reject('A section counting 4 rows, which holds 3.', 'row-count-mismatch', with_(base(), lambda o: entry(o, rowCount=4)))
reject("A section whose tags reach slot 5, past its highest row's.", 'tag-span-mismatch',
       with_(base(), lambda o: entry(o, maxTag=(3, 5, 0))))

reject("A compaction output whose second section's Bloom filter fails its CRC-32C.", 'crc-mismatch',
       with_(natural(
           [{'partition': (bytes([0x00]), 1), 'rows': [row(b'\x10', (9, 4, 0), 'Replace', b'\x01')]},
            {'partition': (bytes([0x80]), 1), 'rows': [row(b'\x80', (9, 4, 1), 'Delete')]}],
           {'kind': 'compaction', 'inputs': [('d/9/0/a', 0)]}, 1, 1), lambda o: o['sections'][1]['filter'].update(crc=0)))

# Two rules broken at once: the reason is the one a reader meets first.
reject('Two rules: version 2 and a CRC-32C which does not match; the version is checked first.', 'bad-version',
       with_(base(), lambda o: trailer(o, version=2, crc=0)))
reject('Two rules: a section at offset 1 whose tags are unordered; the offset is read first.', 'sections-not-contiguous',
       with_(base(), lambda o: entry(o, offset=1, minTag=(3, 2, 0))))
reject("Two rules: a Bloom filter of 0 hashes and a block whose CRC-32C fails; the metadata is read first.", 'bad-filter',
       with_(base(), lambda o: filt(o, hashCount=0), block(0, crc=0)))
reject('Two rules: a drain of another ledger and a byte after the condition; the condition is checked first.',
       'condition-mismatch', with_(base(condition={'kind': 'drain', 'ledger': 4, 'from': 0}), lambda o: trailer(o, extra=b'\x00')))
reject('Two rules: a row outside the partition in a section counting 4 rows; rows are checked before totals.',
       'key-outside-partition',
       with_(base([base_rows()[0], base_rows()[1], row(b'\x40', (3, 1, 1), 'Delete', b'\xbb\xcc')], block_bytes=4096),
             lambda o: entry(o, rowCount=4)))

ORDER = ['truncated', 'bad-magic', 'bad-version', 'length-mismatch', 'crc-mismatch', 'body-truncated', 'no-sections',
         'bad-section-layout', 'sections-not-contiguous', 'bad-section-range', 'bad-partition', 'bad-tag',
         'tags-unordered', 'no-rows', 'bad-condition', 'bad-object-name',
         'inputs-unordered', 'condition-mismatch', 'trailing-bytes', 'no-blocks', 'blocks-mismatch',
         'bad-continuation', 'empty-key', 'keys-unordered', 'first-key-outside-partition', 'bad-filter', 'unknown-kind',
         'index-mismatch',
         'rows-unordered', 'key-outside-partition', 'tag-outside-span', 'filter-miss',
         'row-count-mismatch', 'tag-span-mismatch']
assert set(r['reason'] for r in rejections) == set(ORDER), set(ORDER) ^ set(r['reason'] for r in rejections)
assert len(ORDER) == len(set(ORDER))


def write(name, description, cases):
    with open(os.path.join(HERE, name), 'w', newline='\n') as f:
        json.dump({'description': description, 'order': ORDER, 'cases': cases} if 'rejections' in name
                  else {'description': description, 'cases': cases}, f, indent=2)
        f.write('\n')


write('run-objects.json',
      "Run objects, CLAR version 1, with the value each holds: the writer's block size and Bloom bits per key, the "
      "reference condition, and each section's partition, hash count, block count, and rows in order. Decoding the bytes "
      "yields the value, and, where encoded is true, the writer encodes the value as the bytes.",
      accepted)
write('run-object-rejections.json',
      "Run objects a decoder rejects, each breaking the rule of the archive's section 7 its reason names, the first a "
      "reader of the whole object meets; every part carries its CRC-32C, but in the crc-mismatch cases. order lists "
      "the reasons in the order of section 7's table.",
      rejections)

# The hand-made corpus: inputs a step from each accepted vector, none of them a seed, until a campaign merges the corpus.
seeds = set(bytes.fromhex(c['hex']) for c in accepted + rejections)
if os.path.isdir(CORPUS):
    shutil.rmtree(CORPUS)
os.makedirs(CORPUS)
inputs = []
for case in accepted:
    a = bytes.fromhex(case['hex'])
    if len(a) > 8192:
        continue
    inputs.append(a[:-1] + bytes([a[-1] ^ 1]))
    inputs.append(b'\x00' + a)
    inputs.append(a + b'\x00')
    x = bytearray(a)
    x[len(x) // 3] ^= 0x10
    inputs.append(bytes(x))
for i in inputs:
    assert i not in seeds
    with open(os.path.join(CORPUS, hashlib.sha1(i).hexdigest()), 'wb') as f:
        f.write(i)
print(len(accepted), 'accepted;', len(rejections), 'rejected;', len(inputs), 'corpus inputs')
