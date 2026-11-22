// -*- mode: Bluespec; -*-
/**
 * Object-store ledger model (test/Durable.Storage/docs/spec/components/object-ledgers.md §5, §7,
 * §8, §10), refining AtomicLedger.qnt.
 *
 * Models one ledger stored in a strongly consistent object store under either
 * storage layout selected by `Layout`:
 *
 * Layout == "per-slot" (S3 / GCS / default):
 * - One object per slot address (0 .. Slots): "empty", "data" (a batch), or
 *   "seal" (an empty object), placed by create-if-absent and settled by
 *   properties (OL-I2).
 * - Concurrent recovery executors: check `dropped`, create `sealed`, list
 *   newest-first down to max(evidence, h + 1 - K) (OL-I6), place conditional
 *   SEALs over [s, s + K) (looping above when the writer fills the window) and
 *   over (T, T + K) (OL-I3), re-read `dropped` before answering (OL-I6), and
 *   close the ledger in the map with a conditional write.
 * - The sweeper creates `dropped`, deletes batches while keeping markers and
 *   every SEAL forever (OL-I8), and re-sweeps straggler batches (OL-I9).
 *
 * Layout == "append" (Azure append extents, CLOE):
 * - Appendable extents keyed by their first slot (0 .. Slots): "absent",
 *   "appendable", "sealed", or "tombstone" (a 17-byte SEAL-frame block blob
 *   replacing the newest extent on drop).
 * - The writer coalesces contiguous queued batches into one append at the
 *   expected offset, settles unknown append/ROLL outcomes by reading the extent
 *   even after a seal (OL-I2), and creates extent `nextSlot` only after a ROLL
 *   naming `nextSlot` has answered Written on the current extent (OL-I10).
 * - Concurrent recovery executors check `dropped`, create `sealed`, list
 *   newest-first to find the newest existing extent (or 0 if none), create if
 *   absent and tag-seal it, follow any ROLL frame to the successor extent and
 *   seal it too, and re-read `dropped` before answering (OL-I6, OL-I10).
 * - The sweeper creates `dropped`, replaces the newest extent with a Tombstone
 *   (permanently fencing straggler writers), and deletes strictly older extents
 *   (OL-I8, OL-I9).
 *
 * Shared across both layouts:
 * - Two permanent marker objects: `sealed` (OL-I4) and `dropped` (OL-I4).
 * - Readers performing tail checks fix the bound before reading `sealed`
 *   (OL-I5, LL-I5).
 * - Fetchers with possibly stale map views reading and draining below a learned
 *   bound check `dropped` on absent data or SEAL (OL-I7, LL-I6, LL-I9).
 * - Step-by-step refinement of AtomicLedger.qnt checked as a state invariant
 *   (RefinesAtomicLedger).
 *
 * Mutants:
 * - "seal-unconditional"   (OL-I2): per-slot SEAL placement overwrites Data.
 * - "check-without-marker" (OL-I4, OL-I5, LL-I5): recovery omits `sealed`, and
 *   checks probe slot/extent seal state instead of reading the marker.
 * - "drop-without-marker"  (OL-I4, LL-I9): sweeper deletes/tombstones without
 *   creating `dropped`.
 * - "recover-from-gap"     (OL-I3, OL-I6): per-slot listing starts one slot
 *   too high (h + 2 - K), skipping a gap below h.
 * - "drop-deletes-seal"    (OL-I8, LL-I9): per-slot sweeper deletes SEALs too.
 * - "read-ignores-drop"    (OL-I7): a read meeting absent data below its bound
 *   fails as corruption without reading `dropped`.
 * - "tombstone-appendable" (OL-I8, LL-I9): append sweeper replaces the newest
 *   extent with an appendable object instead of a block-blob tombstone.
 * - "roll-before-written"  (OL-I10): append writer creates the next extent
 *   before the ROLL frame naming it has been written to the current extent.
 */
module ObjectLedger {
  import AtomicLedgerContract.* from "./AtomicLedger"

  // ---------------------------------------------------------------------------
  // Parameters
  // ---------------------------------------------------------------------------

  const Layout: str            // "per-slot" or "append"
  const Slots: int             // data slots 0 .. Slots - 1
  const Window: int            // K: writer window size (LP-I2)
  const Recoverers: Set[int]   // concurrent recovery executor IDs
  const Readers: Set[int]      // tail-check reader IDs
  const Fetchers: Set[int]     // fetcher IDs reading/draining below a bound
  const Mutation: str          // "none", or one of the eight guard-disabling mutants

  // ---------------------------------------------------------------------------
  // Types
  // ---------------------------------------------------------------------------

  // Store cell:     "empty" | "data" | "seal"
  // Extent kind:    "absent" | "appendable" | "sealed" | "tombstone"
  // Map status:     "open" | "closed" | "dropped"
  // Straggler:      "none" | "recreated" | "swept"
  // Recovery phase: "idle" | "mark_seal" | "listing" | "fencing" | "fence_above"
  //               | "sealing_extent" | "check_drop" | "closing" | "closed"
  //               | "superseded" | "dropped"
  // FRead phase:    "idle" | "check_drop"

  type ExtentState = {
    kind: str,
    end: int,
    roll: int,
  }

  type StoreState = {
    slots: int -> str,
    extents: int -> ExtentState,
    tombstoneHistory: Set[int],
    sealedMark: bool,
    droppedMark: bool,
    sealIssued: Set[int],
    sealHistory: Set[int],
    swept: bool,
    straggler: str,
  }

  type WriterState = {
    nextPos: int,
    completed: int,
    written: Set[int],
    sealed: Set[int],
    ackAfterSealed: bool,
    curExt: int,
    nextSlot: int,
    extCreated: bool,
    coalesced: bool,
  }

  type RecovererState = {
    phase: str,
    evidence: int,
    start: int,
    pending: Set[int],
    data: Set[int],
    seals: Set[int],
    end: int,
  }

  type MapState = {
    status: str,
    end: int,
  }

  type FReadState = {
    phase: str,
    pos: int,
    saw: str,
  }

  type FetchState = {
    known: int -> int,
    fread: int -> FReadState,
    served: Set[(int, bool)],
    drained: int,
    corruptRead: bool,
    sawLateSeal: bool,
  }

  // ---------------------------------------------------------------------------
  // State variables
  // ---------------------------------------------------------------------------

  var store: StoreState
  var writer: WriterState
  var rec: int -> RecovererState
  var ledgerMap: MapState
  var closes: Set[int]
  var ackedAtClose: int
  var chk: int -> AtomicCheckState
  var fetch: FetchState
  var recLooped: bool
  var recFollowedRoll: bool
  var refinesAtomic: bool

  // ---------------------------------------------------------------------------
  // Pure helpers and constants
  // ---------------------------------------------------------------------------

  pure def maxInt(a: int, b: int): int = if (a >= b) a else b
  pure def minInt(a: int, b: int): int = if (a <= b) a else b
  pure def rangeSet(lo: int, hi: int): Set[int] = if (lo <= hi) lo.to(hi) else Set()
  pure def minOf(xs: Set[int], fallback: int): int =
    xs.fold(fallback, (best, x) => if (x < best) x else best)
  pure def maxOf(xs: Set[int]): int =
    xs.fold(-1, (best, x) => if (x > best) x else best)

  val positions: Set[int] = rangeSet(0, Slots - 1)
  val addresses: Set[int] = rangeSet(0, Slots)

  pure def isFinished(phase: str): bool =
    phase == "closing" or phase == "closed" or phase == "superseded"

  pure def isSealingPhase(phase: str): bool =
    phase == "listing" or phase == "fencing" or phase == "fence_above" or
      phase == "sealing_extent" or phase == "check_drop"

  pure def emptyExtent(e: int): ExtentState = {
    kind: "absent",
    end: e,
    roll: -1,
  }

