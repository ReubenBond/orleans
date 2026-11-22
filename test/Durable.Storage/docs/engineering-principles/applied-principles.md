# Applying the principles

Worked examples for the [design](design.md),
[correctness](correctness.md), [testing](testing.md),
[performance](performance.md), and [code-craft](code-craft.md)
lenses. Each example identifies a contract, the boundary that owns it, and evidence that
it holds. Examples are synthesized guidance; the linked sources provide their original
context and assumptions.

## Hide decisions behind contracts

**Rule:** Choose module boundaries around decisions likely to change. Specify the
observable behavior independently of today's representation.

Parnas's KWIC example contrasts a decomposition around processing steps with one that
hides decisions about storage and ordering. The useful question is which callers must
change when a representation changes, rather than how many small functions exist.

For a service with movable entities:

| Decision | Owning component | Caller-visible contract |
| --- | --- | --- |
| Which machine hosts an entity | Placement and lifecycle | Address an entity by stable logical ID. |
| How state is persisted | State provider | Define committed versions and recovery outcomes. |
| How work is scheduled | Entity scheduler | Define serialization and permitted interleaving. |

Virtualization preserves logical identity while infrastructure changes its physical
realization. Its contract also states activation, concurrency, and persistence
guarantees; callers rely on those specific guarantees. Orleans supplies a concrete
example of this separation.

**Check:** Describe an alternative representation or placement policy. Identify the
module that changes and show which caller-visible observations remain the same.

Sources: [Parnas][parnas], [Orleans][orleans], [Lampson][lampson].

## Place success at the right boundary

**Rule:** Define success where the complete promised outcome can be established.

Suppose `StoreDocument` promises that validated content survives the declared storage
failure model. Its completion points mean different things:

| Observation | What it establishes |
| --- | --- |
| A transport accepts bytes | The transport accepted the request under its contract. |
| A storage transaction commits | The storage contract's durable state transition completed. |
| The document service returns a receipt | The promised content and metadata are committed together. |

The receipt's meaning comes from the service contract. If a lower layer already provides
the required integrity or durability guarantee, the service can rely on it explicitly.
Additional checks belong where they establish a missing application property.

This also guides the choice of abstraction. LogDrive separates durable storage from
sequencing; AtomicLog supplies the stronger shared-log contract through a particular
composition and recovery discipline. Assign each guarantee to the layer that establishes
it and state the preconditions needed by the next layer.

**Check:** For each success return, identify the completed state transition, the lower-layer
contract it relies on, and the failure histories under which the promise still holds.

Sources: [Saltzer, Reed, and Clark][endtoend], [LogDrive][logdrive].

## Make answers durable

**Rule:** Before an answer leaves a component, make durable every state the answer
asserts. A crash never revokes an answer.

Every answer asserts something about state, and its recipients act on the assertion: a
writer counts acknowledgments toward a quorum, a recovery counts refusals toward the
replicas it has sealed, a reader hands a value to an application, and a caller stops or
retries. When a crash can roll back the asserted state, the recipient has acted on a state
the system never durably reached.

A replicated log's replica drop shows the failure. The drop closes the replica's admission,
appends a drop record, and publishes the replica's tombstone once the record is durable.
Operations that arrived during the drop answered `Dropped` at once, and a recovery counts a
dropped replica as sealed toward the replicas it must seal. A crash before the drop record
was durable brought the replica back unsealed. The recovery's count then included a replica
that still accepted writes, and a deposed writer could reach a write quorum past the end the
recovery decided. The fix: an operation that would answer `Dropped` waits for the drop
record's durable outcome.

