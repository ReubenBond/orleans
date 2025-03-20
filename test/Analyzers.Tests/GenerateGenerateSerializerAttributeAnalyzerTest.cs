// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.CodeAnalysis;
using Orleans.Analyzers;
using Xunit;

namespace Analyzers.Tests;

[TestCategory("BVT"), TestCategory("Analyzer")]
public class GenerateGenerateSerializerAttributeAnalyzerTest : DiagnosticAnalyzerTestBase<GenerateGenerateSerializerAttributeAnalyzer>
{
    private async Task VerifyGeneratedDiagnostic(string code)
    {
        var (diagnostics, _) = await GetDiagnosticsAsync(code, new string[0]);

        Assert.NotEmpty(diagnostics);
        Assert.Single(diagnostics);

        var diagnostic = diagnostics.First();
        Assert.Equal(GenerateGenerateSerializerAttributeAnalyzer.RuleId, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
    }

    [Fact]
    public Task SerializableClass()
        => VerifyGeneratedDiagnostic(@"[System.Serializable] public class D { }");

    [Fact]
    public Task SerializableStruct()
        => VerifyGeneratedDiagnostic(@"[System.Serializable] public struct D { }");

    [Fact]
    public Task SerializableRecord()
        => VerifyGeneratedDiagnostic(@"[System.Serializable] public record D { }");

    [Fact]
    public Task SerializableRecordStruct()
        => VerifyGeneratedDiagnostic(@"[System.Serializable] public record struct D { }");
}
