using Xunit.Sdk;
using Xunit.v3;

// Simulations share the code under test's process-wide state, such as the ArcBuffer page pool, whose controlled lock
// one simulation can find held by another's thread, which adds steps to a seed's run. A simulation project's tests
// therefore run one at a time, so a seed takes the same steps in every process (test/Durable.Storage/docs/spec/verification.md §7.4).
[assembly: Parallelization(Mode = ParallelMode.None)]
