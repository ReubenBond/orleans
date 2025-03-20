// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator;

public class OrleansGeneratorDiagnosticAnalysisException : Exception
{
    public OrleansGeneratorDiagnosticAnalysisException(Diagnostic diagnostic) : base(diagnostic.GetMessage())
    {
        Diagnostic = diagnostic;
    }

    public Diagnostic Diagnostic { get; }
}
