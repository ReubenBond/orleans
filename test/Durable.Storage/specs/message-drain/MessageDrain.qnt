// -*- mode: Bluespec; -*-
// Unified single-pass DrainScan message draining and idempotent per-segment durable sink commit
// (ledgers.md §3.1, archive.md §2.3, archive-lsm.md §3.1, archive-logs.md §3.2).
//
// Models both LSM (DrainWorker: DrainScan -> CommitMessagesAsync -> ManifestChange.Drain -> Drop) and
// Archive Logs (ArchiveLogDrainWorker: DrainScan -> WriteAsync + CommitMessagesAsync -> DrainMarker -> Drop)
// across single- and multi-segment ledgers, message-only segments (0 archive rows), same-segment retract
// (0 durable sink commits), cross-segment emit-then-retract, concurrent racing drainers colliding on deterministic
// per-segment commit keys, primary volatile session consumption vs crash recovery, and drainer crashes at every step.
module MessageDrain {
  // Archive backend mode: "lsm" or "logs".
  const Mode: str
  // Slot operations per segment: "E" = archive row + Emit message, "R" = archive row + Retract message,
  // "K" = archive-only row, "M" = message-only Emit (0 archive rows).
  const Seg0: List[str]
  const Seg1: List[str]
  // Number of concurrent drainers (1 or 2).
  const Drainers: int
  // Maximum total primary + drainer crashes.
  const MaxCrashes: int
  // Mutation selector ("none", "drop-before-messages-committed", "skip-unknown-channel",
  // "non-idempotent-commit", "write-on-empty-fold", "volatile-answer").
  const Mutation: str

  pure val NumSegments: int = if (Seg1.length() > 0) 2 else 1
  pure val AllDrainers: Set[int] = 1.to(Drainers)
  pure val AllSegments: Set[int] = 0.to(NumSegments - 1)

  pure def segSlots(s: int): List[str] = if (s == 0) Seg0 else Seg1

  pure def foldSurviving(slots: List[str], count: int): bool =
    0.to(count - 1).fold(false, (acc, i) =>
      val op = slots[i]
      if (op == "E" or op == "M") true
      else if (op == "R") false
      else acc)

  pure def hasArchive(slots: List[str], count: int): bool =
    0.to(count - 1).exists(i => slots[i] != "M")

  pure def hasMessages(slots: List[str], count: int): bool =
    0.to(count - 1).exists(i => slots[i] != "K")

  // Ledger & primary volatile session state.
  var ledgerState: str         // "open" | "closed" | "dropped"
  var writtenSeg: int          // 0..NumSegments
  var writtenSlot: int         // 0..segSlots(writtenSeg).length()
  var primaryAlive: bool
  var primaryPending: bool
  var logicalPending: bool
  var delivered: bool
  var crashes: int

  // Archive drain & idempotent durable-sink state.
  var lsmWatermark: int        // number of consecutive segments 0..lsmWatermark-1 committed in manifest
  var lsmComplete: bool        // ManifestChange.Drain(..., complete: true) committed
  var logsMarked: Set[int]     // segments with durable drain marker d/{S}/{start}
  var durableSink: Set[int]    // deterministic per-segment durable commits (keyed by segment index)
  var sinkWrites: int          // total distinct durable sink writes (equals size(durableSink) under idempotent commits)

  // Per-drainer state.
  var drainerPhase: int -> str     // "idle" | "scanned" | "committed"
  var drainerSeg: int -> int       // -1 when idle, else segment index
  var drainerSurviving: int -> bool
  var racedOnSameSegment: bool     // witness flag: 2 drainers committed the same segment

  def segWrittenLen(s: int): int =
    if (s < writtenSeg) segSlots(s).length()
    else if (s == writtenSeg) writtenSlot
    else 0

  def segSurviving(s: int): bool = foldSurviving(segSlots(s), segWrittenLen(s))
  def segHasArchive(s: int): bool = hasArchive(segSlots(s), segWrittenLen(s))
  def segHasMessages(s: int): bool = hasMessages(segSlots(s), segWrittenLen(s))

  def writtenSegments: Set[int] = AllSegments.filter(s => segWrittenLen(s) > 0)

