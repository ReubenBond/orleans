"""Writes archive logs' golden vectors (archive-logs.md §7.2, §7.3) and the fuzz targets' hand-made corpora from an
independent reading of the spec. Run from the repository root:

    python -m pip install -r tests/shared-vectors/archive-logs/requirements.txt
    python tests/shared-vectors/archive-logs/generate_vectors.py
"""
import hashlib
import json
import os

import xxhash

HERE = os.path.dirname(os.path.abspath(__file__))
CORPUS = os.path.join(HERE, '..', '..', '..', 'fuzz', 'corpus')
U64 = (1 << 64) - 1
WIDE = (1 << 56) - 1  # the largest generation and length a token holds
NARROW = (1 << 35) - 1  # the largest base rows and units a token holds

# ---- tokens (§7.2) -------------------------------------------------------------------------------------------------


def varint(value):
    out = bytearray()
    while value >= 0x80:
        out.append(value & 0x7F | 0x80)
        value >>= 7
    out.append(value)
    return bytes(out)


def token(g, r, p, b):
    return b'' if (g, r, p, b) == (0, 0, 0, 0) else bytes([1]) + varint(g) + varint(r) + varint(p) + varint(b)


TOKENS = [
    ('Origin, the empty token', 0, 0, 0, 0),
    ('an opener from Origin: generation 1, its own row, no appends', 1, 1, 0, 0),
    ('a first append run of one block', 1, 0, 5, 1),
    ('a run of two blocks on generation 1', 1, 0, 300, 2),
    ('an opener from generation 7', 8, 1, 0, 0),
    ('an opener whose base folds two appends after it', 2, 3, 0, 0),
    ('an opener which superseded four rows', 3, 5, 0, 0),
    ('a run after an opener of two rows', 2, 2, 7, 1),
    ('a length of two varint bytes', 2, 1, 128, 1),
    ('a block count of two varint bytes', 3, 1, 16384, 128),
    ('base rows of two varint bytes', 2, 200, 0, 0),
    ('the Azure layout at its block limit', 9, 1, 49900 * 104857600, 49900),
    ('the Azure layout at its replace request', 4, 1, 40000 * 1024, 40000),
    ('the chunk layout: one chunk per run', 5, 1, 1000000, 3),
    ('the largest generation', WIDE, 1, 0, 0),
    ('the largest base rows', 2, NARROW, 0, 0),
    ('the largest length and units', 1, 0, WIDE, NARROW),
    ('every field at its largest', WIDE, NARROW, WIDE, NARROW),
]


def token_rejections():
    one = varint(1)
    zero = varint(0)
    cases = [
        ('version 0', 'bad-version', bytes([0]) + one + one + one + one),
        ('version 2', 'bad-version', bytes([2]) + one + one + one + one),
        ('version 2 with trailing bytes: the version first', 'bad-version', bytes([2]) + one + one + one + one + b'\x00'),
        ('a version byte alone', 'bad-varint', bytes([1])),
        ('a missing units field', 'bad-varint', bytes([1]) + one + zero + one),
        ('the three fields of the former format', 'bad-varint', bytes([1]) + one + varint(5) + one),
        ('an unterminated length', 'bad-varint', bytes([1]) + one + zero + b'\x81'),
        ('an overlong generation', 'bad-varint', bytes([1, 0x81, 0x00]) + zero + one + one),
        ('an overlong zero', 'bad-varint', bytes([1, 0x80, 0x00, 0x00, 0x00, 0x00])),
        ('a varint past 64 bits', 'bad-varint', bytes([1]) + b'\xff' * 9 + b'\x02' + zero + one + one),
        ('eleven varint bytes', 'bad-varint', bytes([1]) + b'\x80' * 10 + b'\x01' + zero + one + one),
        ('a byte after the units', 'trailing-bytes', token(1, 0, 5, 1) + b'\x00'),
        ('Origin in five bytes', 'zero-generation', bytes([1, 0, 0, 0, 0])),
        ('generation 0 with appends', 'zero-generation', bytes([1, 0, 0]) + varint(5) + one),
        ('generation 0 whose units break their rule too: the generation first', 'zero-generation', bytes([1, 0, 0, 0]) + one),
        ('a generation of 2^56', 'out-of-range', bytes([1]) + varint(WIDE + 1) + one + zero + zero),
        ('base rows of 2^35', 'out-of-range', bytes([1]) + one + varint(NARROW + 1) + zero + zero),
        ('a length of 2^56', 'out-of-range', bytes([1]) + one + zero + varint(WIDE + 1) + one),
        ('units of 2^35', 'out-of-range', bytes([1]) + one + zero + varint(WIDE) + varint(NARROW + 1)),
        ('a length out of range whose units break their rule too: the range first', 'out-of-range', bytes([1]) + one + zero + varint(WIDE + 1) + zero),
        ('appends in no unit', 'bad-units', bytes([1]) + one + zero + varint(5) + zero),
        ('units with no appends', 'bad-units', bytes([1]) + one + zero + zero + one),
        ('more units than bytes', 'units-above-length', bytes([1]) + one + zero + varint(2) + varint(3)),
    ]
    return [{'name': name, 'reason': reason, 'hex': data.hex()} for name, reason, data in cases]


