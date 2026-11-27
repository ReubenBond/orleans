// Grain runtime (L4): turns, staging, durability gating, cancellation,
// termination, Replace snapshots (R31), Rejected version rebase & Fenced
// resend (R14), and a minimal inbox and outbox, over Clog journals.
//
// See docs/design/runtime.md.
//
// Modeling choices:
// - Clog appears only through its journal contract. Each grain has one
//   journal: a list of accepted entries (`Append(cmds)` or `Replace(snap)`),
//   plus an opaque `journalVer` counter that advances on every accepted write
//   and may also advance on an internal Clog version rebase (`rebaseVersion`)
//   without changing logical content (`cmdsOf`).
// - Every write names the opaque `atVer` it expects and the activation-local
//   durability watermark `covers` it satisfies. A write lands only when
//   `journalVer == atVer`; otherwise it is `Rejected`.
// - On `Rejected` (R14): the activation re-reads `(cmdsOf(journal), journalVer)`.
//   If logical content matches its last accepted snapshot (`acceptedMem`,
//   modeling length + XXH3-128 digest), it rebases `atVer` and resends; if
//   content differs, the activation terminates.
// - On `ReplaceRequired` (R14/R31): Clog refuses an `Append` with no effect.
//   The activation keeps all in-memory commands (including the refused write
//   and any commands staged since) and sends a `Replace(mem)` snapshot between
//   segments, preserving held-message durability dependencies.
// - On `Fenced` (R14): Clog refuses a write with no effect while this node
//   still leads the range; the runtime resends the same record verbatim.
// - Ambiguous writes move into `limbo`, where they may still land at most once
//   while their `atVer` expectation holds, or be dropped.
// - The ledger-replica lease appears only through its contract: at most one activation
//   of a grain exists at a time, and a terminated activation never releases
//   the messages it held (they become zombies). A mutant removes that rule.
// - Messaging delivers released messages any number of times: `visible` is
//   the set of released messages, and receivers may act on each repeatedly.
// - A turn is a sequence of segments. A segment starts, writes its commands
//   one at a time, and ends; turns interleave only between segments.
// - Each activation has at most one write in flight (R30). Commands staged
//   while it is in flight join the next write.
// - Grain "A" serves client requests and owns an outbox; grain "B" owns an
//   inbox. Both follow the same runtime rules.
module grainRuntime {

  // ---------------------------------------------------------------- Types

  type TurnId = Client(int) | Handler(int)

  type TurnKind =
    // Writes in two segments.
    | Write
    // Reads across two segments.
    | Read
    // Adds an outbox message.
    | SendMsg(int)
    // The inbox handler: effect and completion in one segment.
    | Handle(int)
    // Mutant handler: effect and completion in separate segments.
    | HandleSplit(int)

  type Turn = { kind: TurnKind, seg: int, written: int }

  type Cmd =
    // A client write; `of` is the number of Incs its segment writes.
    | Inc({ turn: TurnId, seg: int, of: int })
    | OutboxAdd(int)
    | OutboxRemove(int)
    | InboxAccept(int)
    | InboxDone(int)
    // The inbox handler's effect for a message.
    | Effect(int)

  type Entry =
    | Append(List[Cmd])
    | Replace(List[Cmd])

  // A write sent to Clog: its entry, expected opaque JournalVersion, and the
  // activation-local durability watermark it satisfies upon acceptance.
  type Pend = { entry: Entry, atVer: int, covers: int }

  type MsgKind =
    // A response to a client turn; `value` is the number of Incs in the
    // grain's state when the response was created.
    | Resp({ turn: TurnId, value: int })
    // Outbox delivery of a message to the receiver's inbox.
    | Deliver(int)
    // The receiver's acknowledgement that its inbox accepted a message.
    | Ack(int)

  // `dep`: the activation-local write watermark the message depends on (R3).
  type Msg = { src: str, kind: MsgKind, dep: int }

  type Act = {
    // In-memory state: every command applied since recovery, in order.
    mem: List[Cmd],
    // Logical content of the journal as of the last accepted write / recovery
    // (models the (byte_len, XXH3-128 digest) check for R14 version rebase).
    acceptedMem: List[Cmd],
    // Expected Clog opaque JournalVersion for the next write.
    ver: int,
    // The staging buffer (R2).
    staged: List[Cmd],
    // The write in flight, if any: at most one (R30).
    pending: List[Pend],
    // True after Clog returns ReplaceRequired: next write MUST be Replace(mem).
    replaceNeeded: bool,
    // Local watermark after the last write sent, and after the last write accepted.
    sent: int,
    accepted: int,
    turns: TurnId -> Turn,
    // Turns whose segment is executing.
    running: Set[TurnId],
    // Messages created but not yet released (R3).
    held: Set[Msg],
  }


