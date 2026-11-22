using System.ComponentModel;
using System.Security.Cryptography;
using Orleans.Testing.Simulation.Runtime.Execution;

namespace Orleans.Testing.Simulation.Shims.System.Security.Cryptography;

/// <summary>
/// A <see cref="RandomNumberGenerator"/> that produces deterministic, non-cryptographic bytes from a
/// simulation's isolated randomness stream. Controlled <c>RandomNumberGenerator.Create()</c> calls
/// return this type so instance APIs remain reproducible.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class SimulationRandomNumberGenerator : RandomNumberGenerator
{
    private readonly ISimulationRuntimeEnvironment _environment;
    private readonly SimulationNodeIdentity? _node;

    /// <summary>Initializes a new instance of the <see cref="SimulationRandomNumberGenerator"/> class.</summary>
    /// <param name="environment">The environment supplying deterministic non-cryptographic bytes.</param>
    /// <param name="node">The active node identity, or <see langword="null"/> for cluster-level execution.</param>
    public SimulationRandomNumberGenerator(ISimulationRuntimeEnvironment environment, SimulationNodeIdentity? node)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _environment = environment;
        _node = node;
    }

    /// <inheritdoc/>
    public override void GetBytes(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        _environment.FillCryptoRandomBytes(_node, data);
    }

    /// <inheritdoc/>
    public override void GetBytes(Span<byte> data) => _environment.FillCryptoRandomBytes(_node, data);

    /// <inheritdoc/>
    public override void GetNonZeroBytes(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        GetNonZeroBytes(data.AsSpan());
    }

    /// <inheritdoc/>
    public override void GetNonZeroBytes(Span<byte> data)
    {
        for (var i = 0; i < data.Length;)
        {
            _environment.FillCryptoRandomBytes(_node, data.Slice(i, 1));
            if (data[i] != 0)
            {
                i++;
            }
        }
    }
}
