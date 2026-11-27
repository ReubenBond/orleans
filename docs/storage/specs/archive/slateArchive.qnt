// -*- mode: Bluespec; -*-
/**
 * Roux SlateDB Archive (`clog-archive`) safety model
 * (docs/design/storage.md §3, C1, ST2, ST3; docs/design/clog/invariants.md HI-I1–HI-I4).
 *
 * Models the concrete single-writer SlateDB archive coupled to live ledgers,
 * the MetaStore ledger map (CLLM), and lagging SlateDB `DbReader` instances:
 *
 * - Live ledgers accept committed entries `(ledger, slot)` and may be
 *   closed, trimmed up to `cllmWatermark`, and (once closed and fully drained)
 *   dropped from the ledger map.
 * - Archive writers open SlateDB with strictly increasing fence epochs.
 *   SlateDB object-store fencing rejects commits from any epoch < activeEpoch.
 * - Each archive drain commits `(rows, slateWatermark[l], commitSeq)` atomically
 *   in one SlateDB WriteBatch; publishing `cllmWatermark[l]` to MetaStore is a
 *   separate later step (so crashes/delays between them are explored).
 * - Readers follow ST3 / HI-I2:
 *   1. Snapshot the MetaStore ledger map (`readMap`), learning live ledgers
 *      and each ledger's `cllmWatermark`.
 *   2. Refresh `DbReader` (via `subscribe()` or cache-sharing reopen) until
 *      its manifest snapshot covers every `cllmWatermark` in `readMap` and
 *      every dropped ledger.
 *   3. Read archive rows at `readerSeq` unioned with live ledger rows; retry
 *      from step 1 if a needed live ledger was trimmed or dropped past `readerSeq`.
 *
 * Mutants disable one guard at a time and are verified via ../check-mutants.sh.
 */
module slateArchive {
  const LEDGERS: Set[int]
  const MAX_SLOT: int         // slots 0 .. MAX_SLOT - 1 per ledger
  const WRITERS: Set[int]     // candidate archive writer IDs
  const READERS: Set[int]     // concurrent DbReader IDs
  const MAX_EPOCH: int        // SlateDB writer fence epochs

  type Entry = { ledger: int, slot: int }

  type SlateSnapshot = {
    seq: int,
    rows: Set[Entry],
    watermarks: int -> int,
  }

  type ReaderState = {
    phase: str,               // "idle" | "mapped"
    // Committed entries present when this read began (linearizability baseline).
    baseline: Set[Entry],
    // Snapshot of live ledgers and their CLLM watermarks at step 1.
    mapLive: Set[int],
    mapWatermarks: int -> int,
    // Current DbReader manifest snapshot seq (may lag behind latestSeq).
    readerSeq: int,
    // Entries collected by the most recent completed read.
    collected: Set[Entry],
  }

  // ---------------------------------------------------------------------------
  // State
  // ---------------------------------------------------------------------------
  // Live ledgers & MetaStore (CLLM) state
  var liveLedgers: Set[int]
  var closedLedgers: Set[int]
  var ledgerTail: int -> int            // committed slots are 0 .. tail - 1
  var ledgerFloor: int -> int           // live trimmed floor (available: floor .. tail - 1)
  var cllmWatermark: int -> int         // archive_watermark published to MetaStore
  var allCommitted: Set[Entry]          // ghost history of every committed entry

  // SlateDB single-writer fenced state
  var activeEpoch: int
  var writerEpoch: int -> int           // writer -> last opened epoch (0 = unopened)
  var latestSeq: int
  var slateSnapshots: int -> SlateSnapshot  // seq -> immutable SlateDB snapshot
  var unfencedCommitOccurred: bool

  // Concurrent DbReaders
  var readers: int -> ReaderState
  var staleReadObserved: bool

  pure val EMPTY_WM: int -> int = LEDGERS.mapBy(_ => 0)
  pure val INIT_SNAP: SlateSnapshot = { seq: 0, rows: Set(), watermarks: EMPTY_WM }
  pure val IDLE_READER: ReaderState = {
    phase: "idle",
    baseline: Set(),
    mapLive: Set(),
    mapWatermarks: EMPTY_WM,
    readerSeq: 0,
    collected: Set(),
  }