  // How a terminated activation's in-flight write resolves.
  type Fate = Landed | Dropped | Limbo

  // ------------------------------------------------------------ Constants

  const MAX_TURNS: int
  const MAX_MSGS: int
  const MAX_CRASHES: int
  const MAX_ENTRIES: int
  const MAX_REBASES: int

  pure val GRAINS = Set("A", "B")
  pure val MSGS = 0.to(MAX_MSGS - 1)
  pure val CLIENT_KINDS = Set("w", "r", "s")
  pure val TURN_IDS = 0.to(MAX_TURNS - 1).map(i => Client(i)).union(MSGS.map(m => Handler(m)))
  pure val FATES = Set(Landed, Dropped, Limbo)

  // ------------------------------------------------------------ State

  // Activations, keyed by grain: a grain is active exactly when it is a key.
  var acts: str -> Act
  // Clog: each grain's accepted entries and opaque version.
  var journal: str -> List[Entry]
  var journalVer: str -> int
  // Clog: ambiguous writes that may still land.
  var limbo: str -> List[Pend]
  // Released messages.
  var visible: Set[Msg]
  // Messages held by terminated activations.
  var zombie: Set[Msg]
  var nextTurn: int
  var nextMsg: int
  var crashes: int
  var rebases: int
  // Ghost flag: true if any accepted write failed to preserve prior logical content.
  var journalShrank: bool

  // ------------------------------------------------------------ Helpers

  // Clog journal logical content: last Replace payload (or empty) followed by
  // every subsequent Append payload.
  pure def cmdsOf(es: List[Entry]): List[Cmd] =
    es.foldl(List(), (acc, e) => match e {
      | Append(cs) => acc.concat(cs)
      | Replace(snap) => snap
    })

  pure def entryCmds(e: Entry): List[Cmd] = match e {
    | Append(cs) => cs
    | Replace(snap) => snap
  }

  pure def isPrefix(a: List[Cmd], b: List[Cmd]): bool =
    a.length() <= b.length() and a.indices().forall(i => a[i] == b[i])

  pure def countCmd(cs: List[Cmd], c: Cmd): int = cs.foldl(0, (n, x) => if (x == c) n + 1 else n)

  pure def hasCmd(cs: List[Cmd], c: Cmd): bool = countCmd(cs, c) > 0

  pure def countInc(cs: List[Cmd]): int =
    cs.foldl(0, (n, c) => match c { | Inc(_) => n + 1 | _ => n })

  pure def outboxOf(cs: List[Cmd]): Set[int] =
    MSGS.filter(m => hasCmd(cs, OutboxAdd(m)) and not(hasCmd(cs, OutboxRemove(m))))

  pure def removeTurn(ts: TurnId -> Turn, id: TurnId): TurnId -> Turn =
    ts.keys().exclude(Set(id)).mapBy(x => ts.get(x))

  pure def numSegs(k: TurnKind): int = match k {
    | Write => 2
    | Read => 2
    | HandleSplit(_) => 2
    | _ => 1
  }

  pure def responds(k: TurnKind): bool = match k {
    | Write => true
    | Read => true
    | SendMsg(_) => true
    | _ => false
  }

  // The commands that segment `t.seg` of turn `id` writes.
  pure def segCmds(id: TurnId, t: Turn): List[Cmd] = match t.kind {
    | Write => List(Inc({ turn: id, seg: t.seg, of: 2 }), Inc({ turn: id, seg: t.seg, of: 2 }))
    | Read => List()
    | SendMsg(m) => List(OutboxAdd(m), Inc({ turn: id, seg: 0, of: 1 }))
    | Handle(m) => List(Effect(m), InboxDone(m))
    | HandleSplit(m) => if (t.seg == 0) List(Effect(m)) else List(InboxDone(m))
  }

  pure def kindOf(s: str, m: int): TurnKind =
    if (s == "w") Write else if (s == "r") Read else SendMsg(m)


  // R3: the message depends on the write that will carry staged commands,
  // or on the last write sent when nothing is staged.
  pure def withMsg(a: Act, src: str, kind: MsgKind): Act = {
    val dep = if (a.staged.length() > 0) a.sent + 1 else a.sent
    { ...a, held: a.held.union(Set({ src: src, kind: kind, dep: dep })) }
  }

  pure def fresh(j: List[Entry], v: int): Act = {
    val cs = cmdsOf(j)
    {
      mem: cs, acceptedMem: cs, ver: v, staged: List(), pending: List(),
      replaceNeeded: false, sent: 0, accepted: 0,
      turns: Map(), running: Set(), held: Set(),
    }
  }

  // ------------------------------------------------------------ Actions

