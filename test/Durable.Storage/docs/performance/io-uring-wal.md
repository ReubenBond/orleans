# Local io_uring WAL measurements

## Result

On the measured laptop SSD, a durable append's latency is set by the SSD's durable-write path, about 1–2 ms. Clog adds
little at low load. Measured through `WriteAheadLog` with default options, as payload throughput (appends per second ×
record size; 1 MiB = 1,048,576 bytes), median of three process runs per row:

| Payload × clients | Original | Final | Change | Final p50 / p99 |
| --- | --- | --- | --- | --- |
| 128 B × 1 | 0.049 MiB/s (405/s) | 0.093 MiB/s (762/s) | 1.9× | 1.29 / 3.1 ms |
| 128 B × 64 | 2.09 MiB/s | 3.69 MiB/s | 1.8× | 1.51 / 7.2 ms |
| 128 B × 1,024 | — | 27.2 MiB/s (222k/s) | — | 3.74 / 16.9 ms |
| 4 KiB × 1 | 1.49 MiB/s | 2.63 MiB/s | 1.8× | 1.38 / 5.5 ms |
| 4 KiB × 64 | 25.9 MiB/s | 70.0 MiB/s | 2.7× | 2.32 / 17.0 ms |
| 4 KiB × 1,024 | 35.7 MiB/s, 4.2 s p99 | 308.5 MiB/s | 8.6× | 10.6 / 32.0 ms |
| 64 KiB × 1 | 13.8 MiB/s | 32.2 MiB/s | 2.3× | 1.62 / 6.3 ms |
| 64 KiB × 256 | 37.0 MiB/s, 4.35 s p99 | 525.7 MiB/s | 14.2× | 24.0 / 71.2 ms |

The original 1,024-client and 256-client rows were measured with data sync already enabled. Payload throughput counts
application bytes only. The device also writes padding in each group's last page, and the zero-fill.

1. **Data sync instead of FUA writes.** A direct `WRITEV` followed by `IORING_FSYNC_DATASYNC` costs half a FUA write
   here.
2. **Shared data syncs.** Each file has one data sync in flight. It covers every write whose transfer completed before
   it was submitted, so concurrent groups share flushes, and the adaptive group policy rises on fast-flush devices.
3. **A faster zero-fill.** Preallocation wrote 256 KiB synced chunks at 50 MiB/s, so a busy log waited seconds for
   its next file. It now writes 4 MiB buffered chunks, syncs every 16 MiB, and drops the synced pages from the cache.
4. **A 16 MiB default write window,** up from 1 MiB, so a durable flush carries up to 16 MiB.
5. **Adaptive preallocation.** While durable groups average at least 3 MiB, rolls create allocated files without a
   zero-fill; below 1 MiB the log returns to zero-filled files, rolling to one early.
6. **Correct large groups.** A group above 1,024 vectors failed with `EINVAL` and stopped the process. It is now split
   across successive submissions.

The engine also builds write vectors once and pools pin handles, which cut managed allocation per append 17–53%.

## Is 2–3 ms real?

Yes, for this device and the old `O_DSYNC` path. fio, outside Clog, on the same ext4 volume and a preallocated file,
measured 4 KiB `O_DIRECT | O_DSYNC` writes at queue depth 1 through io_uring at 2.24 ms p50, 6.19 ms p99, 439 writes/s.
A direct write without durability took 15.9 µs p50. A direct write plus `fdatasync` took about 1.2 ms: the write 31 µs
p50, the `fdatasync` 1.14 ms p50, 5.1 ms p99.

That is typical of client NVMe SSDs with a volatile write cache and no power-loss protection: a durable write must
flush the cache or use FUA, and millisecond flushes are common. Enterprise SSDs with power-loss protection can
acknowledge durable writes from protected cache in microseconds. These results should not be used to predict server
latency.

## Environment and method

