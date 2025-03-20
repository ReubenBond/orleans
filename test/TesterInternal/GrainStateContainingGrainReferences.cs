// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace TesterInternal;

[Serializable]
[Orleans.GenerateSerializer]
public class GrainStateContainingGrainReferences
{
    [Orleans.Id(0)]
    public IAddressable Grain { get; set; }
    [Orleans.Id(1)]
    public List<IAddressable> GrainList { get; set; }
    [Orleans.Id(2)]
    public Dictionary<string, IAddressable> GrainDict { get; set; }

    public GrainStateContainingGrainReferences()
    {
        GrainList = new List<IAddressable>();
        GrainDict = new Dictionary<string, IAddressable>();
    }
}
