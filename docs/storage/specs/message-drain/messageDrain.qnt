// -*- mode: Bluespec; -*-
/**
 * Generic L2 atomic journal message draining (`RowKind::Message = 3`) and single-pass
 * `DrainScan` folding with deterministic idempotent durable sink commits
 * (ST12, LG-I15, LG-I16, JS-I11, JS-I12).
 *
 * Models Roux's L2 `clog-ledgers` + `clog-journals` + `clog-archive` (`SlateDbDrainWorker`:
 * `DrainScan` -> `SegmentMessageFolds::commit` -> atomic SlateDB `WriteBatch` advancing
 * `0x00wm/{L}` -> `LedgerDropper` `Drop(L)`), independent of any L4 channel consumer:
 *   - Unversioned `RowKind::Message = 3` rows attached atomically to journal writes:
 *     foldable channel operations `Emit` (`"E0"`, `"E1"`) and `Retract` (`"R0"`, `"R1"`)
 *     across two sink partitions (`0` and `1`), journal-only rows (`"K"`), and message-only
 *     rows with zero archive rows (`"M0"`).
 *   - Open-ledger primary volatile `MessageSession` (`on_batch_durable` in strict slot order,
 *     `primaryConsume`) vs primary crash losing volatile session state and closing `L` at its
 *     decided tail.
 *   - Single-pass `DrainScan` over each closed segment `[a, b)` folding archive rows and
 *     `RowKind::Message` rows together under `DrainScan`'s shared `DrainMemoryBytes`
 *     `ByteBudget` (including `OverBudget` -> `MessageFold::clear()` -> `Alone` rescan).
 *   - Per-segment `MessageProcessor::commit()` publishing 0 durable sink batches when a
 *     segment's fold clears to empty, and 1 deterministic idempotent durable sink batch per
 *     surviving `(seg, partition)` strictly before SlateDB advances `0x00wm/{L}` past the
 *     segment (including message-only segments with 0 archive rows).
 *   - Cross-segment `Emit`-then-`Retract`, concurrent racing drainers colliding on
 *     deterministic sink batch keys, and drainer crashes at every step.
 *
 * Mutants disable one guard at a time and are verified via `../check-mutants.sh`.
 */
module messageDrain {
  // Slot operations per segment:
  //   "K"  = journal-only row
  //   "E0" = journal row + Emit(partition 0)
  //   "R0" = journal row + Retract(partition 0)
  //   "M0" = message-only Emit(partition 0), 0 archive rows
  //   "E1" = journal row + Emit(partition 1)
  //   "R1" = journal row + Retract(partition 1)
  const Seg0: List[str]
  const Seg1: List[str]
  // Number of concurrent drainers (1 or 2).
  const Drainers: int
  // Maximum total primary + drainer crashes and OverBudget rescans.
  const MaxCrashes: int

  pure val NumSegments: int = if (Seg1.length() > 0) 2 else 1
  pure val AllDrainers: Set[int] = 1.to(Drainers)
  pure val AllSegments: Set[int] = 0.to(NumSegments - 1)
  pure val AllPartitions: Set[int] = Set(0, 1)

  type BatchKey = {
    seg: int,
    partition: int,
  }

  pure def segSlots(s: int): List[str] = if (s == 0) Seg0 else Seg1

  pure def foldSurvivingPartition(slots: List[str], count: int, p: int): bool =
    range(0, count).foldl(false, (acc, i) =>
      val op = slots[i]
      if (p == 0) {
        if (op == "E0" or op == "M0") true
        else if (op == "R0") false
        else acc
      } else {
        if (op == "E1") true
        else if (op == "R1") false
        else acc
      })

  pure def foldSurvivingPartitions(slots: List[str], count: int): Set[int] =
    AllPartitions.filter(p => foldSurvivingPartition(slots, count, p))

  pure def hasArchive(slots: List[str], count: int): bool =
    range(0, count).foldl(false, (acc, i) => acc or slots[i] != "M0")

  pure def hasMessages(slots: List[str], count: int): bool =
    range(0, count).foldl(false, (acc, i) => acc or slots[i] != "K")