  def segDrainable(s: int): bool =
    (s < writtenSeg) or (ledgerState == "closed" and s < NumSegments and segWrittenLen(s) > 0)

  def allCoveredByWatermark(nextWatermark: int): bool =
    writtenSegments.forall(s => s < nextWatermark)

  action init = all {
    ledgerState' = "open",
    writtenSeg' = 0,
    writtenSlot' = 0,
    primaryAlive' = true,
    primaryPending' = false,
    logicalPending' = false,
    delivered' = false,
    crashes' = 0,
    lsmWatermark' = 0,
    lsmComplete' = false,
    logsMarked' = Set(),
    durableSink' = Set(),
    sinkWrites' = 0,
    drainerPhase' = AllDrainers.mapBy(_ => "idle"),
    drainerSeg' = AllDrainers.mapBy(_ => -1),
    drainerSurviving' = AllDrainers.mapBy(_ => false),
    racedOnSameSegment' = false,
  }

  // Primary durably writes the next slot (atomic archive row + optional RowKind.Message).
  action primaryWrite = {
    val slots = segSlots(writtenSeg)
    val op = slots[writtenSlot]
    val nextSlot = writtenSlot + 1
    val advanceSeg = nextSlot == slots.length()
    all {
      ledgerState == "open",
      primaryAlive,
      writtenSeg < NumSegments,
      writtenSeg' = if (advanceSeg) writtenSeg + 1 else writtenSeg,
      writtenSlot' = if (advanceSeg) 0 else nextSlot,
      primaryPending' = if (op == "E" or op == "M") true else if (op == "R") false else primaryPending,
      logicalPending' = if (op == "E" or op == "M") true else if (op == "R") false else logicalPending,
      ledgerState' = ledgerState,
      primaryAlive' = primaryAlive,
      delivered' = delivered,
      crashes' = crashes,
      lsmWatermark' = lsmWatermark,
      lsmComplete' = lsmComplete,
      logsMarked' = logsMarked,
      durableSink' = durableSink,
      sinkWrites' = sinkWrites,
      drainerPhase' = drainerPhase,
      drainerSeg' = drainerSeg,
      drainerSurviving' = drainerSurviving,
      racedOnSameSegment' = racedOnSameSegment,
    }
  }

  // Primary's volatile IMessageSession consumes a pending message while the ledger is open.
  action primaryConsume = all {
    ledgerState == "open",
    primaryAlive,
    primaryPending,
    primaryPending' = false,
    logicalPending' = false,
    delivered' = true,
    ledgerState' = ledgerState,
    writtenSeg' = writtenSeg,
    writtenSlot' = writtenSlot,
    primaryAlive' = primaryAlive,
    crashes' = crashes,
    lsmWatermark' = lsmWatermark,
    lsmComplete' = lsmComplete,
    logsMarked' = logsMarked,
    durableSink' = durableSink,
    sinkWrites' = sinkWrites,
    drainerPhase' = drainerPhase,
    drainerSeg' = drainerSeg,
    drainerSurviving' = drainerSurviving,
    racedOnSameSegment' = racedOnSameSegment,
  }

  // Primary crashes: volatile IMessageSession state is lost and ledger is closed at the current tail.
  action primaryCrash = all {
    ledgerState == "open",
    primaryAlive,
    crashes < MaxCrashes,
    writtenSegments.size() > 0,
    primaryAlive' = false,
    primaryPending' = false,
    ledgerState' = "closed",
    crashes' = crashes + 1,
    writtenSeg' = writtenSeg,
    writtenSlot' = writtenSlot,
    logicalPending' = logicalPending,
    delivered' = delivered,
    lsmWatermark' = lsmWatermark,
    lsmComplete' = lsmComplete,
    logsMarked' = logsMarked,
    durableSink' = durableSink,
    sinkWrites' = sinkWrites,
    drainerPhase' = drainerPhase,
    drainerSeg' = drainerSeg,
    drainerSurviving' = drainerSurviving,
    racedOnSameSegment' = racedOnSameSegment,
  }

