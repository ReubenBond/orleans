using Orleans.Testing.Simulation.Runtime.Scheduling;

namespace Orleans.Testing.Simulation.Runtime.Tests.Scheduling;

/// <summary>
/// Exhaustive coverage of the <see cref="SimulationOperation"/> state machine's legality table:
/// every legal edge is accepted and every one of the remaining edges is rejected. This is a pure
/// table test with no threads, so it pins the contract independently of the scheduler mechanics.
/// </summary>
public sealed class SimulationOperationStateMachineTests
{
    private static readonly SimulationOperationState[] AllStates = Enum.GetValues<SimulationOperationState>();

    private static readonly (SimulationOperationState From, SimulationOperationState To)[] LegalEdges =
    [
        (SimulationOperationState.Created, SimulationOperationState.Runnable),
        (SimulationOperationState.Created, SimulationOperationState.Canceled),
        (SimulationOperationState.Runnable, SimulationOperationState.Running),
        (SimulationOperationState.Runnable, SimulationOperationState.Canceled),
        (SimulationOperationState.Running, SimulationOperationState.Paused),
        (SimulationOperationState.Running, SimulationOperationState.Runnable),
        (SimulationOperationState.Running, SimulationOperationState.Completed),
        (SimulationOperationState.Running, SimulationOperationState.Faulted),
        (SimulationOperationState.Running, SimulationOperationState.Canceled),
        (SimulationOperationState.Paused, SimulationOperationState.Runnable),
        (SimulationOperationState.Paused, SimulationOperationState.Canceled),
    ];

    [Fact]
    public void EveryLegalEdgeIsPermitted()
    {
        foreach (var (from, to) in LegalEdges)
        {
            Assert.True(SimulationOperation.CanTransition(from, to), $"Expected {from} -> {to} to be legal.");
        }
    }

    [Fact]
    public void EveryEdgeNotInTheLegalSetIsRejected()
    {
        var legal = new HashSet<(SimulationOperationState, SimulationOperationState)>(LegalEdges);
        foreach (var from in AllStates)
        {
            foreach (var to in AllStates)
            {
                if (legal.Contains((from, to)))
                {
                    continue;
                }

                Assert.False(SimulationOperation.CanTransition(from, to), $"Expected {from} -> {to} to be illegal.");
            }
        }
    }

    [Fact]
    public void SelfTransitionsAreAlwaysIllegal()
    {
        foreach (var state in AllStates)
        {
            Assert.False(SimulationOperation.CanTransition(state, state), $"Expected {state} -> {state} to be illegal.");
        }
    }

    [Theory]
    [InlineData(SimulationOperationState.Completed)]
    [InlineData(SimulationOperationState.Faulted)]
    [InlineData(SimulationOperationState.Canceled)]
    public void TerminalStatesHaveNoOutgoingEdges(SimulationOperationState terminal)
    {
        Assert.True(SimulationOperation.IsTerminalState(terminal));
        foreach (var to in AllStates)
        {
            Assert.False(SimulationOperation.CanTransition(terminal, to), $"Terminal {terminal} must not transition to {to}.");
        }
    }

    [Theory]
    [InlineData(SimulationOperationState.Created)]
    [InlineData(SimulationOperationState.Runnable)]
    [InlineData(SimulationOperationState.Running)]
    [InlineData(SimulationOperationState.Paused)]
    public void NonTerminalStatesAreNotTerminal(SimulationOperationState state)
    {
        Assert.False(SimulationOperation.IsTerminalState(state));
    }
}