  // Ledger & primary volatile MessageSession state.
  var ledgerState: str                // "open" | "closed" | "dropped"
  var writtenSeg: int                 // 0..NumSegments
  var writtenSlot: int                // 0..segSlots(writtenSeg).length()
  var primaryAlive: bool
  var primaryPending: Set[int]        // partitions held in open-ledger volatile MessageSession
  var logicalPending: Set[int]        // partitions durably emitted and not yet consumed or retracted
  var delivered: Set[int]
  var crashes: int

  // SlateDB archive drain & deterministic durable sink state.
  var slateWatermark: int             // consecutive segments 0..slateWatermark-1 committed in 0x00wm/{L}
  var slateComplete: bool             // 0x00wm/{L} committed with complete = 1
  var committedBatches: Set[BatchKey] // durable deterministic sink batches keyed by (seg, partition)
  var sinkWrites: int                 // total sink-creation writes (equals committedBatches.size() under deterministic keys)

  // Per-drainer single-pass DrainScan state.
  var drainerPhase: int -> str           // "idle" | "scanned" | "committed"
  var drainerSeg: int -> int             // -1 when idle, else segment index
  var drainerPartitions: int -> Set[int] // surviving partitions folded during DrainScan
  var racedOnSameSegment: bool           // witness flag: 2 drainers committed the same deterministic batch key
  var overBudgetRescanned: bool          // witness flag: DrainScan reset folds on OverBudget and rescanned

  def segWrittenLen(s: int): int =
    if (s < writtenSeg) segSlots(s).length()
    else if (s == writtenSeg) writtenSlot
    else 0

  def segSurvivingPartitions(s: int): Set[int] = foldSurvivingPartitions(segSlots(s), segWrittenLen(s))
  def segHasArchive(s: int): bool = hasArchive(segSlots(s), segWrittenLen(s))
  def segHasMessages(s: int): bool = hasMessages(segSlots(s), segWrittenLen(s))

  def writtenSegments: Set[int] = AllSegments.filter(s => segWrittenLen(s) > 0)

  def segDrainable(s: int): bool =
    (s < writtenSeg) or (ledgerState == "closed" and s < NumSegments and segWrittenLen(s) > 0)

  def allCoveredByWatermark(nextWatermark: int): bool =
    writtenSegments.forall(s => s < nextWatermark)

  action keepArchive = all {
    slateWatermark' = slateWatermark,
    slateComplete' = slateComplete,
    committedBatches' = committedBatches,
    sinkWrites' = sinkWrites,
  }

  action keepDrainers = all {
    drainerPhase' = drainerPhase,
    drainerSeg' = drainerSeg,
    drainerPartitions' = drainerPartitions,
    racedOnSameSegment' = racedOnSameSegment,
    overBudgetRescanned' = overBudgetRescanned,
  }

  action keepPrimary = all {
    ledgerState' = ledgerState,
    writtenSeg' = writtenSeg,
    writtenSlot' = writtenSlot,
    primaryAlive' = primaryAlive,
    primaryPending' = primaryPending,
    logicalPending' = logicalPending,
    delivered' = delivered,
    crashes' = crashes,
  }

  action init = all {
    ledgerState' = "open",
    writtenSeg' = 0,
    writtenSlot' = 0,
    primaryAlive' = true,
    primaryPending' = Set(),
    logicalPending' = Set(),
    delivered' = Set(),
    crashes' = 0,
    slateWatermark' = 0,
    slateComplete' = false,
    committedBatches' = Set(),
    sinkWrites' = 0,
    drainerPhase' = AllDrainers.mapBy(_ => "idle"),
    drainerSeg' = AllDrainers.mapBy(_ => -1),
    drainerPartitions' = AllDrainers.mapBy(_ => Set()),
    racedOnSameSegment' = false,
    overBudgetRescanned' = false,
  }