  // Graceful roll / close of the ledger once all scripted slots are written.
  action closeLedger = all {
    ledgerState == "open",
    writtenSeg == NumSegments,
    ledgerState' = "closed",
    primaryPending' = false,
    writtenSeg' = writtenSeg,
    writtenSlot' = writtenSlot,
    primaryAlive' = primaryAlive,
    logicalPending' = logicalPending,
    delivered' = delivered,
    crashes' = crashes,
    lsmWatermark' = lsmWatermark,
    lsmComplete' = lsmComplete,
    logsMarked' = logsMarked,
    durableSink' = durableSink,
    sinkWrites' = sinkWrites,
    drainerPhase' = drainerPhase,
    drainerSeg' = drainerSeg,
    drainerSurviving' = drainerSurviving,
    racedOnSameSegment' = racedOnSameSegment,
  }

  // Single-pass DrainScan over segment s: folds archive rows + RowKind.Message rows in one pass.
  action scanSegment(d: int, s: int): bool = {
    val surv = if (Mutation == "skip-unknown-channel") false else segSurviving(s)
    all {
      ledgerState != "dropped",
      drainerPhase.get(d) == "idle",
      segDrainable(s),
      if (Mode == "lsm") (s == lsmWatermark and not(lsmComplete)) else not(s.in(logsMarked)),
      drainerPhase' = drainerPhase.set(d, "scanned"),
      drainerSeg' = drainerSeg.set(d, s),
      drainerSurviving' = drainerSurviving.set(d, surv),
      ledgerState' = ledgerState,
      writtenSeg' = writtenSeg,
      writtenSlot' = writtenSlot,
      primaryAlive' = primaryAlive,
      primaryPending' = primaryPending,
      logicalPending' = logicalPending,
      delivered' = delivered,
      crashes' = crashes,
      lsmWatermark' = lsmWatermark,
      lsmComplete' = lsmComplete,
      logsMarked' = logsMarked,
      durableSink' = durableSink,
      sinkWrites' = sinkWrites,
      racedOnSameSegment' = racedOnSameSegment,
    }
  }

  // IMessageFold.CommitAsync: idempotently commits surviving messages to the durable sink (0 writes if fold is empty).
  action commitMessages(d: int): bool = {
    val s = drainerSeg.get(d)
    val shouldWrite = drainerSurviving.get(d) or Mutation == "write-on-empty-fold"
    val alreadyCommitted = s.in(durableSink)
    all {
      ledgerState != "dropped",
      drainerPhase.get(d) == "scanned",
      drainerPhase' = drainerPhase.set(d, "committed"),
      durableSink' =
        if (shouldWrite and Mutation != "volatile-answer") durableSink.union(Set(s))
        else durableSink,
      sinkWrites' =
        if (shouldWrite and Mutation != "volatile-answer") (
          if (Mutation == "non-idempotent-commit" or not(alreadyCommitted)) sinkWrites + 1
          else sinkWrites
        ) else sinkWrites,
      racedOnSameSegment' = (racedOnSameSegment or (shouldWrite and alreadyCommitted)),
      ledgerState' = ledgerState,
      writtenSeg' = writtenSeg,
      writtenSlot' = writtenSlot,
      primaryAlive' = primaryAlive,
      primaryPending' = primaryPending,
      logicalPending' = logicalPending,
      delivered' = delivered,
      crashes' = crashes,
      lsmWatermark' = lsmWatermark,
      lsmComplete' = lsmComplete,
      logsMarked' = logsMarked,
      drainerSeg' = drainerSeg,
      drainerSurviving' = drainerSurviving,
    }
  }

  // Commit ManifestChange.Drain (LSM) or drain marker d/{S}/{start} (Logs) after messages commit.
  action commitSegment(d: int): bool = {
    val s = drainerSeg.get(d)
    val allowedPhase =
      drainerPhase.get(d) == "committed"
      or (Mutation == "drop-before-messages-committed" and drainerPhase.get(d) == "scanned")
    all {
      ledgerState != "dropped",
      allowedPhase,
      if (Mode == "lsm") (s == lsmWatermark and not(lsmComplete)) else not(s.in(logsMarked)),
      lsmWatermark' = if (Mode == "lsm") s + 1 else lsmWatermark,
      lsmComplete' =
        if (Mode == "lsm") (lsmComplete or (ledgerState == "closed" and allCoveredByWatermark(s + 1)))
        else lsmComplete,
      logsMarked' = if (Mode == "logs") logsMarked.union(Set(s)) else logsMarked,
      drainerPhase' = drainerPhase.set(d, "idle"),
      drainerSeg' = drainerSeg.set(d, -1),
      drainerSurviving' = drainerSurviving.set(d, false),
      ledgerState' = ledgerState,
      writtenSeg' = writtenSeg,
      writtenSlot' = writtenSlot,
      primaryAlive' = primaryAlive,
      primaryPending' = primaryPending,
      logicalPending' = logicalPending,
      delivered' = delivered,
      crashes' = crashes,
      durableSink' = durableSink,
      sinkWrites' = sinkWrites,
      racedOnSameSegment' = racedOnSameSegment,
    }
  }

