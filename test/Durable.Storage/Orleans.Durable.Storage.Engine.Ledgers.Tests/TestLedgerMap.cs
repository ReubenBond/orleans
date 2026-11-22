using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// The ledger map's register in memory and a process's view of it: a test writes values as the committer would, and
/// counts the register's reads.
/// </summary>
internal sealed class TestLedgerMap
{
    private readonly ScriptedConditionalRegister _register;

    private TestLedgerMap(ScriptedConditionalRegister register, LedgerMapView view)
    {
        _register = register;
        View = view;
    }

    public LedgerMapView View { get; }

    /// <summary>Gets the number of reads the register has served.</summary>
    public int Reads => _register.Reads;

    /// <summary>Gets or sets a gate every read of the register waits at, or <see langword="null"/> for none.</summary>
    public TaskCompletionSource? ReadGate
    {
        get => _register.ReadGate;
        set => _register.ReadGate = value;
    }

    public static async Task<TestLedgerMap> OpenAsync(CancellationToken cancellationToken)
    {
        var register = new ScriptedConditionalRegister();
        return new TestLedgerMap(register, await LedgerMapView.OpenAsync(register, cancellationToken));
    }

    /// <summary>Writes <paramref name="value"/> and reads it into the view.</summary>
    public async Task PublishAsync(LedgerMapValue value, CancellationToken cancellationToken)
    {
        Write(value);
        _ = await View.RefreshAsync(value.Epoch, cancellationToken);
    }

    /// <summary>Writes <paramref name="value"/> without reading it into the view, which then misses it.</summary>
    public void Write(LedgerMapValue value) => _register.Put(LedgerMapSamples.Encoded(value));
}
