// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

namespace Orleans.Runtime.Hosting
{
    internal class NamedService<TService>(string name)
    {
        public string Name { get; } = name;
    }
}
