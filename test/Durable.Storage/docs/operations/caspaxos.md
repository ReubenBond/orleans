# Fast CASPaxos MetaStore operations

This guide covers running the native Fast CASPaxos MetaStore: the cluster's own silos serve the metadata registers of
durable storage, with voters on selected silos, a proposer on every silo, Orleans RPC between them, SQLite persistence,
and operator-driven voter reconfiguration. It covers configuring voters and proposers, sizing quorums, keeping SQLite
state durable, voter lifecycle, replacing/adding/removing voters, crash recovery, and the failures operators observe.
The register contract (conditional writes, versions, ambiguous outcomes, key layout) is specified in
[MetaStore](../spec/components/metastore.md); the native consensus mechanism is
[MetaStore §8.1](../spec/components/metastore.md#81-fast-caspaxos-semantics).

## Topology

At bootstrap (`InitialConfiguration = 1`), every silo registers the MetaStore with the identical bootstrap voter list
`Voters`. After the first reconfiguration, the reserved configuration register `__caspaxos/config` becomes the
authority for the active voter set, and proposers refresh automatically when they observe a higher configuration floor.
Each voter is served by the silo at its endpoint; every silo runs a proposer.

| Part | Identity | Durable state |
| --- | --- | --- |
| Voter | `Id`, unique and never reused after retirement, bound to its database | Acceptor SQLite database and its WAL, holding accepted state, acceptor floor, identity binding, bootstrap digest, and granted admissions |
| Voter's silo | The voter's `Endpoint`, an advertised silo endpoint `address:port`; each restart has a new silo generation | — |
| Proposer | `ProposerId`, a cluster-unique positive integer per silo | Proposer allocator SQLite database and its WAL holding monotonic `(configuration, round)` reservations |

A proposer reaches each voter's system target over Orleans RPC at the newest live silo generation which Orleans
membership holds at the voter's endpoint, and counts a reply only from the voter it addressed. A voter's own silo
reaches it in process while it is a member of the active configuration. Orleans membership is configured as for any
cluster and never depends on the registers consensus serves.

## Membership and quorums

| Voters | Fast quorum | Classic recovery quorum | Unavailable voters tolerated |
| --- | --- | --- | --- |
| 5 | 4 | 3 | 1 on the fast path; 2 through classic recovery |
| 4 | 3 | 3 | 1 on the fast path; 1 through classic recovery |
| 3 | 3 | 2 | 0 on the fast path; 1 through classic recovery |

Size the voter set for the number of simultaneous voter losses it must ride through: five voters keep writes available
with two down. Membership changes—replacing a lost voter, adding a voter, removing a voter, or migrating to a new voter
set—run through [`CasPaxosReconfigurationService`](#voter-reconfiguration).

The data plane sizes its quorums independently: a ledger's replicas acknowledge a write at their own quorum (`Q` of
`N`, 2 of 3 by default) regardless of the metadata quorum state.

## Configuration

`AddCasPaxosMetaStore` (`Orleans.Durable.Storage.CasPaxos.Hosting`) registers both `IMetaStore` and
`CasPaxosReconfigurationService`, from a configuration section or in code. An unknown key or an out-of-range option
fails the silo's start.

```csharp
builder.UseOrleans(silo => silo
    .AddCasPaxosMetaStore(builder.Configuration.GetSection("CasPaxos"))
    .AddDurableJournalStorage(builder.Configuration.GetSection("DurableStorage")));
```

| Setting | Guidance | Default |
| --- | --- | --- |
| `Voters` | Bootstrap voters (`InitialConfiguration = 1`), identical on every bootstrap silo: `{ "Id", "Endpoint", "Database" }`. After bootstrap voters are retired, discovery seeds for `InitialConfiguration > 1`. | — |
| `InitialConfiguration` | 1-based configuration number of `Voters`. Set greater than `1` only when all bootstrap voters have been retired and `Voters` names an active configuration's discovery seeds; startup verifies `__caspaxos/config` is active. | `1` |
| `JoiningVoter` | Optional configuration for a new voter first introduced on this silo in configuration `c_join >= 2`: `{ "Id", "Database", "JoinConfiguration", "AdmissionPeers": [{ "Id", "Endpoint" }] }`. | `null` |
| `ProposerId` | This silo's proposer, positive and unique in the cluster for the lifetime of its database | — |
| `ProposerDatabase` | This proposer's allocator file, distinct from every acceptor file | — |
| `OperationTimeout`, `AttemptTimeout`, `RetryBackoff` | Register operation deadline, protocol phase deadline, retry delay | `10s`, `1s`, `10ms` |
| `Capacity` | Register operations the proposer admits at once; one more fails as busy | `256` |
| `VoterQueueCapacity`, `VoterMaxBatchSize` | The hosted voter's SQLite admission queue and transaction batch size | `256`, `64` |
| `AdmissionRetryDelay` | The longest delay between a fresh voter's admission attempts | `1s` |

A section for a three-voter bootstrap cluster, on the silo which serves `voter-0` (its proposer is `1`; the other silos
use `2` and `3`):

```json
{
  "CasPaxos": {
    "Voters": [
      { "Id": "voter-0", "Endpoint": "10.0.0.10:11111", "Database": "/var/lib/orleans/consensus/acceptor.db" },
      { "Id": "voter-1", "Endpoint": "10.0.0.11:11111", "Database": "/var/lib/orleans/consensus/acceptor.db" },
      { "Id": "voter-2", "Endpoint": "10.0.0.12:11111", "Database": "/var/lib/orleans/consensus/acceptor.db" }
    ],
    "ProposerId": 1,
    "ProposerDatabase": "/var/lib/orleans/consensus/proposer.db"
  }
}
```

Give each voter silo a stable advertised endpoint, for example a StatefulSet pod's stable address and a fixed silo port,
and a persistent volume holding its databases.

## SQLite durability

- Keep each acceptor database, each proposer allocator database, and their WAL files for the lifetime of that identity.
  A voter which restarts with its original files rejoins in any order, without admission.
- Put them on local file systems with SQLite locking and durable flush; give the acceptor and the allocator distinct
  paths, and never let two processes open the same database.
- Before a silo's first start, create the parent of each database's directory on durable storage. The silo creates the
  database directory under that parent when it is missing and flushes the parent, then each database flushes its
  directory, so the directory, database, and WAL names are durable before the voter answers.
- Linux and macOS flush a directory with `fsync`. On Windows, data directories lie on local NTFS volumes; any other
  volume fails startup.
- A voter's database is bound to its voter identity and bootstrap digest: opened under another identity, holding
  acceptor state without a binding, or opened with a disagreeing bootstrap voter list, it fails the silo's start before
  the voter answers.
- A proposer allocator rolled back to older state, or a fresh allocator under a former `ProposerId`, can reuse ballots
  other voters relied on. Fresh proposer storage takes a new, never-used `ProposerId`.

## Lifecycle

| Operation | Procedure |
| --- | --- |
| First start | Create the parent of each database's directory durably, then start the bootstrap silos with the identical `Voters`. Each fresh voter validates bootstrap digest agreement and serves once a majority of the other voters admit it, so start the voter silos together (for a StatefulSet, `podManagementPolicy: Parallel`): with three voters, each first start needs both others. Durable storage's node registers once a quorum of voters serves. |
| Restart | Restart a silo at its endpoint with its original databases and WAL files. Its voter keeps its identity under the new silo generation and serves at once; if the cluster reconfigured while it was stopped, its proposer discovers the active configuration from `__caspaxos/config` automatically. |
| Graceful stop | The silo drains its proposer's admitted operations and its voter's admitted requests before their databases close. |
| Rolling update | Restart one voter silo at a time, keeping a classic-recovery quorum of voters serving. |

A voter silo's start waits for its admission after joining membership and before becoming active; its log reports how
many of the other voters have admitted it.

## Voter reconfiguration

Every silo with `AddCasPaxosMetaStore` exposes `CasPaxosReconfigurationService` from dependency injection. Each
reconfiguration advances the configuration number by one (`c -> c + 1`) through four idempotent, resumable steps:
**Choose** (`__caspaxos/config` transitions to `Pending`) → **Fence `c`** (raise `floor` on a classic quorum `F` of
`V(c)` to `(c + 1, 0, 0)`) → **Install** (enumerate keys on `F` and transfer each key via Vertical Paxos I hand-off into
`V(c + 1)`, transferring `__caspaxos/config` last) → **Activate** (`__caspaxos/config` transitions to `Active` in
`c + 1`).

Register mutations and linearizable reads pause briefly between **Fence `c`** and **Activate**. Data-plane writes and
hot reads of already-loaded Durable Storage ledgers continue uninterrupted during the pause.

### Inspecting the current configuration

```csharp
var reconfig = siloServices.GetRequiredService<CasPaxosReconfigurationService>();
var current = await reconfig.GetCurrentConfigurationAsync(cancellationToken);
// current.Number, current.State (Active or Pending), current.CanonicalVoters
```

### Replacing a lost voter

1. Inspect `current = await reconfig.GetCurrentConfigurationAsync(ct)` and verify `current.State == Active` and a
   classic quorum of `V(c)` is healthy (with 3 voters and 1 lost, the 2 surviving voters form the required quorum; no
   further loss in `V(c)` is tolerated until Activate commits).
2. Allocate a brand-new, never-previously-used voter identity (for example `voter-3` to replace retired `voter-2`) and a
   fresh acceptor database path on the replacement silo (either a new silo endpoint or an in-place pod replacement at the
   same endpoint).
3. Configure `JoiningVoter` on the replacement silo while keeping `Voters` at the active bootstrap/seed configuration:

   ```json
   {
     "CasPaxos": {
       "Voters": [
         { "Id": "voter-0", "Endpoint": "10.0.0.10:11111", "Database": "/var/lib/orleans/consensus/acceptor.db" },
         { "Id": "voter-1", "Endpoint": "10.0.0.11:11111", "Database": "/var/lib/orleans/consensus/acceptor.db" },
         { "Id": "voter-2", "Endpoint": "10.0.0.12:11111", "Database": "/var/lib/orleans/consensus/acceptor.db" }
       ],
       "JoiningVoter": {
         "Id": "voter-3",
         "Database": "/var/lib/orleans/consensus/voter-3.db",
         "JoinConfiguration": 2,
         "AdmissionPeers": [
           { "Id": "voter-0", "Endpoint": "10.0.0.10:11111" },
           { "Id": "voter-1", "Endpoint": "10.0.0.11:11111" }
         ]
       },
       "ProposerId": 4,
       "ProposerDatabase": "/var/lib/orleans/consensus/proposer.db"
     }
   }
   ```

4. Start the replacement silo and wait for it to become active. Startup atomically binds `voter-3`, initializes its
   acceptor floor to `(2, 0, 0)`, obtains majority admission from `{voter-0, voter-1}`, and opens its system target.
5. Run replacement from any silo:

   ```csharp
   var active = await reconfig.ReplaceVoterAsync(
       lostVoterId: "voter-2",
       replacement: new VoterDescriptor("voter-3", "10.0.0.13:11111"),
       cancellationToken);
   ```

6. Verify `active.State == VoterConfigurationState.Active` and `active.Number == c + 1`. Surviving and stale silos
   automatically refresh to `c + 1` on their next MetaStore operation. Do not wipe any surviving `V(c)` voter until
   `ReplaceVoterAsync` returns `Active`.

### Adding a voter

1. Inspect `current = await reconfig.GetCurrentConfigurationAsync(ct)` (`c = current.Number`).
2. Start the new silo with `JoiningVoter` naming its fresh `Id`, fresh `Database`, `JoinConfiguration = c + 1`, and
   `AdmissionPeers` listing the current voters `V(c)`. Wait for the silo to become active.
3. Add the voter from any silo:

   ```csharp
   var active = await reconfig.AddVoterAsync(
       new VoterDescriptor("voter-3", "10.0.0.13:11111"),
       cancellationToken);
   ```

### Removing a voter

1. Verify `current.Voters.Length > 1` and at least a classic quorum of both `V(c)` and `V(c) \ {voterId}` are online.
2. Remove the voter from any silo:

   ```csharp
   var active = await reconfig.RemoveVoterAsync("voter-2", cancellationToken);
   ```

3. Only after `RemoveVoterAsync` returns `Active`, stop and decommission the retired voter's silo and database. Its
   identity is permanently retired and must never be reused.

### Crash recovery and resuming a pending reconfiguration

Every reconfiguration step is idempotent. If the operator silo, network, or a voter fails mid-reconfiguration (leaving
`__caspaxos/config` in `Pending`), call `ResumeAsync` from any live silo (or re-run the same `ReplaceVoterAsync` /
`AddVoterAsync` / `RemoveVoterAsync` / `ReconfigureAsync` call):

```csharp
var active = await reconfig.ResumeAsync(cancellationToken);
```

Whenever `__caspaxos/config` is `Pending`, `ResumeAsync` always re-runs **Fence `c` → Install all keys → Activate** so
no key is ever left untransferred. If a member of the active floor quorum `F` failed during Install in a 5-voter
cluster, re-running Fence raises the floor on the remaining surviving voter of `V(c)` to form a new classic quorum `F'`.
If two operators race with different target voter sets, one wins Choose and the other receives
`ReconfigurationConflictException` naming the winning `VoterConfiguration`.

### Updating seed configuration after retiring all bootstrap voters

Silos whose configuration still names `V(1)` follow the configuration chain `1 -> 2 -> ... -> c` automatically as long
as a classic quorum of `V(1)` remains reachable. Before decommissioning the last bootstrap voters of `V(1)`, update
restarting/new silos' `Voters` to the active voter set `V(c)` and set `InitialConfiguration = c` (removing
`JoiningVoter` once the voter is part of `V(c)`). On startup, those silos verify `(c' >= c, V(c'), Active)` in
`__caspaxos/config` before serving.

