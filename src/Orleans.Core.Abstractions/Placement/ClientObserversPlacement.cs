// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Runtime
{
    internal class ClientObserversPlacement : PlacementStrategy
    {
        public static ClientObserversPlacement Instance { get; } = new();
    }
}