# ---- metadata (§7.3) -----------------------------------------------------------------------------------------------

FORMAT = 'json'
LOG = 'journals/account-17'
KEY = b'journal-1'
KEY_HASH = xxhash.xxh64(KEY, seed=0).intdigest()


def hex16(value):
    return format(value, '016x')


def checkpoint_name(log, g):
    return 'checkpoints/' + log + '/' + format(g, '020d')


def base_md5(g, empty=False):
    return hashlib.md5(b'' if empty else ('the base of generation %d' % g).encode('utf-8')).hexdigest()


def wal(g, kind=None, tomb=None, log=LOG, key_hash=KEY_HASH, empty=False, rows=1):
    pairs = [('format', FORMAT), ('wal_generation', str(g)), ('durable_key', hex16(key_hash))]
    if kind is not None:
        pairs += [('checkpoint', checkpoint_name(log, g)), ('checkpoint_offset', '0'), ('durable_kind', kind)]
        if tomb is not None:
            pairs.append(('durable_tomb', hex16(tomb)))
        pairs.append(('durable_base_md5', base_md5(g, empty)))
        pairs.append(('durable_base_rows', str(rows)))
    return pairs


def wal_case(name, g, kind=None, tomb=None, write=None, empty=False, rows=1):
    return {
        'name': name,
        'blob': 'wal',
        'write': write,
        'log': LOG,
        'key': KEY.hex(),
        'keyHash': hex16(KEY_HASH),
        'format': FORMAT,
        'tag': format(g, '020d'),
        'generation': str(g),
        'checkpoint': checkpoint_name(LOG, g) if kind is not None else None,
        'kind': kind,
        'tombstone': hex16(tomb) if tomb is not None else None,
        'baseMd5': base_md5(g, empty) if kind is not None else None,
        'base_rows': str(rows) if kind is not None else None,
        'metadata': [list(pair) for pair in wal(g, kind, tomb, empty=empty, rows=rows)],
    }


def metadata_cases():
    return [
        wal_case("a key's first append run: generation 1 with no checkpoint", 1, write='append-run'),
        wal_case('a Replace from generation 1', 2, 'replace', write='replace'),
        wal_case('an empty Replace from Origin', 1, 'replace', write='replace'),
        wal_case('a Delete with a tombstone and nothing after it: an empty base', 3, 'delete', 0x0102030405060708, write='delete', empty=True),
        wal_case('an empty Delete, as an Expired', 4, 'delete', write='delete', empty=True),
        wal_case('a Delete whose base holds the appends after it', 6, 'delete', write='delete', rows=3),
        wal_case('a Replace which superseded rows before it in its segment', 7, 'replace', write='replace', rows=4),
        wal_case('a Delete whose tombstone is all ones', 5, 'delete', U64, write='delete'),
        wal_case('the largest generation', U64, 'replace', write='replace'),
        {
            'name': "a Replace's checkpoint",
            'blob': 'checkpoint',
            'write': 'checkpoint',
            'log': LOG,
            'key': KEY.hex(),
            'keyHash': hex16(KEY_HASH),
            'format': FORMAT,
            'tag': None,
            'metadata': [['format', FORMAT], ['durable_key', hex16(KEY_HASH)]],
        },
        dict(wal_case('names in other cases, which Azure keeps case-insensitive', 2, 'replace'),
             metadata=[['Format', FORMAT], ['WAL_GENERATION', '2'], ['Durable_Key', hex16(KEY_HASH)],
                       ['Checkpoint', checkpoint_name(LOG, 2)], ['checkpoint_offset', '0'], ['durable_kind', 'replace'],
                       ['DURABLE_BASE_MD5', base_md5(2)], ['Durable_Base_Rows', '1']]),
        dict(wal_case("an Orleans caller's metadata, left alone", 1), metadata=[list(p) for p in wal(1)] + [['owner', 'grain-7']]),
        dict(wal_case('a generation 1 read without its tag', 1), tag=None),
    ]