  action setAct(g: str, a: Act): bool = all {
    acts' = acts.set(g, a),
    journal' = journal, journalVer' = journalVer, limbo' = limbo, visible' = visible, zombie' = zombie,
    nextTurn' = nextTurn, nextMsg' = nextMsg, crashes' = crashes,
    rebases' = rebases, journalShrank' = journalShrank,
  }

  action init = all {
    acts' = Map(),
    journal' = GRAINS.mapBy(g => List()),
    journalVer' = GRAINS.mapBy(g => 0),
    limbo' = GRAINS.mapBy(g => List()),
    visible' = Set(),
    zombie' = Set(),
    nextTurn' = 0,
    nextMsg' = 0,
    crashes' = 0,
    rebases' = 0,
    journalShrank' = false,
  }

  // A new activation reads the grain's journal and opaque version. It may start
  // while an ambiguous write is still in limbo: Clog's read need not reflect it.
  action activate(g: str): bool = all {
    not(acts.keys().contains(g)),
    acts' = acts.put(g, fresh(journal.get(g), journalVer.get(g))),
    journal' = journal, journalVer' = journalVer, limbo' = limbo, visible' = visible, zombie' = zombie,
    nextTurn' = nextTurn, nextMsg' = nextMsg, crashes' = crashes,
    rebases' = rebases, journalShrank' = journalShrank,
  }

  // A client request starts a turn on grain A.
  action request(s: str): bool = all {
    acts.keys().contains("A"),
    {
      val a = acts.get("A")
      all {
        nextTurn < MAX_TURNS,
        s != "s" or nextMsg < MAX_MSGS,
        acts' = acts.set("A", ({ ...a, turns: a.turns.put(Client(nextTurn), { kind: kindOf(s, nextMsg), seg: 0, written: 0 }) })),
        nextTurn' = nextTurn + 1,
        nextMsg' = if (s == "s") nextMsg + 1 else nextMsg,
        journal' = journal, journalVer' = journalVer, limbo' = limbo, visible' = visible, zombie' = zombie,
        crashes' = crashes, rebases' = rebases, journalShrank' = journalShrank,
      }
    },
  }

  // A segment starts only when no other segment of the activation runs.
  action startSeg(g: str, id: TurnId): bool = all {
    acts.keys().contains(g),
    {
      val a = acts.get(g)
      all {
        a.turns.keys().contains(id),
        a.running.size() == 0,
        setAct(g, { ...a, running: a.running.union(Set(id)) }),
      }
    },
  }

  // The running segment applies its next command in memory and stages it.
  action writeCmd(g: str, id: TurnId): bool = all {
    acts.keys().contains(g),
    {
      val a = acts.get(g)
      all {
        a.running.contains(id),
        val t = a.turns.get(id)
        val cs = segCmds(id, t)
        all {
          t.written < cs.length(),
          setAct(g, { ...a,
            mem: a.mem.append(cs[t.written]),
            staged: a.staged.append(cs[t.written]),
            turns: a.turns.set(id, { ...t, written: t.written + 1 }),
          }),
        }
      }
    },
  }

  // The segment ends: the turn suspends at an await, or completes and
  // creates its response.
  action endSeg(g: str, id: TurnId): bool = all {
    acts.keys().contains(g),
    {
      val a = acts.get(g)
      all {
        a.running.contains(id),
        val t = a.turns.get(id)
        val base = { ...a, running: a.running.exclude(Set(id)) }
        val suspended = { ...base, turns: base.turns.set(id, { ...t, seg: t.seg + 1, written: 0 }) }
        val done = { ...base, turns: removeTurn(base.turns, id) }
        val completed = if (responds(t.kind)) withMsg(done, g, Resp({ turn: id, value: countInc(a.mem) })) else done
        all {
          t.written == segCmds(id, t).length(),
          setAct(g, if (t.seg + 1 < numSegs(t.kind)) suspended else completed),
        }
      }
    },
  }

  // R2 and R30: between segments, with no write in flight and no ReplaceRequired
  // pending, send everything staged as one Append write.
  action send(g: str): bool = all {
    acts.keys().contains(g),
    {
      val a = acts.get(g)
      all {
        a.running.size() == 0,
        a.pending.length() == 0,
        not(a.replaceNeeded),
        a.staged.length() > 0,
        journal.get(g).length() < MAX_ENTRIES,
        setAct(g, {
          ...a,
          pending: List({ entry: Append(a.staged), atVer: a.ver, covers: a.sent + 1 }),
          staged: List(),
          sent: a.sent + 1,
        }),
      }
    },
  }

