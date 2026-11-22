# Clog samples

`Orleans.Durable.Storage.Samples.Journaling` is an Orleans app whose journaled grain keeps its state in Clog.
`Orleans.Durable.Storage.Samples.AppHost` runs it as three silos on one machine, each with a node disk of its own, over
Azurite, which holds the cluster's MetaStore registers (Azure Table), the LSM archive's objects (Azure Blob), and
Orleans' membership (Azure Table). The Aspire dashboard shows each silo's logs and Clog's metrics.

## Run it

You need Docker, and [just](https://github.com/casey/just) run from `test/Durable.Storage` ([AGENTS.md](../../test/Durable.Storage/AGENTS.md#build-format-and-test)).

```powershell
just sample
```

The AppHost prints the dashboard's login URL. The silos serve HTTP on ports 5080 to 5082, and any silo reaches every
grain:

```powershell
Invoke-RestMethod -Method Put http://localhost:5080/grains/cart-1/apples -Body 3
Invoke-RestMethod -Method Put http://localhost:5081/grains/cart-1/pears -Body 5
Invoke-RestMethod http://localhost:5082/grains/cart-1
```

The read answers the grain's entries and the silo its activation runs on. `KeyValueGrain` holds an
`IDurableDictionary<string, string>`, which Orleans Journaling writes to the grain's journal in Clog, and its
`[JournalPlacement]` places each activation on the silo whose node is its journal's primary.

Each run is a new cluster: the AppHost, as the deployment, creates each silo's node root anew under `test/Durable.Storage/artifacts/sample`,
where it survives the silo's restarts within the run. Ctrl+C stops the silos and Azurite.

## What to watch

In the dashboard's **Metrics** page, choose a silo, then the `Orleans.Durable.Storage` meter:

- `orleans.durable.storage.ledger.age`: the age of the oldest undrained batch of each ledger the node is primary of. It
  climbs while a ledger's segment is open, and falls once the segment ends and drains to the LSM archive. The sample
  ends segments after two minutes (`DurableStorage:Node:MaxSegmentAge`, in its Development settings), so the sawtooth
  shows within a few minutes of writing.
- `orleans.durable.storage.ledger.undrained`: the bytes the node proposed to each of those ledgers at or above its drain
  watermark.
- `orleans.durable.storage.node.primaries`, `orleans.durable.storage.node.disk.used`,
  `orleans.durable.storage.node.disk.free`, and `orleans.durable.storage.node.disk.pressure`: the node's ledgers and its
  disk, which governance measures each control interval.

The `Orleans.Durable.Storage.Engine.Ledgers`, `Orleans.Durable.Storage.Engine.Journals`,
`Orleans.Durable.Storage.Engine.Wal`, and `Orleans.Durable.Storage.Engine.Archive.Lsm` meters show the layers beneath.
**Structured logs** show each node's start: it opens its LedgerStore, registers in the node registry, and logs that its
Journals serve once the archive store's record matches the silo's journal format.

## Kill a silo

Read a grain to find its silo, then end that silo's process. The dashboard's resource details show its process ID, or
find it by its Orleans port, 11111 plus the silo's ordinal:

```powershell
Invoke-RestMethod http://localhost:5080/grains/cart-1                # "silo": "silo-2"
Stop-Process -Id (Get-NetTCPConnection -LocalPort 11113 -State Listen).OwningProcess -Force
Invoke-RestMethod http://localhost:5080/grains/cart-1                # retry until it answers
```

Within about ten seconds, once membership declares the silo dead, the controller takes its ledgers over through the
surviving nodes, and the grain activates on another silo with every entry it had. **Start resource** in the dashboard
starts the silo again: its node rejoins from its disk under the same identity and takes work again. **Stop resource**
stops a silo gracefully instead, which hands its ledgers off before it stops.

Each ledger keeps three replicas (`DurableStorage:Control:Replicas`), one on each silo. When a silo dies, the takeover
keeps its dead replica in the successor while the two live ones form a quorum, and repairs it out once a node is free
([orleans §5.7](../../test/Durable.Storage/docs/spec/compositions/orleans.md#57-membership-and-replica-placement)), so three silos survive the
loss of one.

## Configuration

`appsettings.json` holds what a deployment sets too: the silo's Clog options under `DurableStorage`, where
`DurableStorage:Node` binds to `DurableStorageOptions` and `DurableStorage:Control` to `ControlOptions` by the keys
[orleans §6](../../test/Durable.Storage/docs/spec/compositions/orleans.md#6-configuration) lists, and a key which names no option stops the
silo. The AppHost runs the silos in the Development environment, whose `appsettings.Development.json` tunes the cluster
to show its behaviour within minutes on one machine. A deployment keeps the defaults:

| Key | Sample | Default | Why the sample differs |
| --- | --- | --- | --- |
| `DurableStorage:Node:MaxSegmentAge` | 2 minutes | 1 hour | Segments end and drain to the LSM within minutes, so `orleans.durable.storage.ledger.age` shows its sawtooth |
| `DurableStorage:Node:WalFileSize` | 64 MiB | 1 GiB | Three node roots share one disk |
| `DurableStorage:Node:PreallocateWalFiles` | `false` | `true` | A new WAL file is not zero-filled, so the silos start at once; a deployment reserves the space up front |
| `DurableStorage:Node:MinimumFreeDiskBytes` | 256 MiB | 1 GiB | A development machine's disk is small and shared |
| `DurableStorage:Control:LedgersPerNode` | 2 | 8 | Three silos and a few grains need few ledgers |
| `DurableStorage:Control:ReplicaGrace` | 1 minute | 10 minutes | A dead silo's replicas are rolled out within the demo; a deployment waits out a node's reboot |
| `DurableStorage:Control:SweepInterval` | 1 minute | 10 minutes | The LSM's sweeps show within the demo |

The AppHost sets what differs per silo: `DurableStorage:Node:Root`, the node's directory on its persistent volume, and
`DurableStorage:Node:Ordinal`; and `DurableStorage:Node:Cluster`, equal on every silo. `SampleSilo` registers Clog with
`AddDurableJournalStorage(configuration.GetSection("DurableStorage"))`; `Program.cs` supplies the MetaStore
(`AzureTableMetaStore`) and the LSM's object store (`AzureBlobObjectStore`), whose table and container names lie under
`Sample`, since the `DurableStorage` section holds Clog's options alone.

The cluster's archive store lies in that container under its store prefix, which Clog derives from Orleans'
`ServiceId`; the sample sets its own, `Sample:ServiceId`, since a silo whose service ID is one of Orleans' defaults,
`default` or localhost clustering's `dev`, and which configures no `DurableStorage:Node:ArchiveStorePrefix`, does not
start ([orleans §5.13](../../test/Durable.Storage/docs/spec/compositions/orleans.md#513-archive-log-store)).

## Deploying it

The sample keeps a deployment's shape on one machine. A deployment differs where the machine does:

- **Identity.** The sample reaches Azurite with its published development account key, which only the emulator accepts.
  A deployment authenticates with Microsoft Entra ID instead, never with an account key: build the clients with
  `DefaultAzureCredential` from `Azure.Identity`, for example
  `new TableServiceClient(new Uri("https://<account>.table.core.windows.net"), new DefaultAzureCredential())`, and
  likewise `BlobServiceClient` for the objects.
- **Storage.** The deployment provisions the MetaStore's table and the objects' container with the rest of its
  infrastructure, and grants the silos' identity data-plane roles on them alone; the sample creates them at each start.
- **Archive store.** The sample's cluster archives to the LSM. A cluster on archive logs sets
  `DurableStorage:Node:ArchiveStore` to `Logs` and registers its container with `Orleans.Durable.Storage.AzureStorage`'s
  `AddAzureArchiveLogs` on the silo's services, by the container's URI and a `TokenCredential` such as
  `DefaultAzureCredential` ([orleans §5.13](../../test/Durable.Storage/docs/spec/compositions/orleans.md#513-archive-log-store)).
- **Store prefix.** Several clusters may share one container, each under its own store prefix: set each deployment's
  own `ServiceId`, equal on every silo of the cluster and for its whole life, or
  `DurableStorage:Node:ArchiveStorePrefix`.
- **Cluster.** `DurableStorage:Node:Cluster` is set once for the cluster's life, equal on every silo. The AppHost draws
  a new one each run, which is a new, empty cluster.
- **Nodes.** Each silo's node root lies on a volume of its own which outlives the silo's pod, and its ordinal is its
  StatefulSet ordinal ([orleans §5.2](../../test/Durable.Storage/docs/spec/compositions/orleans.md#52-nodes-and-silos)).
- **Endpoints.** The sample's silos advertise the loopback address and a port each; a deployment's advertise each pod's
  own IP address.

`test/Durable.Storage/Orleans.Durable.Storage.Samples.Tests` runs the sample's configuration in a three-silo test cluster over
in-memory stores, kills the silo of a grain, and checks that the grain recovers its state on another silo and that the
killed silo's restart rejoins.
