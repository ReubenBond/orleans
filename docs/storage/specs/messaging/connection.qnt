// Connections, the handshake, in-process forwarding, and responses between a
// dialing process and an accepting process's runtime threads. Messaging is
// membership-free: peers are processes, identified by their InstanceId and
// authenticated by TLS.
//
// See docs/design/messaging.md: N2 (handshake), N12 (topology), N13 (owner
// threads), N18 (connection lifecycle), and S1 (identity).
//
// Modeling choices:
// - Processes are numbered instances. The dialing node and the accepting node
//   each restart as new instances; an old dialer instance may keep running.
//   Restarting the acceptor closes its connections and empties its queues.
// - Each instance has its own TLS key. A connection authenticates the key of
//   the instance that actually dialed (`dialer`) and claims an instance in
//   Hello (`claimed`); the key check compares them (S1). Hello names the
//   acceptor instance the dialer expects (`target`), or none (0) when dialing
//   a configured address.
// - One connection per dialer instance at a time to the acceptor. The
//   acceptor reads a dialer's connections on one thread, chosen by its
//   instance; each request names its destination thread, and the reader
//   forwards it through that thread's FIFO queue.
// - The dialer's owner thread holds a queue of frames not yet written, and a
//   pending-call table of requests written and not yet answered. Responses
//   return on the request's connection, and the owner routes each to the
//   thread that sent the request.
// - A connection's byte stream is reliable and ordered. When the dialer's side
//   fails, its unwritten frames fail with NotSent and its pending calls fail
//   with an unknown outcome, while the acceptor may still read the bytes it
//   already received.
// - Process crashes and handshake timeouts are not separate actions: they only
//   disable later actions, so for safety they are covered by idle processes
//   and failed streams.
// - `delivered`, `responses`, and `notSent` are ghost logs; the invariants are
//   stated over them.
module connection {

  // ---------------------------------------------------------------- Types

  type State = Pending | Open | Closed

  // A request: its ID, destination thread, and the dialer thread that sent it.
  type Msg = { id: int, dest: int, caller: int }

  type Conn = {
    // The instance that actually dialed, whose key TLS authenticated.
    dialer: int,
    // The instance claimed in Hello.
    claimed: int,
    // The acceptor instance the dialer expects; 0 when dialing a configured address.
    target: int,
    // The acceptor instance whose listener accepted the socket.
    acceptor: int,
    // The acceptor's side of the connection.
    state: State,
    // The handshake completed, so the dialer may write.
    accepted: bool,
    // The dialer's side failed or was closed.
    broken: bool,
    // Dialer: enqueued, not yet written.
    queued: List[Msg],
    // Ghost: every request written, in order.
    written: List[Msg],
    // Written, not yet read by the acceptor.
    inflight: List[Msg],
    // Dialer's pending-call table: request ID to calling thread.
    pending: int -> int,
    // Responses written by the acceptor, not yet read by the dialer.
    replies: List[Reply],
  }

  // A request the reader forwarded, waiting in its destination thread's queue.
  type Item = { msg: int, dialer: int, claimed: int, target: int, acceptor: int, conn: int }

  type Delivery = { msg: int, dialer: int, claimed: int, target: int, acceptor: int, thread: int }

  // A response on the wire: the request ID, and whether the turn ran
  // (`Completed`) or never started (`NotStarted`).
  type Reply = { id: int, ran: bool }

  // A completed call, as its caller saw it.
  type Response = { msg: int, caller: int, ran: bool }

  // Each flag reintroduces one bug. All false is the design.
  type Bugs = {
    // Trust the claimed instance without comparing the TLS key.
    skipKeyCheck: bool,
    // Accept a Hello that names another acceptor instance.
    skipTargetCheck: bool,
    // Report written but unanswered requests as NotSent when the connection fails.
    notSentAfterWrite: bool,
    // Keep the older inbound connection from the same instance open.
    keepOlder: bool,
    // Keep reading a connection after the acceptor closed it.
    readAfterClose: bool,
    // Resend requests written on older connections on the newest one.
    resendOnReconnect: bool,
    // Answer NotStarted for a request whose turn already ran.
    notStartedAfterDispatch: bool,
  }

