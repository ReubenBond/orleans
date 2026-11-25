// -*- mode: Bluespec; -*-
// Unified single-pass DrainScan message draining + deterministic sealed JobShard publishing
// (ledgers.md §3.1, archive.md §2.3, archive-lsm.md §3.1, archive-logs.md §3.2, orleans.md §5.15).
//
// Models both LSM (DrainWorker: DrainScan -> CommitMessagesAsync -> ManifestChange.Drain -> Drop) and
// Archive Logs (ArchiveLogDrainWorker: DrainScan -> WriteAsync + CommitMessagesAsync -> DrainMarker -> Drop)
// across single- and multi-segment ledgers, message-only segments (0 archive rows), same-segment clear
// (0 sealed shards), cross-segment set-then-clear, concurrent racing drainers colliding on deterministic
// sealed JobShard IDs, primary volatile wakeup heap vs crash recovery, and drainer crashes at every step.
module MessageDrain {
  // Archive backend mode: "lsm" or "logs".
  const Mode: str
  // Slot operations per segment: "S" = journal + SetWakeup, "C" = journal + ClearWakeup,
  // "K" = journal-only, "M" = message-only SetWakeup (0 archive rows).
  const Seg0: List[str]
  const Seg1: List[str]
  // Number of concurrent drainers (1 or 2).
  const Drainers: int
  // Maximum total primary + drainer crashes.
  const MaxCrashes: int
  // Mutation selector ("none", "drop-before-messages-committed", "skip-unknown-channel",
  // "non-deterministic-shard-id", "write-on-empty-fold", "volatile-answer").
  const Mutation: str

  pure val NumSegments: int = if (Seg1.length() > 0) 2 else 1
  pure val AllDrainers: Set[int] = 1.to(Drainers)
  pure val AllSegments: Set[int] = 0.to(NumSegments - 1)

  pure def segSlots(s: int): List[str] = if (s == 0) Seg0 else Seg1

  pure def foldSurviving(slots: List[str], count: int): bool =
    0.to(count - 1).fold(false, (acc, i) =>
      val op = slots[i]
      if (op == "S" or op == "M") true
      else if (op == "C") false
      else acc)

  pure def hasArchive(slots: List[str], count: int): bool =
    0.to(count - 1).exists(i => slots[i] != "M")

  pure def hasMessages(slots: List[str], count: int): bool =
    0.to(count - 1).exists(i => slots[i] != "K")

  // Ledger & primary volatile state.
  var ledgerState: str         // "open" | "closed" | "dropped"
  var writtenSeg: int          // 0..NumSegments
  var writtenSlot: int         // 0..segSlots(writtenSeg).length()
  var primaryAlive: bool
  var primaryArmed: bool
  var logicalArmed: bool
  var delivered: bool
  var crashes: int

  // Archive drain & deterministic sealed-shard state.
  var lsmWatermark: int        // number of consecutive segments 0..lsmWatermark-1 committed in manifest
  var lsmComplete: bool        // ManifestChange.Drain(..., complete: true) committed
  var logsMarked: Set[int]     // segments with durable drain marker d/{S}/{start}
  var sealedShards: Set[int]   // deterministic sealed JobShard IDs (keyed by segment index)
  var createdShards: int       // total shard-creation writes (equals size(sealedShards) under deterministic IDs)

  // Per-drainer state.
  var drainerPhase: int -> str     // "idle" | "scanned" | "published"
  var drainerSeg: int -> int       // -1 when idle, else segment index
  var drainerSurviving: int -> bool
  var racedOnSameSegment: bool     // witness flag: 2 drainers published the same segment

  def segWrittenLen(s: int): int =
    if (s < writtenSeg) segSlots(s).length()
    else if (s == writtenSeg) writtenSlot
    else 0

  def segSurviving(s: int): bool = foldSurviving(segSlots(s), segWrittenLen(s))
  def segHasArchive(s: int): bool = hasArchive(segSlots(s), segWrittenLen(s))
  def segHasMessages(s: int): bool = hasMessages(segSlots(s), segWrittenLen(s))

  def writtenSegments: Set[int] = AllSegments.filter(s => segWrittenLen(s) > 0)