def rejection(name, reason, pairs, blob='wal', tag='default', log=LOG, key_hash=KEY_HASH):
    if tag == 'default':
        tag = None
    return {
        'name': name,
        'reason': reason,
        'blob': blob,
        'log': log,
        'keyHash': hex16(key_hash),
        'tag': tag,
        'metadata': [list(pair) for pair in pairs],
    }


def without(pairs, name):
    return [pair for pair in pairs if pair[0] != name]


def replaced(pairs, name, value):
    return [(n, value if n == name else v) for n, v in pairs]


def metadata_rejections():
    replace2 = wal(2, 'replace')
    delete3 = wal(3, 'delete', 0x0102030405060708)
    first = wal(1)
    other = hex16(KEY_HASH ^ 1)
    return [
        rejection('a name twice', 'duplicate-name', first + [('format', FORMAT)]),
        rejection('a name in two cases', 'duplicate-name', first + [('FORMAT', FORMAT)]),
        rejection('a name twice and no generation: the duplicate first', 'duplicate-name', without(first, 'wal_generation') + [('durable_key', hex16(KEY_HASH))]),
        rejection('no format', 'bad-format', without(first, 'format')),
        rejection('an empty format', 'bad-format', replaced(first, 'format', '')),
        rejection('no format and no key: the format first', 'bad-format', without(without(first, 'format'), 'durable_key')),
        rejection('an empty checkpoint', 'bad-checkpoint', replaced(replace2, 'checkpoint', '')),
        rejection('a checkpoint with no offset', 'bad-checkpoint_offset', without(replace2, 'checkpoint_offset')),
        rejection('a checkpoint offset of 1', 'bad-checkpoint_offset', replaced(replace2, 'checkpoint_offset', '1')),
        rejection('an offset with no checkpoint', 'bad-checkpoint_offset', first + [('checkpoint_offset', '0')]),
        rejection('no generation', 'bad-wal_generation', without(first, 'wal_generation')),
        rejection('generation 0', 'bad-wal_generation', replaced(first, 'wal_generation', '0')),
        rejection('a generation with a leading zero', 'bad-wal_generation', replaced(first, 'wal_generation', '01')),
        rejection("Orleans' own generation, a GUID", 'bad-wal_generation', replaced(first, 'wal_generation', '3f2504e04f8941d39a0c0305e82c3301')),
        rejection('a generation past 64 bits', 'bad-wal_generation', replaced(first, 'wal_generation', '18446744073709551616')),
        rejection('a signed generation', 'bad-wal_generation', replaced(first, 'wal_generation', '+1')),
        rejection('a tag of 19 digits', 'bad-durable-gen', first, tag='0000000000000000001'),
        rejection('a tag of generation 0', 'bad-durable-gen', first, tag='00000000000000000000'),
        rejection('a tag past 64 bits', 'bad-durable-gen', first, tag='99999999999999999999'),
        rejection('an unknown kind', 'bad-durable_kind', replaced(replace2, 'durable_kind', 'append')),
        rejection('a kind in capitals', 'bad-durable_kind', replaced(replace2, 'durable_kind', 'Replace')),
        rejection('a short tombstone', 'bad-durable_tomb', replaced(delete3, 'durable_tomb', '0102')),
        rejection('an upper-case tombstone', 'bad-durable_tomb', replaced(delete3, 'durable_tomb', '0102030405060A08')),
        rejection('a short base MD5', 'bad-durable_base_md5', replaced(replace2, 'durable_base_md5', '0102')),
        rejection('an upper-case base MD5', 'bad-durable_base_md5', replaced(replace2, 'durable_base_md5', base_md5(2).upper())),
        rejection('a base MD5 in Base64', 'bad-durable_base_md5', replaced(replace2, 'durable_base_md5', '1B2M2Y8AsgTpgAmY7PhCfg==')),
        rejection('a bad base MD5 and no key: the MD5 first', 'bad-durable_base_md5', without(replaced(replace2, 'durable_base_md5', '0102'), 'durable_key')),
        rejection('base rows of 0', 'bad-durable_base_rows', replaced(replace2, 'durable_base_rows', '0')),
        rejection('base rows with a leading zero', 'bad-durable_base_rows', replaced(replace2, 'durable_base_rows', '01')),
        rejection('base rows of 2^35', 'bad-durable_base_rows', replaced(replace2, 'durable_base_rows', str(NARROW + 1))),
        rejection('base rows in hexadecimal', 'bad-durable_base_rows', replaced(replace2, 'durable_base_rows', 'a')),
        rejection('bad base rows and no key: the rows first', 'bad-durable_base_rows', without(replaced(replace2, 'durable_base_rows', '0'), 'durable_key')),
        rejection('no key', 'bad-durable_key', without(first, 'durable_key')),
        rejection('an upper-case key', 'bad-durable_key', replaced(first, 'durable_key', hex16(KEY_HASH).upper())),
        rejection('a key of 15 digits', 'bad-durable_key', replaced(first, 'durable_key', hex16(KEY_HASH)[1:])),
        rejection('a checkpoint with no kind', 'kind-mismatch', without(replace2, 'durable_kind')),
        rejection('a kind with no checkpoint', 'kind-mismatch', first + [('durable_kind', 'delete')]),
        rejection('an opener with no base MD5', 'kind-mismatch', without(replace2, 'durable_base_md5')),
        rejection('a base MD5 with no opener', 'kind-mismatch', first + [('durable_base_md5', base_md5(1))]),
        rejection('an opener with no base rows', 'kind-mismatch', without(replace2, 'durable_base_rows')),
        rejection('base rows with no opener', 'kind-mismatch', first + [('durable_base_rows', '1')]),
        rejection('generation 2 with no opener', 'opener-missing', wal(2)),
        rejection('generation 2 with no opener, and a tombstone: the opener first', 'opener-missing', wal(2) + [('durable_tomb', hex16(7))]),
        rejection('generation 2 with no opener, under the tag of 3: the opener first', 'opener-missing', wal(2), tag=format(3, '020d')),
        rejection('a tombstone on a Replace', 'tomb-mismatch', replace2 + [('durable_tomb', hex16(7))]),
        rejection('a tombstone with no opener', 'tomb-mismatch', first + [('durable_tomb', hex16(7))]),
        rejection('a checkpoint with no kind, and a tombstone: the kind first', 'kind-mismatch', without(replace2, 'durable_kind') + [('durable_tomb', hex16(7))]),
        rejection('a tag of another generation', 'generation-mismatch', replace2, tag=format(3, '020d')),
        rejection('another generation and another log: the generation first', 'generation-mismatch',
                  replaced(replace2, 'checkpoint', checkpoint_name('journals/account-18', 2)), tag=format(3, '020d')),
        rejection("a checkpoint of another log", 'checkpoint-mismatch', replaced(replace2, 'checkpoint', checkpoint_name('journals/account-18', 2))),
        rejection('a checkpoint of another generation', 'checkpoint-mismatch', replaced(replace2, 'checkpoint', checkpoint_name(LOG, 1))),
        rejection("Orleans' own checkpoint name", 'checkpoint-mismatch', replaced(replace2, 'checkpoint', 'checkpoints/' + LOG + '/3f2504e04f8941d39a0c0305e82c3301')),
        rejection('a checkpoint and a key of others: the checkpoint first', 'checkpoint-mismatch',
                  replaced(replaced(replace2, 'checkpoint', checkpoint_name(LOG, 1)), 'durable_key', other)),
        rejection("another key's hash", 'key-mismatch', replaced(first, 'durable_key', other)),
        rejection("a checkpoint of another key", 'key-mismatch', [('format', FORMAT), ('durable_key', other)], blob='checkpoint'),
        rejection("a checkpoint with no format", 'bad-format', [('durable_key', hex16(KEY_HASH))], blob='checkpoint'),
        rejection("a checkpoint with no key", 'bad-durable_key', [('format', FORMAT)], blob='checkpoint'),
        rejection("a checkpoint with a name twice", 'duplicate-name', [('format', FORMAT), ('durable_key', hex16(KEY_HASH)), ('Durable_Key', hex16(KEY_HASH))], blob='checkpoint'),
    ]