  pure def slotHasData(st: StoreState, p: int): bool =
    if (Layout == "per-slot") st.slots.get(p) == "data"
    else addresses.exists(e =>
      val ext = st.extents.get(e)
      (ext.kind == "appendable" or ext.kind == "sealed") and e <= p and p < ext.end
    )

  pure val initialRec: RecovererState = {
    phase: "idle",
    evidence: 0,
    start: 0,
    pending: Set(),
    data: Set(),
    seals: Set(),
    end: -1,
  }

  pure val initialFRead: FReadState = {
    phase: "idle",
    pos: 0,
    saw: "none",
  }

  val ended: bool = ledgerMap.status != "open"

  // Every slot below currentBound is decided: completed while open, end once closed.
  val currentBound: int =
    if (ledgerMap.status == "open") writer.completed else ledgerMap.end

  pure def sealOf(status: str, sealedMark: bool, r: int -> RecovererState): str =
    if (status != "open" or Recoverers.exists(i => isFinished(r.get(i).phase))) "sealed"
    else if (sealedMark or Recoverers.exists(i => isSealingPhase(r.get(i).phase))) "sealing"
    else "none"

  // ---------------------------------------------------------------------------
  // Projection to AtomicLedger (RefinesAtomicLedger)
  // ---------------------------------------------------------------------------

  val currentAtomic: AtomicState = {
    status: ledgerMap.status,
    issued: writer.nextPos,
    acked: writer.completed,
    end: ledgerMap.end,
    seal: sealOf(ledgerMap.status, store.sealedMark, rec),
    checks: chk,
    known: fetch.known,
    served: fetch.served,
    drained: fetch.drained,
  }

  def checkAtomic(post: AtomicState): bool =
    refinesAtomic and isAtomicStep(currentAtomic, post, Slots, Readers, Fetchers, "none")

  // ---------------------------------------------------------------------------
  // Initial state
  // ---------------------------------------------------------------------------

