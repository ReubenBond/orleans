# WAL vectors

Language-neutral golden vectors for WAL files, version 1 ([WAL](../../../docs/spec/components/wal.md#7-formats)):
the header page, data pages of frames, the hole page, and the worked example. Clog's test suites read these files
directly, and the vectors change with the format they pin.

Integers in the encodings are unsigned little-endian. 64-bit integers in the vectors are decimal strings. Byte
strings are lowercase hexadecimal. A page's `hex` holds its bytes through its last non-zero byte; the page continues
with zero bytes to `pageSize`.

## Format

Bytes 0–3 of every P-byte page hold the CRC-32C of bytes 4 through P − 1. The header page is:

| Offset | Field |
| --- | --- |
| 0 | `u32` CRC-32C |
| 4 | Magic `CLFL` |
| 8 | `u32` version, 1 |
| 12 | `u32` page size P, a power of two from 512 to 65,536 equal to the page's length |
| 16 | `u32` write window W, from 1 to 2<sup>31</sup> − 1 |
| 20 | `u32` flags: bit 0 when the file is the first of its open |
| 24 | Zero |

A data page holds frames back to back from offset 4, then zero. Each frame starts with a `u32` word whose bits
31..30 are the kind and bits 29..0 the fragment length n:

| Kind | Frame |
| --- | --- |
| 1 Full | The word, then the whole record of n bytes |
| 2 First | The word, the `u64` total record length (above n, at most 2<sup>31</sup> − 1), then n ≥ 1 bytes filling the page |
| 3 Continuation | Only at offset 4: the word, then n bytes, 1 ≤ n ≤ P − 8, filling the page unless it finishes the record |

A zero word, or fewer than 4 remaining bytes, ends the frames. A hole page is a valid page whose word at offset 4
is kind 0 with length P − 8 and whose bytes from 8 are zero.

## Files

| File | Contract |
| --- | --- |
| `headers.json` | Each header's fields and the exact page it encodes to; decoding the page yields the fields |
| `header-rejections.json` | Header pages a reader does not accept, each named by the first rule it breaks; a page breaking two adjacent rules pins their order |
| `pages.json` | Each data page's frames and its exact bytes, and the hole page; decoding yields the frames |
| `page-rejections.json` | Data pages a reader does not accept, each named by the first rule it breaks; a page breaking two adjacent rules pins their order |
| `example.json` | The worked example as the WAL writes it: a 9,000-byte record (byte i is i mod 251) in file 7, P = 4096, from data page 4, with its position, page, and frontier, the file's header page, and data pages 4 to 6 |

Each rejection names its `reason` and its `outcome`. Header outcomes are `damage` (an invalid header, WAL-I3) and
`unsupported` (the `CLFL` magic with another version). Header reasons are `truncated`, `zero-page`, `bad-magic`,
`unsupported-version`, `page-size-unsupported`, `page-size-mismatch`, `crc-mismatch`, `window-zero`,
`window-out-of-range`, `unknown-flag`, and `nonzero-padding`, in the order the decoder checks them. Page outcomes are
`invalid` (the page CRC fails) and `ill-formed` (a valid page which breaks the frame rules and is not a hole page). Page
reasons are `crc-mismatch` (`zero-page` for an all-zero page), then, for a page which is not a hole page,
`continuation-empty` and `continuation-overlong` for a leading Continuation; for each later frame in turn,
`frame-overrun`, `continuation-not-leading`, `hole-not-zero`, `end-with-length`, and for a First frame `first-empty`,
`frame-overrun`, `first-not-filling`, `first-total-not-above-fragment`, and `first-total-out-of-range`; and last
`nonzero-after-end`, in the order the decoder checks them. Each reason is the code the
decoder's rejection carries in `Data["orleans.durable.storage.decode.reason"]` (`WalFormat.DecodeHeader` and `WalFormat.ValidatePage`),
which the tests and the fuzz oracles match.