  pure val NO_BUGS: Bugs = {
    skipKeyCheck: false,
    skipTargetCheck: false,
    notSentAfterWrite: false,
    keepOlder: false,
    readAfterClose: false,
    resendOnReconnect: false,
    notStartedAfterDispatch: false,
  }

  // ------------------------------------------------------------ Constants

  // Instances are numbered 1..MAX_INST across both nodes.
  const MAX_INST: int
  const MAX_CONNS: int
  const MAX_MSGS: int
  const THREADS: int

  pure val threadIds = 0.to(THREADS - 1)

  // ------------------------------------------------------------ State

  // Dialer instances whose process is running.
  var dialers: Set[int]
  // The accepting node's current instance.
  var acceptor: int
  var nextInst: int
  var conns: List[Conn]
  // Each acceptor thread's queue of forwarded requests.
  var fwd: int -> List[Item]
  var nextMsg: int
  // Ghost logs.
  var delivered: List[Delivery]
  var responses: List[Response]
  var notSent: Set[int]
  var callers: int -> int

  // ------------------------------------------------------------ Helpers

  def closedByAcceptor(c: Conn): Conn = { ...c, state: Closed }

  // The dialer's side fails: unwritten frames fail with NotSent, pending calls
  // fail with an unknown outcome, and unread replies are lost.
  def failDialerSide(c: Conn): Conn =
    { ...c, broken: true, queued: [], pending: Map(), replies: [] }

  def notSentOn(c: Conn, bugs: Bugs): Set[int] = {
    val unwritten = c.queued.foldl(Set(), (s, m) => s.union(Set(m.id)))
    if (bugs.notSentAfterWrite) unwritten.union(c.pending.keys()) else unwritten
  }

  // True if `j` was dialed after `i` by the same instance.
  def newer(j: int, i: int): bool = j > i and conns[j].dialer == conns[i].dialer

  def isNewest(i: int): bool = conns.indices().forall(j => not(newer(j, i)))

  // ------------------------------------------------------------ Init

  action init = all {
    dialers' = Set(1),
    acceptor' = 2,
    nextInst' = 3,
    conns' = [],
    fwd' = threadIds.mapBy(t => []),
    nextMsg' = 1,
    delivered' = [],
    responses' = [],
    notSent' = Set(),
    callers' = Map(),
  }

  // ------------------------------------------------------------ Environment

  // The dialing node restarts as a new instance; the old one may keep running.
  action restartDialer: bool = all {
    nextInst <= MAX_INST,
    dialers' = dialers.union(Set(nextInst)),
    nextInst' = nextInst + 1,
    acceptor' = acceptor, conns' = conns, fwd' = fwd, nextMsg' = nextMsg,
    delivered' = delivered, responses' = responses, notSent' = notSent, callers' = callers,
  }

