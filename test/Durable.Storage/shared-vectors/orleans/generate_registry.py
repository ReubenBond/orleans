"""Writes the node registry golden vectors (orleans.md §7.3, CLNR version 1), the hand-made fuzz seeds among them, from an
independent reading of the spec: valid bodies are serialized by the Protocol Buffers library from the schema of §7.3,
and each rejection breaks one rule of its own. Run from the repository root:

    python -m pip install -r tests/shared-vectors/orleans/requirements.txt
    python tests/shared-vectors/orleans/generate_registry.py
"""
import json
import os
import struct
import uuid

from google.protobuf import descriptor_pb2, descriptor_pool, message_factory

HERE = os.path.dirname(os.path.abspath(__file__))

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


assert crc32c(b'123456789') == 0xE3069283


def value(body, magic=b'CLNR', version=1, flags=0, length=None, crc=None, trailing=b''):
    """A CLNR value: the 16-byte header over the body, with the CRC-32C of bytes 0-11 and the body unless given."""
    head = magic + struct.pack('<HHI', version, flags, len(body) if length is None else length)
    checksum = crc32c(head + body) if crc is None else crc
    return head + struct.pack('<I', checksum) + body + trailing


# ---- the schema of orleans.md §7.3 ---------------------------------------------------------------------------------

F = descriptor_pb2.FieldDescriptorProto
proto = descriptor_pb2.FileDescriptorProto(name='orleans/durable/storage/v1/registry.proto', package='orleans.durable.storage.v1', syntax='proto3')
store = proto.enum_type.add(name='ArchiveStore')
for name, number in (('ARCHIVE_STORE_UNSPECIFIED', 0), ('ARCHIVE_STORE_LSM', 1), ('ARCHIVE_STORE_LOGS', 2), ('ARCHIVE_STORE_MEMORY', 3)):
    store.value.add(name=name, number=number)
entry_type = proto.message_type.add(name='NodeEntry')
entry_type.field.add(name='node', number=1, type=F.TYPE_BYTES, label=F.LABEL_OPTIONAL)
entry_type.field.add(name='silo_endpoint', number=2, type=F.TYPE_STRING, label=F.LABEL_OPTIONAL)
entry_type.field.add(name='generation', number=3, type=F.TYPE_INT32, label=F.LABEL_OPTIONAL)
entry_type.field.add(name='ordinal', number=4, type=F.TYPE_INT32, label=F.LABEL_OPTIONAL, oneof_index=0, proto3_optional=True)
entry_type.field.add(name='retired_revision', number=5, type=F.TYPE_UINT64, label=F.LABEL_OPTIONAL)
entry_type.field.add(name='failure_domain', number=6, type=F.TYPE_STRING, label=F.LABEL_OPTIONAL)
entry_type.field.add(name='diskless', number=7, type=F.TYPE_BOOL, label=F.LABEL_OPTIONAL)
entry_type.oneof_decl.add(name='_ordinal')
registry_type = proto.message_type.add(name='NodeRegistry')
registry_type.field.add(name='nodes', number=1, type=F.TYPE_MESSAGE, label=F.LABEL_REPEATED, type_name='.orleans.durable.storage.v1.NodeEntry')
registry_type.field.add(name='revision', number=2, type=F.TYPE_UINT64, label=F.LABEL_OPTIONAL)
registry_type.field.add(name='archive_store', number=3, type=F.TYPE_ENUM, label=F.LABEL_OPTIONAL, type_name='.orleans.durable.storage.v1.ArchiveStore')
registry_type.field.add(name='archive_store_id', number=4, type=F.TYPE_BYTES, label=F.LABEL_OPTIONAL)
registry_type.field.add(name='archive_store_prefix', number=5, type=F.TYPE_STRING, label=F.LABEL_OPTIONAL)
registry_type.field.add(name='object_ledger_store_id', number=6, type=F.TYPE_BYTES, label=F.LABEL_OPTIONAL)
registry_type.field.add(name='drain_all_revision', number=7, type=F.TYPE_UINT64, label=F.LABEL_OPTIONAL)
pool = descriptor_pool.DescriptorPool()
pool.Add(proto)
NodeRegistry = message_factory.GetMessageClass(pool.FindMessageTypeByName('orleans.durable.storage.v1.NodeRegistry'))