# ---- chunk bases (§7.4) --------------------------------------------------------------------------------------------

CHUNK_KINDS = {None: 0, 'Replace': 1, 'Delete': 2}
TOMBSTONE = 0x0102030405060708


def crc32c(data):
    crc = 0xFFFFFFFF
    for byte in data:
        crc ^= byte
        for _ in range(8):
            crc = (crc >> 1) ^ (0x82F63B78 if crc & 1 else 0)
    return crc ^ 0xFFFFFFFF


def chunk_header(kind=0, flags=0, reserved=0, key_hash=KEY_HASH, rows=0, tomb=0, length=0, md5=None, magic=b'CLAB',
                 version=1, crc=None):
    """A base's 60-byte header: the fields of §7.4 in order, little-endian, then the CRC-32C of the 56 bytes before it."""
    body = (magic + bytes([version, kind, flags, reserved]) + key_hash.to_bytes(8, 'little') + rows.to_bytes(8, 'little')
            + tomb.to_bytes(8, 'little') + length.to_bytes(8, 'little') + (md5 if md5 is not None else hashlib.md5(b'').digest()))
    return body + (crc32c(body) if crc is None else crc).to_bytes(4, 'little')


def chunk_base(opener=None, rows=0, tomb=None, content=b''):
    kind = CHUNK_KINDS[opener]
    return chunk_header(kind, 1 if tomb is not None else 0, 0, KEY_HASH, rows, tomb or 0, len(content),
                        hashlib.md5(content).digest()) + content


