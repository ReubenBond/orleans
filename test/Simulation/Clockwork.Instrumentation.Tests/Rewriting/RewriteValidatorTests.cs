using Orleans.Testing.Simulation.Instrumentation.Diagnostics;
using Orleans.Testing.Simulation.Instrumentation.Rewriting;
using Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Rewriting;

/// <summary>
/// Checks the validator's read-back binding check: every member reference whose signature mentions a substituted or
/// substitute type, and whose declaring assembly does not change in the closure, must resolve.
/// </summary>
public sealed class RewriteValidatorTests
{
    private const string Fixture = """
        using ClockworkFixtures.Api;
        using ClockworkFixtures.Shims;

        namespace Fx
        {
            public static class Caller
            {
                public static string Describe(LegacyMarker marker) => MarkerShim.Describe(marker);

                public static string Create() => new ModernMarker().ToString();
            }
        }
        """;

    private static readonly RewriteValidator.BindingScope MarkerScope = new(
        new HashSet<string>(StringComparer.Ordinal) { "ClockworkFixtures.Api.LegacyMarker", "ClockworkFixtures.Shims.ModernMarker" },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void ReferencesThatBindPassValidation()
    {
        using var context = RewriteTestContext.Create();
        string path = context.CompileFixture("Fx.Validated", Fixture);

        Assert.Empty(RewriteValidator.Validate(path, [context.Directory], MarkerScope));
    }

    [Fact]
    public void ReferenceWhoseSubstitutedSignatureIsNotDeclaredFailsValidation()
    {
        using var context = RewriteTestContext.Create();
        string path = BreakDescribe(context, context.Directory);

        RewriteDiagnostic error = Assert.Single(RewriteValidator.Validate(path, [context.Directory], MarkerScope));

        Assert.Equal(RewriteDiagnosticIds.ValidationFailed, error.Id);
        Assert.Equal(
            "Member reference 'System.String ClockworkFixtures.Shims.MarkerShim::Describe(ClockworkFixtures.Shims.ModernMarker)' " +
            "does not bind after rewriting: 'ClockworkFixtures.Shims.MarkerShim' declares no member with that signature.",
            error.Message);
    }

    [Fact]
    public void ReferenceIntoAnUnresolvableAssemblyIsReportedAsSuch()
    {
        using var context = RewriteTestContext.Create();
        string isolated = Path.Combine(context.Directory, "isolated");
        string path = BreakDescribe(context, isolated);

        IReadOnlyList<RewriteDiagnostic> errors = RewriteValidator.Validate(path, [], MarkerScope);

        Assert.NotEmpty(errors);
        Assert.All(errors, error =>
        {
            Assert.Equal(RewriteDiagnosticIds.ValidationFailed, error.Id);
            Assert.Contains("could not be resolved to check that it binds", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("does not bind after rewriting", error.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ReferenceIntoAnotherRewrittenAssemblyIsLeftToTheClosureCheck()
    {
        using var context = RewriteTestContext.Create();
        string path = BreakDescribe(context, context.Directory);
        var scope = MarkerScope with
        {
            RewrittenAssemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ClockworkFixtures.Shims" },
        };

        Assert.Empty(RewriteValidator.Validate(path, [context.Directory], scope));
    }

    // Retargets the MarkerShim.Describe call onto a signature taking the substitute, which the shim does not declare.
    private static string BreakDescribe(RewriteTestContext context, string outputDirectory)
    {
        string input = context.CompileFixture("Fx.Validated", Fixture);
        Directory.CreateDirectory(outputDirectory);
        string output = Path.Combine(outputDirectory, "Fx.Validated.broken.dll");
        using (ModuleDefinition shims = ModuleDefinition.ReadModule(context.ShimPath))
        using (ModuleDefinition module = context.LoadModule(input))
        {
            Instruction call = module.GetType("Fx.Caller").Methods.Single(method => method.Name == "Describe").Body.Instructions
                .Single(instruction => instruction.Operand is MethodReference { Name: "Describe" });
            var original = (MethodReference)call.Operand;
            TypeReference modern = module.ImportReference(shims.GetType("ClockworkFixtures.Shims.ModernMarker"));
            var broken = new MethodReference(original.Name, original.ReturnType, original.DeclaringType);
            broken.Parameters.Add(new ParameterDefinition(modern));
            call.Operand = broken;
            module.Write(output);
        }

        return output;
    }
}
