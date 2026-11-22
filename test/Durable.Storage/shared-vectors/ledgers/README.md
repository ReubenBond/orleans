# Ledgers vectors

Language-neutral golden vectors for the Ledgers layer's durable formats
([Ledgers](../../docs/spec/components/ledgers.md#7-formats)): the ledger map, `CLLM` version 1, and the batch, `CLLB`
version 1; and the object-store ledgers' names, slots, and store record
([object-store ledgers](../../docs/spec/components/object-ledgers.md#7-formats)). Clog's test suites read
these files directly, and the vectors change with the format they pin.

Integers in the encodings are little-endian. Integers of 64 bits and more in the vectors are decimal strings, a
CRC-32C is 8 hexadecimal digits, and byte strings are lowercase hexadecimal.

## Ledger map, `CLLM` version 1

A value of length `L` is the magic `CLLM`, the version 1 (`u32`), the epoch, `NextLedgerId`, and retirement floor
(each `u64`), and the ledger count (`u32`); then the ledgers, in ascending ID order; then the CRC-32C of bytes 0 to
`L − 5` (`u32`). Each ledger is its ID (`u64`); its range's depth (`u32`) and `⌈depth / 8⌉` prefix bytes; `N` (`u8`)
and its replicas' node identities; `K` (`u32`); its primary's node and sequencer identities; and its status (`u8`: 0
open, 1 closed), followed when closed by `T` (`u64`). Identities are their UUIDs' 16 bytes, big-endian.

`maps.json` holds values with their `epoch`, `nextLedgerId`, `retirementFloor`, and `ledgers`: each ledger's `id`,
range `depth` and `prefix`, `replicas` and `primary` and `sequencer` as UUIDs, `k`, `status` (`open` or `closed`), and,
when closed, `tail`. Decoding `hex` yields the value, and encoding the value yields `hex`.

`map-rejections.json` holds values which each break one rule, unless a case names two, with the `reason` code of the
first rule a decoder checks; [ledgers §7](../../docs/spec/components/ledgers.md#7-formats) lists the rules in that
order. Every rejection long enough to hold a CRC carries the CRC-32C of its bytes, except the `crc-mismatch` case.

## Batch, `CLLB` version 1

A batch of length `L` is the magic `CLLB`, the version 1 (`u32`), the flags (`u8`: bit 0 the segment start), and three
varints (unsigned LEB128, at most 10 bytes, shortest form): the segment offset, the ordinal base, and the row count;
then the rows; then the CRC-32C of bytes 0 to `L − 5` (`u32`). Each row is its flags (`u8`: the kind in bits 0 and 1,
0 `Append`, 1 `Replace`, 2 `Delete`, and *defining* in bit 2); for a defining row its key's length (varint) and bytes,
its version before: the base's length (`u8`) and bytes and the count (varint), and its token's length (`u8`) and bytes;
for a referencing row its ordinal (varint); and its body's length (varint) and bytes. A defining row's ordinal is the
batch's ordinal base plus the defining rows before it in the batch.

`batches.json` holds batches with their segment state (`segmentStart`, `segmentOffset`, `ordinalBase`) and their rows:
each row's `kind`, `defining`, and `ordinal`, a defining row's `key`, `versionBase`, `versionCount`, and `token`, and
its `body`. Decoding `hex` yields them, and encoding them yields `hex`.

`batch-rejections.json` holds batches which each break one rule, or two where the case's name says so, with the
`reason` code of the first rule a decoder checks, which it reports in `Data["orleans.durable.storage.decode.reason"]`;
[ledgers §7](../../docs/spec/components/ledgers.md#7-formats) lists the batch rules in that order. Every rejection
which holds a CRC carries the CRC-32C of its bytes, except the `crc-mismatch` case.

## Segments

`segments.json` holds runs of batches at consecutive slots from `slot`, the first at a slot where a segment starts,
with the rows a segment decoder resolves from them: each row's `slot`, `index`, `key`, `kind`, version after it
(`versionBase`, `versionCount`), its key's `token`, and its `body`. A rewind to any batch with the start flag resolves
the same rows from there on.

`segment-rejections.json` holds runs of batches which a segment decoder rejects: `batch` is the index of the first batch
which breaks a rule, batch rules before segment rules, and `reason` that rule's reason code, among them a batch rule in
one batch beside a segment rule in an earlier batch, and `count-overflow`.

## Object-store ledgers

`object-names.json` holds slots with their reverse keys, `(2^63 − 2) − slot` as 20 decimal digits, and strings which
are not reverse keys; slots in `listingOrder.slots`, whose names in ordinal order list them as `listingOrder.listed`;
and each marker's and slot's name under `ledgers/l/{id:020}/` relative to the cluster's store prefix, with the store's
record's name, `store`.

`object-slots.json` holds a slot object's `classification` from its properties (`length`, and `crc32c`, the CRC-32C its
create recorded, or null) and its content: `Seal` when empty, `Batch` when the content matches the length and the
CRC-32C, `Corrupt` otherwise; and a writer's `settlement` of a create from the properties it `observed` at the slot,
null when absent: `Repeat`, `Sealed` when empty, `Written` for its own length and CRC-32C, `Corrupt` otherwise.

`object-store-records.json` holds store records, two lines, `id={32 lowercase hexadecimal digits}` and
`layout={per-slot|append}`, with their `id` and `layout`; decoding `hex` yields them, and encoding them yields `hex`.
`object-store-record-rejections.json` holds records which each break one rule, with the `reason` of the first a decoder
checks: `bad-line`, `bad-id`, then `bad-layout`.

`object-extents.json` holds valid `CLOE` version 1 tombstones (`tombstones`, 17-byte `SEAL` frames) and extents
(`extents`, one or more appends of `DATA` frames with an optional final `ROLL` frame, each append ending in its 40-byte
`CLOE` trailer). `object-extent-rejections.json` holds rejected tombstones and extents with the `reason` code of the
first rule a decoder checks (`truncated`, `trailing-bytes`, `bad-magic`, `bad-version`, `crc-mismatch`, `bad-slot`,
`no-frames`, `bad-offset`, `bad-kind`, `bad-length`, `slot-mismatch`, `roll-not-last`).
