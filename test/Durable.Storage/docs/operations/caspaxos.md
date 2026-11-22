# Fast CASPaxos MetaStore operations

This guide covers running the native Fast CASPaxos MetaStore: the cluster's own silos serve the metadata registers of
durable storage, with fixed voters on selected silos, a proposer on every silo, Orleans RPC between them, and SQLite
persistence. It covers configuring voters and proposers, sizing quorums, keeping SQLite state durable, the lifecycle of
a voter, and the failures operators observe. The register contract (conditional writes, versions, ambiguous outcomes,
key layout) is specified in [MetaStore](../spec/components/metastore.md); the native consensus's mechanism is
[MetaStore §8.1](../spec/components/metastore.md#81-fast-caspaxos-semantics).

## Topology

Every silo of the cluster registers the MetaStore with the same voter list. Each voter is served by the silo whose
advertised silo endpoint is the voter's endpoint; every silo runs a proposer.

| Part | Identity | Durable state |
| --- | --- | --- |
| Voter | `Id`, unique in `Voters`, bound to its database | Acceptor SQLite database and its WAL, which also hold the voter's identity binding and the admissions it granted |
| Voter's silo | The voter's `Endpoint`, an advertised silo endpoint `address:port`; each restart has a new silo generation | — |
| Proposer | `ProposerId`, a cluster-unique positive integer per silo | Proposer allocator SQLite database and its WAL |

A proposer reaches each voter's system target over Orleans RPC at the newest silo generation which Orleans membership
holds at the voter's endpoint, and counts a reply only from the voter it addressed. A voter's own silo reaches it in
process. Orleans membership is configured as for any cluster and never depends on the registers consensus serves.

## Membership and quorums

| Voters | Fast quorum | Classic recovery quorum | Unavailable voters tolerated |
| --- | --- | --- | --- |
| 5 | 4 | 3 | 1 on the fast path; 2 through classic recovery |
| 3 | 3 | 2 | 0 on the fast path; 1 through classic recovery |

Size the voter set for the number of simultaneous voter losses it must ride through: five voters keep writes available
with two down. Membership is fixed: adding or removing a voter, and replacing a voter whose state was lost, are
[deferred work](../spec/deferred.md).

The data plane sizes its quorums independently: a ledger's replicas acknowledge a write at their own quorum (`Q` of
`N`, 2 of 3 by default) regardless of the metadata quorum state.

## Configuration

`AddCasPaxosMetaStore` (`Orleans.Durable.Storage.CasPaxos.Hosting`) registers the silo's `IMetaStore`, from a
configuration section or in code. An unknown key fails the silo's start, and so does an option out of range.

```csharp
builder.UseOrleans(silo => silo
    .AddCasPaxosMetaStore(builder.Configuration.GetSection("CasPaxos"))
    .AddDurableJournalStorage(builder.Configuration.GetSection("DurableStorage")));
```

| Setting | Guidance | Default |
| --- | --- | --- |
| `Voters` | Every voter: `{ "Id", "Endpoint", "Database" }`, identical on every silo. Ids and endpoints are unique; each endpoint is the advertised silo endpoint of the silo which serves the voter. | — |
| `ProposerId` | This silo's proposer, positive and unique in the cluster for the lifetime of its database | — |
| `ProposerDatabase` | This proposer's allocator file, distinct from every acceptor file | — |
| `OperationTimeout`, `AttemptTimeout`, `RetryBackoff` | Register operation deadline, protocol phase deadline, retry delay | `10s`, `1s`, `10ms` |
| `Capacity` | Register operations the proposer admits at once; one more fails as busy | `256` |
| `VoterQueueCapacity`, `VoterMaxBatchSize` | The hosted voter's SQLite admission queue and transaction batch size | `256`, `64` |
| `AdmissionRetryDelay` | The longest delay between a fresh voter's admission attempts | `1s` |

A section for a three-voter cluster, on the silo which serves `voter-0` (its proposer is `1`; the other silos use `2`
and `3`):

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
- A voter's database is bound to its voter identity: opened under another identity, or holding acceptor state without a
  binding, it fails the silo's start before the voter answers.
- A proposer allocator rolled back to older state, or a fresh allocator under a former `ProposerId`, can reuse ballots
  other voters relied on. Fresh proposer storage takes a new, never-used `ProposerId`.

## Lifecycle

| Operation | Procedure |
| --- | --- |
| First start | Create the parent of each database's directory durably, then start the silos with the same `Voters`. Each fresh voter serves once a majority of the other voters admit it, so start the voter silos together (for a StatefulSet, `podManagementPolicy: Parallel`): with three voters, each first start needs both others. Durable storage's node registers once a quorum of voters serves. |
| Restart | Restart a silo at its endpoint with its original databases and WAL files. Its voter keeps its identity under the new silo generation and serves at once. |
| Graceful stop | The silo drains its proposer's admitted operations and its voter's admitted requests before their databases close. |
| Rolling update | Restart one voter silo at a time, keeping a classic-recovery quorum of voters serving. |

A voter silo's start waits for its admission after joining membership and before becoming active; its log reports how
many of the other voters have admitted it.

## Failures operators observe

| Condition | Effect | Action |
| --- | --- | --- |
| A minority of voters down | Writes continue, through classic recovery when the fast quorum is short | Restore the voter silos with their original state |
| Fewer than a classic quorum of voters reachable | Register reads and writes fail, so every operation which must read or write a register waits: ledger-map transitions, node registrations, and the drains and drops which depend on them. Writes and hot reads continue, and data already acknowledged stays durable. | Restore a quorum; voters rejoin with their original state |
| A write's reply is lost, times out, or is cancelled after dispatch | The outcome is unknown: the register may or may not have changed. The caller re-reads the register and tests its goal ([MetaStore §2.2](../spec/components/metastore.md#22-iconditionalregister)). | None |
| A proposer is at capacity | The call fails as busy before mutation | Retry later or raise `Capacity` |
| A voter silo restarted on an empty disk under its old identity | The other voters, which admitted its first incarnation, refuse it; the silo's start fails with an error saying the voter's state was lost | Restore the voter's databases, or keep the silo out of the cluster: replacing a voter is [deferred work](../spec/deferred.md) |
| The voter of a single-voter deployment restarted on an empty disk | No other voter remembers its first incarnation, so it admits itself and serves empty registers: the loss goes undetected | Deploy three or five voters; with one, restore its databases before its silo starts |
| A database bound to another voter, or acceptor state without a binding | The silo's start fails before the voter answers | Correct the configuration or the volume mapping |
| A SQLite directory flush, transaction, admission record, or ballot reservation fails | The process writes the operation's site, the path, and the operating-system error to standard error and exits with code 117 (`ConsensusDirectoryFlush`) or 118 (`ConsensusTransaction`) ([conventions §14](../spec/conventions.md#14-local-storage-failures-stop-the-process)); afterward it cannot know what the device holds | Let the supervisor restart the silo, which recovers from the device's state; replace the device if the failure repeats |

## Verification

`Orleans.Durable.Storage.CasPaxos.Tests` runs the register contract and fault scenarios over real SQLite voters through
the production voter service and client, `Orleans.Consensus.CasPaxos.Sqlite.Tests` the identity binding and admission
records, and `Orleans.Durable.Storage.Tests`' `OrleansNativeConsensusTests` durable storage over three voter silos with
real Orleans RPC ([MetaStore §10](../spec/components/metastore.md#10-verification)).
