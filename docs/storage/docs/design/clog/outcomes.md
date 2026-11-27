# Outcomes and errors

Every operation outcome by layer, and the error classes shared across layers. Each component file specifies its
operations' preconditions and the exact conditions of each outcome; this file is the common vocabulary.

Clog distinguishes **outcomes**, which are values an operation returns and its caller acts on (`Durable`, `Fenced`,
`Moved`, `Missing`, and failures a caller decides on, such as a defect's `Rejected`), from **errors**, which are
exceptions a caller propagates (§8); [conventions §15](conventions.md#15-outcomes-unions-and-errors) draws the line and
gives each outcome its shape. An outcome is never inferred from a
timeout: a result is reported only once it is durably established, so no crash revokes it
([durable answers](conventions.md#13-durable-answers)), and an operation whose result cannot be established throws or
reports `Ambiguous`. §8 lists the outcomes which assert nothing about state, and the answers which are hints.

## 1. Ledger replicas

Owner: [ledger-replicas §2](components/ledger-replicas.md#2-contract-upward). Every provider, the LedgerStore among
them, and the Orleans replica transport return these, as the unions of
[conventions §15](conventions.md#15-outcomes-unions-and-errors): a write answers `ReplicaWriteOutcome`, a read
`ReplicaRead`, and a contiguous-tail read or a seal `ReplicaTailRead`. Each has a `Dropped` and a `Backpressure` case.

| Operation | Outcomes |
| --- | --- |
| `WriteAsync` | `Written`: DATA occupies the address (this write's or an identical earlier one). `Sealed`: a writer write met a sealed replica, and the payload is not stored ([LR-I8](components/ledger-replicas.md#5-invariants)); to the ledger, the write is unknown until its recovered tail decides it. `Dropped` or `Backpressure`, below. |
| `ReadAsync` (whole or byte-range) | The owned batch (an `ArcBuffer` the caller disposes), `Missing` (unwritten, or a replica the node does not hold), `Dropped`, or `Backpressure`; to a byte-range read past the batch, `OutsideBatch` with the batch's length. |
| `ReadTailAsync(contiguousTail, seal: false)` | `LedgerReplicaTail(ContiguousTail, IsSealed)`: the replica's retained contiguous tail and seal; or `Dropped` or `Backpressure`. |
| `ReadTailAsync(contiguousTail, seal: true)` | A sealed `LedgerReplicaTail` once the seal is durable, with the replica's retained contiguous tail ([LR-I8](components/ledger-replicas.md#5-invariants)); idempotent. Or `Dropped` or `Backpressure`. |
| `ILedgerReplicaTransport.Open(node, ledger, options)` | A handle over the replica, opened without I/O and without creating it; its operations answer as the node does. |
| `ILedgerStore.DropReplicaAsync(ledger)` | Completes once the node's drop is durable: the ledger is sealed and dropped on the node, its replica removed if it held one; a repeat changes nothing ([LR-I3](components/ledger-replicas.md#5-invariants)). |
| `ILedgerStore.ListReplicasAsync()` | The ledgers whose replicas the node holds, ascending. |

Every operation of a replica has two more answers:

- **`Dropped`.** The node dropped the ledger, which its tombstone records: the ledger left the map, and is sealed there.
  The ledger protocol counts the answer as sealed ([LR-I3](components/ledger-replicas.md#5-invariants),
  [LP-I5](components/ledger-protocol.md#5-invariants), [LR-I7](components/ledger-replicas.md#5-invariants),
  [LS-I2](components/ledgerstore.md#5-invariants)). Repeating never changes it.
- **`Backpressure`.** A replica's has no effect
  ([ledger-replicas §2.1](components/ledger-replicas.md#21-iledgerreplica)); a `Ledger`'s asserts nothing (§2).
  Repeating may change either.

| Failure | Meaning | Repeating changes it |
| --- | --- | --- |
| `LedgerReplicaCorruptException` | The address holds a value which fails integrity; never reported as `Missing` ([LR-I2](components/ledger-replicas.md#5-invariants)). | No |
| `LedgerStoreClusterMismatchException` | The node root belongs to another cluster ([LS-I12](components/ledgerstore.md#5-invariants)). | No (configuration) |
| I/O or timeout failures | The outcome is not established; a write may still land, at most once. | Yes |
| `LedgerReplicaUnavailableException` (Orleans replica transport) | The replica call did not reach the store's answer: `NotThisNode`, no registered `Active` silo, an Orleans overload rejection, a response timeout, a lost reply, or another callee or Orleans failure, wrapped. Never `Missing` or another storage outcome. A refusal before sending had no effect; any other call may still take effect ([LR-I9](components/ledger-replicas.md#5-invariants), [OR-I16](compositions/orleans.md#51-invariants)). | Yes |
| `LedgerReplicaQueueTimeoutException` (any transport with a per-node byte budget) | The budget for the call's node stayed short until the call's deadline: the call waited in its node's FIFO queue, never left its caller, and had no effect ([ledger-replicas §2](components/ledger-replicas.md#2-contract-upward)). A call never fails at once for lack of budget. | Yes |

The Orleans replica transport carries each storage outcome and ledger replica failure between silos with its type, and
§5.12 of the Orleans composition lists every result it reports
([orleans §5.12](compositions/orleans.md#512-replica-transport)).

## 2. Ledger protocol

Owner: [ledger-protocol §2](components/ledger-protocol.md#2-contract-upward). A `Ledger` answers a read with
`ReplicaRead`, a seal check with `LedgerTailRead`, its writer's write with `LedgerWriteOutcome`, and a recovery with
`RecoveryOutcome`, whose `Dropped` and `Backpressure` cases are §1's
([conventions §15](conventions.md#15-outcomes-unions-and-errors)). One rule decides when a `Ledger` answers them
([ledger-protocol §6.4](components/ledger-protocol.md#64-failures)). A `Ledger`'s `Backpressure` asserts nothing: a
write may have left DATA on fewer than `Q` replicas, as a failed write may, and the writer's repeat at the same slot
covers that ([LP-I12](components/ledger-protocol.md#5-invariants)).

| Operation | Outcomes |
| --- | --- |
| `LedgerWriter.AppendAsync` | An `AppendOutcome` ([ledger-protocol §2.4](components/ledger-protocol.md#24-append-outcomes)). `Appended(Slot)`: durable and linearized at `Slot`, every lower slot written. `Sealed(Slot)`: the write met a seal or a dropped replica; the slot never completes, the lifetime was stopped, and the caller decides the append from the recovered tail. `Unresolved(null)`: the slot could not be acquired, since the lifetime is stopped; nothing was written, and the batch is `Fenced`. `Unresolved(Slot)`: the batch may be on replicas at `Slot`, since its write was sent, but the write failed in a way repetition cannot change, the writer's disposal ended it, or completing the slot failed; the lifetime was stopped (`PayloadWriteFailed`, `Requested`, or `CompletionFailed`), a recovery may still copy the batch below `T`, and the batch is `Durable` if `Slot < T`, otherwise `Fenced`. |
| `Ledger.ReadTailAsync` | A `LedgerTailRead`: a `LedgerTail` holding the known contiguous tail, not a replica's retained one, and the seal check, sealed once a replica answered sealed or `Dropped` and unsealed once `Q` answered unsealed ([LP-I5](components/ledger-protocol.md#5-invariants), [LP-I9](components/ledger-protocol.md#5-invariants)); or `Backpressure`. The primary's open check: when it finds the ledger sealed, it stops its writer's lifetime with `Sealing` before it answers ([LP-I14](components/ledger-protocol.md#5-invariants)). |
| `Ledger.ReadAsync` (whole or byte-range) | A `ReplicaRead`: the batch, or `Missing` once `Q` replicas answer it; a byte-range read past the batch, `OutsideBatch`; or, when it cannot decide, `Dropped` or `Backpressure` ([LP-I5](components/ledger-protocol.md#5-invariants)). Below the known contiguous tail the slot is decided; at or above it the caller decides what a result means. |
| `Ledger.RecoverAsync` | A `RecoveryOutcome`. `Recovered(End)`: a candidate end `T` ([LP-I8](components/ledger-protocol.md#5-invariants)), which only the ledger map's conditional write makes the recorded `T`; idempotent. The known contiguous tail rises to the recovery's `c`, never from its end ([LP-I11](components/ledger-protocol.md#5-invariants)). `Dropped`: a replica answered dropped, so the ledger left the map and no close can apply ([LP-I5](components/ledger-protocol.md#5-invariants)). A recovery repeats a `Backpressure` answer rather than giving one. It first stops its writer's lifetime with `Sealing`, before its first seal request ([LP-I14](components/ledger-protocol.md#5-invariants)). |
| `Ledger.ObserveRecordedTail(T)` | Raises the known contiguous tail to the map's recorded `T`. |

| Failure | Meaning | Repeating changes it |
| --- | --- | --- |
| `AggregateException` from a `Ledger` | No decision can reach its quorum or coverage among the answers. An append never throws it: a write which fails so answers `Unresolved(Slot)`. | Only if one of its failures may change, once that replica recovers |
| `InvalidDataException` from a `Ledger` | A read below the known contiguous tail found every replica missing, or recovery found a batch at or above `c + 2K`: a caller broke the window discipline or carried a tail above the ledger's. | No |
| `LedgerStorageCorruptException` from a `Ledger` | An object-store ledger's one copy holds a batch which fails its CRC-32C, or lacks a decided batch while the ledger is not dropped ([OL-I7](components/object-ledgers.md#5-invariants)). | No |
| `ObjectDisposedException` | The `Ledger` or `LedgerWriter` was disposed; an append which the writer's disposal met before its write began wrote nothing. An append whose write the disposal ended answers `Unresolved(Slot)`. | No |

A stop is permanent and synchronous ([LP-I13](components/ledger-protocol.md#5-invariants)): afterwards the lifetime
grants and completes no slot, and its acquisitions and completions throw `SequencerStoppedException`, which the writer
answers as `Unresolved`. `SequencerStopReason` records why ([ledger-protocol
§4](components/ledger-protocol.md#4-data-types)).

**Ledger storage and object-store ledgers.** The seam and both object-store ledger layouts (per-slot and append) are
in code (rows 7.1–7.4). Below `Ledger`, every `ILedgerStorage`
answers with the unions above, so a `Ledger`'s answers are the same for both locations
([ledger-protocol §2.7](components/ledger-protocol.md#27-ledger-storage)). Below the bound a read never answers
`Missing` ([LL-I6](components/ledger-protocol.md#5-invariants)), and an object-store ledger reports a batch which fails
integrity, or one absent below the bound without its `dropped` marker, as `LedgerStorageCorruptException`. An
object-store ledger adds:

| Operation or failure | Outcomes |
| --- | --- |
| `ObjectLedgerStore.StoreRecordAsync` | The store's record, created by the first call which finds none ([object-store ledgers §2.2](components/object-ledgers.md#22-objectledgerstore)); `ObjectLedgerStoreMismatchException` when it names another layout. The composition's check throws the same exception when the record's ID differs from the one the node registry records, or the record is absent while the registry records one ([OL-I1](components/object-ledgers.md#5-invariants)). Repeating does not change it. |
| `IAppendableObjectStore.AppendAsync` | An `ObjectAppendOutcome`: `Appended(Length)`, the expected length plus the content's, once the bytes lie at the expected length, an earlier attempt's before a seal among them; `LengthMismatch(Length)`, an open object's durable length, which may be this call's own earlier attempt's doing; `Sealed`, sealed or not appendable without the bytes there; or `Absent` ([object store §2.2](components/object-store.md#22-iappendableobjectstore)). An exception leaves the outcome unknown: the append lands at most once, at its expected length, while the object is open. |
| `IAppendableObjectStore.CreateAppendableAsync` | `Created`, or `Exists` when an object of the name durably exists, of any kind, which may be this call's own earlier attempt. |
| `IAppendableObjectStore.SealAsync` | An `ObjectSealOutcome`: `Sealed` once an appendable object is durably sealed, `NotAppendable` for an object created whole or a tombstone, which the seal leaves unchanged, or `Absent`; idempotent. |
| `IAppendableObjectStore.ReplaceWithTombstoneAsync` | Completes once the tombstone is durable, whatever the object held; an exception leaves the outcome unknown, the replace landing at most once. |
| `IObjectStore.GetPropertiesAsync` | `ObjectProperties(Length, Crc32c?)`, or null when the object is durably absent. |

## 3. MetaStore

Owner: [metastore §2](components/metastore.md#2-contract-upward).

| Operation | Outcomes |
| --- | --- |
| `IConditionalRegister.ReadAsync` | `null` (absent), or a `MetaStoreEntry` holding the version and a durable value, which the caller owns and disposes; a present empty value is not `null`. `InvalidDataException` for a malformed stored entry. |
| `IConditionalRegister.WriteAsync(expectedVersion, value)` | `RegisterWriteOutcome`: `Applied(Version)`, applied and durable, at `Version`, the register's version after the write. `Mismatch`, a known version mismatch (possibly hiding an earlier applied attempt of the same write). Any exception: **unknown**; the write applies at most once, possibly later, and only at `expectedVersion`. |

After `Applied`, a caller which writes again names the returned version. Callers re-read the register after `Mismatch`
and after an exception, and test their goal ([MS-I3](components/metastore.md#5-invariants)). After `Mismatch`, after
which the write can never land, a committer
decides again and writes against the value read. After an exception, the read ends the write's batch: the committer
answers each change the value read decides, and the batch's other changes fail as while the MetaStore is unavailable
(§8; [conventions §6](conventions.md#6-retries-and-idempotency)).

## 4. Ledgers

Owner: [ledgers §2](components/ledgers.md#2-contract-upward).

### 4.1 Proposals

| Operation | Outcomes |
| --- | --- |
| `LedgerWriter.CreateBatchAsync(rowCount, maxLength)` | A `LedgerBatch` at the writer's next slot, with its segment state, once the previously created batch is proposed, the window has room, and `maxLength` fits `MaxProposalBytes` beside the batches created and not yet decided. Cancellation while it waits withdraws it: nothing is created and no slot is taken. Once the lifetime has stopped, which ends every wait, the batch takes no slot and its proposal completes `Fenced`. A created batch is proposed or the writer is abandoned. `ArgumentOutOfRangeException` for a `rowCount` below 1 or a `maxLength` above the payload limit or `MaxProposalBytes` ([ledgers §2.4](components/ledgers.md#24-primary-ledgerwriter)). |
| `LedgerWriter.ProposeAsync(batch)` | A `ProposalOutcome`: `Durable(Slot)`: the batch is the decided DATA at `Slot`, and every lower slot is decided. `Fenced`: the batch is not in the ledger and never will be; the ledger's recorded `T` is at or below its slot, or it took none. `Ambiguous`: the ledger left the map before the primary learned `T`; it asserts nothing about the batch (§8). The call takes the batch's bytes at once, after which the batch is proposed and immutable, and the append is decided whatever follows: cancellation only stops the caller waiting. `ObjectDisposedException` when the writer's disposal ends a proposal while its map view lists the ledger open or does not yet list it, which asserts nothing; a view which lists the ledger closed or no longer lists it decides the proposal. |
| `LedgerWriter.EndSegmentAsync()` | The `ProposalOutcome` of the marker batch it creates and proposes; once `Durable(Slot)`, the open segment is closed and ends at `Slot`. Cancellation before the marker is created withdraws the call; after, it stops only the caller's wait. |
| `LedgerWriter.ConfirmOpenAsync` | An `OpenCheckOutcome`: `Open(ContiguousTail)`: a seal check of `Q` replicas begun after the call found none sealed; `ContiguousTail` is the primary's contiguous tail. `Sealed`: the primary proposes nothing more. `Backpressure`: backpressure kept the check from deciding; it asserts nothing. Concurrent calls share checks. |

Batches take slots when they are created, one at a time and in creation order (LG-I12). Proposals of batches which took
slots complete in slot order; one whose batch took none, since the lifetime had stopped, completes `Fenced` at once.
After the first `Fenced` or `Ambiguous` outcome, every proposal called later completes `Fenced` or `Ambiguous`
([LG-I5](components/ledgers.md#5-invariants)). A `LedgerBatch` refuses, as its caller writes it, a row outside the
ledger's range, a row which would take the batch past its `maxLength` or the replica's payload limit, or a version whose
count is at `u64` maximum; a batch so refused is never proposed ([ledgers §2.2,
§2.4](components/ledgers.md#22-rows-batches-and-tags)).

### 4.2 Reads

| Operation | Outcomes |
| --- | --- |
| `LedgerMapView.ReadAsync` | The register's current map, read linearizably after the call began; `Current` keeps the newest by epoch, and `LedgerMapView.OpenAsync` returns after the first read. An absent register reads as epoch 0, `NextLedgerId` 1, retirement floor 0, and no ledgers. |
| `LedgerMapView.RefreshAsync(epoch)` | Completes once `Current` has at least `epoch`, reading the register only while it is older. |
| `LedgerReader.ReadAsync(ledger, slot)` | A `LedgerRead` of a decided slot: `Data`, `Dropped`, or `Backpressure`, which asserts nothing; `Data` owns the batch's verified bytes, which its caller disposes, and lends a `BatchView` over them ([ledgers §4](components/ledgers.md#4-data-types)). `Dropped`: a replica reported the ledger dropped. `ArgumentOutOfRangeException` for a slot at or above a closed ledger's `T`, or at or above an open ledger's known contiguous tail in the process, so a caller reads the tail first. |
| `LedgerReader.ReadTailAsync(ledger)` | A `LedgerTailRead`: `(ContiguousTail, IsSealed)` from a seal check of `Q` replicas begun after the call; for a closed ledger, `(T, true)` from the map without I/O. |
| `LedgerReader.ScanAsync(ledger, from, below, key)` | The decided batches with `from ≤ slot < below`, in slot order, each a `LedgerScanItem`: its `Data`, which the caller disposes, with its rows as the scan's `LedgerSegmentView`, which the scan owns and disposes, resolved them, valid until the scan's next step; the scan rewinds to the segment start of `from` and resolves each row from `from` on, only `key`'s when given. A last `Dropped` when a replica reports the ledger dropped, or a last `Backpressure`. `ArgumentOutOfRangeException` when `below` lies under `from`, at the call, or above the ledger's known contiguous tail in the process, when enumerated. `InvalidDataException` for a batch which breaks a rule of its format or its segment. |

A reader which meets `Dropped`, or a sealed answer for a ledger its map shows open, re-reads the map and starts again
([LG-I8](components/ledgers.md#5-invariants)): `LedgerReader` re-reads it before it answers.

### 4.3 Transitions

| Operation | Outcomes |
| --- | --- |
| `TransitionExecutor.ExecuteAsync(transition, commit)`, with `commit` an in-process delegate which submits the change once to the map committer, directly or by a call carrying only the change | The outcome `commit` returned, once the process's map view reached its epoch. `Applied(Epoch)`: the process serves the ledgers its view lists under its claims and has passed each recovered ledger's recorded `T` to its `Ledger`. `Superseded(Epoch)` or `Refused`: the process has stopped the sequencer lifetimes it created; the controller chooses again, and the executor never submits a refused change again, since a retry is a new transition. `Rejected(Reason)`, a defect: the process has stopped the lifetimes it created and reported the defect, and the controller issues the transition no more ([ledgers §2.6](components/ledgers.md#26-transitions)). An exception when a recovery fails, before any change is submitted, or when `commit` throws: the outcome is unknown, and the map view decides the lifetimes. |
| `LedgerMapCommitter.CommitAsync(change)` | `Applied(Epoch)`: the map at `Epoch`, a value written or read after the change was submitted, holds the change's goal. `Superseded(Epoch)`: the change's goal is absent from that value and its commit rule fails against it, as for an install chosen below the retirement floor; no attempt of it applies from then on. `Refused`: the change would make the map exceed the register's value limit, in a value no write the committer dispatched can still replace; nothing applied, and submitting it again may apply it. An exception: the MetaStore stayed unavailable, or a write whose outcome is unknown was written for the change's batch and the register's next read shows neither the change's goal nor, for a change that write did not carry, its permanently failed rule; the outcome is unknown, and submitting the same change again decides it. A change the held write carried is answered only `Applied`, or fails with the write's exception, and a failed read after the unknown outcome fails the batch with the write's exception; a corrupt read after an unknown outcome or a mismatch fails a carried change with an unknown outcome around the rejection. `Rejected(Reason)` for a defect: an install whose result would leave the open ranges not partitioning the keyspace (LG-I1), or a drop whose goal is absent and whose rule fails; nothing applied, no write carries the submission, and a later submission is decided afresh. `OverflowException` for an install past the last ledger ID, for that change alone; the rejection itself when the register holds a value which is not a ledger map and no write of the batch carried the change; `InvalidOperationException` when a value the committer would write breaks an invariant, a committer defect, for a change no dispatched write carried; a carried one fails with an unknown outcome around the defect. |
| `LedgerWriterRegistry.Unserved` | The open ledgers the map view lists with this node as primary under a claim this process does not hold; each needs a `Replace`. |

A node creates a replica unless it dropped the ledger ([LR-I7](components/ledger-replicas.md#5-invariants)): every
request for a ledger it dropped, a seal included, answers `Dropped`, which writes and seal checks count as a sealed
answer ([LP-I5](components/ledger-protocol.md#5-invariants)).

## 5. Archive

Owner: [archive §2, §3](components/archive.md#2-contract-upward), and each store's own: [archive LSM
§2](components/archive-lsm.md#2-contract-upward) and [archive logs](components/archive-logs.md#2-contract-upward).

**History reads and the store contract.**

| Operation | Outcomes |
| --- | --- |
| `ArchiveReader.ReadAsync(key)` | `ArchiveRead(Prefix, Rows, Through, Linearizable)`: the key's history, its prefix in the store (null on `Absent`) and its live rows in tag order, each with whether the prefix holds it, as of a cut which includes every row of a ledger closed before the call began and, when `Linearizable`, every row answered `Durable` before it began: true only in the process which serves the writer of the key's open ledger ([HI-I1](components/archive.md#5-invariants)). A store's exception propagates; `Stale`, a drop, and a seal restart the read, and backpressure delays it. |
| `ArchiveReader.ReadStoreAsync(key)` | `ArchiveStoreAnswer(Map, Prefix?)`: the store's prefix, or null for its `Absent`, with the map value read before the store, which holds while that value's open ledger for the key is unsealed ([archive §2.2](components/archive.md#22-history-reads)); on the LSM, `Absent` only when no committed section holds a row of the key, judged with a manifest which shows complete every ledger `Map` no longer lists. |
| `IArchiveStore.ReadAsync(key, map)` | `ArchiveStoreRead`: `Absent`; `Stale`, when the store's own snapshot proved out of date, after which the caller reads the map and the store again; or `Read(ArchivePrefix)`, which its caller owns: the key's token, version base, whether a `Replace` opened its base (`HasBase`), tombstone when the key is deleted now, and content ([archive §3](components/archive.md#3-requirements-downward)). |
| `Plan`, `Admits`, `Holds`, `IsReplaceRequested`, `AdmitsKey` | Pure values: a token from `Plan`, and a Boolean from each of the others. `Plan` throws `ArchiveCorruptException` for a token its provider could not have minted ([HI-I3](components/archive.md#5-invariants)). |
| `MessageFold::commit` | Idempotently publishes surviving folded messages for one segment before the segment's SlateDB watermark advance ([storage §3a](../storage.md#3a-atomic-journal-messages-rowkindmessage--3-and-single-pass-drainscan-folding-st12-lg-i15-lg-i16-js-i11-js-i12), `LG-I16`); performs zero durable sink writes when empty. Fails closed with `ArchiveCorruptError` when a `RowKind::Message` channel or payload fails validation. |

History reads restart internally when a cached map or manifest proves stale. A caller sees only the outcome or
`ArchiveCorruptException`: a store which contradicts the map or itself, a CRC-32C mismatch in an LSM section or trailer,
a trailer which does not list the section the manifest names, or a stored token or defining version which breaks the
key's chain ([HI-I4](components/archive.md#5-invariants)).

**The LSM.**

| Operation | Outcomes |
| --- | --- |
| `DrainWorker.DrainAsync(S, below, commit)` | `DrainOutcome`: `Drained(Drain(S, a, b, sections, complete), Outcome)`, the rows of the whole closed segment `[a, b)` uploaded in objects of about the section size, none for a segment holding only a marker, `b` the next decided segment start below `below` or `T`, and the change submitted through `commit`, whose answer it carries and records; `OpenSegment(a)`, the segment at `a` does not close below `below`; `Covered`, the Archive already holds the rows below `below`, a map entry which predates the ledger's completion among them; or the ledger scan's `Dropped`, or `Backpressure` past its retries before the segment's end. The last four upload and submit nothing. `DrainMemoryExceededException`, naming the ledger and the segment, when the segment's rows alone pass `ArchiveOptions.DrainMemoryBytes`, which uploads and submits nothing. An exception from `commit` leaves the change pending, and the next drain of the ledger submits it again without uploading; any other exception (an upload or a manifest read failed) leaves any object it uploaded as garbage. A `below` past `T` or the known tail is `ArgumentOutOfRangeException`. |
| `LedgerDropper.DropCompleteAsync(commit)` | The ledgers whose `Drop(S)` applied, each closed, shown complete by a manifest value the process read or applied, and unblocked along its lineage. A rejected, superseded, or refused drop is a defect, logged and never submitted again. An exception from `commit` leaves its drop's outcome unknown: the pass decides every other drop and refreshes the map view first, then throws, and a later call submits the drop again. |
| `CompactionWorker.CompactAsync(partition, manifest, map)` | `CompactionOutcome`: `Compacted(Compaction(inputs, outputs))`, its objects uploaded, a section of each output partition in an object of its own, which the caller submits to the manifest committer; `NothingToCompact`, nothing read or uploaded: `manifest` holds no compaction of the partition; or `Stale`: an input's object is gone, and the caller compacts from a manifest read afresh, which is `ArchiveCorruptException` if it still lists the input's section; any object uploaded for an output partition before the miss is garbage. `ArchiveCorruptException`: an input's or an outside section's object breaks its format or contradicts the manifest, an input's totals over a push's reads are not its entry's, or two inputs hold a row of one key under one tag; the objects of output partitions uploaded before it are garbage which the sweeper reclaims only once a commit removes an input, so the caller compacts the partition no more until the corruption is repaired. Any other exception: a read or an upload failed, or the wait for the compaction budget was cancelled, and any object it left is garbage. |
| `ManifestCommitter.CommitAsync(change)` | `Applied(Epoch)`: the manifest at `Epoch`, a value written or read after the change was submitted, holds the change's goal. `Superseded(Epoch)`: the change's goal is absent from that value and its commit rule fails against it; no attempt of it applies from then on. An exception: the object store stayed unavailable, or a replace whose outcome is unknown was written for the change's batch and the manifest's next read shows neither the change's goal nor, for a change that replace did not carry, its permanently failed rule, or that read failed; the outcome is unknown, and submitting the same change again decides it. A change the held replace carried is answered only `Applied`, or fails with the replace's exception. `Applied` for a compaction without outputs says only that its inputs are gone, which a competing compaction's commit also brings about. |
| `ManifestView.ReadAsync` | The current manifest and its object version, read linearizably after the call began. An absent manifest reads as epoch 0 and `DrainedBelow` 1. |
| `ManifestValue.IsComplete(S)` | `S < DrainedBelow`, or `S` is marked complete. |
| `Sweeper.SweepAsync` | `SweepResult(Referenced, Pending, Marked, Deleted, Unreadable)`, once it deleted each listed run object which the manifest read after its listing neither references nor could, and which an earlier sweep of the same sweeper found so too, marked those no earlier sweep found, and left in place, counted, those whose reference condition it could not read; idempotent. `ArchiveCorruptException` when the manifest breaks its format; `InvalidOperationException` while another sweep of the same sweeper runs. |
| `FileSystemObjectStore.ReclaimTemporaryFiles` | `TemporaryFileReclamation(Reclaimed, Refused)`: the temporary files past their age it deleted, or found gone or written again since the listing, and those whose deletion another handle refused, which a later pass deletes. A failed listing or deletion is reported by the file-system seam, which stops the process ([conventions §14](conventions.md#14-local-storage-failures-stop-the-process)). |

**Archive logs** (the archive-log store, `IArchiveLogStore`, stateless; [archive
logs](components/archive-logs.md#2-contract-upward)).

| Operation | Outcomes |
| --- | --- |
| `IArchiveLogStore.ReadAsync(name)` | `ArchiveLogRead`: `Absent`; `Stale`, when the log changed during the read or an object it names is gone, after which the caller reads again; or `Read(Position, HasBase, Tombstone?, Content)`, as of one linearizable state of the log: its position, whether a `Replace` opened its current generation, the generation's tombstone when its opener recorded one and it holds no appends, and its content, which the caller owns. |
| `IArchiveLogStore.GetPropertiesAsync(name)` | `ArchiveLogProperties?`: the log's position and its current generation's opener metadata from one linearizable state, or null when the log is absent. |
| `IArchiveLogStore.AppendAsync(name, from, run)`, `OpenAsync(name, from, opener, content)` | `ArchiveLogWriteOutcome`: `Applied`, every unit of the write landed under its condition; or `ConditionFailed`, a unit's condition failed, after the units before it landed and none after, which asserts nothing about why. Content crosses as a `ReadOnlySequence<byte>`, read only during the call. An exception leaves the outcome unknown: each unit lands at most once, and only at its position. |
| The failed-condition classifier, over one properties read | One verdict per write kind. An append run's `ArchiveLogAppendConditionOutcome`: `Held`, the run landed, by this drain or another; `Resume(Unit)`, it landed through an earlier unit and resumes at `Unit`; or `PredecessorMissing`, its predecessor is not in the log. An opener's `ArchiveLogOpenerConditionOutcome`: `Held`; `Retry`, the log reached the opener's start after its condition failed; or `PredecessorMissing`. A position no landing can explain throws `ArchiveCorruptException`. |
| An archive-log write, `ArchiveLogWriter.WriteAsync` | `ArchiveLogConditionOutcome`: `Held` or `PredecessorMissing`; corruption throws `ArchiveCorruptException`, and a refusal `ArchiveLogRefusedException`. |
| `ArchiveLogDrainWorker.DrainAsync(S, below)` | `ArchiveLogDrainOutcome`: `Drained(Below)`, every segment of `S` starting below `Below` is complete; `Complete`, `S` is closed and every segment below its `T` is complete, so the caller submits `Drop(S)`; `Waiting(Older)`, a listed lower-ID ledger overlaps `S`, and the drain wrote nothing; or `Dropped`, a map value shows `S` dropped. `ArchiveLogDrainBlockedException`, with its `ArchiveLogDrainBlockedCause`, when a key's write no retry can make succeed: `Corrupt`, a corrupt log, a predecessor missing although every older ledger dropped, or a stored token which breaks the key's chain; `Damaged`, a lost decided batch of the segment; `Refused`, a write the store refuses for a reason no retry removes; or `CorruptMarker`, a drain marker which does not parse, ends out of place, or vanished while its ledger is listed. `DrainMemoryExceededException` when a segment's footprint passes the whole `DrainMemoryBytes`. `S`'s drop waits for the key until the operator procedure restores it ([archive logs §6](components/archive-logs.md#6-faults-concurrency-and-cancellation)). |
| `ArchiveLogSweeper.SweepAsync` | `ArchiveLogSweepResult`, once every generation base below its log's observed current generation, but those of a log it skipped or found unreadable, and every progress marker of a dropped ledger, was deleted; idempotent. |

**Object store** (`IObjectStore`, from every provider; [object
store §2.1](components/object-store.md#21-iobjectstore)). Every answer reflects durable state: an object a create or
replace answered, or a read, a listing, or `Exists` observed, is on durable storage before the answer leaves, and a
delete's absence too.

| Operation | Outcomes |
| --- | --- |
| `CreateAsync(name, content)` | `Created`, durable, or `Exists` when the name exists durably; never overwrites. The store rejects content which fails the integrity checksum the upload carries, and the create fails without creating the object. An exception leaves the outcome unknown: the create lands at most once, possibly later. |
| `ReadAsync(name, offset, length)` | Exactly the requested bytes, or `null` when the object is absent. |
| `ListAsync(prefix)` | A stream, which a provider may fill page by page as a service's listing pages, of each name with its object's length, for every name which existed from the call until the stream ended, in ordinal order, and possibly names created or deleted meanwhile, which are hints. |
| `DeleteAsync(name)` | Absent afterwards; deleting an absent object succeeds. |
| `ReadVersionedAsync(name)` | `(content, version)`, or `null`; linearizable. |
| `ReplaceAsync(name, expectedVersion, content)` | `Applied(Version)` with the new version, or `Mismatch`. An exception leaves the outcome unknown: the replace applies at most once, and only at `expectedVersion`. |

## 6. Journals and Orleans

Owner: [journals §2](components/journals.md#2-contract-upward),
[orleans §5.5](compositions/orleans.md#55-forwarding-and-journal-outcomes).

| `JournalWriteOutcome` | Meaning | Effect | From the ledger |
| --- | --- | --- | --- |
| `Accepted(Version, ReplaceRequested)` | Durable, and applied by the owner; carries the journal's version after the entry, and the archive store's replace request at the key's token | In the history | `Durable`, then applied ([JS-I2](components/journals.md#5-invariants)) |
| `Rejected(CurrentVersion)` | Applies to no state the key's in-flight entries can leave on the owner; carries the owner's applied, durable `CurrentVersion` | None; never proposed | — (admission) |
| `ReplaceRequired` | An `Append` the archive store's per-generation limits refuse (`Admits`), decided against the owner's applied state; the writer's next write is a `Replace` | None; never proposed | — (admission) |
| `JournalMoved(Owner?)` | This process does not serve the key's open ledger's writer; carries the primary this process's map names, absent when that map lists no open ledger for the key, as before bootstrap, or names this process while its registry serves no writer for it | None | — (routing) |
| `Fenced` | The entry is not in the ledger and never will be | None | `Fenced`, or a primary which proposes nothing more |
| `Ambiguous` | Nothing: the entry may still be decided, at most once | Unknown | `Ambiguous`, or a lost reply |

Every write resolves to exactly one outcome ([JS-I4](components/journals.md#5-invariants)). A sender may
resend `Moved` and `Fenced` entries; a resend of an accepted entry is `Rejected` with the advanced `CurrentVersion`.
Versions are opaque and compared only for equality, so nothing infers from them whether an `Ambiguous` entry applied;
[journals §2.2](components/journals.md#22-writes) states how its writer continues. A write whose key the archive store
does not admit, whose body is above `MaxBodyBytes`, which is an empty `Append`, or whose attached `MessageRecord`s
exceed count/byte limits, name an unregistered channel, or fail `MessageHandler::validate` returns a pre-admission
argument error (`JournalArgError`, answered `ForwardWriteResponse::Refused { kind: Argument }` over target `98`, never
`Ambiguous`) without effect (`JS-I11`). A write whose key's state loads while the key's ledger cannot reach its quorum throws
`JournalStoreUnavailableException` without effect: it waited for the load, and was never proposed. The Orleans
composition also answers `Throttled`, which has no effect, before a write reaches the Journals while the key's ledger is
throttled ([orleans §5.9](compositions/orleans.md#59-resource-governance)).

| Read outcome | Meaning |
| --- | --- |
| `JournalReadResult` | From `ReadAsync(key)`: the journal's `CurrentVersion`, `Exists`, `ContentLength`, its replace request, and its content as bytes in order, in chunks unrelated to entry boundaries, reflecting every entry acknowledged before the call, and only decided entries ([JS-I3](components/journals.md#5-invariants)). |
| `JournalMoved(Owner?)` | The answering process does not serve the key's open ledger's writer, as for a write. |
| `JournalStoreCorruptException` | A record fails its CRC-32C or decoding where it enters the primary, a `Delete` row has a body other than empty or a tombstone, or the archive reports corruption (`ArchiveCorruptException`), a stored token or defining version which breaks the key's chain among it ([HI-I4](components/archive.md#5-invariants)). |
| `JournalStoreUnavailableException` | The read's history read could not reach the quorum of the key's ledger, which a repetition may reach: unavailability, which asserts nothing ([journals §6](components/journals.md#6-faults-concurrency-and-cancellation)). |

**Orleans mapping** ([orleans §5.5](compositions/orleans.md#55-forwarding-and-journal-outcomes)):

| Outcome | Orleans sees |
| --- | --- |
| `Accepted` | Completion, with the new version as the journal's ETag |
| `Rejected`, `Ambiguous`, and every failure of a send but the no-effect failures, cancellation, and corruption | `InconsistentStateException`; the grain recovers by reading its journal |
| `ReplaceRequired` | `InconsistentStateException`, with the key's replace request latched until a `Replace` of it is accepted, so the grain's next write is a `Replace` |
| `Moved`, `Fenced` | Resent to the key's current primary until another outcome settles it |
| `Throttled`, from the composition | Resent to the same primary with backoff until another outcome settles it |
| `JournalStoreUnavailableException` from a write, which the composition answers `Unavailable` | Resent to the key's current primary with backoff, after a map re-read, until another outcome settles it |
| One of the no-effect failures ([OR-I7](compositions/orleans.md#51-invariants)) | A resend |
| The caller's cancellation | `OperationCanceledException`, which asserts nothing |
| `JournalStoreCorruptException`, or corruption from a lower layer | The exception |

**Node registration** ([orleans §5.3](compositions/orleans.md#53-node-registry)), answered by
`NodeRegistryCommitter.RegisterAsync` from a value read after the submission or written by a write which landed
([OR-I19](compositions/orleans.md#51-invariants)):

| `NodeRegistrationOutcome` | Meaning | Effect and the silo's response |
| --- | --- | --- |
| `Registered(Revision)` | The registry at `Revision` holds the node's entry at the silo's address and generation: active, or retired at the revision which retired it, which a registration never changes | Applied; the silo serves, a retired node's silo serving its replicas until its retirement gate opens and executing no transition |
| `Stale` | The node's entry is at a generation at or above the silo's at another address; holds against every later value (OR-I2) | None; the silo stops |
| `WrongArchiveStore` | The registry records another archive store; holds against every later value (OR-I20) | None; the silo stops |
| `WrongObjectLedgerStore` | The registry records another object-ledger store ID; holds against every later value (OL-I1, OR-I20) | None; the silo stops |
| `Refused` | The entry would take the registry, with every entry at its longest, past `NodeRegistryCodec.MaxValueLength`, in the value read | None, and no write carried it; the silo stops |
| `OrdinalOccupied(Ordinal, Occupant, HasEntry, EntryOrdinal)` | `Occupant`'s entry is active at the registration's ordinal in the value read, and the registration does not name it for replacement; `HasEntry` and `EntryOrdinal` give the registering node's own active entry, if any, and its ordinal; holds while that entry stays active | None, and no write carried it; the silo logs a critical event naming the ordinal, the occupant, its root, and its `NodeToReplace`, and stops: for a new node the way forward is retiring the occupant by identity (`RetireNodeAsync`) or naming it in `NodeToReplace`, and for a known node, fixing its volume's binding |
| `NodeRegistryUnavailableException` | Nothing: the registration may still apply | Unknown; the silo registers again with backoff |

**Node retirement** ([orleans §5.3](compositions/orleans.md#53-node-registry)), answered by
`NodeRegistryCommitter.RetireAsync` from a value read after the submission or written by a write which landed
([OR-I19](compositions/orleans.md#51-invariants)):

| `NodeRetirementOutcome` | Meaning | Effect and the controller grain's response |
| --- | --- | --- |
| `Retired(Revision)` | The registry holds the node's entry retired at `Revision`, the revision of the write which retired it; holds against every later value (OR-I2) | Applied; the grain observes the value, and its next pass raises the ledger map's retirement floor to the newest retirement it holds; the request settles |
| `NotRegistered` | The value read holds no entry of the node | None, and no write carried it; the grain logs a warning and the request settles; `GetRetirementAsync` answers `NotRegistered` from a value it reads |
| `NodeRegistryUnavailableException` | Nothing: the retirement may still apply | Unknown; the request stays, and the next pass submits it again |
| Any other exception | A committer defect | The grain logs it as critical, and the request settles |

**Whole-cluster drain-all** ([orleans §5.14](compositions/orleans.md#514-object-store-ledgers),
[§8](compositions/orleans.md#8-operation)):

- Registry committer outcomes (`NodeRegistryCommitter.StartDrainAllAsync`, `CancelDrainAllAsync`,
  `CompleteDrainAllAsync`): `NodeDrainAllStartOutcome` (`Started(Revision)` or `ObjectLedgersNotConfigured`),
  `NodeDrainAllCancelOutcome` (`Cancelled` or `NotActive`), and `NodeDrainAllCompleteOutcome` (`Completed(Revision)`
  or `Superseded`), each answered only from a durable `CLNR` value read after submission or written by a write which
  landed (OR-I19), throwing `NodeRegistryUnavailableException` on unknown write outcome.
- Operator grain outcomes (`IClusterControllerGrain.StartDrainAllAsync()`, `CancelDrainAllAsync()`,
  `GetDrainAllAsync()`):

| `DrainAllStartOutcome` | Meaning | Effect and the operator's response |
| --- | --- | --- |
| `Started` | `CLNR.DrainAllRevision` is durably non-zero; idempotent while active | Raises `LedgerMap.RetirementFloor` to fence pre-drain `Nodes` transitions (LG-I11), rolls open `Nodes` ledgers into `ObjectStore`, lets closed `Nodes` ledgers drain and drop, then atomically commits `CompleteDrainAll` (retiring active diskful nodes and resetting `DrainAllRevision = 0`) and raises the map's floor; poll `GetDrainAllAsync()` until `IsReady` |
| `ObjectLedgersNotConfigured` | Neither the silo nor `CLNR` configures an object-ledger store | None; configure object-store ledgers before releasing all instances |
| `ObjectLedgersNotReady` | The controller has not yet adopted the object-ledger store layout (`ObjectLedgerGate` not yet passed) | None; retry `StartDrainAllAsync()` once the object-ledger gate passes |

`GetDrainAllAsync()` reads the ledger map first and `CLNR` second (linearizable map-then-registry order,
[OR-I19](compositions/orleans.md#51-invariants)) and answers `DrainAllStatus`: `Active`
(`registry.DrainAllRevision > 0`), map `Epoch` and `Floor`, `RegistryRevision`, `LatestDiskfulRetiredRevision`,
`NodeLedgers`, `ActiveDiskfulNodes`, `FloorCovers`, and `IsReady` (`RegistryRevision > 0 && !Active &&
NodeLedgers.IsEmpty && ActiveDiskfulNodes.IsEmpty && FloorCovers`).

## 7. Read results across layers

| Situation | Ledger replicas | Ledger protocol | Ledgers | Archive history | Journals |
| --- | --- | --- | --- | --- | --- |
| Holds an entry | The owned batch | The owned batch | `Data` | The prefix, then the live rows it does not hold, in tag order | Journal content |
| Not decided | `Missing` | `Missing` | `ArgumentOutOfRangeException`: a read waits for a tail which covers the slot | Read through a tail observed after the call began | The owner answers once it has applied through that tail |
| A replica's hole below the known contiguous tail | `Missing` from that replica | The owned batch ([LP-I10](components/ledger-protocol.md#5-invariants)) | `Data` | — | — |
| Sealed ledger | `IsSealed` from a contiguous-tail read | `IsSealed` from the seal check | `Sealed` from the open check or a tail read; the reader re-reads the map | Restarts with a fresh map and manifest | Reads and writes go to the successor's primary |
| Ledger replica dropped | `Dropped` | `Dropped` from a read or recovery; a sealed answer to a write or seal check | `Dropped`; the reader re-reads the map | Restarts with a fresh map and manifest | Reads restart |
| Ledger absent from the map | — | — | — | The prefix holds its rows | — |
| Archive object absent | — | — | — | `Stale`: restarts with a fresh map and manifest | — |
| Damaged | `LedgerReplicaCorruptException` | Same | Propagates; a batch failing its CRC-32C is corruption | `ArchiveCorruptException` | `JournalStoreCorruptException` for an entry's CRC-32C, a contradiction, or the archive's corruption; ledger corruption propagates |
| Quorum out of reach | I/O or timeout failures | `AggregateException` of the replicas' failures | Propagates | Propagates | `JournalStoreUnavailableException`, its inner exception the aggregate, while a repetition may succeed |
| Key not owned here | — | — | — | — | `Moved` |

Each layer classifies only what it owns: ledger replicas and the ledger protocol report storage, Ledgers decides from
the map what a seal and a dropped replica mean, the Archive decides from the map and its store's prefix which rows it
holds, and Journals decides ownership and journal semantics.

## 8. Error taxonomy

| Class | Meaning | Retry | Exceptions and outcomes (owning layer) |
| --- | --- | --- | --- |
| **Corruption** | Stored bytes fail their integrity check, or contradict an invariant. Never reported as missing or absent. | No; repair, rebuild, or roll to healthy replicas | `WalCorruption` in band (WAL); `LedgerReplicaCorruptException` (ledger replica, LedgerStore); `LedgerStorageCorruptException` (object-store ledgers); `InvalidDataException` for a malformed register entry or register value (MetaStore, Ledgers, Orleans); `StateDatabaseCorruptException` (state database: discarded and rebuilt); `ArchiveCorruptException` (Archive); `ArchiveLogDrainBlockedException` (archive logs); `JournalStoreCorruptException` (Journals); `InvalidDataException` from any wire decoder |
| **Unavailability** | A dependency is unreachable, slow, or overloaded, or an outcome is unknown. A write may still land, at most once. | Yes; the caller re-reads and acts conditionally, since an unknown write lands at most once | I/O and timeout failures (ledger replica, ledger protocol); `Backpressure` from a `Ledger`, while a replica's has no effect (§1); `LedgerReplicaUnavailableException` (Orleans replica transport); `AggregateException` from a `Ledger`, and `Unresolved` appends (the ledger protocol, resolved by the ledger's primary); `JournalStoreUnavailableException`, a `Ledger`'s aggregate as the Journals report it, which has no effect; any exception from `WriteAsync` or `ReadAsync` (MetaStore); any exception from an object store or archive-log store operation but `ObjectContentChangedException` (Archive); `LedgerReplicaQueueTimeoutException` and a forwarded write's queue deadline, which have no effect; `Refused` map changes, while the map is full until drops free room (Ledgers); `ObjectDisposedException` during shutdown |
| **Precondition and conflict** | The operation is invalid for the current durable state, or lost a race. | No, not unchanged; re-plan from fresh state | `Mismatch` from a conditional register write; `Mismatch` from a manifest replace; `ConditionFailed` from an archive-log store write, which one properties read decides; `ReplaceRequired` (Journals: the writer re-plans with a `Replace`); `Superseded` map and manifest changes; a `Stale` node registration (a newer generation or a retired entry; the silo stops); `Dropped` from a ledger read (Ledgers: re-read the map); `Dropped` from a replica or a `Ledger`; `InvalidOperationException` |
| **Not found** | A named directory or file does not exist. | No | `WalNotFoundException` (WAL) |
| **Usage** | An argument violates a precondition. | No | `ArgumentException` and `ArgumentOutOfRangeException` (every layer); the outcome `Rejected(Reason)` (Ledgers: a map change which would break the map's invariants, a defect of its submitter) |
| **Configuration** | A provider option or storage root is invalid for this deployment. | No | `LedgerStoreClusterMismatchException` and the cluster checks of every storage root ([identity §1](identity.md#1-cluster)); `WalExistsException`, `WalUnsupportedFormatException` (WAL) |
| **Cancellation** | The caller stopped waiting. | — | `OperationCanceledException` carrying the caller's token |

Wrong-format errors on open (`WalUnsupportedFormatException`) are configuration: Clog keeps one current format
([conventions](conventions.md#10-one-current-representation)). Each class lists the outcomes a caller decides on, which
are values, beside the exceptions a caller propagates ([conventions §15](conventions.md#15-outcomes-unions-and-errors)).

**Local storage failures are no class.** A failed write, flush, rename, or read of local storage stops the process
through its fatal-error handler before any caller sees it
([conventions §14](conventions.md#14-local-storage-failures-stop-the-process)); a remote caller of the stopped node
sees unavailability. Answers the storage gives, a missing file, an existing name, and a lock another owner holds,
keep their classes: not found, precondition and conflict, and configuration.

**Changed content is no class either.** `ObjectContentChangedException` reports that an object write's content no
longer matched the CRC-32C its writer computed when it verified it, so the store wrote nothing: the process's memory
changed after verification. Its writer reports it as a defect, which stops the process ([archive LSM
§9](components/archive-lsm.md#9-providers-and-implementations), [object-store ledgers
§6](components/object-ledgers.md#6-faults-concurrency-and-cancellation)), so no caller retries it or reads it as an
unknown outcome: a ledger's writer and recovery repeat no failure the fatal-error handler reported.

**Outcomes which assert nothing.** Every unavailability, including an unknown outcome (`Ambiguous` from Ledgers and
Journals, an append `Unresolved` until recovery decides it, any exception from a conditional register write or an object
store create or replace but `ObjectContentChangedException`, and any exception from a group committer), `Backpressure`
from a `Ledger` (§2; a replica's has no effect, §1), `Throttled`, and cancellation asserts nothing about state, so a
component may give one at any time ([durable answers](conventions.md#13-durable-answers)). A caller acts on one only by
waiting, retrying, or reading the authoritative state again.

**Version mismatches.** `Mismatch` from a conditional register write and from an object store replace assert
that the store held another version, so they are durable answers: a provider makes the version it compares against
durable before it compares.

**Hints.** These answers assert nothing durable, and no safety decision counts them: the owner a `Moved` answer names;
the node registry's cached resolution and every grain placement
([orleans §5.3, §5.4](compositions/orleans.md#53-node-registry)); an epoch a reply carries, which names a value to
refresh to; Journals's expiry candidates and replace requests
([journals §8.6, §8.7](components/journals.md#86-tombstone-expiry)). Their users confirm through the
authoritative path (a seal check, a register or manifest read, or admission) whenever a hint proves stale.
`LedgerMapView.Current` is durable but may be stale: it holds only values read from the register, so it is a hint about
currency alone, and a decision which no later map can invalidate may count it
([ledgers §2.3](components/ledgers.md#23-ledger-map)).

## 9. Cancellation

**Cancellation stops the caller waiting, never the write** ([conventions](conventions.md#3-cancellation)).

| Layer | A cancelled caller |
| --- | --- |
| WAL | Cancels only before its record is accepted; afterwards the record reaches `OnDurable` or `OnFailed`. |
| Ledger replicas | Stops waiting; a request already sent may land. A cancelled create is reported as `OperationCanceledException`, never reconciled. Compositions leave pending child operations running. |
| LedgerStore | Before the WAL accepts the record: nothing is written. After: the mutation completes durably. A create or drop which holds the catalog gate runs to its record's durable outcome. |
| Orleans replica transport | Before the request leaves the caller's silo: nothing is run. After: the callee's store completes any mutation it accepted. |
| Ledger protocol | Before acquiring a slot: nothing is written. After: the append writes, completes, and decides its slot without the caller. A recovery stops waiting; the seals and recovery writes it sent may still land. |
| MetaStore | Before dispatch: no I/O. After: the write's outcome is unknown and may still land. |
| Ledgers | A proposal continues and is decided as usual; a cancelled open check leaves a shared check running. A cancelled transition stops the caller waiting; the executor still decides its change. A transition interrupted before it submits its change leaves its sealed ledgers open in the map, which any later transition recovers and closes. A cancelled commit stops the caller waiting; the change may still apply, and submitting it again decides it. |
| LSM | A worker cancelled before its commit leaves garbage for the sweeper; after it, the commit may land. A cancelled read stops waiting. |
| State database | `BeginAsync`, `GetAsync`, `ScanAsync`, `FlushAsync`, `ResetAsync` stop waiting; a requested flush or reset still runs. |
| Journals | The proposal runs to completion and is decided as usual; reads and history loads stop at their awaits. |
