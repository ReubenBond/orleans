// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace BenchmarkGrainInterfaces.Ping;

[GenerateSerializer]
public class UserProfile
{
    [Id(0)]
    public string DisplayName { get; set; }

    [Id(1)]
    public string PreferredLanguage { get; set; }

    [Id(2)]
    public DateTimeOffset AccountCreated { get; set; }
}









