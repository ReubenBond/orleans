using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Orleans.Grpc.CodeGenerator.Internal;

internal static class AnalyzerConfigOptionsExtensions
{
    public static string[] GetMetadataValues(this AnalyzerConfigOptions? fileOptions, string metadataName)
    {
        if (fileOptions is null)
        {
            return [];
        }

        fileOptions.TryGetValue($"build_metadata.protobuf.{metadataName}", out var value);
        return value?.Split(';').Select(s => s.Trim()).ToArray() ?? [];
    }
}