Measured on 2026-11-17, on the ext4 root volume `/dev/nvme1n1p2` (`rw,relatime`), on a Samsung
MZVKW1T0HMLH-000H1, the OEM SM961 1 TB: PCIe 3.0 x4, MLC, firmware CXA74H0Q. Linux reports a write-back cache, FUA
support, 512-byte logical and physical blocks, and a 512-byte atomic write unit. CPU: Intel Core i7-11850H, 8 cores/16
logical processors; Linux 7.3.0-10-generic; .NET SDK 11.0.100, runtime 11.0.0, Release builds. This is a shared
development machine. No raw-device writes, cache drops, device reconfiguration, or whole-drive preconditioning were
performed. Kernel `perf` sampling was restricted (`perf_event_paranoid=2`), so kernel CPU is per-process system time.

`WalWriteWorkload` uses a real `WriteAheadLog` with default `WalOptions` unless stated: 4 KiB pages, adaptive 1–4
write groups, 256-page write window, 16 MiB admission budget, preallocation, and files growing from 4 MiB toward 256
MiB. Each client keeps one append outstanding. Each process warms up with up to 1,024 appends per client, then measures
a fixed number per client. Setup and close are excluded; packing, callbacks, admission, rolls, and file preparation are
included. Cells are medians of three process runs unless stated; there is no confidence interval.

## Durable primitive and kernel packing

These fio runs measure 8 seconds after 2 seconds of warmup on a newly prefilled 256 MiB file. "Records per flush"
models a write group: in-process rows write one padded 4 KiB page, and the kernel row appends 128-byte buffered records.

| Records per flush | Kernel: buffered + fdatasync | In-process: direct + fdatasync | In-process: `O_DIRECT \| O_DSYNC` |
| --- | --- | --- | --- |
| 1 | 816 records/s | 822 | 444 |
| 8 | 6,382 | 6,548 | 3,549 |
| 32 | 24,233 | 26,273 | 14,284 |