  // The accepting node restarts as a new instance. Its connections reset, so
  // each dialer side fails too.
  action restartAcceptor: bool = all {
    nextInst <= MAX_INST,
    acceptor' = nextInst,
    nextInst' = nextInst + 1,
    conns' = conns.foldl([], (acc, c) => acc.append(
      if (c.acceptor == acceptor) failDialerSide(closedByAcceptor(c)) else c)),
    notSent' = conns.foldl(notSent, (s, c) => if (c.acceptor == acceptor) s.union(notSentOn(c, NO_BUGS)) else s),
    fwd' = threadIds.mapBy(t => []),
    dialers' = dialers, nextMsg' = nextMsg, delivered' = delivered, responses' = responses, callers' = callers,
  }

  // ------------------------------------------------------------ Handshake

  // A running dialer instance connects to the acceptor, claiming some instance
  // and naming the acceptor instance it expects, or none.
  action dial(p: int, claim: int, target: int): bool = all {
    dialers.contains(p),
    conns.length() < MAX_CONNS,
    conns' = conns.append({
      dialer: p, claimed: claim, target: target, acceptor: acceptor,
      state: Pending, accepted: false, broken: false,
      queued: [], written: [], inflight: [], pending: Map(), replies: [],
    }),
    dialers' = dialers, acceptor' = acceptor, nextInst' = nextInst, fwd' = fwd, nextMsg' = nextMsg,
    delivered' = delivered, responses' = responses, notSent' = notSent, callers' = callers,
  }

  // The acceptor answers Hello at once: a matching key and target accepts and
  // closes the older connection from the same instance; anything else rejects.
  action handshake(i: int, bugs: Bugs): bool = {
    val c = conns[i]
    val keyOk = bugs.skipKeyCheck or c.claimed == c.dialer
    val targetOk = bugs.skipTargetCheck or c.target == 0 or c.target == acceptor
    val accept = keyOk and targetOk
    all {
      c.acceptor == acceptor,
      c.state == Pending,
      not(c.broken),
      conns' = conns.indices().fold(conns, (cs, j) =>
        if (j == i)
          cs.replaceAt(j, if (accept) { ...c, state: Open, accepted: true } else closedByAcceptor(c))
        else if (accept and not(bugs.keepOlder) and cs[j].state == Open and cs[j].acceptor == c.acceptor
                 and cs[j].claimed == c.claimed)
          cs.replaceAt(j, closedByAcceptor(cs[j]))
        else cs),
      dialers' = dialers, acceptor' = acceptor, nextInst' = nextInst, fwd' = fwd, nextMsg' = nextMsg,
      delivered' = delivered, responses' = responses, notSent' = notSent, callers' = callers,
    }
  }

  // ------------------------------------------------------------ Requests

  // A dialer thread enqueues a request on its instance's newest accepted connection.
  action send(i: int, dest: int, caller: int): bool = all {
    conns[i].accepted,
    not(conns[i].broken),
    dialers.contains(conns[i].dialer),
    isNewest(i),
    nextMsg <= MAX_MSGS,
    conns' = conns.replaceAt(i, { ...conns[i],
      queued: conns[i].queued.append({ id: nextMsg, dest: dest, caller: caller }) }),
    callers' = callers.put(nextMsg, caller),
    nextMsg' = nextMsg + 1,
    dialers' = dialers, acceptor' = acceptor, nextInst' = nextInst, fwd' = fwd,
    delivered' = delivered, responses' = responses, notSent' = notSent,
  }

  // The owner thread writes the next queued request and records its caller.
  action write(i: int): bool = {
    val c = conns[i]
    val m = c.queued[0]
    all {
      not(c.broken),
      c.queued.length() > 0,
      conns' = conns.replaceAt(i, { ...c,
        queued: c.queued.slice(1, c.queued.length()),
        written: c.written.append(m),
        inflight: c.inflight.append(m),
        pending: c.pending.put(m.id, m.caller) }),
      dialers' = dialers, acceptor' = acceptor, nextInst' = nextInst, fwd' = fwd, nextMsg' = nextMsg,
      delivered' = delivered, responses' = responses, notSent' = notSent, callers' = callers,
    }
  }

  // Bug: on a new connection, rewrite what older connections carried.
  action resend(i: int): bool = {
    val old = conns.indices().fold([], (acc, j) => if (newer(i, j)) acc.concat(conns[j].written) else acc)
    all {
      conns[i].accepted,
      not(conns[i].broken),
      old.length() > 0,
      conns' = conns.replaceAt(i, { ...conns[i], inflight: conns[i].inflight.concat(old) }),
      dialers' = dialers, acceptor' = acceptor, nextInst' = nextInst, fwd' = fwd, nextMsg' = nextMsg,
      delivered' = delivered, responses' = responses, notSent' = notSent, callers' = callers,
    }
  }

  // The reader reads the next request and forwards it to its destination
  // thread's queue (its own queue when it is the destination).
  action read(i: int, bugs: Bugs): bool = {
    val c = conns[i]
    val m = c.inflight[0]
    all {
      c.acceptor == acceptor,
      c.state == Open or (bugs.readAfterClose and c.accepted),
      c.inflight.length() > 0,
      conns' = conns.replaceAt(i, { ...c, inflight: c.inflight.slice(1, c.inflight.length()) }),
      fwd' = fwd.set(m.dest, fwd.get(m.dest).append({
        msg: m.id, dialer: c.dialer, claimed: c.claimed, target: c.target, acceptor: c.acceptor, conn: i })),
      dialers' = dialers, acceptor' = acceptor, nextInst' = nextInst, nextMsg' = nextMsg,
      delivered' = delivered, responses' = responses, notSent' = notSent, callers' = callers,
    }
  }

  // The reader answers a request NotStarted instead of forwarding it: the
  // target is unknown, not callable by this peer, or overloaded.
  action reject(i: int): bool = {
    val c = conns[i]
    val m = c.inflight[0]
    all {
      c.acceptor == acceptor,
      c.state == Open,
      c.inflight.length() > 0,
      conns' = conns.replaceAt(i, { ...c,
        inflight: c.inflight.slice(1, c.inflight.length()),
        replies: c.replies.append({ id: m.id, ran: false }) }),
      dialers' = dialers, acceptor' = acceptor, nextInst' = nextInst, fwd' = fwd, nextMsg' = nextMsg,
      delivered' = delivered, responses' = responses, notSent' = notSent, callers' = callers,
    }
  }

  // Thread t dispatches the next forwarded request and answers it Completed on
  // the request's connection, if the acceptor still has that connection open.
  action dispatch(t: int, bugs: Bugs): bool = {
    val q = fwd.get(t)
    val it = q[0]
    val c = conns[it.conn]
    all {
      q.length() > 0,
      fwd' = fwd.set(t, q.slice(1, q.length())),
      delivered' = delivered.append({
        msg: it.msg, dialer: it.dialer, claimed: it.claimed, target: it.target,
        acceptor: it.acceptor, thread: t }),
      conns' = if (c.state == Open)
          conns.replaceAt(it.conn, { ...c, replies: c.replies.append({ id: it.msg, ran: not(bugs.notStartedAfterDispatch) }) })
        else conns,
      dialers' = dialers, acceptor' = acceptor, nextInst' = nextInst, nextMsg' = nextMsg,
      responses' = responses, notSent' = notSent, callers' = callers,
    }
  }

  // The owner reads the next response and completes the pending call with it,
  // on the thread that sent the request. A response with no pending call is dropped.
  action readReply(i: int): bool = {
    val c = conns[i]
    val reply = c.replies[0]
    val id = reply.id
    all {
      not(c.broken),
      c.replies.length() > 0,
      conns' = conns.replaceAt(i, { ...c,
        replies: c.replies.slice(1, c.replies.length()),
        pending: c.pending.keys().exclude(Set(id)).mapBy(k => c.pending.get(k)) }),
      responses' = if (c.pending.keys().contains(id)) responses.append({ msg: id, caller: c.pending.get(id), ran: reply.ran }) else responses,
      dialers' = dialers, acceptor' = acceptor, nextInst' = nextInst, fwd' = fwd, nextMsg' = nextMsg,
      delivered' = delivered, notSent' = notSent, callers' = callers,
    }
  }

  // The dialer's side of a connection fails or is closed by a layer above
  // (`close(instance)`). The acceptor may still read what it already received.
  action breakConn(i: int, bugs: Bugs): bool = all {
    not(conns[i].broken),
    notSent' = notSent.union(notSentOn(conns[i], bugs)),
    conns' = conns.replaceAt(i, failDialerSide(conns[i])),
    dialers' = dialers, acceptor' = acceptor, nextInst' = nextInst, fwd' = fwd, nextMsg' = nextMsg,
    delivered' = delivered, responses' = responses, callers' = callers,
  }

  // The acceptor closes a connection: an I/O error, a protocol violation, or
  // the end of the stream after the dialer's side failed.
  action acceptorClose(i: int): bool = all {
    conns[i].state != Closed,
    conns' = conns.replaceAt(i, closedByAcceptor(conns[i])),
    dialers' = dialers, acceptor' = acceptor, nextInst' = nextInst, fwd' = fwd, nextMsg' = nextMsg,
    delivered' = delivered, responses' = responses, notSent' = notSent, callers' = callers,
  }

  // ------------------------------------------------------------ Step

  action envStep = any { restartDialer, restartAcceptor }

  action connStep(bugs: Bugs): bool = any {
    nondet p = 1.to(MAX_INST).oneOf()
    nondet claim = 1.to(MAX_INST).oneOf()
    nondet target = 0.to(MAX_INST).oneOf()
    all { claim < nextInst, target < nextInst, dial(p, claim, target) },
    nondet t = threadIds.oneOf()
    dispatch(t, bugs),
    all {
      conns.length() > 0,
      nondet i = conns.indices().oneOf()
      nondet dest = threadIds.oneOf()
      nondet caller = threadIds.oneOf()
      any {
        handshake(i, bugs),
        send(i, dest, caller),
        write(i),
        read(i, bugs),
        reject(i),
        readReply(i),
        breakConn(i, bugs),
        acceptorClose(i),
        all { bugs.resendOnReconnect, resend(i) },
      },
    },
  }

  action stepWith(bugs: Bugs): bool = any { envStep, connStep(bugs) }

  action step = stepWith(NO_BUGS)

  // ------------------------------------------------------------ Mutants

  // breaks: authenticated
  action mutantSkipKeyCheck = stepWith({ ...NO_BUGS, skipKeyCheck: true })

  // breaks: rightTarget
  action mutantSkipTargetCheck = stepWith({ ...NO_BUGS, skipTargetCheck: true })

  // breaks: noEffectOnNotSent
  action mutantNotSentAfterWrite = stepWith({ ...NO_BUGS, notSentAfterWrite: true })

  // breaks: outcomesSound
  action mutantNotStartedAfterDispatch = stepWith({ ...NO_BUGS, notStartedAfterDispatch: true })

  // The next three need a reconnect: an older connection's request must stay
  // unread, or be resent, while a newer connection is dialed, accepted,
  // written, and read. Random simulation rarely reaches that, so CI checks
  // them with TLC on connectionMutants instead of the shared gate, which runs
  // every `action mutant*`.

  // breaks: atMostOnce
  action tlcMutantResendOnReconnect = stepWith({ ...NO_BUGS, resendOnReconnect: true })

  // breaks: fifoWithGaps
  action tlcMutantKeepOlder = stepWith({ ...NO_BUGS, keepOlder: true })

  // breaks: fifoWithGaps
  action tlcMutantReadAfterClose = stepWith({ ...NO_BUGS, readAfterClose: true })

  // ------------------------------------------------------------ Properties

  // Every dispatched request comes from the instance its connection claims.
  val authenticated = delivered.indices().forall(k => delivered[k].claimed == delivered[k].dialer)

  // A request whose sender named an acceptor instance reaches only that instance.
  val rightTarget = delivered.indices().forall(k =>
    delivered[k].target == 0 or delivered[k].target == delivered[k].acceptor)

  // Each request is dispatched at most once.
  val atMostOnce = delivered.indices().forall(a => delivered.indices().forall(b =>
    a < b implies delivered[a].msg != delivered[b].msg))

  // From one sender instance to one thread of one acceptor instance,
  // dispatches follow send order; requests may be missing, never reordered.
  val fifoWithGaps = delivered.indices().forall(a => delivered.indices().forall(b =>
    (a < b and delivered[a].dialer == delivered[b].dialer
      and delivered[a].acceptor == delivered[b].acceptor
      and delivered[a].thread == delivered[b].thread)
    implies delivered[a].msg < delivered[b].msg))

  // A request that failed with NotSent was never dispatched.
  val noEffectOnNotSent = delivered.indices().forall(k => not(notSent.contains(delivered[k].msg)))

  // Each response completes the call of the thread that sent its request, at
  // most once.
  val responsesSound = and {
    responses.indices().forall(k => callers.get(responses[k].msg) == responses[k].caller),
    responses.indices().forall(a => responses.indices().forall(b =>
      a < b implies responses[a].msg != responses[b].msg)),
  }

  // A Completed response follows its request's dispatch, and a NotStarted
  // response's request is never dispatched.
  val outcomesSound = responses.indices().forall(k =>
    responses[k].ran == delivered.indices().exists(d => delivered[d].msg == responses[k].msg))

  val safety = and {
    authenticated,
    rightTarget,
    atMostOnce,
    fifoWithGaps,
    noEffectOnNotSent,
    responsesSound,
    outcomesSound,
  }
}

