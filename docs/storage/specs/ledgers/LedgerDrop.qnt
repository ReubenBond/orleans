// -*- mode: Bluespec; -*-
/**
 * Dropping a drained closed ledger (ledgers.md §8.5; LG-I4, LG-I5, LG-I7,
 * LR-I3, LP-I5, LR-I7, LR-I8, LP-I7, and LS-I2).
 *
 * One address in the final window of ledger X, replicated on N nodes with
 * quorum Q, as in specs/ledger-protocol/LedgerProtocol.tla. A node holds no state for X
 * until a write, a recovery write, or a seal creates its replica, which it does
 * unless it holds X's tombstone. The former primary's writer write is
 * outstanding: copies reach nodes at any time, including after X is dropped, and
 * answers may be lost. A recovery races it: it seals the replicas, reads the
 * address, and either copies the DATA it found with recovery writes or, once
 * N - Q + 1 sealed replicas report the address absent, ends X there. Seals and
 * recovery writes are idempotent and retried, so each may land on any node at
 * any later time. Once the address is decided, the Archive drains X, X leaves the
 * ledger map, and each node's reconciliation drops each replica its view of the
 * map revokes, with its DATA and its seal, recording the tombstone, sealed and
 * dropped, in one catalog write. A request which meets the drop waits for that
 * write and answers Dropped only once it is durable. Creation reads only the
 * node's catalog, and a restart keeps every replica, seal, and tombstone, so a
 * node's view decides only what its reconciliation drops.
 *
 * X is S10 and Y S9: one map write, the split of S7, installed both and listed
 * them with every node. Each node's view starts before that write and advances,
 * at its own pace, to the current map. A view revokes a ledger whose ID lies
 * below its NextLedgerId and which it does not list with the node (ledgers.md
 * §8.5), so a view older than the split revokes neither, though Y's first write
 * may already have created Y's replica on the node, from its sender's map. Y's
 * first write may also come after the node dropped X, which a floor at the
 * highest dropped ID would refuse.
 *
 * The argument (LR-I7): recovery sealed N - Q + 1 nodes, and each keeps its
 * replica sealed until it drops X, and X's tombstone from then on, so no request
 * creates X there again. At most Q - 1 nodes, those never sealed, accept the
 * former primary's write after the close, and it reaches no write quorum. The
 * writer resolves Sealed or Dropped from the ledger map (LG-I5): while X is
 * listed, its recovered tail decides; afterwards the outcome is Ambiguous.
 */
module LedgerDrop {
  const Replicas: Set[int]
  const Quorum: int
  const Mutation: str
  // A crash may interrupt a drop split into two catalog writes (the drop-split
  // mutants), or lose a drop answered before its catalog write is durable (the
  // drop-volatile mutant); the split mutants' control cases run without crashes.
  const Crashes: bool

  type Answer = NoAnswer | Written | SealedAnswer | DroppedAnswer
  type Outcome = Unknown | Durable | Fenced | Ambiguous

  type State = {
    // X has left the ledger map.
    unlisted: bool,
    // Each node's view of the map, which only advances: 0 before the split which
    // installed X and Y, 1 with both listed, 2 once X is removed.
    view: int -> int,
    // The node holds X's replica (its catalog lists it).
    held: int -> bool,
    // The replica holds the address's DATA.
    data: int -> bool,
    // The replica's durable seal record.
    sealed: int -> bool,
    // X's tombstone in the node's catalog: X is sealed and dropped there.
    tomb: int -> bool,
    // A drop split into two catalog writes, between them: the drop-split
    // mutants only. Requests wait at the catalog gate meanwhile.
    deleting: int -> bool,
    pending: int -> bool,
    // A drop which answers Dropped before its catalog write is durable: the
    // drop-volatile mutant only. Until the write, the WAL still holds the
    // replica, its DATA, and its seal.
    volatileDrop: int -> bool,
    // The node answered a request for X Dropped.
    droppedAnswered: int -> bool,
    // The writer's writes in flight.
    creates: Set[int],
    // The recovery has begun sealing.
    recovering: bool,
    // The recovery found the address's DATA on a replica and copies it.
    found: bool,
    resp: int -> Answer,
    dataDecided: bool,
    endDecided: bool,
    drained: bool,
    // Y's replica on the node its first write reaches, and Y's tombstone there.
    yHeld: bool,
    yTomb: bool,
    // Y's first write created its replica on a node which had dropped X.
    lowerCreatedAfterDrop: bool,
    // A stray: a request created X's replica on a node which held none while
    // another node held X's tombstone; and a node's reconciliation dropped one.
    strayHeld: int -> bool,
    strayCreated: bool,
    strayDropped: bool,
    outcome: Outcome,
  }