STORES = {None: 0, 'lsm': 1, 'logs': 2, 'memory': 3}
STORE_ID = 'fedcba98-7654-4321-a000-00000000000a'
STORE_PREFIX = 'abcdefghij'
OBJECT_STORE_ID = '11223344-5566-4778-99aa-bbccddeeff01'


def node_id(n):
    return '01234567-89ab-4def-8000-%012x' % n


def body_of(revision, nodes, archive_store=None, store_id=None, raw_store=None, prefix=None, raw_prefix=None, object_store_id=None, drain_all_revision=0):
    """Serializes a NodeRegistry deterministically; nodes are dicts in the vectors' form, with raw overrides."""
    registry = NodeRegistry()
    for node in nodes:
        entry = registry.nodes.add()
        entry.node = node['raw_node'] if 'raw_node' in node else uuid.UUID(node['node']).bytes
        entry.silo_endpoint = node['siloEndpoint']
        entry.generation = node['generation']
        if node.get('ordinal') is not None:
            entry.ordinal = node['ordinal']
        entry.retired_revision = int(node.get('retiredRevision', '0'))
        entry.failure_domain = node.get('failureDomain') or ''
        entry.diskless = bool(node.get('diskless', False))
    registry.revision = revision
    registry.archive_store = STORES[archive_store] if raw_store is None else raw_store
    if store_id is not None:
        registry.archive_store_id = store_id
    elif archive_store is not None:
        registry.archive_store_id = uuid.UUID(STORE_ID).bytes
    if prefix is not None:
        registry.archive_store_prefix = prefix
    elif archive_store is not None or raw_store not in (None, 0):
        registry.archive_store_prefix = STORE_PREFIX
    if object_store_id is not None:
        registry.object_ledger_store_id = object_store_id
    if drain_all_revision:
        registry.drain_all_revision = drain_all_revision
    body = registry.SerializeToString(deterministic=True)
    # A prefix which is not UTF-8, which the library refuses to write: field 5's bytes appended, the last field.
    return body if raw_prefix is None else body + length_delimited(5, raw_prefix)


def node(n, endpoint, generation, ordinal=None, retired=0, domain=None, diskless=False):
    return {
        'node': node_id(n),
        'siloEndpoint': endpoint,
        'generation': generation,
        'ordinal': ordinal,
        'retiredRevision': str(retired),
        'failureDomain': domain,
        'diskless': diskless,
    }


# ---- accepted values -----------------------------------------------------------------------------------------------