// Configuration for random simulation and directed scenarios.
module connectionSim {
  import connection(MAX_INST = 5, MAX_CONNS = 4, MAX_MSGS = 4, THREADS = 2).*

  // A call travels to another thread and back on the same connection.
  run callAndResponseTest =
    init
      .then(dial(1, 1, 2))
      .then(handshake(0, NO_BUGS))
      .then(send(0, 1, 0))
      .then(write(0))
      .then(read(0, NO_BUGS))
      .then(dispatch(1, NO_BUGS))
      .then(readReply(0))
      .expect(responses == [{ msg: 1, caller: 0, ran: true }] and safety)

  // A request the reader rejects completes NotStarted and is never dispatched.
  run notStartedTest =
    init
      .then(dial(1, 1, 2))
      .then(handshake(0, NO_BUGS))
      .then(send(0, 1, 0))
      .then(write(0))
      .then(reject(0))
      .then(readReply(0))
      .expect(responses == [{ msg: 1, caller: 0, ran: false }] and delivered.length() == 0 and safety)

  // Answering NotStarted after the turn ran claims no effect for a call that had one.
  run notStartedAfterDispatchTest =
    init
      .then(dial(1, 1, 2))
      .then(handshake(0, NO_BUGS))
      .then(send(0, 1, 0))
      .then(write(0))
      .then(read(0, NO_BUGS))
      .then(dispatch(1, { ...NO_BUGS, notStartedAfterDispatch: true }))
      .then(readReply(0))
      .expect(not(outcomesSound))

