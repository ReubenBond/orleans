# Archive LSM vectors

Language-neutral golden vectors for the archive's durable formats: the LSM's manifest, `CLAM` version 1, its run
object, `CLAR` version 1, and its version base ([Archive LSM](../../../docs/spec/components/archive-lsm.md#7-formats)),
and every store's record ([Archive §7](../../../docs/spec/components/archive.md#7-formats)). Clog's test suites read
these files directly, and the vectors change with the format they pin.

Integers in the encodings are little-endian. Integers of 64 bits in the vectors are decimal strings, and byte strings
are lowercase hexadecimal.

## Manifest, `CLAM` version 1

A manifest is a 16-byte header, the magic `CLAM`, the version 1, the body's length, and the CRC-32C of the body (each
`u32`), and then the body: the epoch (`u64`), `DrainedBelow` (`u64`), the watermark count (`u32`) and each entry, in
ascending ledger order, as its ledger (`u64`), watermark (`u64`), and completion flag (`u8`, 0 or 1), then the section
count (`u32`) and each section, in order of object name and then offset. A section is its object's name (a `u8` length
and the name's ASCII bytes), its offset and length (`u64` each), its partition's depth (`u32`) and its
`⌈depth / 8⌉` prefix bytes, its lowest and highest tags (each a ledger `u64`, a slot `u64`, and an index `u32`), and its
tail's offset and length (`u64` each): the run object's block indexes, Bloom filters, and trailer, which end it.

`manifest.json` holds manifests with their values: the `epoch`, `drainedBelow`, the `watermarks` (each its `ledger`,
`watermark`, and `complete`), and the `sections` (each its `object`, `offset`, `length`, `partition` as `prefix` and
`depth`, `minTag` and `maxTag` as `ledger`, `slot`, and `index`, and `tail` as `offset` and `length`).
Decoding `hex` yields the value, and encoding the value yields `hex`.

`manifest-rejections.json` holds manifests which each break one rule, with the `reason` code the decoder reports in
`Data["orleans.durable.storage.decode.reason"]`. The rules, in the order a decoder checks them:

| Reason | Rule |
| --- | --- |
| `truncated` | The manifest holds its 16-byte header |
| `bad-magic` | The magic is `CLAM` |
| `bad-version` | The version is 1 |
| `length-mismatch` | The body's length is the header's |
| `crc-mismatch` | The CRC-32C of the body is the header's |
| `body-truncated` | Each field ends at or before the body's end |
| `zero-epoch` | The epoch is at least 1 |
| `zero-drained-below` | `DrainedBelow` is at least 1 |
| `watermark-below-drained` | Each watermark's ledger is at least `DrainedBelow` |
| `watermarks-unordered` | Each watermark's ledger is above the previous entry's |
| `slot-out-of-range` | Each watermark is at most 2<sup>63</sup> − 1 |
| `bad-complete-flag` | Each completion flag is 0 or 1 |
| `empty-watermark` | No entry is at slot 0 and incomplete |
| `complete-at-drained-below` | The entry of `DrainedBelow`, if any, is incomplete |
| `bad-object-name` | Each section's object name is an object name |
| `bad-section-range` | Each section holds at least one byte and ends at or below 2<sup>63</sup> − 1 |
| `bad-partition` | Each partition is at most 524,280 bits deep and sets no bit past its depth |
| `bad-tag` | Each tag's ledger is at least 1, its slot at most 2<sup>63</sup> − 2, and its index at most 2<sup>31</sup> − 1 |
| `tags-unordered` | Each section's lowest tag is at most its highest |
| `bad-tail-range` | Each tail holds at least one byte and ends at or below 2<sup>63</sup> − 1 |
| `sections-unordered` | Each section lies after the previous one, by object name and then offset |
| `trailing-bytes` | The last section ends at the body's end |

The body's fields are checked in layout order, each first to end at or before the body's end (`body-truncated`) and
then against its own rule. Every rejection which holds a header carries its body's length and CRC-32C, except the
`length-mismatch` and `crc-mismatch` cases, so each breaks only its own rule.

## Run object, `CLAR` version 1

A run object is its sections' data blocks, back to back from offset 0, then each section's block index and Bloom
filter, in section order, then its trailer. The tail, from the first block index to the end, is what a `SectionRef`
records. Each block, block index, Bloom filter, and the trailer ends with the CRC-32C of its own bytes before it. A key
is a `u16` length and its bytes, a tag a ledger `u64`, a slot `u64`, and an index `u32`, and a partition a depth `u32`
and its `⌈depth / 8⌉` prefix bytes.

- A row is its key, tag, kind (`u8`), and payload length (`u32`) and bytes. A data block is its rows, up to its CRC-32C.
- A block index is the block count (`u32`) and each block's length (`u32`), continuation flag (`u8`, 1 when its first
  key is the previous block's last), and first key.
- A Bloom filter is its hash count (`u8`) and its bytes. A key sets bits `(h₁ + i·h₂) mod m` for `i` below the hash
  count, `h₁` and `h₂` the low and high halves of its XXH3 64-bit hash (seed 0), and bit `j` is bit `j mod 8` of byte
  `⌊j / 8⌋`.
- A trailer is the magic `CLAR`, the version 1 (`u32`), the section count (`u32`), the metadata offset (`u64`), where
  the first block index starts, and each section's blocks' offset and length (`u64` each), partition, lowest and
  highest tags, row count (`u64`), and the offsets of its block index and Bloom filter (`u64` each); the condition, its
  kind (`u8`) then for a drain (0) the ledger and watermark (`u64` each), or for a compaction (1) the input count
  (`u32`) and each input's object name (a `u8` length and ASCII bytes) and offset (`u64`); and the trailer's own length
  (`u32`).

`run-objects.json` holds objects the writer encodes, with their values: the `blockBytes` and `bloomBitsPerKey` the
writer used, the `condition` (`drain` with its `ledger` and `from`, or `compaction` with its `inputs`, each an `object`
and `offset`), and the `sections`, each its `partition` (`prefix` and `depth`), the `hashCount` its filter records, its
`blocks` count, and `rows` in order, each its `key`, `tag`, `kind` (`Append`, `Replace`, or `Delete`), and `payload`.
Decoding `hex` yields the value, and, where `encoded` is true, the writer encodes the value as `hex`: it starts a block
before a row which would make it longer than the block size unless the block is empty, and sizes each filter at
`⌈keys × bits per key / 8⌉` bytes with `max(1, round(bits per key × ln 2))` hashes, rounding half to even.

`run-object-rejections.json` holds objects which each break one rule, with the `reason` code the decoder reports in
`Data["orleans.durable.storage.decode.reason"]`, and lists in `order` every reason in the order of archive §7's table. A
reader reads the trailer from the object's last 8 bytes, then each section's block index and Bloom filter, then each
section's blocks, checking each part's CRC-32C before its fields. Every part of a rejection carries its CRC-32C, except
in the `crc-mismatch` cases, so each breaks only its own rule; the cases named "Two rules" break two, and show which a
reader meets first.

`generate_run_objects.py` writes both files and the `archive-run-object` fuzz corpus from its own reading of archive §7,
sharing no code with the codec. The corpus is hand-made seeds, a step from each accepted object: its last byte flipped,
a byte before or after it, and a bit flipped a third of the way in; none is a vector, and campaigns grow the corpus from
them. Run it from the repository root after installing its pinned dependency:

```powershell
python -m pip install -r tests/shared-vectors/archive/requirements.txt
python tests/shared-vectors/archive/generate_run_objects.py
```

## Store record

A store's record is strict UTF-8 lines separated by a line feed, with no terminator, each `{field}={value}`: `id`, the
store ID as 32 lowercase hexadecimal digits, not all zero; `layout`, non-empty; the store's own fields, each a key of
`[a-z0-9-]`; and the caller's parameters, each `caller.` and such a key; each group in ordinal order of its keys.

`store-records.json` holds records with their values: the `id`, `layout`, the store's `fields`, and the caller's
`parameters`. Decoding `hex` yields the value, and encoding the value yields `hex`.

`store-record-rejections.json` holds records which each break one rule, with the `reason` code the decoder reports in
`Data["orleans.durable.storage.decode.reason"]`. The rules, in the order a decoder checks them:

| Reason | Rule |
| --- | --- |
| `bad-utf8` | The bytes are strict UTF-8, with no overlong encoding or surrogate |
| `bad-line` | Each line is a key of `[a-z0-9-]`, or `caller.` and one, an equals sign, and a value |
| `duplicate-field` | No line repeats an earlier line's key |
| `bad-id` | The first line is `id` with 32 lowercase hexadecimal digits, not all zero |
| `bad-layout` | The second line is `layout` with a non-empty value |

The `archive-store-record` fuzz target's oracle, `StoreRecordRules`, reads the same rules from the bytes, and both files
seed it.

## Version base

The LSM's version base is the tag of its prefix's last row: the ledger (`u64`), slot (`u64`), and index (`u32`),
little-endian, 20 bytes. `version-bases.json` holds tags, each its `ledger`, `slot`, and `index` as decimal strings,
with the `hex` they encode to.
