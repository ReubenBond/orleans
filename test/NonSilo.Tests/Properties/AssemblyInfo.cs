// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Xunit;

// Disable XUnit concurrency limit
[assembly: CollectionBehavior(MaxParallelThreads = -1)]

[assembly: InternalsVisibleTo("TesterInternal")]