  // Dialing a configured address names no target and is accepted.
  run configuredAddressTest =
    init
      .then(dial(1, 1, 0))
      .then(handshake(0, NO_BUGS))
      .expect(conns[0].state == Open and safety)

  // A request still queued when the connection fails is NotSent and never
  // dispatched; a written one has an unknown outcome and may still be.
  run notSentVersusUnknownTest =
    init
      .then(dial(1, 1, 2))
      .then(handshake(0, NO_BUGS))
      .then(send(0, 0, 0))
      .then(send(0, 0, 0))
      .then(write(0))
      .then(breakConn(0, NO_BUGS))
      .then(read(0, NO_BUGS))
      .then(dispatch(0, NO_BUGS))
      .expect(notSent == Set(2) and delivered.length() == 1 and safety)

  // Reporting the written request as NotSent contradicts its dispatch.
  run notSentAfterWriteTest =
    init
      .then(dial(1, 1, 2))
      .then(handshake(0, NO_BUGS))
      .then(send(0, 0, 0))
      .then(write(0))
      .then(breakConn(0, { ...NO_BUGS, notSentAfterWrite: true }))
      .then(read(0, NO_BUGS))
      .then(dispatch(0, NO_BUGS))
      .expect(not(noEffectOnNotSent))

  // A newer connection from the same instance replaces the older one, so the
  // older one's unread request is lost rather than dispatched out of order.
  run newerReplacesOlderTest =
    init
      .then(dial(1, 1, 2))
      .then(handshake(0, NO_BUGS))
      .then(send(0, 1, 0))
      .then(write(0))
      .then(dial(1, 1, 2))
      .then(handshake(1, NO_BUGS))
      .then(send(1, 1, 0))
      .then(write(1))
      .then(read(1, NO_BUGS))
      .then(dispatch(1, NO_BUGS))
      .expect(conns[0].state == Closed and safety)