| Answer | What it asserts | Durable before it leaves |
| --- | --- | --- |
| Write acknowledged | The value survives | The write's record |
| Vote or promise | The voter refuses lower ballots | The promise (Raft's `currentTerm` and `votedFor`) |
| Sealed, fenced, or dropped refusal | The replica accepts no later write from the old owner | The seal or drop record |
| Read value | The value belongs to the decided history | The value's decision |
| Absence | The recovered history holds no such item | Every write the index or catalog covers |
| Commit verdict or new version | The conditional write applied | The store's acknowledgment of the write |
| Published frontier, tail, or catalog | Everything it covers holds | Everything it covers |

An answer that reflects a change, such as the `Dropped` refusal above, waits for the
change's durable outcome, or is an outcome that asserts nothing about state: unavailable,
ambiguous, throttled, or cancelled. Concurrent operations need no serialization: an operation
with no happens-before relation to an in-flight change answers from the durable state before
it, as a read concurrent with a create answers that the item is absent. A derived answer (a
quorum, a fold, a cache, a forwarded reply) is durable only when every answer it counts is. A hint, such as a cache, routing, or placement
entry, asserts nothing durable, and no safety decision counts it.

**Check:** For each answer, name what it asserts and the durable record that establishes it.
Enumerate crash points between the start of each change and its durable completion, and check
that every answer given before the crash point holds in the recovered state. In a model, give
each durable answer a mutant that answers before its record is durable; the mutant must
produce a counterexample.

Sources: [Raft, Figure 2][raft], [Elnozahy et al., the output commit problem][rollback],
[Izraelevitz, Mendes, and Scott, durable linearizability][durablelin].

## Stop the process on a local storage failure

**Rule:** When a local storage operation fails, report it to the process's fatal-error handler,
which writes a message naming the operation, the path, and the operating-system error, and exits
with a distinct exit code. Leave restart or replacement to the orchestrator.
Recover in place only when the recovery is trivially simple and safe.

PostgreSQL retried a failed `fsync`. Linux had reported the error once and marked the failed
pages clean, so the retry succeeded, a checkpoint completed, and the WAL it relied on was
removed: acknowledged data was lost without any error. PostgreSQL now panics on an `fsync`
failure and recovers from its WAL at restart. Rebello et al. found that none of the
failure-handling strategies of five widely used applications was sufficient, and that the
file systems differ in what they leave in the page cache after a failure.

After a failed write or flush, the process's view of the file (its cached pages, its length,
which writes landed) is unknown, so any answer it gives afterward may assert a state the
device does not hold. A fresh process recovers from what the device holds, through the same
recovery path a crash takes, which the crash tests already exercise. Crash-only software
makes that the only recovery path. The exception is a recovery which answers nothing from the
failed state and leaves only a state the crash path already handles: abandoning a file which was
never published is such a recovery; retrying the failed operation never is.

