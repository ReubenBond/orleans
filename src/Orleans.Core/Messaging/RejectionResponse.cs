// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Runtime
{
    [Id(102), GenerateSerializer, Immutable]
    internal sealed class RejectionResponse
    {
        [Id(0)]
        public string RejectionInfo { get; init; }

        [Id(1)]
        public Message.RejectionTypes RejectionType { get; init; }

        [Id(2)]
        public Exception Exception { get; init; }
    }
}