  var s: State

  pure def coverage: int = Replicas.size() - Quorum + 1
  // The node Y's first write reaches.
  pure def yNode: int = Replicas.fold(Replicas.size() * 1000, (a, r) => if (r < a) r else a)

  // Ledger IDs and the views' NextLedgerId: S7 split into S9 (Y) and S10 (X).
  pure val yId: int = 9
  pure val xId: int = 10
  pure def nextId(v: int): int = if (v == 0) 9 else 11
  pure def listsX(v: int): bool = v == 1
  pure def listsY(v: int): bool = v >= 1

  // A view revokes a ledger whose ID lies below its NextLedgerId and which it
  // does not list with the node (ledgers.md §8.5). The revoke-unlisted mutant
  // drops whatever the view does not list.
  pure def revokes(v: int, id: int, listed: bool): bool =
    (Mutation == "revoke-unlisted" or id < nextId(v)) and not(listed)

  action init = s' = {
    unlisted: false,
    view: Replicas.mapBy(_ => 0),
    held: Replicas.mapBy(_ => false),
    data: Replicas.mapBy(_ => false),
    sealed: Replicas.mapBy(_ => false),
    tomb: Replicas.mapBy(_ => false),
    deleting: Replicas.mapBy(_ => false),
    pending: Replicas.mapBy(_ => false),
    volatileDrop: Replicas.mapBy(_ => false),
    droppedAnswered: Replicas.mapBy(_ => false),
    creates: Replicas,
    recovering: false,
    found: false,
    resp: Replicas.mapBy(_ => NoAnswer),
    dataDecided: false,
    endDecided: false,
    drained: false,
    yHeld: false,
    yTomb: false,
    lowerCreatedAfterDrop: false,
    strayHeld: Replicas.mapBy(_ => false),
    strayCreated: false,
    strayDropped: false,
    outcome: Unknown,
  }

  // The node holds no replica and X's tombstone (LR-I7): it answers Dropped and
  // creates nothing.
  def answersDropped(t: State, r: int): bool = not(t.held.get(r)) and t.tomb.get(r)

  // The catalog gate is free: no drop of X is between its two catalog writes.
  def gateFree(t: State, r: int): bool = not(t.deleting.get(r)) and not(t.pending.get(r))

  // Sealed replicas the node holds.
  def isSealed(t: State, r: int): bool = t.held.get(r) and t.sealed.get(r)

  // A request which creates X's replica on a node which holds none, while another
  // node holds X's tombstone: a stray.
  def stray(t: State, r: int): bool =
    not(t.held.get(r)) and Replicas.exists(q => q != r and t.tomb.get(q))

  // A creation on node r by a request: the replica, and whether it is a stray.
  def create(t: State, r: int): State =
    if (t.held.get(r)) t
    else {
      ...t,
      held: t.held.set(r, true),
      strayHeld: t.strayHeld.set(r, stray(t, r)),
      strayCreated: t.strayCreated or stray(t, r),
    }

  // Q replicas holding the DATA decide it the moment they form; decisions are
  // irrevocable. The drain reads the decision, which the ledger's recovered tail
  // then fixes; DATA written after the drain is garbage in a dropped ledger.
  def decided(t: State): State = if (t.drained) t else {
    ...t,
    dataDecided: t.dataDecided or Replicas.filter(r => t.data.get(r)).size() >= Quorum,
  }

  // The former primary's writer write: a node which holds the tombstone answers
  // Dropped, a sealed replica Sealed, and any other node creates the replica if
  // it holds none, unsealed, and the DATA if absent (LR-I7, LR-I8).
  action deliverCreate(r: int, respond: bool): bool =
    val answer = if (answersDropped(s, r)) DroppedAnswer else if (isSealed(s, r)) SealedAnswer else Written
    val t = if (answer == Written) { ...create(s, r), data: s.data.set(r, true) } else s
    all {
      s.creates.contains(r), gateFree(s, r),
      s' = decided({
        ...t,
        creates: s.creates.exclude(Set(r)),
        resp: if (respond) s.resp.set(r, answer) else s.resp,
        droppedAnswered: s.droppedAnswered.set(r, s.droppedAnswered.get(r) or answer == DroppedAnswer),
      }),
    }

  // The writer retries its identical write.
  action retry(r: int): bool = all {
    s.outcome == Unknown,
    not(s.creates.contains(r)),
    s' = { ...s, creates: s.creates.union(Set(r)) },
  }

  // A recovery seals every replica.
  action beginRecovery: bool = all {
    not(s.recovering), not(s.dataDecided), not(s.endDecided),
    s' = { ...s, recovering: true },
  }

  // A seal lands: it creates the replica, sealed, if the node holds none, and
  // its record is durable before the node answers. A node which holds the
  // tombstone creates nothing.
  action deliverSeal(r: int): bool = all {
    s.recovering, gateFree(s, r), not(answersDropped(s, r)), not(isSealed(s, r)),
    s' = { ...create(s, r), sealed: s.sealed.set(r, true) },
  }

  // Once N - Q + 1 replicas are sealed, the recovery reads the address. DATA on
  // any replica is copied with recovery writes to every replica. Absence counts
  // only from sealed replicas (LP-I7), and N - Q + 1 of them end X at the
  // address; the short-coverage mutant ends it on N - Q. Until the recovery finds
  // the DATA, nothing adds it to a sealed replica, so reading those replicas one
  // at a time, each after its seal, observes what recoveryEnd reads in one step.
  action recoveryFind(r: int): bool = all {
    s.recovering, Replicas.filter(q => isSealed(s, q)).size() >= coverage,
    not(s.found), not(s.endDecided), not(s.drained),
    s.held.get(r), s.data.get(r),
    s' = { ...s, found: true },
  }

  action recoveryEnd: bool =
    val needed = if (Mutation == "short-coverage") coverage - 1 else coverage
    all {
      s.recovering, not(s.found), not(s.endDecided), not(s.drained),
      Replicas.filter(r => isSealed(s, r) and not(s.data.get(r))).size() >= needed,
      s' = { ...s, endDecided: true },
    }

  // A recovery write lands: it creates the replica, unsealed, if the node holds
  // none, and the DATA if absent, sealed or not; a node which holds the
  // tombstone creates nothing.
  action deliverCopy(r: int): bool = all {
    s.found, gateFree(s, r), not(answersDropped(s, r)), not(s.data.get(r)),
    s' = decided({ ...create(s, r), data: s.data.set(r, true) }),
  }

  // The Archive drains the decided DATA, or finds X ended at the address, and X
  // becomes complete; the drop transition then removes it from the map.
  action drain: bool = all {
    s.dataDecided or s.endDecided,
    not(s.drained),
    s' = { ...s, drained: true },
  }

  action unlist: bool = all {
    s.drained, not(s.unlisted),
    s' = { ...s, unlisted: true },
  }

  // A node's view advances to the current map, at its own pace.
  action learn(r: int): bool =
    val current = if (s.unlisted) 2 else 1
    all {
      s.view.get(r) < current,
      s' = { ...s, view: s.view.set(r, current) },
    }

  // The node's reconciliation drops each replica it holds which its view revokes:
  // the drop is serialized with creation, and removes the replica, with its DATA
  // and its seal, and records X's tombstone, in one catalog write (LS-I2). It
  // runs on every view change and periodically, so it also drops a stray replica
  // a delayed request created after an earlier drop. The mutants break the drop:
  // drop-keeps-replica deletes the DATA and the seal but keeps the replica
  // listed, serving it; no-tombstone records no tombstone; non-atomic-drop deletes
  // the DATA and the seal first and removes the replica, recording its tombstone,
  // in a later catalog write; split-tombstone removes the replica first and
  // records its tombstone in a later catalog write. A crash between the two
  // writes loses the second. drop-volatile answers Dropped from the moment it
  // begins, in place of holding requests until its one catalog write is durable,
  // and a crash before that write keeps the replica as it was.
  def without(t: State, r: int): State =
    { ...t, held: t.held.set(r, false), data: t.data.set(r, false), sealed: t.sealed.set(r, false) }

  action reconcile(r: int): bool =
    val v = s.view.get(r)
    val t = { ...s, strayDropped: s.strayDropped or s.strayHeld.get(r), strayHeld: s.strayHeld.set(r, false) }
    val u =
      if (Mutation == "drop-keeps-replica") { ...t, data: t.data.set(r, false), sealed: t.sealed.set(r, false) }
      else if (Mutation == "non-atomic-drop")
        { ...t, data: t.data.set(r, false), sealed: t.sealed.set(r, false), deleting: t.deleting.set(r, true) }
      else if (Mutation == "split-tombstone") { ...without(t, r), pending: t.pending.set(r, true) }
      else if (Mutation == "no-tombstone") without(t, r)
      else if (Mutation == "drop-volatile")
        { ...t, held: t.held.set(r, false), tomb: t.tomb.set(r, true), volatileDrop: t.volatileDrop.set(r, true) }
      else { ...without(t, r), tomb: t.tomb.set(r, true) }
    all {
      revokes(v, xId, listsX(v)), s.held.get(r), gateFree(s, r),
      u != s,
      s' = u,
    }

  // The same reconciliation over Y's replica, which the map lists throughout.
  action reconcileY: bool =
    val v = s.view.get(yNode)
    all {
      revokes(v, yId, listsY(v)), s.yHeld,
      s' = { ...s, yHeld: false, yTomb: true },
    }

  // The drop-split mutants' later catalog write, and a crash which loses it: a
  // replica the non-atomic-drop mutant left listed stays listed, empty, and
  // unsealed until the node drops it again, and a replica the split-tombstone
  // mutant removed leaves no tombstone. The drop-volatile mutant's one catalog
  // write, and a crash which loses it: the node's WAL replay restores the
  // replica, with its DATA and its seal, and no tombstone.
  action finishDrop(r: int): bool = any {
    all {
      s.deleting.get(r),
      s' = { ...without(s, r), deleting: s.deleting.set(r, false), tomb: s.tomb.set(r, true) },
    },
    all {
      s.pending.get(r),
      s' = { ...s, pending: s.pending.set(r, false), tomb: s.tomb.set(r, true) },
    },
    all {
      s.volatileDrop.get(r),
      s' = { ...without(s, r), volatileDrop: s.volatileDrop.set(r, false) },
    },
  }

  action crash(r: int): bool =
    val lost = s.volatileDrop.get(r)
    all {
      Crashes, not(gateFree(s, r)) or lost,
      s' = {
        ...s,
        deleting: s.deleting.set(r, false),
        pending: s.pending.set(r, false),
        volatileDrop: s.volatileDrop.set(r, false),
        held: if (lost) s.held.set(r, true) else s.held,
        tomb: if (lost) s.tomb.set(r, false) else s.tomb,
      },
    }

  // Y's first write reaches its node, from a sender whose map lists Y with it
  // (LG-I7), and creates Y's replica unless the node holds Y's tombstone.
  action createY: bool = all {
    not(s.yHeld), not(s.yTomb),
    s' = { ...s, yHeld: true, lowerCreatedAfterDrop: s.tomb.get(yNode) },
  }

  // A Ledger's resolution: a write quorum of Written answers lands the
  // write; a Sealed or Dropped answer counts toward nothing (LP-I4, LP-I5) and
  // leaves it unknown until the close. While the map lists X, its recovered tail
  // places this address below it (DATA decided) or at it (end decided); once X is
  // unlisted, the write is Ambiguous (ledgers.md §2.4). The count-dropped mutant
  // counts a Dropped answer as Written; the ambiguous-as-fenced mutant reports
  // Fenced without the listing.
  action resolve: bool =
    val written = Replicas.filter(r =>
      s.resp.get(r) == Written or (Mutation == "count-dropped" and s.resp.get(r) == DroppedAnswer))
    val sealedAnswers = Replicas.filter(r => s.resp.get(r) == SealedAnswer or s.resp.get(r) == DroppedAnswer)
    val gone = if (Mutation == "ambiguous-as-fenced") Fenced else Ambiguous
    val listed = if (s.dataDecided) Durable else Fenced
    all {
      s.outcome == Unknown,
      if (written.size() >= Quorum) s' = { ...s, outcome: Durable }
      else all {
        sealedAnswers != Set() or s.unlisted,
        s.dataDecided or s.endDecided,
        s' = { ...s, outcome: if (s.unlisted) gone else listed },
      },
    }

  action step = any {
    nondet r = Replicas.oneOf()
    nondet flag = Set(true, false).oneOf()
    any {
      deliverCreate(r, flag), retry(r), deliverSeal(r), recoveryFind(r), deliverCopy(r),
      learn(r), reconcile(r), finishDrop(r), crash(r),
    },
    beginRecovery, recoveryEnd, drain, unlist, reconcileY, createY, resolve,
  }

  // Recovery's decision and a write quorum are exclusive and irrevocable.
  val DecisionsAgree = not(s.dataDecided and s.endDecided)

  // A write reported Durable is decided DATA, so the drain carried it.
  val DurableIsDecided = s.outcome == Durable implies s.dataDecided

  // JS-I4 (journals.md): "`Rejected`, `Moved`, `Throttled`, and `Fenced` have no effect."
  val FencedHasNoEffect = s.outcome == Fenced implies not(s.dataDecided)

  // Seal finality across the drop: once recovery ends X at the address, fewer
  // than Q nodes answered the writer Written there or could still do so, since
  // every other node holds a sealed replica or X's tombstone, or is mid-drop with
  // requests waiting at its gate.
  val EndAddressSealed = s.endDecided implies
    Replicas.filter(r =>
      s.resp.get(r) == Written
        or (gateFree(s, r) and s.held.get(r) and not(s.sealed.get(r)))
        or (gateFree(s, r) and not(s.held.get(r)) and not(s.tomb.get(r)))
    ).size() < Quorum

  // LG-I7: no node drops a ledger the map lists with it.
  val NeverDropsListed = not(s.yTomb) and (not(s.unlisted) implies Replicas.forall(r => not(s.tomb.get(r))))

  // LS-I2: a node holds a ledger's replica or its tombstone, never both.
  val TombstoneExcludesReplica =
    Replicas.forall(r => not(s.held.get(r) and s.tomb.get(r))) and not(s.yHeld and s.yTomb)

  // LR-I3, LS-I2: a Dropped answer is final. A node which answered a request
  // Dropped holds X's tombstone and no replica of X from then on, across crashes.
  val DroppedIsFinal =
    Replicas.forall(r => s.droppedAnswered.get(r) implies (s.tomb.get(r) and not(s.held.get(r))))

  // Negated reachability; TLC must reach each. Y's first write succeeds on a node
  // which dropped X, which a floor at the highest dropped ID would refuse; a
  // delayed request creates a stray, and a reconciliation drops it again; the
  // writer is answered Dropped.
  val NeverLowerCreatedAfterDrop = not(s.lowerCreatedAfterDrop)
  val NeverStrayAfterRemoval = not(s.strayCreated)
  val NeverStrayDroppedAgain = not(s.strayDropped)
  val NeverWriterAnsweredDropped = Replicas.forall(r => s.resp.get(r) != DroppedAnswer)
}