  // Primary durably writes the next slot (atomic journal row + optional unversioned RowKind::Message)
  // and invokes MessageSession::on_batch_durable in strict slot order before warm-state apply (JS-I11, JS-I12).
  action primaryWrite = {
    val slots = segSlots(writtenSeg)
    val op = slots[writtenSlot]
    val nextSlot = writtenSlot + 1
    val advanceSeg = nextSlot == slots.length()
    val nextPrimary =
      if (op == "E0" or op == "M0") primaryPending.union(Set(0))
      else if (op == "R0") primaryPending.exclude(Set(0))
      else if (op == "E1") primaryPending.union(Set(1))
      else if (op == "R1") primaryPending.exclude(Set(1))
      else primaryPending
    val nextLogical =
      if (op == "E0" or op == "M0") logicalPending.union(Set(0))
      else if (op == "R0") logicalPending.exclude(Set(0))
      else if (op == "E1") logicalPending.union(Set(1))
      else if (op == "R1") logicalPending.exclude(Set(1))
      else logicalPending
    all {
      ledgerState == "open",
      primaryAlive,
      writtenSeg < NumSegments,
      writtenSeg' = if (advanceSeg) writtenSeg + 1 else writtenSeg,
      writtenSlot' = if (advanceSeg) 0 else nextSlot,
      primaryPending' = nextPrimary,
      logicalPending' = nextLogical,
      ledgerState' = ledgerState,
      primaryAlive' = primaryAlive,
      delivered' = delivered,
      crashes' = crashes,
      keepArchive,
      keepDrainers,
    }
  }

  // Primary's volatile MessageSession consumes/delivers a message while L is open (zero sink writes).
  action primaryConsume(p: int): bool = all {
    ledgerState == "open",
    primaryAlive,
    p.in(primaryPending),
    primaryPending' = primaryPending.exclude(Set(p)),
    logicalPending' = logicalPending.exclude(Set(p)),
    delivered' = delivered.union(Set(p)),
    ledgerState' = ledgerState,
    writtenSeg' = writtenSeg,
    writtenSlot' = writtenSlot,
    primaryAlive' = primaryAlive,
    crashes' = crashes,
    keepArchive,
    keepDrainers,
  }

  // Primary crashes while open: volatile MessageSession state is lost and ledger closes at tail.
  action primaryCrash = all {
    ledgerState == "open",
    primaryAlive,
    crashes < MaxCrashes,
    writtenSegments.size() > 0,
    primaryAlive' = false,
    primaryPending' = Set(),
    ledgerState' = "closed",
    crashes' = crashes + 1,
    writtenSeg' = writtenSeg,
    writtenSlot' = writtenSlot,
    logicalPending' = logicalPending,
    delivered' = delivered,
    keepArchive,
    keepDrainers,
  }

  // Graceful roll / close of the ledger once at least one slot has been written.
  action closeLedger = all {
    ledgerState == "open",
    writtenSegments.size() > 0,
    ledgerState' = "closed",
    primaryPending' = Set(),
    writtenSeg' = writtenSeg,
    writtenSlot' = writtenSlot,
    primaryAlive' = primaryAlive,
    logicalPending' = logicalPending,
    delivered' = delivered,
    crashes' = crashes,
    keepArchive,
    keepDrainers,
  }

  // Single-pass DrainScan over closed segment s: validates and folds archive rows and
  // RowKind::Message rows in one pass under DrainMemoryBytes.
  action scanSegment(d: int, s: int): bool = all {
    ledgerState != "dropped",
    drainerPhase.get(d) == "idle",
    segDrainable(s),
    s == slateWatermark,
    not(slateComplete),
    drainerPhase' = drainerPhase.set(d, "scanned"),
    drainerSeg' = drainerSeg.set(d, s),
    drainerPartitions' = drainerPartitions.set(d, segSurvivingPartitions(s)),
    racedOnSameSegment' = racedOnSameSegment,
    overBudgetRescanned' = overBudgetRescanned,
    keepPrimary,
    keepArchive,
  }

  // DrainScan hits OverBudget: calls MessageFold::clear(), releases ByteBudget, and returns
  // to idle to acquire DrainMemoryBytes alone and rescan segment s cleanly.
  action overBudgetReset(d: int): bool = all {
    drainerPhase.get(d) == "scanned",
    crashes < MaxCrashes,
    drainerPhase' = drainerPhase.set(d, "idle"),
    drainerSeg' = drainerSeg.set(d, -1),
    drainerPartitions' = drainerPartitions.set(d, Set()),
    overBudgetRescanned' = true,
    racedOnSameSegment' = racedOnSameSegment,
    crashes' = crashes + 1,
    ledgerState' = ledgerState,
    writtenSeg' = writtenSeg,
    writtenSlot' = writtenSlot,
    primaryAlive' = primaryAlive,
    primaryPending' = primaryPending,
    logicalPending' = logicalPending,
    delivered' = delivered,
    keepArchive,
  }

