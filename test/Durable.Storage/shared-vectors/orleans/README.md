# Orleans vectors

Language-neutral golden vectors for the Orleans composition's durable format
([Orleans §7.3](../../../docs/spec/compositions/orleans.md#73-node-registry-clnr-v1)): the node registry, `CLNR`
version 1. Clog's test suites read these files directly, and the vectors change with the format they pin.

Integers in the header are little-endian. Integers of 64 bits in the vectors are decimal strings, UUIDs are in their
dashed form, and byte strings are lowercase hexadecimal.

## Node registry, `CLNR` version 1

A value is a 16-byte header, the magic `CLNR`, the version 1 and the flags 0 (each `u16`), the body's length (`u32`),
and the CRC-32C of header bytes 0 to 11 and the body (`u32`); and then the body, the canonical Protocol Buffers
encoding of `orleans.durable.storage.v1.NodeRegistry`. Node identities are their UUIDs' 16 bytes, big-endian, and the
archive store's ID is its 128-bit value's 16 bytes, big-endian.

`registries.json` holds values with their `revision`, their `archiveStore` (its `kind`, `lsm`, `logs`, or `memory`,
`ARCHIVE_STORE_LSM`, `ARCHIVE_STORE_LOGS`, and `ARCHIVE_STORE_MEMORY`, and its `id`, 32 lowercase hexadecimal digits as
the store's record writes it, or `null` when none is recorded), and their `nodes`: each node's `node`, `siloEndpoint`,
`generation`, `ordinal` (`null` when absent), `retiredRevision`, and `failureDomain` (`null` when none is configured).
Decoding `hex` yields the value, and encoding the value yields `hex`. One value records each kind, which pins each
kind's value against the Archive's `ArchiveStoreKind`.

`registry-rejections.json` holds values which each break one rule, or two, with the `reason` code the decoder reports
in `Data["orleans.durable.storage.decode.reason"]`; the rules, in the order a decoder checks them, are §7.3's table of
reasons. Some cases sit at a rule's boundary, and those whose name begins "Two rules" break two rules adjacent in that
order and carry the reason of the one checked first, which pins the order. Every rejection which holds a header carries
the CRC-32C of its header and body unless its name says otherwise, so each breaks only the rules it names.

`generate_registry.py` writes both files from an independent reading of §7.3: it serializes each valid body with the
Protocol Buffers library from §7.3's schema, and writes each non-canonical body byte by byte. The hand-made inputs which
seed the `orleans-registry` fuzz target are vectors here too, each named `Seed {name}: …`, so a campaign's merge, which
owns `fuzz/corpus/orleans-registry` and drops the inputs a seed already covers, never loses them. Run it from the
repository root after `python -m pip install -r tests/shared-vectors/orleans/requirements.txt`.
