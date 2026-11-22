# System and fault model

This file states the environment Clog runs in, the failures it tolerates, the assumptions outside which it
makes no promise, and the storage behavior it relies on. Each component's "Requirements downward" section
refines these for its layer; each composition states how it meets them.

## 1. Processes, nodes, silos, and disks

| Element | Definition |
| --- | --- |
| Process | An operating-system process hosting Clog components: an Orleans silo, a Fast CASPaxos host, or an application which embeds the libraries. Processes communicate only by messages and shared storage services. |
| Host | A process which runs the data plane beside the application, with one node root and a volatile disk; in the Orleans composition, a silo. A host serves one cluster. |
| Node | The durable identity of one LedgerStore, a random UUID held in its node file beside its WAL ([identity](identity.md#2-node-and-silo)). A node exists for as long as its disk does. |
| Disk | The durable storage under one node root. It holds the cluster claim, the node identity, the WAL, the catalog and slot-index checkpoints, and the journal index ([formats](formats.md#5-local-storage-layout)). |
| Volatile disk | A host's local storage whose contents need not survive a restart or replacement. It holds the LSM block cache, which verifies every block's CRC-32C ([archive LSM](components/archive-lsm.md#87-caches)). |
| Silo | One Orleans process incarnation of a node, named by its `SiloAddress` with a generation which increases on every restart. Silos exist only in the [Orleans composition](compositions/orleans.md). |
| Cluster | The processes and storage which share one cluster identity: one MetaStore namespace, one LSM prefix, and the node roots which record it ([identity](identity.md#1-cluster)). Clusters sharing a backend never affect each other. |
| Storage service | A cloud object store (LSM), or a cloud conditional-register service or Fast CASPaxos deployment (MetaStore), reached over the network. |

Clog never relies on a process's memory surviving the process. Everything a layer must recover is durable in a
node's WAL (ledger replicas, their creates and drops) and node file, the MetaStore (the ledger map and the node
registry), or the object store (LSM objects and the manifest). Derived state (catalog and slot-index checkpoints,
the journal index, and every cache) is rebuilt from those.

## 2. Clocks

Safety needs no synchronized clocks and no bound on clock drift outside two strictly bounded uses: tombstone retention
(§4) and the optional L2 ledger-replica quorum read lease (§4,
[ledger-protocol LP-I18](components/ledger-protocol.md#5-invariants)).

**Clock containment boundary.** The L2 ledger-replica quorum lease is contained strictly inside the `Ledgers` layer
(replicas, `LedgerQuorum`, `Ledger`, `LedgerWriter`, `LedgerWriterRegistry`, and `TransitionExecutor`) and serves one
purpose only: allowing a ledger's primary to answer `Ledger.ReadTailAsync` locally in 0 RTT while a quorum of replicas
guarantees no successor ledger can commit in `LedgerMap`.

- **Monotonic rate drift only.** The lease uses each host's monotonic clock (`TimeProvider.GetTimestamp`) and relies
  exclusively on a bounded monotonic clock-rate ratio `v_max / v_min ≤ F` across hosts (default `F = 1.25`, covering
  symmetric `±10%` frequency drift, with guard band `δ`), never on wall-clock or NTP synchronization.
- **No leakage to durability, consensus, or layers above `Ledgers`.** Write durability (`LP-I4`), ledger recovery and
  candidate ends (`LP-I6`–`LP-I8`), `LedgerMap` transitions (`LG-I2`), drains, the LSM manifest (`LSM-I1`), garbage
  collection (`LSM-I6`), and every layer above `Ledgers` (`Archive`, `Journals`, `Orleans` — including `JS-I1` and
  `JS-I10`) never depend on clocks or leases. A deposed primary is still ended durably by replica seals, never by a
  timeout.
- **Clean disablement.** Setting `LeaseDuration = TimeSpan.Zero` disables leases completely: replicas grant zero
  duration and report zero remaining lease, no lease heartbeats are sent, transitions perform zero lease wait, and
  `Ledger.ReadTailAsync` reduces cleanly to the clock-free quorum seal check ([LP-I9](components/ledger-protocol.md#5-invariants)).

Garbage collection deletes by reference conditions, never by age. Outside the quorum read lease and tombstone
retention, clocks decide only when a waiting period ends:

| Use | Effect of a wrong clock |
| --- | --- |
| L2 ledger-replica quorum read lease ([LP-I18](components/ledger-protocol.md#5-invariants)) | Bounded monotonic rate drift within `v_max / v_min ≤ F` preserves linearizability of 0-RTT local reads. Exceeding the rate bound breaks the §4 assumption and may let a primary serve a stale 0-RTT read after a successor commits; write durability, seals, and recovery remain unaffected. |
| Failure detection and takeover delays | A takeover happens earlier (the seal ends the old primary, and recovery decides its in-flight batches) or later (the range is unavailable longer). |
| Tombstone retention before `Expired` ([journals](components/journals.md#86-tombstone-expiry)) | An expiry happens earlier or later: the primary which judges the retention reads its own clock against the writing silo's stamp, so a journal keeps its tombstone for at least `TombstoneRetention` less the largest skew between two silos' clocks ([JS-I7](components/journals.md#5-invariants)); §4 assumes that skew far below the retention, and the retention outlasting every request which observed the journal before its deletion. |
| Deletion time of a `Delete` | The writing silo's stamp is stored in the entry, so every reader agrees whatever its own clock reads. |
| Flush, checkpoint, drain, compaction, sweep, roll, and tail-check intervals | Work happens earlier or later. |
| Deadlines and backoff | Operations time out or retry earlier or later; outcomes stay decided by storage, the ledger map, and the manifest. |

Every component reads time through an injected `TimeProvider` ([conventions](conventions.md#2-time)).

## 3. Failures tolerated

Safety holds under every failure in this table, in any combination and at any moment, including during
recovery from an earlier failure.

| Failure | Description | How it is tolerated |
| --- | --- | --- |
| Process crash | A process stops without warning, losing its memory. | Durable state is in storage; the WAL finalizes its crash region ([WAL-I4](components/wal.md#5-invariants)); the LedgerStore recovers its replicas from its WAL and checkpoints ([LS-I3](components/ledgerstore.md#5-invariants)); the takeover's recovery decides in-flight batches ([LG-I4](components/ledgers.md#5-invariants)); warm state, which lives for one primary claim, is recomputed from the history, and key indexes catch up from their durable watermarks ([JS-I6](components/journals.md#5-invariants)). |
| Operating-system crash and power loss | Unflushed writes are lost or torn at sector granularity. | Only durable records are acknowledged ([WAL-I8](components/wal.md#5-invariants)); page CRCs detect torn pages ([WAL-I2](components/wal.md#5-invariants)). |
| Local storage failure | A write, flush, rename, directory flush, or read of a node's disk or a host's volatile disk fails, or the disk fills. | The storage seam reports it to the process's fatal-error handler, which exits with the site's exit code, so the failure becomes a process crash: its next incarnation recovers from what the device holds ([conventions §14](conventions.md#14-local-storage-failures-stop-the-process)). Restarting or replacing the host is the orchestrator's. |
| Device write error | A write fails, leaving any subset of the sectors it covers on the device, each old, new, or torn. | Each write reports its own error, so the failure acknowledges nothing it held ([WAL-I8](components/wal.md#5-invariants)) and stops the process; reads return the device's bytes, and the next incarnation's open flushes before it reads ([WAL-I10](components/wal.md#5-invariants)), so recovery classifies the failed write's pages as an unfinished write's. |
| Pause | A process stops for an unbounded time and resumes with stale views. | Seal finality ends a deposed primary's later proposals ([LP-I8](components/ledger-protocol.md#5-invariants), [LR-I8](components/ledger-replicas.md#5-invariants), [LG-I5](components/ledgers.md#5-invariants)); conditional writes reject its stale map and manifest versions ([MS-I1](components/metastore.md#5-invariants), [LSM-I1](components/archive-lsm.md#5-invariants)); a reader confirms its cached map with a check begun after the read ([LG-I8](components/ledgers.md#5-invariants)). |
| Partition | Messages between processes are delayed without bound or lost. | As for a pause; a partitioned primary's proposals resolve `Fenced` once another process recovers its ledger. |
| Delayed requests | A request which failed or timed out lands later, at most once, possibly after the caller acted on its failure. | Create-once registers ([LR-I1](components/ledger-replicas.md#5-invariants)); conditional MetaStore writes and manifest replaces ([MS-I3](components/metastore.md#5-invariants)); commits name only objects whose creates returned `Created`, and names are never reused ([LSM-I7](components/archive-lsm.md#5-invariants)); a write or seal delayed past a drop creates no replica, since the node keeps the ledger's tombstone ([LR-I7](components/ledger-replicas.md#5-invariants), [LG-I7](components/ledgers.md#5-invariants)); decisions by re-reading and testing the goal. |
| Duplicated requests | A retry or a resend repeats a request which already took effect. | Identical creates are harmless; the version check rejects a duplicate entry ([JS-I1](components/journals.md#5-invariants)); a repeated map or manifest change finds its goal; drain and compaction commits are conditional ([LSM-I1](components/archive-lsm.md#5-invariants), [LSM-I5](components/archive-lsm.md#5-invariants)). |
| Racing controllers and workers | Several processes, including two activations of one controller, execute or commit one transition, drain, compaction, or sweep at once. | Every committer applies a change only under its commit rule, by conditional write, so of conflicting changes at most one applies ([LG-I2](components/ledgers.md#5-invariants), [LSM-I1](components/archive-lsm.md#5-invariants), [LSM-I5](components/archive-lsm.md#5-invariants)); a loser's outputs are garbage; the sweeper deletes only objects no commit can reference ([LSM-I6](components/archive-lsm.md#5-invariants)). |
| Node departure | A process stops with its disk intact, for a restart or a whole-cluster stop. | Its replicas are unavailable until it returns; quorums continue without it; its ledgers are taken over. |
| Node loss | A disk is lost, wiped, or replaced, or, on instance-local NVMe, its instance is released or moved. Its replicas are gone, and the node returns, if at all, as a new node. | Each ledger tolerates `Q − 1` lost replicas: every acknowledged entry is on `Q` ([LP-I4](components/ledger-protocol.md#5-invariants)); a roll onto live nodes restores redundancy; a wiped disk cannot serve the old identity ([LS-I6](components/ledgerstore.md#5-invariants), [OR-I14](compositions/orleans.md#51-invariants)). |
| Catalog loss | Every catalog checkpoint a node retains is lost or damaged, and with them the tombstones of the drop records its WAL released. | The store detects it by [LS-I5](components/ledgerstore.md#5-invariants)'s rule and refuses to open. The node is lost, counted against each of its ledgers' `Q − 1` budget, and its disk returns, if at all, as a new node, as after node loss. |
| Volatile disk loss | A host's block cache is lost or damaged. | Reads fetch the blocks from their objects again. |
| Detected corruption | Stored bytes fail their CRC-32C or contradict an invariant. | Reported as corruption, never as missing ([LR-I2](components/ledger-replicas.md#5-invariants), [LS-I5](components/ledgerstore.md#5-invariants), [LSM](components/archive-lsm.md#6-faults-concurrency-and-cancellation)); scoped to the affected replicas; reads use the remaining replicas, and a read below the known contiguous tail repairs its preferred replica's corrupt value with a batch which passes its CRC-32C ([LP-I10](components/ledger-protocol.md#5-invariants)); a corrupt journal index is discarded and rebuilt ([journals §8.3](components/journals.md#83-warm-state)); a corrupt cached block is read again from its object. |
| Object-store faults and outages | Requests fail, time out, or are throttled; failed writes may land later, at most once; the store may be unreachable. | Liveness only. Commits are conditional and decided by re-reading the manifest; abandoned objects are garbage ([LSM-I7](components/archive-lsm.md#5-invariants)). Drains, compactions, ledger drops, and sweeps wait; the live ledgers keep accepting writes until node disk budgets throttle admission. |
| MetaStore faults and outages | A register read or write fails or times out; a write's outcome is unknown; the backend may be unreachable. | Liveness only. Every user re-reads and tests its goal ([MS-I3](components/metastore.md#5-invariants)). Proposals and reads on open ledgers continue through cached maps; transitions, ledger drops, and registrations wait. |
| Lost quorum | Fewer than `Q` replicas of a ledger, fewer than `N − Q + 1`, or fewer than a MetaStore quorum, are reachable. | The ledger stalls and loses nothing: without `Q` it cannot be written, and without `N − Q + 1` it cannot be recovered, so no transition closes it until enough replicas return. A primary alive in membership but without a quorum stalls until an operator fences its node; timeouts fence nothing. A MetaStore without its quorum stalls transitions. |
| Duplicate grain activations | Orleans briefly runs two activations of one grain during a partition or directory race. | No safety property rests on Orleans; admission by expected version on the primary orders their writes ([orleans](compositions/orleans.md)). |

## 4. Assumptions

Clog makes no promise when an assumption is violated.

| Assumption | Relied on by |
| --- | --- |
| A disk preserves fsynced data, and preserves the node identity together with the WAL. A persistent volume preserves them through restarts and replacements of its instance; instance-local NVMe only through process restarts and reboots in place, on a cloud qualified for it ([verification §13](verification.md#13-qualification-boundaries)), and loses them, a node loss, whenever its instance is released or moved ([orleans §4](compositions/orleans.md#4-restrictions)). | [LedgerStore](components/ledgerstore.md#3-requirements-downward), [Orleans replica transport](compositions/orleans.md#512-replica-transport) |
| At most `Q − 1` replicas of any live ledger are lost or corrupted at once. A node's disk is released only through the retirement gate ([OR-I4](compositions/orleans.md#51-invariants)), so a retirement never counts against this budget. | [Ledger protocol](components/ledger-protocol.md#5-invariants), [Ledgers](components/ledgers.md#6-faults-concurrency-and-cancellation), [Orleans](compositions/orleans.md) |
| Every corruption of stored bytes invalidates a CRC-32C. | Every integrity check ([conventions](conventions.md#4-integrity)) |
| Bytes are not damaged in a process's memory or in transit between processes: damage there is outside the fault model. A batch's CRC-32C, which its writer computes, is checked end to end by its readers ([ledgers §2.5](components/ledgers.md#25-reads-ledgerreader)) and by both read repair and recovery before either copies the batch with a recovery write ([ledger-protocol §8.3](components/ledger-protocol.md#83-quorum-operations)), whose copy a replica keeps for the ledger's life. | [Ledger protocol](components/ledger-protocol.md#83-quorum-operations), [ledger replicas](components/ledger-replicas.md#5-invariants) |
| A tombstone's retention outlasts every request which observed its journal before the deletion: activations and in-flight entries. | [Journals](components/journals.md#86-tombstone-expiry) |
| Silo clocks are loosely synchronized: the largest skew between any two silos' clocks is far below `TombstoneRetention`, so a tombstone, stamped by the writing silo and judged by the primary's clock, keeps its retention less that skew. | [Journals](components/journals.md#5-invariants) (JS-I7) |
| When ledger-replica quorum leases are enabled (`LeaseDuration > TimeSpan.Zero`), hosts' monotonic clocks advance at rates within `[v_min, v_max]` satisfying `v_max / v_min ≤ ClockDriftFactor` (default `1.25`, covering `±10%` rate drift). No wall-clock or NTP synchronization is assumed, and setting `LeaseDuration = TimeSpan.Zero` removes this assumption completely. | [Ledger replicas](components/ledger-replicas.md#5-invariants) (LR-I10), [Ledger protocol](components/ledger-protocol.md#5-invariants) (LP-I18), [Ledgers](components/ledgers.md#5-invariants) (LG-I14) |
| A storage service behaves as §5 states. | Every provider |
| Only Clog writes below a node root, an LSM prefix, an archive-log container, and a cluster's MetaStore namespace, and only each store's sweeper deletes its objects; plain Orleans reads an archive-log container only. | [Ledgers](components/ledgers.md), [LSM](components/archive-lsm.md), [archive logs](components/archive-logs.md) (LOG-I6), [MetaStore](components/metastore.md) |
| One process owns a node root, a WAL directory, and a journal index at a time. | [WAL](components/wal.md#3-requirements-downward), [journals §8.3](components/journals.md#83-warm-state) |
| Every process which can reach a silo or gateway port, every silo of the cluster and every Orleans client (`IClusterClient`) connected through a gateway, is inside Clog's trust domain: a correct Clog component, which sends only the requests and contiguous tails the ledger replica contract permits. Any such process can call every grain, the node replica grains included, and a false contiguous tail would let reads and recovery pass a real hole. The deployment admits no other process to those ports. | [Ledger replicas](components/ledger-replicas.md#3-requirements-downward), [Orleans §4](compositions/orleans.md#4-restrictions), [Orleans replica transport](compositions/orleans.md#512-replica-transport) |

An acknowledged journal entry is fsynced on at least `Q` replicas. A ledger leaves the map, and its replicas are
dropped, only once the archive store holds every decided row of it ([HI-I2](components/archive.md#5-invariants)), so
the entry stays on `Q` intact disks until a drain places it in the archive store, whose durability it then has.

An archive object which its writer verified and the object store accepted with its checksum is durable on
acknowledgement (§5), and only Clog writes or deletes under the store's prefix, so it stays as written until the
sweeper deletes it. Corruption found in such an object later breaks one of these assumptions: the store lost
acknowledged bytes, a writer outside Clog changed it, or Clog has a defect. Clog detects it and reports it, and never
reads it as data ([LSM §8.2](components/archive-lsm.md#82-compaction)).

## 5. Storage behavior relied on

### 5.1 Object stores (LSM)

| Behavior | Azure Blob | Amazon S3 | Google Cloud Storage |
| --- | --- | --- | --- |
| Create which never overwrites | `If-None-Match: *` | `If-None-Match: *` | `ifGenerationMatch = 0` |
| Conditional replace of the manifest | `If-Match: {ETag}` | `If-Match: {ETag}` | `ifGenerationMatch = {generation}` |
| Upload rejected when its content fails the integrity checksum it carries | `Content-MD5` | `x-amz-checksum-crc32c` | `crc32c` |
| Version token returned with each read | ETag | ETag | Generation |
| Ranged GET | `x-ms-range` | `Range` | `Range` |
| Listing returns every key which existed for the whole call | yes | yes | yes |
| Read-after-write for creates, deletes, replaces, and listings which begin after a write returned | yes | yes | yes |
| Durable on acknowledgement | yes | yes | yes |

A request which fails or times out takes effect at most once, possibly much later, and a deleted name may reappear
through such a request. A listing may include names created or deleted during it. The LSM relies on a create never
overwriting an object, so a name holds one content while it exists; on ranged reads returning exactly the requested
range; and on linearizable versioned reads and conditional replaces of the manifest. It relies on these documented
semantics of each service and does not probe them ([object store §3](components/object-store.md#3-requirements-downward)).
The file-system provider meets the same contract by the flush sequence [archive LSM
§9](components/archive-lsm.md#9-providers-and-implementations) states. The memory provider's objects last only as long
as its process ([archive LSM §9](components/archive-lsm.md#9-providers-and-implementations)).

### 5.2 Conditional registers (MetaStore)

Each backend provides, per key, linearizable reads and linearizable conditional create and replace over durable
state: a write is acknowledged, and its value is visible to a read, only once the value is durable (durable
linearizability). It also provides a version token which changes on every applied write and is never reissued, and
at-most-once effect for a failed or timed-out request
([metastore](components/metastore.md#3-requirements-downward)).
Fast CASPaxos provides these with durable acceptors and fixed membership; a classic-recovery quorum preserves
a chosen value with two of five members unavailable. The memory provider's registers last only as long as its
process ([metastore §9](components/metastore.md#9-providers-and-implementations)).

### 5.3 Local storage (WAL, LedgerStore, journal index, block cache)

| Behavior | Relied on by |
| --- | --- |
| A write completes only when its bytes, and the file length needed to read them, are durable, and each write reports its own errors: a file `statx` reports a direct-I/O alignment for (Linux 6.1) is served by direct I/O, and `FILE_FLAG_NO_BUFFERING` bypasses NTFS's cache. | [WAL](components/wal.md#3-requirements-downward) |
| A crash damages only bytes a write in flight covered; a page of at least `MinimumPageSize` bytes shares no sector or block with another page. | WAL |
| Renames are atomic, and flushing a directory makes its entries durable, on the file systems each user qualifies ([WAL §3](components/wal.md#3-requirements-downward) states the WAL's); a directory flush which succeeds makes durable every entry change made before it, even after an earlier flush of the directory failed. Complete same-directory temporary files can be atomically published without replacing an existing destination (no-replace move on Windows, hard link on POSIX). | WAL named files (the LedgerStore's node file and checkpoints), cluster claims, the file-system object store and MetaStore |
| A read returns the device's bytes, and flushing an existing file makes durable every byte an earlier process's writes left on the device. | WAL recovery |
| Each node root, each file-system MetaStore root, and each file-system object store's directory exists, and it and every directory above it are durable, before its store opens or is used: the operator or the deployment creates it, and Clog creates and flushes only the root and what lies beneath it. | [LedgerStore](components/ledgerstore.md#33-root), the WAL's [caller precondition](components/wal.md#3-requirements-downward), the [file-system MetaStore](components/metastore.md#9-providers-and-implementations), the [file-system object store](components/archive-lsm.md#9-providers-and-implementations) |
| On Windows, NTFS deletes with POSIX semantics (Windows 10 version 1903 and later, Windows Server 2022): an unlink removes the name at once although another handle, opened sharing deletion, holds the file open, so a directory flush after it makes the absence durable. | The [file-system object store](components/archive-lsm.md#9-providers-and-implementations) |
| On Windows, another process's open handle can briefly refuse a rename, a deletion, or an open (antivirus and indexing services hold a new file without sharing deletion or writes), and the refusal changes nothing. | The local storage seams, which wait the contention out ([conventions §14](conventions.md#14-local-storage-failures-stop-the-process)) |
| An exclusively opened lock file excludes every other owner of a directory, every other writer of a register, or every other replace and delete of a file-system object store: on Windows through its handle's sharing mode, and on Linux and macOS through `flock(2)`, which every local file system Clog qualifies supports; a directory's other entries are deleted under its lock, then its lock file and the empty directory, still under the lock on Linux and macOS and just after its release on Windows. | WAL ([WAL-I11](components/wal.md#5-invariants)), the [file-system MetaStore](components/metastore.md#9-providers-and-implementations), the [file-system object store](components/archive-lsm.md#9-providers-and-implementations) |
| RocksDB applies a write batch atomically across its column families (`key_index`, `tombstones`, and `default` watermarks) into its memtables (`disableWAL = true`), flushes all column families atomically (`atomic_flush = true`) into durable SSTs + `MANIFEST`, verifies block checksums on read, and reports damage as a `Corruption` status. | [Journals §8.3](components/journals.md#83-warm-state) |
| Nothing: a volatile disk may lose or damage any file; every cached block carries a CRC-32C, and a miss reads the object. | [LSM block cache](components/archive-lsm.md#87-caches) |

### 5.4 Azure append blobs (archive logs)

Archive logs on Azure rely on these behaviors of Blob Storage, service version 2022-11-02 or later, each confirmed by a
fidelity test on a live account at milestones ([archive logs
§9.1](components/archive-logs.md#91-azure-append-blobs-azurearchivelogstore)):

| Behavior | Relied on by |
| --- | --- |
| `x-ms-if-tags` is evaluated atomically with `Append Block` and with `Put Blob`; a `Put Blob` which sets `x-ms-tags` never exposes new content under old tags. | The generation fence of every write (LOG-I1) |
| On an absent blob, `x-ms-if-tags` fails an `Append Block` with 404 `BlobNotFound`, and `If-Match: *` fails a `Put Blob` with 412 `ConditionNotMet`, while `x-ms-if-tags` alone lets a `Put Blob` create it. | An opener creates a generation only over an existing write-ahead blob (LOG-I2) |
| `Append Block` honours `appendpos` together with `x-ms-if-tags`, and accepts a block of 100 MiB, the `MaxAppendBytes` the store's record holds. | Append runs at their token's position (LOG-I1) |
| `If-None-Match: *` creates a blob only when it is absent. | Openers' checkpoints and a key's first write (LOG-I2) |
| `Get Blob` returns content, length, committed block count, and metadata from one version, and `Get Blob Properties` the same properties. | Reads and the classifier of a failed condition |
| `Content-MD5` rejects a block or blob whose bytes differ from its checksum. | Integrity in transit; nothing Clog-specific protects content at rest |

A request which fails or times out takes effect at most once, at its position, since its condition fails on a retry.
Hierarchical-namespace accounts, which have no blob index tags, are unsupported.

## 6. Composition requirements

Each layer establishes its guarantees under its documented prerequisites, and each composition supplies those
prerequisites, including the fault model. Orleans single activation is eventually consistent; seals, conditional writes,
and admission by expected version establish safety during duplicate activations, while Orleans turns, messaging, and
membership provide the stated ordering and progress properties ([Orleans](compositions/orleans.md)).

| # | Requirement | Stated by | Kind | Orleans |
| --- | --- | --- | --- | --- |
| R1 | Node resolution: a node identity resolves to the silo serving it, registry entries only advance, and a wiped node at the same address is refused | [Ledgers](components/ledgers.md#3-requirements-downward), [Orleans replica transport](compositions/orleans.md#512-replica-transport) | Liveness; the refusal is safety ([OR-I14](compositions/orleans.md#51-invariants)) | Node registry with monotonic silo generations |
| R2 | A failure detector which asks a live node to take over a suspected primary's ledgers | [Ledgers](components/ledgers.md#86-takeover) | Liveness: a false suspicion fences the old primary safely | Membership declares silos `Dead` |
| R3 | Controllers for placement, split and merge, rolls, drains, compactions, ledger drops, and sweeps | [Ledgers](components/ledgers.md#87-policy), [LSM](components/archive-lsm.md#88-policy) | Liveness; duplicates are safe | The controller grain, which hosts the committers, and each node's control plane |
| R4 | One Journals per process, which routes each key to its primary, the only process which proposes, and forwards encoded entries verbatim | [Journals](components/journals.md#85-forwarding), [Ledgers](components/ledgers.md#3-requirements-downward) | Safety: only the primary proposes ([LG-I5](components/ledgers.md#5-invariants)). Routing is liveness: `Moved` and the version check keep misrouting safe | Transitions executed in the primary's process; grain placement on the primary, and forwarding ([§5.5](compositions/orleans.md#55-forwarding-and-journal-outcomes)) |
| R5 | Resource governance which bounds admitted growth by node disk budgets, including while the object store is unavailable | [Journals](components/journals.md#3-requirements-downward), [LSM](components/archive-lsm.md#6-faults-concurrency-and-cancellation) | Liveness | The primary's node answers `Throttled` under node budgets ([§5.9](compositions/orleans.md#59-resource-governance)) |
| R6 | Replicas from registered nodes, at least `Q` of them live | [Ledgers](components/ledgers.md#3-requirements-downward) | Liveness | The controller grain chooses `N` distinct active nodes which membership has not declared `Dead`; while fewer than `N` may be given work, a takeover or a leaving primary's hand-off chooses every node which may, at least `Q`, then replicas of the ledger it closes which membership declared `Dead` ([OR-I5](compositions/orleans.md#51-invariants)) |

## 7. Safety and liveness

**Safety** holds under every failure of §3 while the assumptions of §4 hold: no acknowledged entry is lost; no crash
revokes an answer ([durable answers](conventions.md#13-durable-answers)); no slot holds two batches, and every replica
reaches the same decisions; every key lies in exactly one open ledger
([LG-I1](components/ledgers.md#5-invariants)), and its history has one order
([LG-I6](components/ledgers.md#5-invariants)); compaction preserves every journal's fold; garbage collection
deletes nothing a commit or a read can still need; and corruption is reported rather than read as data or as
absence. Safety is preferred to liveness: when a decision cannot be established, an operation stalls, reports
`Ambiguous`, or fails loudly; it never guesses.

**Liveness** additionally requires:

| Progress | Requires |
| --- | --- |
| Writes to a range | Its open ledger's primary alive, `Q` of the ledger's `N` replicas responsive, and the owner's node within its disk budget. |
| Reads on the owner | `Q` responsive replicas for the seal check; for rows below a ledger's watermark, the block cache or a reachable object store. |
| Takeover, handoff, roll, split, and merge | `max(Q, N − Q + 1)` responsive replicas of each ledger it closes: `N − Q + 1` to seal and recover it and `Q` for recovery writes, live nodes for each new ledger's `N` replicas, or, for a takeover or a leaving primary's hand-off, for `Q` of them ([OR-I5](compositions/orleans.md#51-invariants)), and a reachable MetaStore; a takeover also needs a failure detector (R2). |
| Drains | One responsive replica holding each decided position below the ledger's recovered tail, which the local replica usually is, and a reachable object store. |
| Ledger drops and live-ledger space | A complete ledger and a reachable MetaStore; each replica's node to see the map without the ledger, at its next view change or reconciliation, and drop the replica. A node's WAL is released below the first file of its oldest live replica once two valid catalog checkpoints cover the drops below it ([LS-I4](components/ledgerstore.md#5-invariants)), so controllers roll long-lived ledgers. |
| Compactions and sweeps | A reachable object store; archive logs' sweeps also read the ledger map. |

A ledger whose quorum is lost stalls without losing data and resumes when the quorum returns. Current replica
values require the layer's quorum and currentness proof: matching CRCs establish the integrity of those bytes,
while the read, seal, and recovery protocols establish their currentness.
