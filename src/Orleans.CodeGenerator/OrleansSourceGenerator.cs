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
                // Check if this is a design-time build or running in Visual Studio background services
                var isDesignTimeBuild = !Debugger.IsAttached &&
                    context.AnalyzerConfigOptions.GlobalOptions.TryGetValue("build_property.orleans_designtimebuild", out var designTimeBuildValue)
                    && string.Equals("true", designTimeBuildValue, StringComparison.OrdinalIgnoreCase);

                var processName = Process.GetCurrentProcess().ProcessName.ToLowerInvariant();
                var isVisualStudioProcess = processName.Contains("devenv") || processName.Contains("servicehub");

                // For performance: Skip expensive code generation in VS design-time builds
                // BUT: Always generate during actual compilation builds (BuildingProject=true)
                // This ensures incremental builds work while keeping IDE responsive
                if ((isVisualStudioProcess || isDesignTimeBuild) && !IsRealCompilationBuild(context))
                {
                    // Emit a marker to indicate generation was skipped for this design-time build
                    // Real compilation builds will always generate fresh code
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
        /// Determines if this is a real compilation build (not a design-time/IntelliSense build).
        /// Real builds include: dotnet build, msbuild, CI/CD builds, and explicit VS builds.
        /// </summary>
        private static bool IsRealCompilationBuild(GeneratorExecutionContext context)
        {
            // Check if BuildingProject is true - this indicates a real build operation
            if (context.AnalyzerConfigOptions.GlobalOptions.TryGetValue("build_property.buildingproject", out var buildingProject)
                && string.Equals("true", buildingProject, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Check if we're in a CI/CD environment
            if (context.AnalyzerConfigOptions.GlobalOptions.TryGetValue("build_property.continuousintegrationbuild", out var isCi)
                && string.Equals("true", isCi, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Check if design-time build is explicitly false
            if (context.AnalyzerConfigOptions.GlobalOptions.TryGetValue("build_property.orleans_designtimebuild", out var designTimeBuild)
                && string.Equals("false", designTimeBuild, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Default to assuming it's a real build if we can't determine otherwise
            // This ensures we generate code when in doubt
            return !context.AnalyzerConfigOptions.GlobalOptions.TryGetValue("build_property.orleans_designtimebuild", out _);
        }
    }
}
#pragma warning restore RS1035 // Do not use APIs banned for analyzers