  def segDrainable(s: int): bool =
    // Either sealed while open (all slots of s written and ledger moved to next segment),
    // or the ledger is closed and s has at least one written slot.
    (s < writtenSeg) or (ledgerState == "closed" and s < NumSegments and segWrittenLen(s) > 0)

  def allCoveredByWatermark(nextWatermark: int): bool =
    writtenSegments.forall(s => s < nextWatermark)

  action init = all {
    ledgerState' = "open",
    writtenSeg' = 0,
    writtenSlot' = 0,
    primaryAlive' = true,
    primaryArmed' = false,
    logicalArmed' = false,
    delivered' = false,
    crashes' = 0,
    lsmWatermark' = 0,
    lsmComplete' = false,
    logsMarked' = Set(),
    sealedShards' = Set(),
    createdShards' = 0,
    drainerPhase' = AllDrainers.mapBy(_ => "idle"),
    drainerSeg' = AllDrainers.mapBy(_ => -1),
    drainerSurviving' = AllDrainers.mapBy(_ => false),
    racedOnSameSegment' = false,
  }

  // Primary durably writes the next slot (atomic journal row + optional RowKind.Message).
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
      primaryArmed' = if (op == "S" or op == "M") true else if (op == "C") false else primaryArmed,
      logicalArmed' = if (op == "S" or op == "M") true else if (op == "C") false else logicalArmed,
      ledgerState' = ledgerState,
      primaryAlive' = primaryAlive,
      delivered' = delivered,
      crashes' = crashes,
      lsmWatermark' = lsmWatermark,
      lsmComplete' = lsmComplete,
      logsMarked' = logsMarked,
      sealedShards' = sealedShards,
      createdShards' = createdShards,
      drainerPhase' = drainerPhase,
      drainerSeg' = drainerSeg,
      drainerSurviving' = drainerSurviving,
      racedOnSameSegment' = racedOnSameSegment,
    }
  }

  // Primary's volatile WakeupLedgerSession fires while the ledger is open.
  action primaryFireWakeup = all {
    ledgerState == "open",
    primaryAlive,
    primaryArmed,
    primaryArmed' = false,
    logicalArmed' = false,
    delivered' = true,
    ledgerState' = ledgerState,
    writtenSeg' = writtenSeg,
    writtenSlot' = writtenSlot,
    primaryAlive' = primaryAlive,
    crashes' = crashes,
    lsmWatermark' = lsmWatermark,
    lsmComplete' = lsmComplete,
    logsMarked' = logsMarked,
    sealedShards' = sealedShards,
    createdShards' = createdShards,
    drainerPhase' = drainerPhase,
    drainerSeg' = drainerSeg,
    drainerSurviving' = drainerSurviving,
    racedOnSameSegment' = racedOnSameSegment,
  }

  // Primary crashes: volatile WakeupLedgerSession heap is lost and ledger is closed at the current tail.
  action primaryCrash = all {
    ledgerState == "open",
    primaryAlive,
    crashes < MaxCrashes,
    writtenSegments.size() > 0,
    primaryAlive' = false,
    primaryArmed' = false,
    ledgerState' = "closed",
    crashes' = crashes + 1,
    writtenSeg' = writtenSeg,
    writtenSlot' = writtenSlot,
    logicalArmed' = logicalArmed,
    delivered' = delivered,
    lsmWatermark' = lsmWatermark,
    lsmComplete' = lsmComplete,
    logsMarked' = logsMarked,
    sealedShards' = sealedShards,
    createdShards' = createdShards,
    drainerPhase' = drainerPhase,
    drainerSeg' = drainerSeg,
    drainerSurviving' = drainerSurviving,
    racedOnSameSegment' = racedOnSameSegment,
  }

  // Graceful roll / close of the ledger once all scripted slots (or at least one) are written.
  action closeLedger = all {
    ledgerState == "open",
    writtenSeg == NumSegments,
    ledgerState' = "closed",
    primaryArmed' = false,
    writtenSeg' = writtenSeg,
    writtenSlot' = writtenSlot,
    primaryAlive' = primaryAlive,
    logicalArmed' = logicalArmed,
    delivered' = delivered,
    crashes' = crashes,
    lsmWatermark' = lsmWatermark,
    lsmComplete' = lsmComplete,
    logsMarked' = logsMarked,
    sealedShards' = sealedShards,
    createdShards' = createdShards,
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
      primaryArmed' = primaryArmed,
      logicalArmed' = logicalArmed,
      delivered' = delivered,
      crashes' = crashes,
      lsmWatermark' = lsmWatermark,
      lsmComplete' = lsmComplete,
      logsMarked' = logsMarked,
      sealedShards' = sealedShards,
      createdShards' = createdShards,
      racedOnSameSegment' = racedOnSameSegment,
    }
  }

  // CommitMessagesAsync: publishes 1 deterministic sealed JobShard if surviving entries > 0 (0 writes if 0).
  action publishShard(d: int): bool = {
    val s = drainerSeg.get(d)
    val shouldWrite = drainerSurviving.get(d) or Mutation == "write-on-empty-fold"
    val alreadyExists = s.in(sealedShards)
    all {
      ledgerState != "dropped",
      drainerPhase.get(d) == "scanned",
      drainerPhase' = drainerPhase.set(d, "published"),
      sealedShards' =
        if (shouldWrite and Mutation != "volatile-answer") sealedShards.union(Set(s))
        else sealedShards,
      createdShards' =
        if (shouldWrite and Mutation != "volatile-answer") (
          if (Mutation == "non-deterministic-shard-id" or not(alreadyExists)) createdShards + 1
          else createdShards
        ) else createdShards,
      racedOnSameSegment' = (racedOnSameSegment or (shouldWrite and alreadyExists)),
      ledgerState' = ledgerState,
      writtenSeg' = writtenSeg,
      writtenSlot' = writtenSlot,
      primaryAlive' = primaryAlive,
      primaryArmed' = primaryArmed,
      logicalArmed' = logicalArmed,
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
      drainerPhase.get(d) == "published"
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
      primaryArmed' = primaryArmed,
      logicalArmed' = logicalArmed,
      delivered' = delivered,
      crashes' = crashes,
      sealedShards' = sealedShards,
      createdShards' = createdShards,
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
    primaryArmed' = primaryArmed,
    logicalArmed' = logicalArmed,
    delivered' = delivered,
    crashes' = crashes,
    lsmWatermark' = lsmWatermark,
    logsMarked' = logsMarked,
    sealedShards' = sealedShards,
    createdShards' = createdShards,
    drainerPhase' = drainerPhase,
    drainerSeg' = drainerSeg,
    drainerSurviving' = drainerSurviving,
    racedOnSameSegment' = racedOnSameSegment,
  }

  // Drainer crashes mid-scan or mid-publish; already-published sealed shards survive durably.
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
    primaryArmed' = primaryArmed,
    logicalArmed' = logicalArmed,
    delivered' = delivered,
    lsmWatermark' = lsmWatermark,
    lsmComplete' = lsmComplete,
    logsMarked' = logsMarked,
    sealedShards' = sealedShards,
    createdShards' = createdShards,
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
    primaryArmed' = primaryArmed,
    logicalArmed' = logicalArmed,
    delivered' = delivered,
    crashes' = crashes,
    lsmWatermark' = lsmWatermark,
    lsmComplete' = lsmComplete,
    logsMarked' = logsMarked,
    sealedShards' = sealedShards,
    createdShards' = createdShards,
    drainerPhase' = drainerPhase,
    drainerSeg' = drainerSeg,
    drainerSurviving' = drainerSurviving,
    racedOnSameSegment' = racedOnSameSegment,
  }

  // A durable sealed JobShard is adopted and executes the wakeup.
  action shardFireWakeup(s: int): bool = all {
    s.in(sealedShards),
    logicalArmed' = false,
    delivered' = true,
    ledgerState' = ledgerState,
    writtenSeg' = writtenSeg,
    writtenSlot' = writtenSlot,
    primaryAlive' = primaryAlive,
    primaryArmed' = primaryArmed,
    crashes' = crashes,
    lsmWatermark' = lsmWatermark,
    lsmComplete' = lsmComplete,
    logsMarked' = logsMarked,
    sealedShards' = sealedShards,
    createdShards' = createdShards,
    drainerPhase' = drainerPhase,
    drainerSeg' = drainerSeg,
    drainerSurviving' = drainerSurviving,
    racedOnSameSegment' = racedOnSameSegment,
  }

  action step = any {
    primaryWrite,
    primaryFireWakeup,
    primaryCrash,
    closeLedger,
    lsmMarkClosedComplete,
    dropLedger,
    nondet d = AllDrainers.oneOf()
    any {
      nondet s = AllSegments.oneOf()
      scanSegment(d, s),
      publishShard(d),
      commitSegment(d),
      drainerCrash(d),
    },
    nondet s = AllSegments.oneOf()
    shardFireWakeup(s),
  }

  // --- Safety invariants ---

  val TypeOK: bool = all {
    ledgerState.in(Set("open", "closed", "dropped")),
    writtenSeg >= 0 and writtenSeg <= NumSegments,
    writtenSlot >= 0,
    crashes >= 0 and crashes <= MaxCrashes,
    lsmWatermark >= 0 and lsmWatermark <= NumSegments,
    logsMarked.subseteq(AllSegments),
    sealedShards.subseteq(AllSegments),
    createdShards >= 0,
    AllDrainers.forall(d => drainerPhase.get(d).in(Set("idle", "scanned", "published"))),
  }

  // Every durably accepted wakeup is preserved by either the live primary, the undropped ledger, or a sealed JobShard.
  val WakeupPreserved: bool =
    logicalArmed implies (
      (primaryAlive and primaryArmed)
      or (ledgerState != "dropped")
      or (sealedShards.size() > 0)
    )

  // A closed ledger is never dropped until every written segment with a surviving wakeup has published its sealed shard.
  val NoDropBeforeMessagesCommitted: bool =
    (ledgerState == "dropped") implies writtenSegments.forall(s =>
      segSurviving(s) implies s.in(sealedShards))

  // A segment whose messages fold to empty (e.g. SetWakeup followed by ClearWakeup) publishes zero sealed shards.
  val ClearedInSegmentWritesNothing: bool =
    AllSegments.forall(s => (not(segSurviving(s))) implies not(s.in(sealedShards)))

  // Deterministic sealed JobShard IDs guarantee racing drainers and crash retries create at most 1 shard per segment.
  val IdempotentShardPublish: bool = createdShards == sealedShards.size()

  // --- Non-vacuity witnesses (each negated so Apalache produces a concrete counterexample) ---

  val NeverSurvivingCrashWakeup: bool =
    not(not(primaryAlive) and ledgerState == "dropped" and delivered and sealedShards.size() > 0)

  val NeverClearedInSegment: bool =
    not(ledgerState == "dropped" and sealedShards.size() == 0 and createdShards == 0)

  val NeverMessageOnlySegment: bool =
    not(ledgerState == "dropped" and writtenSegments.exists(s => not(segHasArchive(s)) and segHasMessages(s)) and sealedShards.size() > 0)

  val NeverCrossSegmentSetThenClear: bool =
    not(NumSegments == 2 and ledgerState == "dropped" and 0.in(sealedShards) and not(1.in(sealedShards)))

  val NeverRacingDrainers: bool =
    not(racedOnSameSegment and createdShards == sealedShards.size())

  // --- Weak-fairness liveness properties ---

  val stateVars = (
    ledgerState, writtenSeg, writtenSlot, primaryAlive, primaryArmed,
    logicalArmed, delivered, crashes, lsmWatermark, lsmComplete,
    logsMarked, sealedShards, createdShards, drainerPhase, drainerSeg,
    drainerSurviving, racedOnSameSegment
  )

  temporal Fairness = and {
    primaryWrite.weakFair(stateVars),
    closeLedger.weakFair(stateVars),
    lsmMarkClosedComplete.weakFair(stateVars),
    dropLedger.weakFair(stateVars),
    AllDrainers.forall(d => AllSegments.forall(s => scanSegment(d, s).weakFair(stateVars))),
    AllDrainers.forall(d => publishShard(d).weakFair(stateVars)),
    AllDrainers.forall(d => commitSegment(d).weakFair(stateVars)),
    AllSegments.forall(s => shardFireWakeup(s).weakFair(stateVars)),
  }

  temporal EventuallyDrainedAndDropped = Fairness implies always(eventually(ledgerState == "dropped"))
  temporal EventuallyWakeupDelivered = Fairness implies always(logicalArmed implies eventually(not(logicalArmed)))
}