  // LSM a == T completion when all segments were already drained while open before closeLedger.
  action lsmMarkClosedComplete = all {
    Mode == "lsm",
    ledgerState == "closed",
    not(lsmComplete),
    allCoveredByWatermark(lsmWatermark),
    lsmComplete' = true,
    ledgerState' = ledgerState,
    writtenSeg' = writtenSeg,
    writtenSlot' = writtenSlot,
    primaryAlive' = primaryAlive,
    primaryPending' = primaryPending,
    logicalPending' = logicalPending,
    delivered' = delivered,
    crashes' = crashes,
    lsmWatermark' = lsmWatermark,
    logsMarked' = logsMarked,
    durableSink' = durableSink,
    sinkWrites' = sinkWrites,
    drainerPhase' = drainerPhase,
    drainerSeg' = drainerSeg,
    drainerSurviving' = drainerSurviving,
    racedOnSameSegment' = racedOnSameSegment,
  }

  // Drainer crashes mid-scan or mid-commit; already-committed sink entries survive durably.
  action drainerCrash(d: int): bool = all {
    drainerPhase.get(d) != "idle",
    crashes < MaxCrashes,
    drainerPhase' = drainerPhase.set(d, "idle"),
    drainerSeg' = drainerSeg.set(d, -1),
    drainerSurviving' = drainerSurviving.set(d, false),
    crashes' = crashes + 1,
    ledgerState' = ledgerState,
    writtenSeg' = writtenSeg,
    writtenSlot' = writtenSlot,
    primaryAlive' = primaryAlive,
    primaryPending' = primaryPending,
    logicalPending' = logicalPending,
    delivered' = delivered,
    lsmWatermark' = lsmWatermark,
    lsmComplete' = lsmComplete,
    logsMarked' = logsMarked,
    durableSink' = durableSink,
    sinkWrites' = sinkWrites,
    racedOnSameSegment' = racedOnSameSegment,
  }

  // LedgerMapChange.Drop once the closed ledger is completely drained.
  action dropLedger = all {
    ledgerState == "closed",
    if (Mode == "lsm") lsmComplete else writtenSegments.forall(s => s.in(logsMarked)),
    ledgerState' = "dropped",
    writtenSeg' = writtenSeg,
    writtenSlot' = writtenSlot,
    primaryAlive' = primaryAlive,
    primaryPending' = primaryPending,
    logicalPending' = logicalPending,
    delivered' = delivered,
    crashes' = crashes,
    lsmWatermark' = lsmWatermark,
    lsmComplete' = lsmComplete,
    logsMarked' = logsMarked,
    durableSink' = durableSink,
    sinkWrites' = sinkWrites,
    drainerPhase' = drainerPhase,
    drainerSeg' = drainerSeg,
    drainerSurviving' = drainerSurviving,
    racedOnSameSegment' = racedOnSameSegment,
  }

  // A durable-sink batch for segment s is delivered to its downstream consumer.
  action sinkDeliver(s: int): bool = all {
    s.in(durableSink),
    logicalPending' = false,
    delivered' = true,
    ledgerState' = ledgerState,
    writtenSeg' = writtenSeg,
    writtenSlot' = writtenSlot,
    primaryAlive' = primaryAlive,
    primaryPending' = primaryPending,
    crashes' = crashes,
    lsmWatermark' = lsmWatermark,
    lsmComplete' = lsmComplete,
    logsMarked' = logsMarked,
    durableSink' = durableSink,
    sinkWrites' = sinkWrites,
    drainerPhase' = drainerPhase,
    drainerSeg' = drainerSeg,
    drainerSurviving' = drainerSurviving,
    racedOnSameSegment' = racedOnSameSegment,
  }