  // MessageProcessor::commit(): performs 0 durable sink writes when the segment fold is empty,
  // or publishes 1 deterministic idempotent sink batch per surviving (seg, partition).
  action commitBatches(d: int): bool = {
    val s = drainerSeg.get(d)
    val parts = drainerPartitions.get(d)
    val targets = parts.map(p => { seg: s, partition: p })
    val newBatches = targets.exclude(committedBatches)
    val collided = targets.intersect(committedBatches).size() > 0
    all {
      ledgerState != "dropped",
      drainerPhase.get(d) == "scanned",
      drainerPhase' = drainerPhase.set(d, "committed"),
      committedBatches' = committedBatches.union(targets),
      sinkWrites' = sinkWrites + newBatches.size(),
      racedOnSameSegment' = (racedOnSameSegment or collided),
      slateWatermark' = slateWatermark,
      slateComplete' = slateComplete,
      drainerSeg' = drainerSeg,
      drainerPartitions' = drainerPartitions,
      overBudgetRescanned' = overBudgetRescanned,
      keepPrimary,
    }
  }

  // SlateDbDrainWorker commits one atomic WriteBatch advancing 0x00wm/{L} to s + 1
  // (marking complete = 1 if closed and all written segments are covered) ONLY AFTER
  // SegmentMessageFolds::commit() completed durably (LG-I16).
  action commitSegment(d: int): bool = {
    val s = drainerSeg.get(d)
    all {
      ledgerState != "dropped",
      drainerPhase.get(d) == "committed",
      s == slateWatermark,
      not(slateComplete),
      slateWatermark' = s + 1,
      slateComplete' = (slateComplete or (ledgerState == "closed" and allCoveredByWatermark(s + 1))),
      committedBatches' = committedBatches,
      sinkWrites' = sinkWrites,
      drainerPhase' = drainerPhase.set(d, "idle"),
      drainerSeg' = drainerSeg.set(d, -1),
      drainerPartitions' = drainerPartitions.set(d, Set()),
      racedOnSameSegment' = racedOnSameSegment,
      overBudgetRescanned' = overBudgetRescanned,
      keepPrimary,
    }
  }

  // SlateDB watermark == T completion: when all written segments were already drained (and their
  // messages already committed) while L was open, closeLedger allows marking complete = 1 directly.
  action slateMarkClosedComplete = all {
    ledgerState == "closed",
    not(slateComplete),
    allCoveredByWatermark(slateWatermark),
    slateComplete' = true,
    slateWatermark' = slateWatermark,
    committedBatches' = committedBatches,
    sinkWrites' = sinkWrites,
    keepPrimary,
    keepDrainers,
  }

  // Drainer crashes mid-scan or mid-commit; already-committed deterministic sink batches survive.
  action drainerCrash(d: int): bool = all {
    drainerPhase.get(d) != "idle",
    crashes < MaxCrashes,
    drainerPhase' = drainerPhase.set(d, "idle"),
    drainerSeg' = drainerSeg.set(d, -1),
    drainerPartitions' = drainerPartitions.set(d, Set()),
    crashes' = crashes + 1,
    ledgerState' = ledgerState,
    writtenSeg' = writtenSeg,
    writtenSlot' = writtenSlot,
    primaryAlive' = primaryAlive,
    primaryPending' = primaryPending,
    logicalPending' = logicalPending,
    delivered' = delivered,
    racedOnSameSegment' = racedOnSameSegment,
    overBudgetRescanned' = overBudgetRescanned,
    keepArchive,
  }

  // LedgerDropper submits LedgerMapChange::Drop(L) once SlateDB marked the closed ledger complete.
  action dropLedger = all {
    ledgerState == "closed",
    slateComplete,
    ledgerState' = "dropped",
    writtenSeg' = writtenSeg,
    writtenSlot' = writtenSlot,
    primaryAlive' = primaryAlive,
    primaryPending' = primaryPending,
    logicalPending' = logicalPending,
    delivered' = delivered,
    crashes' = crashes,
    keepArchive,
    keepDrainers,
  }

  // A durable sink batch is consumed and delivers its messages to the downstream consumer.
  action sinkDeliver(b: BatchKey): bool = all {
    b.in(committedBatches),
    logicalPending' = logicalPending.exclude(Set(b.partition)),
    delivered' = delivered.union(Set(b.partition)),
    ledgerState' = ledgerState,
    writtenSeg' = writtenSeg,
    writtenSlot' = writtenSlot,
    primaryAlive' = primaryAlive,
    primaryPending' = primaryPending,
    crashes' = crashes,
    keepArchive,
    keepDrainers,
  }

