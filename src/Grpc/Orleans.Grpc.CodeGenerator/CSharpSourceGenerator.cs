using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ProtoBuf.Reflection;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Orleans.Grpc.CodeGenerator;

[Generator(LanguageNames.CSharp)]
public sealed class CSharpSourceGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        AppDomain.CurrentDomain.AssemblyResolve += EmbeddedAssemblyResolver.AssemblyResolve;
        var sourceProtoFiles = context.AdditionalTextsProvider
            .Where(static (pair) => pair.Path.EndsWith(".proto"))
            .Combine(context.AnalyzerConfigOptionsProvider)
            .SelectMany(static (pair, ct) =>
            {
                var file = pair.Left;
                var config = pair.Right;
                var fileOptions = config.GetOptions(file);

                try
                {
                    var input = new GeneratorInput(file, fileOptions);
                    return ProtoProcessor.Generate(input, ct).ToList();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex);
                    throw;
                }
            });

        context.RegisterSourceOutput(
            sourceProtoFiles,
            static (context, output) =>
            {
                var errors = output.Errors.Errors;
                if (errors is { Length: > 0 })
                {
                    foreach (var error in errors)
                    {
                        Console.WriteLine("Error: " + error);
                        //context.ReportDiagnostic(Diagnostic.Create(error.ErrorNumber, error.Message, error.LineNumber, error.ColumnNumber, error.IsError));
                    }

                    return;
                }

                context.AddSource(output.HintName, output.SourceText!);
            });
    }
}

internal readonly record struct GeneratorOutput(
    string HintName,
    SourceText? SourceText,
    ErrorList Errors);

// This type exists for the sake of the source generator infrastructure being able to cache results.
internal readonly struct ErrorList(Error[] errors)
{
    public Error[] Errors { get; } = errors;

    public override int GetHashCode()
    {
        if (Errors == null)
        {
            return 0;
        }

        var hashCode = new HashCode();
        hashCode.Add(Errors.Length);
        foreach (var value in Errors)
        {
            hashCode.Add(value.ErrorNumber);
            hashCode.Add(value.LineNumber);
            hashCode.Add(value.ColumnNumber);
            hashCode.Add(value.IsError);
            hashCode.Add(value.Text);
            hashCode.Add(value.File);
            hashCode.Add(value.Message);
        }

        return hashCode.ToHashCode();
    }

    public override bool Equals(object obj) => obj is ErrorList other && Equals(other);

    public bool Equals(ErrorList other)
    {
        if (ReferenceEquals(Errors, other.Errors))
        {
            return true;
        }

        if (Errors == null || other.Errors == null)
        {
            return false;
        }

        if (Errors.Length != other.Errors.Length)
        {
            return false;
        }

        for (var i = 0; i < Errors.Length; i++)
        {
            var left = Errors[i];
            var right = other.Errors[i];
            if (!Equals(left, right))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Equals(Error left, Error right) =>
        left.ErrorNumber == right.ErrorNumber
        && left.LineNumber == right.LineNumber
        && left.ColumnNumber == right.ColumnNumber
        && left.IsError == right.IsError
        && string.Equals(left.Text, right.Text, StringComparison.Ordinal)
        && string.Equals(left.File, right.File, StringComparison.Ordinal)
        && string.Equals(left.Message, right.Message, StringComparison.Ordinal);
}

public record class GeneratorInput(AdditionalText File, AnalyzerConfigOptions? FileOptions);
