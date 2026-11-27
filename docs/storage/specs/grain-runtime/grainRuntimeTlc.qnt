// -*- mode: Bluespec; -*-
// Standalone TLC wrapper for grainRuntime so Apalache translates only
// grainRuntime without bundling grainRuntimeSim's directed test runs.
module grainRuntimeTlc {
  import grainRuntime(
    MAX_TURNS = 1,
    MAX_MSGS = 1,
    MAX_CRASHES = 1,
    MAX_ENTRIES = 2,
    MAX_REBASES = 1
  ).* from "./grainRuntime"
}