Kernel packing writes fewer application bytes, but each `fdatasync` still writes the whole cached page and pays the
same flush, so it was not faster. It also rewrites pages already holding acknowledged records. The device guarantees
atomicity only for 512-byte writes, so a crash can tear such a 4 KiB page, and the per-page CRC would make its
acknowledged records unreadable. Buffered I/O also reintroduces the writeback-error and page-cache read concerns of
[WAL §1](../spec/components/wal.md#design-rationale). The WAL keeps its in-process bounce buffer and write-once pages.

## Queue depth, group size, and the zero-fill limit

Queue depth raises throughput, not single-append latency. `O_DSYNC` writes overlap, giving 5,877 writes/s at 16 in
flight, each still about 2.5 ms. Device flushes do not overlap. With data sync, one write group in flight was best at
64 clients, and the adaptive policy selects it:

| Payload | Groups | Appends/s | p50 ms | p99 ms |
| --- | --- | --- | --- | --- |
| 128 B | adaptive | 23,831 | 2.04 | 8.87 |
| 128 B | 1 | 24,551 | 1.79 | 9.48 |
| 128 B | 2 | 19,185 | 2.92 | 8.54 |
| 128 B | 8 | 9,480 | 6.23 | 16.39 |
| 4 KiB | adaptive | 8,084 | 6.02 | 16.23 |
| 4 KiB | 1 | 8,418 | 6.08 | 16.37 |
| 4 KiB | 8 | 5,213 | 8.44 | 24.29 |

These runs predate the zero-fill change. The WAL hides flush latency by batching every waiting record into the next
group. Durable throughput then depends on bytes per flush; fio measured one write plus `fdatasync`:

| Bytes per flush | Durable MiB/s | `fdatasync` p50 |
| --- | --- | --- |
| 64 KiB | 37 | 1.42 ms |
| 256 KiB | 106 | 1.73 ms |
| 1 MiB | 296 | 2.11 ms |
| 4 MiB | 753 | 1.78 ms |

With large records, groups reached the 256-page window, about 1 MiB, but only 37 completed each second. The WAL metrics
showed the device idle between groups while rolls waited for the next preallocated file. Disabling preallocation
raised 64 KiB × 256 clients from 37 to 153 MiB/s but tripled single-record latency (1.30 → 3.97 ms p50), because each
extending write's `fdatasync` commits file metadata. The zero-fill was the actual limit:

| Zero-fill mode (fio, 512 MiB) | MiB/s |
| --- | --- |
| `O_SYNC`, 256 KiB writes (previous) | 50 |
| `O_SYNC`, 1 MiB writes | 169 |
| `O_SYNC`, 4 MiB writes (adopted) | 473 |
| Buffered 256 KiB, sync every 16 MiB | 923 |
| Direct 4 MiB, sync at end | 1,636 |

On this device a write-through write's sync dominates its cost: a 256 KiB write took 5 ms and a 4 MiB write 8.5 ms. The
larger chunk therefore barely lengthens the wait of a WAL write queued behind it, while keeping the existing
write-through crash model. End to end, 1 MiB chunks gave 106–108 MiB/s at the heavy loads above, and 4 MiB chunks
gave 192–194 MiB/s with p99 under 125 ms.

A larger write window trades tail latency for throughput:

| Load | Window | MiB/s | p50 ms | p99 ms |
| --- | --- | --- | --- | --- |
| 64 KiB × 256 | 256 (default) | 191 | 84 | 115 |
| 64 KiB × 256 | 1,024 | 282 | 36 | 333 |
| 4 KiB × 1,024 | 256 (default) | 195 | 21 | 33 |
| 4 KiB × 1,024 | 1,024 | 249 | 12 | 166 |

A 4,096-page window or a larger admission budget added nothing. The default stays at 256; a throughput-oriented
deployment may raise `WriteWindowPages`, which also widens the region replay treats as a possible crash tear.

## Device write ceiling

fio sustained 1 MiB direct sequential writes at queue depth 32 at 1,647 MiB/s (1.73 GB/s) for 60 seconds and 96.5
GiB. Every one-second sample lay between 1,621 and 1,673 MiB/s, so there was no cache cliff and no thermal throttling.
Durable throughput depends on bytes per flush, measured for 30 seconds each:

| Durable pattern | MiB/s | `fdatasync` p50 / p99 |
| --- | --- | --- |
| 1 MiB write, then `fdatasync` | 293 | 2.15 / 6.19 ms |
| 4 MiB write, then `fdatasync` | 756 | 1.76 / 6.00 ms |
| 16 MiB write, then `fdatasync` | 1,133 | 2.47 / 9.63 ms |
| 1 MiB writes at queue depth 8, `fdatasync` every 8 | 965 | 6.72 / 12.65 ms |

Clog's heavy-load ceiling, about 196 MiB/s of payload, writes about twice that to the device once the zero-fill is
counted. It also uses roughly 1 MiB groups, so it sits near the 1 MiB row. Larger groups, or overlapping the next
group's write with the current flush, are what would let it approach the device's durable ceiling.

## Approaching device saturation

This section's runs predate adaptive preallocation and the 16 MiB window; the next section adds them. The device
sustains 1,647 MiB/s of non-durable sequential writes, and 756 MiB/s when each 4 MiB write is followed by a flush. At
64 KiB × 256 clients the WAL wrote 398 MiB/s of payload. Its 4 MiB groups each take about 8 ms, and the
zero-fill writes another byte of zeros ahead of each logged byte. The device therefore takes about 800 MiB/s of writes,
at its durable ceiling for 4 MiB flushes.

Each change's contribution, at 4 KiB × 1,024 and 64 KiB × 256 clients:

| Configuration | 4 KiB × 1,024 | 64 KiB × 256 |
| --- | --- | --- |
| Data sync, synced 4 MiB zero-fill, 1 MiB window | 193 MiB/s, 33 ms p99 | 196 MiB/s, 124 ms p99 |
| Shared data syncs | 189 MiB/s | 190 MiB/s |
| Shared syncs, 4 MiB window | 243 MiB/s, 201 ms p99 | 272 MiB/s, 344 ms p99 |
| Shared syncs, buffered zero-fill synced every 16 MiB, 1 MiB window | 212 MiB/s, 39 ms p99 | 216 MiB/s, 117 ms p99 |
| All, with a 4 MiB window | 322 MiB/s, 26 ms p99 | 398 MiB/s, 59 ms p99 |

Sharing syncs adds nothing on its own here, because one group fills the 1 MiB window, so no second group can overlap
it. Synced zero-fill writes made large windows' tails worse by contending with the log's flushes. On this device one
group in flight remained best, with fixed concurrency of 1, 2, and 4 at 395–401 MiB/s at 64 KiB and falling at small
records. The adaptive policy keeps it at one. On a power-loss-protected device whose flush is nearly free, concurrent
groups complete about as fast as lone ones, and the same policy allows up to `MaxWriteGroupsInFlight`.

A zero-fill sync interval of 4 MiB collapsed 4 KiB × 1,024 to 255 MiB/s with a 179 ms p99, while 8 and 16 MiB gave
310–314 MiB/s with a 26–27 ms p99. At 128 B × 64, intervals differed within run-to-run noise (27.7k–29.9k appends/s).

The remaining gap is the zero-fill's second write. Without preallocation, a 16 MiB window reached 606 MiB/s at 64 KiB ×
256 with a 52 ms p99. But each small durable write into an extending or `fallocate`d file costs a metadata commit:
fio measured 3.6 ms per 4 KiB write plus `fdatasync` in either, against 1.14 ms in a zero-filled file. At 1 MiB writes
the difference vanished (2.07 versus 2.09 ms). Two designs would remove the second write and keep small appends fast:

- zeroing a file's unwritten extents only ahead of the log's cursor and only while the device has spare bandwidth, so
  a busy log writes large groups into unwritten extents;
- recycling released files, which needs each page to name its file, so replay rejects a recycled file's stale pages.

Both change the preparation design, and the second changes the format; neither is built. `FALLOC_FL_WRITE_ZEROES`
(Linux 6.17) would zero a file without data transfer, but this device does not support it
(`write_zeroes_unmap_max_hw_bytes` is 0).

## Adaptive preallocation

Zero-filled and allocated (`fallocate`, unwritten) files each win in one regime, measured as payload throughput or
latency, two runs per cell:

| Load | Window | Zero-filled | Allocated |
| --- | --- | --- | --- |
| 128 B × 1 | 1,024 | 1.27 ms p50 | 3.80 ms p50 |
| 128 B × 64 | 1,024 | 28,120/s | 11,433/s |
| 4 KiB × 1,024 (1.3–2 MiB groups) | 1,024 | 291 MiB/s | 279 MiB/s |
| 64 KiB × 256 | 1,024 | 394 MiB/s | 406 MiB/s |
| 64 KiB × 256 (4–5 MiB groups) | 4,096 | 371 MiB/s, 170 ms p99 | 574 MiB/s, 53 ms p99 |

A small durable write into an allocated file pays a metadata commit, and a zero-filled file costs a second write of
every byte. The log therefore chooses by the size of its durable groups, which measures how much each flush carries.
It keeps an average weighted by an eighth per group, and enters bulk mode at 3 MiB and leaves it below 1 MiB. In bulk
mode, rolls create allocated files at once. Leaving it in an allocated file, the log prepares a zero-filled file
unpaced and rolls to it once it is ready. A hill-climbing controller was considered: its signal, throughput, is
confounded by the offered load, and a file-sized step adapts slowly. The group size reflects the cost directly and
needs no exploration.

With the heuristic, the window's tail cost disappeared, so the default window became 4,096 pages (16 MiB):

| Load | Window 1,024 | Window 4,096 (default) |
| --- | --- | --- |
| 128 B × 1 | 0.09 MiB/s, 1.29 ms p50 | 0.09 MiB/s, 1.32 ms p50 |
| 128 B × 64 | 3.79 MiB/s | 3.54 MiB/s |
| 4 KiB × 64 | 67.0 MiB/s | 70.2 MiB/s |
| 4 KiB × 1,024 | 313.7 MiB/s, 29.5 ms p99 | 317.0 MiB/s, 31.3 ms p99 |
| 64 KiB × 1 | 30.7 MiB/s | 31.6 MiB/s |
| 64 KiB × 256 | 399.2 MiB/s, 53.2 ms p99 | 552.4 MiB/s, 68.4 ms p99 |

Light load recovers right after heavy load. Single 128-byte appends measured immediately after a 64 KiB × 256-client
burst ran at 1.28–1.30 ms p50 and 1.48–1.58 ms p95, the zero-filled latency, with p99 7.0–7.3 ms over the transition,
against about 5.4 ms at steady light load.

## Fixed buffers and CPU

The SSD is not slow; its durable flush is. fio with one io_uring thread on a 2 GiB prefilled file measured:

| Workload | Without fixed buffers | With fixed buffers |
| --- | --- | --- |
| 4 KiB random read, QD 128 | 409,720 IOPS, 2.44 CPU µs/I/O | 427,032 IOPS, 2.33 CPU µs/I/O |
| 4 KiB random read, QD 32 | 324,570 IOPS, 2.56 CPU µs/I/O | 325,944 IOPS, 2.37 CPU µs/I/O |
| 4 KiB direct write, QD 32 | 306,027 IOPS, 3.25 CPU µs/I/O | 316,348 IOPS, 3.12 CPU µs/I/O |
| 128 KiB sequential read, QD 8 | 3,159 MiB/s, 34.4% system CPU | 3,151 MiB/s, 29.1% system CPU |
| 128 KiB sequential write, QD 8 | 1,769 MiB/s | 1,717 MiB/s |

Fixed buffers help when one core is saturated, here by 3–5% on 4 KiB I/O, and save about 15% of kernel CPU on
128 KiB reads. They save roughly 0.1–0.2 µs per 4 KiB page.

Clog's final runs spent 7.8–17 µs of CPU per append under load. Only 7–30% of that was kernel time, and at most
2.5 cores were busy, at 212k 128-byte appends/s. A user-space profile of 4 KiB × 1,024 clients found about 30% of user
cycles spinning in the runtime: the WAL's admission `Lock` under contention, and thread-pool workers spinning before
they sleep. The rest was spread over async continuations, completion delivery, page copying, and CRC.

Fixed buffers would therefore save under 2% of Clog's CPU per append on this machine. They become worthwhile once
flushes are cheap, on a power-loss-protected SSD. At that point, user-space CPU must be cut first: per-record locking,
continuations, and wake-ups. After that, fixed buffers and registered files are the next kernel-side step, with
`IORING_OP_WRITEV_FIXED` (Linux 6.15) or contiguous slab runs for `WRITE_FIXED`. NVMe poll queues were unavailable
here (`io_poll=0`), and polling would spend CPU while waiting on millisecond flushes.

## Cloud deployment

No cloud provider documents whether its local NVMe has a volatile write cache, nor its `fdatasync` latency. Qualify each
VM type before relying on these recommendations. The kernel strips flushes and FUA for devices without a volatile
write cache ([kernel docs](https://www.kernel.org/doc/html/latest/block/writeback_cache_control.html)). On such
devices the WAL's data sync becomes cheap with no configuration change, and CPU per append becomes the limit. Never
disable barriers or flushes to imitate this.

| Cloud | Preferred WAL device | Remote-disk alternative | Avoid |
| --- | --- | --- | --- |
| Azure | Local NVMe on [Lsv4/Lasv4](https://learn.microsoft.com/en-us/azure/virtual-machines/sizes/storage-optimized/lsv4-series) (NVMe-only, Azure Boost) | [Premium SSD v2 or Ultra Disk](https://learn.microsoft.com/en-us/azure/virtual-machines/disks-types), 4K sectors, host caching none | The SCSI temp disk; Premium SSD v1 small sizes (burst credits); `ReadWrite` host caching; 512e sectors |
| AWS | Instance store on Nitro SSD types such as I7i, I7ie, I8g ([lifetime](https://docs.aws.amazon.com/AWSEC2/latest/UserGuide/instance-store-lifetime.html)) | [io2 Block Express](https://docs.aws.amazon.com/ebs/latest/userguide/provisioned-iops.html) | gp2/gp3 as the primary WAL |
| GCP | Local SSD over NVMe on Z3, C3, or N2 ([docs](https://docs.cloud.google.com/compute/docs/disks/local-ssd)) | [Hyperdisk Extreme or Balanced](https://docs.cloud.google.com/compute/docs/disks/hyperdisks) | C4, C4A, C4D, C4N, and H4D Titanium SSD, whose [known issue](https://docs.cloud.google.com/compute/docs/troubleshooting/known-issues) says writes completed just before power loss may be missing; Spot VMs |

Local NVMe has the lowest latency but is lost when the VM is stopped, deallocated, moved by maintenance, or retired.
That is tolerable only because a ledger acknowledges at `Q = 2` of `N = 3` replicas in different zones. A node which
returns with an empty disk must rejoin as a new replica and re-replicate. Remote disks survive VM loss, but cost about
0.5 ms or more per durable write and vary more, which hurts a serialized group commit.

AWS documents 4 KiB torn-write prevention on current I-family instance stores, and 16 KiB on Nitro SSD types and EBS,
for aligned direct writes ([docs](https://docs.aws.amazon.com/AWSEC2/latest/UserGuide/supported-block-sizes.html)). The
WAL's write-once 4 KiB pages fit within that guarantee, though they do not depend on it. AWS also recommends leaving
about 10% of an instance-store SSD unpartitioned.

Container runtimes' default seccomp profiles, and some distributions' `kernel.io_uring_disabled`, block io_uring
([LWN](https://lwn.net/Articles/943239/)). Storage selection is runtime policy, so the portable storage remains the
fallback. Check `sysctl kernel.io_uring_disabled` and the container profile before selecting `io-uring`.

To qualify a VM type:

1. Record `/sys/block/<dev>/queue/{write_cache,fua,logical_block_size,physical_block_size,max_hw_sectors_kb,nr_requests,io_poll}`,
   `atomic_write_*`, `nvme id-ctrl -H` (volatile write cache), and on AWS `nvme id-ns -v` (torn-write units).
2. Run the fio controls in [Reproducing](#reproducing): queue-depth-1 4 KiB writes with `fdatasync`, with `O_DSYNC`,
   and without durability, and durable bandwidth at 64 KiB to 4 MiB per flush.
3. Run `--wal-perf` at 1, 64, 256, and 1,024 clients with 128 B, 4 KiB, and 64 KiB payloads, and record kernel and user
   CPU per append. If `fdatasync` is microseconds, profile CPU before adding fixed buffers.
4. Soak for at least an hour, or one device capacity written, to see garbage collection, and repeat after filling the
   device.
5. Check crash recovery with a process kill, a VM reboot, and the provider's hard reset, and time a lost node's
   re-replication.

## Gist and paper comparison

The [provided recommendations](https://gist.github.com/ReubenBond/792478d513d4ceb5b64020e20d928588) match Clog's
existing aligned page packing, padding on flush, page write-once rule, buffer lifetime through completion, and
single-issuer engine. Their direct-I/O layout is durable only with a durable completion: `O_DIRECT` alone bypasses
the page cache but does not flush a volatile device cache. Clog must also not adopt a global contiguous acknowledgement
LSN, since independent records may complete out of order (WAL-I1, WAL-I8). Registered buffers are measured above.

[Jasny et al., *io_uring for High-Performance DBMSs*](https://github.com/ReubenBond/research-papers/blob/main/io_uring_for_High-Performance_DBMSs_When_and_How_to_Use_It/io_uring_for_High-Performance_DBMSs_When_and_How_to_Use_It.md)
§3.6 reports that consumer SSDs are dominated by millisecond flushes, and that `O_SYNC` writes can be more than twice as
slow as write plus `fsync`; this device reproduced both. Its registered-buffer gains appear when I/O saturates a core,
as in the fio rows above.

[Lee et al., *How to Write to SSDs*](https://github.com/ReubenBond/research-papers/blob/main/How_to_Write_to_SSDs/How_to_Write_to_SSDs.md)
§§2–3 argue for aligned, packed, out-of-place writes and end-to-end write amplification. The WAL writes packed pages
sequentially and once. Its padded partial pages and its zero-fill are deliberate costs, for crash classification and
for avoiding metadata commits in the write path.

## Reproducing

Initialize with `just sync`. From PowerShell at the repository root:

```powershell
. ./eng/dotnet-env.ps1
dotnet build benchmarks/Orleans.Durable.Storage.Benchmarks/Orleans.Durable.Storage.Benchmarks.csproj -c Release
dotnet run --project benchmarks/Orleans.Durable.Storage.Benchmarks -c Release --no-build -- --wal-perf io-uring 128 64 1000
dotnet run --project benchmarks/Orleans.Durable.Storage.Benchmarks -c Release --no-build -- --wal-perf io-uring 65536 256 40 window=1024
Push-Location benchmarks/Orleans.Durable.Storage.Benchmarks
dotnet run -c Release --no-build -- --filter '*WalWriteBenchmarks*' --job Dry
Pop-Location
```

`--wal-perf <io-uring|portable> <payload-bytes> <clients> <appends-per-client>` emits JSON with throughput,
p50/p95/p99, user and kernel CPU, managed bytes per append, and the WAL's write-group metrics: groups per second, mean
pages, records, and duration per group, and mean concurrency. Optional arguments are a fixed write-group count,
`prealloc=false`, `window=<pages>`, `budgetMiB=<n>`, `fileMiB=<n>`, `maxGroups=<n>`, and
`prewarm=<bytes>x<clients>x<per-client>`, which runs a heavier phase before the measured one.

Run BenchmarkDotNet from its project directory so project discovery does not walk active test temporary directories. On
long worktree paths, set `TESTINGPLATFORM_PIPE_DIRECTORY=/tmp` for `dotnet test`. The debug-assertion canary tests start
a child host without that variable, so they also need a short `TMPDIR`; the WAL itself refuses tmpfs.

The fio controls used prefilled files under `artifacts/tmp`, for example:

```bash
fio --name=dsync --filename=artifacts/tmp/fio.dat --size=256M --bs=4k --rw=write --direct=1 \
  --ioengine=io_uring --iodepth=1 --sync=dsync --time_based --runtime=8 --ramp_time=2
fio --name=fdatasync --filename=artifacts/tmp/fio.dat --size=256M --bs=4k --rw=write --direct=1 \
  --ioengine=psync --fdatasync=1 --time_based --runtime=8 --ramp_time=2
fio --name=fixed --filename=artifacts/tmp/fio.dat --size=2G --rw=randread --bs=4k --direct=1 \
  --ioengine=io_uring --iodepth=128 --registerfiles=1 --fixedbufs=1 --time_based --runtime=5
```

Raw local JSON is under `artifacts/wal-perf/` and is not committed.