CHUNK_BASES = [
    ('a key\'s first append run\'s empty base of generation 1', None, 0, None, b''),
    ('a Replace of one row', 'Replace', 1, None, b'state'),
    ('a Replace which folds three rows', 'Replace', 3, None, bytes(range(40))),
    ('a Delete with no tombstone and no appends', 'Delete', 1, None, b''),
    ('a Delete whose base folds appends after it', 'Delete', 2, None, b'after'),
    ('a Delete with a tombstone', 'Delete', 1, TOMBSTONE, b''),
    ('the largest base rows', 'Replace', NARROW, None, b'x'),
]


def chunk_cases():
    return [{'name': name, 'keyHash': hex16(KEY_HASH), 'opener': opener or 'none', 'rows': str(rows),
             'tombstone': None if tomb is None else hex16(tomb), 'content': content.hex(),
             'hex': chunk_base(opener, rows, tomb, content).hex()} for name, opener, rows, tomb, content in CHUNK_BASES]


def chunk_rejections():
    replace = chunk_base('Replace', 1, None, b'state')
    md5 = hashlib.md5(b'state').digest()
    flipped = bytearray(replace)
    flipped[20] ^= 1
    cases = [
        ('an empty object', 'truncated', b''),
        ('a header one byte short', 'truncated', replace[:59]),
        ('another magic', 'bad-magic', chunk_header(magic=b'CLAX')),
        ('another magic and version: the magic first', 'bad-magic', chunk_header(magic=b'CLAX', version=2)),
        ('version 0', 'bad-version', chunk_header(version=0)),
        ('version 2', 'bad-version', chunk_header(version=2)),
        ('a flipped header bit', 'bad-crc', bytes(flipped)),
        ('a flipped header bit and a bad kind: the checksum first', 'bad-crc', chunk_header(kind=3, crc=0)),
        ('kind 3', 'bad-kind', chunk_header(kind=3)),
        ('a flag past the tombstone\'s', 'bad-flags', chunk_header(kind=2, flags=2, rows=1)),
        ('a reserved byte set', 'bad-flags', chunk_header(reserved=1)),
        ('a Replace with a tombstone', 'tomb-mismatch', chunk_header(kind=1, flags=1, rows=1, tomb=TOMBSTONE)),
        ('a tombstone value with no flag', 'tomb-mismatch', chunk_header(kind=2, rows=1, tomb=TOMBSTONE)),
        ('base rows on a first run\'s base', 'bad-rows', chunk_header(rows=1)),
        ('a Replace of no rows', 'bad-rows', chunk_header(kind=1, rows=0)),
        ('a Delete of 2^35 rows', 'bad-rows', chunk_header(kind=2, rows=NARROW + 1)),
        ('a first run\'s base with content', 'none-with-content', chunk_header(length=1, md5=hashlib.md5(b'x').digest()) + b'x'),
        ('a length past the content', 'bad-length', chunk_header(kind=1, rows=1, length=6, md5=md5) + b'state'),
        ('content past the length', 'bad-length', chunk_header(kind=1, rows=1, length=4, md5=md5) + b'state'),
        ('content which does not hash to its MD5', 'bad-md5', chunk_header(kind=1, rows=1, length=5, md5=md5) + b'stale'),
    ]
    return [{'name': name, 'reason': reason, 'hex': data.hex()} for name, reason, data in cases]