  action init = all {
    store' = {
      slots: addresses.mapBy(_ => "empty"),
      extents: addresses.mapBy(e => emptyExtent(e)),
      tombstoneHistory: Set(),
      sealedMark: false,
      droppedMark: false,
      sealIssued: Set(),
      sealHistory: Set(),
      swept: false,
      straggler: "none",
    },
    writer' = {
      nextPos: 0,
      completed: 0,
      written: Set(),
      sealed: Set(),
      ackAfterSealed: false,
      curExt: 0,
      nextSlot: 0,
      extCreated: false,
      coalesced: false,
    },
    rec' = Recoverers.mapBy(_ => initialRec),
    ledgerMap' = { status: "open", end: -1 },
    closes' = Set(),
    ackedAtClose' = 0,
    chk' = Readers.mapBy(_ => noAtomicCheck),
    fetch' = {
      known: Fetchers.mapBy(_ => 0),
      fread: Fetchers.mapBy(_ => initialFRead),
      served: Set(),
      drained: 0,
      corruptRead: false,
      sawLateSeal: false,
    },
    recLooped' = false,
    recFollowedRoll' = false,
    refinesAtomic' = true,
  }

  // ---------------------------------------------------------------------------
  // Writer actions
  // ---------------------------------------------------------------------------

  action acquire: bool = {
    val nextW = { ...writer, nextPos: writer.nextPos + 1 }
    all {
      writer.nextPos < Slots,
      writer.nextPos - writer.completed < Window,
      writer' = nextW,
      refinesAtomic' = checkAtomic({ ...currentAtomic, issued: nextW.nextPos }),
      store' = store,
      rec' = rec,
      ledgerMap' = ledgerMap,
      closes' = closes,
      ackedAtClose' = ackedAtClose,
      chk' = chk,
      fetch' = fetch,
      recLooped' = recLooped,
      recFollowedRoll' = recFollowedRoll,
    }
  }

  // Per-slot: batch create lands on an empty slot and Created reaches the writer
  // (unless the writer already settled this slot as sealed).
  action deliverWrite(a: int): bool = {
    val nextWritten =
      if (not(writer.sealed.contains(a))) writer.written.union(Set(a))
      else writer.written
    val nextStraggler =
      if (store.swept and store.straggler == "none") "recreated"
      else store.straggler
    all {
      Layout == "per-slot",
      a >= 0 and a < writer.nextPos,
      store.slots.get(a) == "empty",
      store' = {
        ...store,
        slots: store.slots.set(a, "data"),
        straggler: nextStraggler,
      },
      writer' = { ...writer, written: nextWritten },
      refinesAtomic' = refinesAtomic,
      rec' = rec,
      ledgerMap' = ledgerMap,
      closes' = closes,
      ackedAtClose' = ackedAtClose,
      chk' = chk,
      fetch' = fetch,
      recLooped' = recLooped,
      recFollowedRoll' = recFollowedRoll,
    }
  }

  // Per-slot: batch create lands on an empty slot, but its response is lost
  // (unknown outcome, OL-I2): the writer settles from object properties or retries.
  action deliverWriteUnknown(a: int): bool = {
    val nextStraggler =
      if (store.swept and store.straggler == "none") "recreated"
      else store.straggler
    all {
      Layout == "per-slot",
      a >= 0 and a < writer.nextPos,
      store.slots.get(a) == "empty",
      store' = {
        ...store,
        slots: store.slots.set(a, "data"),
        straggler: nextStraggler,
      },
      writer' = writer,
      refinesAtomic' = refinesAtomic,
      rec' = rec,
      ledgerMap' = ledgerMap,
      closes' = closes,
      ackedAtClose' = ackedAtClose,
      chk' = chk,
      fetch' = fetch,
      recLooped' = recLooped,
      recFollowedRoll' = recFollowedRoll,
    }
  }

  // Per-slot: writer settles Exists or an unknown outcome by reading object properties (OL-I2).
  action settleWrite(a: int): bool = {
    val cell = store.slots.get(a)
    val nextW =
      if (cell == "data") { ...writer, written: writer.written.union(Set(a)) }
      else { ...writer, sealed: writer.sealed.union(Set(a)) }
    all {
      Layout == "per-slot",
      a >= 0 and a < writer.nextPos,
      not(writer.written.contains(a)),
      not(writer.sealed.contains(a)),
      cell != "empty",
      writer' = nextW,
      refinesAtomic' = refinesAtomic,
      store' = store,
      rec' = rec,
      ledgerMap' = ledgerMap,
      closes' = closes,
      ackedAtClose' = ackedAtClose,
      chk' = chk,
      fetch' = fetch,
      recLooped' = recLooped,
      recFollowedRoll' = recFollowedRoll,
    }
  }

  // Append layout: create the current extent (if absent) before appending.
  action appendCreateExtent: bool = {
    val cur = store.extents.get(writer.curExt)
    val nextExtents =
      if (cur.kind == "absent")
        store.extents.set(writer.curExt, { kind: "appendable", end: writer.curExt, roll: -1 })
      else
        store.extents
    all {
      Layout == "append",
      not(writer.extCreated),
      writer.sealed == Set(),
      writer.nextSlot < writer.nextPos,
      store' = { ...store, extents: nextExtents },
      writer' = { ...writer, extCreated: true },
      refinesAtomic' = refinesAtomic,
      rec' = rec,
      ledgerMap' = ledgerMap,
      closes' = closes,
      ackedAtClose' = ackedAtClose,
      chk' = chk,
      fetch' = fetch,
      recLooped' = recLooped,
      recFollowedRoll' = recFollowedRoll,
    }
  }

  // Append layout: coalesce queued slots writer.nextSlot..hi in one conditional
  // append at offset writer.nextSlot (OL-I10) and receive Appended.
  action appendData(hi: int): bool = {
    val cur = store.extents.get(writer.curExt)
    val nextStraggler =
      if (store.swept and store.straggler == "none") "recreated"
      else store.straggler
    all {
      Layout == "append",
      writer.extCreated,
      writer.sealed == Set(),
      writer.nextSlot <= hi and hi < writer.nextPos,
      cur.kind == "appendable",
      cur.end == writer.nextSlot,
      cur.roll == -1,
      store' = {
        ...store,
        extents: store.extents.set(writer.curExt, { ...cur, end: hi + 1 }),
        straggler: nextStraggler,
      },
      writer' = {
        ...writer,
        written: writer.written.union(rangeSet(writer.nextSlot, hi)),
        nextSlot: hi + 1,
        coalesced: writer.coalesced or (hi > writer.nextSlot),
      },
      refinesAtomic' = refinesAtomic,
      rec' = rec,
      ledgerMap' = ledgerMap,
      closes' = closes,
      ackedAtClose' = ackedAtClose,
      chk' = chk,
      fetch' = fetch,
      recLooped' = recLooped,
      recFollowedRoll' = recFollowedRoll,
    }
  }

  // Append layout: coalesced append lands on the extent, but its response is
  // lost (unknown outcome, OL-I2); writer settles by reading the extent.
  action appendDataUnknown(hi: int): bool = {
    val cur = store.extents.get(writer.curExt)
    val nextStraggler =
      if (store.swept and store.straggler == "none") "recreated"
      else store.straggler
    all {
      Layout == "append",
      writer.extCreated,
      writer.sealed == Set(),
      writer.nextSlot <= hi and hi < writer.nextPos,
      cur.kind == "appendable",
      cur.end == writer.nextSlot,
      cur.roll == -1,
      store' = {
        ...store,
        extents: store.extents.set(writer.curExt, { ...cur, end: hi + 1 }),
        straggler: nextStraggler,
      },
      writer' = writer,
      refinesAtomic' = refinesAtomic,
      rec' = rec,
      ledgerMap' = ledgerMap,
      closes' = closes,
      ackedAtClose' = ackedAtClose,
      chk' = chk,
      fetch' = fetch,
      recLooped' = recLooped,
      recFollowedRoll' = recFollowedRoll,
    }
  }

  // Append layout: settle an unknown append outcome by reading the extent at
  // writer.nextSlot, even if recovery sealed the extent after the append landed
  // (object-ledgers.md §3.1, OL-I2).
  action settleAppend: bool = {
    val cur = store.extents.get(writer.curExt)
    all {
      Layout == "append",
      writer.extCreated,
      writer.sealed == Set(),
      writer.nextSlot < writer.nextPos,
      cur.kind == "appendable" or cur.kind == "sealed",
      cur.end > writer.nextSlot,
      writer' = {
        ...writer,
        written: writer.written.union(rangeSet(writer.nextSlot, cur.end - 1)),
        nextSlot: cur.end,
        coalesced: writer.coalesced or (cur.end - 1 > writer.nextSlot),
      },
      refinesAtomic' = refinesAtomic,
      store' = store,
      rec' = rec,
      ledgerMap' = ledgerMap,
      closes' = closes,
      ackedAtClose' = ackedAtClose,
      chk' = chk,
      fetch' = fetch,
      recLooped' = recLooped,
      recFollowedRoll' = recFollowedRoll,
    }
  }

  // Append layout: append meets a sealed, tombstoned, or swept extent; the
  // writer records Sealed for all queued slots and stops appending.
  action appendHitSealed: bool = {
    val cur = store.extents.get(writer.curExt)
    all {
      Layout == "append",
      writer.extCreated,
      writer.sealed == Set(),
      writer.nextSlot < writer.nextPos,
      cur.kind == "absent" or cur.kind == "tombstone" or
        (cur.kind == "sealed" and cur.end == writer.nextSlot and cur.roll == -1),
      writer' = { ...writer, sealed: rangeSet(writer.nextSlot, writer.nextPos - 1) },
      refinesAtomic' = refinesAtomic,
      store' = store,
      rec' = rec,
      ledgerMap' = ledgerMap,
      closes' = closes,
      ackedAtClose' = ackedAtClose,
      chk' = chk,
      fetch' = fetch,
      recLooped' = recLooped,
      recFollowedRoll' = recFollowedRoll,
    }
  }

  // Append layout: append a ROLL frame naming writer.nextSlot after writing at
  // least one slot in writer.curExt, and once Written advance to the next extent (OL-I10).
  action appendRoll: bool = {
    val cur = store.extents.get(writer.curExt)
    all {
      Layout == "append",
      writer.extCreated,
      writer.sealed == Set(),
      writer.nextSlot > writer.curExt,
      writer.nextSlot < Slots,
      cur.kind == "appendable",
      cur.end == writer.nextSlot,
      cur.roll == -1,
      store' = {
        ...store,
        extents: store.extents.set(writer.curExt, { ...cur, roll: writer.nextSlot }),
      },
      writer' = { ...writer, curExt: writer.nextSlot, extCreated: false },
      refinesAtomic' = refinesAtomic,
      rec' = rec,
      ledgerMap' = ledgerMap,
      closes' = closes,
      ackedAtClose' = ackedAtClose,
      chk' = chk,
      fetch' = fetch,
      recLooped' = recLooped,
      recFollowedRoll' = recFollowedRoll,
    }
  }

  // Append layout: ROLL frame lands on writer.curExt, but its response is lost.
  action appendRollUnknown: bool = {
    val cur = store.extents.get(writer.curExt)
    all {
      Layout == "append",
      writer.extCreated,
      writer.sealed == Set(),
      writer.nextSlot > writer.curExt,
      writer.nextSlot < Slots,
      cur.kind == "appendable",
      cur.end == writer.nextSlot,
      cur.roll == -1,
      store' = {
        ...store,
        extents: store.extents.set(writer.curExt, { ...cur, roll: writer.nextSlot }),
      },
      writer' = writer,
      refinesAtomic' = refinesAtomic,
      rec' = rec,
      ledgerMap' = ledgerMap,
      closes' = closes,
      ackedAtClose' = ackedAtClose,
      chk' = chk,
      fetch' = fetch,
      recLooped' = recLooped,
      recFollowedRoll' = recFollowedRoll,
    }
  }

  // Append layout: settle an unknown ROLL by observing roll == writer.nextSlot
  // in writer.curExt (even after writer.curExt was sealed).
  action settleRoll: bool = {
    val cur = store.extents.get(writer.curExt)
    all {
      Layout == "append",
      writer.extCreated,
      writer.sealed == Set(),
      writer.nextSlot > writer.curExt,
      cur.kind == "appendable" or cur.kind == "sealed",
      cur.roll == writer.nextSlot,
      writer' = { ...writer, curExt: writer.nextSlot, extCreated: false },
      refinesAtomic' = refinesAtomic,
      store' = store,
      rec' = rec,
      ledgerMap' = ledgerMap,
      closes' = closes,
      ackedAtClose' = ackedAtClose,
      chk' = chk,
      fetch' = fetch,
      recLooped' = recLooped,
      recFollowedRoll' = recFollowedRoll,
    }
  }

  // Mutant `roll-before-written` (OL-I10): writer creates a higher extent before
  // a ROLL frame naming it is written to writer.curExt.
  action appendCreateNextEarly(target: int): bool = {
    val cur = store.extents.get(writer.curExt)
    all {
      Layout == "append",
      Mutation == "roll-before-written",
      writer.sealed == Set(),
      target > writer.curExt and target <= writer.nextPos and target < Slots,
      store.extents.get(target).kind == "absent",
      cur.roll != target,
      store' = {
        ...store,
        extents: store.extents.set(target, { kind: "appendable", end: target, roll: -1 }),
      },
      refinesAtomic' = refinesAtomic,
      writer' = writer,
      rec' = rec,
      ledgerMap' = ledgerMap,
      closes' = closes,
      ackedAtClose' = ackedAtClose,
      chk' = chk,
      fetch' = fetch,
      recLooped' = recLooped,
      recFollowedRoll' = recFollowedRoll,
    }
  }

  // In-order completion (LP-I3): slot writer.completed completes once written.
  action complete: bool = {
    val nextW = {
      ...writer,
      completed: writer.completed + 1,
      ackAfterSealed: writer.ackAfterSealed or writer.sealed != Set(),
    }
    all {
      writer.completed < writer.nextPos,
      writer.written.contains(writer.completed),
      writer' = nextW,
      refinesAtomic' = checkAtomic({ ...currentAtomic, acked: nextW.completed }),
      store' = store,
      rec' = rec,
      ledgerMap' = ledgerMap,
      closes' = closes,
      ackedAtClose' = ackedAtClose,
      chk' = chk,
      fetch' = fetch,
      recLooped' = recLooped,
      recFollowedRoll' = recFollowedRoll,
    }
  }

  // ---------------------------------------------------------------------------
  // Recovery actions
  // ---------------------------------------------------------------------------

  // Step 1 (OL-I6): read `dropped` first; if present, answer Dropped creating nothing.
  action beginRecovery(i: int, ev: int): bool = {
    val r = rec.get(i)
    val nextR =
      if (store.droppedMark) { ...initialRec, phase: "dropped" }
      else { ...initialRec, phase: "mark_seal", evidence: ev }
    all {
      r.phase == "idle",
      ev >= 0 and ev <= currentBound,
      rec' = rec.set(i, nextR),
      refinesAtomic' = refinesAtomic,
      store' = store,
      writer' = writer,
      ledgerMap' = ledgerMap,
      closes' = closes,
      ackedAtClose' = ackedAtClose,
      chk' = chk,
      fetch' = fetch,
      recLooped' = recLooped,
      recFollowedRoll' = recFollowedRoll,
    }
  }

  // Step 2 (OL-I4): create `sealed` marker before placing any fence.
  // Under `check-without-marker`, recovery omits creating the marker.
  action createSealedMarker(i: int): bool = {
    val r = rec.get(i)
    val nextMark = if (Mutation == "check-without-marker") store.sealedMark else true
    val nextRec = rec.set(i, { ...r, phase: "listing" })
    all {
      r.phase == "mark_seal",
      store' = { ...store, sealedMark: nextMark },
      rec' = nextRec,
      refinesAtomic' = checkAtomic({ ...currentAtomic, seal: sealOf(ledgerMap.status, nextMark, nextRec) }),
      writer' = writer,
      ledgerMap' = ledgerMap,
      closes' = closes,
      ackedAtClose' = ackedAtClose,
      chk' = chk,
      fetch' = fetch,
      recLooped' = recLooped,
      recFollowedRoll' = recFollowedRoll,
    }
  }

  // Per-slot step 3a (OL-I3, OL-I6): list newest-first down to max(evidence, h + 1 - K).
  // Under `recover-from-gap`, the listing window starts one slot too high (h + 2 - K).
  action listWindow(i: int): bool = {
    val r = rec.get(i)
    val nonEmpty = addresses.filter(a => store.slots.get(a) != "empty")
    val rawLow =
      if (nonEmpty == Set()) r.evidence
      else if (Mutation == "recover-from-gap")
        minInt(Slots, maxInt(r.evidence, maxOf(nonEmpty) + 2 - Window))
      else
        maxInt(r.evidence, maxOf(nonEmpty) + 1 - Window)
    val low = maxInt(0, rawLow)
    val nonData = rangeSet(low, Slots).filter(a => store.slots.get(a) != "data")
    val start = minOf(nonData, Slots)
    val winEnd = minInt(start + Window - 1, Slots)
    val window = rangeSet(start, winEnd)
    val tailRange = rangeSet(start + 1, winEnd)
    val startIsSeal = store.slots.get(start) == "seal"
    val above = tailRange.filter(a => store.slots.get(a) == "empty")
    val pend = window.filter(a => store.slots.get(a) == "empty")
    val nextIssued =
      if (startIsSeal) store.sealIssued.union(above)
      else store.sealIssued.union(pend)
    val nextR =
      if (startIsSeal) {
        phase: if (above == Set()) "check_drop" else "fence_above",
        evidence: r.evidence,
        start: start,
        pending: above,
        data: tailRange.filter(a => store.slots.get(a) == "data"),
        seals: Set(start).union(tailRange.filter(a => store.slots.get(a) == "seal")),
        end: start,
      }
      else {
        phase: "fencing",
        evidence: r.evidence,
        start: start,
        pending: pend,
        data: window.filter(a => store.slots.get(a) == "data"),
        seals: window.filter(a => store.slots.get(a) == "seal"),
        end: -1,
      }
    all {
      Layout == "per-slot",
      r.phase == "listing",
      store' = { ...store, sealIssued: nextIssued },
      rec' = rec.set(i, nextR),
      refinesAtomic' = refinesAtomic,
      writer' = writer,
      ledgerMap' = ledgerMap,
      closes' = closes,
      ackedAtClose' = ackedAtClose,
      chk' = chk,
      fetch' = fetch,
      recLooped' = recLooped,
      recFollowedRoll' = recFollowedRoll,
    }
  }

  // Per-slot: a pending SEAL create lands in the store (create-if-absent, OL-I2).
  // Under `seal-unconditional`, the SEAL overwrites existing Data.
  action deliverSeal(a: int): bool = {
    val cell = store.slots.get(a)
    all {
      Layout == "per-slot",
      store.sealIssued.contains(a),
      cell == "empty" or (cell == "data" and Mutation == "seal-unconditional"),
      store' = {
        ...store,
        slots: store.slots.set(a, "seal"),
        sealHistory: store.sealHistory.union(Set(a)),
      },
      refinesAtomic' = refinesAtomic,
      writer' = writer,
      rec' = rec,
      ledgerMap' = ledgerMap,
      closes' = closes,
      ackedAtClose' = ackedAtClose,
      chk' = chk,
      fetch' = fetch,
      recLooped' = recLooped,
      recFollowedRoll' = recFollowedRoll,
    }
  }

  // Per-slot: recovery settles one pending slot in its fence window once occupied.
  action settleSeal(i: int, a: int): bool = {
    val r = rec.get(i)
    val cell = store.slots.get(a)
    val nextR = {
      ...r,
      pending: r.pending.exclude(Set(a)),
      data: if (cell == "data") r.data.union(Set(a)) else r.data,
      seals: if (cell == "seal") r.seals.union(Set(a)) else r.seals,
    }
    all {
      Layout == "per-slot",
      r.phase == "fencing" or r.phase == "fence_above",
      r.pending.contains(a),
      cell != "empty",
      rec' = rec.set(i, nextR),
      refinesAtomic' = refinesAtomic,
      store' = store,
      writer' = writer,
      ledgerMap' = ledgerMap,
      closes' = closes,
      ackedAtClose' = ackedAtClose,
      chk' = chk,
      fetch' = fetch,
      recLooped' = recLooped,
      recFollowedRoll' = recFollowedRoll,
    }
  }

  // Per-slot step 3b (OL-I3): once [start, start + K) is settled, loop above if
  // the writer filled the window, otherwise decide end T and fence (T, T + K).
  action finishWindow(i: int): bool = {
    val r = rec.get(i)
    val winEnd = minInt(r.start + Window - 1, Slots)
    val window = rangeSet(r.start, winEnd)
    val fullData = window.subseteq(r.data) and winEnd < Slots
    val nonData = window.filter(a => not(r.data.contains(a)))
    val end = if (nonData != Set()) minOf(nonData, winEnd) else winEnd + 1
    val aboveEnd = minInt(end + Window - 1, Slots)
    val above = rangeSet(end + 1, aboveEnd).filter(a =>
      not(r.data.contains(a)) and not(r.seals.contains(a))
    )
    val nextR =
      if (fullData) { ...initialRec, phase: "listing", evidence: r.start + Window }
      else {
        ...r,
        phase: if (above == Set()) "check_drop" else "fence_above",
        pending: above,
        end: end,
      }
    all {
      Layout == "per-slot",
      r.phase == "fencing",
      r.pending == Set(),
      store' = if (fullData) store else { ...store, sealIssued: store.sealIssued.union(above) },
      rec' = rec.set(i, nextR),
      recLooped' = if (fullData) true else recLooped,
      recFollowedRoll' = recFollowedRoll,
      refinesAtomic' = refinesAtomic,
      writer' = writer,
      ledgerMap' = ledgerMap,
      closes' = closes,
      ackedAtClose' = ackedAtClose,
      chk' = chk,
      fetch' = fetch,
    }
  }

  // Per-slot step 3c: every slot of (T, T + K) has been settled.
  action finishFenceAbove(i: int): bool = {
    val r = rec.get(i)
    all {
      Layout == "per-slot",
      r.phase == "fence_above",
      r.pending == Set(),
      rec' = rec.set(i, { ...r, phase: "check_drop" }),
      refinesAtomic' = refinesAtomic,
      store' = store,
      writer' = writer,
      ledgerMap' = ledgerMap,
      closes' = closes,
      ackedAtClose' = ackedAtClose,
      chk' = chk,
      fetch' = fetch,
      recLooped' = recLooped,
      recFollowedRoll' = recFollowedRoll,
    }
  }

  // Append layout step 3a (OL-I6): list newest-first to find the newest existing
  // extent (or extent 0 if none exist).
  action appendListExtent(i: int): bool = {
    val r = rec.get(i)
    val existing = addresses.filter(e => store.extents.get(e).kind != "absent")
    val target = if (existing == Set()) 0 else maxOf(existing)
    all {
      Layout == "append",
      r.phase == "listing",
      rec' = rec.set(i, { ...initialRec, phase: "sealing_extent", evidence: r.evidence, start: target }),
      refinesAtomic' = refinesAtomic,
      store' = store,
      writer' = writer,
      ledgerMap' = ledgerMap,
      closes' = closes,
      ackedAtClose' = ackedAtClose,
      chk' = chk,
      fetch' = fetch,
      recLooped' = recLooped,
      recFollowedRoll' = recFollowedRoll,
    }
  }

  // Append layout step 3b: create target extent if absent before sealing it.
  action appendRecoveryCreateExtent(i: int): bool = {
    val r = rec.get(i)
    val target = r.start
    all {
      Layout == "append",
      r.phase == "sealing_extent",
      store.extents.get(target).kind == "absent",
      store' = {
        ...store,
        extents: store.extents.set(target, { kind: "appendable", end: target, roll: -1 }),
      },
      refinesAtomic' = refinesAtomic,
      writer' = writer,
      rec' = rec,
      ledgerMap' = ledgerMap,
      closes' = closes,
      ackedAtClose' = ackedAtClose,
      chk' = chk,
      fetch' = fetch,
      recLooped' = recLooped,
      recFollowedRoll' = recFollowedRoll,
    }
  }

  // Append layout step 3c (OL-I6, OL-I10): tag-seal target extent; Tombstone
  // answers NotAppendable -> Dropped; otherwise seal, follow ROLL if present,
  // or decide T = extents[target].end and proceed to CheckDrop.
  action appendSealExtent(i: int): bool = {
    val r = rec.get(i)
    val target = r.start
    val ext = store.extents.get(target)
    val isTombstone = ext.kind == "tombstone"
    val nextStore =
      if (isTombstone) store
      else {
        ...store,
        extents: store.extents.set(target, { ...ext, kind: "sealed" }),
        sealHistory: store.sealHistory.union(Set(target)),
      }
    val nextR =
      if (isTombstone) { ...initialRec, phase: "dropped" }
      else if (ext.roll != -1) { ...r, start: ext.roll }
      else { ...r, phase: "check_drop", end: ext.end }
    val nextRec = rec.set(i, nextR)
    all {
      Layout == "append",
      r.phase == "sealing_extent",
      ext.kind != "absent",
      store' = nextStore,
      rec' = nextRec,
      recFollowedRoll' = if (not(isTombstone) and ext.roll != -1) true else recFollowedRoll,
      refinesAtomic' = checkAtomic({ ...currentAtomic, seal: sealOf(ledgerMap.status, nextStore.sealedMark, nextRec) }),
      writer' = writer,
      ledgerMap' = ledgerMap,
      closes' = closes,
      ackedAtClose' = ackedAtClose,
      chk' = chk,
      fetch' = fetch,
      recLooped' = recLooped,
    }
  }

  // Transient failure retries recovery from its current `evidence`, keeping
  // earlier per-slot SEAL creates in `sealIssued`.
  action retryRecovery(i: int): bool = {
    val r = rec.get(i)
    val nextR =
      if (store.droppedMark) { ...initialRec, phase: "dropped" }
      else { ...initialRec, phase: "listing", evidence: r.evidence }
    all {
      r.phase == "fencing" or r.phase == "fence_above" or
        r.phase == "sealing_extent" or r.phase == "check_drop",
      rec' = rec.set(i, nextR),
      refinesAtomic' = refinesAtomic,
      store' = store,
      writer' = writer,
      ledgerMap' = ledgerMap,
      closes' = closes,
      ackedAtClose' = ackedAtClose,
      chk' = chk,
      fetch' = fetch,
      recLooped' = recLooped,
      recFollowedRoll' = recFollowedRoll,
    }
  }

  // Step 4 (OL-I6, LL-I9): re-read `dropped` before returning candidate end T.
  action finishRecovery(i: int): bool = {
    val r = rec.get(i)
    val nextR =
      if (store.droppedMark) { ...initialRec, phase: "dropped" }
      else { ...r, phase: "closing" }
    val nextRec = rec.set(i, nextR)
    all {
      r.phase == "check_drop",
      rec' = nextRec,
      refinesAtomic' = checkAtomic({ ...currentAtomic, seal: sealOf(ledgerMap.status, store.sealedMark, nextRec) }),
      store' = store,
      writer' = writer,
      ledgerMap' = ledgerMap,
      closes' = closes,
      ackedAtClose' = ackedAtClose,
      chk' = chk,
      fetch' = fetch,
      recLooped' = recLooped,
      recFollowedRoll' = recFollowedRoll,
    }
  }

  // Conditional write of the ledger map closing the ledger at r.end.
  action closeLedger(i: int): bool = {
    val r = rec.get(i)
    val won = ledgerMap.status == "open"
    val nextMap = if (won) { status: "closed", end: r.end } else ledgerMap
    val nextRec = rec.set(i, { ...r, phase: if (won) "closed" else "superseded" })
    all {
      r.phase == "closing",
      ledgerMap' = nextMap,
      rec' = nextRec,
      closes' = if (won) closes.union(Set(r.end)) else closes,
      ackedAtClose' = if (won) writer.completed else ackedAtClose,
      refinesAtomic' = checkAtomic({
        ...currentAtomic,
        status: nextMap.status,
        end: nextMap.end,
        seal: sealOf(nextMap.status, store.sealedMark, nextRec),
      }),
      store' = store,
      writer' = writer,
      chk' = chk,
      fetch' = fetch,
      recLooped' = recLooped,
      recFollowedRoll' = recFollowedRoll,
    }
  }

  // ---------------------------------------------------------------------------
  // Drop and ObjectLedgerSweeper actions
  // ---------------------------------------------------------------------------

  action dropLedger: bool = {
    val nextMap = { ...ledgerMap, status: "dropped" }
    all {
      ledgerMap.status == "closed",
      ledgerMap' = nextMap,
      refinesAtomic' = checkAtomic({
        ...currentAtomic,
        status: nextMap.status,
        seal: sealOf(nextMap.status, store.sealedMark, rec),
      }),
      store' = store,
      writer' = writer,
      rec' = rec,
      closes' = closes,
      ackedAtClose' = ackedAtClose,
      chk' = chk,
      fetch' = fetch,
      recLooped' = recLooped,
      recFollowedRoll' = recFollowedRoll,
    }
  }

  // Sweeper step 1 (OL-I4): create `dropped` marker before deleting any object.
  action sweepMarkDrop: bool = all {
    ledgerMap.status == "dropped",
    not(store.droppedMark),
    Mutation != "drop-without-marker",
    store' = { ...store, droppedMark: true },
    refinesAtomic' = refinesAtomic,
    writer' = writer,
    rec' = rec,
    ledgerMap' = ledgerMap,
    closes' = closes,
    ackedAtClose' = ackedAtClose,
    chk' = chk,
    fetch' = fetch,
    recLooped' = recLooped,
    recFollowedRoll' = recFollowedRoll,
  }

  // Per-slot sweeper step 2 (OL-I8, OL-I9): delete batches of a revoked ledger
  // while keeping markers and every SEAL; re-sweeps straggler batches.
  action sweepDelete(a: int): bool = {
    val cell = store.slots.get(a)
    val nextStraggler =
      if (store.straggler == "recreated" and cell == "data") "swept"
      else store.straggler
    all {
      Layout == "per-slot",
      ledgerMap.status == "dropped",
      store.droppedMark or Mutation == "drop-without-marker",
      cell == "data" or (cell == "seal" and Mutation == "drop-deletes-seal"),
      store' = {
        ...store,
        slots: store.slots.set(a, "empty"),
        swept: true,
        straggler: nextStraggler,
      },
      refinesAtomic' = refinesAtomic,
      writer' = writer,
      rec' = rec,
      ledgerMap' = ledgerMap,
      closes' = closes,
      ackedAtClose' = ackedAtClose,
      chk' = chk,
      fetch' = fetch,
      recLooped' = recLooped,
      recFollowedRoll' = recFollowedRoll,
    }
  }

  // Append sweeper step 2a (OL-I8): replace the newest existing extent with a
  // 17-byte SEAL-frame block-blob tombstone. Under `tombstone-appendable`,
  // replaces it with an appendable object instead.
  action sweepTombstone: bool = {
    val existing = addresses.filter(e => store.extents.get(e).kind != "absent")
    val newest = maxOf(existing)
    val nextKind = if (Mutation == "tombstone-appendable") "appendable" else "tombstone"
    val nextTombstones =
      if (Mutation == "tombstone-appendable") store.tombstoneHistory
      else store.tombstoneHistory.union(Set(newest))
    all {
      Layout == "append",
      ledgerMap.status == "dropped",
      store.droppedMark or Mutation == "drop-without-marker",
      existing != Set(),
      store.extents.get(newest).kind != "tombstone",
      store' = {
        ...store,
        extents: store.extents.set(newest, { kind: nextKind, end: newest, roll: -1 }),
        tombstoneHistory: nextTombstones,
        swept: true,
      },
      refinesAtomic' = refinesAtomic,
      writer' = writer,
      rec' = rec,
      ledgerMap' = ledgerMap,
      closes' = closes,
      ackedAtClose' = ackedAtClose,
      chk' = chk,
      fetch' = fetch,
      recLooped' = recLooped,
      recFollowedRoll' = recFollowedRoll,
    }
  }

  // Append sweeper step 2b (OL-I8, OL-I9): delete any strictly older extent
  // below the newest existing extent.
  action sweepDeleteExtent(e: int): bool = {
    val nextStraggler =
      if (store.straggler == "recreated") "swept"
      else store.straggler
    all {
      Layout == "append",
      ledgerMap.status == "dropped",
      store.droppedMark or Mutation == "drop-without-marker",
      store.extents.get(e).kind != "absent",
      addresses.exists(h => h > e and store.extents.get(h).kind != "absent"),
      store' = {
        ...store,
        extents: store.extents.set(e, emptyExtent(e)),
        swept: true,
        straggler: nextStraggler,
      },
      refinesAtomic' = refinesAtomic,
      writer' = writer,
      rec' = rec,
      ledgerMap' = ledgerMap,
      closes' = closes,
      ackedAtClose' = ackedAtClose,
      chk' = chk,
      fetch' = fetch,
      recLooped' = recLooped,
      recFollowedRoll' = recFollowedRoll,
    }
  }

  // ---------------------------------------------------------------------------
  // Readers (tail checks) and Fetchers (reads and drains below bound)
  // ---------------------------------------------------------------------------

  // CheckAsync step 1 (OL-I5): fix the bound before reading `sealed`.
  action beginCheck(c: int, t: int): bool = {
    val nextChk = chk.set(c, {
      phase: "reading",
      carry: t,
      late: sealOf(ledgerMap.status, store.sealedMark, rec) == "sealed",
      sealed: false,
      tail: t,
    })
    all {
      chk.get(c).phase == "idle",
      t >= 0 and t <= currentBound,
      chk' = nextChk,
      refinesAtomic' = checkAtomic({ ...currentAtomic, checks: nextChk }),
      store' = store,
      writer' = writer,
      rec' = rec,
      ledgerMap' = ledgerMap,
      closes' = closes,
      ackedAtClose' = ackedAtClose,
      fetch' = fetch,
      recLooped' = recLooped,
      recFollowedRoll' = recFollowedRoll,
    }
  }

  // CheckAsync step 2 (OL-I4, OL-I5): read `sealed` marker.
  // Under `check-without-marker`, probes slot/extent seal state instead.
  action answerCheck(c: int): bool = {
    val cur = chk.get(c)
    val isSealed =
      if (Mutation == "check-without-marker")
        if (Layout == "per-slot") store.slots.get(cur.carry) == "seal"
        else addresses.exists(e =>
          val ext = store.extents.get(e)
          (ext.kind == "sealed" or ext.kind == "tombstone") and ext.end == cur.carry
        )
      else store.sealedMark
    val nextChk = chk.set(c, { ...cur, phase: "done", sealed: isSealed, tail: cur.carry })
    all {
      cur.phase == "reading",
      chk' = nextChk,
      refinesAtomic' = checkAtomic({ ...currentAtomic, checks: nextChk }),
      store' = store,
      writer' = writer,
      rec' = rec,
      ledgerMap' = ledgerMap,
      closes' = closes,
      ackedAtClose' = ackedAtClose,
      fetch' = fetch,
      recLooped' = recLooped,
      recFollowedRoll' = recFollowedRoll,
    }
  }

  // Fetcher learns a bound (acknowledged prefix while open, or recorded T once closed).
  action learnTail(f: int, t: int): bool = {
    val nextKnown = fetch.known.set(f, t)
    all {
      fetch.known.get(f) < t,
      t <= currentBound,
      fetch' = { ...fetch, known: nextKnown },
      refinesAtomic' = checkAtomic({ ...currentAtomic, known: nextKnown }),
      store' = store,
      writer' = writer,
      rec' = rec,
      ledgerMap' = ledgerMap,
      closes' = closes,
      ackedAtClose' = ackedAtClose,
      chk' = chk,
      recLooped' = recLooped,
      recFollowedRoll' = recFollowedRoll,
    }
  }

  // Fetcher reads slot p below its learned bound (OL-I7).
  action readSlot(f: int, p: int): bool = {
    val curFRead = fetch.fread.get(f)
    val hasData = slotHasData(store, p)
    val sawKind = if (Layout == "per-slot") store.slots.get(p) else "empty"
    val entry = (p, ledgerMap.status == "open")
    val nextServed = fetch.served.union(Set(entry))
    all {
      curFRead.phase == "idle",
      p < fetch.known.get(f),
      if (hasData) all {
        not(fetch.served.contains(entry)),
        fetch' = { ...fetch, served: nextServed },
        refinesAtomic' = checkAtomic({ ...currentAtomic, served: nextServed }),
      }
      else if (Mutation == "read-ignores-drop") all {
        not(fetch.corruptRead),
        fetch' = { ...fetch, corruptRead: true },
        refinesAtomic' = refinesAtomic,
      }
      else all {
        fetch' = {
          ...fetch,
          fread: fetch.fread.set(f, { phase: "check_drop", pos: p, saw: sawKind }),
        },
        refinesAtomic' = refinesAtomic,
      },
      store' = store,
      writer' = writer,
      rec' = rec,
      ledgerMap' = ledgerMap,
      closes' = closes,
      ackedAtClose' = ackedAtClose,
      chk' = chk,
      recLooped' = recLooped,
      recFollowedRoll' = recFollowedRoll,
    }
  }

  // Fetcher reads `dropped` after seeing empty or seal below its bound (OL-I7).
  action finishReadDropCheck(f: int): bool = {
    val curFRead = fetch.fread.get(f)
    val nextFetch =
      if (store.droppedMark) {
        ...fetch,
        fread: fetch.fread.set(f, initialFRead),
        sawLateSeal: fetch.sawLateSeal or (curFRead.saw == "seal"),
      }
      else {
        ...fetch,
        fread: fetch.fread.set(f, initialFRead),
        corruptRead: true,
      }
    all {
      curFRead.phase == "check_drop",
      fetch' = nextFetch,
      refinesAtomic' = refinesAtomic,
      store' = store,
      writer' = writer,
      rec' = rec,
      ledgerMap' = ledgerMap,
      closes' = closes,
      ackedAtClose' = ackedAtClose,
      chk' = chk,
      recLooped' = recLooped,
      recFollowedRoll' = recFollowedRoll,
    }
  }

  // Fetcher drains below its learned bound.
  action drain(f: int): bool = {
    val nextDrained = fetch.known.get(f)
    all {
      fetch.drained < nextDrained,
      fetch' = { ...fetch, drained: nextDrained },
      refinesAtomic' = checkAtomic({ ...currentAtomic, drained: nextDrained }),
      store' = store,
      writer' = writer,
      rec' = rec,
      ledgerMap' = ledgerMap,
      closes' = closes,
      ackedAtClose' = ackedAtClose,
      chk' = chk,
      recLooped' = recLooped,
      recFollowedRoll' = recFollowedRoll,
    }
  }

  // ---------------------------------------------------------------------------
  // Next-state relation
  // ---------------------------------------------------------------------------

  action step = any {
    acquire,
    all {
      positions != Set(),
      nondet a = positions.oneOf()
      any {
        deliverWrite(a),
        deliverWriteUnknown(a),
        settleWrite(a),
        appendData(a),
        appendDataUnknown(a),
        appendCreateNextEarly(a),
      },
    },
    appendCreateExtent,
    settleAppend,
    appendHitSealed,
    appendRoll,
    appendRollUnknown,
    settleRoll,
    complete,
    all {
      nondet i = Recoverers.oneOf()
      any {
        all {
          nondet ev = 0.to(Slots).oneOf()
          beginRecovery(i, ev),
        },
        createSealedMarker(i),
        listWindow(i),
        all {
          nondet a = addresses.oneOf()
          settleSeal(i, a),
        },
        finishWindow(i),
        finishFenceAbove(i),
        appendListExtent(i),
        appendRecoveryCreateExtent(i),
        appendSealExtent(i),
        retryRecovery(i),
        finishRecovery(i),
        closeLedger(i),
      },
    },
    all {
      nondet a = addresses.oneOf()
      any {
        deliverSeal(a),
        sweepDelete(a),
        sweepDeleteExtent(a),
      },
    },
    dropLedger,
    sweepMarkDrop,
    sweepTombstone,
    all {
      Readers != Set(),
      nondet c = Readers.oneOf()
      any {
        all {
          nondet t = 0.to(Slots).oneOf()
          beginCheck(c, t),
        },
        answerCheck(c),
      },
    },
    all {
      Fetchers != Set(),
      nondet f = Fetchers.oneOf()
      any {
        all {
          nondet t = 0.to(Slots).oneOf()
          learnTail(f, t),
        },
        all {
          positions != Set(),
          nondet p = positions.oneOf()
          readSlot(f, p),
        },
        finishReadDropCheck(f),
        drain(f),
      },
    },
  }

  // ---------------------------------------------------------------------------
  // Invariants
  // ---------------------------------------------------------------------------

  val TypeOK: bool = and {
    addresses.forall(a =>
      val c = store.slots.get(a)
      c == "empty" or c == "data" or c == "seal"
    ),
    store.slots.get(Slots) != "data",
    addresses.forall(e =>
      val ext = store.extents.get(e)
      and {
        ext.kind == "absent" or ext.kind == "appendable" or ext.kind == "sealed" or ext.kind == "tombstone",
        ext.end >= e and ext.end <= Slots,
        ext.roll >= -1 and ext.roll <= Slots,
      }
    ),
    store.tombstoneHistory.subseteq(addresses),
    store.sealIssued.subseteq(addresses),
    store.sealHistory.subseteq(addresses),
    store.straggler == "none" or store.straggler == "recreated" or store.straggler == "swept",
    writer.nextPos >= 0 and writer.nextPos <= Slots,
    writer.completed >= 0 and writer.completed <= writer.nextPos,
    writer.nextPos - writer.completed <= Window,
    writer.written.subseteq(positions),
    writer.sealed.subseteq(positions),
    writer.curExt >= 0 and writer.curExt <= Slots,
    writer.nextSlot >= 0 and writer.nextSlot <= Slots,
    Recoverers.forall(i =>
      val r = rec.get(i)
      and {
        r.phase == "idle" or r.phase == "mark_seal" or r.phase == "listing" or
          r.phase == "fencing" or r.phase == "fence_above" or r.phase == "sealing_extent" or
          r.phase == "check_drop" or r.phase == "closing" or r.phase == "closed" or
          r.phase == "superseded" or r.phase == "dropped",
        r.evidence >= 0 and r.evidence <= Slots,
        r.start >= 0 and r.start <= Slots,
        r.pending.subseteq(addresses),
        r.data.subseteq(addresses),
        r.seals.subseteq(addresses),
        r.end >= -1 and r.end <= Slots,
      }
    ),
    ledgerMap.status == "open" or ledgerMap.status == "closed" or ledgerMap.status == "dropped",
    ledgerMap.end >= -1 and ledgerMap.end <= Slots,
    closes.subseteq(addresses),
    ackedAtClose >= 0 and ackedAtClose <= Slots,
    Readers.forall(c =>
      val x = chk.get(c)
      (x.phase == "idle" or x.phase == "reading" or x.phase == "done") and
      x.tail >= 0 and x.tail <= Slots
    ),
    Fetchers.forall(f =>
      val fr = fetch.fread.get(f)
      and {
        fetch.known.get(f) >= 0 and fetch.known.get(f) <= Slots,
        fr.phase == "idle" or fr.phase == "check_drop",
        fr.pos >= 0 and fr.pos < Slots,
        fr.saw == "none" or fr.saw == "empty" or fr.saw == "seal",
      }
    ),
    fetch.served.forall(entry => entry._1 >= 0 and entry._1 < Slots),
    fetch.drained >= 0 and fetch.drained <= Slots,
  }

  // No acknowledged slot is lost by recovery and close, and before a sweep
  // every acknowledged slot holds its batch (LL-I2, OL-I2, OL-I3).
  val AcknowledgedSurvive: bool = and {
    not(store.swept) implies rangeSet(0, writer.completed - 1).forall(a => slotHasData(store, a)),
    ended implies (ackedAtClose <= ledgerMap.end),
  }

  // A deposed writer never acknowledges a slot at or above T, including after
  // drop and sweep (LL-I3, LL-I9, OL-I3, OL-I8).
  val NoAcknowledgementAtOrAboveEnd: bool =
    ended implies (writer.completed <= ledgerMap.end)

  // Before any sweep, every slot below a recovery's candidate end or the
  // recorded T holds its batch (LL-I3, OL-I3, OL-I6).
  val DecidedBelowEnd: bool =
    not(store.swept) implies and {
      Recoverers.forall(i =>
        val r = rec.get(i)
        isFinished(r.phase) implies rangeSet(0, r.end - 1).forall(a => slotHasData(store, a))
      ),
      ended implies rangeSet(0, ledgerMap.end - 1).forall(a => slotHasData(store, a)),
    }

  pure def endFenced(st: StoreState, t: int): bool =
    if (Layout == "per-slot") st.slots.get(t) == "seal"
    else {
      val existing = addresses.filter(e => st.extents.get(e).kind != "absent")
      existing.exists(f =>
        val ext = st.extents.get(f)
        f <= t and (
          (ext.kind == "tombstone" and f == maxOf(existing)) or
          (ext.kind == "sealed" and ext.end == t and ext.roll == -1)
        )
      )
    }

  // Every candidate end and the recorded T are fenced forever, including after
  // sweeps (LL-I3, LL-I4, OL-I3, OL-I8).
  val EndSealed: bool = and {
    Recoverers.forall(i =>
      val r = rec.get(i)
      isFinished(r.phase) implies endFenced(store, r.end)
    ),
    ended implies endFenced(store, ledgerMap.end),
  }

  // Every SEAL object (per-slot) or fence/tombstone extent (append) persists
  // for the store's life (OL-I8).
  val SealsPermanent: bool =
    if (Layout == "per-slot")
      store.sealHistory.forall(a => store.slots.get(a) == "seal")
    else and {
      store.tombstoneHistory.forall(e => store.extents.get(e).kind == "tombstone"),
      (store.sealHistory != Set()) implies (
        val topKind = store.extents.get(maxOf(store.sealHistory)).kind
        topKind == "sealed" or topKind == "tombstone"
      ),
    }

  // Concurrent recoveries that return an end agree on the same T (OL-I3, OL-I6).
  val RecoveriesAgree: bool = and {
    closes.size() <= 1,
    Recoverers.forall(i => Recoverers.forall(j =>
      val ri = rec.get(i)
      val rj = rec.get(j)
      (isFinished(ri.phase) and isFinished(rj.phase)) implies (ri.end == rj.end)
    )),
    ended implies Recoverers.forall(i =>
      val r = rec.get(i)
      isFinished(r.phase) implies (r.end == ledgerMap.end)
    ),
  }

  // No durable answer is ever revoked: every Written slot holds Data until
  // swept, and every Sealed answer stays fenced forever (LL-I4, LL-I8, OL-I2, OL-I8).
  val NoRevokedAnswer: bool = and {
    not(store.swept) implies writer.written.forall(a => slotHasData(store, a)),
    if (Layout == "per-slot")
      writer.sealed.forall(a => store.slots.get(a) == "seal")
    else
      (writer.sealed != Set()) implies addresses.exists(e =>
        val k = store.extents.get(e).kind
        k == "sealed" or k == "tombstone"
      ),
  }

  // A tail check begun once a recovery decided its end reports sealed (LL-I5, OL-I4, OL-I5).
  val OpenCheckSound: bool =
    Readers.forall(c =>
      val x = chk.get(c)
      (x.phase == "done" and x.late) implies x.sealed
    )

  // No tail check reports a tail past T (LL-I5, LL-I7).
  val CheckTailBelowEnd: bool =
    ended implies Readers.forall(c =>
      val x = chk.get(c)
      (x.phase == "done") implies (x.tail <= ledgerMap.end)
    )

  // Every slot a fetcher reads or drains below its learned bound is decided (LL-I6).
  pure def decided(isEnded: bool, endPos: int, completed: int, p: int): bool =
    if (isEnded) p < endPos else p < completed

  val BelowTailDecided: bool =
    fetch.served.map(entry => entry._1)
      .union(positions.filter(q => q < fetch.drained))
      .forall(p => decided(ended, ledgerMap.end, writer.completed, p))

  // No read below a fetcher's bound fails with corruption: absent slots and
  // late SEALs after a sweep are answered Dropped via the drop marker (OL-I7).
  val NoFalseCorruption: bool = not(fetch.corruptRead)

  // Only revoked ledgers are swept, and the drop marker precedes any delete (OL-I4, OL-I9).
  val OnlySweeperDeletes: bool = and {
    (ledgerMap.status != "dropped") implies (not(store.droppedMark) and not(store.swept)),
    store.swept implies store.droppedMark,
  }

  // Every batch present in the store was issued by the writer (OL-I9).
  val StoreBatchesIssued: bool =
    positions.forall(a => slotHasData(store, a) implies (a < writer.nextPos))

  // Append-layout extent chain invariant (OL-I10): before sweep, existing extents
  // start at 0 and each consecutive pair chains by ROLL(next) and end == next;
  // every tombstone has end == e and roll == -1.
  val AppendExtentShape: bool =
    (Layout == "append") implies and {
      addresses.forall(e =>
        val ext = store.extents.get(e)
        (ext.kind != "absent") implies and {
          ext.roll == -1 or ext.roll == ext.end,
          (ext.kind == "tombstone") implies (ext.end == e and ext.roll == -1),
        }
      ),
      not(store.swept) implies (
        val existing = addresses.filter(e => store.extents.get(e).kind != "absent")
        and {
          (existing != Set()) implies (minOf(existing, Slots) == 0),
          existing.forall(e1 => existing.forall(e2 =>
            (e1 < e2 and not(existing.exists(m => e1 < m and m < e2))) implies
              (store.extents.get(e1).roll == e2 and store.extents.get(e1).end == e2)
          )),
        }
      ),
    }

  // Step-by-step refinement of AtomicLedger.qnt.
  val RefinesAtomicLedger: bool = refinesAtomic

  // ---------------------------------------------------------------------------
  // Reachability witnesses (negated: TLC must produce a counterexample)
  // ---------------------------------------------------------------------------

  // A delayed SEAL create lands on an emptied slot below T after the sweep.
  val NeverLateSealLanded: bool =
    not(store.droppedMark and ended and rangeSet(0, ledgerMap.end - 1).exists(a => store.slots.get(a) == "seal"))

  // A reader with a stale map meets a late SEAL on an emptied slot below T and
  // answers Dropped via the `dropped` marker (object-ledgers.md §1, OL-I7).
  val NeverReadSeesLateSeal: bool = not(fetch.sawLateSeal)

  // A delayed write re-creates a batch after a sweep, and a later sweep deletes it (OL-I9).
  val NeverStragglerSwept: bool = store.straggler != "swept"

  // A recovery races a drop and answers Dropped (OL-I6).
  val NeverRecoveryDropped: bool =
    not(Recoverers.exists(i => rec.get(i).phase == "dropped"))

  // Two concurrent recoveries both finish and agree on the same end (OL-I3).
  val NeverBothRecoveriesFinish: bool =
    not(Recoverers.exists(i => Recoverers.exists(j =>
      i != j and isFinished(rec.get(i).phase) and isFinished(rec.get(j).phase)
    )))

  // The writer fills recovery's window, forcing recovery to loop and list above it.
  val NeverRecoveryLoops: bool = not(recLooped)

  // The writer acknowledges a lower slot after a higher slot answered Sealed (K >= 2).
  val NeverAckAfterSealed: bool = not(writer.ackAfterSealed)

  // A close records T above the writer's acknowledged prefix, and the writer later
  // acknowledges below T.
  val NeverClosedAboveAcked: bool = not(ended and ledgerMap.end > ackedAtClose)
  val NeverAcknowledgedAfterClose: bool = not(ended and writer.completed > ackedAtClose)

  // A batch landed above T (and never completes).
  val NeverDataAboveEnd: bool =
    not(ended and positions.exists(a => a > ledgerMap.end and store.slots.get(a) == "data"))

  // Append layout: recovery sealed an extent containing a ROLL frame and
  // followed it to the successor extent (OL-I10).
  val NeverRecoveryFollowedRoll: bool = not(recFollowedRoll)

  // Append layout: a rolled ledger was dropped, its newest extent tombstoned,
  // and its older extent deleted (OL-I8).
  val NeverRolledAndTombstoned: bool =
    not(store.droppedMark and addresses.exists(e =>
      e > 0 and store.extents.get(e).kind == "tombstone" and store.extents.get(0).kind == "absent"
    ))

  // Append layout: the writer coalesced two or more slots into a single append.
  val NeverCoalescedAppend: bool = not(writer.coalesced)
}
