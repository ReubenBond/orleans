// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator
{
    internal interface ICopierDescription
    {
        ITypeSymbol UnderlyingType { get; }
    }
}