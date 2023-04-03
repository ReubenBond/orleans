#nullable enable

using System;
using System.Collections.Generic;

namespace Orleans.Connections.Transport;

/// <summary>
/// Property bag describing a messaging endpoint.
/// </summary>
[GenerateSerializer]
public sealed class EndpointInfo : Dictionary<string, string>
{
    public EndpointInfo() : base(StringComparer.Ordinal) { }

    /// <summary>
    /// Gets or sets the name of the endpoint.
    /// </summary>
    [Newtonsoft.Json.JsonIgnore, System.Text.Json.Serialization.JsonIgnore]
    public required string Name { get => this["name"]; init => this["name"] = value; }
}