  action step = any {
    primaryWrite,
    primaryConsume,
    primaryCrash,
    closeLedger,
    lsmMarkClosedComplete,
    dropLedger,
    nondet d = AllDrainers.oneOf()
    any {
      nondet s = AllSegments.oneOf()
      scanSegment(d, s),
      commitMessages(d),
      commitSegment(d),
      drainerCrash(d),
    },
    nondet s = AllSegments.oneOf()
    sinkDeliver(s),
  }

  // --- Safety invariants ---

  val TypeOK: bool = all {
    ledgerState.in(Set("open", "closed", "dropped")),
    writtenSeg >= 0 and writtenSeg <= NumSegments,
    writtenSlot >= 0,
    crashes >= 0 and crashes <= MaxCrashes,
    lsmWatermark >= 0 and lsmWatermark <= NumSegments,
    logsMarked.subseteq(AllSegments),
    durableSink.subseteq(AllSegments),
    sinkWrites >= 0,
    AllDrainers.forall(d => drainerPhase.get(d).in(Set("idle", "scanned", "committed"))),
  }

  // Every durably accepted message is preserved by the live primary session, the undropped ledger, or the durable sink.
  val MessagesPreserved: bool =
    logicalPending implies (
      (primaryAlive and primaryPending)
      or (ledgerState != "dropped")
      or (durableSink.size() > 0)
    )

  // A closed ledger is never dropped until every written segment with surviving messages has committed to the sink.
  val NoDropBeforeMessagesCommitted: bool =
    (ledgerState == "dropped") implies writtenSegments.forall(s =>
      segSurviving(s) implies s.in(durableSink))

  // A segment whose message fold is empty (e.g. Emit followed by Retract) writes nothing to the durable sink.
  val EmptyFoldWritesNothing: bool =
    AllSegments.forall(s => (not(segSurviving(s))) implies not(s.in(durableSink)))

  // Idempotent per-segment commits guarantee racing drainers and crash retries write at most once per segment.
  val IdempotentCommit: bool = sinkWrites == durableSink.size()

  // --- Non-vacuity witnesses (each negated so Apalache produces a concrete counterexample) ---

  val NeverSurvivingCrashMessage: bool =
    not(not(primaryAlive) and ledgerState == "dropped" and delivered and durableSink.size() > 0)

  val NeverClearedInSegment: bool =
    not(ledgerState == "dropped" and durableSink.size() == 0 and sinkWrites == 0)

  val NeverMessageOnlySegment: bool =
    not(ledgerState == "dropped" and writtenSegments.exists(s => not(segHasArchive(s)) and segHasMessages(s)) and durableSink.size() > 0)

  val NeverCrossSegmentEmitThenRetract: bool =
    not(NumSegments == 2 and ledgerState == "dropped" and 0.in(durableSink) and not(1.in(durableSink)))

  val NeverRacingDrainers: bool =
    not(racedOnSameSegment and sinkWrites == durableSink.size())

  // --- Weak-fairness liveness properties ---

  val stateVars = (
    ledgerState, writtenSeg, writtenSlot, primaryAlive, primaryPending,
    logicalPending, delivered, crashes, lsmWatermark, lsmComplete,
    logsMarked, durableSink, sinkWrites, drainerPhase, drainerSeg,
    drainerSurviving, racedOnSameSegment
  )

  temporal Fairness = and {
    primaryWrite.weakFair(stateVars),
    closeLedger.weakFair(stateVars),
    lsmMarkClosedComplete.weakFair(stateVars),
    dropLedger.weakFair(stateVars),
    AllDrainers.forall(d => AllSegments.forall(s => scanSegment(d, s).weakFair(stateVars))),
    AllDrainers.forall(d => commitMessages(d).weakFair(stateVars)),
    AllDrainers.forall(d => commitSegment(d).weakFair(stateVars)),
    AllSegments.forall(s => sinkDeliver(s).weakFair(stateVars)),
  }

  temporal EventuallyDrainedAndDropped = Fairness implies always(eventually(ledgerState == "dropped"))
  temporal EventuallyMessagesDelivered = Fairness implies always(logicalPending implies eventually(not(logicalPending)))
}
