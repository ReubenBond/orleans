# LedgerStore vectors

Language-neutral golden vectors for the LedgerStore's durable formats
([LedgerStore](../../docs/spec/components/ledgerstore.md#7-formats)): the node file, the `CLSC` catalog checkpoint,
the WAL record, and the `CLSI` slot-index checkpoints. Clog's test suites read these files directly, and the vectors
change with the formats they pin.

Integers in the encodings are little-endian. 64-bit integers in the vectors are decimal strings, and byte strings are
lowercase hexadecimal. A checkpoint reference's `crc` is 8 hexadecimal digits. Where a file or slot is longer than its
content, `hex` holds its bytes through its last non-zero byte and the rest is zero.

## Node file

The node file, `wal/node`, holds the node identity as exactly 32 lowercase hexadecimal ASCII digits, the UUID's `N`
format, with no terminator, naming a non-empty UUID.

## Catalog checkpoint, `CLSC` version 1

A catalog checkpoint is named `{file:D20}-{offset:D20}.clsc` by its cut. A 16-byte header (magic `CLSC`, `u32` version
1, `u32` body length, `u32` CRC-32C of the body), then the body: the cut, `u64` file and `u64` offset; `u32` next handle
(at most 2<sup>24</sup> − 1); `u32` replica count; per replica in ascending handle order, 24 bytes: `u32` handle, `u32`
window K which the replica's creating request recorded, `u64` first file, and `u64` ledger ID; `u32` tombstone count;
and the dropped ledger IDs, ascending `u64`s. The cut's file is non-zero and the cut is the one the name gives; handles
lie below the next handle; windows lie in 1 to 2<sup>31</sup> − 1; first files are non-zero and at most the cut's file;
ledger IDs are non-zero and distinct; and tombstones are non-zero, strictly ascending, and name no listed replica's
ledger.

## WAL record

A record is the `u64` key `(handle << 40) | slot` followed by the body. A slot record has a handle below the store
handle, 2<sup>24</sup> − 1, and the slot's DATA, a non-empty value, as its body. A store record has the store handle
and its kind in the slot field: 1 for a create, 2 for a drop, and 3 for a seal. A create's body is 16-byte pairs, each a
`u64` ledger ID, a `u32` handle, and the `u32` window K of the request which creates the replica, whose handles run
consecutively from the catalog's next handle; a drop's body is the `u64` ledger ID it drops, which leaves the
ledger's tombstone whether or not the node holds its replica; and a seal's body is 12 bytes, the `u64` ledger ID and
`u32` handle of the replica it seals. A record shorter than its 8-byte key, a slot record with an empty value, or a slot
record whose handle is at or above the catalog's next handle, is damage; so is a store record of another kind, a create
whose body is not a positive multiple of 16 bytes, a drop whose body is not 8 bytes, a seal whose body is not 12 bytes,
a ledger ID of 0, a create's or seal's handle at or above the store handle, or a create's window of 0 or above
2<sup>31</sup> − 1.

## Slot-index checkpoints, `CLSI` version 1

A leaf is a 24-byte header (flags, state, file-index, and residual widths, `u16` file count and exception count,
`i64` intercept and slope), the file table, a presence bitmap unless every slot is present, the bit-packed states
(0 DATA, 1 Faulty; a state width of at most 1), file indexes, and residuals, then 9-byte exceptions. Leaves list their
present entries as runs: entry `first + k` of a run has the run's `state` (`data` or `faulty`), file
`file + k × fileStep`, and page `page + k × pageStep`; `count`, `fileStep`, and `pageStep` default to 1, 0, and 0, and
every other slot is Missing.

An object reference is `u64` generation (0 is null; every generation lies below 2<sup>63</sup>), `u64` offset, `u32`
length, and `u32` CRC-32C; an interior node is 256 references. A generation file holds the generation's leaves, then its
interior nodes bottom-up, padded to a multiple of S. A root slot of S bytes holds the CRC-32C of bytes 4 to S − 1, magic
`CLSI`, `u32` version 1, `u32` height, `u64` generation, the replay start (`u64` file and offset), the `CorruptionEnd`
file (`u64`) and page (`u32`), `u32` flags (bit 0: the `CorruptionEnd` is set; bit 1: the replica is sealed, its seal
record preceding the checkpoint's cut; no other bit), and the root reference, then zero.

## Files

| File | Contract |
| --- | --- |
| `node-files.json` | Each node file's identity and the exact bytes it encodes to; decoding the bytes yields the identity |
| `node-file-rejections.json` | Node files a decoder rejects, each named by the first rule it breaks; a file breaking both rules pins their order |
| `catalog.json` | Each catalog checkpoint's cut, file name, and catalog, and the exact bytes they encode to; decoding the bytes under the file name yields the cut and the catalog |
| `catalog-rejections.json` | Catalog checkpoints a decoder rejects, each with the file name it is read under and named by the first rule it breaks: first one per reason, breaking that rule of the `replicas-and-tombstones` checkpoint, then one per pair of adjacent rules which a checkpoint can break together, pinning their order |
| `records.json` | Each slot record's handle, slot, and DATA value, and its exact bytes, which hold DATA below `nextHandle`; each create record's pairs with their windows, each drop record's ledger, and each seal record's ledger and handle, and its exact bytes |
| `record-rejections.json` | Records which are damage, each named by the first rule it breaks; a store record breaking two adjacent rules pins their order |
| `leaves.json` | Each leaf's entries and the exact bytes the canonical encoder produces; decoding yields the entries |
| `leaf-rejections.json` | Leaves a decoder rejects, each named by the first rule it breaks; a leaf breaking two adjacent rules pins their order |
| `generations.json` | A generation as the checkpoint writer lays it out with S = 4096: its leaves and their references, its interior nodes with their non-null children, the generation file, and `slots.root`; loading the files yields the leaves and the replay start |
| `roots.json` | Each root slot's fields, its seal among them, and the exact slot it encodes to, for S = 4096 and S = 65536; decoding yields the fields |
| `root-rejections.json` | Root slots which are not usable, each named by the first rule it breaks; a slot breaking two adjacent rules pins their order |
| `tree-rejections.json` | Trees whose root is not usable, each a `slots.root` whose slot 0 holds the root and one generation file of the root reference's generation, in the layout of `generations.json`, named by the first rule an object of the tree breaks, met depth-first in child order; a tree breaking two adjacent rules of one object pins their order |

Node file rejection reasons are `digits` and `empty-uuid`. Catalog rejection reasons are `truncated`, `bad-magic`,
`bad-version`, `length-mismatch`, `crc-mismatch`,
`zero-cut-file`, `cut-mismatch`, `next-handle-out-of-range`, `count-overflow`, `handle-order`, `handle-not-below-next`,
`window-out-of-range`, `zero-first-file`, `first-file-above-cut`, `zero-ledger`, `duplicate-ledger`,
`tombstone-order`, `tombstone-live`, and `trailing-bytes`. Record reasons are `record-truncated`, `empty-slot-record`,
`handle-not-allocated`, `store-kind-unknown`, `create-body-length`, `drop-body-length`, `seal-body-length`,
`zero-ledger`, `handle-out-of-range`, and `window-out-of-range`. Leaf reasons are `truncated`, `reserved-flags`,
`width-out-of-range`, `file-count-out-of-range`, `file-table-order`, `bitmap-empty-or-full`, `too-many-exceptions`,
`length-mismatch`, `exception-order`, `exception-absent-slot`, `exception-residual`, `file-index-out-of-range`,
`page-out-of-range`, and `nonzero-padding`. Root reasons are `crc-mismatch`,
`bad-magic`, `bad-version`, `nonzero-padding`, `height-out-of-range`, `height-root-mismatch`, `generation-zero`,
`generation-out-of-range`, `unknown-flags`, `corruption-end-without-flag`, and `null-reference-fields`. Tree
reasons are `node-length`, `leaf-beyond-address-space`, `leaf-length`, `generation-after-root`, `generation-missing`,
`outside-file`, `overlap`, `crc-mismatch`, `null-reference-fields`, and `node-empty`.

The node file, catalog, store record, leaf, and root decoders and tree loading report each rejection as an
`InvalidDataException` carrying its reason in `Data["orleans.durable.storage.decode.reason"]`; the tests and the fuzz
oracles match them. A short record, an empty slot record, or a slot record of an unassigned handle is classified as
damage without a reason, so those reasons document the rule. The tests also check that the rejection vectors cover every
node file, catalog, record, leaf, and tree reason, and every root reason but `truncated`: a root vector is a whole slot,
never shorter than its fields.