  // R31: between segments, with no write in flight, send a Replace(mem) snapshot
  // — either required after ReplaceRequired (folding the refused write and any
  // newly staged commands while preserving `covers`) or as voluntary compaction.
  action sendReplace(g: str): bool = all {
    acts.keys().contains(g),
    {
      val a = acts.get(g)
      val nextCovers = if (a.staged.length() > 0) a.sent + 1 else a.sent
      all {
        a.running.size() == 0,
        a.pending.length() == 0,
        a.replaceNeeded or a.staged.length() > 0 or a.mem.length() > 0,
        journal.get(g).length() < MAX_ENTRIES,
        setAct(g, {
          ...a,
          pending: List({ entry: Replace(a.mem), atVer: a.ver, covers: nextCovers }),
          staged: List(),
          sent: nextCovers,
          replaceNeeded: false,
        }),
      }
    },
  }

  // Clog: an Append write is refused with ReplaceRequired (no effect on journal).
  // Activation keeps `a.mem` and `a.sent` intact and sets `replaceNeeded = true`.
  action replaceRequired(g: str): bool = all {
    acts.keys().contains(g),
    {
      val a = acts.get(g)
      all {
        a.pending.length() > 0,
        match a.pending[0].entry { | Append(_) => true | Replace(_) => false },
        setAct(g, { ...a, pending: List(), replaceNeeded: true }),
      }
    },
  }

  // R14: Clog answers Fenced while this node still leads the range (no effect on
  // journal). The activation resends the exact same record verbatim.
  action fencedResend(g: str): bool = all {
    acts.keys().contains(g),
    {
      val a = acts.get(g)
      all {
        a.pending.length() > 0,
        setAct(g, a),
      }
    },
  }

  // Clog internal version rebase (JS-I10): journal's opaque version advances
  // (e.g. ledger roll / archive compaction) without changing logical content.
  action rebaseVersion(g: str): bool = all {
    rebases < MAX_REBASES,
    journalVer' = journalVer.set(g, journalVer.get(g) + 1),
    rebases' = rebases + 1,
    acts' = acts, journal' = journal, limbo' = limbo,
    visible' = visible, zombie' = zombie,
    nextTurn' = nextTurn, nextMsg' = nextMsg, crashes' = crashes,
    journalShrank' = journalShrank,
  }

