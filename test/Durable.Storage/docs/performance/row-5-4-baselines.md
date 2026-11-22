# Row 5.4 performance baselines

## Environment and method

Measured sequentially on 2026-11-20 on a quiet reference machine with no concurrent builds, tests, or background
scanners:

- **Host:** Windows 11 (`10.0.26300.9911`, Hyper-V), Intel Xeon Platinum 8370C (32 logical cores), 128 GiB RAM, single
  local NTFS NVMe volume.
- **Runtime:** .NET 11 (`net11.0`, Release builds), pinned Orleans nightly
  ([build and packaging §2](../spec/build-packaging.md#2-source-and-dependency-policy)).
- **Suites:**
  - `--wal-perf portable`: `WriteAheadLog` over `PortableWalStorage` (`FILE_FLAG_WRITE_THROUGH`), 128 B / 4 KiB / 64 KiB
    × 1 / 16 / 64 clients.
  - BenchmarkDotNet (`--job Short`, `[MemoryDiagnoser]`): all engine, codec, WAL/LedgerStore, ObjectLedger, Journal, and
    LSM suites.
  - `--layer-bench` (`DOTNET_gcServer=1`, `GCSettings.IsServerGC == true`): direct single-node Clog layer decomposition
    across all 14 layers — L0 (`WriteAheadLog`), L1 (`LedgerStore`, 1 / 16 / 64 ledgers), L2 (`Ledger` quorum
    `N = 3, Q = 2`, in-memory and WAL-backed, 1 / 16 ledgers), and L3 (`JournalStore`, in-memory and WAL-backed, 1 hot
    journal, 1 ledger with many journals, and 16 ledgers with many journals). Includes both **closed-loop**
    fixed-concurrency runs (1, 16, 64 writers) and **coordinated-omission-free open-loop** Poisson sweeps at 10%, 50%,
    75%, and 90% of each layer's closed-loop saturation rate (`max(c=1, c=16, c=64)`), recording both total latency
    (`t_done − t_scheduled`), service time (`t_done − t_dispatch`), and load-shed drops at the 1,024-in-flight cap.
    Open-loop CPU per op includes the dedicated pacer thread's spin/sleep work.
  - `--orleans-bench` (`DOTNET_gcServer=1`, one silo per OS process, real loopback TCP sockets via
    `UseDevelopmentClustering`, shared `FileSystemMetaStore` and `FileSystemObjectStore`, per-silo `PortableWalStorage`
    roots with 1 GiB files and default limits): 6 scenarios, 3 runs each (mean and min–max range), with exact
    `GC.GetTotalAllocatedBytes(precise: true)` and continuous `PeakWorkingSetMiB` sampling.

All throughput figures use binary mebibytes (1 MiB = 1,048,576 bytes).

## Layer-by-layer overhead decomposition (L0–L4)

All layers below ran on the same machine and NTFS volume under Server GC (`DOTNET_gcServer=1`): L0 and L1 were
measured after replacing `LedgerStore._stripes` with exact per-replica in-flight slot tracking (#462), L2 was
re-measured after optimizing `LedgerQuorum` and `LedgerWriter`, L3 was re-measured after optimizing the L3 Journal
write hot path (`JournalStore`, `LedgerAdmission`, `JournalWarmState`, and `JournalEntryRecord`), and L4 was
re-measured after optimizing the Orleans replica transport, wire buffers, host admission/dispatch, and forwarded-write
hot paths. Comparing matched 4 KiB payloads separates disk fsync cost, WAL/LedgerStore framing, quorum/batching CPU,
journal admission/fold, and Orleans TCP transport overhead.

### Closed-loop throughput and latency (4 KiB payload)

| Layer | Config | c=1 ops/s | c=1 p50 (µs) | c=16 ops/s | c=16 p50 (µs) | c=64 ops/s | c=64 MiB/s | c=64 p50 (µs) | c=64 p99 (µs) | Alloc (B/op) | GC pause |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| L0 WAL (`PortableWalStorage`) | 1 stream | 2,409 | 345.2 | 20,883 | 607.1 | 49,633 | 193.9 | 1,104.9 | 6,840.6 | 1,187 | 1.27% |
| L1 `LedgerStore` | 1 ledger | 2,513 | 351.3 | 17,911 | 810.0 | 37,662 | 147.1 | 1,193.4 | 7,725.0 | 1,850 | 0.64% |
| L1 `LedgerStore` | 16 ledgers | 2,322 | 363.0 | 17,902 | 779.4 | 45,351 | 177.2 | 1,192.0 | 6,645.7 | 1,848 | 0.63% |
| L1 `LedgerStore` | 64 ledgers | 2,362 | 366.5 | 18,195 | 816.4 | 41,975 | 164.0 | 1,163.5 | 7,064.7 | 1,871 | 0.52% |
| L2 `Ledger` quorum (in-mem, N=3 Q=2) | 1 ledger | 506,739 | 1.7 | 184,065 | 78.0 | 181,760 | 710.0 | 332.5 | 1,176.9 | 1,247 | 2.72% |
| L2 `Ledger` quorum (in-mem, N=3 Q=2) | 16 ledgers | 417,297 | 2.1 | 2,090,934 | 4.1 | 2,765,737 | 10,803.7 | 5.6 | 151.4 | 863 | 3.21% |
| L2 `Ledger` quorum (3 WALs, N=3 Q=2) | 1 ledger | 1,277 | 675.4 | 9,331 | 1,417.8 | 23,749 | 92.8 | 1,880.9 | 18,811.0 | 7,393 | 1.01% |
| L2 `Ledger` quorum (3 WALs, N=3 Q=2) | 16 ledgers | 1,332 | 653.1 | 9,794 | 1,378.9 | 23,186 | 90.6 | 1,808.1 | 20,733.4 | 10,308 | 3.54% |
| L3 `JournalStore` (in-mem, N=3 Q=2) | 1L hot key | 314,393 | 2.7 | 147,594 | 100.2 | 146,081 | 570.6 | 415.2 | 1,045.0 | 1,846 | 2.46% |
| L3 `JournalStore` (in-mem, N=3 Q=2) | 1L many keys | 311,460 | 2.7 | 307,002 | 26.4 | 378,801 | 1,479.7 | 95.2 | 656.5 | 1,264 | 3.12% |
| L3 `JournalStore` (in-mem, N=3 Q=2) | 16L many keys | 305,612 | 2.9 | 621,082 | 3.9 | 595,657 | 2,326.8 | 3.9 | 34.2 | 1,366 | 1.25% |
| L3 `JournalStore` (3 WALs, N=3 Q=2) | 1L hot key | 1,250 | 696.3 | 1,296 | 11,169.7 | 1,253 | 4.9 | 47,032.0 | 101,948.5 | 11,716 | 0.00% |
| L3 `JournalStore` (3 WALs, N=3 Q=2) | 1L many keys | 1,289 | 686.7 | 9,715 | 1,381.8 | 18,011 | 70.4 | 1,763.7 | 21,254.2 | 2,490 | 0.10% |
| L3 `JournalStore` (3 WALs, N=3 Q=2) | 16L many keys | 1,234 | 698.1 | 9,932 | 1,399.9 | 23,395 | 91.4 | 1,799.9 | 27,437.3 | 10,614 | 1.63% |
| L4 Orleans TCP (3 silos, 16L, N=3 Q=2) | `replica-fanout` | — | — | — | — | 14,208 | 55.5 | 2,380.0 | 7,580.0 | 44,974 | 1.62% |

Notes:

- L4's `replica-fanout` row is 3 silo processes × 16 ledgers (48 concurrent ledger writers, `K = 1, Q = 3` awaiting all
  3 replicas per write, `14,208 ops/s` aggregate at 4 KiB = `55.50 MiB/s`, `0` backpressure). Single-hop L4 local vs
  remote latency at 1 KiB, 64 KiB, and 1 MiB is in [orleans §5.12](../spec/compositions/orleans.md#512-replica-transport)
  (`+534 µs` remote TCP write hop at 1 KiB).
- `L3-JournalWal-1L-Hot` intentionally serializes all 64 concurrent writers on a single journal key under a per-key
  lock so each write pays its own quorum WAL fsync (~696 µs → ~1,253 ops/s ceiling), whereas `L3-JournalWal-1L-Many`
  and `L3-JournalWal-16L-Many` allow `LedgerAdmission` to coalesce concurrent writes across distinct keys into shared
  quorum batches (18,011 and 23,395 ops/s, 14–19× higher).
- Peak closed-loop throughput across other payload sizes (`--layer-bench`):
  - **128 B (c=64 unless noted):** L0 **79,640 ops/s** (9.7 MiB/s), L1-1L **73,868 ops/s**, L1-16L **75,563 ops/s**,
    L1-64L **76,871 ops/s**; L2-Mem-16L **2,841,773 ops/s**, L2-Wal-1L **28,987 ops/s**, L2-Wal-16L **38,459 ops/s**;
    L3-Mem-16L-Many **693,455 ops/s** (c=16; **625,280 ops/s** c=64), L3-Wal-1L-Hot **1,326 ops/s** (c=16),
    L3-Wal-1L-Many **36,289 ops/s**, L3-Wal-16L-Many **37,120 ops/s**.
  - **64 KiB (c=64 unless noted):** L0 **227.1 MiB/s** (3,633 ops/s, c=16), L1-1L **214.9 MiB/s** (c=16), L1-16L
    **247.8 MiB/s** (c=16), L1-64L **226.0 MiB/s** (c=16); L2-Mem-16L **43,204 MiB/s**, L2-Wal-1L **110.1 MiB/s**
    (c=16), L2-Wal-16L **132.7 MiB/s**; L3-Mem-16L-Many **23,576 MiB/s** (377,212 ops/s), L3-Wal-1L-Hot **67.1 MiB/s**,
    L3-Wal-1L-Many **104.1 MiB/s** (c=16), L3-Wal-16L-Many **124.6 MiB/s**.
  - **1 MiB (peak concurrency):** L0 **252.6 MiB/s** (c=1), L1-1L **324.8 MiB/s** (c=64), L1-16L **316.7 MiB/s**
    (c=64), L2-Mem-16L **54,706 MiB/s** (c=64, 53.4 GiB/s), L2-Wal-1L **118.4 MiB/s** (c=1), L2-Wal-16L **140.5 MiB/s**
    (c=16), L3-Mem-16L-Many **53,471 MiB/s** (c=64, 52.2 GiB/s), L3-Wal-1L-Hot **125.0 MiB/s** (c=16), L3-Wal-1L-Many
    **115.2 MiB/s** (c=1), L3-Wal-16L-Many **111.6 MiB/s** (c=1).

### Coordinated-omission-free open-loop sweeps (4 KiB payload)

Each layer's open-loop Poisson arrival sweep runs at 10%, 50%, 75%, and 90% of its measured closed-loop saturation rate
(`max(c=1, c=16, c=64)`). Latency is `t_done − t_scheduled` (no coordinated omission); service time is
`t_done − t_dispatch`. Any rate that hits the 1,024-in-flight concurrency cap records non-zero `Drops` and is flagged
**`OVERLOADED`** rather than treated as healthy latency.

| Layer | 10% ops/s (p50 / p99 µs) | 50% ops/s (p50 / p99 µs) | 75% ops/s (p50 / p99 µs) | 90% ops/s (p50 / p99 µs) | 90% svc p50 (µs) | Overloaded rates (drops) |
| --- | --- | --- | --- | --- | ---: | --- |
| L0 WAL (1 stream) | 4,950 (636 / 7,027) | 24,795 (741 / 8,785) | 37,090 (944 / 10,112) | 43,520 (1,008 / 155,468)† | 998 | 90% (4,946) |
| L1 `LedgerStore` (1L) | 3,751 (624 / 6,291) | 18,768 (722 / 12,153) | 27,913 (853 / 134,815)† | 33,709 (1,176 / 30,467) | 1,161 | 75% (928) |
| L1 `LedgerStore` (16L) | 4,511 (651 / 5,730) | 22,649 (795 / 10,081) | 33,859 (1,191 / 23,958) | 40,151 (1,063 / 106,534)† | 1,051 | 90% (2,581) |
| L1 `LedgerStore` (64L) | 4,182 (621 / 3,132) | 20,929 (780 / 114,987) | 31,339 (964 / 12,143) | 37,647 (984 / 16,865) | 971 | None (0) |
| L2 `Ledger` in-mem (1L) | 50,509 (6.0 / 102) | 230,025 (17,227 / 23,420)† | 235,865 (16,867 / 23,101)† | 237,164 (16,825 / 22,018)† | 16,819 | 50% (108,642), 75% (712,935), 90% (1,089,152) |
| L2 `Ledger` in-mem (16L) | 275,723 (5.4 / 1,968) | 1,372,223 (8.7 / 20,137)† | 1,675,455 (314,963 / 698,780)† | 1,686,359 (1,009,242 / 1,764,483)† | 4.8 | 50% (27,360), 75% (772,540), 90% (976,161) |
| L2 `Ledger` WAL (1L) | 2,384 (1,225 / 20,266) | 11,778 (1,500 / 35,668) | 17,757 (1,622 / 42,701) | 19,230 (9,496 / 631,446)† | 9,330 | 90% (9,858) |
| L2 `Ledger` WAL (16L) | 2,331 (1,193 / 13,031) | 11,509 (1,468 / 20,788) | 17,307 (1,460 / 19,625) | 20,677 (1,528 / 65,291) | 1,515 | None (0) |
| L3 `JournalStore` in-mem (1L-Hot) | 31,239 (10.1 / 211) | 146,662 (27,358 / 31,124)† | 143,412 (28,044 / 33,533)† | 145,036 (27,752 / 32,379)† | 27,749 | 50% (45,868), 75% (454,471), 90% (686,253) |
| L3 `JournalStore` in-mem (1L-Many) | 37,643 (9.7 / 1,740) | 188,260 (21.3 / 50,744) | 271,572 (336 / 86,754)† | 296,583 (978 / 115,029)† | 963 | 75% (43,636), 90% (202,878) |
| L3 `JournalStore` in-mem (16L-Many) | 61,880 (8.6 / 1,662) | 297,943 (11.5 / 104,701)† | 361,703 (15.9 / 138,623)† | 352,055 (19.7 / 158,165)† | 6.5 | 50% (37,311), 75% (471,871), 90% (990,864) |
| L3 `JournalStore` WAL (1L-Hot) | 125 (877 / 3,278) | 644 (899 / 4,787) | 953 (1,315 / 9,120) | 1,146 (4,726 / 50,697) | 4,685 | None (0) |
| L3 `JournalStore` WAL (1L-Many) | 1,826 (1,083 / 7,497) | 8,959 (1,406 / 19,097) | 13,458 (1,800 / 179,873) | 16,130 (1,515 / 23,714) | 1,501 | None (0) |
| L3 `JournalStore` WAL (16L-Many) | 2,351 (1,173 / 14,157) | 11,623 (1,437 / 19,056) | 17,504 (1,918 / 159,074) | 20,915 (1,528 / 18,872) | 1,516 | None (0) |

† **OVERLOADED:** the arrival burst exceeded the 1,024-in-flight cap and shed load (`Drops > 0`). On high-rate
in-memory layers, `L2-QuorumMem-1L` and `L3-JournalMem-1L-Hot` peak at `c=1` (`506.7k` and `314.4k ops/s`) and degrade
under concurrency (`181.8k` and `146.1k ops/s` at `c=64` due to single-ledger/single-key serialization), while
`L2-QuorumMem-16L` and `L3-JournalMem-16L-Many` saturate the open-loop harness's single dispatch pacer thread even
though service p50 stays at `4.8 µs` and `6.5 µs`.

### Per-layer delta and bottleneck findings

1. **L0 → L1 overhead is minimal (`+6–21 µs` c=1 p50) and scales flatly across 1, 16, and 64 ledgers:** Framing,
   `SlotIndex`, and `LedgerStore` bookkeeping keep c=1 p50 within +6–21 µs of the raw WAL (345 vs 351–367 µs) and
   reach **37.7k–45.4k ops/s** (147–177 MiB/s at 4 KiB) and **73.9k–76.9k ops/s** at 128 B across 1, 16, and 64
   ledgers, with flat open-loop 90% p50 around **0.98–1.18 ms**.
2. **Resolved bottleneck — `LedgerStore._stripes` replaced by exact per-replica in-flight slot tracking (#462):**
   The initial Row 5.4 baseline uncovered severe multi-ledger lock contention from `LedgerStore`'s 1,024-entry
   `SemaphoreSlim` stripe table hashing `key & 1023` (dropping lower slot bits alone across ledgers). At 4 KiB c=64,
   `L1-Store-16L` fell to 9,480 ops/s (open-loop 90% p50 5.70 ms), `L1-Store-64L` fell to 4,583 ops/s (open-loop 90%
   p50 13.12 ms), and `L2-QuorumWal-16L` fell to 5,013 ops/s (open-loop 90% p50 11.51 ms). Removing `_stripes` and
   tracking in-flight slots directly on each `LiveReplica` eliminated cross-ledger collisions completely:
   - `L1-Store-16L`: 4 KiB c=64 **9,480 → 45,351 ops/s** (**4.78×**), p50 **6,260 → 1,192 µs**; 128 B c=64
     **10,420 → 75,563 ops/s** (**7.25×**).
   - `L1-Store-64L`: 4 KiB c=64 **4,583 → 41,975 ops/s** (**9.16×**), open-loop 90% **4,091 → 37,647 ops/s**
     (p50 **13,123 → 984 µs**, 0 drops); 128 B c=64 **5,285 → 76,871 ops/s** (**14.55×**).
   - `L2-QuorumWal-16L`: 4 KiB c=64 **5,013 → 18,733 ops/s** (**3.74×**), open-loop 90% **4,472 → 16,772 ops/s**
     (p50 **11,514 → 1,824 µs**, 0 drops); 128 B c=64 **5,670 → 36,480 ops/s** (**6.43×**).
   - `L4` (`replica-fanout`): 4 KiB ingress rose **47.71 → 51.33 MiB/s** with p99 **9.61 → 6.72 ms** (**−30%**);
     at NVMe saturation, p99 dropped **1,173.4 → 330.9 ms** (**3.5×**) at 600 KiB and **1,888.6 → 562.5 ms**
     (**3.4×**) at 1 MiB.
3. **Resolved bottleneck — `LedgerQuorum` and `LedgerWriter` write hot path:** Post-#462 profiling identified four
   L2 write-path overheads: (a) a full payload copy (`ArcBufferWriter` + `CopyTo`) inside `LedgerQuorum.WriteAsync`;
   (b) per-write `CallScope`, `List<Task>`, `WritePinnedAsync` async boxes, `Task.WhenAny`, and `_stragglers`
   `ConcurrentDictionary` + `ContinueWith` churn on every `Q < N` write; (c) Polly `ExecuteAsync` wrapping attempt 0 in
   `LedgerWriter.WriteUntilDecidedAsync`; and (d) unnecessary async state-machine allocations in
   `LedgerQuorum.WriteAdmittedAsync`, `LedgerWriter.CreateBatchAsync`, and `LedgerWriter.WaitAsync`. Replacing the copy
   with a single zero-copy `payload.Retain()` lease shared across replicas, introducing a lean `WriteCoordinator` with
   atomic straggler tracking and a 1-slot cached write token, bypassing Polly on attempt 0, and removing hot-path async
   boxes delivered major gains across L2 and L3:
   - `L2-QuorumMem-16L`: 4 KiB c=64 **294,518 → 2,765,737 ops/s** (**9.39×**, `10.55 GiB/s`), p99 **4,539.8 → 151.4 µs**
     (**30× lower**), alloc **2,448 → 863 B/op** (**−65%**); 128 B c=64 **2,841,773 ops/s**; 1 MiB c=64
     **34,761 → 54,706 MiB/s** (**1.57×**).
   - `L2-QuorumMem-1L`: 4 KiB c=1 **259,465 → 506,739 ops/s** (**1.95×**, p50 **3.1 → 1.7 µs**); c=64
     **133,372 → 181,760 ops/s** (**1.36×**), alloc **2,862 → 1,247 B/op** (**−56%**).
   - `L2-QuorumWal-1L` / `16L`: 4 KiB c=64 **17,785 → 23,749 ops/s** (**+34%**, p99 **30.9 → 18.8 ms**, alloc
     **10,837 → 7,393 B/op**, **−32%**) and **18,733 → 23,186 ops/s** (**+24%**, open-loop 90% **16,772 → 20,677 ops/s**,
     p99 **188.4 → 65.3 ms**, 0 drops).
   - `L3-JournalMem-1L-Hot` / `16L-Many`: 4 KiB c=1 **69,616 → 96,171 ops/s** (**+38%**, p50 **12.0 → 8.9 µs**);
     1 MiB `16L-Many` c=64 **30,854 → 53,194 MiB/s** (**1.72×**); `L3-JournalWal-1L-Many` 4 KiB c=64
     **19,726 → 26,251 ops/s** (**+33%**, open-loop 90% **17,631 → 23,501 ops/s**, 0 drops).
   - `JournalWriteBenchmarks` (`--job Short`): in-memory `Append1KiB` **16.10 → 12.35 µs** (`4.05 → 2.50 KB`),
     `Replace1MiB` **389.94 → 299.29 µs** (**−23%**); real WAL (×3) `Append1KiB` alloc **15.09 → 11.87 KB**
     (**−21%**), `Replace1MiB` **14.14 → 8.56 ms** (**−39%**).
4. **Resolved bottleneck — L3 `JournalStore`, `ArchiveReader`, `LsmArchiveStore`, and `BlockCache` read hot path:**
   Profiling the absent, warm, and cold read paths exposed per-read allocations and redundant syscalls across four
   layers: (a) unconditional linked `CancellationTokenSource` allocation in `JournalStore.EnterHistoryReadAsync` even
   when `_historyReads` is uncontended; (b) `ArchiveReader` unconditionally creating linked CTS instances,
   `Dictionary<LedgerId, Slot>`, `SemaphoreSlim`, `List<LedgerScan>` / `LedgerScan` buffers, and Polly `ExecuteAsync`
   wrappers on attempt 0 for 0/1-ledger and synchronous-store reads; (c) `LsmArchiveStore.ReadAsync` / `ReadRowsAsync`
   and `RunObjectReader.ReadBlockSpanAsync` allocating `Task[]`, candidate `List`s, `ArcBuffer?[]`, and closure
   delegates even when every section tail is cached or only one candidate/block matches; and (d)
   `BlockCache.ReadVerifiedPayloadAsync` issuing two `file.ReadAsync` syscalls plus `new byte[4]` per block-cache hit
   (payload + trailing CRC). Adding `CallBudget.TryEnter`, zero-allocation 0/1-open-ledger and single-slot indexed
   fast paths in `ArchiveReader`, synchronous cached-tail and 1-candidate/1-block fast paths in `LsmArchiveStore` /
   `RunObjectReader`, and single-read trailing-CRC verification in `BlockCache` delivered consistent Server-GC
   (`DOTNET_gcServer=1`, `--job Short`) improvements across all three suites:
   - `WarmRead` (`1 KiB` / `64 KiB`): **7.12 → 5.03 µs** (**−29%**, `1.42×`) and **18.80 → 16.86 µs** (**−10%**),
     alloc **7.63 → 5.94 KB** (**−22%**).
   - `AbsentRead` (`100` / `10,000` drained journals, `0.00` GETs/read): **24.16 → 19.54 µs** (**−19%**) and
     **26.30 → 21.36 µs** (**−19%**), alloc **7.20 → 5.78 KB** (**−20%**) and **7.18 → 5.89 KB** (**−18%**).
   - `ColdRead` (4 KiB drained journal): `Uncached` **363.9 → 344.2 µs** (**−5%**, alloc **17.01 → 14.03 KB**,
     **−18%**); `SectionMetadataCache` **196.2 → 168.8 µs** (**−14%**, alloc **10.13 → 6.97 KB**, **−31%**);
     `MetadataAndBlockCache` **123.8 → 68.23 µs** (**−45%**, `1.81×`, alloc **9.70 → 6.24 KB**, **−36%**).
5. **Resolved bottleneck — L3 Journal write hot path and LSM segment drain path:** Profiling the L3 write hot path
   exposed per-write and per-batch allocations plus redundant thread-pool handoffs across `JournalStore`,
   `LedgerAdmission`, `JournalWarmState`, and `JournalEntryRecord`: (a) `StartDetached` queuing a `Task.Run` hop and
   waking the `_proposals` channel even on uncontended synchronous batches; (b) separate `Pending` +
   `TaskCompletionSource<JournalWriteOutcome>` objects, `Pending[]`, `List<Pending>` (`After.Answered`), `byte[]`
   record heads, and `Action<WarmEntry>` eviction closures; and (c) per-write lock entry in `JournalStore.EnsureServed`
   and async state-machine boxing in `WriteAsync`. Running the first batch inline under the captured owner
   `ExecutionContext` (preserving [conventions §12](../spec/conventions.md#12-shared-workers-and-execution-context) and
   Clockwork determinism), inheriting `Pending` from `TaskCompletionSource<JournalWriteOutcome>` with inline
   single-slot `PendingQueue`/`PendingBatch`/`After` storage, adding a borrowed-payload fast path and lock-free
   `_servedSnapshot` lookup, and eliminating `JournalEntryRecord`'s 10-byte head allocation cut L3 CPU overhead by
   ~7× and allocations by >50%:
   - `L3-JournalMem-1L-Hot` (4 KiB): c=1 **99,086 → 314,393 ops/s** (**3.17×**, p50 **8.5 → 2.7 µs**), c=64
     **92,711 → 146,081 ops/s** (**+58%**), alloc **2,798 → 1,846 B/op** (**−34%**).
   - `L3-JournalMem-1L-Many` (4 KiB): c=1 **97,381 → 311,460 ops/s** (**3.20×**, p50 **8.8 → 2.7 µs**), c=64
     **232,181 → 378,801 ops/s** (**+63%**, p99 **1,062.6 → 656.5 µs**), alloc **2,021 → 1,264 B/op** (**−37%**).
   - `L3-JournalMem-16L-Many` (4 KiB): c=1 **94,363 → 305,612 ops/s** (**3.24×**, p50 **9.1 → 2.9 µs**), c=16
     **277,107 → 621,082 ops/s** (**2.24×**, p50 **10.4 → 3.9 µs**), c=64 **413,667 → 595,657 ops/s** (**+44%**,
     **2,326.8 MiB/s**, p99 **125.9 → 34.2 µs**, **3.7× lower**), alloc **2,748 → 1,366 B/op** (**−50%**); 64 KiB
     c=64 **19,570 → 23,576 MiB/s** (**+20%**).
   - `JournalWriteBenchmarks` (`--job Short`, Server GC): in-memory `Append1KiB` **14.97 → 4.29 µs** (**3.49×**,
     alloc **2.51 → 1.18 KB**, **−53%**), `Append64KiB` **41.12 → 24.92 µs** (**1.65×**, alloc **2.57 → 1.18 KB**,
     **−54%**), `Replace1MiB` **299.30 → 243.60 µs** (**−19%**, alloc **2.73 → 1.33 KB**, **−51%**); real WAL (×3)
     `Append1KiB` **750.5 → 693.1 µs** (alloc **11.90 → 10.95 KB**).
   - `LsmDrainBenchmarks` (`--job Short`, Server GC, 10,000 × 420 B rows = 4.2 MiB segment): pooling
     `LedgerSegmentView`'s open-addressing key table and `DrainWorker.Segment`'s per-batch/key arrays, using O(1)
     `ArcBuffer.Slice` cursors over `LedgerRead.Data.Batch`, replacing per-slot `Task<LedgerRead>` allocations in
     `LedgerRangeReads` with a struct `PendingRead`, pooling Bloom hashes (`RunBloomFilter.Create(ReadOnlySpan<ulong>)`)
     and adding a span-based `RunObjectFormat.FieldReader` / `ObjectStoreChecksums` fast path reduced allocations by
     **14–66%** and eliminated distinct-journal scaling allocations (**+3.12 → +0.05 MB**, **−98%**):
     - `100 journals`: in-memory **35.51 → 32.44 ms** (**−9%**, alloc **7.65 → 6.58 MB**, **−14%**); file-system
       **50.67 → 47.30 ms** (**−7%**, alloc **3.24 → 2.14 MB**, **−34%**).
     - `10,000 journals`: in-memory **43.04 → 40.19 ms** (**−7%**, alloc **10.77 → 6.65 MB**, **−38%**); file-system
       **57.81 → 57.19 ms** (alloc **6.36 → 2.19 MB**, **−66%**).
6. **Resolved bottleneck — L4 Orleans replica transport, wire buffers, host dispatch, and forwarded writes:**
   Profiling the L4 hot paths exposed per-RPC allocations and redundant copies/handoffs across client transport, grain
   hosts, and forwarded writes: (a) per-call `ConcurrentDictionary` lane lookups, closures/`Func` delegates, duplicate
   `LedgerReplicaWriteRequest` structs, `CallBudget.Grant` boxes, and `Lazy<OrleansBuffer>` + closure allocations in
   `OrleansLedgerReplicaTransport`; (b) per-RPC `LedgerReplicaOptions` + `LedgerStoreReplica` handle creation,
   `LedgerReplicaHostAdmission` heap allocations on writes/tail reads, stacked grain/host async state machines, and
   unconditional grain-scheduler hops in `LedgerReplicaHost`/`LedgerStoreGrain`/`JournalNodeHost`/`JournalStoreGrain`;
   (c) redundant per-segment writer loop overhead in `WireBuffers` and an intermediate Clog `ArcBuffer` copy in
   `JournalRouter` before copying into Orleans' buffer; and (d) per-call `CallBudget.Grant` allocations in
   `JournalRouter` and `ForwardedReads`. Caching per-replica lanes, adding value-type `CallBudget.Lease` /
   `EnterLeaseAsync`, replacing `Lazy<OrleansBuffer>` with lock-guarded direct state, adding stateless `LedgerStore`
   replica entry points and `TryAdmitDirect`, moving off-scheduler `ForceYielding` dispatch with `NotThisNode`-only
   deactivation hops into `*FromGrainAsync`, and encoding `JournalEntryRecord` directly into Orleans' single non-POH
   page (`WireBuffers.CopyToOrleans(JournalEntryRecord)`) reduced allocations by **~4.0 KiB/op** on small fan-out
   writes (the remaining 32 KiB/op is the single non-POH `ArrayPool` page required by `OR-I18` and Orleans'
   `ArcBufferPage`) and improved throughput across fan-out, burst, probe saturation, and forwarded writes:
   - `replica-fanout` (3 silos × 16 ledgers): 1 KiB **15.90 → 16.59 MiB/s** (**+4.3%**; alloc **47.64 → 43.67 B/B**,
     **−8.3%** / −4.07 KiB/op; CPU **1,035.6 → 938.6 s/GiB**, **−9.4%**; GC pause **2.14% → 1.55%**); 4 KiB
     **52.58 → 55.50 MiB/s** (**+5.6%**, 14,208 ops/s; p99 **9.05 → 7.58 ms**, **−16%**; alloc **11.97 → 10.98 B/B**,
     **−8.3%** / −4.05 KiB/op; CPU **270.2 → 256.6 s/GiB**); 1 MiB **109.42 → 109.96 MiB/s** (p99 **558.7 → 484.7 ms**,
     **−13%**; WS peak **353.7 → 337.3 MiB**).
   - `replica-burst` (3 × 32 × 1 MiB → 1 callee): **286.43 → 310.12 MiB/s** (**+8.3%**, CPU **4.44 → 3.97 s/GiB**,
     **−10.6%**; WS peak **395.6 → 378.4 MiB**).
   - `probe-saturation` (20 s 1 MiB saturation): callee **276.34 → 317.75 MiB/s** (**+15.0%**, cluster RTT p99
     **12.90 → 9.50 ms**, 0 missed pings, 0 false suspicions).
   - `forwarded-journals`: 1 MiB forwarded write **48.74 → 56.62 MiB/s** (**+16.2%**, p99 **127.37 → 62.08 ms**,
     **−51%**); 1 KiB forwarded write **424.10 → 432.03 ops/s** (p50 **2.13 → 2.08 ms**); 4 concurrent 64 MiB forwarded
     readers **647.00 → 664.03 MiB/s** (GC pause **4.85% → 4.46%**).
7. **L1 → L2 CPU overhead is `~1.7–2.1 µs`; WAL quorum cost is disk-bound:** In-memory quorum (`N = 3, Q = 2`)
   completes in **1.7–2.1 µs** p50 and scales to **2.77M ops/s** (4 KiB, c=64) and **53.4 GiB/s** (1 MiB, c=64) across
   16 ledgers. With 3 real WALs sharing one NVMe, quorum latency is **653–675 µs** c=1 (1,193–1,225 µs open-loop 10%)
   and saturates at **90.6–140.5 MiB/s** logical ingress (≈ 272–422 MiB/s physical writes across the 3 local WALs,
   saturating the single NVMe's write-through ceiling).
8. **L2 → L3 (`JournalStore`) overhead is `~0.8–1.0 µs` CPU, and batching matches multi-ledger WAL saturation:**
   `JournalStore` admission, batch encoding, and `JournalState` fold add **~0.8–1.0 µs** in-memory (1.7–2.1 →
   2.7–2.9 µs) and reach **621k ops/s** (2,426 MiB/s, c=16; **596k ops/s** / 2,327 MiB/s at c=64) at 4 KiB and
   **52.2 GiB/s** at 1 MiB across 16 ledgers. On real WALs, a single hot journal key (`L3-JournalWal-1L-Hot`)
   serializes behind its per-key lock at **1,253 ops/s** (`4.9 MiB/s`), while distinct keys (`L3-JournalWal-1L-Many`
   and `L3-JournalWal-16L-Many`) let `LedgerAdmission` coalesce concurrent appends into shared quorum batches and reach
   **18,011** and **23,395 ops/s** (`70.4` and `91.4 MiB/s`), matching L2's WAL quorum ceiling (`23,186–23,749 ops/s`).
9. **L3 → L4 (Orleans TCP) adds `+534 µs` remote hop and ~1.62% GC pause at 4 KiB:** On real loopback TCP across 3
   processes, a 1 KiB remote replica write adds **+534 µs** over a local write (472.6 → 1,006.8 µs), and 4 KiB fan-out
   reaches 14.2k ops/s (55.5 MiB/s, p99 7.58 ms) vs L3 WAL's 18.0–23.4k ops/s (70.4–91.4 MiB/s) with 1.62% GC pause
   share. At 1 MiB batches, Orleans reaches **110.0 MiB/s** ingress across 3 silos (p99 484.7 ms) and
   **310.1–317.8 MiB/s** into one callee silo, saturating the single local NVMe with only **0.11–0.31%** GC pause share.

## Row 5.4 Journal and LSM benchmarks

### Journal writes (`JournalWriteBenchmarks`)

Single sequential caller over a 3-replica `LedgerStore` quorum (`N = 3, Q = 2`), comparing a discarding in-memory
replica transport (pure CPU, admission, encode, fold without unbounded replica growth) against 3 real local
`PortableWalStorage` replicas. Journal state resets run in `[IterationCleanup]` outside the timed benchmark body so
`OperationsPerInvoke` counts only timed writes. Measured under Server GC (`DOTNET_gcServer=1`, `--job Short`).

| Method | Replicas | Mean | Error | StdDev | Median | Allocated |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| `Append1KiB` | In-memory | 4.29 µs | 1.75 µs | 0.10 µs | 4.34 µs | 1.18 KB |
| `Append64KiB` | In-memory | 24.92 µs | 66.84 µs | 3.66 µs | 24.18 µs | 1.18 KB |
| `Replace1MiB` | In-memory | 243.60 µs | 21.02 µs | 1.15 µs | 244.24 µs | 1.33 KB |
| `Append1KiB` | Real WAL (×3) | 693.10 µs | 271.69 µs | 14.89 µs | 693.36 µs | 10.95 KB |
| `Append64KiB` | Real WAL (×3) | 2,248.52 µs | 42,180.66 µs | 2,312.06 µs | 963.49 µs | 14.87 KB |
| `Replace1MiB` | Real WAL (×3) | 6,641.95 µs | 28,295.22 µs | 1,550.95 µs | 5,965.39 µs | 1,190.45 KB |

### Journal live-ledger, absent, and cold reads (`JournalReadBenchmarks`)

- **Live-ledger primary reads (`JournalWarmReadBenchmarks`):** `JournalStore.ReadOnceAsync` reads the live ledger on
  the primary and folds the journal's rows into state (not served from `JournalWarmState`), asserting the returned
  content length and version on every call.
- **Absent reads (`JournalAbsentReadBenchmarks`):** First touch on an absent journal over a caught-up RocksDB
  `JournalStateDatabase` key index and preloaded `LsmArchiveStore` section metadata cache. Verified and asserted
  **0 object-store requests** per read across both 100 and 10,000 drained journals
  ([README §1](../spec/README.md#1-motivation)).
- **Cold reads (`JournalColdReadBenchmarks`):** Reuses one `JournalStore` per mode for first touch on a 4 KiB drained
  journal in `LsmArchiveStore` backed by `FileSystemObjectStore` on local disk, asserting returned content length,
  version, and exact object-store GETs/read: uncached (`TailCacheBytes = 0`, 2.00 GETs/read: section tail + data block),
  preloaded section metadata cache (1.00 GET/read: data block only), and section metadata cache + local-disk S3-FIFO
  block cache (0.00 GETs/read).

| Suite / Method | Parameter | Mean | Error | StdDev | Allocated | Object-store GETs/read |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| `WarmRead` | 1 KiB live journal | 5.03 µs | 0.42 µs | 0.02 µs | 5.94 KB | 0.00 |
| `WarmRead` | 64 KiB live journal | 16.86 µs | 6.54 µs | 0.36 µs | 5.94 KB | 0.00 |
| `AbsentRead` | 100 drained journals | 19.54 µs | 4.28 µs | 0.24 µs | 5.78 KB | 0.00 |
| `AbsentRead` | 10,000 drained journals | 21.36 µs | 5.23 µs | 0.29 µs | 5.89 KB | 0.00 |
| `ColdRead` | Uncached (`TailCacheBytes = 0`) | 344.2 µs | 22.12 µs | 1.21 µs | 14.03 KB | 2.00 |
| `ColdRead` | Section metadata cache | 168.8 µs | 45.71 µs | 2.51 µs | 6.97 KB | 1.00 |
| `ColdRead` | Metadata + block cache | 68.23 µs | 6.58 µs | 0.36 µs | 6.24 KB | 0.00 |

Measured under Server GC (`DOTNET_gcServer=1`, `--job Short`).

### LSM drain and compaction (`LsmDrainAndCompactionBenchmarks`)

- **Drain (`LsmDrainBenchmarks`):** `DrainWorker` draining a closed ~4.2 MiB ledger segment holding a constant
  10,000 × 420 B rows across both 100 and 10,000 distinct journals (per-iteration directory setup/cleanup runs outside
  the timed body). Object-store PUTs stayed exactly **1.00 per segment** across the 100× increase in distinct journals,
  confirming that object-store writes scale with ledger segments rather than journal count
  ([README §1](../spec/README.md#1-motivation)).
- **Compaction (`LsmCompactionBenchmarks`):** `CompactionWorker` merging all 8 overlapping ~1.05 MiB LSM sections
  (~8.4 MiB total input, asserting `Inputs.Length == 8`, output partition depths `0` for `MergeInPlace` and `2` across
  all 4 child partitions for `Push`, and `outputBytes > 0`) in `KeyRange.Root`.

| Suite / Method | Parameters | Mean | Effective throughput | Allocated | Object-store PUTs/run |
| --- | --- | ---: | ---: | ---: | ---: |
| `DrainSegment` (4.2 MiB) | 100 journals, in-memory store | 32.44 ms | ~129 MiB/s | 6.58 MB | 1.00 |
| `DrainSegment` (4.2 MiB) | 100 journals, file-system store | 47.30 ms | ~89 MiB/s | 2.14 MB | 1.00 |
| `DrainSegment` (4.2 MiB) | 10,000 journals, in-memory store | 40.19 ms | ~105 MiB/s | 6.65 MB | 1.00 |
| `DrainSegment` (4.2 MiB) | 10,000 journals, file-system store | 57.19 ms | ~73 MiB/s | 2.19 MB | 1.00 |
| `CompactPartition` (8.4 MiB) | `MergeInPlace` (8 sections → root) | 13.75 ms | ~611 MiB/s | 7.00 MB | — |
| `CompactPartition` (8.4 MiB) | `Push` (8 sections → 4 children) | 14.04 ms | ~598 MiB/s | 7.44 MB | — |

## WAL, LedgerStore, and engine microbenchmarks

### Portable WAL (`--wal-perf portable`)

`WriteAheadLog` over `PortableWalStorage` (`FILE_FLAG_WRITE_THROUGH`) on the reference Windows NTFS NVMe:

| Payload × clients | Ops/s | Throughput (MiB/s) | p50 (µs) | p99 (µs) |
| --- | ---: | ---: | ---: | ---: |
| 128 B × 1 | 5,263 | 0.64 | 176.2 | 369.3 |
| 128 B × 16 | 37,384 | 4.56 | 350.7 | 2,141.0 |
| 128 B × 64 | 112,911 | 13.78 | 520.1 | 1,798.0 |
| 4 KiB × 1 | 4,175 | 16.31 | 214.3 | 818.8 |
| 4 KiB × 16 | 19,007 | 74.25 | 498.4 | 3,181.0 |
| 4 KiB × 64 | 21,568 | 84.25 | 1,463.0 | 25,551.0 |
| 64 KiB × 1 | 1,545 | 96.54 | 306.3 | 1,930.0 |
| 64 KiB × 16 | 3,715 | 232.21 | 1,599.0 | 39,886.0 |
| 64 KiB × 64 | 1,760 | 109.98 | 7,176.0 | 511,975.0 |

### `LedgerStore` reads, writes, and checkpoints

- **`LedgerStoreReplicaReadBenchmarks` (evaluated against [WAL cache of recent durable pages](../spec/deferred.md#wal-cache-of-recent-durable-pages)):**
  - `LedgerStorePointRead` (4 KiB idle-disk direct-I/O WAL read): **435.5 µs** (StdDev 7.34 µs, 4.41 KB allocated) —
    aligned with #131's 104–420 µs Windows NTFS baseline and well below the ~1–2 ms quorum write path; concurrent point
    reads under write saturation remain the active revisit condition.
  - `LedgerStoreReopen`: **92.54 ms** (StdDev 3.79 ms, 926.74 KB allocated).
- **`LedgerStoreReplicaWriteBenchmarks`:**
  - `LedgerStoreWindow` vs `LedgerStoreSequential`: 128 B **8.33 µs** vs **219.87 µs** (1.32 vs 2.51 KB); 4 KiB
    **12.42 µs** vs **138.01 µs** (1.41 vs 2.54 KB); 64 KiB **409.40 µs** vs **510.72 µs** (2.50 vs 4.02 KB).
- **`LedgerStoreCheckpointBenchmarks` (`ReopenFromCheckpoint` vs `ReopenByFullScan`):**
  - 64 clients × 128 B: **62.76 ms** vs **105.78 ms** (0.64× time, 786 KB vs 5,309 KB alloc, 0.15×).
  - 64 clients × 4 KiB: **193.21 ms** vs **432.68 ms** (0.45× time, 1,642 KB vs 15,091 KB alloc, 0.11×).

### Quorum reads, ObjectLedger, codecs, buffers, and control plane

| Benchmark suite | Key measurements |
| --- | --- |
| `LedgerQuorumReadBenchmarks` | Single-replica `DecidedRead` vs all-replica `UndecidedRead`: sync **218.4 ns** (120 B) vs **1,055.7 ns** (1,552 B) (**0.21×** time, **0.08×** alloc); async **1,913.8 ns** (921 B) vs **4,924.2 ns** (2,060 B) (**0.39×** time, **0.45×** alloc). |
| `ObjectLedgerWriteBenchmarks` | `PerSlot` `K=1,L=1` **6.86 µs** (3.34 KB) to `K=4,L=8` **4.36 µs** (4.23 KB); `Append` `K=1,L=1` **10.91 µs** (4.33 KB) to `K=4,L=8` **7.57 µs** (4.24 KB). |
| `ObjectLedgerTakeoverBenchmarks` | `PerSlot` 10–1,000 slots **2.35–2.76 µs** (2.95 KB); `Append` **2.55–2.99 µs** (3.19 KB). |
| `ObjectLedgerCoalescerBenchmarks` | 1–16 concurrent appends: 256 B **1.28–27.42 µs**; 4 KiB **2.26–58.10 µs**. |
| `ObjectLedgerCodecBenchmarks` | `EncodeFramesAndTrailer` 1×64 B **40.4 ns** (0 B alloc), 16×4 KiB **4.31 µs** (0 B alloc); `DecodeExtent` 16×4 KiB **3.14 µs** (1,656 B); `EncodeAndDecodeTombstone` **36.0 ns** (112 B). |
| `BatchEncodeBenchmarks` | 1×4 KiB `Encode` **722.2 ns** (664 B); 16×4 KiB **7.18 µs** (449 ns/row, 2,424 B); 256×4 KiB **145.5 µs** (568 ns/row, 45.5 KB). |
| `BatchDecodeBenchmarks` | 1×4 KiB `Decode` **310.2–329.2 ns** (0 B); 256×4 KiB `Decode` **55.1–62.9 µs** (215–246 ns/row, 0 B); `DecodeAndWalk` fewest pages **79.7 µs** (96 B). |
| `BufferWriteBenchmarks` | 64 KiB: `PooledArcBufferPresized` **2.06 µs** (96 B) vs `ArrayBufferWriter` **12.94 µs** (131 KB) — **6.3×** faster, **0.001×** alloc. |
| `SlotLeafCodecBenchmarks` | Sequential leaf: `Decode` **3.99 µs** (160 B), `DecodeAll` **5.07 µs** (0 B), `GetEach` **5.04 µs** (0 B), `Encode` **11.93 µs** (392 B). |
| `ClusterControllerPassBenchmarks` | Steady-state `PassWithNothingChanged`: **450.5–495.1 µs** (8.22 KB) across 0–800 retired nodes and 3–10 domains; `PassAfterARegistryChange`: **1.85–2.89 ms** (1.25 MB). |
| `ReplicaCatalogBenchmarks` | 1,000,000 tombstones: `IsDropped` **40.9 ns** (0 B), `Follows` **105.1 ms** (56 B), `Decode` **163.3 ms** (48.2 MB). |

## Multi-process Server-GC Orleans TCP benchmarks (`--orleans-bench`)

Every scenario ran with one silo per OS process (`DOTNET_gcServer=1`, verified `IsServerGC == true`), loopback TCP
sockets, shared `FileSystemMetaStore` and `FileSystemObjectStore`, per-silo `PortableWalStorage` roots (1 GiB files),
default replica/forwarding limits, and 3 runs per scenario (mean and min–max range). In `probe-saturation`,
`ClusterMembershipOptions` is configured with `ProbeTimeout = 1,000 ms`, `NumProbedSilos = 3`, and
`IAmAliveTablePublishTimeout = 5 s`. The normative tables for replica transport and forwarded journals are recorded in
[orleans §5.5](../spec/compositions/orleans.md#55-forwarding-and-journal-outcomes) and
[orleans §5.12](../spec/compositions/orleans.md#512-replica-transport), and the revisit evaluations are in
[deferred work](../spec/deferred.md#clog-pages-end-to-end-in-replica-calls).
