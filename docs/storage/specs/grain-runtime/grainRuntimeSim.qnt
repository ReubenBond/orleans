// -*- mode: Bluespec; -*-
// Configuration for random simulation and directed scenarios.
module grainRuntimeSim {
  import grainRuntime(MAX_TURNS = 3, MAX_MSGS = 1, MAX_CRASHES = 3, MAX_ENTRIES = 8, MAX_REBASES = 2).* from "./grainRuntime"

  // A: a send turn commits with a state change, and its message is delivered.
  run sendCommitted =
    init
      .then(activate("A"))
      .then(activate("B"))
      .then(request("s"))
      .then(startSeg("A", Client(0)))
      .then(writeCmd("A", Client(0)))
      .then(writeCmd("A", Client(0)))
      .then(endSeg("A", Client(0)))
      .then(send("A"))
      .then(accept("A"))
      .then(release("A"))
      .then(pump(0))
      .then(release("A"))

  // The send is accepted, handled once, and acknowledged.
  run sendTest =
    sendCommitted
      .then(receive(0))
      .then(inboxRun(0))
      .then(startSeg("B", Handler(0)))
      .then(writeCmd("B", Handler(0)))
      .then(writeCmd("B", Handler(0)))
      .then(endSeg("B", Handler(0)))
      .then(send("B"))
      .then(accept("B"))
      .then(release("B"))
      .then(ackReceive(0))
      .expect(safety and countCmd(cmdsOf(journal.get("B")), Effect(0)) == 1)

  // The receiver crashes after accepting; a redelivery is deduplicated, and
  // the handler runs once after reactivation.
  run receiverCrashTest =
    sendCommitted
      .then(receive(0))
      .then(send("B"))
      .then(accept("B"))
      .then(terminate("B", Dropped))
      .then(activate("B"))
      .then(receive(0))
      .then(inboxRun(0))
      .then(startSeg("B", Handler(0)))
      .then(writeCmd("B", Handler(0)))
      .then(writeCmd("B", Handler(0)))
      .then(endSeg("B", Handler(0)))
      .then(send("B"))
      .then(accept("B"))
      .then(release("B"))
      .expect(safety and countCmd(cmdsOf(journal.get("B")), Effect(0)) == 1)

  // The handler's write is ambiguous. A new activation runs the handler
  // again; the late write lands first, and the version check rejects the
  // new activation's write, so the effect applies once.
  run ambiguousHandler =
    sendCommitted
      .then(receive(0))
      .then(send("B"))
      .then(accept("B"))
      .then(inboxRun(0))
      .then(startSeg("B", Handler(0)))
      .then(writeCmd("B", Handler(0)))
      .then(writeCmd("B", Handler(0)))
      .then(endSeg("B", Handler(0)))
      .then(send("B"))
      .then(terminate("B", Limbo))
      .then(activate("B"))
      .then(inboxRun(0))
      .then(startSeg("B", Handler(0)))
      .then(writeCmd("B", Handler(0)))
      .then(writeCmd("B", Handler(0)))
      .then(endSeg("B", Handler(0)))
      .then(send("B"))

  run ambiguousLateLandingTest =
    ambiguousHandler
      .then(landLate("B"))
      .then(reject("B"))
      .expect(safety and countCmd(cmdsOf(journal.get("B")), Effect(0)) == 1)

  // Without the version check, the late write duplicates the effect.
  run noVersionCheckTest =
    ambiguousHandler
      .then(accept("B"))
      .then(landLateUnchecked("B"))
      .expect(not(effectivelyOnce))

  // Without the R14 content-digest check on Rejected, rebasing after a late
  // ambiguous write duplicates the handler's effect.
  run rebaseWithoutDigestCheckTest =
    ambiguousHandler
      .then(landLate("B"))
      .then(rejectRebaseUnchecked("B"))
      .then(accept("B"))
      .expect(not(effectivelyOnce))

  // R14: Clog rebases the journal's opaque version without changing content;
  // Rejected triggers re-read -> digest matches -> rebase & resend succeeds.
  run versionRebaseResendTest =
    init
      .then(activate("A"))
      .then(request("w"))
      .then(startSeg("A", Client(0)))
      .then(writeCmd("A", Client(0)))
      .then(writeCmd("A", Client(0)))
      .then(endSeg("A", Client(0)))
      .then(startSeg("A", Client(0)))
      .then(writeCmd("A", Client(0)))
      .then(writeCmd("A", Client(0)))
      .then(endSeg("A", Client(0)))
      .then(send("A"))
      .then(rebaseVersion("A"))
      .then(reject("A"))
      .then(accept("A"))
      .then(release("A"))
      .expect(safety and visible.exists(v => v.kind == Resp({ turn: Client(0), value: 4 })))