  // Stutter keeps infinite traces well-defined for TLC temporal liveness checks.
  action stutter = all {
    ledgerState == "dropped",
    keepPrimary,
    keepArchive,
    keepDrainers,
  }

  action step = any {
    primaryWrite,
    { nondet p = AllPartitions.oneOf(); primaryConsume(p) },
    primaryCrash,
    closeLedger,
    slateMarkClosedComplete,
    dropLedger,
    stutter,
    all {
      nondet d = AllDrainers.oneOf()
      any {
        { nondet s = AllSegments.oneOf(); scanSegment(d, s) },
        overBudgetReset(d),
        commitBatches(d),
        commitSegment(d),
        drainerCrash(d),
      },
    },
    { nondet b = committedBatches.oneOf(); sinkDeliver(b) },
  }

  // ---------------------------------------------------------------------------
  // Safety invariants
  // ---------------------------------------------------------------------------

  val TypeOK: bool = and {
    ledgerState.in(Set("open", "closed", "dropped")),
    writtenSeg >= 0 and writtenSeg <= NumSegments,
    writtenSlot >= 0,
    crashes >= 0 and crashes <= MaxCrashes,
    slateWatermark >= 0 and slateWatermark <= NumSegments,
    sinkWrites >= 0,
    AllDrainers.forall(d => drainerPhase.get(d).in(Set("idle", "scanned", "committed"))),
  }

  // Every durably accepted undelivered message is preserved by the live open primary's
  // MessageSession, an undropped ledger, or a durable sink batch for its partition.
  val MessagesPreserved: bool =
    logicalPending.forall(p => or {
      primaryAlive and p.in(primaryPending),
      ledgerState != "dropped",
      committedBatches.exists(b => b.partition == p),
    })

  // LG-I16: SlateDB watermark never advances past a segment, and a closed ledger is never
  // dropped, before every surviving partition of that segment has committed its durable sink batch.
  val NoDropBeforeMessagesCommitted: bool = and {
    AllSegments.forall(s =>
      (s < slateWatermark) implies segSurvivingPartitions(s).forall(p => { seg: s, partition: p }.in(committedBatches))
    ),
    (ledgerState == "dropped") implies writtenSegments.forall(s =>
      segSurvivingPartitions(s).forall(p => { seg: s, partition: p }.in(committedBatches))
    ),
  }

  // A segment whose message fold is empty for a partition writes zero durable sink batches for it.
  val EmptyFoldWritesNothing: bool =
    AllSegments.forall(s =>
      AllPartitions.forall(p =>
        not(p.in(segSurvivingPartitions(s))) implies not({ seg: s, partition: p }.in(committedBatches))
      )
    )

  // Deterministic sink batch keys guarantee racing drainers and crash retries create
  // at most one durable sink batch per (segment, partition).
  val IdempotentCommit: bool = sinkWrites == committedBatches.size()

  val safety: bool = and {
    TypeOK,
    MessagesPreserved,
    NoDropBeforeMessagesCommitted,
    EmptyFoldWritesNothing,
    IdempotentCommit,
  }

  // ---------------------------------------------------------------------------
  // Weak-fairness liveness properties (verified via TLC)
  // ---------------------------------------------------------------------------

  val stateVars = (
    ledgerState, writtenSeg, writtenSlot, primaryAlive, primaryPending,
    logicalPending, delivered, crashes, slateWatermark, slateComplete,
    committedBatches, sinkWrites, drainerPhase, drainerSeg,
    drainerPartitions, racedOnSameSegment, overBudgetRescanned
  )

  temporal Fairness = and {
    primaryWrite.weakFair(stateVars),
    closeLedger.weakFair(stateVars),
    slateMarkClosedComplete.weakFair(stateVars),
    dropLedger.weakFair(stateVars),
    AllDrainers.forall(d => AllSegments.forall(s => scanSegment(d, s).weakFair(stateVars))),
    AllDrainers.forall(d => commitBatches(d).weakFair(stateVars)),
    AllDrainers.forall(d => commitSegment(d).weakFair(stateVars)),
    AllSegments.forall(s => AllPartitions.forall(p => sinkDeliver({ seg: s, partition: p }).weakFair(stateVars))),
  }