ACCEPTED = [
    ('A first registration: one node, no ordinal or failure domain, on the LSM.', 1, 'lsm',
     [node(1, '10.0.0.1:11111', 1)]),
    ('Three nodes in node byte order, on archive logs: an ordinal succession retired node 2 at revision 5, ordinal 0 '
     'is written, and an IPv6 endpoint is bracketed.', 5, 'logs',
     [node(1, '10.0.0.1:11111', 3, ordinal=0, domain='zone-a'),
      node(2, '10.0.0.2:11111', 1, ordinal=1, retired=5, domain='zone-b'),
      node(3, '[2001:db8::3]:11111', 2147483647, ordinal=1, domain='zone-b')]),
    ('No node and no archive store: a value revision 1 holds before any registration recorded a store.', 1, None, []),
    ('The largest port, and a failure domain outside ASCII.', 1, 'lsm', [node(1, '10.0.0.1:65535', 1, domain='zone-\u00e9')]),
    ('A failure domain of 64 bytes, the most an entry holds.', 1, 'lsm', [node(1, '10.0.0.1:11111', 1, domain='z' * 62 + '\u00e9')]),
    ('A first registration on the memory store, which only tests and simulations record.', 1, 'memory', [node(1, '10.0.0.1:11111', 1)]),
    ('An archive store ID whose upper 64 bits are zero: the smallest ID.', 1, 'lsm', [node(1, '10.0.0.1:11111', 1)],
     '00000000-0000-0000-0000-000000000001'),
    ('An archive store ID whose lower 64 bits are zero, and whose top bit is set.', 1, 'logs', [node(1, '10.0.0.1:11111', 1)],
     '80000000-0000-0000-0000-000000000000'),
    ('A store prefix of one character.', 1, 'lsm', [node(1, '10.0.0.1:11111', 1)], None, 'a'),
    ('A store prefix of 16 characters, the most, with every kind of character it may hold.', 1, 'logs',
     [node(1, '10.0.0.1:11111', 1)], None, 'z09_-azaz09_-azz'),
    ('A mixed cluster with an object-ledger store ID and a diskless node.', 2, 'lsm',
     [node(1, '10.0.0.1:11111', 1, ordinal=0, domain='zone-a'),
      node(2, '10.0.0.2:11111', 1, domain='zone-b', diskless=True)], None, None, OBJECT_STORE_ID),
    ('A cluster actively draining all node-backed ledgers at revision 3, with an object-ledger store ID.', 3, 'lsm',
     [node(1, '10.0.0.1:11111', 1, ordinal=0, domain='zone-a')], None, None, OBJECT_STORE_ID, 3),
    # Hand-made fuzz seeds, kept as vectors so a corpus merge never drops them; NodeRegistryVectorTests names each.
    ('Seed archive-logs-store: a registry at revision 2 on archive logs.', 2, 'logs', [node(1, '10.0.0.1:11111', 1)]),
    ('Seed ipv6-loopback-endpoint: the IPv6 loopback endpoint.', 1, 'lsm', [node(1, '[::1]:11111', 1)]),
    ('Seed largest-revision: the largest revision, and a node retired at it.', (1 << 64) - 1, 'lsm',
     [node(1, '10.0.0.1:11111', 1, retired=(1 << 64) - 1)]),
]


def accepted():
    cases = []
    for name, revision, archive_store, nodes, *rest in ACCEPTED:
        identifier = uuid.UUID(rest[0] if rest and rest[0] else STORE_ID)
        prefix = rest[1] if len(rest) > 1 and rest[1] else STORE_PREFIX
        object_store = uuid.UUID(rest[2]) if len(rest) > 2 and rest[2] else None
        drain_all_revision = rest[3] if len(rest) > 3 and rest[3] else 0
        cases.append({
            'name': name,
            'revision': str(revision),
            'archiveStore': None if archive_store is None else {'kind': archive_store, 'prefix': prefix, 'id': identifier.hex},
            'objectLedgerStoreId': None if object_store is None else object_store.hex,
            'drainAllRevision': str(drain_all_revision),
            'nodes': nodes,
            'hex': value(body_of(revision, nodes, archive_store, store_id=None if archive_store is None else identifier.bytes,
                                 prefix=None if archive_store is None else prefix,
                                 object_store_id=None if object_store is None else object_store.bytes,
                                 drain_all_revision=drain_all_revision)).hex(),
        })
    return cases


# ---- rejections ----------------------------------------------------------------------------------------------------

ONE = [node(1, '10.0.0.1:11111', 1)]
VALID_BODY = body_of(1, ONE, 'lsm')
UUID1 = uuid.UUID(node_id(1)).bytes


def varint(n):
    out = bytearray()
    n &= (1 << 64) - 1
    while n >= 0x80:
        out.append((n & 0x7F) | 0x80)
        n >>= 7
    out.append(n)
    return bytes(out)


def tag(field, wire):
    return varint((field << 3) | wire)


def length_delimited(field, data):
    return tag(field, 2) + varint(len(data)) + data