  // ---------------------------------------------------------------------------
  // Init
  // ---------------------------------------------------------------------------
  action init = all {
    liveLedgers' = LEDGERS,
    closedLedgers' = Set(),
    ledgerTail' = LEDGERS.mapBy(_ => 0),
    ledgerFloor' = LEDGERS.mapBy(_ => 0),
    cllmWatermark' = EMPTY_WM,
    allCommitted' = Set(),
    activeEpoch' = 0,
    writerEpoch' = WRITERS.mapBy(_ => 0),
    latestSeq' = 0,
    slateSnapshots' = Map(0 -> INIT_SNAP),
    unfencedCommitOccurred' = false,
    readers' = READERS.mapBy(_ => IDLE_READER),
    staleReadObserved' = false,
  }

  action keepSlate = all {
    activeEpoch' = activeEpoch,
    writerEpoch' = writerEpoch,
    latestSeq' = latestSeq,
    slateSnapshots' = slateSnapshots,
    unfencedCommitOccurred' = unfencedCommitOccurred,
  }

  action keepLive = all {
    liveLedgers' = liveLedgers,
    closedLedgers' = closedLedgers,
    ledgerTail' = ledgerTail,
    ledgerFloor' = ledgerFloor,
    cllmWatermark' = cllmWatermark,
    allCommitted' = allCommitted,
  }

  action keepReaders = all {
    readers' = readers,
    staleReadObserved' = staleReadObserved,
  }

  // ---------------------------------------------------------------------------
  // Live ledger actions
  // ---------------------------------------------------------------------------

  // Append next committed entry to an open live ledger.
  action ledgerAppend(l: int): bool = {
    val slot = ledgerTail.get(l)
    val e: Entry = { ledger: l, slot: slot }
    all {
      l.in(liveLedgers),
      not(l.in(closedLedgers)),
      slot < MAX_SLOT,
      ledgerTail' = ledgerTail.set(l, slot + 1),
      allCommitted' = allCommitted.union(Set(e)),
      liveLedgers' = liveLedgers,
      closedLedgers' = closedLedgers,
      ledgerFloor' = ledgerFloor,
      cllmWatermark' = cllmWatermark,
      keepSlate,
      keepReaders,
    }
  }

  // Seal/close a live ledger (no further appends).
  action ledgerClose(l: int): bool = all {
    l.in(liveLedgers),
    not(l.in(closedLedgers)),
    closedLedgers' = closedLedgers.union(Set(l)),
    liveLedgers' = liveLedgers,
    ledgerTail' = ledgerTail,
    ledgerFloor' = ledgerFloor,
    cllmWatermark' = cllmWatermark,
    allCommitted' = allCommitted,
    keepSlate,
    keepReaders,
  }

  // Trim live ledger floor up to CLLM archive_watermark.
  action ledgerTrim(l: int, newFloor: int): bool = all {
    l.in(liveLedgers),
    newFloor > ledgerFloor.get(l),
    newFloor <= cllmWatermark.get(l),
    ledgerFloor' = ledgerFloor.set(l, newFloor),
    liveLedgers' = liveLedgers,
    closedLedgers' = closedLedgers,
    ledgerTail' = ledgerTail,
    cllmWatermark' = cllmWatermark,
    allCommitted' = allCommitted,
    keepSlate,
    keepReaders,
  }

  // Drop a closed ledger once its CLLM archive_watermark reached its closed tail.
  action ledgerDrop(l: int): bool = all {
    l.in(liveLedgers),
    l.in(closedLedgers),
    cllmWatermark.get(l) >= ledgerTail.get(l),
    liveLedgers' = liveLedgers.exclude(Set(l)),
    closedLedgers' = closedLedgers,
    ledgerTail' = ledgerTail,
    ledgerFloor' = ledgerFloor,
    cllmWatermark' = cllmWatermark,
    allCommitted' = allCommitted,
    keepSlate,
    keepReaders,
  }