  temporal EventuallyDrainedAndDropped =
    Fairness implies eventually(ledgerState == "dropped")

  temporal EventuallyMessagesDelivered =
    Fairness implies always(logicalPending.size() > 0 implies eventually(logicalPending.size() == 0))

  // ---------------------------------------------------------------------------
  // Non-vacuity witnesses (each negated so TLC produces a concrete counterexample)
  // ---------------------------------------------------------------------------

  // Primary crashes with a surviving emitted message; DrainScan commits sink batch, L drops, sink delivers.
  val NeverSurvivingCrashMessage: bool =
    not(not(primaryAlive) and ledgerState == "dropped" and delivered.size() > 0 and committedBatches.size() > 0)

  // Message emitted then retracted within one segment drains and drops with zero sink writes.
  val NeverRetractedInSegment: bool =
    not(ledgerState == "dropped" and committedBatches.size() == 0 and sinkWrites == 0)

  // Message-only segment (0 archive rows) still commits its sink batch and advances watermark.
  val NeverMessageOnlySegment: bool =
    not(ledgerState == "dropped" and writtenSegments.exists(s => not(segHasArchive(s)) and segHasMessages(s)) and committedBatches.size() > 0)

  // Cross-segment Emit-then-Retract: segment 0 commits its batch while segment 1 writes 0 batches.
  val NeverCrossSegmentEmitThenRetract: bool =
    not(NumSegments == 2 and ledgerState == "dropped" and { seg: 0, partition: 0 }.in(committedBatches) and not({ seg: 1, partition: 0 }.in(committedBatches)))

  // One segment with surviving messages in 2 distinct partitions commits 2 deterministic sink batches.
  val NeverMultiPartitionSegment: bool =
    not(ledgerState == "dropped" and { seg: 0, partition: 0 }.in(committedBatches) and { seg: 0, partition: 1 }.in(committedBatches))

  // Two racing drainers commit the same deterministic batch key with zero duplicate creations.
  val NeverRacingDrainers: bool =
    not(racedOnSameSegment and sinkWrites == committedBatches.size())

  // ---------------------------------------------------------------------------
  // Guard-disabling mutants (verified via ../check-mutants.sh)
  // ---------------------------------------------------------------------------

  // Mutant 1: SlateDbDrainWorker advances watermark / marks complete before committing messages.
  // breaks: NoDropBeforeMessagesCommitted
  action mutantCommitBeforeMessagesCommitted = any {
    step,
    {
      nondet d = AllDrainers.oneOf()
      val s = drainerSeg.get(d)
      all {
        ledgerState != "dropped",
        drainerPhase.get(d) == "scanned", // BUG: skips commitBatches before SlateDB commit!
        s == slateWatermark,
        not(slateComplete),
        slateWatermark' = s + 1,
        slateComplete' = (slateComplete or (ledgerState == "closed" and allCoveredByWatermark(s + 1))),
        committedBatches' = committedBatches,
        sinkWrites' = sinkWrites,
        drainerPhase' = drainerPhase.set(d, "idle"),
        drainerSeg' = drainerSeg.set(d, -1),
        drainerPartitions' = drainerPartitions.set(d, Set()),
        racedOnSameSegment' = racedOnSameSegment,
        overBudgetRescanned' = overBudgetRescanned,
        keepPrimary,
      }
    },
  }

  // Mutant 2: DrainScan silently drops an unknown/unmatched channel instead of failing closed.
  // breaks: MessagesPreserved
  action mutantSkipUnknownChannel = any {
    step,
    {
      nondet d = AllDrainers.oneOf()
      nondet s = AllSegments.oneOf()
      all {
        ledgerState != "dropped",
        drainerPhase.get(d) == "idle",
        segDrainable(s),
        s == slateWatermark,
        not(slateComplete),
        segSurvivingPartitions(s).size() > 0,
        // BUG: drops surviving messages as empty instead of failing closed!
        drainerPhase' = drainerPhase.set(d, "scanned"),
        drainerSeg' = drainerSeg.set(d, s),
        drainerPartitions' = drainerPartitions.set(d, Set()),
        racedOnSameSegment' = racedOnSameSegment,
        overBudgetRescanned' = overBudgetRescanned,
        keepPrimary,
        keepArchive,
      }
    },
  }