  // Clog: the write lands when the journal is at the opaque version it expects.
  action accept(g: str): bool = all {
    acts.keys().contains(g),
    {
      val a = acts.get(g)
      all {
        a.pending.length() > 0,
        journalVer.get(g) == a.pending[0].atVer,
        acts' = acts.set(g, {
          ...a,
          pending: List(),
          accepted: a.pending[0].covers,
          ver: journalVer.get(g) + 1,
          acceptedMem: cmdsOf(journal.get(g).append(a.pending[0].entry)),
        }),
        journal' = journal.set(g, journal.get(g).append(a.pending[0].entry)),
        journalVer' = journalVer.set(g, journalVer.get(g) + 1),
        journalShrank' = (journalShrank or not(isPrefix(cmdsOf(journal.get(g)), cmdsOf(journal.get(g).append(a.pending[0].entry))))),
        limbo' = limbo, visible' = visible, zombie' = zombie,
        nextTurn' = nextTurn, nextMsg' = nextMsg, crashes' = crashes,
        rebases' = rebases,
      }
    },
  }

  // Clog + R14: expected version does not match `journalVer`.
  // Activation re-reads the journal:
  // - If logical content matches `a.acceptedMem` (length + XXH3-128 digest),
  //   rebase expected version to `journalVer` and resend the same write.
  // - Otherwise another writer moved the journal on: terminate immediately.
  action reject(g: str): bool = all {
    acts.keys().contains(g),
    {
      val a = acts.get(g)
      all {
        a.pending.length() > 0,
        journalVer.get(g) != a.pending[0].atVer,
        if (cmdsOf(journal.get(g)) == a.acceptedMem) all {
          acts' = acts.set(g, {
            ...a,
            ver: journalVer.get(g),
            pending: List({ ...a.pending[0], atVer: journalVer.get(g) }),
          }),
          zombie' = zombie,
        } else all {
          acts' = acts.keys().exclude(Set(g)).mapBy(x => acts.get(x)),
          zombie' = zombie.union(a.held),
        },
        journal' = journal, journalVer' = journalVer, limbo' = limbo,
        visible' = visible, nextTurn' = nextTurn, nextMsg' = nextMsg,
        crashes' = crashes, rebases' = rebases, journalShrank' = journalShrank,
      }
    },
  }

  // R3: release every held message whose dependency is accepted.
  action release(g: str): bool = all {
    acts.keys().contains(g),
    {
      val a = acts.get(g)
      {
        val ready = a.held.filter(m => m.dep <= a.accepted)
        all {
          ready.size() > 0,
          acts' = acts.set(g, ({ ...a, held: a.held.exclude(ready) })),
          visible' = visible.union(ready),
          journal' = journal, journalVer' = journalVer, limbo' = limbo, zombie' = zombie,
          nextTurn' = nextTurn, nextMsg' = nextMsg, crashes' = crashes,
          rebases' = rebases, journalShrank' = journalShrank,
        }
      }
    },
  }

  // R11: a turn that is not running a segment is dropped at its await; its
  // completed segments stay staged.
  action cancel(g: str, id: TurnId): bool = all {
    acts.keys().contains(g),
    {
      val a = acts.get(g)
      all {
        a.turns.keys().contains(id),
        not(a.running.contains(id)),
        setAct(g, { ...a, turns: removeTurn(a.turns, id) }),
      }
    },
  }

  // R14: termination by a crash, panic, lease lapse, or an Ambiguous or Moved
  // write. The in-flight write lands, has no effect, or goes into limbo;
  // memory, staging, turns, and held messages are discarded.
  action terminate(g: str, f: Fate): bool = all {
    acts.keys().contains(g),
    {
      val a = acts.get(g)
      val curJ = journal.get(g)
      all {
        crashes < MAX_CRASHES,
        f == Dropped or a.pending.length() > 0,
        if (f == Landed) journalVer.get(g) == a.pending[0].atVer else true,
        journal' = if (f == Landed) journal.set(g, curJ.append(a.pending[0].entry)) else journal,
        journalVer' = if (f == Landed) journalVer.set(g, journalVer.get(g) + 1) else journalVer,
        journalShrank' = if (f == Landed) (journalShrank or not(isPrefix(cmdsOf(curJ), cmdsOf(curJ.append(a.pending[0].entry))))) else journalShrank,
        limbo' = if (f == Limbo) limbo.set(g, limbo.get(g).append(a.pending[0])) else limbo,
        acts' = acts.keys().exclude(Set(g)).mapBy(x => acts.get(x)),
        zombie' = zombie.union(a.held),
        crashes' = crashes + 1,
        visible' = visible, nextTurn' = nextTurn, nextMsg' = nextMsg, rebases' = rebases,
      }
    },
  }

  // Clog: an ambiguous write lands late, at most once, while its version holds.
  action landLate(g: str): bool = {
    val l = limbo.get(g)
    all {
      l.length() > 0,
      journalVer.get(g) == l[0].atVer,
      journal' = journal.set(g, journal.get(g).append(l[0].entry)),
      journalVer' = journalVer.set(g, journalVer.get(g) + 1),
      journalShrank' = (journalShrank or not(isPrefix(cmdsOf(journal.get(g)), cmdsOf(journal.get(g).append(l[0].entry))))),
      limbo' = limbo.set(g, l.slice(1, l.length())),
      acts' = acts, visible' = visible, zombie' = zombie,
      nextTurn' = nextTurn, nextMsg' = nextMsg, crashes' = crashes, rebases' = rebases,
    }
  }

  // Clog: an ambiguous write is decided against.
  action dropLimbo(g: str): bool = {
    val l = limbo.get(g)
    all {
      l.length() > 0,
      limbo' = limbo.set(g, l.slice(1, l.length())),
      acts' = acts, journal' = journal, journalVer' = journalVer, visible' = visible, zombie' = zombie,
      nextTurn' = nextTurn, nextMsg' = nextMsg, crashes' = crashes,
      rebases' = rebases, journalShrank' = journalShrank,
    }
  }

  // The outbox pump sends a pending message, between segments.
  action pump(m: int): bool = all {
    acts.keys().contains("A"),
    {
      val a = acts.get("A")
      all {
        a.running.size() == 0,
        outboxOf(a.mem).contains(m),
        setAct("A", withMsg(a, "A", Deliver(m))),
      }
    },
  }

  // The inbox accepts a delivered message once, by message ID, and
  // acknowledges every delivery once its acceptance is durable.
  action receive(m: int): bool = all {
    acts.keys().contains("B"),
    {
      val a = acts.get("B")
      {
        val a1 = if (hasCmd(a.mem, InboxAccept(m))) a
                 else { ...a, mem: a.mem.append(InboxAccept(m)), staged: a.staged.append(InboxAccept(m)) }
        all {
          a.running.size() == 0,
          visible.exists(v => v.kind == Deliver(m)),
          setAct("B", withMsg(a1, "B", Ack(m))),
        }
      }
    },
  }

  // The inbox starts a handler turn for an accepted message it has not
  // completed.
  action inboxRun(m: int): bool = all {
    acts.keys().contains("B"),
    {
      val a = acts.get("B")
      all {
        hasCmd(a.mem, InboxAccept(m)),
        not(hasCmd(a.mem, InboxDone(m))),
        not(a.turns.keys().contains(Handler(m))),
        setAct("B", { ...a, turns: a.turns.put(Handler(m), { kind: Handle(m), seg: 0, written: 0 }) }),
      }
    },
  }

  // The outbox removes an acknowledged message.
  action ackReceive(m: int): bool = all {
    acts.keys().contains("A"),
    {
      val a = acts.get("A")
      all {
        a.running.size() == 0,
        visible.exists(v => v.kind == Ack(m)),
        outboxOf(a.mem).contains(m),
        setAct("A", { ...a, mem: a.mem.append(OutboxRemove(m)), staged: a.staged.append(OutboxRemove(m)) }),
      }
    },
  }

  action step = any {
    { nondet g = GRAINS.oneOf()
      activate(g) },
    { nondet s = CLIENT_KINDS.oneOf()
      request(s) },
    { nondet g = GRAINS.oneOf()
      nondet id = TURN_IDS.oneOf()
      startSeg(g, id) },
    { nondet g = GRAINS.oneOf()
      nondet id = TURN_IDS.oneOf()
      writeCmd(g, id) },
    { nondet g = GRAINS.oneOf()
      nondet id = TURN_IDS.oneOf()
      endSeg(g, id) },
    { nondet g = GRAINS.oneOf()
      send(g) },
    { nondet g = GRAINS.oneOf()
      sendReplace(g) },
    { nondet g = GRAINS.oneOf()
      replaceRequired(g) },
    { nondet g = GRAINS.oneOf()
      fencedResend(g) },
    { nondet g = GRAINS.oneOf()
      rebaseVersion(g) },
    { nondet g = GRAINS.oneOf()
      accept(g) },
    { nondet g = GRAINS.oneOf()
      reject(g) },
    { nondet g = GRAINS.oneOf()
      release(g) },
    { nondet g = GRAINS.oneOf()
      nondet id = TURN_IDS.oneOf()
      cancel(g, id) },
    { nondet g = GRAINS.oneOf()
      nondet f = FATES.oneOf()
      terminate(g, f) },
    { nondet g = GRAINS.oneOf()
      landLate(g) },
    { nondet g = GRAINS.oneOf()
      dropLimbo(g) },
    { nondet m = MSGS.oneOf()
      pump(m) },
    { nondet m = MSGS.oneOf()
      receive(m) },
    { nondet m = MSGS.oneOf()
      inboxRun(m) },
    { nondet m = MSGS.oneOf()
      ackReceive(m) },
  }

  // ------------------------------------------------------------ Invariants

  // What a released message claims holds in its sender's durable journal.
  def reflects(v: Msg): bool = {
    val p = cmdsOf(journal.get(v.src))
    match v.kind {
      | Resp(r) => r.value <= countInc(p)
      | Deliver(m) => hasCmd(p, OutboxAdd(m))
      | Ack(m) => hasCmd(p, InboxAccept(m))
    }
  }

  // Nil-externality: every released message depends only on accepted state.
  val nilExternality = visible.forall(v => reflects(v))

  // At most one segment of an activation runs at a time.
  val turnExclusive = acts.keys().forall(g => acts.get(g).running.size() <= 1)

  // Every journal entry (Append or Replace) holds whole segments.
  pure def entryWhole(cs: List[Cmd]): bool =
    cs.foldl(true, (ok, c) => ok and (match c {
      | Inc(r) => countCmd(cs, c) == r.of
      | _ => true
    }))

  val segmentAtomic = GRAINS.forall(g => journal.get(g).foldl(true, (ok, e) => ok and entryWhole(entryCmds(e))))

  // Logical journal content grows monotonically across Append and Replace writes.
  val journalMonotone = not(journalShrank)

  // Effectively once: the receiver's journal applies each message's effect
  // at most once, and only for messages the sender durably sent.
  val effectivelyOnce = MSGS.forall(m => {
    val b = cmdsOf(journal.get("B"))
    val a = cmdsOf(journal.get("A"))
    countCmd(b, Effect(m)) <= 1 and (not(hasCmd(b, Effect(m))) or hasCmd(a, OutboxAdd(m)))
  })

  val safety = nilExternality and turnExclusive and segmentAtomic and journalMonotone and effectivelyOnce

  // ------------------------------------------------------------ Mutants
  //
  // Each mutant breaks one rule of the design. Running the simulator with
  // `--step=<mutant>` must violate the invariant named above it.

  // Releases held messages without waiting for acceptance (R3).
  action releaseEarly(g: str): bool = all {
    acts.keys().contains(g),
    {
      val a = acts.get(g)
      all {
        a.held.size() > 0,
        acts' = acts.set(g, ({ ...a, held: Set() })),
        visible' = visible.union(a.held),
        journal' = journal, journalVer' = journalVer, limbo' = limbo, zombie' = zombie,
        nextTurn' = nextTurn, nextMsg' = nextMsg, crashes' = crashes,
        rebases' = rebases, journalShrank' = journalShrank,
      }
    },
  }

  // Creates a response depending on the last write sent, ignoring staged commands (R3).
  action endSegIgnoringStaged(g: str, id: TurnId): bool = all {
    acts.keys().contains(g),
    {
      val a = acts.get(g)
      all {
        a.running.contains(id),
        val t = a.turns.get(id)
        all {
          responds(t.kind),
          t.seg + 1 == numSegs(t.kind),
          t.written == segCmds(id, t).length(),
          setAct(g, { ...a,
            running: a.running.exclude(Set(id)),
            turns: removeTurn(a.turns, id),
            held: a.held.union(Set({ src: g, kind: Resp({ turn: id, value: countInc(a.mem) }), dep: a.sent })),
          }),
        }
      }
    },
  }

  // Sends an Append write while a segment runs (R2).
  action sendMidSegment(g: str): bool = all {
    acts.keys().contains(g),
    {
      val a = acts.get(g)
      all {
        a.running.size() > 0,
        a.pending.length() == 0,
        not(a.replaceNeeded),
        a.staged.length() > 0,
        journal.get(g).length() < MAX_ENTRIES,
        setAct(g, {
          ...a,
          pending: List({ entry: Append(a.staged), atVer: a.ver, covers: a.sent + 1 }),
          staged: List(),
          sent: a.sent + 1,
        }),
      }
    },
  }

  // Sends a Replace snapshot while a segment runs (R2 / R31).
  action sendReplaceMidSegment(g: str): bool = all {
    acts.keys().contains(g),
    {
      val a = acts.get(g)
      val nextCovers = if (a.staged.length() > 0) a.sent + 1 else a.sent
      all {
        a.running.size() > 0,
        a.pending.length() == 0,
        journal.get(g).length() < MAX_ENTRIES,
        setAct(g, {
          ...a,
          pending: List({ entry: Replace(a.mem), atVer: a.ver, covers: nextCovers }),
          staged: List(),
          sent: nextCovers,
          replaceNeeded: false,
        }),
      }
    },
  }

  // After ReplaceRequired, sends Append(staged) instead of Replace(mem),
  // dropping the refused write's commands while advancing `covers` (R31).
  action sendAppendIgnoringReplaceNeeded(g: str): bool = all {
    acts.keys().contains(g),
    {
      val a = acts.get(g)
      all {
        a.running.size() == 0,
        a.pending.length() == 0,
        a.replaceNeeded,
        a.staged.length() > 0,
        journal.get(g).length() < MAX_ENTRIES,
        setAct(g, {
          ...a,
          pending: List({ entry: Append(a.staged), atVer: a.ver, covers: a.sent + 1 }),
          staged: List(),
          sent: a.sent + 1,
          replaceNeeded: false,
        }),
      }
    },
  }

  // On ReplaceRequired, Replace sends only `acceptedMem` (dropping the refused
  // write) while still covering `a.sent` (R31).
  action sendStaleReplaceDropsRefused(g: str): bool = all {
    acts.keys().contains(g),
    {
      val a = acts.get(g)
      all {
        a.running.size() == 0,
        a.pending.length() == 0,
        a.replaceNeeded,
        journal.get(g).length() < MAX_ENTRIES,
        setAct(g, {
          ...a,
          pending: List({ entry: Replace(a.acceptedMem), atVer: a.ver, covers: a.sent }),
          replaceNeeded: false,
        }),
      }
    },
  }

  // On Rejected, rebases `atVer` to `journalVer` WITHOUT checking that logical
  // content still matches `a.acceptedMem` (R14). Checked by directed test
  // `rebaseWithoutDigestCheckTest`.
  action rejectRebaseUnchecked(g: str): bool = all {
    acts.keys().contains(g),
    {
      val a = acts.get(g)
      all {
        a.pending.length() > 0,
        journalVer.get(g) != a.pending[0].atVer,
        cmdsOf(journal.get(g)) != a.acceptedMem,
        setAct(g, {
          ...a,
          ver: journalVer.get(g),
          pending: List({ ...a.pending[0], atVer: journalVer.get(g) }),
        }),
      }
    },
  }

  // On Fenced, clears pending and advances `accepted` instead of resending (R14).
  action fencedDropsWrite(g: str): bool = all {
    acts.keys().contains(g),
    {
      val a = acts.get(g)
      all {
        a.pending.length() > 0,
        setAct(g, { ...a, pending: List(), accepted: a.pending[0].covers }),
      }
    },
  }

  // Starts a segment while another runs (one segment at a time).
  action startSegConcurrent(g: str, id: TurnId): bool = all {
    acts.keys().contains(g),
    {
      val a = acts.get(g)
      all {
        a.turns.keys().contains(id),
        not(a.running.contains(id)),
        setAct(g, { ...a, running: a.running.union(Set(id)) }),
      }
    },
  }

  // Cancellation discards the staging buffer, though memory keeps the
  // commands (R11).
  action cancelDiscardsStaged(g: str, id: TurnId): bool = all {
    acts.keys().contains(g),
    {
      val a = acts.get(g)
      all {
        a.turns.keys().contains(id),
        not(a.running.contains(id)),
        setAct(g, { ...a, turns: removeTurn(a.turns, id), staged: List() }),
      }
    },
  }

  // A terminated activation releases what it held (no lease check).
  action releaseZombie = all {
    zombie.size() > 0,
    visible' = visible.union(zombie),
    zombie' = Set(),
    acts' = acts, journal' = journal, journalVer' = journalVer, limbo' = limbo,
    nextTurn' = nextTurn, nextMsg' = nextMsg, crashes' = crashes,
    rebases' = rebases, journalShrank' = journalShrank,
  }

  // Runs the handler again for a message already completed (no dedup).
  action inboxRunNoDedup(m: int): bool = all {
    acts.keys().contains("B"),
    {
      val a = acts.get("B")
      all {
        hasCmd(a.mem, InboxAccept(m)),
        not(a.turns.keys().contains(Handler(m))),
        setAct("B", { ...a, turns: a.turns.put(Handler(m), { kind: Handle(m), seg: 0, written: 0 }) }),
      }
    },
  }

  // Runs a handler whose effect and completion are in separate segments.
  action inboxRunSplit(m: int): bool = all {
    acts.keys().contains("B"),
    {
      val a = acts.get("B")
      all {
        hasCmd(a.mem, InboxAccept(m)),
        not(hasCmd(a.mem, InboxDone(m))),
        not(a.turns.keys().contains(Handler(m))),
        setAct("B", { ...a, turns: a.turns.put(Handler(m), { kind: HandleSplit(m), seg: 0, written: 0 }) }),
      }
    },
  }

  // A late ambiguous write lands without Clog's version check. Random simulation
  // does not reach the roughly 30-step path that shows it, so it is checked by
  // the directed scenario noVersionCheckTest instead of the mutant gate.
  action landLateUnchecked(g: str): bool = {
    val l = limbo.get(g)
    all {
      l.length() > 0,
      journal' = journal.set(g, journal.get(g).append(l[0].entry)),
      journalVer' = journalVer.set(g, journalVer.get(g) + 1),
      journalShrank' = (journalShrank or not(isPrefix(cmdsOf(journal.get(g)), cmdsOf(journal.get(g).append(l[0].entry))))),
      limbo' = limbo.set(g, l.slice(1, l.length())),
      acts' = acts, visible' = visible, zombie' = zombie,
      nextTurn' = nextTurn, nextMsg' = nextMsg, crashes' = crashes, rebases' = rebases,
    }
  }

  // breaks: nilExternality
  action mutantReleaseEarly = {
    nondet g = GRAINS.oneOf()
    any { step, releaseEarly(g) }
  }

  // breaks: nilExternality
  action mutantDepIgnoresStaged = {
    nondet g = GRAINS.oneOf()
    nondet id = TURN_IDS.oneOf()
    any { step, endSegIgnoringStaged(g, id) }
  }

  // breaks: segmentAtomic
  action mutantSendMidSegment = {
    nondet g = GRAINS.oneOf()
    any { step, sendMidSegment(g) }
  }

  // breaks: segmentAtomic
  action mutantReplaceMidSegment = {
    nondet g = GRAINS.oneOf()
    any { step, sendReplaceMidSegment(g) }
  }

  // breaks: nilExternality
  action mutantAppendAfterReplaceRequired = {
    nondet g = GRAINS.oneOf()
    any { step, sendAppendIgnoringReplaceNeeded(g) }
  }

  // breaks: nilExternality
  action mutantReplaceDropsRefused = {
    nondet g = GRAINS.oneOf()
    any { step, sendStaleReplaceDropsRefused(g) }
  }

  // breaks: nilExternality
  action mutantFencedDropsWrite = {
    nondet g = GRAINS.oneOf()
    any { step, fencedDropsWrite(g) }
  }

  // breaks: turnExclusive
  action mutantConcurrentSegments = {
    nondet g = GRAINS.oneOf()
    nondet id = TURN_IDS.oneOf()
    any { step, startSegConcurrent(g, id) }
  }

  // breaks: nilExternality
  action mutantCancelDiscardsStaged = {
    nondet g = GRAINS.oneOf()
    nondet id = TURN_IDS.oneOf()
    any { step, cancelDiscardsStaged(g, id) }
  }

  // breaks: nilExternality
  action mutantReleaseZombie = any { step, releaseZombie }

  // breaks: effectivelyOnce
  action mutantNoDedup = {
    nondet m = MSGS.oneOf()
    any { step, inboxRunNoDedup(m) }
  }

  // breaks: effectivelyOnce
  action mutantSplitHandler = {
    nondet m = MSGS.oneOf()
    any { step, inboxRunSplit(m) }
  }

}
