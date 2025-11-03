using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Orleans.CodeGenerator.Diagnostics;
using Orleans.CodeGenerator.Model;

#pragma warning disable RS1035 // Do not use APIs banned for analyzers
namespace Orleans.CodeGenerator
{
    [Generator]
    public class OrleansSerializationSourceGenerator : ISourceGenerator
    {
        public void Execute(GeneratorExecutionContext context)
        {
            try
            {
                // For performance: Skip expensive code generation during design-time builds.
                // Per https://github.com/dotnet/project-system/blob/main/docs/design-time-builds.md:
                // - SDK-style projects set DesignTimeBuild=true during design-time builds
                // - Legacy projects use BuildingProject (it's != 'true' during design-time builds)
                // - DesignTimeBuild is typically empty ('') in normal builds, so we check == 'true'
                if (IsDesignTimeBuild(context))
                {
                    // Skip code generation during design-time builds to keep IDE responsive.
                    // Normal builds (dotnet build, msbuild, CI/CD) will generate code.
                    return;
                }

                if (context.AnalyzerConfigOptions.GlobalOptions.TryGetValue("build_property.orleans_attachdebugger", out var attachDebuggerOption)
                    && string.Equals("true", attachDebuggerOption, StringComparison.OrdinalIgnoreCase))
                {
                    Debugger.Launch();
                }

                var options = new CodeGeneratorOptions();
                if (context.AnalyzerConfigOptions.GlobalOptions.TryGetValue("build_property.orleans_immutableattributes", out var immutableAttributes) && immutableAttributes is { Length: > 0 })
                {
                    options.ImmutableAttributes.AddRange(immutableAttributes.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList());
                }

                if (context.AnalyzerConfigOptions.GlobalOptions.TryGetValue("build_property.orleans_aliasattributes", out var aliasAttributes) && aliasAttributes is { Length: > 0 })
                {
                    options.AliasAttributes.AddRange(aliasAttributes.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList());
                }

                if (context.AnalyzerConfigOptions.GlobalOptions.TryGetValue("build_property.orleans_idattributes", out var idAttributes) && idAttributes is { Length: > 0 })
                {
                    options.IdAttributes.AddRange(idAttributes.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList());
                }

                if (context.AnalyzerConfigOptions.GlobalOptions.TryGetValue("build_property.orleans_generateserializerattributes", out var generateSerializerAttributes) && generateSerializerAttributes is { Length: > 0 })
                {
                    options.GenerateSerializerAttributes.AddRange(generateSerializerAttributes.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList());
                }

                if (context.AnalyzerConfigOptions.GlobalOptions.TryGetValue("build_property.orleans_generatefieldids", out var generateFieldIds) && generateFieldIds is { Length: > 0 })
                {
                    if (Enum.TryParse(generateFieldIds, out GenerateFieldIds fieldIdOption))
                    {
                        options.GenerateFieldIds = fieldIdOption;
                    }
                }

                if (context.AnalyzerConfigOptions.GlobalOptions.TryGetValue("build_property.orleansgeneratecompatibilityinvokers", out var generateCompatInvokersValue)
                    && bool.TryParse(generateCompatInvokersValue, out var genCompatInvokers))
                {
                    options.GenerateCompatibilityInvokers = genCompatInvokers;
                }

                var codeGenerator = new CodeGenerator(context.Compilation, options);
                var syntax = codeGenerator.GenerateCode(context.CancellationToken);
                var sourceString = syntax.NormalizeWhitespace().ToFullString();
                var sourceText = SourceText.From(sourceString, Encoding.UTF8);
                context.AddSource($"{context.Compilation.AssemblyName ?? "assembly"}.orleans.g.cs", sourceText);
            }
            catch (Exception exception)
            {
                if (!HandleException(context, exception))
                {
                    throw;
                }
            }

            static bool HandleException(GeneratorExecutionContext context, Exception exception)
            {
                if (exception is OrleansGeneratorDiagnosticAnalysisException analysisException)
                {
                    context.ReportDiagnostic(analysisException.Diagnostic);
                    return true;
                }

                context.ReportDiagnostic(UnhandledCodeGenerationExceptionDiagnostic.CreateDiagnostic(exception));
                Console.WriteLine(exception);
                Console.WriteLine(exception.StackTrace);
                return false;
            }
        }

        public void Initialize(GeneratorInitializationContext context)
        {
        }

        /// <summary>
        /// Determines whether the current build is a design-time build.
        /// Per https://github.com/dotnet/project-system/blob/main/docs/design-time-builds.md:
        /// - SDK-style projects (CPS-based) set DesignTimeBuild=true during design-time builds
        /// - Legacy .NET Framework projects use BuildingProject (it's != 'true' during design-time builds)
        /// - DesignTimeBuild is typically empty ('') in normal builds
        /// </summary>
        private static bool IsDesignTimeBuild(GeneratorExecutionContext context)
        {
            // Check for SDK-style project design-time build (DesignTimeBuild == 'true')
            if (context.AnalyzerConfigOptions.GlobalOptions.TryGetValue("build_property.orleans_designtimebuild", out var designTimeBuild)
                && string.Equals("true", designTimeBuild, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Check for legacy project design-time build (BuildingProject != 'true')
            // In normal builds, BuildingProject is 'true'. During design-time builds, it's not set or is empty.
            if (context.AnalyzerConfigOptions.GlobalOptions.TryGetValue("build_property.buildingproject", out var buildingProject)
                && !string.Equals("true", buildingProject, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // If neither property indicates a design-time build, assume it's a normal build
            return false;
        }
    }
}
#pragma warning restore RS1035 // Do not use APIs banned for analyzers
