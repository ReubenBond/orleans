# Deferred work

Designs and optimizations Clog has decided not to build yet. Each item states what it is, why it waits, and the
measured condition which reopens it. None is part of the current contract: the component files specify the
system without them.

## 1. Ledgers and the ledger map

### Ledger-map size and sharding

**What.** The ledger map is one MetaStore register holding every live ledger, about 100 bytes per ledger with
`N = 3` ([metastore §8](components/metastore.md#8-mechanism-and-policy)), and every change rewrites it whole. Shard
the map into one register per dyadic range near the root, each with its own committer, which the controller grain
can hand to a grain per shard; a transition within one shard stays a single conditional write. A split or merge
across a shard boundary, and a reader's snapshot of several shards, then need a protocol of their own. Ledger IDs
are dense because one `NextLedgerId` assigns them ([LG-I3](components/ledgers.md#5-invariants)), and
`DrainedBelow` relies on that, so shards need either one shared ID counter or disjoint ID ranges with a
`DrainedBelow` per range.

**Why it waits.** With eight open ledgers per node, a 30-node cluster keeps about 240 open ledgers plus the
closed ones awaiting their drains: tens of kilobytes, within every backend's value limit, and about twice the
open ledgers after a whole-cluster restart. A change which would exceed the limit is refused and retried once
drops free room, and the group committer puts a failed node's takeovers in a few writes
([orleans §5.6](compositions/orleans.md#56-controllers)), so changes do not conflict.

**Revisit when** the map approaches a backend's value limit (Azure Table's 64 KiB holds about 600 ledgers), or the
time to read and rewrite it measurably delays transitions.

### The ledger map's limit from the MetaStore provider

**What.** Take the largest value `ledger-map` may hold from the MetaStore provider, through an `IConditionalRegister`
property each provider states, instead of the silo's `LedgerMapMaxBytes`, so a deployment on a backend with a larger
limit gets its room without configuration, and one configured above its backend's limit fails at its start.

**Why it waits.** It changes the MetaStore abstraction every provider implements. `LedgerMapMaxBytes`' default, 64
KiB, is Azure Table's limit, the smallest of the backends' ([metastore
§8](components/metastore.md#8-mechanism-and-policy)), so the default fits every provider, and a deployment on a larger
backend raises it by configuration. A limit set above the backend's costs liveness, a write the backend fails, never the
map's invariants, since every write is conditional.

**Revisit when** a provider with a different limit is added, or with [map sharding](#ledger-map-size-and-sharding),
which changes how the limit applies.

### Ledger-map deltas to silos

**What.** Carry each map change to every silo as a delta in the controller grain's `ReportAsync` replies, so a silo
applies it to its view instead of re-reading the whole register.

**Why it waits.** Each silo re-reads the map once per change, single-flight and only while its view is older than
the epoch a reply names ([orleans §5.4](compositions/orleans.md#54-primaries-and-grain-placement)): at 30 nodes and
a few map changes per second, about a hundred reads per second of tens of kilobytes each.

**Revisit when** MetaStore reads of the map are measured as a throttling or cost limit, as DynamoDB's per-item read
rate would be near 100 nodes.

### Copyset placement

**What.** Choose each ledger's replicas from a small set of copysets (Cidon et al., *Copysets*, USENIX ATC 2013),
so that a simultaneous loss of `Q` nodes loses an acknowledged entry of few ledgers, or none.

**Why it waits.** Placement spreads replicas across failure domains and keeps several ledgers per node
([ledgers §8.7](components/ledgers.md#87-policy)), which spreads a failed node's takeovers and drains; the
system model budgets at most `Q − 1` lost replicas per ledger
([system model](system-model.md#3-failures-tolerated)).

**Revisit when** a deployment sizes for correlated multi-node failures, or the probability of losing some
ledger's quorum under its failure model is measured as a limit.

### Ledger-roll churn on the ledger map

**What.** Reduce the map writes rolls cause. A roll for WAL release, a replica change, or a configuration change
changes the map once, and the closed ledger's drop changes it again. Options: hold a drop until the next
transition's group commit, or lengthen WAL retention so that rolls are rarer.

**Why it waits.** A node rolls for WAL release about once per half its retention target of WAL traffic, and only
its oldest live replica ([orleans §5.9](compositions/orleans.md#59-resource-governance)); balancing bounds its own
transitions in progress, and the group committer puts concurrent changes in one write.

**Revisit when** rolls and drops are measured as a material share of map writes, or their writes delay
takeovers.

### Ledger rolls by age

**What.** Roll an open ledger once it reaches an age, so that its closed segments' ledger drops and its replicas leave
their nodes on a schedule, not only when WAL release, a replica change, or a configuration change rolls it.

**Why it waits.** Segments, not ledgers, are the unit of drain: a long-lived ledger's closed segments drain on their
own age ([ledgers §8.2](components/ledgers.md#82-appends)), and the WAL is released per ledger through rolls for WAL
release, which already bound a ledger's life ([orleans §5.9](compositions/orleans.md#59-resource-governance)). An age
roll would add map writes for no measured need; its policy belongs with the roll policy of row 4.4 of the
[implementation plan](../implementation-plan.md).

**Revisit when** row 4.4 sets the roll policy, or a deployment measures live ledgers held open long after their rows
drained.

### Degraded WAL-release rolls

**What.** Let a WAL-release roll (OR-I13) place degraded, as a takeover does
([orleans §5.6](compositions/orleans.md#56-controllers), OR-I5): with fewer than `N` nodes which may be given work, its
successor keeps the same primary and every live replica, and keeps the ledger's dead replicas for the rest, so the
rolled ledger releases the WAL it pins while the cluster stays below `N`.

**Why it waits.** A cluster below `N` is a transient state, which lasts until a node returns or repair replaces the
dead one after `G`. Until then its nodes serve until the WAL their open ledgers pin brings disk pressure, and
admission is then throttled (OR-I10), which costs availability, not safety; the takeovers a degraded cluster runs
already roll the dead node's ranges. A degraded roll would also add a recovery and a map write per ledger to a cluster
already short of a node.

**Revisit when** a deployment measures disk pressure throttling a cluster below `N` before its dead node returns or is
repaired, for example a long outage of one node in a cluster of exactly `N` with a small WAL budget.

### Merges of non-sibling ranges and non-dyadic ranges

**What.** Merge adjacent ranges which are not siblings, or let ledgers own arbitrary key ranges, so that load
divides evenly over node counts which are not powers of two. Arbitrary ranges strictly generalize dyadic ones, so
the change invalidates no stored data ([ledgers §1](components/ledgers.md#design-rationale)).

**Why it waits.** Dyadic ranges make containment and overlap prefix comparisons and align the live ledgers with the
LSM partitions; several ledgers per node already smooth uneven halves.

**Revisit when** load imbalance from dyadic division is measured as a limit, or sibling-only merges leave cold
ranges unmerged.

### Recovering a range after a ledger loses its quorum

**What.** A procedure for a ledger which has lost more than `Q − 1` replicas, or so many that recovery cannot
seal `N − Q + 1`: close it at a tail chosen from its surviving replicas without recovery's coverage, record which
slots may be lost, and open a successor over its range, with an operator's explicit acceptance of the loss.

**Why it waits.** The system model assumes no ledger loses more than `Q − 1` replicas, and every row below a
ledger's watermark is in the LSM already; the procedure gives up LG-I4's exactness for the closed ledger.

**Revisit when** a deployment must survive the loss of more than `Q − 1` replicas of a ledger.

## 2. Replication and reads

### Durable contiguous tails

**What.** The LedgerStore would record each replica's retained contiguous tail in its slot-index checkpoints, so
a restarted node serves a decided entry from one replica at once ([LR-I6](components/ledger-replicas.md#5-invariants)).

**Why it waits.** A reopened replica starts from the window bound `max(0, h + 1 − K)`, and the next write, read,
contiguous-tail read, or seal raises it; a lost value costs reads from more replicas only until then.

**Revisit when** read or apply latency after a restart is measured as a limit.

### Piggybacked seals

**What.** Carry the seal on other replica requests, as BookKeeper's requests carry a fence flag, starting with
seal-and-read: recovery's seal request carries the caller's known contiguous tail `c₀` as a read position, and each
replica applies its seal durably and then answers, in its own log order, with its sealed flag, its retained contiguous
tail, and its value at `c₀`: DATA, `Missing`, or a value which fails its integrity check. Recovery starts at
`c = max(c₀, the answers' tails)`. When `c = c₀`, the seal answers count as the first position's reads, and
`N − Q + 1` answers of `Missing` at `c₀` end the recovery with no further round; otherwise it reads from `c`. Each
piggybacked read is ordered after its replica's seal, the guarantee [LP-I7](components/ledger-protocol.md#5-invariants)
takes from a read sent after the recovery held that replica's seal answer. It saves one round trip on every
recovery whose starting tail is current: a primary's own roll, split, or merge, and a recovery whose seal round
begins after a helping old primary's seal completed ([ledgers §8.2](components/ledgers.md#82-appends)).

**Why it waits.** It is an optimization of a recovery which is correct without it, and it gives the seal answer a
value at every layer. Adopting it needs the ledger model's seal delivery to record the replica's answer at the start
position after setting its sealed flag, and a `seal-read-before-seal` mutant, whose piggybacked read reflects the
replica before its seal, to produce its counterexample.

**Revisit when** the read round after a seal is measured as a material share of the admission pause of a roll,
split, merge, or handoff ([orleans §5.6](compositions/orleans.md#56-controllers)).

### Per-node-pair coalescing replication streams

**What.** One long-lived stream between each pair of nodes would carry the replica writes of every ledger the
pair shares, coalescing small writes, in place of one grain call per replica write.

**Why it waits.** It specializes the Orleans replica transport; per-call grain messaging has not been measured as
the bottleneck.

**Revisit when** per-call overhead between node pairs (requests per second, CPU per write, or frame overhead) is
measured as the replication bottleneck.

### Clog pages end to end in replica calls

**What.** Carry the Orleans replica transport's payloads and DATA replies without the copies at the wire boundary
([orleans §5.12](compositions/orleans.md#512-replica-transport)), in three separate Orleans changes:

1. **An ownership contract.** Orleans owns each buffer it creates and releases it exactly once, as soon as it no longer
   needs it: its proxy's copy of an argument, a request's decoded arguments, a response object, and a decoded response.
   It releases them on every path: the message is serialized and sent; it expires or is cancelled before it is sent; it
   is rejected, its forwards run out, or its activation fails; another field fails to decode; the response is dropped
   because its caller is gone, timed out, or received a duplicate; and the silo stops. A grain method or a caller which
   keeps a decoded buffer past its call takes a pin of its own (`Slice(0)`). The Orleans sites at `013ea6ea` which
   today release nothing: the proxy's copy (`ProxyGenerator.cs:316-328`), the invokable's `Dispose`, which only clears
   its fields (`InvokableGenerator.cs:120-132`), `Response<T>.Dispose` (`Response.cs:199-203`), the response's two
   copies (`InsideRuntimeClient.cs:339-343`, `:422`) and an expired one dropped (`:238-242`), a request decoded and then
   cancelled while queued (`ActivationData.cs:2589-2597`), and `Message.Dispose`, which releases only an undecoded body
   (`Message.cs:92-96`).
2. **Receiving into pages the application supplies.** Orleans decodes a message's buffers from a pool Clog supplies, or
   from a page type Clog's `ArcBuffer` can adopt, which removes the callee's and the caller's copies into Clog's pool.
3. **Sending referenced pages.** An outgoing message appends a buffer's pages by reference instead of copying them
   (`ArcBufferCodec.WriteField` writes each span), which removes the sender's copy into Orleans' pool and the
   serializer's copy of each send.

The first alone removes the large-object-heap garbage of the copies Orleans keeps today, about one byte per byte of
ingress at `N = 3` and one per byte a remote read or a forwarded journal read returns ([orleans
§5.5](compositions/orleans.md#55-forwarding-and-journal-outcomes)), and the pinned-object-heap pages small copies
strand. Those rates hold for whole 1 MiB pages; a shorter copy rounds up to a power of two, so a page between 64 KiB and
1 MiB costs between one and two bytes per byte (§5.12).

**A Clog-side alternative.** `WireBuffers.CopyToOrleans` could fill a 1 MiB copy as sixteen 64 KiB segments, each an
`ArrayPool` array on the small object heap, so the copies Orleans keeps become generation-0 and generation-1 garbage:
the same bytes and round trips, about no bytes on the large object heap, and no generation-2 collections from them. It
needs the codec and the readers to take a copy of several segments, and survivors in flight, a few MiB, to stay cheap in
the young generations; re-running `ForwardedReadsOfALargeJournal` and §5.12's fan-out with it, comparing the
large-object-heap bytes per byte, each generation's collections, the CPU per GiB, and the rates, demonstrates it.

**Why it waits.** Each is an Orleans change. Until the first, a Clog page in a message would leak from Clog's pool on
the paths above, which OR-I18 and the leak checks forbid; Orleans' own pages leak only to the GC.

**Acceptance.** On the nightly which claims the first change, Clog's transport tests pass under `CalleeFaults`,
cancellation, and stopped silos with leak checks over Clog's pool and with Orleans' outstanding pages, its rents less
its returns, at zero at quiescence; `OrleansBufferOwnershipContractTests` then fails, as it must.

**Row 5.4 evaluation.** Quiet-machine one-silo-per-process Server-GC TCP measurements
([orleans §5.5](compositions/orleans.md#55-forwarding-and-journal-outcomes),
[orleans §5.12](compositions/orleans.md#512-replica-transport),
[Row 5.4 performance baselines](../performance/row-5-4-baselines.md)) establish the budget and outcome:

- **Replica writes:** Under full 1 MiB 3-silo fan-out (109.3 MiB/s ingress, 1.01 LOH B/B, 0.86 Gen2/s), Server GC
  pause time share is **0.42%** (0.35–0.53%); at 600 KiB (1.74 LOH B/B, 1.34 Gen2/s) it is **0.42%** (0.35–0.50%); at
  1–4 KiB fan-out it is **1.18–1.37%** (1.11–1.63%); and under a 3-caller 1 MiB burst saturating one callee at
  303.6 MiB/s it is **0.12%** (0.11–0.13%) — keeping write-path Server GC pauses well under 2%. After replacing
  `LedgerStore._stripes` with exact per-replica slot tracking (#462), 1 MiB fan-out p99 write latency dropped from
  1,888.6 ms to 562.5 ms, reflecting single-NVMe write-through queue saturation across the 48 concurrent 1 MiB writes
  (`K = 1, Q = 3`) rather than GC pauses.
- **Forwarded journal reads:** A single 64 MiB forwarded reader (293.3 MiB/s, 0.95 LOH B/B) spends **1.38%**
  (1.37–1.39%) in primary Server GC pauses, whereas four concurrent 64 MiB forwarded readers saturating
  `MaxForwardedReadBytes` (627.8 MiB/s aggregate, 0.97 LOH B/B) spend **4.96%** (3.73–5.78%) of wall time in primary
  Server GC pauses. While separate forwarding lanes keep concurrent 1 KiB and 1 MiB write admission unblocked (zero
  backpressure), shared loopback TCP, thread-pool, and primary GC contention under 4× 64 MiB read saturation raises
  concurrent write p99 to 390.7 ms (1 KiB) and 407.8 ms (1 MiB).
- **Verdict:** Stays deferred while awaiting an Orleans nightly carrying the ownership contract: write-path Server GC
  pauses stay well below 2% (0.12–1.37%), whereas saturated multi-reader 64 MiB forwarded reads already reach 4.96%
  primary GC pause share. Revisit as soon as an Orleans nightly carries the ownership contract, or earlier if
  multi-disk write throughput or forwarded-read traffic makes primary LOH copies a production bottleneck. Adopting the
  contract removes Clog's own releases of the buffers Orleans decodes, in `LedgerReplicaHost`,
  `LedgerReplicaCallResults`, and `RemoteReplicaReadConverter`, in the same pin move, so no pin is released twice.

### Replica protocol version ranges for rolling upgrades

**What.** Negotiate a supported range of the replica protocol's versions through the node registry, so a cluster rolls
from one version to the next with callers and callees of both versions serving: each silo registers the versions it
speaks, and a caller sends the highest version its callee speaks
([orleans §5.12](compositions/orleans.md#512-replica-transport)).

**Why it waits.** Clog is a prototype with no deployed clusters or compatibility commitments, and a compatibility
obligation needs an explicit spec change first. Each callee checks the exact version today and refuses another with no
effect, which keeps a version change safe though not online.

**Revisit when** the first deployment is planned, before any version change must roll through a live cluster.

### Hedged creates

**What.** Duplicate a slow ledger replica create with an identical second create to cut tail latency. Identical
creates are already harmless ([LR-I1](components/ledger-replicas.md#5-invariants)).

**Revisit when** create tail latency on a node replica is measured as the append latency limit.

### Adaptive read hedges

**What.** Replace the fixed 50 ms ceiling of a read below the known contiguous tail
([ledger-protocol §8.4](components/ledger-protocol.md#84-policy)) with a delay per replica of
`clamp(p95, about 1 ms, 50 ms)` from a running estimate of that replica's reads; keep per-replica health, so a preferred
replica whose recent reads mostly hedged moves behind the others for a cool-off, then takes one probing read; and rotate
the first replica of a reader without one per scan, or per run of slots, rather than per read, which keeps a scan's
reads on one node's read-ahead and block cache. Read latency has distinct modes, local, remote, and large batches, so
one estimate would mislead, and an adaptive delay feeds back into the load it measures.

**Revisit when** the measured hedge rate (`orleans.durable.storage.ledger.read.hedges`) or the decided-read p99 is above
budget (row 5.4 of the [implementation plan](../implementation-plan.md)).

### Object-ledger appends on S3 Express and Cloud Storage

**What.** The append layout of [object-store ledgers](components/object-ledgers.md) on S3 Express One Zone, whose
`PutObject` appends at `x-amz-write-offset-bytes` (at most 10,000 appends an object), and on Cloud Storage appendable
objects, instead of one object per slot.

**Why it waits.** Both condition an append on its offset only. A recovery's seal append races a live writer's
pipelined appends and can lose every round, so a takeover's end rests on the writer stopping within a bounded time
after it sees the `sealed` marker, a liveness assumption the Azure layout, whose appends are also conditioned on a tag
the seal changes, does not need. The per-slot layout's parallel seal needs neither.

**Revisit when** either service offers a condition an append can carry which a seal changes whatever the offset (a
generation or metadata precondition evaluated atomically with the append), qualified by fidelity tests and a live run;
or measurements show per-slot requests or read amplification on those stores above budget.

### Settled readable bounds for object-store ledgers

**What.** A process other than an object-store ledger's primary learns the open ledger's settled prefix from the store
(the newest slots a listing shows, or an extent's length), above the acknowledged prefix it carries, so it reads the
open ledger without the primary.

**Why it waits.** The settled prefix can pass the writer's acknowledged prefix, so other readers would see rows before
their proposals are answered; LP-I9 and LP-I11 bound the tail by the completed prefix, and Journals' readers and
appliers have not been checked against the weaker bound. Open object-store ledgers drain on their primaries, and
linearizable reads run there already.

**Revisit when** a reader other than the primary needs an open object-store ledger's recent rows, such as a drain moved
off a loaded primary.

### Archive adoption of drained extents

**What.** A drain of an object-store ledger hands the archive the extents it drained in place, as an unsorted run which
compaction later sorts, instead of copying their rows into sections.

**Why it waits.** LSM runs are sorted sections with block indexes, Bloom filters, and a trailer, and archive logs keep a
log per key, so neither reads a ledger's frames. Sharing objects between a ledger's lifecycle and the archive's adds the
lifecycle and sweeper races the inline-body rationale rejected ([ledgers, design
rationale](components/ledgers.md#design-rationale)).

**Revisit when** drain bytes, not requests, dominate an object-store cluster's cost.

### Compacting object-ledger tombstones

**What.** Remove the markers and fences of long-dropped object-store ledgers once no deposed writer of them can run.

**Why it waits.** A partitioned writer repeats a write without bound (LP-I12), so a deleted fence can let its write land
and complete. Bounding writers needs a fence older than the ledger, such as a node incarnation the writer's appends
carry. The tombstones are a few small objects per dropped ledger: its markers, its fences' SEALs, and at most `K`
empty objects for each recovery which outlived it, whose delayed SEALs land after the sweep.

**Revisit when** the count of tombstoned ledgers measurably slows the sweeper's listings or costs storage.

### One object log per node

**What.** One object-store log per node, shared by every object-store ledger its primaries serve, which coalesces
their batches into fewer requests.

**Why it waits.** A takeover of one ledger must then fence the node's whole log and read past the other ledgers' frames,
and requests already stay near `K` per round trip per ledger; few ledgers per node keep the total small.

**Revisit when** clusters measurably need many object-store ledgers per node and their request cost dominates.

### Hedged object-store writes and extent indexes

**What.** Duplicate an object-store ledger's write which is slower than the store's measured p95, within a budget of
extra requests: a create-if-absent, or an append of the same bytes at the same offset, each settled as an unknown
outcome is. Write an extent's slot index as a final frame before its `ROLL`, so a reader finds a slot's offset in one
read.

**Why it waits.** In-order completion makes one slow request stall the ledger, but the cost of a duplicate and the
latency it saves are unmeasured, and the trailer walk caches each extent's index after its first read.

**Revisit when** row 7.6's per-provider latency distributions show the p99 or p999 of entry latency above budget, or
point reads by other processes are frequent.

## 3. LSM and warm state

### Manifest size: root and pages

**What.** Split the manifest into a small root and immutable pages, as Iceberg's manifest list names its manifest
files. The root, replaced conditionally by the one committer, holds the epoch, `DrainedBelow`, the watermarks, and
each page's dyadic range and object name; each page, created under a fresh name, lists the sections of its range. A
commit writes new pages for the ranges it changes, then replaces the root, so it remains one atomic commit under the
same rules; each process reads only the pages of the ranges it serves and caches them by name. Pages need a
reclamation rule, such as the epoch at which their root was written, and a policy for splitting a page which lists
too many sections.

**Why it waits.** The manifest holds about 120 bytes per section, about 17 MB at 10 TB, and changes about once every 14
s at 32 GB a day ([archive LSM §8.8](components/archive-lsm.md#88-policy)), so one object carries the archives the first
version serves. Independent manifests per range, each with its own committer, were rejected: a drain or compaction whose
sections span them would need an atomic commit across them.

**Revisit when** an archive is to exceed about 10 TB, or the manifest's replaces, or the silos' whole-manifest reads
after drops, are measured as costly.

### Compaction scheduling policy

**What.** The parts of the compaction policy which [LSM §8.8](components/archive-lsm.md#88-policy) leaves open: the
priority among partitions waiting to compact, the rate of compaction against drains and foreground reads, and how
promptly superseded rows and empty `Delete` rows are compacted away.

**Why it waits.** Correctness needs only the commit and drop rules (LSM-I5), which hold for any schedule, and the
geometry's defaults (`b = 4`, `k = 16`, `s = 64 MiB`) keep compaction near 1.2 MB/s at 32 GB a day. Rows are rarely
superseded, so little space waits on reclamation.

**Revisit when** read amplification, object-store request cost, or the time from expiry to physical erasure is
measured as a limit, or a deployment requires an erasure deadline.

### Continuous drain slots

**What.** Let a node's drain slot which finishes take its ledger's next closed segment, or the next due ledger, at once,
rather than at the node's next control interval ([Orleans §5.6](compositions/orleans.md#56-controllers)).

**Why it waits.** A node's drains already overlap across intervals: each interval starts the due ledgers whose drains
are not in flight, within `MaxConcurrentDrains`, so a slow drain holds only its own ledger. A slot which finishes stays
free until the next interval, at most a `ControlInterval`, a second by default, per drain; at the representative
workload, eight hourly segments of about 5 MB a node, the drains are idle nearly always, and correctness holds for any
schedule, since each drain takes one whole closed segment from the watermark (LSM-I2).

**Revisit when** drain latency or the count of drain commits and sections, after a closed ledger's backlog or a
failure-triggered drain, is measured as a limit.

### Time-sliced archive-log drains

**What.** Have an archive-log drain yield its slot after a time or key budget while other ledgers are due, and resume
at its next start from the segment it reached, rather than running its ledger's closed segments to `T` in one start
([Orleans §5.6](compositions/orleans.md#56-controllers)).

**Why it waits.** A drain holds one of the node's `MaxConcurrentDrains` slots, 8 by default, so a ledger's backlog holds
one slot and the others keep the node's other ledgers draining, their drops and WAL releases among them. At about 333
requests a second a node, a backlog of 64 MiB segments of distinct keys drains at about 3 minutes a segment ([Orleans
§8.1](compositions/orleans.md#81-resource-and-request-cost-model)); whether that delays other ledgers' drops depends on
how many backlogs a node holds at once, which nothing has measured.

**Revisit when** the drain simulation, measuring time to drop under a backlog at `MaxConcurrentDrains` 8 and 2, shows
the backlog delaying other ledgers' drops.

### Stale reads after sweeps

**What.** Keep a silo's archive store from meeting the compaction inputs a sweep deleted when its view lags past the
sweeper's grace: a longer grace of two sweeps, or a manifest view which advances when a sweep runs rather than only when
a drop or a `Stale` answer makes it. Row 3.4 added a one-sweep grace to LSM-I6, replacing none of it: a sweep deletes an
object only once an earlier sweep of the same sweeper found it deletable, and evaluates its condition again; marks live
in memory, so a restarted sweeper's first sweep deletes nothing. The grace is a cost optimization outside the safety
argument, and serves only views which advance within one sweep interval. A timed view refresh was rejected: it reads
every silo's manifest whether or not anything compacted, about as much as the `Stale` re-reads it avoids at an hourly
period, and still meets the inputs a sweep deletes just after their compaction ([archive LSM
§1](components/archive-lsm.md#design-rationale)).

**Why it waits.** With the grace, a store whose view no newer value reached in the one to two sweep intervals between a
compaction and the deletion of its inputs answers `Stale` once and re-reads the manifest: about 1 GB a day of reads at
10 TB with hourly sweeps, against about 12 GB a day without it, more when drops cluster ([archive LSM
§8.8](components/archive-lsm.md#88-policy)), at about 4 GB more compaction garbage held for a sweep interval. The
reads are in-region and cheap; what may matter is the manifest's read and parse, about 0.3 to 1 s at 17 MB, on the
first read after a sweep. Safety rests on the sweeper's condition alone (LSM-I6), which neither choice changes.

**Revisit when** compactions and sweeps run in the composition, and a measurement of read latency around sweeps, at the
99.9th percentile, shows the residual `Stale` re-reads matter.

### Durable corrupt-partition hold and repair

**What.** Hold a partition whose compaction found durable corruption in the manifest, so that controller moves and
duplicate activations attempt it no more, release the hold once a change replaces the recorded corrupt inputs, and give
operators tooling which repairs the partition: rewriting the corrupt section's rows from a replica or a backup, or
removing the section with an explicit record of what was lost.

**Why it waits.** Corruption of an archive object the writer verified and the store accepted with its checksum breaks
an assumption: the store lost acknowledged bytes, a writer outside Clog changed the prefix, or Clog has a defect
([system-model §4](system-model.md#4-assumptions)). Clog's duty is to detect it and never read it as data. The
controller holds the partition in memory for its activation, and a counter and a critical event report it ([archive LSM
§8.8](components/archive-lsm.md#88-policy)), so each activation leaves at most one attempt's output, a push's, which no
sweep reclaims while the corrupt inputs stay listed.

**Revisit when** the first production corruption is found, or before the first deployment.

### Streaming compaction merges

**What.** Bound a compaction's memory below one output partition's inputs, and its reads to each block once. A merge
which streams each input block by block holds about one block per input, which would bound a merge in place, up to about
1 GiB, as tightly as a push's child. Rows held as slices of their blocks would drop the per-row key copies, about 60% of
the payload at 256-byte rows, and a compaction page pool would keep large pages from the large-object heap. Overlapping
a push's next child's reads with its previous child's upload would shorten a push without raising its memory past two
children's. A cursor per input across a push's children, keeping the last block read and how far it was decoded, would
read each boundary block once and skip the read of a child whose rows lie in that block alone: a push reads each
boundary block twice today, about `F × block / s` more input, 6% at the defaults, but about half again at a grid step of
6 bits and about eight times at 8 bits, with `F²` ranged reads a push.

**Why it waits.** A push compacts one child at a time, so it holds about one child's inputs and output, 130 to 200 MiB
at the defaults, and each process's compaction budget, `CompactionBudgetBytes`, bounds what its compactions hold
together ([archive LSM §8.2](components/archive-lsm.md#82-compaction)). Merges in place, the only compactions which
hold more, are mostly a few drains' worth, far below their 1 GiB bound. At the default grid step of 4 bits the boundary
blocks cost 6% more input.

**Revisit when** compaction's memory or its pauses are measured as a limit on a node which also hosts live ledgers,
merges in place near their bound are measured as common, or the grid step rises above 4 bits.

### Keys past a push's size

**What.** A single key, or keys sharing a prefix deeper than the grid reaches, whose rows pass `F × s` push into one
child at every grid step, rewriting them whole each time without fanning out. A rule could stop pushing a partition
whose push lands in one child, or merge such a partition in place.

**Why it waits.** Keys spread by their hashed prefix, so a partition's keys cluster only for one journal, and the cap
of `2 × F × s` bounds each such compaction's memory.

**Revisit when** a workload holds a journal whose rows pass `F × s`, or write amplification is measured on one.

### Changing the grid step on a live cluster

**What.** Change `CompactionGridBits` on a cluster which holds sections: pushes under the new step land between the
old grid's depths, so a partition's children, the drain placement, and the `k ≥ F` check need a rule for the
transition, such as pushing each old partition once more onto the new grid.

**Why it waits.** Every geometry is correct (reads, commits, drops, and sweeps depend only on each section's partition),
so a fixed step costs only the efficiency of a step chosen before the workload was measured; the default of 4 bits sits
in a flat region of the cost model ([archive, design rationale](components/archive-lsm.md#design-rationale)).

**Revisit when** a deployment's measured read probes, write amplification, or push size calls for another step on a
cluster whose data cannot be rebuilt.

### Changing the archive store of a live cluster

**What.** Move a cluster from the LSM to archive logs, or back: copy every key's history into the new store, switch
every silo's reads and drains together, and retire the old store, while the live ledgers keep admitting.

**Why it waits.** The stores' durable states differ, so a switch is a migration with its own coverage rule: no ledger
may drop on one store's evidence while readers consult the other. The node registry records each cluster's store and
refuses a silo configured otherwise ([orleans §5.3](compositions/orleans.md#53-node-registry)), which keeps every
cluster on one store until a migration is specified ([history, design
rationale](components/archive.md#design-rationale)).

**Revisit when** a deployment needs to change its store without rebuilding its data.

### LSM cluster claim

**What.** A cluster claim on the LSM: before a new cluster's first registration creates or adopts the store's record,
one conditional create of a claim object with the cluster's identity, refusing a store another cluster claimed, so two
clusters pointed at one bucket and prefix cannot both adopt it. Archive logs had one, `durable-storage/cluster`, which
was removed as overly complicated ([archive logs, design rationale](components/archive-logs.md#design-rationale)).

**Why it waits.** Each cluster's store lies under a store prefix of its own, derived from its service ID or configured,
so clusters on one bucket keep apart without a claim, and that the prefixes differ is an operator precondition ([archive
LSM §9](components/archive-lsm.md#9-providers-and-implementations)); a claim would add a write and a refusal path to
every new cluster's start for a clash of prefixes no deployment has shown.

**Revisit when** a deployment configures two clusters with one prefix, or the misconfiguration is reported.

### Archive content above 2 GiB

**What.** Let a key's content, a store's prefix, pass `int.MaxValue` bytes, about 2 GiB: an `ArchiveContent` and the
reads which assemble it with a 64-bit length, and a prefix which streams its pages rather than holding them.

**Why it waits.** A journal's state is bounded far below that by its writer's admission and its `Replace`s
([journals §8.1](components/journals.md#81-admission)), and a prefix's content is held in memory by the read which
assembles it. A read whose content would pass the bound throws `NotSupportedException` before it allocates
([archive §3](components/archive.md#3-requirements-downward)), so the bound is explicit, never an overflow.

**Revisit when** a use case keeps a key's state above a gigabyte, or Journals bounds a journal's state otherwise.

### Reclaiming expired archive logs

**What.** Delete an archive log whose current generation an `Expired` opened and which holds no appends, once no drain
can still write an older command of its key.

**Why it waits.** Logs are never deleted, so a key's generations only rise and a straggler drain of a dropped ledger
meets a newer generation and lands nothing ([archive logs §8.5](components/archive-logs.md#85-garbage-and-sweeping)).
Deleting a log would let such a straggler, or a key's first write after the deletion, create generation 1 again, and
would make a later rebase take a version base the key held before, so a deletion needs a fence which keeps every older
drain from writing after it, and a version base which stays fresh across it
([HI-I4](components/archive.md#5-invariants)). The cost of keeping them is one small log, and one blob index tag, for
each journal written and later expired ([archive logs §8.6](components/archive-logs.md#86-policy)).

**Revisit when** a deployment measures the expired journals' logs, or their index tags, as a cost in storage or listing.

### Sweeping by recorded openers

**What.** Have each drain record the logs whose generations its openers superseded, so a sweep reads and deletes only
those, at one properties read per opener, rather than listing every checkpoint of the store.

**Why it waits.** The sweeper streams the listing in slices of at most `PagesPerSweep` pages a sweep, with memory
bounded by the names' nesting ([archive logs §8.5](components/archive-logs.md#85-garbage-and-sweeping)), so a sweep's
cost is bounded, but a whole pass grows with the store: a page per 5,000 checkpoints, about 73,000 pages after a year at
the spec's workload, about 37 sweeps ([archive logs §8.6](components/archive-logs.md#86-policy)). A record of
superseded logs is a second write per opener, and needs its own garbage rule.

**Revisit when** a whole pass of the slices keeps superseded checkpoints too long, or its list requests cost more than
the superseded checkpoints it deletes.

### Measured sweep page latency

**What.** Measure the latency of a page of the `checkpoints/` listing on a live Azure account, and set
`PagesPerSweep`, or bound a sweep's slice by time, from it ([archive logs §8.6](components/archive-logs.md#86-policy)).

**Why it waits.** A sweep lists at most `PagesPerSweep` pages, 2,000 by default, which fill about one 10-minute
`SweepInterval` at an estimated 0.3 s a page. The bound keeps each sweep's cost and memory fixed whatever the latency:
a slower page makes a sweep outlast its interval, so the next starts late, and a faster one leaves the interval idle
and a whole pass of the listing slower than it could be. Neither breaks a guarantee.

**Revisit when** a live account's page latency is measured, or a sweep is seen outlasting its interval.

### Measured drain memory charges

**What.** Set the drains' per-key and per-command memory charges (`DrainScan`'s callers, 512 and 160 bytes on archive
logs) from a benchmark of the retained allocations, so `DrainMemoryBytes` bounds real memory and not an estimate.

**Why it waits.** The charges come from the types' layouts: a key's dictionary entry, write, and builder, and a
command's entry and list growth. They bound memory only as well as the estimate holds ([archive logs, design
rationale](components/archive-logs.md#design-rationale)).

**Revisit when** a node's drain memory is measured above its budget, or the drain's types change.

### Native appends for archive logs on S3 and Cloud Storage

**What.** A layout of archive logs on S3 Express One Zone's offset-conditioned appends or Cloud Storage's appendable
objects, each log one appendable object per generation as on Azure, in place of the chunk layout's object per append run
([archive logs §7.4](components/archive-logs.md#74-chunk-layout)).

**Why it waits.** The chunk layout serves every object store with create-once writes, at a listing per write and a
request per chunk on a cold read. A native layout would save those, but it rests on service preconditions no emulator
qualifies, and no deployment has measured the chunk layout's cost.

**Revisit when** a deployment's archive-log requests or cold reads on S3 or Cloud Storage are measured above its budget.

### Storage tiers for deep partitions

**What.** Write sections at the deepest grid depths to an object store's infrequent-access tier (S3 Infrequent
Access, Azure cool), whose storage costs less and whose reads and early deletions cost more.

**Why it waits.** Deep sections are pushed or merged rarely but still read on demand, and the tiers' minimum storage
durations charge for objects a push deletes early; the saving depends on how long sections stay at their depth and
how often they are read, which no deployment has measured.

**Revisit when** a deployment's object-store storage cost dominates its LSM cost and its measured section
lifetimes at the deepest depths exceed the tier's minimum storage duration.

### Streaming or multipart object uploads

**What.** Let a writer hand `IObjectStore` an object as a stream, or in parts (S3 multipart upload, Azure staged
blocks, Cloud Storage's resumable session fed as it is written), so it never holds a whole run object.

**Why it waits.** Decided against (row 3.5): each store lands an object through one conditional write, which its SDK
retries from a seekable buffer ([archive LSM §9](components/archive-lsm.md#9-providers-and-implementations), *One
conditional write per cloud upload*). A stream cannot be replayed, so retries would move into Clog, and parts add an
upload state which outlives a failed call and which S3 bills until it is aborted. Compaction bounds a writer's memory
instead, by pushing one object per child partition, of about 64 MiB ([archive LSM
§8.2](components/archive-lsm.md#82-compaction)), without changing the contract.

**Revisit when** the object bound ([object store §2.1](components/object-store.md#21-iobjectstore)) rises, or
measured upload latency or failure rates of large objects make a whole-object write too costly.

### Resuming broken large object reads

**What.** On S3 and Cloud Storage, resume a ranged read which breaks partway from the byte it reached, under the same
ETag or generation, rather than failing it.

**Why it waits.** The SDKs retry a failed read whole. A push reads each input one child at a time, the child's blocks
and the one before them in one `ReadAsync`, about 4 MiB a read at the defaults, so a break repeats little
([archive LSM §8.2](components/archive-lsm.md#82-compaction)). A merge in place still reads each input section whole in
one request, up to `s`, 64 MiB at the defaults, and a break near its end repeats that read. Azure's SDK already resumes
a broken download at the same ETag.

**Revisit when** measured read failures of large objects, merges in place's whole-section reads among them, cost a
deployment noticeable retries.

### Azure upload checksums

**What.** Measure the CPU cost of the `Content-MD5` which each Azure upload carries against the service's
`x-ms-content-crc64`, and switch if CRC64 is cheaper.

**Why it waits.** MD5 is the checksum both the service and Azurite check on `Put Blob`, so the emulator runs verify the
request production sends. Its cost per upload has not been measured.

**Revisit when** an Azure upload's checksum shows in a profile.

### Expiring tombstones the scan cannot see

**What.** Find, and expire, journals deleted longer than `TombstoneRetention` ago whose rows have all drained and
which their primary does not hold warm, such as a key deleted under an earlier claim of its range, or one evicted from
warm state since its `Delete`.

**Why it waits.** The expiry scan reads its primary's warm state and its key indexes of the live ledgers
([journals §8.6](components/journals.md#86-tombstone-expiry)), and such a key is in neither, so its tombstone stays in
the archive until the key is next touched; the scan is a policy, and a stale tombstone costs a little storage and
nothing in correctness ([JS-I7](components/journals.md#5-invariants)). Finding such keys needs a listing of the
archive's tombstones by range, which neither store offers cheaply: a scan of the LSM's sections, or a listing of every
archive log's metadata.

**Revisit when** a deployment measures tombstones of untouched deleted journals as a cost, or an erasure deadline is
stated.

### Warm-state snapshots

**What.** Checkpoints of a primary's warm state, written where a new primary of the range can find them, so that a
restarted or new primary starts with each key's fold rather than computing it from the history at first touch.
Their format, when one is taken, and how a primary chooses and validates one remain open. A snapshot records the
position through which it folds, and a primary which starts from one must see every row of its key since that
position, which a compaction may have dropped from the LSM together with an empty `Delete`; any snapshot is checked
against the history's fold ([JS-I6](components/journals.md#5-invariants)).

**Why it waits.** Warm state is computed lazily per key at first touch, for one primary claim
([journals §8.3](components/journals.md#83-warm-state)), and the key indexes, which survive restarts, keep
absence answers and loads free of object-store requests.

**Revisit when** restart or takeover latency, or history reads at first touch, are measured as a limit.

### Durable journal versions

**What.** One name per journal state: a key's version carried durably through its rows' drain and drop, so a primary
which loads the key cold after the drop continues the version the predecessor accepted rather than rebasing to
`(prefix.VersionBase, 0)`. A writer which read or wrote before the drop then writes on at its version without a
`Rejected`.

**Why it waits.** Each state has two names after a drop, each its alone
([JS-I10](components/journals.md#5-invariants)), so versions stay unique without it, and Orleans' only use of a version
is its content-based rule, which the composition's storage bridges by reading once after a `Rejected` and resending
when the content is unchanged ([orleans §5.5](compositions/orleans.md#55-forwarding-and-journal-outcomes)). Carrying the
version costs the LSM's section and archive logs' formats with their golden vectors, `ArchiveLogs.qnt`'s rebase and
`VersionsUnique`, the rebase mutants (`rebase-with-live-rows`, `rebase-from-map-after-archive`,
`rebase-on-stale-token`, `rejects-without-reload`), and the simulations' version oracles
([journals §10](components/journals.md#10-verification)).

**Revisit when** a caller other than Orleans' content-based rule needs one name per state.

## 4. Storage

### WAL fixed buffers, zero-copy large payloads, and range reads

**What.**

- io_uring fixed-buffer registration of the aligned slabs for the direct writes and reads.
- Vectoring large payload slices into writes instead of copying them into pages.
- The WAL range reads, so a LedgerStore ranged read reads only the pages holding the range instead of the whole
  record.

**Measured boundary.** The [local io_uring WAL investigation](../performance/io-uring-wal.md) found
millisecond-scale durable flushes on a consumer NVMe. There, fixed buffers saved 3–5% of fio's CPU-bound 4 KiB IOPS but
under 2% of Clog's CPU per append, which is mostly user space (7–30% kernel). Revisit fixed buffers on a
power-loss-protected device whose flushes are cheap, after Clog's per-record user-space CPU is reduced.

**Revisit when** buffer pinning, payload copy CPU, or whole-record reads for ranged reads, each page of which is a
device read since the WAL's reads are direct, are measured as a limit of the LedgerStore.

### WAL preallocation without a second write

**What.** Avoid the zero-fill's second write in the middle regime, where durable groups average between 1 and 3 MiB
and bulk mode ([wal §8](components/wal.md#files-and-preallocation)) keeps zero-filled files. One design zeroes a
file's unwritten blocks only ahead of the log's cursor, while the device has spare bandwidth. Another recycles released
files, which needs each page to name its file, so replay rejects a recycled file's stale pages: a format change.
`FALLOC_FL_WRITE_ZEROES` (Linux 6.17) zeroes without a data transfer where the device supports it.

**Why it waits.** Bulk mode already removes the second write where it costs most: allocated files raised 64 KiB × 256
clients from 371 to 574 MiB/s on the measured NVMe. In the middle regime, zero-filled files measured equal or faster:
291 against 279 MiB/s at 4 KiB × 1,024 clients ([io_uring WAL performance](../performance/io-uring-wal.md)).

**Revisit when** a qualified device's durable write bandwidth, not its flush latency, limits a workload whose groups
fall in the middle regime.

### WAL cache of recent durable pages

**What.** The WAL's reads are direct, so they see only the device's bytes (WAL-I10), and a point read of a recently
written record, which buffered reads would often have served from the page cache, costs a device read per page it
spans. A bounded cache of pages whose direct writes completed, keyed by file and page and dropped at release, would
serve those reads from memory: such a page is the device's and never changes (WAL-I9), so the cache keeps WAL-I10. A
keyed read could also read a record's first page and the next, when that one is known durable, in one I/O, and a scan
could read its next chunk while it parses the current one.

**Row 5.4 evaluation.** On the reference Windows NTFS NVMe machine, `LedgerStoreReplicaReadBenchmarks` measured an
idle-disk 4 KiB direct-I/O WAL point read at **435.5 µs** (StdDev 7.34 µs, 4.41 KB allocated) and `LedgerStoreReopen`
at **92.54 ms** ([Row 5.4 performance baselines](../performance/row-5-4-baselines.md)), matching #131's 104–420 µs
Windows NTFS baseline and well below the ~1–2 ms quorum write path. **Verdict:** Stays deferred on idle-disk reads;
concurrent point-read latency under write saturation remains the active revisit condition.

**Revisit when** point-read latency, read IOPS, or reopen and catch-up scan time are measured as a limit of the
LedgerStore.

### Copying long-lived replicas' live records forward

**What.** Copy a long-lived replica's live records out of old WAL files, so files holding mostly dropped replicas
can be released ([ledgerstore](components/ledgerstore.md#8-mechanism-and-policy)) without rolling the ledger.
One WAL serves every replica on a node, so the oldest live replica keeps every later file.

**Why it waits.** Controllers roll a ledger whose replicas pin too much WAL, and the closed ledger drains and
drops ([ledgers §8.7](components/ledgers.md#87-policy)); a stalled drain is bounded by disk pressure
([orleans §5.9](compositions/orleans.md#59-resource-governance)).

**Revisit when** roll churn on the ledger map, or WAL held by ledgers which cannot drain, is measured as a
limit.

### Delta-encoded tombstones in catalog checkpoints

**What.** Encode a catalog checkpoint's ascending tombstones as the first ledger ID and then LEB128 gaps, in place of
8 bytes each ([ledgerstore §7.2](components/ledgerstore.md#72-catalog-checkpoint-clsc-version-1)). A node holds about
`N / S` of the cluster's ledger IDs, so the gaps are small and most take one byte, about an eighth of the plain size.

**Why it waits.** A node keeps every tombstone for its life, and each catalog checkpoint writes them all. At 8,000 drops
a day a node holds about 24 MB after a year, which the `CheckpointReplayBytes` trigger writes once per GiB of WAL, about
2% of the WAL's bytes. At Orleans' representative load a node holds about 160 KB after a year, which the
`CheckpointInterval` trigger writes at most once per interval in which a create or drop awaits a checkpoint, about 8 MB
a day ([ledgerstore, design rationale](components/ledgerstore.md#design-rationale)). The plain encoding is simpler and
costs nothing measurable at those sizes.

**Revisit when** catalog checkpoint bytes, from either trigger, are measured as a material share of a node's write
bandwidth: the replay trigger's on a node with many tombstones, or the interval trigger's on a node whose creates and
drops arrive in most intervals while it writes little WAL; or when a catalog checkpoint's size delays an open or a
capture.

### A dense in-memory tombstone set

**What.** Hold the catalog's tombstones as a sorted `ulong[]` base, which each catalog checkpoint's decode builds, and
a small immutable set of the drops since, merged into the base when a checkpoint is captured
([ledgerstore §2.2](components/ledgerstore.md#22-store)), in place of one `ImmutableSortedSet` node per
tombstone. At a year's 8,000 drops a day, about 2.9 million tombstones, the node-per-tombstone set holds about 140 MB of
heap, and the dense base about 23 MB.

**Why it waits.** The node-per-tombstone set is the simplest representation which keeps publication, capture, and
`Follows` copy-free, and nothing yet measures its cost against a node's memory or an open's time.

**Revisit when** `ReplicaCatalogBenchmarks` (decode, `IsDropped`, and `Follows` at a million tombstones) or a node's
heap shows the set as a material share of an open's time or a node's memory.

### Per-region LedgerStore corruption handling

**What.** Replace the per-replica rule, under which a damaged WAL range faults every slot it may affect, with
suspect values and CRC-matched quorum reads that repair replicas holding missing, corrupt, or suspect data,
following Protocol-Aware Recovery (Alagappan et al., FAST '18).

**Revisit when** a damaged range faults slots which a `Ledger` cannot outvote, or measured corruption rates
make faulting whole ranges costly.

## 5. Metadata

### Fast CASPaxos membership change

**What.** Changing the Fast CASPaxos voting membership, replacing a member identity after disk loss, and
restoring a member or the whole cluster from an older snapshot, each by a procedure which preserves every
acknowledged promise and allocation ([metastore](components/metastore.md#8-mechanism-and-policy)).

**Revisit when** a deployment must replace a member, change membership without stopping the MetaStore, or
restore from a backup.

## 6. Compositions

### Reliability labs for the Orleans topology

**What.** Retained-state deployments of the Orleans composition, on managed Kubernetes and as a local container
suite, with an external oracle which records each intent and outcome and reads every acknowledged entry back at
its version. Fault runs cover crashes, graceful stops, partitions, rolling upgrades, scale-out and scale-in, node
retirement, and volume loss, under one exclusive lease per environment, and a coordinated backup and restore of
node roots, the MetaStore, and the LSM prefix from one cut
([verification §13](verification.md#13-qualification-boundaries)).

**Why it waits.** It needs the Orleans composition's implementation and a deployable silo host
([build and packaging](build-packaging.md#3-executable-artifacts)). A persistent environment exercises releases
against accumulated history, which a new empty cluster per run would leave unqualified.

**Revisit when** the Orleans composition over ledgers is implemented with an executable host, or before any
deployment relies on it.

### Fencing archive-log shares by activation

**What.** Each report reply's share would carry its controller activation's epoch, which the node keeps monotonically,
taking a share only from the newest activation it has heard, so two overlapping activations of the controller grain
could not both have nodes running at their shares ([orleans §5.6](compositions/orleans.md#56-controllers)).

**Why it waits.** Each activation caps its shares against its own reservations, so while two overlap, as a duplicate
across a directory partition may for a short time, the nodes' rates may reach the budget once per activation (OR-I21).
The overlap is brief and rare, the budget is half the account's request limit, and an epoch needs a durable counter
and a wire field, for a bound the store's throttling already absorbs.

**Revisit when** shares are measured above the budget during a controller move, or the store throttles drains then.

### A bounded Journals shutdown

**What.** `JournalStore`'s disposal would bound its wait for its admissions' outstanding proposals, so a process whose
open ledgers' replicas are unreachable stops within a deadline, its undecided writes answered `Ambiguous`.

**Why it waits.** Disposal waits for the writer's decision of each outstanding proposal, which the composition's stop
order bounds: row 4.2's silo stop disposes `JournalStore` before its `LedgerWriterRegistry`, within the stop's
`TransitionTimeout` ([orleans §5.2](compositions/orleans.md#52-nodes-and-silos) step 4), and the registry's disposal
then ends what is left, which admission answers `Ambiguous` ([journals §8.1](components/journals.md#81-admission)).

**Revisit when** a composition other than Orleans stops a process with Journals, or the stop order's bound is measured
as too coarse.

### Drain progress of closed ledgers

**What.** A gauge of each closed ledger's drain progress on the LSM, such as the age of its oldest undrained segment,
reported by the node which owes its drain.

**Why it waits.** `orleans.durable.storage.ledger.age` and `orleans.durable.storage.ledger.undrained` cover the open
ledgers a node is primary of ([orleans §5.9](compositions/orleans.md#59-resource-governance)), so a closed ledger leaves
them when it closes. On archive logs, `orleans.durable.storage.archive.logs.drain.blocked` reports a closed ledger whose
drain is stuck; on the LSM nothing does but the drain's own warnings and the controller's view of the node's reported
drain progress. The drains retry each round, and a closed ledger drains at once, so a stuck one is a fault an operator
meets in the logs first.

**Revisit when** row 4.4's or row 5.x's operational work adds drain-progress alerting, or an LSM deployment needs to
alert on a closed ledger whose drain does not complete.

### A reader-side budget for gathered journals

**What.** A byte budget on the reading silo for the journals it gathers before Orleans' consumer reads them: forwarded
reads, which hold their whole journal in Orleans' pool until it is complete, and local reads, which copy it there
([orleans §5.5](compositions/orleans.md#55-forwarding-and-journal-outcomes)). A read would reserve its journal's
length once its first page names it. That needs a wait-order analysis: a read which holds a pinned snapshot while it
waits for the budget spends the snapshot's window, so it would reserve before its first page, at most
`MaxForwardedReadBytes`, or answer like `Busy` and release the snapshot.

**Why it waits.** Each owner's `MaxForwardedReadBytes` bounds what one silo gathers from it, so forwarded reads hold at
most `(S − 1) × MaxForwardedReadBytes`, about 7.25 GiB at `S = 30`, and local reads at most the journals of the
activations recovering at once, which Orleans' activation rate governs; §8.1 counts both. A budget adds a wait on the
reading silo which every recovery would meet.

**Revisit when** a recovery storm's measurement shows the pinned pool, or the large object heap, growing past §8.1's
count, or a deployment's node memory cannot hold it.

### Automatic succession of a lost instance-local disk

**What.** A silo on instance-local NVMe which starts over an empty root at an ordinal whose node is active names that
node for succession itself, as `NodeToReplace` does, rather than stopping until an operator names it
([orleans §4](compositions/orleans.md#4-restrictions)).

**Why it waits.** An empty root proves nothing about the old disk: a device left unmounted or unformatted at boot
produces the same root over intact data. A succession taken then turns a mount error the operator could repair into a
node loss, and when one event restarts several instances, several losses at once, which may pass a ledger's `Q − 1`
budget. The operator's confirmation that the disk is gone is what keeps the budget safe.

**Revisit when** the silo can positively identify the loss: a device or instance identity, recorded with the node,
which shows the old disk was released rather than left unmounted, and a check that no ledger would then name more than
`Q − 1` lost nodes.

### Retired-node tombstones

**What.** Once a retired node's gate opens, replace its registry entry with a tombstone which keeps only its identity,
so the identity is never reused and never rejoins, and returns the rest of the entry's room to the registry
([orleans §5.3](compositions/orleans.md#53-node-registry)). This is a `CLNR` format change.

**Why it waits.** Every identity keeps its whole entry for the cluster's life today, and each registration reserves
room for every entry at its longest, so the 64 KiB registry holds about 700 to 790 identities over a cluster's life
([orleans §8.1](compositions/orleans.md#81-resource-and-request-cost-model)). That covers a cluster's scale-outs, disk
replacements, and wiped disks well beyond the prototype's deployments, and the committer warns once the reserved
length passes 75% of the limit. A tombstone also needs a rule for when the gate is known open across controller
activations, which the registry does not record.

**Revisit when** a deployment's registry warning fires, or a cluster's expected identities over its life approach the
budget.

## 7. Verification

### Spec traceability check

**What.** A check which fails when the [traceability table](invariants.md) and the code disagree: an invariant
ID defined in a component file without a row, a row naming a test, model, or golden vector which does not exist,
or code citing an invariant ID which no file defines.

**Why it waits.** The table is maintained with each change, and most invariants above the ledger protocol have only
planned evidence; the evidence names settle as Ledgers, the LSM, and Journals land their tests.

**Revisit when** a review finds the table's evidence out of step with the code, or the safety core's planned
evidence has become tests.

### Environment faults on real hardware

**What.** Fault runs on real disks and kernels: full disks, I/O errors and latency spikes injected by the device
mapper (`dm-flakey`, `dm-delay`), power loss on drives which honor and which ignore flushes, and clock steps on
real hosts.

**Why it waits.** Deterministic simulation injects these faults at the WAL, object-store, and MetaStore seams
([verification §7](verification.md#7-deterministic-simulation)), and crash-state enumeration covers the file
system's persistence model; the [system model](system-model.md#4-assumptions) assumes durable fsync.

**Revisit when** a deployable host exists, or a fault observed outside simulation does not reproduce at the
simulated seams.

### Performance regression gates

**What.** Benchmarks with recorded baselines run on dedicated hardware for every change, failing when throughput,
latency percentiles, or allocations regress beyond a measured noise threshold.

**Why it waits.** Optimization claims are measured against explicit baselines with each change
([verification §12](verification.md#12-gates-and-measurements)); shared runners are too noisy for thresholds, and
the fast paths above the ledger protocol are not yet built.

**Revisit when** the fast paths and their benchmarks exist and dedicated benchmark hardware is available, or a
performance regression reaches `main` unnoticed.

### Black-box fault tests on real processes

**What.** Separate silo processes over real networks and disks, with an external client which records each
journal's history, faults injected by killing, pausing, and partitioning processes, and the linearizability
checker over the recorded histories, in the manner of Jepsen.

**Why it waits.** Deterministic simulation covers the composed protocol under the same faults reproducibly, and
end-to-end tests run the real composition in one process; a process-level harness needs an executable host
([build and packaging](build-packaging.md#3-executable-artifacts)).

**Revisit when** the Orleans composition has an executable host, together with the
[reliability labs](#reliability-labs-for-the-orleans-topology).

### Real-thread stress

**What.** Long concurrent runs on real threads and the thread pool, without a controlled scheduler, on many cores
and on ARM64's weaker memory model, checking the invariants and the linearizability of the recorded histories.

**Why it waits.** Clockwork's controlled scheduling and race exploration cover interleavings reproducibly, and the
concurrency tests of lock-free components (buffers, `AdmissionGate`, the WAL's writer) run on real threads.

**Revisit when** a concurrency defect escapes controlled scheduling, or a component's correctness depends on
memory ordering which Clockwork does not model.