def chunk(content, crc=None):
    """A chunk: an append run's bytes, then their CRC-32C in 4 little-endian bytes (§7.4)."""
    return content + (crc32c(content) if crc is None else crc).to_bytes(4, 'little')


CHUNKS = [
    ('a run of one byte', b'\x01'),
    ('a run of several appends', b'append-one|append-two'),
    ('a run of every byte value', bytes(range(256))),
]


def chunk_object_cases():
    return [{'name': name, 'content': content.hex(), 'hex': chunk(content).hex()} for name, content in CHUNKS]


def chunk_object_rejections():
    cases = [
        ('an empty object', 'truncated', b''),
        ('a trailer alone', 'truncated', chunk(b'')),
        ('three bytes', 'truncated', b'\x01\x02\x03'),
        ('a flipped content bit', 'bad-crc', bytes([0x02]) + chunk(b'\x01')[1:]),
        ('a flipped trailer bit', 'bad-crc', chunk(b'\x01', crc32c(b'\x01') ^ 1)),
    ]
    return [{'name': name, 'reason': reason, 'hex': data.hex()} for name, reason, data in cases]


# ---- the metadata target's input (archive-log-metadata) ------------------------------------------------------------


def metadata_input(case):
    """The target's input: lines of the blob ('wal' or 'checkpoint'), the log, the key hash, and the tag or nothing,
    then one 'name=value' line per metadata pair, joined by line feeds."""
    lines = [case['blob'], case['log'], case['keyHash'], case['tag'] or '']
    lines += [name + '=' + value for name, value in case['metadata']]
    return '\n'.join(lines).encode('utf-8')


def write(path, document):
    with open(path, 'w', encoding='utf-8', newline='\n') as file:
        json.dump(document, file, indent=2, ensure_ascii=False)
        file.write('\n')


def write_corpus(target, inputs):
    directory = os.path.join(CORPUS, target)
    os.makedirs(directory, exist_ok=True)
    for data in inputs:
        with open(os.path.join(directory, hashlib.sha1(data).hexdigest()), 'wb') as file:
            file.write(data)


def main():
    tokens = [{'name': name, 'generation': str(g), 'base_rows': str(r), 'length': str(p), 'units': str(b),
               'hex': token(g, r, p, b).hex()} for name, g, r, p, b in TOKENS]
    write(os.path.join(HERE, 'tokens.json'), {'cases': tokens, 'rejections': token_rejections()})
    cases = metadata_cases()
    rejections = metadata_rejections()
    write(os.path.join(HERE, 'metadata.json'), {'cases': cases})
    write(os.path.join(HERE, 'metadata-rejections.json'), {'cases': rejections})
    chunks = chunk_cases()
    chunk_rejected = chunk_rejections()
    write(os.path.join(HERE, 'chunk-bases.json'), {'cases': chunks, 'rejections': chunk_rejected})
    write(os.path.join(HERE, 'chunks.json'), {'cases': chunk_object_cases(), 'rejections': chunk_object_rejections()})

    # Hand-made inputs beyond the vectors: near misses of each rule.
    write_corpus('archive-log-token', [
        bytes([1, 1, 0, 0x80, 0x01, 0x80, 0x01]), bytes([1, 1, 0, 0x7f, 0x7f]), bytes([1, 0xff, 0x01, 1, 0, 0]),
        bytes([1, 1]) + varint(NARROW) + bytes([0, 0]), bytes([1]) + varint(WIDE) + bytes([2, 0, 0]),
    ])
    write_corpus('archive-log-metadata', [
        metadata_input(dict(cases[0], metadata=[['wal_generation', '1'], ['format', FORMAT], ['durable_key', hex16(KEY_HASH)]])),
        metadata_input(dict(cases[1], tag=None)),
        metadata_input(dict(rejections[0], log='a/b/c')),
    ])
    write_corpus('archive-log-chunk', [chunk(b'\x00' * 5), chunk(b'ab')[:-1], chunk(b'\xff' * 7, 0)])
    write_corpus('archive-log-chunk-base', [
        chunk_header(kind=2, flags=1, rows=1, tomb=0),
        chunk_header(kind=1, rows=1, length=1 << 40),
        chunk_base('Replace', 1, None, b'state')[:-1],
        chunk_header(flags=0x80),
    ])


if __name__ == '__main__':
    main()