  // ---------------------------------------------------------------------------
  // SlateDB single-writer archive actions (C1, ST2)
  // ---------------------------------------------------------------------------

  // A candidate archive writer opens SlateDB, fencing all earlier epochs.
  action writerOpen(w: int): bool = all {
    w.in(WRITERS),
    activeEpoch < MAX_EPOCH,
    activeEpoch' = activeEpoch + 1,
    writerEpoch' = writerEpoch.set(w, activeEpoch + 1),
    latestSeq' = latestSeq,
    slateSnapshots' = slateSnapshots,
    unfencedCommitOccurred' = unfencedCommitOccurred,
    keepLive,
    keepReaders,
  }

  // Active writer drains [curWm, nextWm) for ledger l in ONE atomic SlateDB
  // WriteBatch (data rows + watermark update together).
  action archiveDrainBatch(w: int, l: int, nextWm: int): bool = {
    val curSnap = slateSnapshots.get(latestSeq)
    val curWm = curSnap.watermarks.get(l)
    val newEntries = curWm.to(nextWm - 1).map(s => { ledger: l, slot: s })
    val nextSeq = latestSeq + 1
    val nextSnap: SlateSnapshot = {
      seq: nextSeq,
      rows: curSnap.rows.union(newEntries),
      watermarks: curSnap.watermarks.set(l, nextWm),
    }
    all {
      w.in(WRITERS),
      writerEpoch.get(w) > 0,
      writerEpoch.get(w) == activeEpoch,   // SlateDB writer fence
      l.in(liveLedgers),
      nextWm > curWm,
      nextWm <= ledgerTail.get(l),
      latestSeq' = nextSeq,
      slateSnapshots' = slateSnapshots.put(nextSeq, nextSnap),
      activeEpoch' = activeEpoch,
      writerEpoch' = writerEpoch,
      unfencedCommitOccurred' = unfencedCommitOccurred,
      keepLive,
      keepReaders,
    }
  }

  // Publish already-durable SlateDB watermark for ledger l into MetaStore (CLLM).
  action publishCllmWatermark(l: int, wm: int): bool = {
    val curSlateWm = slateSnapshots.get(latestSeq).watermarks.get(l)
    all {
      l.in(liveLedgers),
      wm > cllmWatermark.get(l),
      wm <= curSlateWm,
      cllmWatermark' = cllmWatermark.set(l, wm),
      liveLedgers' = liveLedgers,
      closedLedgers' = closedLedgers,
      ledgerTail' = ledgerTail,
      ledgerFloor' = ledgerFloor,
      allCommitted' = allCommitted,
      keepSlate,
      keepReaders,
    }
  }

  // ---------------------------------------------------------------------------
  // DbReader actions (ST3, HI-I2)
  // ---------------------------------------------------------------------------

  // Step 1: Snapshot MetaStore ledger map (live ledgers + CLLM watermarks).
  action readerStart(r: int): bool = {
    val st = readers.get(r)
    val nextSt: ReaderState = {
      ...st,
      phase: "mapped",
      baseline: allCommitted,
      mapLive: liveLedgers,
      mapWatermarks: cllmWatermark,
      collected: Set(),
    }
    all {
      r.in(READERS),
      st.phase == "idle",
      readers' = readers.set(r, nextSt),
      staleReadObserved' = staleReadObserved,
      keepLive,
      keepSlate,
    }
  }

  // Background poll or subscribe()/reopen advances DbReader's manifest snapshot.
  action readerAdvanceManifest(r: int, newSeq: int): bool = {
    val st = readers.get(r)
    all {
      r.in(READERS),
      newSeq > st.readerSeq,
      newSeq <= latestSeq,
      readers' = readers.set(r, { ...st, readerSeq: newSeq }),
      staleReadObserved' = staleReadObserved,
      keepLive,
      keepSlate,
    }
  }

