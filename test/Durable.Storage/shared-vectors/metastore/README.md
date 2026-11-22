# MetaStore vectors

Language-neutral golden vectors for MetaStore register keys and version tokens
([MetaStore](../../../docs/spec/components/metastore.md#7-formats)). Clog's test suites read these files
directly, and the vectors change with the mappings they pin.

Clusters are 32 lowercase hexadecimal digits (`{cluster:N}`). Firestore seconds are 64-bit integers, written as
decimal strings.

## Mapping

A register is keyed by `(cluster, name)`. A name is one or more `/`-separated components, each
`[a-z0-9._-]{1,128}`; the cluster is a non-empty GUID. The register key is `{cluster:N}/{name}`, which each
backend places below its configured root. Azure Table row keys and Firestore document identifiers write each
`/` of the register key as `:`.

| Backend | Key |
| --- | --- |
| `caspaxos` | `{cluster:N}/{name}` |
| `azure-blob` | `{prefix}{cluster:N}/{name}` |
| `azure-table` | `{rowKeyPrefix}{cluster:N}:{name}`, `/` written as `:` |
| `aws-dynamodb` | `{partitionKeyPrefix}{cluster:N}/{name}` |
| `google-firestore` | `{collection}/{cluster:N}:{name}`, `/` written as `:` |

A Firestore version is the Base64 of 12 bytes: the document's update time, seconds (`int64` big-endian) then
nanoseconds (`int32` big-endian).

## Files

| File | Contract |
| --- | --- |
| `register-keys.json` | Each register's key and every backend's key under the configuration in `backends`; rejected names and the empty cluster; Firestore version tokens, and malformed tokens which a write treats as a mismatch |
| `register-file-rejections.json` | File-system register files (`CLFM` version 1) a reader rejects, each named by the first rule it breaks; a file breaking two adjacent rules pins their order |

## Register file

The file-system provider's register file is the ASCII magic `CLFM`, the format version byte 1, the 16-byte version, then
the value. A reader rejects, in this order, a file shorter than its 21-byte header (`truncated`), another magic
(`bad-magic`), and another format version (`bad-version`); each reason is the code its rejection carries in
`Data["orleans.durable.storage.decode.reason"]`, which the tests and the `metastore-register` fuzz oracle match.
