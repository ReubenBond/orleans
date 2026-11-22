# Journals vectors

Language-neutral golden vectors for the Journals layer's formats
([Journals](../../docs/spec/components/journals.md#7-formats)): the entry record, the version's text form, and the
row key. Clog's test suites read these files directly, and the vectors change with the format they pin. They were
written from an independent reading of the format, with xxhash32, CRC-32C, the varint, and base64url implemented apart
from the C# codec.

Integers in the encodings are little-endian, but for the row key's hash, which is big-endian. Integers of 64 bits and
more in the vectors are decimal strings, and byte strings are lowercase hexadecimal.

## Row key

A journal key's row key is `xxhash32` of the key's strict UTF-8, seed 0, as 4 big-endian bytes, then the UTF-8.
`row-keys.json` holds keys with their row keys' `hex`.

## Entry record

A record is the CRC-32C of its row key and then every byte of the record after the CRC (`u32`); the kind (`u8`: 0
`Append`, 1 `Replace`, 2 `Delete`, 3 `Expired`); the expected version: the base's length (`u8`, at most 32), the base's
bytes, and the count as an unsigned LEB128 varint in its shortest form, at most 10 bytes; a `Delete`'s deletion time in
UTC ticks (`i64`); and the body: an `Append`'s payload, non-empty, or a `Replace`'s snapshot, possibly empty, the rest
of the record, and nothing for a `Delete` or an `Expired`. The record carries no format version.

Each record carries the `rowKey` it was written for, in hexadecimal, which its CRC-32C covers. `entries.json` holds
records with their `kind` and `expectedVersion` (`base` and `count`); an `Append` or `Replace` its `payload`; and a
`Delete` its `deletedAt` in ticks. Decoding `hex` under `rowKey` yields the fields, and encoding the fields for `rowKey`
yields `hex`. Each record also names the row the primary writes for it
([Journals §4.2](../../docs/spec/components/journals.md#42-entries-and-rows)): its `rowKind` (`Append`, `Replace`,
or `Delete`, an `Expired`'s among them), its `versionBefore`, the expected version, and its `rowBody`: an `Append`'s or
`Replace`'s payload, a `Delete`'s tombstone (its deletion time in UTC ticks, `i64`), and nothing for an `Expired`.

`entry-rejections.json` holds records a decoder rejects under their `rowKey`, with the `reason` code it reports in
`Data["orleans.durable.storage.decode.reason"]`. The rules, in the order a decoder checks them:

| Reason | Rule |
| --- | --- |
| `truncated` | The record holds its CRC, its kind, and its base's length: 6 bytes |
| `crc-mismatch` | The CRC-32C matches the row key and the record's bytes after the CRC |
| `unknown-kind` | The kind is 0 to 3 |
| `base-too-long` | The base's length is at most 32 |
| `field-truncated` | The base, and a `Delete`'s 8-byte deletion time, lie within the record |
| `bad-varint` | The count is a shortest varint of at most 10 bytes which fits a `u64` and ends within the record |
| `bad-deletion-time` | A `Delete`'s deletion time is 0 to 3,155,378,975,999,999,999 ticks |
| `empty-append` | An `Append`'s body is non-empty |
| `trailing-bytes` | A `Delete` or an `Expired` has no bytes after its last field |

Every rejection which holds a CRC carries the CRC-32C of its row key and bytes, except the `crc-mismatch` cases: one
whose payload changed after its CRC was computed, a valid record presented under another journal's row key, and a record
of kind 4 under the CRC of kind 0. Each case breaks only its own rule, but the cases whose names say a rule is checked
first, which each break two rules and are rejected for the earlier, so they pin the order.

## Version text form

A version's text, the grain's ETag, is base64url without padding of the base's length, the base, and the count as the
record holds them: 3 to 58 characters. `versions.json` holds versions (`base` and `count`) with their `text`; parsing
`text` yields the version, and formatting the version yields `text`. `version-rejections.json` holds texts no version
formats to, which parsing rejects: padding, unused bits set, a missing, overlong, unterminated, 11-byte, or above-`u64`
count, a long or truncated base, a byte after the count, characters outside base64url's alphabet, and a text past the longest.