def entry_bytes(node_bytes=UUID1, endpoint=b'10.0.0.1:11111', generation=1, extra=b''):
    out = length_delimited(1, node_bytes) + length_delimited(2, endpoint)
    if generation:
        out += tag(3, 0) + varint(generation)
    return out + extra


STORE_FIELDS = tag(3, 0) + varint(1) + length_delimited(4, uuid.UUID(STORE_ID).bytes) + length_delimited(5, STORE_PREFIX.encode())


def rejections():
    r = []

    def add(name, reason, data):
        r.append({'name': name, 'reason': reason, 'hex': data.hex()})

    full = value(VALID_BODY)
    assert full == value(length_delimited(1, entry_bytes()) + tag(2, 0) + varint(1) + STORE_FIELDS)
    add('No bytes.', 'truncated', b'')
    add('Fifteen bytes of a header.', 'truncated', full[:15])
    add('A body one byte shorter than the header says.', 'truncated', value(VALID_BODY, length=len(VALID_BODY) + 1))
    add('Magic XLNR.', 'bad-magic', value(VALID_BODY, magic=b'XLNR'))
    add('Version 2.', 'bad-version', value(VALID_BODY, version=2))
    add('Flags 1.', 'bad-flags', value(VALID_BODY, flags=1))
    add('The CRC-32C of other bytes.', 'crc-mismatch', value(VALID_BODY, crc=crc32c(b'other')))
    add('One byte after the body.', 'trailing-bytes', value(VALID_BODY, trailing=b'\x00'))
    add('An unknown field 9 after the archive store ID.', 'non-canonical', value(VALID_BODY + tag(9, 0) + varint(1)))
    add('The revision before the nodes.', 'non-canonical',
        value(tag(2, 0) + varint(1) + length_delimited(1, entry_bytes()) + STORE_FIELDS))
    add('The revision as a padded two-byte varint.', 'non-canonical',
        value(length_delimited(1, entry_bytes()) + tag(2, 0) + b'\x81\x00' + STORE_FIELDS))
    add('The revision written twice.', 'non-canonical',
        value(length_delimited(1, entry_bytes()) + tag(2, 0) + varint(1) + tag(2, 0) + varint(1) + STORE_FIELDS))
    add('A retired revision of 0 written explicitly.', 'non-canonical',
        value(length_delimited(1, entry_bytes(extra=tag(5, 0) + varint(0))) + tag(2, 0) + varint(1) + STORE_FIELDS))
    add('A silo endpoint which is not UTF-8.', 'non-canonical',
        value(length_delimited(1, entry_bytes(endpoint=b'\xff:1')) + tag(2, 0) + varint(1) + STORE_FIELDS))
    add('The revision as a fixed64.', 'non-canonical',
        value(length_delimited(1, entry_bytes()) + tag(2, 1) + struct.pack('<Q', 1) + STORE_FIELDS))
    add('A node entry whose length runs past the body.', 'non-canonical', value(tag(1, 2) + varint(100) + entry_bytes()))
    add('A truncated varint.', 'non-canonical', value(tag(2, 0) + b'\x80'))
    add('No revision.', 'zero-revision', value(body_of(0, ONE, 'lsm')))
    add('An empty body, which holds no revision.', 'zero-revision', value(b''))
    add('Archive store 4.', 'unknown-archive-store', value(body_of(1, ONE, raw_store=4, store_id=uuid.UUID(STORE_ID).bytes)))
    add('An archive store ID of 15 bytes.', 'bad-archive-store-id', value(body_of(1, ONE, 'lsm', store_id=uuid.UUID(STORE_ID).bytes[:15])))
    add('An archive store ID of 17 bytes.', 'bad-archive-store-id', value(body_of(1, ONE, 'lsm', store_id=uuid.UUID(STORE_ID).bytes + b'\x01')))
    add('The memory store with an ID of 15 bytes.', 'bad-archive-store-id', value(body_of(1, ONE, 'memory', store_id=uuid.UUID(STORE_ID).bytes[:15])))
    add('An archive store ID of zeros.', 'bad-archive-store-id', value(body_of(1, ONE, 'lsm', store_id=bytes(16))))
    add('An archive store ID with no archive store.', 'bad-archive-store-id', value(body_of(1, ONE, None, store_id=uuid.UUID(STORE_ID).bytes)))
    add('A recorded archive store without its ID.', 'bad-archive-store-id', value(body_of(1, ONE, raw_store=1, store_id=b'')))
    add('A recorded archive store without its prefix.', 'bad-archive-store-prefix', value(body_of(1, ONE, 'lsm', prefix='')))
    add('A store prefix of 17 characters.', 'bad-archive-store-prefix', value(body_of(1, ONE, 'lsm', prefix='a' * 17)))
    add('A store prefix in upper case.', 'bad-archive-store-prefix', value(body_of(1, ONE, 'lsm', prefix='Abc')))
    add('A store prefix of two components.', 'bad-archive-store-prefix', value(body_of(1, ONE, 'lsm', prefix='a/b')))
    add('A store prefix with a dot.', 'bad-archive-store-prefix', value(body_of(1, ONE, 'lsm', prefix='a.b')))
    add('A store prefix outside ASCII.', 'bad-archive-store-prefix', value(body_of(1, ONE, 'lsm', prefix='\u00e9')))
    add('A store prefix which is not UTF-8.', 'non-canonical', value(body_of(1, ONE, 'lsm', prefix='', raw_prefix=b'\xff')))
    add('A store prefix with no archive store.', 'bad-archive-store-prefix', value(body_of(1, ONE, None, prefix=STORE_PREFIX)))
    add('An object-ledger store ID of 15 bytes.', 'bad-object-ledger-store-id', value(body_of(1, ONE, 'lsm', object_store_id=uuid.UUID(OBJECT_STORE_ID).bytes[:15])))
    add('An object-ledger store ID of 17 bytes.', 'bad-object-ledger-store-id', value(body_of(1, ONE, 'lsm', object_store_id=uuid.UUID(OBJECT_STORE_ID).bytes + b'\x01')))
    add('An object-ledger store ID of zeros.', 'bad-object-ledger-store-id', value(body_of(1, ONE, 'lsm', object_store_id=bytes(16))))
    add('A drain-all revision of 2 in a registry at revision 1.', 'drain-all-above-revision',
        value(body_of(1, ONE, 'lsm', object_store_id=uuid.UUID(OBJECT_STORE_ID).bytes, drain_all_revision=2)))
    add('A non-zero drain-all revision without an object-ledger store ID.', 'drain-all-without-object-ledger-store',
        value(body_of(1, ONE, 'lsm', drain_all_revision=1)))
    add('A node identity of 15 bytes.', 'bad-node', value(body_of(1, [dict(ONE[0], raw_node=UUID1[:15])], 'lsm')))
    add('The zero node identity.', 'bad-node', value(body_of(1, [dict(ONE[0], raw_node=bytes(16))], 'lsm')))
    add('Nodes out of byte order.', 'nodes-not-ascending',
        value(body_of(1, [node(2, '10.0.0.2:11111', 1), node(1, '10.0.0.1:11111', 1)], 'lsm')))
    add('A node listed twice.', 'repeated-node',
        value(body_of(1, [node(1, '10.0.0.1:11111', 1), node(1, '10.0.0.2:11111', 1)], 'lsm')))
    add('A host name for an endpoint.', 'bad-silo-endpoint', value(body_of(1, [node(1, 'silo-0:11111', 1)], 'lsm')))
    add('An endpoint without a port.', 'bad-silo-endpoint', value(body_of(1, [node(1, '10.0.0.1', 1)], 'lsm')))
    add('An IPv6 endpoint without brackets.', 'bad-silo-endpoint', value(body_of(1, [node(1, '2001:db8::3', 1)], 'lsm')))
    add('An IPv4-mapped IPv6 endpoint, which a silo address writes as IPv4.', 'bad-silo-endpoint',
        value(body_of(1, [node(1, '[::ffff:10.0.0.1]:11111', 1)], 'lsm')))
    add('No generation.', 'non-positive-generation', value(body_of(1, [node(1, '10.0.0.1:11111', 0)], 'lsm')))
    add('Generation -1.', 'non-positive-generation', value(body_of(1, [node(1, '10.0.0.1:11111', -1)], 'lsm')))
    add('Ordinal -1.', 'negative-ordinal', value(body_of(1, [node(1, '10.0.0.1:11111', 1, ordinal=-1)], 'lsm')))
    add('A node retired at revision 2 of a registry at revision 1.', 'retired-above-revision',
        value(body_of(1, [node(1, '10.0.0.1:11111', 1, retired=2)], 'lsm')))
    add('A failure domain of 65 bytes.', 'long-failure-domain', value(body_of(1, [node(1, '10.0.0.1:11111', 1, domain='z' * 65)], 'lsm')))
    add('A diskless node carrying an ordinal.', 'diskless-ordinal', value(body_of(1, [node(1, '10.0.0.1:11111', 1, ordinal=0, diskless=True)], 'lsm')))

    # Boundaries of single rules.
    add('Port 65536.', 'bad-silo-endpoint', value(body_of(1, [node(1, '10.0.0.1:65536', 1)], 'lsm')))
    add('An IPv6 address in upper case.', 'bad-silo-endpoint', value(body_of(1, [node(1, '[2001:DB8::3]:11111', 1)], 'lsm')))
    add('An uncompressed IPv6 address.', 'bad-silo-endpoint', value(body_of(1, [node(1, '[2001:db8:0:0:0:0:0:3]:11111', 1)], 'lsm')))
    add('An IPv4 address with a leading zero.', 'bad-silo-endpoint', value(body_of(1, [node(1, '10.0.0.01:11111', 1)], 'lsm')))
    add('An IPv6 address with a scope ID.', 'bad-silo-endpoint', value(body_of(1, [node(1, '[fe80::1%3]:11111', 1)], 'lsm')))
    add('Archive store -1, as ten bytes.', 'unknown-archive-store', value(body_of(1, ONE, raw_store=-1, store_id=uuid.UUID(STORE_ID).bytes)))
    add('A failure domain which is not UTF-8.', 'non-canonical',
        value(length_delimited(1, entry_bytes(extra=length_delimited(6, b'\xff'))) + tag(2, 0) + varint(1) + STORE_FIELDS))
    add('An empty failure domain written explicitly.', 'non-canonical',
        value(length_delimited(1, entry_bytes(extra=length_delimited(6, b''))) + tag(2, 0) + varint(1) + STORE_FIELDS))
    add('Diskless false written explicitly.', 'non-canonical',
        value(length_delimited(1, entry_bytes(extra=tag(7, 0) + varint(0))) + tag(2, 0) + varint(1) + STORE_FIELDS))
    add('Diskless varint 2.', 'non-canonical',
        value(length_delimited(1, entry_bytes(extra=tag(7, 0) + varint(2))) + tag(2, 0) + varint(1) + STORE_FIELDS))
    add('An empty object-ledger store ID written explicitly.', 'non-canonical',
        value(VALID_BODY + length_delimited(6, b'')))
    add('A drain-all revision of 0 written explicitly.', 'non-canonical',
        value(VALID_BODY + tag(7, 0) + varint(0)))
    add('A failure domain of 64 characters and 65 bytes: the limit counts UTF-8 bytes.', 'long-failure-domain',
        value(body_of(1, [node(1, '10.0.0.1:11111', 1, domain='z' * 63 + '\u00e9')], 'lsm')))
    add('Generation -1 in five bytes rather than ten.', 'non-canonical',
        value(length_delimited(1, length_delimited(1, UUID1) + length_delimited(2, b'10.0.0.1:11111') + tag(3, 0) + b'\xff\xff\xff\xff\x0f')
              + tag(2, 0) + varint(1) + STORE_FIELDS))

    # Two rules each, adjacent in the order of checks: the earlier one's reason.
    add('Two rules: 15 bytes with magic XLNR.', 'truncated', value(VALID_BODY, magic=b'XLNR')[:15])
    add('Two rules: magic XLNR and version 2.', 'bad-magic', value(VALID_BODY, magic=b'XLNR', version=2))
    add('Two rules: version 2 and flags 1.', 'bad-version', value(VALID_BODY, version=2, flags=1))
    add('Two rules: flags 1 and a body shorter than the header says.', 'bad-flags', value(VALID_BODY, flags=1, length=len(VALID_BODY) + 1))
    add('Two rules: a body shorter than the header says and the CRC-32C of other bytes.', 'truncated',
        value(VALID_BODY, length=len(VALID_BODY) + 1, crc=crc32c(b'other')))
    add('Two rules: the CRC-32C of other bytes and a byte after the body.', 'crc-mismatch', value(VALID_BODY, crc=crc32c(b'other'), trailing=b'\x00'))
    add('Two rules: a byte after a body with an unknown field.', 'trailing-bytes', value(VALID_BODY + tag(9, 0) + varint(1), trailing=b'\x00'))
    add('Two rules: an unknown field and no revision.', 'non-canonical', value(body_of(0, ONE, 'lsm') + tag(9, 0) + varint(1)))
    add('Two rules: no revision and archive store 4.', 'zero-revision', value(body_of(0, ONE, raw_store=4, store_id=uuid.UUID(STORE_ID).bytes)))
    add('Two rules: archive store 4 and an ID of 15 bytes.', 'unknown-archive-store', value(body_of(1, ONE, raw_store=4, store_id=uuid.UUID(STORE_ID).bytes[:15])))
    add('Two rules: an archive store ID of 15 bytes and a store prefix of 17 characters.', 'bad-archive-store-id',
        value(body_of(1, ONE, 'lsm', store_id=uuid.UUID(STORE_ID).bytes[:15], prefix='a' * 17)))
    add('Two rules: a store prefix of 17 characters and the zero node identity.', 'bad-archive-store-prefix',
        value(body_of(1, [dict(ONE[0], raw_node=bytes(16))], 'lsm', prefix='a' * 17)))
    add('Two rules: a store prefix of 17 characters and an object-ledger store ID of 15 bytes.', 'bad-archive-store-prefix',
        value(body_of(1, ONE, 'lsm', prefix='a' * 17, object_store_id=uuid.UUID(OBJECT_STORE_ID).bytes[:15])))
    add('Two rules: an object-ledger store ID of 15 bytes and the zero node identity.', 'bad-object-ledger-store-id',
        value(body_of(1, [dict(ONE[0], raw_node=bytes(16))], 'lsm', object_store_id=uuid.UUID(OBJECT_STORE_ID).bytes[:15])))
    add('Two rules: an object-ledger store ID of 15 bytes and a drain-all revision above the revision.', 'bad-object-ledger-store-id',
        value(body_of(1, ONE, 'lsm', object_store_id=uuid.UUID(OBJECT_STORE_ID).bytes[:15], drain_all_revision=2)))
    add('Two rules: a drain-all revision above the revision and no object-ledger store ID.', 'drain-all-above-revision',
        value(body_of(1, ONE, 'lsm', drain_all_revision=2)))
    add('Two rules: a non-zero drain-all revision without an object-ledger store ID and the zero node identity.', 'drain-all-without-object-ledger-store',
        value(body_of(1, [dict(ONE[0], raw_node=bytes(16))], 'lsm', drain_all_revision=1)))
    add('Two rules: an archive store ID of 15 bytes and the zero node identity.', 'bad-archive-store-id',
        value(body_of(1, [dict(ONE[0], raw_node=bytes(16))], 'lsm', store_id=uuid.UUID(STORE_ID).bytes[:15])))
    add('Two rules: node 2, then the zero node identity, which precedes it.', 'bad-node',
        value(body_of(1, [node(2, '10.0.0.2:11111', 1), dict(ONE[0], raw_node=bytes(16))], 'lsm')))
    add('Two rules: node 2, then node 1 with a host name for an endpoint.', 'nodes-not-ascending',
        value(body_of(1, [node(2, '10.0.0.2:11111', 1), node(1, 'silo-0:11111', 1)], 'lsm')))
    add('Two rules: node 1 twice, the second with a host name for an endpoint.', 'repeated-node',
        value(body_of(1, [node(1, '10.0.0.1:11111', 1), node(1, 'silo-0:11111', 1)], 'lsm')))
    add('Two rules: a host name for an endpoint and no generation.', 'bad-silo-endpoint', value(body_of(1, [node(1, 'silo-0:11111', 0)], 'lsm')))
    add('Two rules: generation -1 and ordinal -1.', 'non-positive-generation', value(body_of(1, [node(1, '10.0.0.1:11111', -1, ordinal=-1)], 'lsm')))
    add('Two rules: ordinal -1 and a node retired above the revision.', 'negative-ordinal',
        value(body_of(1, [node(1, '10.0.0.1:11111', 1, ordinal=-1, retired=2)], 'lsm')))
    add('Two rules: a node retired above the revision with a failure domain of 65 bytes.', 'retired-above-revision',
        value(body_of(1, [node(1, '10.0.0.1:11111', 1, retired=2, domain='z' * 65)], 'lsm')))
    add('Two rules: a failure domain of 65 bytes on a diskless node carrying an ordinal.', 'long-failure-domain',
        value(body_of(1, [node(1, '10.0.0.1:11111', 1, ordinal=0, domain='z' * 65, diskless=True)], 'lsm')))
    add('Two rules, node by node: node 1 retired above the revision, then the zero node identity.', 'retired-above-revision',
        value(body_of(1, [node(1, '10.0.0.1:11111', 1, retired=2), dict(ONE[0], raw_node=bytes(16))], 'lsm')))
    add('Archive store 2^32 + 3 as a varint, whose low 32 bits are the memory store\'s 3.', 'non-canonical',
        value(length_delimited(1, entry_bytes()) + tag(2, 0) + varint(1) + tag(3, 0) + varint((1 << 32) + 3)
              + length_delimited(4, uuid.UUID(STORE_ID).bytes) + length_delimited(5, STORE_PREFIX.encode())))
    add('An empty store prefix written explicitly, with no archive store.', 'non-canonical',
        value(length_delimited(1, entry_bytes()) + tag(2, 0) + varint(1) + length_delimited(5, b'')))

    # Hand-made fuzz seeds, kept as vectors so a corpus merge never drops them; NodeRegistryVectorTests names each.
    add('Seed unknown-length-delimited-field: an unknown field 15, length-delimited, after the body.', 'non-canonical',
        value(VALID_BODY + length_delimited(15, b'abc')))
    add('Seed group-wire-type: a field of the group wire type after the body.', 'non-canonical', value(VALID_BODY + tag(9, 3)))
    add('Seed eleven-byte-varint: a revision varint of eleven bytes.', 'non-canonical', value(tag(2, 0) + b'\xff' * 10 + b'\x01'))
    return r


def write(name, description, cases):
    with open(os.path.join(HERE, name), 'w', newline='\n', encoding='utf-8') as file:
        json.dump({'description': description, 'cases': cases}, file, indent=2)
        file.write('\n')


def main():
    write('registries.json', 'Node registry values, CLNR version 1 (orleans.md \u00a77.3): decoding hex yields the value, and '
          'encoding the value yields hex.', accepted())
    write('registry-rejections.json', 'Node registry values a decoder rejects, each breaking one rule of orleans.md '
          '\u00a77.3, or, where its name says so, two rules adjacent in the order of checks, with the reason code of the '
          'first rule it breaks; every value long enough to hold one carries the CRC-32C of its header and body, but '
          'where its name says otherwise.', rejections())


if __name__ == '__main__':
    main()
