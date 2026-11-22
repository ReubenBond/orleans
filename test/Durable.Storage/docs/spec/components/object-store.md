# Object store

## 1. Purpose and scope

The object store is the seam between Clog's object-storage layers and the cloud and local services which keep their
objects: `IObjectStore`, objects created once or replaced by version, read by range, by properties, and in ordered
listings; and `IAppendableObjectStore`, objects extended by length-conditioned appends, sealed, and replaced by a
tombstone. This file states their one contract, which every provider meets and every caller relies on; the
[LSM](archive-lsm.md#3-requirements-downward), [archive logs' chunk layout](archive-logs.md#74-chunk-layout), and
[object-store ledgers](object-ledgers.md#31-from-the-object-store) state only their own rules for their own objects.

Code: `Orleans.Durable.Storage.Engine.ObjectStore.Abstractions` (namespace `Orleans.Durable.Storage.Engine.ObjectStore`)
holds the contract, its types, and `PrefixedObjectStore`; the memory provider, `InMemoryObjectStore`, lives in
`Orleans.Durable.Storage.Engine.ObjectStore.Memory`, and the file-system, Azure, S3, and Cloud Storage providers in
their technology packages (§9).

### Design rationale

- **One contract, stated once.** Three layers use one store, so the contract names no caller: each caller's rules about
  its own objects (the LSM replaces only its manifest; a run object's length is fixed for life) live with that caller,
  and the demand every caller meets (§2.4), on which providers rely, lives here.
- **Two interfaces over the same objects.** Appends are a second interface, which a provider implements beside
  `IObjectStore` over the same objects, so reads, properties, listings, and deletes of an appendable object are
  `IObjectStore`'s. Neither extends the other and no caller tests a store for appends: a caller which needs both takes
  one instance constrained to both (§2.3). *Rejected:* an `IAppendableObjectStore : IObjectStore` capability, which
  callers would discover by type tests.
- **A definite `Sealed`.** An append whose answer its client lost may land, after which a seal may land before the
  client's retry, which then meets the seal. A store which answered `Sealed` there would report bytes the object holds as
  never written, so an append to a sealed object which holds its bytes at its expected length answers `Appended`, and
  `Sealed` means the call's bytes are not there.
- **Start names filtered by every provider.** Azure's `startFrom` and Cloud Storage's `startOffset` include the start's
  name, S3's `StartAfter` excludes it, and `PrefixedObjectStore` cuts a start longer than any name, so each provider
  passes the start to its service and filters each name it lists against it.
- **Hierarchical directory-bucket layout (S3 Express One Zone).** An S3 directory bucket (`--x-s3`) returns
  `ListObjectsV2` entries unordered, refuses `StartAfter`, requires every non-empty `Prefix` to end in `/`, and forbids
  keys that collide with directory prefixes. Mapping each object name onto fixed 3-character directories with `.`
  encoding `/` and an `L` leaf marker (§9.2, from `research/papers/logdrive/paper.md` §3.2) preserves ordinal order
  across characters (`'-'` < `'.'` < `'/'` < `'0'`), guarantees disjoint key intervals between sibling leaves and
  subdirectories, keeps decimal sequences (20-digit ledger IDs and reverse slot keys) to at most 1,000 entries per
  directory (one `ListObjectsV2` page), aligns `ledgers/l/{id:D20}/s/` onto an exact 3-character directory boundary
  (an outer `PrefixedObjectStore` prefix whose length is not a multiple of 3 — including the default 10-character
  cluster prefix plus `/`, 11 characters — shifts chunk boundaries while preserving the at-most-1,000 decimal-directory
  cap at most one extra directory level), and stays within 267 bytes for a 200-character `ObjectName` (well under S3's
  1,024-byte key limit). *Rejected:* variable-depth split solely on logical `/`, which leaves a flat `s/` directory of
  millions of unordered slots that a listing would have to download whole to find the newest `K` slots.

## 2. Contract upward

### 2.1 `IObjectStore`

| Member | Outcomes |
| --- | --- |
| `CreateAsync(name, content, crc32c)` | `Created`, or `Exists` when the name exists, which may be this call's own earlier attempt, landed before a retry. Never overwrites. `content` may be empty (`default(ArcBuffer)`). `crc32c` is the CRC-32C the writer computed over the bytes it verified, or `null` where no writer verified them, required at every call. Content which does not match it is refused, on every provider and in every build, and the create then fails without creating the object: with `ObjectContentChangedException` when the provider's own check fails or a service reached over TLS refuses it, and as an unknown outcome when a service reached over plain HTTP refuses it; an upload also carries an integrity checksum of `content` computed before it leaves the process, so the service refuses bytes damaged on the way. A non-empty object records its CRC-32C. Any other exception leaves the outcome unknown: the create lands at most once, possibly later. |
| `ReadAsync(name, offset, length)` | Exactly the requested bytes, or `null` when the object is absent; `EndOfStreamException` when the object ends before the range does. |
| `GetPropertiesAsync(name)` | `ObjectProperties(Length, Crc32c?)` from one metadata read, with the CRC-32C its create recorded, none for an empty or appendable object; or `null` when the object is absent. |
| `ListAsync(prefix, startAfter)` | A stream, which a provider may fill page by page as a service's listing pages, of each name after `startAfter` (from the prefix's first name when it is `null`) with its object's length, for every name which existed from the call until the stream ended, in ordinal order, and possibly names created or deleted meanwhile, which are hints. |
| `DeleteAsync(name)` | Absent afterwards; deleting an absent object succeeds. |
| `ReadVersionedAsync(name)` | `(content, version)` or `null`, linearizable. |
| `ReplaceAsync(name, expectedVersion, content, crc32c)` | `Applied(Version)` iff the object holds `expectedVersion` (or is absent, for `null`), with the new version; otherwise `Mismatch`, which may be this call's own earlier attempt's doing, landed before a retry. `content` is not empty. Content which does not match `crc32c` is refused as a create's is, and leaves the object as it was. Any other exception leaves the outcome unknown: it applies at most once, and only at `expectedVersion`. |

Read-after-write holds for creates, deletes, replaces, and listings which begin after a write returned. Every answer
reflects durable state, so no crash revokes it ([durable answers](../conventions.md#13-durable-answers)): `Created`,
`Applied`, and a delete's completion are given only once the change is durable, and `Exists`, a read, a properties read,
and the names a listing asserts only from durable state. A service's client retries a request whose answer it lost, so a
retry meets its own landed attempt as a precondition failure: `Exists` and `Mismatch` report the durable state a call
observed, and a caller which acts on either reads the state it needs.

**Content.** An object holds at most 2³¹ − 1 bytes, the most an `ArcBuffer` holds. Objects are large payloads
([conventions §1](../conventions.md#1-buffers-and-ownership)), and an upload is work which may outlive its caller's
wait: a write whose outcome is unknown may still read its content. A write therefore takes an `ArcBuffer`, which the
caller keeps owning and disposes, and a provider which reads the content after the call completes retains its own
lease, disposing it once its write finishes. A read returns an `ArcBuffer`, which the caller owns and disposes. A
provider over a service converts at its SDK's boundary to the SDK's type ([buffers](buffers.md#stream-adapter)).

**Versions.** A version may be a digest of the content, as the file-system provider's SHA-256 and an S3 single-part
ETag are, so it names content rather than a write: an object which holds earlier content again holds its earlier
version again, and a replace naming that version applies. A caller therefore never writes content the object held
before its current content.

### 2.2 `IAppendableObjectStore`

| Member | Outcomes |
| --- | --- |
| `CreateAppendableAsync(name)` | Creates an empty, open, appendable object only if no object of the name exists: `Created`, or `Exists`, of any kind, which may be this call's own earlier attempt. |
| `AppendAsync(name, expectedLength, content, crc32c)` | Appends 1 byte to 100 MiB (`MaxAppendLength`), ending at most at 2³¹ − 1 bytes, only when the object is open and holds exactly `expectedLength` bytes: an `ObjectAppendOutcome`. `Appended(expectedLength + n)` once the bytes lie durably at `expectedLength`, this attempt's or an earlier one's which landed before a seal; `LengthMismatch(Length)`, the durable length of an open object at another length, which may be this call's own earlier attempt's doing; `Sealed` when the object is sealed or not appendable and does not hold the bytes there; or `Absent`. Exactly one append lands at each length. |
| `SealAsync(name)` | An `ObjectSealOutcome`: `Sealed` once an appendable object is durably sealed, so that every append which lands afterwards answers `Sealed`, whatever its length; `NotAppendable` when the object was created whole or replaced by its tombstone, which the seal leaves unchanged; or `Absent`. Idempotent. |
| `ReplaceWithTombstoneAsync(name, content, crc32c)` | Atomically replaces the object, whatever it holds, or creates it when absent, with a non-appendable object holding `content`, which records its CRC-32C and on which every append answers `Sealed`. Every tombstone of one name carries the same content. |

Every answer reflects durable state, and a call whose answer is lost, by an exception or by cancellation, lands at most
once, possibly later: an append only at its expected length and only while the object is open. A read of an appendable
object reflects every append whose answer left before the read began. Content which does not match its writer's CRC-32C
is refused with `ObjectContentChangedException`, and the call writes nothing.

### 2.3 Pairing

A provider of appendable objects implements both interfaces over the same objects, as one instance. A component which
uses both takes that one instance through a type parameter constrained to both interfaces: the fault-injecting test
store (`FaultInjectingObjectStore<TStore>`), `ObjectLedgerStore`, and the cluster prefix's wrapper of both,
`PrefixedAppendableObjectStore`. `PrefixedObjectStore` places an `IObjectStore` under a prefix.

### 2.4 The callers' demand

Every caller meets one demand, on which a provider may rely. An object is one of three kinds by how its name is first
written, and a caller never writes one name two ways:

- **Created once** by `CreateAsync`: it keeps its content until it is deleted, and a name created again after its
  deletion holds the content it held, except in an object-store ledger whose `dropped` marker exists, where a delayed
  SEAL may re-create a deleted batch's name empty, and every read of the name answers the ledger's drop ([object-store
  ledgers §1](object-ledgers.md#design-rationale), OL-I7).
- **Replaced** by `ReplaceAsync`: it is read only with `ReadVersionedAsync`.
- **Appendable**, created by `CreateAppendableAsync`: only `IAppendableObjectStore` writes it, its tombstone included,
  until it is deleted. A caller appends to one object at most 50,000 times, Azure's limit of blocks per append blob.

The file-system provider answers a ranged or properties read of a created object it has already seen durable without a
flush (§9), so after another process deletes and re-creates the name, the read may answer before the re-creation is
durable; Debug builds assert that this process reads no name it replaced that way.

## 3. Requirements downward

Each provider relies on its service's documented semantics and does not probe them: conditional creates and replaces
(S3 conditional writes, Azure Blob ETags, Cloud Storage generations), checksums the service validates, durable answers,
and strongly consistent reads, conditions, and listings. General-purpose S3, Azure, and Cloud Storage list in ordinal
order; an S3 directory bucket (`--x-s3`) lists unordered, refuses `StartAfter`, and requires every non-empty `Prefix` to
end in `/`, which `S3ObjectStore` serves in ordinal order through its hierarchical directory layout (§9.2). Azure's
appendable objects rely on `x-ms-if-tags` evaluated atomically with `Append Block` and `Set Blob Tags`, and on a
`Put Blob` replacing an append blob whole ([verification §13](../verification.md#13-qualification-boundaries)). The
container, bucket, or directory settings the host provides are those of
[archive LSM §9](archive-lsm.md#9-providers-and-implementations).

## 4. Data types

| Type | Definition |
| --- | --- |
| `ObjectName` | 1 to 200 ASCII characters, `/`-separated components of `[a-z0-9_-]` ([identity §11](../identity.md#11-lsm-object-names)); a listing prefix or start is the start of a name. |
| `ObjectVersion` | An opaque version: an ETag, a generation, or the file-system provider's SHA-256 of the content. |
| `ObjectProperties` | `(Length, Crc32c?)`. |
| `ListedObject` | `(Name, Length)`. |
| `VersionedObject` | `(Content, Version)`, the content an `ArcBuffer` the caller owns. |
| `ObjectCreateOutcome`, `ObjectReplaceOutcome`, `ObjectAppendOutcome`, `ObjectSealOutcome` | The unions of §2 ([conventions §15](../conventions.md#15-outcomes-unions-and-errors)). |

## 5. Invariants

The object store states no invariants of its own: §2 is its contract, which each caller's invariants rest on (LSM-I7,
the chunk layout's, and the OL invariants), and which `ObjectStoreSpec` judges over every provider (§10).

## 6. Faults, concurrency, and cancellation

- **Unknown outcomes.** An exception other than a definite refusal leaves a write's outcome unknown: it lands at most
  once, possibly later, only where its condition holds.
- **Refusals.** A request the service refuses for a reason no repetition changes throws `ObjectStoreRefusedException`,
  a definite failure with no effect, which its caller reports rather than retries.
- **Changed content.** Content which does not match its writer's CRC-32C throws `ObjectContentChangedException`
  ([archive LSM §9](archive-lsm.md#9-providers-and-implementations)).
- **Retries.** Each provider uses its client's built-in retries, under which every operation is idempotent by its
  condition ([conventions §6](../conventions.md#6-retries-and-idempotency)); Azure repeats an append which its blob's
  state leaves undecided at most three times, after which its outcome is unknown.
- **Cancellation** stops the caller's wait; a write already sent may still land.

## 7. Formats

| Provider | Kept beside the content |
| --- | --- |
| Azure | Metadata `durablecrc32c`, the CRC-32C of a non-empty block blob as 8 lowercase hexadecimal digits; index tag `durable-open`, `1` while an append blob is open, `0` once sealed and on a tombstone. |
| S3 | The object's `x-amz-checksum-crc32c`. In a general-purpose bucket the key is `{prefix}{name}`; in a directory bucket (`--x-s3`) the key is `{prefix}` followed by the hierarchical directory layout of §9.2. |
| Cloud Storage | The object's `crc32c`. |
| File system | Nothing: the store computes a CRC-32C from the file as it answers. An object's file is `+{name}`, an open appendable object's `!{name}`, a sealed one's `={name}`, each `/` written as `+`. |

## 8. Mechanism and policy

**Listings.** General-purpose S3 (`StartAfter`), Azure (`startFrom`), and Cloud Storage (`startOffset`) pass
`startAfter` to their service and filter each name against it (Design rationale), so a service which starts at or before
the start, or ignores it, still lists exactly the names after it. An S3 directory bucket (`--x-s3`) lists lazily in
ordinal order by descending its `Delimiter = "/"` directory hierarchy with prefix and `startAfter` pruning (§9.2).

**Appends on the file system.** Every appendable call holds the store's lock. An append writes the open file's bytes
and its own to a temporary file, flushes it, and renames it over the open file, so a published file never changes; a
seal renames the open file to its sealed name; a tombstone renames its file over the `+` name, flushes the directory,
then removes the `!` and `=` files, so a crash between them leaves the tombstone, which hides them.

## 9. Providers and implementations

| Provider | Project | `IAppendableObjectStore` | Notes |
| --- | --- | --- | --- |
| Memory | `Orleans.Durable.Storage.Engine.ObjectStore.Memory` | Yes | Tests and simulation; each write copies its content and checks the copy. |
| File system | `Orleans.Durable.Storage.FileSystem` | Yes | §7, §8; its CRC-32C is computed, not recorded. |
| Azure Blob Storage | `Orleans.Durable.Storage.AzureStorage` | Yes (§9.1) | Block blobs; append blobs for appendable objects. |
| Amazon S3 | `Orleans.Durable.Storage.S3` | No | General-purpose buckets and S3 Express One Zone directory buckets (`--x-s3`, §9.2). |
| Google Cloud Storage | `Orleans.Durable.Storage.GoogleCloud` | No | An object's size absent from an answer is 0, the proto3 JSON mapping's default, which Google's testbench omits; appendable objects deferred ([deferred](../deferred.md)). |

Each create, replace, read, listing, and delete is as [archive LSM §9](archive-lsm.md#9-providers-and-implementations)
states for its provider, with S3 Express One Zone directory buckets (`--x-s3`) governed by §9.2 for key layout,
ordered listing, and opaque ETags. Tests add `SimulatedObjectStore`, with delayed landings, and
`FaultInjectingObjectStore`, plain or over a store of both interfaces.

### 9.1 Azure appendable objects

An appendable object is an append blob created with `If-None-Match: *` and the index tag `durable-open = 1`. An append
is one `Append Block` conditioned on `appendpos` at its expected length and on `x-ms-if-tags: "durable-open" = '1'`. A
seal reads `Get Blob Properties`, whose blob type tells an append blob, then sets `durable-open = 0` with
`Set Blob Tags` under the same tag condition, so it changes no sealed or non-appendable blob, reading the type again
when the condition fails. A tombstone is one unconditional `Put Blob` of a block blob tagged `durable-open = 0`. A
refused append (412 on either condition, 404, or 409 `InvalidBlobType`) is classified by `Get Blob Properties` and
`Get Blob Tags`: absent; not an append blob, so `Sealed`; sealed, `Appended` when a ranged read finds its bytes at its
expected length and otherwise `Sealed`; open at another length, `LengthMismatch`; or open at its expected length, which
other appends reached since, so the append is sent again, at most three times. The composition pins its client to
service version 2026-10-06, whose `startFrom` the listing uses, and `ConfigureClientOptions` refuses any version
before 2022-11-02, from which `Append Block` takes tag conditions and 100 MiB blocks.

### 9.2 S3 directory buckets

When `bucketName` ends in `--x-s3`, `S3ObjectStore` activates directory-bucket mode (`S3DirectoryBucketLayout`). S3
Express One Zone returns opaque random ETags rather than MD5 content digests, which `ObjectVersion` treats opaquely as
on every provider.

**Key layout.** Each character of `name` is preserved except `/`, which encodes as `.` (strictly order-preserving over
`ObjectName`'s alphabet, since `'-'` (45) < `'.'` (46) < `'/'` (47) < `'0'` (48) and `ObjectName` forbids `.`, leading or
trailing `/`, and `//`). The encoded string is split into 3-character directory segments terminated by `/` until 1 to 3
characters remain, followed by the uppercase leaf marker `L` (disjoint from `[a-z0-9-_.]`), placed under the store's
`prefix` (empty or ending in `/`): for example, `manifest` becomes `{prefix}man/ife/stL`, and `ledgers/l/{id:D20}/s/{rev:D20}`
places its reverse slot digits in 3-digit directories of at most 1,000 entries each directly below the 11-segment
directory `{prefix}led/ger/s.l/.{id chunks}/.s./`. When composed beneath `PrefixedObjectStore`, an outer prefix whose
length is not a multiple of 3 (including the default 10-character cluster prefix plus `/`, 11 characters) shifts chunk
boundaries while keeping each decimal directory at most 1,000 entries across at most one extra directory level. Leaves
end in `L` and directories never do, so no object key collides with a directory prefix.

**Ordered listing.** `ListAsync(prefix, startAfter)` computes the starting directory from the deepest 3-character chunk
boundary guaranteed to contain every matching name, jumping directly into that directory without listing its ancestors
(and issuing zero requests when `prefix` and `startAfter` admit no valid `ObjectName`). It enumerates one directory at a
time with `ListObjectsV2` (`Delimiter = "/"`, no `StartAfter`, `Prefix` omitted when empty and otherwise ending in `/`),
deduplicating entries across continuation pages, keeping only direct leaves and subdirectories whose key range overlaps
`prefix` and lies strictly above `startAfter`, sorting the directory's surviving entries in forward ordinal order (a
leaf before its equal-prefix subdirectory, since `x < x...`), and pushing a forward `DirectoryCursor` (sorted list plus
index cursor) onto an explicit DFS stack. At most `ceil(200 / 3) = 67` directories are active on the stack at once, each
holding only its direct surviving entries. Each leaf is yielded lazily before descending into later subtrees, so
stopping after the newest `K` slots visits only `O(depth)` directories; empty directories retained in `CommonPrefixes`
after deletes and foreign keys outside the layout are skipped cleanly.

## 10. Verification

| Evidence | What it establishes |
| --- | --- |
| `ObjectStoreSpec` through the conformance kit | §2.1 and §2.2: empty objects, properties, start names, appends, seals, tombstones, and an append to a sealed object which holds its bytes, sequential and concurrent, with unknown outcomes, over the memory, file-system, fault-injecting, and simulated stores, on Azurite, LocalStack, and the Cloud Storage emulators (`ObjectStoreConformanceTests`, `SimulatedObjectStoreConformanceTests`, `CloudObjectStoreConformanceTests`), and over `S3ObjectStore` directory-bucket mode on the unordered `FakeS3DirectoryBucketService` (`S3DirectoryBucketTests`); its caller keeps §2.4. |
| S3 directory-bucket layout and ledger conformance | `S3DirectoryBucketTests` (colliding prefixes and chunk boundaries, impossible ranges, subtree pruning for ledger tails and `AfterLedger`, retained empty directories and foreign keys, `PrefixedObjectStore` composition, missing-bucket `GetPropertiesAsync`, cancellation, and fake constraint enforcement) and `S3DirectoryBucketLedgerConformanceTests` (`LedgerStorageSpec` over `ObjectLedgerSystem` on `S3ObjectStore` directory-bucket mode with `FakeS3DirectoryBucketService`, sequential, faulty, and concurrent); `ListingStartFidelityTests.AnS3DirectoryBucketListsInOrdinalOrderFromAStartOverAnUnorderedService`. Live S3 Express run at row 7.6. |
| Landed, then sealed, then retried | Both test doubles inject an append's landing, a seal, and the retry, which answers `Appended`; a mutant answering `Sealed` is flagged by the spec. On Azurite, `AzureAppendableObjectFidelityTests` loses an append's answer, seals before the retry, and asserts `Appended`, with the provider's mutant answering `Sealed`. |
| Crash states | `FileSystemObjectStoreCrashStateTests`: every answer of plain and appendable workloads holds in every crash state. |
| Provider fidelity | `AzureAppendableObjectFidelityTests` on Azurite, each precondition's failure code asserted, and a seal racing appends in flight, after which no append lands; `ListingStartFidelityTests` on LocalStack and both Cloud Storage emulators. Live runs at row 7.6. |
