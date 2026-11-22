# Conventions

Rules every component follows. A component's file states only where it refines one.

## 1. Buffers and ownership

- **Large payloads are `ArcBuffer`s or sequences.** A large payload (below) crosses an API as an `ArcBuffer`, or a
  type which owns one, such as a `Row` ([buffers](components/buffers.md)), or as a `ReadOnlySequence<byte>`:
  - **Kept or returned: `ArcBuffer`.** An API which may keep the bytes beyond the call, to queue, replicate, cache, or
    upload them, takes an `ArcBuffer` and retains its own lease for what it keeps; the caller keeps and disposes its
    own. Work which may outlive its caller's wait copies before dispatch or takes an `ArcBuffer` (below). An API which
    returns a payload returns an `ArcBuffer`, and
    only a returned payload transfers ownership: the caller disposes it. An `ArcBuffer`-based value lent for a step,
    such as a batch's rows during apply or a WAL scan's `WalRecord`, is borrowed, and valid only for that step.
    An `ArcBuffer`'s pooled, reference-counted pages cross layers, storage SDKs, and transports without copies:
    `Retain` and `Slice` give independent ownership, `UnsafeSlice` aliases the source's lease, and disposing an owner
    releases its pages exactly once ([BUF-I1](components/buffers.md#5-invariants)).
  - **Read during the call: `ReadOnlySequence<byte>`.** An API which only reads a payload before it completes,
    copying, encoding, or hashing it, takes a `ReadOnlySequence<byte>`, which may span pages; so does a synchronous
    API which takes a large payload. A caller which holds an `ArcBuffer` passes its `AsReadOnlySequence()`.
- **A borrowed sequence ends with the call.** An operation completes when its returned task reaches a terminal state:
  a result, an exception, or cancellation. A callee which takes a `ReadOnlySequence<byte>` reads it for the last time
  before then, on every outcome, so its caller may release and reuse the pages once the task completes. Work which may
  outlive the caller's wait, such as a write whose outcome is unknown and which may still land (§3), copies the
  sequence into its own buffer before it dispatches, or takes an `ArcBuffer` and retains it. Only this rule protects a
  borrowed sequence: `AsReadOnlySequence()` checks its buffer's validity once, when it builds the sequence, so
  [BUF-I4](components/buffers.md#5-invariants)'s checks do not reach a read through it after the buffer's release,
  which Debug builds' poisoned pages expose instead ([buffers §2](components/buffers.md#ownership-model)).
- **A producer releases what no one awaits.** A producer which made an owned payload for a waiter whose wait has
  already ended, by cancellation or otherwise, releases the payload itself.
- **Holders never write.** A caller keeps the bytes it passes unchanged until the operation completes. No holder
  writes through an `ArcBuffer`'s pages, and an array which `ArcBuffer.Wrap` lends is never changed while a lease on
  it lives.
- **The empty payload is the default value.** A producer of an empty payload produces `default(ArcBuffer)`, which owns
  nothing, reads as an empty buffer, and does nothing when disposed
  ([buffers §2](components/buffers.md#ownership-model)): an empty register value or journal
  payload is `default(ArcBuffer)`.
- **Small means a stated bound of at most 64 KiB.** A payload is small when the contract which carries it states a
  fixed bound on its length of at most 65,536 bytes, as for a key of the ledger keyspace (at most 65,535 bytes,
  [ledgers §2.1](components/ledgers.md#21-keys-and-ranges)), a journal key, a tag, an ID, a version, a record header,
  and a slot-index leaf. Every other payload is large: its contract states no fixed bound, or a larger one, as for an
  entry, a row's body, a batch, a register value, and an object. The bound a
  contract states decides, whatever a payload's typical size.
- **Small values and held bytes are contiguous.** A small value crosses as `ReadOnlyMemory<byte>` or an array.
  Synchronous, non-escaping work takes `ReadOnlySpan<T>` for a small value, and for bytes its own component already
  holds.
- **External libraries keep their own types.** At an external library's boundary (a storage SDK, RocksDB, Orleans
  serialization, a gRPC client), the provider or adapter uses the library's native type, such as `Stream`,
  `ReadOnlySequence<byte>`, `BinaryData`, `ByteString`, or an array, and converts to Clog's types there:
  `ArcBuffer.Wrap` owns an array without copying it, `ArcBufferReadStream` hands an `ArcBuffer` to an SDK which
  reads a `Stream`, on which it holds its own lease until the stream is disposed, and `ArcBufferWriteStream` fills
  pooled pages from an SDK which writes one ([buffers](components/buffers.md#stream-adapter)). A `Stream` appears
  only at such a boundary.
- **The file-system seam takes the BCL's types.** `WalFileSystem` and the I/O engines beneath the WAL hand the
  operating system the memory it writes from and reads into, in the shapes of `RandomAccess` and
  `File.ReadAllBytesAsync`: lists of `ReadOnlyMemory<byte>` and `Memory<byte>` over pages for vectored writes and
  reads, and one array for a whole-file read.
- **Direct I/O.** Pages which storage may hand to the kernel come from the aligned 4 KiB class, carved from
  pinned slabs which never move and are never freed ([BUF-I3](components/buffers.md#5-invariants)).
- **Hot paths are allocation-conscious.** Payloads are not copied between `ArcBuffer`, serializers, storage SDKs,
  and transports; a copy is made only where a payload crosses between silos, into Orleans' own buffers and out of them
  into Clog's pool, so that no message Orleans drops holds a Clog page
  ([orleans §5.12](compositions/orleans.md#512-replica-transport)),
  where a record spans pages (the WAL reads), or where the primary encodes its rows into a batch, once per payload, and
  then appends the CRC of what it wrote ([ledgers §7](components/ledgers.md#7-formats)). A component converts an
  `ArcBuffer` to a sequence once per payload and passes the sequence on, since each conversion of a buffer which spans
  pages allocates a ledger object, about 48 bytes, per page. A writer of a large payload passes its expected length
  to the writer, which caps its pages at the largest large page, 1 MiB
  ([buffers §8](components/buffers.md#8-mechanism-and-policy)): a larger payload is a chain of 1 MiB pages, neither a
  run of 1 KiB minimum pages nor one array of its whole length.
- **Entry bodies are held whole and bounded.** Every entry's body is inline in its record and its row, at most
  `MaxBodyBytes` (16 MiB by default, fixed per cluster): the writer passes it as an `ArcBuffer`, which the store
  retains while it needs the bytes, and reads return a journal's content as `ArcBuffer` pages, so every layer holds an
  entry whole within the byte budgets of §7 ([journals §8.8](components/journals.md#88-policy)).

### Design rationale for payload types

A large payload is never a `ReadOnlyMemory<byte>` or an array, because both are contiguous. Bytes held in pages (an
`ArcBuffer`, a pipe, a socket's buffers) are copied into one block to become one, and a block of about 85,000 bytes or
more lands on the large object heap, which only generation-2 collections reclaim and which fragments.
`ReadOnlySequence<byte>` lends pages as they are, so a callee which only reads takes one. Where the callee keeps the
bytes, memory also names no owner and no lifetime: the callee copies it, or trusts an unchecked convention while the
pooled memory behind it may return to its pool and be reused. An `ArcBuffer` gives such a callee a lease of its own,
whose page versions detect use after release ([BUF-I4](components/buffers.md#5-invariants)), and an API which returns
one hands its pages to the caller instead of allocating a block per answer. Ownership chooses between the two forms:
requiring an `ArcBuffer` where the callee only reads would make callers which hold other memory wrap it, and requiring
a sequence where the callee keeps the bytes would make it copy them.

A borrowed sequence ends with its call because it carries no lease, and cancellation ends only the caller's wait
(§3): a write whose outcome is unknown may read its payload after its task has completed, which a sequence cannot
survive. Such work makes a copy at its library's boundary before it dispatches, as a register value's is, or retains
an `ArcBuffer`. The checks which guard an `ArcBuffer` stop at `AsReadOnlySequence()`, so the rule rests on
each callee, and Debug builds' poisoned pages make a broken one visible in tests.

`default(ArcBuffer)` is the empty payload because an empty value needs no page. Empty register values and journal
payloads are ordinary ([MS-I2](components/metastore.md#5-invariants)), and a representation which
allocated a page for one, or threw, would make every provider handle emptiness apart.

The large object heap is the reason for the line between small and large, and a contract's bound draws it. The
runtime's threshold, 85,000 bytes by default, is measured on the whole object and is configurable (`GCLOHThreshold`),
and a byte array reaches it at 84,976 bytes; a stated bound of at most 64 KiB keeps every small value below the default
with room to spare, without depending on the collector's configuration. A contiguous copy below it is an ordinary
small-object allocation, and keys, tags, and IDs are compared, hashed, and encoded as spans. The bound a contract
states decides because values which are usually small, such as the ledger map and a journal's warm state, grow with
use.

Converting at an external library's boundary is unavoidable, and converting earlier would only move the copy. A
`Stream` in Clog's own APIs was rejected: it hides a payload's length and owner, it can be read once, and every read
copies into the reader's buffer. The Orleans replica transport's copies into and out of Orleans' own `ArcBuffer` are a
trade-off, not a consequence of this rule: they cost a copy at each crossing, and the sender's copy of a full batch and
the callee's copy of a DATA reply become large-object-heap garbage, since Orleans releases none of the buffers it holds,
which row 4.7 measures; in
return no message Orleans drops can hold a Clog page ([orleans §5.12](compositions/orleans.md#512-replica-transport)).

A journal entry's body is held whole, in an `ArcBuffer` its writer passes and the store retains, rather than streamed:
a forwarded write which outlives its caller's cancelled wait still reads live pages, a resend after `Moved` replays
the same bytes, and `MaxBodyBytes` bounds what each layer holds, so every in-flight budget is a byte budget
([journals §8.8](components/journals.md#88-policy)).

## 2. Time

Every component reads time and waits through an injected `TimeProvider`; none reads the wall clock or delays
directly. Tests use `FakeTimeProvider`. Time decides only when intervals, deadlines, backoff, and retention end;
safety never depends on it: Clog has no leases, and no grace period on which safety rests
([system-model](system-model.md#2-clocks)).

## 3. Cancellation

**Cancellation stops the caller waiting, never the write.**

- A token cancelled before an operation starts makes it throw `OperationCanceledException` without touching
  storage.
- Once a write may have been issued, cancelling ends only the caller's wait. The write runs to its outcome:
  an accepted WAL record becomes durable, an acquired sequencer slot is written and completed, a proposed
  batch is decided, a dispatched MetaStore write, object create, or manifest replace may still land.
- The caller of a cancelled write acts on the system only conditionally afterwards, since the write may still land,
  at most once: it re-reads the register or the manifest and tests its goal, creates objects under fresh names, and
  orders a journal's next entry after the cancelled one with the version check
  ([journals §2.2](components/journals.md#22-writes)).
- Cancellation is never reconciled as an outcome: a cancelled create is reported as
  `OperationCanceledException`, not as `Written`, `Sealed`, or absent.
- One caller's cancellation never cancels work other callers share (a coalesced seal check, a leaf load, a
  flush).
- Only disposal ends work a write started, and then the outcome stays undecided for its caller (an append
  waiting out an outage reports `ObjectDisposedException`).

## 4. Integrity

### 4.1 CRC-32C

Every integrity check uses CRC-32C (Castagnoli): reflected polynomial `0x82F63B78`, initial value and final
XOR `0xFFFFFFFF`, `CRC32C("123456789") = 0xE3069283`. Every corruption of stored bytes is assumed to
invalidate one ([system-model](system-model.md#4-assumptions)), and a value which fails its check is reported
as corruption, never as missing or absent.

| Where | Covers | Computed by | Verified by |
| --- | --- | --- | --- |
| WAL page, bytes 0–3 | Bytes 4 to `P − 1` of the page, including the header page | WAL when it closes the page | Every scan and point read ([wal](components/wal.md#7-formats)) |
| `CLSC` catalog checkpoint header | The checkpoint body | LedgerStore | Catalog checkpoint decoding ([ledgerstore](components/ledgerstore.md#7-formats)) |
| `CLSI` object reference | The referenced leaf or interior node bytes | LedgerStore checkpoint writer | Checkpoint loading |
| `CLSI` root slot, bytes 0–3 | Bytes 4 to `S − 1` of the slot | LedgerStore | Root loading |
| Ledger map value, its last 4 bytes | The value's bytes before them | The map committer | Every map decode: the map view and the committer ([ledgers](components/ledgers.md#7-formats)) |
| Batch, its last 4 bytes | The batch's bytes before them | The primary | Every batch decode: the apply loop, scans, and drains ([ledgers](components/ledgers.md#7-formats)) |
| Journal entry record, bytes 0–3 | The row key, then the rest of the record | The writing process, once, when it encodes the entry (in Orleans, the silo whose grain issued it); the bytes then travel verbatim through forwarding | The primary, where the record enters it, before it writes the entry's row; from there the batch's CRC-32C covers the row ([JS-I5](components/journals.md#5-invariants)) |
| Run object data block | The block's rows | The drain or compaction writer | Every block read ([archive LSM](components/archive-lsm.md#7-formats)) |
| Run object block index, Bloom filter, and trailer | The part's bytes | The drain or compaction writer | Every tail load ([archive LSM](components/archive-lsm.md#7-formats)) |
| Manifest | The whole value | The committing worker | Every manifest decode |
| Block cache entry | One cached block | The block cache, on admission | Every cache read ([archive LSM](components/archive-lsm.md#87-caches)) |
| `CLNR` header | Header bytes 0–11 and the node registry body | The node registry committer | Registry decoding: the committer and every silo's resolver ([orleans §7.3](compositions/orleans.md#73-node-registry-clnr-v1)) |

The journal index has no CRC of its own: it is derived, RocksDB checksums its table blocks and verifies them on read,
and a periodic check reads every block, so damage is reported and discards and rebuilds the index
([journals §8.3](components/journals.md#83-warm-state)). Transports (Orleans messaging) carry no CRC; the stored formats
verify end to end. Archive-log content carries no CRC-32C of Clog's: each write carries the store's own transport
checksum (on Azure `Content-MD5`), computed from the verified batch pages, and nothing Clog-specific protects
archive content at rest ([archive logs §9.1](components/archive-logs.md#91-azure-append-blobs-azurearchivelogstore),
[verification §13](verification.md#13-qualification-boundaries)).

## 5. Providers

- **One contract, several technologies.** A provider implements one layer's contract for one technology:
  Ledger replica providers (memory and the LedgerStore), MetaStore providers (memory, file system, Fast CASPaxos, Azure
  Blob, Azure Table, DynamoDB, Firestore), LSM object store providers (memory, file system, Azure Blob
  Storage, Amazon S3, Google Cloud Storage), and archive-log storages (memory and Azure append blobs).
- **Construction by code.** The composition constructs each provider with its runtime options; there is no
  reflection or assembly scanning. Durable state names nodes, ledgers, registers, and objects, and records no
  provider or option, except the archive store's record of its kind, its layout, and the parameters its tokens and
  bodies depend on, which fix their meaning for the cluster's life
  ([archive §2.4](components/archive.md#24-selecting-the-store)).
- **Runtime policy stays out of durable state.** Credentials, clients, trust anchors, timeouts, retry policy,
  transport limits, `LedgerReplicaOptions`, and the WAL storage selection (`portable` or `io-uring`) are runtime
  options.
- **Diagnostics are credential-free.** A provider's diagnostics never render credentials or payload bytes.
- **Technology packages.** `Orleans.Durable.Storage.FileSystem` (one host's local file system),
  `Orleans.Durable.Storage.AzureStorage`, `Orleans.Durable.Storage.S3`, and `Orleans.Durable.Storage.GoogleCloud` each
  group one technology's providers for several layers (MetaStore registers, LSM object stores, and, in
  `Orleans.Durable.Storage.AzureStorage`, the archive-log storage) and depend only on layer contracts: those layers'
  abstractions, and for `Orleans.Durable.Storage.FileSystem` the WAL, whose file-system seam flushes directories ([wal
  §9](components/wal.md#file-system-directories)).
- **A provider validates its own inputs.** Naming rules, capacity, key encodings, retry policy, and storage
  limits stay at the provider's boundary; shared contracts carry none of them.

### Design rationale

Keeping runtime policy out of durable state lets credentials and transport policy rotate without rewriting stored
data, and keeps interpretation at each provider. Construction by code gives trimming and AOT a visible dependency
closure. Technology packages share SDK integration across layers while preserving downward dependencies;
separate implementation packages per layer would duplicate that integration, and one technology-neutral
implementation assembly would merge unrelated SDK dependencies. The local file system is a technology of its own: its
providers publish through the WAL's file-system seam, so they share the WAL's qualified directory flushes and
crash-state enumeration records their calls, while each layer's own project depends only on its abstractions.

## 6. Retries and idempotency

- **Prefer the SDK's retries.** Cloud providers use their SDK's retry and timeout policy, configured as runtime
  options.
- **Otherwise bounded, jittered backoff.** Where Clog retries itself it uses `Microsoft.Extensions.Resilience`
  and Polly with exponential backoff, jitter, cancellation, and an idempotency analysis of the operation. Every
  jittered repetition takes its wait from `Orleans.Durable.Storage.Engine.Backoff`, with a jitter its caller draws, so a
  simulation's rewrite of the caller makes it deterministic: the wait before repetition `n` lies between its step, the
  first wait doubled `n` times up to the longest, and twice the step, and never exceeds the longest, so no repetition
  waits less than the first wait. Polly's own jitter can draw a wait of zero, which repeats at once.
  `StorageOutageRetry` (a ledger writer's writes) backs off from 100 ms to at most 5 s with unbounded attempts: a
  storage outage delays an outcome and never decides it.
- **Identical creates.** A create-once write is retried only with identical bytes at the identical address, so a
  retry which meets its own landed create reports success ([LP-I12](components/ledger-protocol.md#5-invariants)). A
  sealed or `Dropped` answer, and a failure which repetition cannot change (an invalid argument, corruption, an
  unsupported operation, disposal; [ledger-replicas §6.4](components/ledger-replicas.md#64-failures)), end the
  repetition; a `Backpressure` answer, which asserts nothing, is repeated.
- **Fresh names for objects.** A commit references an LSM object only after its create returned `Created`, or `Exists`
  for the fresh random name its creator drew, which only that creator's own landed attempt can hold ([archive LSM
  §3](components/archive-lsm.md#3-requirements-downward)); a create which throws is abandoned, and the upload repeats
  under a fresh name. An abandoned object which lands later is garbage the sweeper deletes
  ([LSM-I7](components/archive-lsm.md#5-invariants)).
- **A committer ends a batch whose write has an unknown outcome with one read.** After a ledger map, node registry, or
  manifest write whose outcome is unknown, the committer's one further step for the batch is a read of the register or
  the manifest: it answers each change of the batch whose goal the value read shows; answers each change the held write
  did not carry whose permanently failed commit rule the value read shows; and fails every other change with the write's
  exception, its outcome unknown, as while the store is unavailable ([ledgers
  §2.6](components/ledgers.md#26-transitions), [archive LSM §2.9](components/archive-lsm.md#29-manifest-commits),
  [Orleans §5.3](compositions/orleans.md#53-node-registry)). A change the held write carried is answered only as
  applied, when its goal holds, or fails with the write's exception; it is never answered negatively, since the held
  write may still apply it. A failed read fails the batch with the write's exception. A read which finds the register or
  the manifest corrupt, after an unknown outcome or a mismatch, fails each change a write of the batch carried with an
  unknown outcome whose inner exception is the corruption, since that write may have landed, and every other change with
  the corruption, which every later read meets too; a read after a mismatch which fails does the same with its failure.
  The unknown outcome is a `GroupCommitOutcomeUnknownException`, an `IOException`, or the register's own unavailability
  exception, and a submitter treats it as an unknown outcome, as it treats the store's own failures: it submits the
  change again. The held write lands
  at most once, and only over the value it was decided against ([MS-I3](components/metastore.md#5-invariants)), so an
  answer which a later value could revoke, such as a map change `Refused` for a size a held drop would shrink, or a drop
  `Rejected` which the held write carries after the close which makes it valid, waits until no held write can land
  ([ledgers §2.6](components/ledgers.md#26-transitions)). Within the committer, the store client's own retries are the
  only retries; beyond it, submitters retry: a controller issues its transition again, and a worker or a silo submits
  its change again. A mismatch, after which the held write can never land, is decided again against the value read, as a
  new conditional write. Map, manifest, and registry changes are repeatable: a submission which finds its goal answers
  as the write which applied it did.
- **One group committer.** The ledger map's, the manifest's, and the node registry's committers are one mechanism, the
  group committer `GroupCommitter` (`Orleans.Durable.Storage.Engine.GroupCommit`), which each parameterizes with its
  value's commit rules (`IGroupCommitRules`): the store's read and conditional write, the codec, and the fold of a batch
  into a per-batch decision (`IGroupCommitBatch`) holding the value to write, which changes it carries, each change's
  answer, and the invariants the written value keeps. The mechanism sequences submissions and merges identical pending
  ones, answers each from a value read or written after it was submitted, orders admission against close with an
  `AdmissionGate` (§11), ends an unknown outcome with one read, decides a batch again after a mismatch, waits out an
  optional interval from one write's completion before taking the next batch and before each write (the manifest's
  per-object write rate), and answers each submission exactly once, as a value or an exception (`GroupCommitAnswer`). A
  read returns the value or its corruption as a union (`GroupCommitRead`), and every value is checked to decode as
  itself before it is written, over the finished encoding (`GroupCommitEncoding`), after the encoder took any CRC-32C
  the store verifies. A write the store refuses having written nothing (`GroupCommitWrite.Refused`), as an object store
  refuses content which changed after its CRC-32C was taken, fails each change only that attempt carried with the
  refusal, and no read follows it; a change an earlier, mismatched attempt of the batch carried is answered with an
  unknown outcome around the refusal. The batch loop runs in its owner's execution context, captured when the owner
  created the committer, and in no submitter's (§12). The store's read and write are its only I/O. An exception of the
  rules' fold, decision, or answers, a malformed answer, or a value which breaks the rules' invariants or does not
  round-trip, is a defect: it fails each change no dispatched write carried with `InvalidOperationException`, nothing
  written for it, and each change a dispatched write carried with an unknown outcome around the defect, since that write
  may have landed. The rules' notice of an applied write never throws, and comes after the batch's answers are decided
  and before they are released. A value whose negative answers a held write could revoke, the ledger map's `Refused` and
  `Rejected`, fences in its fold: while the held write may land over the value it was decided against, the fold writes
  that value again at the next version, and the committer decides the batch again against the fence. The fold's
  precondition is that it writes a value carrying no change only as such a fence, at most once per batch: a write which
  carries no change while no held write may land, or after the batch's fence applied, is a defect.
- **Resends above the ledger are safe.** An entry answered `Moved`, `Throttled`, or `Fenced` had no effect and may
  be resent; the version check rejects one which already took effect ([JS-I1](components/journals.md#5-invariants)).
- **Hedging is deferred.** A slow create waits for its own outcome
  ([deferred](deferred.md#hedged-creates)).

### Design rationale for committer retries

**One policy for every committer: one read ends the batch.** A read after a write whose outcome is unknown decides each
change whose goal it shows, and each change whose commit rule it shows failed, since such a failure is permanent: the
ledger map and the manifest only advance past the value each change was derived from, in which its rule held (a caller
precondition, [ledgers §3](components/ledgers.md#3-requirements-downward), [archive LSM
§3](components/archive-lsm.md#3-requirements-downward)), and a registration's rule, once failed, stays failed, since
registry entries only advance ([OR-I2](compositions/orleans.md#51-invariants)). The value read is either the one the
held write was decided against, or a newer one, after which the held write has landed or never will, since a conditional
write lands only over the value it names. A change the held write did not carry, whose rule the value read shows failed,
fails against every later value too, so a `Superseded`, `Stale`, or `Rejected` answer to it stays true. A change the
held write carried had its rule hold against the fold's result before it, which may differ from the value read, so the
held write may still apply it, and the committer answers it only as applied or fails it. Under the precondition, a
carried change's rule fails in the value read only when that value is newer than the held write's, which then never
lands, and failing the change costs only its resubmission, which reads the negative answer; the rule is the defence for
when the precondition is broken, as by a drop derived from a map in which a ledger earlier in its lineage is still
listed, which the same batch drops first. The ledger map committer, the manifest committer, and the node registry
committer follow the same rule. The other changes fail as they would while the store is unavailable, and their
submitters act as they do then: a controller issues its transition again, a worker or a silo submits its change again,
and a submission made after the held write lands finds its goal. Deciding the batch again and writing it again inside
the committer was rejected: a committer which writes again needs an answer for that write's unknown outcome too, which
only a further loop gives, and the loop holds every submitter of the batch, and every batch queued behind it, for as
long as the store keeps failing. Where the store's client is a cloud SDK, which retries each call with bounded backoff,
the loop would also repeat each of the client's attempts; Fast CASPaxos's client retries only a write its acceptors
refused, and the file-system MetaStore stops the process on a failure (§14), so for those stores the submitters'
resubmissions are the retries. One policy for all three committers gives them one rule to state, implement, and test.

**One group committer, parameterized by commit rules.** The policy has one implementation, `GroupCommitter`, so a
change to it reaches every committer at once: three copies had drifted, as when the manifest's merged no identical
submissions and closed no admission gate, and only the node registry's told a defect of its rules from a failure of its
register. Each value supplies its commit rules. The manifest is an object, not a MetaStore register, so the rules carry
the store's read and conditional write, with its own version, besides the codec and the fold; and the core's names are
neutral. The fold returns one decision per batch, so each register keeps what it needs between the fold and the answers
(the map's verdicts, the registry's successions) without the core carrying it. The ledger map's fence is a choice of its
rules' fold, a write of the value the batch was decided against, so the mechanism needs no notion of a revocable
answer; it guards only that a write carrying nothing is a fence. It lies in its own project,
`Orleans.Durable.Storage.Engine.GroupCommit`, beneath the layers whose committers use it, and reaches only the buffers
and, of the threading project, the admission gate, which keeps the MetaStore's contract reaching only the buffers.

**A defect or a corruption after a dispatched write leaves its changes unknown.** A defect or a corrupt read which
follows a write the batch dispatched cannot tell whether that write landed, so each change it carried fails with an
unknown outcome, with the defect or the corruption as its inner exception; a submitter which reads an
`InvalidOperationException` or an `InvalidDataException` as "nothing written", as the transition executor abandons the
ledgers it would have opened, then never abandons what a landed write installed. A defect before any write is
dispatched writes nothing, and `InvalidOperationException` says so. Answering every change with the corruption was
rejected for that reason, and answering around it was rejected because the corruption has to surface.

## 7. Payload size

There is no Clog-wide payload limit. The ledger replica and the ledger protocol reject only empty payloads. Each
transport and provider enforces and reports its own bound at its own boundary:

| Bound | Default | Owner |
| --- | --- | --- |
| Replica call | A payload through the Orleans replica transport is at most the payload limit, `min(MaxNodeCallBytes / 2, MaxReplicaCallBytes / 4, MaxMessageBodySize − 64 KiB)`, 32 MiB by default, so no one call takes a node's whole budget, and a larger one fails with `ArgumentException` before sending, to any node. A caller keeps at most `MaxNodeCallBytes` (64 MiB) of replica calls' request payloads and remote replies outstanding to each node, each remote read reserving the reply it accepts before it is sent, a whole read `MaxReadReplyBytes` (1 MiB), as the per-node budget counts reply bytes; a longer batch is answered `TooLarge` and read again reserving its length. A call waits for that budget in the node's FIFO lane, which no later call overtakes, and fails unsent with `LedgerReplicaQueueTimeoutException`, no effect, only at its deadline. Replica calls never wait on other calls, so their budget is their own: no call waits for budget behind a call whose completion depends on it. Each silo's replica host admits a call only while its held request payloads and DATA replies, each admitted read counted at the reply its caller reserved, stay within `MaxReplicaCallBytes` (128 MiB), which bounds what it holds until each reply is serialized | [Orleans](compositions/orleans.md#512-replica-transport) |
| Proposals in flight | Each `LedgerWriter` holds at most `MaxProposalBytes` (64 MiB) of batches created and not yet decided, and waits before creating a batch which would pass it; the window `K` caps their count | [Ledgers](components/ledgers.md#24-primary-ledgerwriter) |
| Forwarded entries and reads | A forwarded write carries its record in one call, at most the payload limit, and waits for the destination node's forwarding budget (`MaxForwardCallBytes`) in a FIFO write lane of its own, separate from the replica calls' and from first read pages; a deadline passed there is `ForwardQueueTimeout`, a no-effect outcome. A forwarded read pins one snapshot on the owner, one archive read and the live rows through its cut, within the node's forwarded-read budget (`MaxForwardedReadBytes`), reserved before the first page, which waits in its own read lane (`MaxForwardReadCalls`), and returns it in pages of at most 1 MiB with no restart; a pinned snapshot's later pages take no lane, one page per snapshot at a time, never queued behind first pages which wait for a snapshot's release | [Orleans](compositions/orleans.md#55-forwarding-and-journal-outcomes) |
| Fast CASPaxos client messages | 4 MiB send and receive | [MetaStore](components/metastore.md#8-mechanism-and-policy) |
| MetaStore value, including the ledger map | Each backend's own (Azure Table 64 KiB property, DynamoDB 400 KB item, Firestore 1 MiB document) | MetaStore providers |
| WAL record | `int.MaxValue` bytes | [WAL](components/wal.md#2-contract-upward) |
| Key | 65,535 bytes | [Ledgers](components/ledgers.md#21-keys-and-ranges) |
| Journal key | 65,531 UTF-8 bytes | [Journals](components/journals.md#41-keys) |
| Entry body (`MaxBodyBytes`) | 16 MiB by default, fixed per cluster in the archive store's record and configurable up to the payload limit less a maximal row's overhead (a 65,535-byte key, a 32-byte token and version base, and headers: about 64 KiB); every body is inline, and a larger `Append` or `Replace` is refused with `ArgumentException` before admission | [Journals](components/journals.md#88-policy) |
| Entries queued | `MaxQueuedBytes`, 64 MiB per ledger's admission: entries accepted and not yet in a batch | [Journals](components/journals.md#88-policy) |
| Batch | 256 entries and 1 MiB by policy (`MaxBatchBytes`), or one row alone when its body is larger; at most the replica's payload limit | [Journals](components/journals.md#88-policy), [Ledgers](components/ledgers.md#7-formats) |
| Archive `Append` | `MaxAppendBytes`, 100 MiB on Azure (`Append Block`), recorded per cluster | [Archive logs](components/archive-logs.md#22-archive-implementation) |

A process checks at start that its limits nest, and refuses to start otherwise: the largest batch Journals' policy
admits, `max(MaxBatchBytes, one row of a 65,535-byte key and a MaxBodyBytes body)` with its headers, is at most the
payload limit; `MaxBodyBytes` is at most the archive store's `MaxAppendBytes`, so no `Append` spans units of the
archive; and `MaxProposalBytes` and `MaxQueuedBytes` are each at least that largest batch. In-flight memory is so
bounded by bytes, not by slot count: a primary holds at most `MaxQueuedBytes + MaxProposalBytes` per ledger (128 MiB by
default), plus the copies in transit which `MaxNodeCallBytes` bounds per destination node. A `MaxProposalBytes` above
what the per-node budgets let a ledger keep in transit adds memory and no throughput.

A lower layer's capacity is never reduced to fit a higher layer's transport, benchmark, or UI default; a layer
which needs larger values gets a segmented or chunked `ArcBuffer` API at that layer.

## 8. Checked arithmetic

Address and sequence arithmetic is checked where it can reach the end of its space, and reaching the end fails
clearly instead of wrapping:

| Boundary | Behavior |
| --- | --- |
| `Slot` arithmetic and construction | Overflow throws `OverflowException`; a negative result throws `ArgumentOutOfRangeException` |
| In-process sequencer | Granting slot `2^63 − 1` throws `OverflowException` |
| LedgerStore slots and handles | A slot at or above 2<sup>40</sup> is rejected; exhausting the 2<sup>24</sup> − 1 handles (handle 2<sup>24</sup> − 1 keys create, seal, and drop records) fails creation |
| Ledger IDs | A map change which would advance `NextLedgerId` past `2^64 − 1` fails with `OverflowException` |
| Key ranges | `Children` of a range at depth 524,280, and `Parent` and `Sibling` of the whole keyspace, throw `InvalidOperationException` |
| Batches | An encoding longer than `int.MaxValue` bytes fails with `OverflowException` |
| Row versions | A row whose version's count is at `2^64 − 1` is refused: `RowVersion.Next()` throws `OverflowException`, and Journals refuses such an entry with `ArgumentException` before admission |

No layer adds encodings, negotiation, or fallback formats to make every theoretical value representable.

## 9. Citations in code

- **Paper quotations.** Code which implements an algorithm or invariant from the LogDrive paper, Delos, or the
  Delos erratum quotes the paper verbatim in an adjacent comment, short and focused, and cites the file and
  section, for example `(research/papers/delos-erratum/paper.md, Correction)`. Papers which are not checked in
  (*Log-Structured Protocols in Delos*, *Hints for Computer System Design*, *Vertical Paxos and
  Primary-Backup Replication*) are cited by title and section.
- **Invariant IDs.** Code which enforces or relies on an invariant cites its ID and file, for example
  `// LG-I5 (ledgers.md#5-invariants)`. Code cites the owning specification section; the
  [traceability table](invariants.md) lists each invariant's enforcing code and evidence.

## 10. One current representation

Clog maintains exactly one current form of each API, configuration schema, wire contract, and durable format.
A change replaces the superseded form in place and updates its callers, documentation, and golden vectors
together ([formats](formats.md#7-rules-for-format-changes)). A compatibility obligation requires an explicit
spec change stating the affected surfaces and qualification before compatibility code is written
([build and packaging](build-packaging.md#5-representation-and-compatibility)).

## 11. Admission and close

A component whose operations must not run once it is disposed, dropped, or stopped orders each operation's
admission against that close with one `AdmissionGate` (`Orleans.Durable.Storage.Engine.Threading`), not with flags and
counters of its own.

- **Admission is atomic against close.** `TryEnter` returns a scoped token; `TryEnterUnscoped` admits an operation
  whose ownership passes elsewhere (a callback, a lease, a resolution which outlives its caller), and is paired with
  exactly one `Exit`. One atomic word holds the closed bit and the count of entry attempts, so an operation is
  admitted exactly when it entered before `CloseAsync`, and no admission races a close.
- **Close is permanent and drains.** `CloseAsync` closes admission before it returns, and its task, shared by every
  caller, completes once every admitted operation has exited. `IsClosed` reports the close; an owner which closes
  the gate under a lock of its own reads it under that lock. An owner closes the gate, then cancels whatever its
  admitted operations wait on so that they exit, awaits the drain, and only then releases what they use.
- **A rejected operation answers as its owner decides:** `ObjectDisposedException`, or the layer's own answer (a
  dropped LedgerStore ledger's `Dropped`).
- **The gate neither cancels nor answers work, and never reopens.** An owner which holds queued work answers it on
  close itself. A count which also drives other state under an owner's lock stays under that lock.

Gates order admission against close in a dropped LedgerStore ledger
([ledgerstore §6](components/ledgerstore.md#6-faults-concurrency-and-cancellation)), a `Ledger`'s operations and a
`LedgerWriter`'s slot writes and completions
([ledger-protocol §6.3](components/ledger-protocol.md#63-cancellation-and-disposal)), and the journal index's reads
([journals §8.3](components/journals.md#83-warm-state)).

`AdmissionGate` is adapted from Orleans' `AdmissionGate` (MIT, `src/Orleans.Core/Async/AdmissionGate.cs` in
`dotnet/orleans` at commit `8c4887aba920b9250ad2914a039777a43781603f`) and maintained as Clog code with its own tests;
Clog adds `IsClosed`. `Orleans.Durable.Storage.Engine.Threading.Tests` holds the ported tests (`AdmissionGateTests`) and
checks every sequence of entries, exits, and closes against a model, and that under concurrent entry no operation is
admitted after `CloseAsync` returns or runs after the drain completes (`AdmissionGatePropertyTests`).

## 12. Shared workers and execution context

Work runs in the execution context of the code which started it, so its ambient state (an `Activity`, a test's
`ArcBuffer` page accounting, the test a failed debug assertion is attributed to
([verification §11.2](verification.md#112-debug-assertions))) belongs to its starter. A worker which may serve more than
one caller, such as a process-wide thread, keeps no caller's context of its own: it starts without capturing one
(`Thread.UnsafeStart`, or under `ExecutionContext.SuppressFlow`), and runs each work item in the context of the caller
which submitted it, as the thread pool does. The state database's `DedicatedThreads`, which RocksDB's process-wide
readers and checker use, follows this rule (`DedicatedThreadsTests`). A worker which one owner creates and owns, such as
a WAL storage's io_uring thread or a group committer's batch loop (§6), runs in its owner's context. An owner which
creates such a worker inside a caller's flow, as a grain does in the call which activates it, under that call's
`RequestContext` and Orleans' activation `Activity`, creates it under `ExecutionContext.SuppressFlow`, so the worker
runs in no context rather than in the caller's; the controller grain builds its node registry committer so
(`TheRegistryCommitterRunsInNoRegisteringCallersContext`).

## 13. Durable answers

A crash never revokes an answer. Every answer a component gives holds in every state the system can recover to after
any failure of [system model §3](system-model.md#3-failures-tolerated), from the moment the answer leaves the component
([engineering principles](../engineering-principles/correctness.md)). An answer is anything another component or the
application can act on: a result, an exception and its reason, a callback such as the WAL's `OnDurable`, a published
value such as `DurableFrontier`, a catalog, a listing, a version, or a grain reply.

- **Durable first.** A component makes durable what an answer asserts before the answer leaves: a write's record, a
  create, seal, or drop record, a conditional write's acknowledgment, the decision of a value it returns, and every
  write a frontier, a tail, or an absence covers. State another operation can observe is published from its record's
  durable completion, never at its submission.
- **In-flight changes.** An answer which reflects a change, such as a create, seal, or drop record or a commit (the
  change's effect, or a refusal it causes), waits for the change's durable outcome and answers from it. When the
  change fails, or the component closes first, the operation answers with that failure. Concurrent operations need
  no serialization: an operation with no happens-before relation to an in-flight change answers from the durable
  state before it. A read which meets a replica's create answers that the replica is absent, a read of DATA which
  meets a seal answers the DATA, and a read which meets the drop of a replica the node does not hold answers that the
  replica is absent. A repeated seal, a sealed answer, a `Dropped` answer, and every other answer which reports the
  change's effect wait.
- **Outcomes which assert nothing.** Unavailability, an unknown outcome (`Ambiguous`), `Throttled`, and cancellation
  assert nothing about state ([outcomes §8](outcomes.md#8-error-taxonomy)), so a component may give them at any time.
- **Derived answers.** A composition counts only durable answers: a quorum's `Written` counts `Written` answers, a
  seal's coverage counts `Sealed` and `Dropped` answers, and a forwarded reply relays its source's answer.
- **Hints.** A cache, routing, or placement entry asserts nothing durable. No safety decision counts it, and its user
  recovers through the authoritative path when it is stale. A cache whose entries hold only values the authority
  durably held, such as a cached ledger map, is stale but durable, and a decision which no later value can invalidate
  may count it ([outcomes §8](outcomes.md#8-error-taxonomy)).

- **Type-enforced durability barriers.** Hot-path state machines (`WriteAheadLog`, `LiveReplica`, `LedgerStore`,
  `GroupCommitter`) enforce the durable-first rule at compile time with zero-allocation `readonly struct` proof tokens
  whose constructors are restricted (`private` or `internal`) and whose members reject `default`:
  - `WriteAheadLog` mints a `SyncedGroup` only after `WalFile.WriteAsync` returns, marks pages durable only from that
    token, and delivers a `WalDurableRecord` to `WalWriteRequest.OnDurable(WalDurableRecord)` only when every page
    holding the record is durable; `DurableFrontier` advances only from completed durable callbacks, while the failure
    path (`FinishFailed`) invokes only `OnFailed`.
  - `LedgerStore` and `LiveReplica` publish slot writes (`DurableSlotWrite`), seals (`DurableSeal`), creates
    (`DurableCreate`), and drops (`DurableDrop`) strictly inside `WalWriteRequest.OnDurable(WalDurableRecord)` (or
    from checkpoint/WAL recovery), and produce `Written`, `Sealed`, sealed `LedgerReplicaTail`, and `Dropped` answers
    only from those proof tokens.
  - `GroupCommitter` mints `DurableCommit` only from a confirmed `GroupCommitWrite.Applied` CAS outcome, `DurableRead`
    only from a completed `GroupCommitRead.Found` read, and `FreshDurableState` only from a valid `DurableCommit` or
    `DurableRead` whose pre-I/O submission sequence is at or above the read-only batch's latest submission; each
    settlement helper (`SettleApplied`, `SettleReadOnly`, `SettleAfterUnknownOutcome`) requires its corresponding proof.

### Design rationale for type-enforced durability barriers

Using zero-allocation `readonly struct` proof tokens with non-public constructors and runtime non-default guards keeps
the hot paths free of extra heap allocations and Clockwork scheduling points while making premature acknowledgments
(answering `Written`, `Sealed`, `Dropped`, or `Applied` before storage sync or CAS completion) structurally
unrepresentable during refactors. Guarding every token accessor and boundary against `default` closes C#'s implicit
parameterless struct value hole without allocating reference witnesses. Test-only durability mutants (such as
`GroupCommitter.RecordsSequenceAfterIoMutant`) remain explicitly gated behind dedicated test flags so dynamic tests
keep verifying the barrier alongside compile-time enforcement.

Each component's spec names the record which makes each of its answers durable. Crash-state enumeration checks that
every answer given before a crash point holds after recovery
([verification §7.5](verification.md#75-crash-state-enumeration)), and the models give each answer their protocols
count a mutant which answers before its record is durable, such as the ledger model's `write-volatile`, whose restart
revokes a `Written` answer (`NoRevokedAnswer`)
([verification §5](verification.md#5-models)).

## 14. Local storage failures stop the process

A failed local storage operation stops the process
([engineering principles](../engineering-principles/correctness.md)), and so does a defect which would leave a
component's memory out of step with the log it follows: its write-ahead log, or, for Journals' warm state, its ledger's
decided rows. Local storage is a node root's disk and a host's volatile disk:
The WAL's files and directories, the LedgerStore's node file and checkpoints, cluster claims, the state database, the
file-system MetaStore and object store, and the block cache. After a write, flush, or read fails, the process cannot
know what the device holds: Linux can report an `fsync` error once and mark the failed pages clean. After such a
defect, the process's memory no longer follows what its log holds. Either way only the next incarnation answers again.
It recovers from what the device holds, through the crash path each component's crash tests exercise, so no answer
rests on a state the device does not hold ([§13](#13-durable-answers)). Restarting or replacing the host is the
orchestrator's.

The [object-store ledgers](components/object-ledgers.md)' cache is the exception: it holds only
batches the object store holds, each checked by its CRC-32C on every hit, and answers nothing it cannot read, so a
failed read or write of it discards the entry, or the whole cache, and the read goes to the store, the trivially safe
recovery in place ([OL-I11](components/object-ledgers.md#5-invariants)).

- **The seams report storage failures.** Every local storage operation goes through a storage seam: the WAL's file
  system (`WalFileSystem`), through which the WAL's directories and portable files, the LedgerStore's node file,
  checkpoints, and measurement of its disk, cluster claims, the file-system MetaStore and object store, and the block
  cache reach the disk; the io_uring storage's files and ring; and Journals' RocksDB journal index
  (`RocksDbJournalIndex`). When a write, flush, rename, directory flush, create, delete, truncate, or read fails, or the
  disk is full, the seam reports the site, the path, and the error to the process's fatal-error handler,
  `FatalErrorHandler.StopOnStorageFailure` of `Orleans.Durable.Storage.Diagnostics`. The seams alone report storage
  failures.
- **Owners report defects.** A component reports a defect which would leave its memory out of step with the log it
  follows where it detects it, through `FatalErrorHandler.StopOnDefect`, which names the defect. The WAL reports a
  throwing outcome callback (`WalOutcomeCallback`), a throwing placement of records (`WalPlacement`), and a write
  group, roll, or discard which failed with an error no seam reported (`WalWriteGroup`, `WalRoll`, `WalDiscard`). The
  state database reports a flush which failed with an error that is neither corruption nor reported (`StateFlush`). The
  LedgerStore reports a throwing publication of a durable record (`LedgerStoreRecordPublication`,
  `LedgerStoreSlotPublication`), a seal or drop which failed with an error no seam reported (`LedgerStoreSeal`,
  `LedgerStoreDrop`), and a failure of its maintenance (`LedgerStoreMaintenance`), which would otherwise end
  checkpoints while the WAL grew without bound. Journals' admission reports a `Durable` entry which does not apply to
  its warm state (`JournalsWarmState`), which then no longer folds its ledger's decided rows
  ([journals §8.1](components/journals.md#81-admission)). The archive's drain worker, compaction worker, and manifest
  committer report an object write which the store refused because its content no longer matches the CRC-32C its
  writer computed when it verified it (`ObjectContentChanged`): the process's memory changed after verification, which
  may have damaged other in-flight state ([archive LSM §9](components/archive-lsm.md#9-providers-and-implementations)).
- **A failure is reported once.** The handler records each failure it takes, so a seam's failure which reaches a
  component's catch, itself or as an inner exception, is known as reported (`FatalErrorHandler.IsReported`), and a
  second report of it does nothing. Each component stops through one helper, which reports its failure as the
  component's defect unless a seam reported it, before it completes any request or releases anything a waiter
  observes. Components never end the process themselves, and none retries a failed operation, swallows it, or keeps
  the process alive in a degraded state.
- **One exit code per storage site, and one defect code.** Exit codes are values of `FatalErrorSite` in the block
  79–125, clear of sysexits (64–78), the shell's 126 and 127, and the statuses of a signal (128 and above). Each storage
  site takes its own code from 80, which one production site names, so operators tell storage failures apart by it.
  Every defect takes 79 (`Defect`), and its report and the process's message name it, as `WalRoll`: a defect is read
  from its message and its exception, and needs no code of its own. Operators see the codes, so they are a contract.
- **The exit is immediate.** The handler writes one line to standard error, naming the exit code, and the defect or,
  for a storage failure, the site, the path, the error, and the operating-system error. It waits for the write at
  most 5 s, on a thread of its own: a full disk behind standard error fails the write, and a pipe which no reader
  drains stalls it.
  It then ends the process with `_exit` on Unix and `TerminateProcess` on Windows, whether or not the write succeeded.
  The process runs no `ProcessExit` handler, finalizer, hosted service's stop, or native static destructor. So no
  component's shutdown writes storage after the failure, no third-party handler can hang the exit, and RocksDB's
  destructors cannot crash while its background threads run and replace the site's code with a signal's. The
  reporting thread never returns, and a later report waits for the first's exit, so no answer rests on the failed
  operation.
- **Answers are not failures.** A missing file or directory, an existing name (`WalNameExistsException`), a lock
  another owner holds (`WalLockHeldException`, and RocksDB's contention for its `LOCK` file), a volume which cannot do
  direct I/O (`PlatformNotSupportedException`), and the end of a file are answers the storage gives, which no seam
  reports. An existence check never reads an error as absence.
- **A missing directory answers only for a directory operation.** An existence check, a listing, and the creation of
  a directory whose parent is missing answer that a directory is missing, and an open of a missing WAL directory
  answers it once its parent's flush makes the absence durable (WAL-I12). A file operation or a directory flush which
  finds its directory missing reports a failure: a directory its component created or was given has gone, as when its
  volume vanished.
- **Contention is an answer.** On Windows, a rename or deletion which another process's open handle refuses, as
  antivirus and indexing services refuse one while they briefly hold a new file without sharing deletion, and an open
  which another handle's sharing mode refuses, change nothing. The seam repeats the operation, pausing from 1 ms and
  doubling to 100 ms, for up to 30 s on its clock, after which the refusal is a failure. A caller which holds a lock
  that appends wait on pauses outside it. A lock file's open which another handle refuses answers that the lock is
  held (`WalLockHeldException`), except in a directory's deletion, which no owner holds: there the lock's open and the
  lock file's deletion, which comes last, wait out another handle on the lock file as contention, and a handle still
  there past the bound, or an arriving owner's new lock file, answers that the lock is held, not a failure
  ([wal §9](components/wal.md#9-providers-and-implementations)). A caller which meets that answer leaves the
  directory for a later attempt.
- **Corruption is not a failure of the operation.** Bytes read successfully which fail their CRC-32C are corruption
  ([§4](#4-integrity)), handled by the layer's corruption rules.
- **In-place recovery.** A component may handle a failure itself only when its recovery is trivially simple and safe:
  it answers nothing from the failed state and leaves only a state the crash path already handles. Retrying the failed
  operation is never such a recovery. No component keeps one: a checkpoint whose unpublished file fails stops the
  process too, and the next open removes what it left.
- **Remote storage fails per its contract.** Object stores and registers report unavailability and unknown outcomes
  ([§6](#6-retries-and-idempotency)).

**Tests and simulation.** The handler's `Interceptor` takes a report in place of the stop, and no production type sets
it. The test host installs one recorder (`FatalErrors`), which records each report with the test whose execution
context raised it. That context flows into the code a Clockwork simulation rewrites, so parallel tests never see each
other's reports. Within a test, a storage failure's path names the failed node, since every node root is unique within
a test. After an intercepted report, the reporting code rethrows the failure, and the storage double which injected it
(`SimulatedWalStorage`, the recording file system) stops where the process would, so nothing the process does
afterwards reaches the device. Its later calls fail with an error which carries the reported failure, and a crashed
process's calls fail with `CrashedProcessException`, whose reports the recorder drops. Every component answers nothing
after an intercepted report: each later request throws the reported failure, as a stopped process's callers see it
fail. A test takes the reports its injected failures raised and fails on any other. In a seeded simulation, a node
whose storage failed answers nothing more and crashes and restarts. The driver takes each report after the step which
raised it, and accepts only a storage failure which the injection of the node whose root holds its path caused; any
other report, a defect among them, fails the run ([verification §7.4](verification.md#74-seeds-and-replay)).

## 15. Outcomes, unions, and errors

An operation returns what its caller decides on, and throws what its caller propagates.

- **The caller's decision picks the representation.** An operation returns as a value each answer its immediate
  caller decides on, whatever the answer's class in the [error taxonomy](outcomes.md#8-error-taxonomy): a definite
  outcome (`Written`, `Missing`, `Applied(Epoch)`); an unknown outcome the caller resolves (`Ambiguous`, or an append
  `Unresolved`, with the slot it wrote if it wrote one, which the primary resolves from the recovered tail); a
  replica's `Dropped` and `Backpressure`, which a `Ledger` decides on
  ([ledger-protocol §6.4](components/ledger-protocol.md#64-failures)); a defect's
  `Rejected(Reason)`, on which its submitter stops; and damage its owner classifies in band, as the WAL's
  `WalCorruption`. An answer the caller propagates is an exception: corruption at a layer's API, unavailability,
  configuration, usage errors, and cancellation. A local storage failure goes to the fatal-error handler (§14). Every
  exception a layer lets escape falls in one class of the taxonomy; the classes are the closed set a layer's Rust error
  `enum` mirrors, its unavailability variant carrying a dependency's own error.
- **Shapes.** Each kind of value has one C# shape and the Rust shape it ports to:

  | Value | C# | Rust |
  | --- | --- | --- |
  | An answer which may be absent | `T?` | `Option<T>` |
  | An operation's outcome, with or without data | Union | `enum` |
  | A mode or a kind | `enum` | Fieldless `enum` |
  | Fields always present, outside a union's cases | `readonly record struct` | `struct` |
  | A decoded value or the rejection of its input | Union of the value and `InvalidDataException`, or, for a per-record decoder, `InvalidDataException?` with the value through `out` | `Result<T, DecodeError>` |
  | A check which passes or names its rejection | `InvalidDataException?`, null when valid | `Result<(), DecodeError>` |
  | A concurrent operation's value or the exception which ended it | What its completed task holds | `Result<T, E>` |
  | A layer's exceptions | Exception types, each in one taxonomy class | Error `enum`, propagated with `?` |
  | Owned bytes | `ArcBuffer` | `Bytes` |

  Lookups keep the platform's `bool` and `out` idiom (`TryGetValue`, and `TryParse` of primitives), and an input's mode
  is a flag (`recovery`, `seal`).
- **Representation.** A union is a `readonly union` declaration. A case new to the union is a `sealed record` nested
  in it and used by that union and by the unions which list its cases; a unit case is one shared `Instance` behind a
  private constructor. A type the union carries whole, such as a decoded value, an `InvalidDataException`, an
  `ArcBuffer`, or a `WalCorruption`, is a case as it stands. A union which another would carry contributes its cases
  instead: the outer union lists them beside its own, as `JournalForwardedWriteOutcome` lists `JournalWriteOutcome`'s
  cases with `Throttled` and `NotThisNode`. The case list names nested cases by their qualified names:

  ```csharp
  public readonly union LedgerMapCommitOutcome(
      LedgerMapCommitOutcome.Applied,
      LedgerMapCommitOutcome.Superseded,
      LedgerMapCommitOutcome.Refused,
      LedgerMapCommitOutcome.Rejected) : IEquatable<LedgerMapCommitOutcome>
  {
      public sealed record Applied(ulong Epoch);

      public sealed record Superseded(ulong Epoch);

      public sealed record Refused
      {
          private Refused() { }

          public static Refused Instance { get; } = new();
      }

      public sealed record Rejected(LedgerMapRejection Reason);

      public bool Equals(LedgerMapCommitOutcome other) => Equals(Value, other.Value);

      public override bool Equals(object? obj) => obj is LedgerMapCommitOutcome other && Equals(other);

      public override int GetHashCode() => Value?.GetHashCode() ?? 0;
  }
  ```

  A declaration holds its value as `object?`: a record case by reference, a struct case boxed. Per-record and per-slot
  paths (WAL scans and reads, the replica contract, a ledger's appends) use a struct union written by hand for the
  non-boxing pattern, as `WalEntry` ([WAL §4](components/wal.md#4-data-types)), the replica contract's `ReplicaRead`
  and `ReplicaTailRead` ([ledger-replicas §4](components/ledger-replicas.md#4-data-types)), and a ledger writer's
  `AppendOutcome` ([ledger-protocol §2.4](components/ledger-protocol.md#24-append-outcomes)) are: one field per case,
  `[Union]`, `IUnion`, `HasValue`, and a `TryGetValue` per case. A union whose cases are all shared instances, such as
  `ReplicaWriteOutcome` and a `Ledger`'s `LedgerWriteOutcome`, allocates nothing, so it stays a declaration on those
  paths too.
- **Union laws.** The compiler trusts a hand-written union's members, so one property holds every hand-written union
  to these laws: for a held value, exactly one `TryGetValue` answers true, with that value; `HasValue` is true exactly
  when `Value` is non-null; repeated access agrees; a `default` value holds nothing; where the union declares equality,
  two values are equal exactly when they hold the same case with equal values, `GetHashCode` agrees, and equal fields
  in different cases (`WalTorn(a, b)` and `WalCorruption(a, b)`) differ; and a type-pattern switch in a warmed loop
  allocates no bytes (`GC.GetAllocatedBytesForCurrentThread`). One generic CsCheck harness in `tests/shared` checks
  them through an adapter per union, each case in its own page leak scope
  ([verification §11.1](verification.md#111-leak-checks)); a self-test runs it against unions which each break one law,
  and a test fails for any hand-written union in `src` (a `[Union]` type which declares `HasValue`; the compiler marks
  declarations `[Union]` too) whose adapter no test passes to `UnionLaws.Check`.
- **Matching.** A switch over a union names every case and ends with `null => throw new UnreachableException()` as
  its only other arm, so a new case fails every switch which misses it (CS8509, an error under warnings as errors), and
  a default union fails at the switch which meets it. A reader of one case uses an `is` pattern, or a hand-written
  union's `TryGetValue`, when every other case takes one path. The code which uses a union matches it; the union's own
  members compare its `Value` and never match on `this` (Sonar S3060). Case types are non-nullable, and an operation
  returns or sends only a union which holds a value, which it asserts first: `Debug.Assert(outcome.Value is not null)`
  for a declaration, which has no `HasValue`, and `Debug.Assert(outcome.HasValue)` for a hand-written union. A switch
  over an `enum` names every member and ends with a throwing discard arm.
- **Equality.** A union whose values are compared, by tests, specs, or a committer merging identical submissions,
  implements `IEquatable<T>` over its value, as above; a case holding a collection compares it element by element. A
  union with an `ArcBuffer` case leaves equality to the code which owns the bytes.
- **Names.** A verdict is `{Operation}Outcome` (`LedgerMapCommitOutcome`, `JournalWriteOutcome`), a read's answer
  `{Thing}Read` (`WalRead`), a decoder's union `{Format}Decoding` (`CatalogDecoding`), and any other union the noun
  for what it holds (`WalEntry`). Cases carry the spec's outcome names and nest in their union
  (`LedgerMapCommitOutcome.Applied`); a field such as an epoch belongs to each case which carries it. Where the spec
  declares a case's fields, it writes them as the case's properties: `Applied(Epoch)`, `Superseded(Epoch)`,
  `Refused`, or `Rejected(Reason)`.
- **Wire.** The Orleans composition carries each union which crosses silos as a surrogate of its own, a case tag and
  the case's fields, registered as an Orleans converter, whose encoding of every case a snapshot pins; decoding an
  unknown tag throws `InvalidDataException`. Serializer attributes live on the surrogates, in the composition
  ([dependency rules](README.md#25-dependency-rules)).
- **Decoders.** A decoder which runs once per file or object, or whose value is a heap object, returns a union of the
  decoded value and the `InvalidDataException` naming the first rule the input breaks, declared beside it
  (`CatalogDecoding`, `StoreRecordDecoding`), with each check returning its rejection and success returning the value. A
  per-record decoder whose value is a struct or a view (batches, journal entry records, WAL pages and records) returns
  `InvalidDataException?`, null when the input is valid, and gives its value through an `out` parameter, as
  `BatchView.Decode(buffer, out view)` does, so a valid input allocates nothing; a plain struct value may instead take a
  hand-written union. A decoder's sub-checks return `InvalidDataException?` and are named `Check…`, or return the reason
  code as a string and are named `…Reason` where a `bool` probe shares them. A probe which tests many candidates, as the
  WAL's search for a file's last page does, keeps a `bool` predicate, and the exception is built where a rejection is
  reported. The exception carries the rule's reason code, the code of the format's golden vectors and fuzz oracle, in
  `Data["orleans.durable.storage.decode.reason"]`, which `Orleans.Durable.Storage.Engine.DecodeRejection` writes and
  reads, and the owning section of each format lists its codes in check order. CA1806, at warning severity, lists every
  synchronous validator in `additional_use_results_methods` by its Roslyn documentation ID, so a validator's result is
  consumed: an architecture test checks the list against every synchronous method of `src` returning
  `InvalidDataException?`, and that CA1806 reports a canary which discards a listed one. An asynchronous validator (the
  LedgerStore's checkpoint object reads and tree loading) returns a task, which its own component awaits and tests.
- **Classification.** The layer which owns a format classifies its rejections, from the reason and from where it met
  the bytes: the WAL reports the pages recovery recorded as a crash's hole as `WalTorn` and other invalid pages as
  `WalCorruption`, and a known magic with an unsupported version as `WalUnsupportedFormatException`. A rejection its
  owner reports as corruption at its API is thrown once: a layer with a corruption exception throws it with the
  rejection as its inner exception, and a layer without one throws the rejection. Layers above receive the classified
  answer.
- **Platform types.** Every shape is the platform's ([engineering principles](../engineering-principles/design.md)):
  `T?`, union declarations and the non-boxing pattern over `IUnion`, `InvalidDataException`, and the value or exception
  a completed task holds. That last is a meaning, which a quorum's fan-out keeps whatever it collects its replicas'
  answers in, a pooled collector included.

### Design rationale for outcomes and unions

A caller which branches on an answer is acting on an outcome, whatever the answer's error class. Returning it as a
value puts the branch in the signature, where the compiler checks that every case is handled, and keeps a frequent
outcome, such as a dropped replica during reconciliation or backpressure under load, off the cost of a throw. Answers
which only propagate stay exceptions: C# has no `?` operator, so a returned failure would be checked and returned again
at every `await`, with each step a way to drop it unseen, and platform and SDK code throws regardless. The taxonomy's
classes map onto a layer's Rust error `enum` propagated with `?`, so the port moves the same line rather than
redrawing it. Returning `Result<T, LayerError>` from every operation was rejected for that reason.

A union lists cases which share no base: a type it carries whole needs no wrapper, and a case new to it nests in it. A
closed record hierarchy also matches exhaustively, but its cases need a common base, convert to it only through their
constructors, and stay open to extension below each case which is not closed itself. A `union` declaration is concise
and checked by the compiler; a data case costs one allocation per value, which per-record and per-slot paths avoid with
the non-boxing pattern. A union inside another would be boxed in the outer's `object?`, so the outer lists the inner's
cases. The compiler trusts the non-boxing pattern's members, so one property checks them for every hand-written union;
a source generator for the pattern waits until hand-written unions are numerous. The compiler requires a `null` arm
only when a case type is nullable, and a default union reaches a switch without one as a `SwitchExpressionException`
far from its cause, so every union switch names that state as unreachable.

The platform supplies each shape, so Clog adds none. .NET 11 ships the union support types (`IUnion`, `UnionAttribute`)
and no option or result type, and the C# design team closed standard unions
([csharplang#9565](https://github.com/dotnet/csharplang/issues/9565)). Nullable types are the option: the compiler
tracks them, they allocate nothing, and they map one-to-one onto Rust's `Option`. A decoder's union over a heap value
and `InvalidDataException` adds no allocation to a valid decoding; per-record decoders, whose values are structs and
views decoded once per record, return the nullable rejection instead, which allocates only for a rejected input; a store
record is a heap object, which its union carries without a further allocation. The exception's `Data` carries the reason
code the fuzz oracles compare, one key for every format. A completed task already holds a value or an exception. A
generic `Result<T, E>` and a decode-error type in a shared project were rejected: they would duplicate what these give,
in a type the Rust port renames anyway.