  // Step 2: Watermark-gated refresh check (ST3): DbReader proceeds to read
  // only once its manifest snapshot covers every CLLM watermark in readMap
  // (plus every ledger already absent from readMap, which was dropped after drain).
  pure def snapshotCoversMap(snap: SlateSnapshot, mapLive: Set[int], mapWm: int -> int, committed: Set[Entry]): bool = and {
    mapLive.forall(l => snap.watermarks.get(l) >= mapWm.get(l)),
    LEDGERS.exclude(mapLive).forall(l =>
      committed.filter(e => e.ledger == l).forall(e => e.slot < snap.watermarks.get(l))
    ),
  }

  // Step 3: Execute merged archive + live read (or retry if live was trimmed/dropped past readerSeq).
  action readerComplete(r: int): bool = {
    val st = readers.get(r)
    val snap = slateSnapshots.get(st.readerSeq)
    val liveTrimmedPastSnap = st.mapLive.exists(l =>
      not(l.in(liveLedgers)) or ledgerFloor.get(l) > snap.watermarks.get(l)
    )
    all {
      r.in(READERS),
      st.phase == "mapped",
      snapshotCoversMap(snap, st.mapLive, st.mapWatermarks, st.baseline),
      if (liveTrimmedPastSnap) {
        all {
          readers' = readers.set(r, {
            ...st,
            mapLive: liveLedgers,
            mapWatermarks: cllmWatermark,
          }),
          staleReadObserved' = staleReadObserved,
        }
      } else {
        val liveRows = st.mapLive.map(l =>
          ledgerFloor.get(l).to(ledgerTail.get(l) - 1).map(s => { ledger: l, slot: s })
        ).flatten()
        val result = snap.rows.union(liveRows)
        all {
          readers' = readers.set(r, { ...st, phase: "idle", collected: result }),
          staleReadObserved' = (staleReadObserved or not(st.baseline.subseteq(result))),
        }
      },
      keepLive,
      keepSlate,
    }
  }

  // ---------------------------------------------------------------------------
  // Next-state relation
  // ---------------------------------------------------------------------------
  action step = any {
    { nondet l = LEDGERS.oneOf(); ledgerAppend(l) },
    { nondet l = LEDGERS.oneOf(); ledgerClose(l) },
    { nondet l = LEDGERS.oneOf(); nondet f = 1.to(MAX_SLOT).oneOf(); ledgerTrim(l, f) },
    { nondet l = LEDGERS.oneOf(); ledgerDrop(l) },
    { nondet w = WRITERS.oneOf(); writerOpen(w) },
    { nondet w = WRITERS.oneOf(); nondet l = LEDGERS.oneOf(); nondet wm = 1.to(MAX_SLOT).oneOf(); archiveDrainBatch(w, l, wm) },
    { nondet l = LEDGERS.oneOf(); nondet wm = 1.to(MAX_SLOT).oneOf(); publishCllmWatermark(l, wm) },
    { nondet r = READERS.oneOf(); readerStart(r) },
    { nondet r = READERS.oneOf(); nondet sq = 1.to(latestSeq).oneOf(); readerAdvanceManifest(r, sq) },
    { nondet r = READERS.oneOf(); readerComplete(r) },
  }

  // ---------------------------------------------------------------------------
  // Safety invariants
  // ---------------------------------------------------------------------------

  // C1 / ST2: No commit ever succeeds from a fenced SlateDB writer epoch.
  val singleWriterFenced: bool = not(unfencedCommitOccurred)

  // HI-I1 / ST2: SlateDB watermarks are monotone across snapshots and CLLM never
  // leads durable SlateDB watermarks.
  val watermarksMonotone: bool = and {
    0.to(latestSeq).forall(s1 =>
      0.to(latestSeq).forall(s2 =>
        (s1 <= s2) implies LEDGERS.forall(l =>
          slateSnapshots.get(s1).watermarks.get(l) <= slateSnapshots.get(s2).watermarks.get(l)
        )
      )
    ),
    LEDGERS.forall(l => cllmWatermark.get(l) <= slateSnapshots.get(latestSeq).watermarks.get(l)),
  }

  // Atomic batch invariant: every slot below a snapshot's watermark is present in
  // that exact snapshot's rows.
  val archiveMatchesData: bool =
    0.to(latestSeq).forall(sq => {
      val snap = slateSnapshots.get(sq)
      LEDGERS.forall(l =>
        0.to(snap.watermarks.get(l) - 1).forall(sl => snap.rows.contains({ ledger: l, slot: sl }))
      )
    })

  // HI-I3: No live trim or closed-ledger drop discards an unarchived entry.
  val dropsAfterDrain: bool = {
    val latest = slateSnapshots.get(latestSeq)
    and {
      LEDGERS.forall(l => ledgerFloor.get(l) <= latest.watermarks.get(l)),
      LEDGERS.exclude(liveLedgers).forall(l => latest.watermarks.get(l) >= ledgerTail.get(l)),
    }
  }

  // HI-I2 / ST3: Every completed read sees every entry committed before it started.
  val readsComplete: bool = not(staleReadObserved)

  val safety: bool = and {
    singleWriterFenced,
    watermarksMonotone,
    archiveMatchesData,
    dropsAfterDrain,
    readsComplete,
  }

  // ---------------------------------------------------------------------------
  // Mutants (checked by ../check-mutants.sh)
  // ---------------------------------------------------------------------------

  // breaks: readsComplete
  action mutantStaleDbReaderNoRefresh = any {
    step,
    {
      nondet r = READERS.oneOf()
      val st = readers.get(r)
      val snap = slateSnapshots.get(st.readerSeq)
      // Bug: completes read without waiting for DbReader snapshot to cover readMap.
      val liveRows = st.mapLive.filter(l => l.in(liveLedgers)).map(l =>
        ledgerFloor.get(l).to(ledgerTail.get(l) - 1).map(s => { ledger: l, slot: s })
      ).flatten()
      val result = snap.rows.union(liveRows)
      all {
        st.phase == "mapped",
        not(snapshotCoversMap(snap, st.mapLive, st.mapWatermarks, st.baseline)),
        readers' = readers.set(r, { ...st, phase: "idle", collected: result }),
        staleReadObserved' = (staleReadObserved or not(st.baseline.subseteq(result))),
        keepLive,
        keepSlate,
      }
    },
  }

  // breaks: watermarksMonotone
  action mutantPublishCllmBeforeSlateCommit = any {
    step,
    {
      nondet l = LEDGERS.oneOf()
      nondet wm = 1.to(MAX_SLOT).oneOf()
      all {
        l.in(liveLedgers),
        wm > cllmWatermark.get(l),
        wm <= ledgerTail.get(l),
        // Bug: omits wm <= curSlateWm guard.
        cllmWatermark' = cllmWatermark.set(l, wm),
        liveLedgers' = liveLedgers,
        closedLedgers' = closedLedgers,
        ledgerTail' = ledgerTail,
        ledgerFloor' = ledgerFloor,
        allCommitted' = allCommitted,
        keepSlate,
        keepReaders,
      }
    },
  }

  // breaks: archiveMatchesData
  action mutantNonAtomicWatermarkBatch = any {
    step,
    {
      nondet w = WRITERS.oneOf()
      nondet l = LEDGERS.oneOf()
      nondet nextWm = 1.to(MAX_SLOT).oneOf()
      val curSnap = slateSnapshots.get(latestSeq)
      val curWm = curSnap.watermarks.get(l)
      val nextSeq = latestSeq + 1
      // Bug: updates watermark in SlateDB without writing the data rows in the same batch.
      val nextSnap: SlateSnapshot = {
        seq: nextSeq,
        rows: curSnap.rows,
        watermarks: curSnap.watermarks.set(l, nextWm),
      }
      all {
        writerEpoch.get(w) > 0,
        writerEpoch.get(w) == activeEpoch,
        l.in(liveLedgers),
        nextWm > curWm,
        nextWm <= ledgerTail.get(l),
        latestSeq' = nextSeq,
        slateSnapshots' = slateSnapshots.put(nextSeq, nextSnap),
        activeEpoch' = activeEpoch,
        writerEpoch' = writerEpoch,
        unfencedCommitOccurred' = unfencedCommitOccurred,
        keepLive,
        keepReaders,
      }
    },
  }

  // breaks: singleWriterFenced
  action mutantUnfencedStaleWriter = any {
    step,
    {
      nondet w = WRITERS.oneOf()
      nondet l = LEDGERS.oneOf()
      nondet nextWm = 1.to(MAX_SLOT).oneOf()
      val curSnap = slateSnapshots.get(latestSeq)
      val curWm = curSnap.watermarks.get(l)
      val newEntries = curWm.to(nextWm - 1).map(s => { ledger: l, slot: s })
      val nextSeq = latestSeq + 1
      val nextSnap: SlateSnapshot = {
        seq: nextSeq,
        rows: curSnap.rows.union(newEntries),
        watermarks: curSnap.watermarks.set(l, nextWm),
      }
      all {
        writerEpoch.get(w) > 0,
        // Bug: allows commit from a fenced epoch < activeEpoch.
        writerEpoch.get(w) < activeEpoch,
        l.in(liveLedgers),
        nextWm > curWm,
        nextWm <= ledgerTail.get(l),
        latestSeq' = nextSeq,
        slateSnapshots' = slateSnapshots.put(nextSeq, nextSnap),
        activeEpoch' = activeEpoch,
        writerEpoch' = writerEpoch,
        unfencedCommitOccurred' = true,
        keepLive,
        keepReaders,
      }
    },
  }

  // breaks: dropsAfterDrain
  action mutantDropBeforeDrain = any {
    step,
    {
      nondet l = LEDGERS.oneOf()
      all {
        l.in(liveLedgers),
        l.in(closedLedgers),
        // Bug: drops closed ledger even though tail > cllmWatermark.
        ledgerTail.get(l) > cllmWatermark.get(l),
        liveLedgers' = liveLedgers.exclude(Set(l)),
        closedLedgers' = closedLedgers,
        ledgerTail' = ledgerTail,
        ledgerFloor' = ledgerFloor,
        cllmWatermark' = cllmWatermark,
        allCommitted' = allCommitted,
        keepSlate,
        keepReaders,
      }
    },
  }
}