  // Keeping the older connection open dispatches its request after a newer one.
  run keepOlderReordersTest =
    init
      .then(dial(1, 1, 2))
      .then(handshake(0, NO_BUGS))
      .then(send(0, 1, 0))
      .then(write(0))
      .then(dial(1, 1, 2))
      .then(handshake(1, { ...NO_BUGS, keepOlder: true }))
      .then(send(1, 1, 0))
      .then(write(1))
      .then(read(1, NO_BUGS))
      .then(read(0, NO_BUGS))
      .then(dispatch(1, NO_BUGS))
      .then(dispatch(1, NO_BUGS))
      .expect(not(fifoWithGaps))

  // Reading a replaced connection after closing it reorders the same way.
  run readAfterCloseReordersTest =
    init
      .then(dial(1, 1, 2))
      .then(handshake(0, NO_BUGS))
      .then(send(0, 1, 0))
      .then(write(0))
      .then(dial(1, 1, 2))
      .then(handshake(1, NO_BUGS))
      .then(send(1, 1, 0))
      .then(write(1))
      .then(read(1, NO_BUGS))
      .then(read(0, { ...NO_BUGS, readAfterClose: true }))
      .then(dispatch(1, NO_BUGS))
      .then(dispatch(1, NO_BUGS))
      .expect(not(fifoWithGaps))

