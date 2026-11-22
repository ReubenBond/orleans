using Microsoft.Extensions.Options;
using Orleans.Durable.Storage.Engine.Cluster.Control;

namespace Orleans.Durable.Storage.Hosting;

/// <summary>
/// The silo's durable storage options (orleans.md §6), one root which the silo's wiring takes: the node's own, of its LedgerStore,
/// registration, replica calls, and archive, and the control plane's, of the controllers' policy and intervals. It lives in
/// the hosting namespace, which composes both planes.
/// </summary>
public sealed class DurableStorageSiloOptions
{
    /// <summary>Gets or sets the node's options.</summary>
    public DurableStorageOptions Node { get; set; } = new();

    /// <summary>Gets or sets the control plane's options.</summary>
    public ControlOptions Control { get; set; } = new();

    /// <summary>Throws unless both sections can start a node.</summary>
    /// <exception cref="ArgumentException">A section is missing, or an option is missing or out of range.</exception>
    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Node, nameof(Node));
        ArgumentNullException.ThrowIfNull(Control, nameof(Control));
        Node.Validate();
        Control.Validate();
        if (Control.LedgerPlacement == LedgerPlacement.ObjectStore && Node.ObjectLedgers is null)
        {
            throw new ArgumentException(
                "Control:LedgerPlacement is ObjectStore, which requires Node:ObjectLedgers to be configured (orleans.md §5.14).",
                $"{nameof(Control)}:{nameof(ControlOptions.LedgerPlacement)}");
        }
    }
}

/// <summary>
/// Validates the silo's options as Microsoft.Extensions.Options builds them, so a silo whose options cannot start a node
/// fails its start naming the option.
/// </summary>
internal sealed class DurableStorageSiloOptionsValidator : IValidateOptions<DurableStorageSiloOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, DurableStorageSiloOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            options.Validate();
            return ValidateOptionsResult.Success;
        }
        catch (ArgumentException invalid)
        {
            return ValidateOptionsResult.Fail(invalid.Message);
        }
    }
}
