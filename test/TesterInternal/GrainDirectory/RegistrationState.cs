#nullable enable
using BraggerSpecs;

namespace UnitTests.GrainDirectory.StateDefinitions;

[StateDefinition]
public partial class RegistrationState
{
    public string? ActivationId { get; set; }
    public string? GrainId { get; set; }
    public string? SiloAddress { get; set; }
}

[StateDefinition]
public partial class ClientDirectoryState
{
    public Dictionary<string, RegistrationState>? Directory { get; set; }
}

[StateDefinition]
public partial class SystemState
{
    // Maps from client id to observed directory state
    public Dictionary<string, ClientDirectoryState>? Clients { get; set; } = new();
}