## Failures operators observe

| Condition | Effect | Action |
| --- | --- | --- |
| A minority of voters down | Writes continue, through classic recovery when the fast quorum is short | Restore the voter silos with their original state, or replace a permanently lost voter via `ReplaceVoterAsync` |
| Fewer than a classic quorum of voters reachable | Register reads and writes fail, so every operation which must read or write a register waits: ledger-map transitions, node registrations, and the drains and drops which depend on them. Writes and hot reads continue, and data already acknowledged stays durable. | Restore a quorum; voters rejoin with their original state |
| MetaStore pause during reconfiguration (`Pending`) | Register reads and writes wait or fail transiently between Fence and Activate; already-loaded data-plane operations continue | Wait for reconfiguration to finish, or run `ResumeAsync` if the operator crashed |
| Competing operator reconfiguration | The losing operator receives `ReconfigurationConflictException` naming the winning configuration | Inspect `WinningConfiguration` and call `ResumeAsync` if it is still `Pending` |
| A write's reply is lost, times out, or is cancelled after dispatch | The outcome is unknown: the register may or may not have changed. The caller re-reads the register and tests its goal ([MetaStore §2.2](../spec/components/metastore.md#22-iconditionalregister)). | None |
| A proposer is at capacity | The call fails as busy before mutation | Retry later or raise `Capacity` |
| Bootstrap silos configured with differing `V(1)` voter lists | Admission rejects startup immediately with a bootstrap configuration digest mismatch | Configure the identical `V(1)` `Voters` list on every bootstrap silo |
| A voter silo restarted on an empty disk under its old identity | The other voters, which admitted its first incarnation, refuse it; the silo's start fails with an error saying the voter's state was lost | Restore the voter's original databases, or permanently retire the lost identity and replace it with a fresh voter identity via `CasPaxosReconfigurationService.ReplaceVoterAsync` |
| The voter of a single-voter deployment restarted on an empty disk | No other voter remembers its first incarnation, so it admits itself and serves empty registers: the loss goes undetected | Deploy three or five voters; with one, restore its databases before its silo starts |
| A database bound to another voter, or acceptor state without a binding | The silo's start fails before the voter answers | Correct the configuration or the volume mapping |
| A SQLite directory flush, transaction, admission record, floor update, or ballot reservation fails | The process writes the operation's site, the path, and the operating-system error to standard error and exits with code 117 (`ConsensusDirectoryFlush`) or 118 (`ConsensusTransaction`) ([conventions §14](../spec/conventions.md#14-local-storage-failures-stop-the-process)); afterward it cannot know what the device holds | Let the supervisor restart the silo, which recovers from the device's state; replace the device if the failure repeats |

## Verification

`Orleans.Durable.Storage.CasPaxos.Tests` runs the register contract, fault scenarios, and reconfiguration transport
tests (`NativeConsensusTests`, `NativeReconfigurationTests`) over real SQLite voters through the production voter
service and client; `Orleans.Consensus.CasPaxos{,.Server,.Sqlite}.Tests` verify configuration-qualified ballots, floor
fencing, Vertical Paxos I hand-off, and SQLite durability; and `Orleans.Durable.Storage.Tests`
(`OrleansNativeConsensusTests`, `OrleansNativeConsensusReconfigurationTests`) runs durable storage over multi-silo
voter clusters with real Orleans RPC ([MetaStore §10](../spec/components/metastore.md#10-verification)).