  // A Hello naming the previous acceptor instance is rejected.
  run staleTargetRejectedTest =
    init
      .then(restartAcceptor)
      .then(dial(1, 1, 2))
      .then(handshake(0, NO_BUGS))
      .expect(conns[0].state == Closed and safety)

  // Without the key check, one instance claims another's identity.
  run impersonationTest =
    init
      .then(restartDialer)
      .then(dial(1, 3, 2))
      .then(handshake(0, { ...NO_BUGS, skipKeyCheck: true }))
      .then(send(0, 0, 0))
      .then(write(0))
      .then(read(0, NO_BUGS))
      .then(dispatch(0, NO_BUGS))
      .expect(not(authenticated))

  // Resending on a new connection what an older one carried duplicates it.
  run resendDuplicatesTest =
    init
      .then(dial(1, 1, 2))
      .then(handshake(0, NO_BUGS))
      .then(send(0, 0, 0))
      .then(write(0))
      .then(read(0, NO_BUGS))
      .then(dispatch(0, NO_BUGS))
      .then(dial(1, 1, 2))
      .then(handshake(1, NO_BUGS))
      .then(resend(1))
      .then(read(1, NO_BUGS))
      .then(dispatch(0, NO_BUGS))
      .expect(not(atMostOnce))
}

// Small configuration for the mutant checks: one restart of either node, so
// random walks and TLC reach reconnect sequences quickly.
module connectionMutants {
  import connection(MAX_INST = 3, MAX_CONNS = 3, MAX_MSGS = 2, THREADS = 2).*
}

// Configuration for exhaustive model checking of safety with TLC.
module connectionTlc {
  import connection(MAX_INST = 3, MAX_CONNS = 2, MAX_MSGS = 2, THREADS = 2).*
}