  // R31: Append gets ReplaceRequired while a second turn has staged commands;
  // Replace(mem) folds both into one snapshot and releases both responses.
  run replaceRequiredFoldsRefusedAndStagedTest =
    init
      .then(activate("A"))
      .then(request("w"))
      .then(request("r"))
      .then(startSeg("A", Client(0)))
      .then(writeCmd("A", Client(0)))
      .then(writeCmd("A", Client(0)))
      .then(endSeg("A", Client(0)))
      .then(send("A"))
      .then(startSeg("A", Client(0)))
      .then(writeCmd("A", Client(0)))
      .then(writeCmd("A", Client(0)))
      .then(endSeg("A", Client(0)))
      .then(startSeg("A", Client(1)))
      .then(endSeg("A", Client(1)))
      .then(startSeg("A", Client(1)))
      .then(endSeg("A", Client(1)))
      .then(replaceRequired("A"))
      .then(sendReplace("A"))
      .then(accept("A"))
      .then(release("A"))
      .expect(
        safety
          and visible.exists(v => v.kind == Resp({ turn: Client(0), value: 4 }))
          and visible.exists(v => v.kind == Resp({ turn: Client(1), value: 4 }))
          and countInc(cmdsOf(journal.get("A"))) == 4
      )

  // R14: Fenced write resends cleanly and commits without dropping state.
  run fencedResendTest =
    init
      .then(activate("A"))
      .then(request("w"))
      .then(startSeg("A", Client(0)))
      .then(writeCmd("A", Client(0)))
      .then(writeCmd("A", Client(0)))
      .then(endSeg("A", Client(0)))
      .then(startSeg("A", Client(0)))
      .then(writeCmd("A", Client(0)))
      .then(writeCmd("A", Client(0)))
      .then(endSeg("A", Client(0)))
      .then(send("A"))
      .then(fencedResend("A"))
      .then(accept("A"))
      .then(release("A"))
      .expect(safety and visible.exists(v => v.kind == Resp({ turn: Client(0), value: 4 })))

  // A cancelled write's completed segment commits, and a later read reports
  // it once it is accepted.
  run cancelThenReadTest =
    init
      .then(activate("A"))
      .then(request("w"))
      .then(request("r"))
      .then(startSeg("A", Client(0)))
      .then(writeCmd("A", Client(0)))
      .then(writeCmd("A", Client(0)))
      .then(endSeg("A", Client(0)))
      .then(cancel("A", Client(0)))
      .then(startSeg("A", Client(1)))
      .then(endSeg("A", Client(1)))
      .then(startSeg("A", Client(1)))
      .then(endSeg("A", Client(1)))
      .then(send("A"))
      .then(accept("A"))
      .then(release("A"))
      .expect(safety and visible.exists(v => v.kind == Resp({ turn: Client(1), value: 2 })))

  // A handler split across segments applies its effect twice after a crash
  // between them.
  run splitHandlerTest =
    sendCommitted
      .then(receive(0))
      .then(send("B"))
      .then(accept("B"))
      .then(inboxRunSplit(0))
      .then(startSeg("B", Handler(0)))
      .then(writeCmd("B", Handler(0)))
      .then(endSeg("B", Handler(0)))
      .then(send("B"))
      .then(accept("B"))
      .then(terminate("B", Dropped))
      .then(activate("B"))
      .then(inboxRunSplit(0))
      .then(startSeg("B", Handler(0)))
      .then(writeCmd("B", Handler(0)))
      .then(endSeg("B", Handler(0)))
      .then(send("B"))
      .then(accept("B"))
      .expect(not(safety))

  // A terminated activation's held response, released without a lease
  // check, reports a write that never landed.
  run zombieReleaseTest =
    init
      .then(activate("A"))
      .then(request("w"))
      .then(startSeg("A", Client(0)))
      .then(writeCmd("A", Client(0)))
      .then(writeCmd("A", Client(0)))
      .then(endSeg("A", Client(0)))
      .then(startSeg("A", Client(0)))
      .then(writeCmd("A", Client(0)))
      .then(writeCmd("A", Client(0)))
      .then(endSeg("A", Client(0)))
      .then(send("A"))
      .then(terminate("A", Dropped))
      .then(releaseZombie)
      .expect(not(safety))
}