  // Mutant 3: Non-deterministic batch keys cause racing drainers to create duplicate sink writes.
  // breaks: IdempotentCommit
  action mutantNonDeterministicBatchKey = any {
    step,
    {
      nondet d = AllDrainers.oneOf()
      val s = drainerSeg.get(d)
      val parts = drainerPartitions.get(d)
      val targets = parts.map(p => { seg: s, partition: p })
      all {
        ledgerState != "dropped",
        drainerPhase.get(d) == "scanned",
        targets.intersect(committedBatches).size() > 0,
        // BUG: random batch key creates duplicate sink write on retry/race!
        drainerPhase' = drainerPhase.set(d, "committed"),
        committedBatches' = committedBatches.union(targets),
        sinkWrites' = sinkWrites + targets.size(),
        racedOnSameSegment' = true,
        slateWatermark' = slateWatermark,
        slateComplete' = slateComplete,
        drainerSeg' = drainerSeg,
        drainerPartitions' = drainerPartitions,
        overBudgetRescanned' = overBudgetRescanned,
        keepPrimary,
      }
    },
  }

  // Mutant 4: Processor writes a sink batch even when a segment's fold cleared to empty.
  // breaks: EmptyFoldWritesNothing
  action mutantWriteOnEmptyFold = any {
    step,
    {
      nondet d = AllDrainers.oneOf()
      val s = drainerSeg.get(d)
      all {
        ledgerState != "dropped",
        drainerPhase.get(d) == "scanned",
        drainerPartitions.get(d).size() == 0,
        // BUG: commits empty/retracted batch into partition 0 anyway!
        drainerPhase' = drainerPhase.set(d, "committed"),
        committedBatches' = committedBatches.union(Set({ seg: s, partition: 0 })),
        sinkWrites' = sinkWrites + 1,
        racedOnSameSegment' = racedOnSameSegment,
        slateWatermark' = slateWatermark,
        slateComplete' = slateComplete,
        drainerSeg' = drainerSeg,
        drainerPartitions' = drainerPartitions,
        overBudgetRescanned' = overBudgetRescanned,
        keepPrimary,
      }
    },
  }

  // Mutant 5: Commit answers complete while the sink write is still volatile (lost before durability).
  // breaks: MessagesPreserved
  action mutantVolatileSinkCommit = any {
    step,
    {
      nondet d = AllDrainers.oneOf()
      all {
        ledgerState != "dropped",
        drainerPhase.get(d) == "scanned",
        drainerPartitions.get(d).size() > 0,
        // BUG: marks committed without durably persisting committedBatches!
        drainerPhase' = drainerPhase.set(d, "committed"),
        racedOnSameSegment' = racedOnSameSegment,
        drainerSeg' = drainerSeg,
        drainerPartitions' = drainerPartitions,
        overBudgetRescanned' = overBudgetRescanned,
        keepPrimary,
        keepArchive,
      }
    },
  }

  // Mutant 6: DrainWorker skips message-only segments (0 archive rows) without committing messages.
  // breaks: NoDropBeforeMessagesCommitted
  action mutantSkipMessageOnlySegment = any {
    step,
    {
      nondet d = AllDrainers.oneOf()
      val s = drainerSeg.get(d)
      all {
        ledgerState != "dropped",
        drainerPhase.get(d) == "scanned",
        not(segHasArchive(s)),
        segHasMessages(s),
        s == slateWatermark,
        not(slateComplete),
        // BUG: skips commit on 0-archive-row message-only segment!
        slateWatermark' = s + 1,
        slateComplete' = (slateComplete or (ledgerState == "closed" and allCoveredByWatermark(s + 1))),
        committedBatches' = committedBatches,
        sinkWrites' = sinkWrites,
        drainerPhase' = drainerPhase.set(d, "idle"),
        drainerSeg' = drainerSeg.set(d, -1),
        drainerPartitions' = drainerPartitions.set(d, Set()),
        racedOnSameSegment' = racedOnSameSegment,
        overBudgetRescanned' = overBudgetRescanned,
        keepPrimary,
      }
    },
  }
}

// =============================================================================
// Simulation & Directed Test Module
// =============================================================================

module messageDrainSim {
  import messageDrain(
    Seg0 = ["E0", "R0", "E1"],
    Seg1 = ["M0"],
    Drainers = 2,
    MaxCrashes = 2
  ).*