module slateArchiveSim {
  import slateArchive(
    LEDGERS = Set(1, 2),
    MAX_SLOT = 3,
    WRITERS = Set(1, 2),
    READERS = Set(1, 2),
    MAX_EPOCH = 3
  ).*

  // Directed scenario 1: Reader starts before drain, ledger drains + publishes
  // CLLM watermark + drops while reader holds stale manifest seq 0; reader
  // refreshes DbReader (subscribe / reopen) and reads all committed entries.
  run laggingDbReaderRefreshTest =
    init
      .then(writerOpen(1))
      .then(ledgerAppend(1))
      .then(ledgerAppend(1))
      .then(ledgerClose(1))
      .then(readerStart(1))
      .then(archiveDrainBatch(1, 1, 2))
      .then(publishCllmWatermark(1, 2))
      .then(ledgerDrop(1))
      // Reader refreshes manifest to seq 1, retries map snapshot, and completes
      .then(readerAdvanceManifest(1, 1))
      .then(readerComplete(1))
      .then(readerComplete(1))
      .expect(safety and readers.get(1).collected == Set({ ledger: 1, slot: 0 }, { ledger: 1, slot: 1 }))

  // Directed scenario 2: Writer failover fences old writer; new writer resumes
  // draining from previous durable SlateDB watermark without data loss.
  run writerFailoverFenceTest =
    init
      .then(writerOpen(1))
      .then(ledgerAppend(1))
      .then(ledgerAppend(1))
      .then(archiveDrainBatch(1, 1, 1))
      .then(writerOpen(2))
      .then(archiveDrainBatch(2, 1, 2))
      .then(publishCllmWatermark(1, 2))
      .then(ledgerTrim(1, 2))
      .expect(safety and activeEpoch == 2 and ledgerFloor.get(1) == 2)
}

module slateArchiveTlc {
  import slateArchive(
    LEDGERS = Set(1, 2),
    MAX_SLOT = 2,
    WRITERS = Set(1, 2),
    READERS = Set(1),
    MAX_EPOCH = 2
  ).*
}