| Event | Response |
| --- | --- |
| A local write, flush, `fsync`, rename, or directory flush fails | Stop the process |
| A local read fails with an I/O error | Stop the process |
| The disk is full | Stop the process; budgets and throttling exist to keep it from happening |
| A write of a file not yet published fails (a checkpoint's temporary file) | Delete the temporary file and keep the published one; the crash path removes it too |
| An answer: the file is missing, or the name exists | Handle it as the answer it is |
| Bytes read successfully fail their checksum | Corruption: report it, then repair or rebuild |
| A remote store or service fails | Handle it per its contract: retry, or an unknown outcome |

**Check:** Every catch around a local file operation converts an answer (not found, exists),
performs a recovery which is trivially simple and safe, or reports the failure; none retries,
swallows, or moves the component into a terminal state which keeps the process alive.
Components report the failure to the process's fatal-error handler (a static handler, or an
injected one such as Orleans' `IFatalErrorHandler`) and never end the process themselves: the
handler writes the message and exits. Tests and deterministic simulation observe the report (for
example through an interceptor delegate), suppress the exit, and model a crash and restart of the
failed node, so recovery from the device's state is tested.

Sources: [PostgreSQL, "Fsync Errors"][fsyncgate], [Rebello et al.][fsyncfailures],
[Candea and Fox, crash-only software][crashonly].

## Fix a broken guarantee at its owner

**Rule:** When a layer is found not to provide a guarantee its contract states, fix that
layer and add a contract test. Dependents keep relying on the stated contract.

Suppose a journal storage interface states that an append is atomic and succeeds only if
the journal's current version equals the caller's expected version. A journaling layer
relies on that precondition to detect a concurrent writer, and a transaction layer relies
on atomicity to recover a consistent history. Testing then finds that some providers read
the version and write the entries in separate requests, so a concurrent writer can
interleave, and a failure can leave a partial append.

| Response | Effect |
| --- | --- |
| Each affected provider applies the version check and the write in one atomic operation, such as a conditional write or a storage transaction. | The contract holds for every dependent, current and future. |
| A shared conformance test kit exercises the contract, and every provider runs it. | Each provider, including new ones, demonstrates the guarantee before it ships. |
| The journaling and transaction layers keep their code and tests as written against the contract. | The guarantee lives in one place, and dependents stay simple. |

Compensation above the provider, such as re-reading after each append, adding a lock,
retrying, or excluding the affected providers from tests, copies the guarantee into one
dependent, leaves the others exposed, and keeps the defect in the provider. When no
implementation can supply a guarantee, the owning boundary changes its contract and each
dependent is revised against the new contract.

**Check:** For a found violation, name the owning layer, its fix, and the contract test
that fails before the fix and passes after it for every implementation. Verify that the
dependent layers' code and tests are unchanged.

Sources: [Parnas][parnas], [It Takes Two to Contract][contract].

## Make retries preserve one logical effect

**Rule:** Specify the relationship among operation identity, committed effects, and
the result returned after a retry.

Consider `CreditAccount(operationId, account, amount)`. In this example, the database
supports serializable transactions, and every attempt for an operation reaches the
same authoritative receipt record:

```text
atomic transaction:
    if receipt[operationId] exists:
        require its canonical request matches this request
        result = its recorded result
    else:
        apply the account credit
        record operationId, canonical request, and result
return result after the transaction commits
```

The effect and receipt share a commit and recovery fate. Concurrent attempts serialize
through the same uniqueness decision. A retry after a lost response returns the recorded
result; an ID reused with different arguments produces a defined conflict.

Receipt retention covers the advertised retry window. The protocol defines the outcome
of older IDs when their records have been reclaimed. For an external effect, the
receiving system participates in duplicate-safe execution or an explicit reconciliation
protocol. An outbox supplies durable dispatch; the receiver owns the execution guarantee.

**Check:** Commit the transaction, lose the response, and retry with the same ID. Verify
one credit and the original receipt. Also test concurrent attempts, conflicting payloads,
and the receipt-retention boundary.

This is an application of end-to-end reasoning and explicit state-machine contracts;
the transactional sketch states the additional assumptions needed for its guarantee.

Sources: [Saltzer, Reed, and Clark][endtoend], [Lampson][lampson].

## Make error paths preserve the state machine

**Rule:** Classify a condition at its owning boundary, then give each reachable outcome
a defined state transition.

| Condition | Owning behavior | Evidence |
| --- | --- | --- |
| Invalid external input | Parse and reject it before it changes trusted state. | Boundary cases verify the error and unchanged state. |
| A durable write fails | Return the I/O failure and retain an uncommitted operation state. | Inject the failure and verify publication remains withheld. |
| A routing hint is stale | Detect staleness and refresh through the authority under the routing contract. | Move ownership and exercise the stale route. |
| A programming invariant is violated | Diagnose the precise broken assumption with the repository's assertion policy. | Assertion-enabled checks expose the violated term. |

The invariant `Published implies Committed` is established by control flow and the
storage commit contract. An internal `Debug.Assert` can diagnose a violation; operational
I/O failures have their normal runtime error path in every build.

Yuan et al. studied 198 failures, including 48 catastrophic failures, in five distributed
data-intensive systems. Incorrect handling of non-fatal errors accounted for 92% of those
catastrophic failures. This motivates examining error handlers and their resulting states.
The study's evidence concerns those systems and failures; choosing an operational
alternative still follows the component's own specification.

**Check:** For every changed handler, name the resulting state, the caller-visible
outcome, and the test oracle. A log entry records context; the state transition
establishes the behavior.

Sources: [Yuan et al.][yuan], [Parse, don't validate][parse].

## Fence work at the protected resource

**Rule:** Enforce ownership where a delayed operation would mutate the protected state.

For a service that promises a single active writer, consider this cutover:

```text
Owner A obtains generation 7 and issues a delayed write.
The authority installs generation 8 at the protected store, then enables owner B.
The store atomically checks the generation and applies each mutation.
A's generation-7 write arrives and receives a stale-owner result.
```

The protected store's invariant is: after generation 8 is installed, requests carrying
an earlier generation cannot change its state. The ownership-installation path is
authenticated, and resource recovery preserves that fencing decision before writes
resume. The service contract defines what happens to work already committed before
the cutover.

Chubby's sequencers attach lock identity, mode, and generation to operations, with
validation by the recipient. Paxos Made Live describes a related technique: database
operations are conditional on a stored master epoch.

**Check:** Pause an owner, transfer ownership, and release its delayed write. Verify the
resource's contents and version, then restart the resource and repeat the stale write.

Sources: [Chubby, section 2.4][chubby], [Paxos Made Live, section 5.3][paxos].

## Reconfigure with virtual synchrony

**Rule:** An operation takes effect within the configuration that accepted it, or not at
all. The reconfiguration decision fixes the old configuration's closing state.

Virtual synchrony began as a group-communication model, in which members agree on the set
of messages delivered in each membership view. Birman, Malkhi, and van Renesse restate it
for any reconfigurable service, as one recipe applied epoch by epoch:

```text
1. Decide the next configuration within the current one.
2. Seal (wedge) the current configuration: members reject new operations for that epoch.
3. Agree on the closing state: every operation that completed or may still complete.
4. Transfer that state to the next configuration.
5. Enable the next configuration; its messages never mix with the previous epoch's.
```

Without step 3, each new member pulls whatever state the old members it happens to query
hold. An operation that reached only some old members then stays ambiguous: a later
reconfiguration can make it durable, so it completes retroactively, arbitrarily far in the
future. Agreeing on the closing state as part of the reconfiguration decision removes that
ambiguity.

Sealing is fencing applied to a whole configuration. The same pattern recurs throughout
systems built by Balakrishnan, Malkhi, and colleagues:

- **CORFU:** every message carries an epoch. A flash unit sealed at epoch `e` rejects
  messages tagged `e` or lower, and reports its highest written offset so the
  reconfiguring client can determine the sealed projection's tail.
- **Delos:** the VirtualLog seals the active loglet, reads its tail, and records that
  position in the new chain. Appends that become durable on the sealed loglet after the
  seal ("zombie appends") fall outside the mapped segment and never appear in the
  virtual log. Clog's segments are sealed the same way before the segment map closes them.
- **Vertical Paxos:** a configuration master activates a new ballot only once state
  transfer from the previous configuration is complete. Vertical Paxos I decouples the
  two by letting a new configuration accept requests while state transfer continues.

FoundationDB's recovery and the Orleans grain directory's partition handoff on membership
changes apply the same structure: stop the old epoch, fix its cut point, and only then
start serving from the new one.

**Check:** Let an operation reach only some members, then reconfigure. Revive the old
members and repeat the reconfiguration with different survivors. Verify that the operation
appears in every later configuration or in none, and that requests tagged with a sealed
epoch are rejected.

Sources: [Birman, Malkhi, and van Renesse, sections 1 and 6][vsync],
[Reliable Communication in the Presence of Failures][birman87], [CORFU, sections 3.1 and 3.2.1][corfu],
[Virtual Consensus in Delos, sections 3.2 and 3.4][delos], [Vertical Paxos][vpaxos],
[LogDrive][logdrive].

## Recover before rejoining a role

**Rule:** Derive role eligibility from the restored evidence needed for that role's
safety guarantees.

A replica that loses persistent promises has lost part of the history on which
consensus safety depends. Paxos Made Live handles a corrupted replica through a
specific non-voting catch-up protocol and an additional fresh consensus instance before
voting resumes. That eligibility condition belongs to that protocol and its fault model.

Checkpoint recovery has a similar boundary. A snapshot through log position `k`, its
protocol metadata, and replay after `k` together define the recovered state. The snapshot
must represent the state at that position even when capture runs concurrently with new
writes. Reclaiming older log records follows durable publication of a verified snapshot
and the metadata needed to recover it.

**Check:** State the evidence needed for each transition, such as
`Recovering -> CatchingUp -> Eligible`. Inject failure during snapshot capture,
snapshot publication, and log reclamation. Verify that each surviving recovery path
reconstructs an allowed committed history.

Sources: [Paxos Made Live, sections 5.1 and 5.5][paxos].

## Test histories and failure cut points

**Rule:** Derive the oracle from the public state machine, including ambiguous outcomes.

For the credit example, a reference model records operation IDs, arguments, committed
credits, and receipts. A timed-out operation may already have committed. The model
therefore admits histories consistent with that ambiguity while enforcing duplicate
semantics and every acknowledged durability promise.

| Failure point | Required observation under the example's contract |
| --- | --- |
| Before a known commit outcome | The history admits an uncommitted operation or a committed operation whose response was lost. |
| After commit, before the reply | Recovery includes the effect and receipt; a retry returns the recorded result. |
| After a successful durability receipt | Recovery retains the acknowledged effect within the stated fault model. |
| During checkpoint publication or log reclamation | Snapshot plus replay represents an allowed committed history. |

Use a controlled clock, transport, scheduler, randomness, and initial state where those
affect the property. Record the operation trace and seed so a failing execution can be
replayed. Start with the smallest topology that crosses the affected boundary and add
larger or correlated-failure scenarios when the contract requires them.

Separate safety and progress phases. During injected failures, check invariants against
the allowed histories. Then restore the protocol's quorum, scheduling, and communication
assumptions and verify progress under a stated bound. Paxos Made Live uses this distinction;
FoundationDB demonstrates deterministic simulation across a complete cluster.

**Check:** Name the oracle before selecting the test technique. Minimize a failing
history into the few operations and failure events needed to preserve the violation.

Sources: [Paxos Made Live, section 7][paxos], [FoundationDB simulation][foundationdb],
[Yuan et al.][yuan].

## Budget fan-out and speculative work

**Rule:** Measure the latency of the complete request and account for all work it creates.

If a request waits for all `n` independent subrequests, each with probability `p` of
exceeding a latency threshold, the probability of a slow overall request is
`1 - (1 - p)^n`. For `p = 0.01` and `n = 100`, that is about 63.4%. These assumptions
make the example useful for a first estimate; production measurements also account for
correlated delays, queueing, and shared resources.

Measure end-to-end percentiles under representative load, including background work.
When measured replica latency justifies hedging, define its delay, admission budget,
shared request deadline, and cancellation behavior. Eligible replicas must satisfy the
same result contract, and duplicate execution must preserve the operation's semantics.
The work budget includes attempts still running after cancellation or a lost response.

Batching likewise has a contract: a maximum size or wait, per-item ordering and results,
and a defined outcome for partial failure. Move repeated policy decisions outside the
batch and retain the information needed to report each item's outcome.

**Check:** Vary fan-out and offered load; measure tail latency, queue depth, extra
attempts, and completed work. Verify the overload outcome and the original correctness
contract as well as the latency change.

Sources: [The Tail at Scale][tail], [Push Ifs Up And Fors Down][batch].

[parnas]: https://github.com/ReubenBond/research-papers/blob/main/On_the_Criteria_To_Be_Used_in_Decomposing_Systems_into_Modules/On_the_Criteria_To_Be_Used_in_Decomposing_Systems_into_Modules.md
[orleans]: https://github.com/ReubenBond/research-papers/blob/main/Orleans_Distributed_Virtual_Actors_for_Programmability_and_Scalability/Orleans_Distributed_Virtual_Actors_for_Programmability_and_Scalability.md
[lampson]: https://github.com/ReubenBond/research-papers/blob/main/Hints_and_Principles_for_Computer_System_Design/Hints_and_Principles_for_Computer_System_Design.md
[endtoend]: https://github.com/ReubenBond/research-papers/blob/main/End-to-End_Arguments_in_System_Design/End-to-End_Arguments_in_System_Design.md
[logdrive]: https://github.com/ReubenBond/research-papers/blob/main/The_LogDrive_Composable_Durability_for_Cloud-Based_Shared_Logs/The_LogDrive_Composable_Durability_for_Cloud-Based_Shared_Logs.md
[yuan]: https://github.com/ReubenBond/research-papers/blob/main/Simple_Testing_Can_Prevent_Most_Critical_Failures/Simple_Testing_Can_Prevent_Most_Critical_Failures.md
[parse]: https://github.com/ReubenBond/research-papers/blob/main/Parse_Dont_Validate/Parse_Dont_Validate.md
[contract]: https://github.com/ReubenBond/research-papers/blob/main/It_Takes_Two_to_Contract/It_Takes_Two_to_Contract.md
[chubby]: https://github.com/ReubenBond/research-papers/blob/main/The_Chubby_Lock_Service_for_Loosely-Coupled_Distributed_Systems/The_Chubby_Lock_Service_for_Loosely-Coupled_Distributed_Systems.md
[vsync]: https://github.com/ReubenBond/research-papers/blob/main/Virtually_Synchronous_Methodology_for_Dynamic_Service_Replication/Virtually_Synchronous_Methodology_for_Dynamic_Service_Replication.md
[birman87]: https://github.com/ReubenBond/research-papers/blob/main/Reliable_Communication_in_the_Presence_of_Failures/Reliable_Communication_in_the_Presence_of_Failures.md
[corfu]: https://github.com/ReubenBond/research-papers/blob/main/CORFU_A_Shared_Log_Design_for_Flash_Clusters/CORFU_A_Shared_Log_Design_for_Flash_Clusters.md
[delos]: https://github.com/ReubenBond/research-papers/blob/main/Virtual_Consensus_in_Delos/Virtual_Consensus_in_Delos.md
[vpaxos]: https://github.com/ReubenBond/research-papers/blob/main/Vertical_Paxos_and_Primary-Backup_Replication/Vertical_Paxos_and_Primary-Backup_Replication.md
[paxos]: https://github.com/ReubenBond/research-papers/blob/main/Paxos_Made_Live_An_Engineering_Perspective/Paxos_Made_Live_An_Engineering_Perspective.md
[raft]: https://github.com/ReubenBond/research-papers/blob/main/In_Search_of_an_Understandable_Consensus_Algorithm/In_Search_of_an_Understandable_Consensus_Algorithm.md
[rollback]: https://doi.org/10.1145/568522.568525
[durablelin]: https://doi.org/10.1007/978-3-662-53426-7_23
[fsyncgate]: https://wiki.postgresql.org/wiki/Fsync_Errors
[fsyncfailures]: https://www.usenix.org/conference/atc20/presentation/rebello
[crashonly]: https://www.usenix.org/conference/hotos-ix/crash-only-software
[foundationdb]: https://github.com/ReubenBond/research-papers/blob/main/FoundationDB_Simulation_and_Testing/FoundationDB_Simulation_and_Testing.md
[tail]: https://github.com/ReubenBond/research-papers/blob/main/The_Tail_at_Scale/The_Tail_at_Scale.md
[batch]: https://github.com/ReubenBond/research-papers/blob/main/Push_Ifs_Up_and_Fors_Down/Push_Ifs_Up_and_Fors_Down.md