  // Directed scenario 1: Primary crashes after "E0"; DrainScan commits 1 deterministic sink batch,
  // SlateDB commits watermark + complete, ledger drops, and durable sink delivers the message.
  run survivingCrashMessageTest = {
    init
      .then(primaryWrite)          // Seg0[0] = "E0"
      .then(primaryCrash)          // closes ledger with Seg0 = ["E0"]
      .then(scanSegment(1, 0))
      .then(commitBatches(1))
      .then(commitSegment(1))
      .then(dropLedger)
      .then(sinkDeliver({ seg: 0, partition: 0 }))
      .then(all {
        assert(safety),
        assert(not(NeverSurvivingCrashMessage)),
        assert(ledgerState == "dropped"),
        assert(0.in(delivered)),
        keepPrimary, keepArchive, keepDrainers,
      })
  }

  // Directed scenario 2: Emit then retract within Seg0 -> 0 sink writes.
  run retractedInSegmentZeroWritesTest = {
    init
      .then(primaryWrite)          // Seg0[0] = "E0"
      .then(primaryWrite)          // Seg0[1] = "R0"
      .then(closeLedger)
      .then(scanSegment(1, 0))
      .then(commitBatches(1))
      .then(commitSegment(1))
      .then(dropLedger)
      .then(all {
        assert(safety),
        assert(not(NeverRetractedInSegment)),
        assert(committedBatches.size() == 0),
        assert(sinkWrites == 0),
        keepPrimary, keepArchive, keepDrainers,
      })
  }

  // Directed scenario 3: Racing drainers + OverBudget rescan + message-only Seg1 ("M0").
  run racingDrainersAndMessageOnlySegmentTest = {
    init
      .then(primaryWrite)          // Seg0[0] = "E0"
      .then(primaryWrite)          // Seg0[1] = "R0"
      .then(primaryWrite)          // Seg0[2] = "E1" -> advances to Seg1
      .then(primaryWrite)          // Seg1[0] = "M0" (0 archive rows)
      .then(closeLedger)
      .then(scanSegment(1, 0))
      .then(overBudgetReset(1))    // OverBudget clears folds & rescans
      .then(scanSegment(1, 0))
      .then(scanSegment(2, 0))     // drainer 2 races drainer 1 on Seg0
      .then(commitBatches(1))      // creates { seg: 0, partition: 1 }
      .then(commitBatches(2))      // collides idempotently on { seg: 0, partition: 1 }
      .then(commitSegment(1))      // watermark -> 1
      .then(scanSegment(1, 1))     // Seg1 ("M0", message-only)
      .then(commitBatches(1))      // creates { seg: 1, partition: 0 }
      .then(commitSegment(1))      // watermark -> 2, complete = true
      .then(dropLedger)
      .then(all {
        assert(safety),
        assert(not(NeverMessageOnlySegment)),
        assert(not(NeverRacingDrainers)),
        assert(overBudgetRescanned),
        assert(sinkWrites == 2),
        keepPrimary, keepArchive, keepDrainers,
      })
  }
}

// =============================================================================
// Exhaustive TLC Model-Checking Modules
// =============================================================================

// Multi-segment with same-segment retract, surviving partition 1, message-only segment,
// 2 racing drainers, OverBudget rescan, and crashes.
module messageDrainTlc {
  import messageDrain(
    Seg0 = ["E0", "R0", "E1"],
    Seg1 = ["M0"],
    Drainers = 2,
    MaxCrashes = 1
  ).*
}

// Single-segment retracted-in-segment ("E0", "R0") with 2 racing drainers.
module messageDrainRetractedTlc {
  import messageDrain(
    Seg0 = ["E0", "R0"],
    Seg1 = [],
    Drainers = 2,
    MaxCrashes = 1
  ).*
}

// Cross-segment Emit-then-Retract: Seg0 = ["E0"], Seg1 = ["R0"].
module messageDrainCrossSegTlc {
  import messageDrain(
    Seg0 = ["E0"],
    Seg1 = ["R0"],
    Drainers = 1,
    MaxCrashes = 1
  ).*
}

// Single segment with 2 distinct partitions ("E0", "E1") committing 2 deterministic sink batches.
module messageDrainMultiPartitionTlc {
  import messageDrain(
    Seg0 = ["E0", "E1"],
    Seg1 = [],
    Drainers = 2,
    MaxCrashes = 1
  ).*
}
