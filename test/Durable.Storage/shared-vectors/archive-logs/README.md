# Archive-log vectors

Language-neutral golden vectors for archive logs' durable formats: the position token
([archive logs §7.2](../../docs/spec/components/archive-logs.md#72-tokens)) and the Azure layout's metadata and index
tag ([archive logs §7.3](../../docs/spec/components/archive-logs.md#73-azure-layout)). Clog's test suites read these
files directly, and the vectors change with the format they pin. The store's record, `durable-storage/store`, is pinned
with every store's record in [`archive/store-records.json`](../archive/README.md#store-record).

Integers of 64 bits in the vectors are decimal strings, and byte strings are lowercase hexadecimal.

## Tokens

A token is the position `(g, r, P, b)`: `Origin`, `(0, 0, 0, 0)`, is the empty token, and any other is the version
byte 1 followed by `g`, `r`, `P`, and `b` as unsigned LEB128 varints in their shortest form, `g` and `P` below `2^56`,
`r` and `b` below `2^35`.

`tokens.json` holds, in `cases`, positions of both layouts with the `hex` they encode to: each case's `generation`,
`base_rows`, `length`, and `units`. Decoding `hex` yields the position, and encoding the position yields `hex`. Its
`rejections` hold tokens which each break one rule, with the `reason` code the decoder reports in
`Data["orleans.durable.storage.decode.reason"]`. The rules, in the order a decoder checks them, which the cases named "…
first", breaking two, show:

| Reason | Rule |
| --- | --- |
| `bad-version` | The first byte is the version 1 |
| `bad-varint` | Each of `g`, `r`, `P`, and `b` is a terminated varint of at most 64 bits in its shortest form |
| `trailing-bytes` | Nothing follows `b` |
| `zero-generation` | `g` is at least 1 |
| `out-of-range` | `g` and `P` are below `2^56`, and `r` and `b` below `2^35` |
| `bad-units` | `b` is 0 exactly when `P` is 0 |
| `units-above-length` | `b` is at most `P` |

## Azure metadata

`metadata.json` holds, in `cases`, the metadata each write kind leaves on the write-ahead blob (`blob` `wal`) or a
checkpoint (`checkpoint`): the `write` (`append-run`, `replace`, `delete`, or `checkpoint`), the `log`'s name, its
`key` and `keyHash` (the key's xxHash64, seed 0), the store's `format`, the `durable-gen` `tag`, and the values a reader
takes from it (`generation`, `checkpoint`, `kind`, `tombstone`, `baseMd5`, the MD5 of the generation's base, and
`base_rows`, the rows of its key the base folds), with
the `metadata` as name and value pairs in the order the store writes them. Reading the metadata with the tag yields
the values, and, where `write` is not null, the store's write produces exactly the metadata; a null `write` is
metadata no store writes which a reader accepts, such as names in other cases.

`metadata-rejections.json` holds metadata which each break one rule, with the `reason` code the reader reports in
`Data["orleans.durable.storage.decode.reason"]`; the cases named "… first" break two, and show which a reader meets
first. The rules, in the order a reader checks them:

| Reason | Rule |
| --- | --- |
| `duplicate-name` | No name is present twice, in any case |
| `bad-format` | `format` is present and non-empty |
| `bad-checkpoint` | `checkpoint`, when present, is non-empty |
| `bad-checkpoint_offset` | `checkpoint_offset` is `0`, present exactly when `checkpoint` is |
| `bad-wal_generation` | `wal_generation` is a decimal `u64` of at least 1, with no leading zero |
| `bad-durable-gen` | The tag, when read, is 20 decimal digits naming a generation of at least 1 |
| `bad-durable_kind` | `durable_kind`, when present, is `replace` or `delete` |
| `bad-durable_tomb` | `durable_tomb`, when present, is 16 lowercase hexadecimal digits |
| `bad-durable_base_md5` | `durable_base_md5`, when present, is 32 lowercase hexadecimal digits |
| `bad-durable_base_rows` | `durable_base_rows`, when present, is a decimal from 1 to `2^35 - 1`, with no leading zero |
| `bad-durable_key` | `durable_key` is 16 lowercase hexadecimal digits |
| `kind-mismatch` | `checkpoint`, `durable_kind`, `durable_base_md5`, and `durable_base_rows` are all present or all absent |
| `opener-missing` | `wal_generation` is 1 when no opener created the generation |
| `tomb-mismatch` | `durable_tomb` is present only with `durable_kind = delete` |
| `generation-mismatch` | `wal_generation` is the tag's generation |
| `checkpoint-mismatch` | `checkpoint` names the log's own checkpoint of `wal_generation` |
| `key-mismatch` | `durable_key` is the key's hash |

A checkpoint carries `format` and `durable_key` alone, checked by the same rules.

`chunk-bases.json` holds, in `cases`, the chunk layout's bases (§7.4): each case's `keyHash`, `opener` (`none`,
`Replace`, or `Delete`), `rows`, `tombstone` (16 hexadecimal digits, or null), and `content`, with the `hex` of the
whole base, its `CLAB` header and content. Its `rejections` each break one rule, with the `reason` the decoder reports;
the cases named "… first" break two. The rules, in the order a decoder checks them:

| Reason | Rule |
| --- | --- |
| `truncated` | The base holds at least the 60-byte header |
| `bad-magic` | It begins `CLAB` |
| `bad-version` | Its version is 1 |
| `bad-crc` | The header's last 4 bytes are the CRC-32C of the 56 before them |
| `bad-kind` | The kind is 0, 1, or 2 |
| `bad-flags` | Only flag bit 0 is set, and the reserved byte is 0 |
| `tomb-mismatch` | The tombstone flag is set only on a `Delete`, and the tombstone is 0 without it |
| `bad-rows` | Rows are 0 for kind 0, and from 1 to `2^35 - 1` otherwise |
| `none-with-content` | Kind 0 records no content |
| `bad-length` | The recorded length is the content's |
| `bad-md5` | The content hashes to the recorded MD5 |

`chunks.json` holds, in `cases`, chunks: each case's `content` and the `hex` of the chunk, the content followed by its
CRC-32C in 4 little-endian bytes. Its `rejections` hold a chunk with no content before its trailer (`truncated`) and
ones whose trailer is not the content's CRC-32C (`bad-crc`).

The `archive-log-token`, `archive-log-chunk-base`, `archive-log-chunk`, and `archive-log-metadata` fuzz targets' oracles
read the same rules from their inputs, and these files seed them.

`generate_vectors.py` writes the five files from its own reading of archive logs §7, sharing no code with the codecs.
Run it from the repository root after installing its pinned dependency:

```powershell
python -m pip install -r test/Durable.Storage/shared-vectors/archive-logs/requirements.txt
python test/Durable.Storage/shared-vectors/archive-logs/generate_vectors.py
```
