using System.Reflection;
using System.Runtime.Loader;
using Orleans.Testing.Simulation.Instrumentation.Configuration;
using Orleans.Testing.Simulation.Instrumentation.Diagnostics;
using Orleans.Testing.Simulation.Instrumentation.Orchestration;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Orchestration;

/// <summary>
/// Closure-level checks that a member reference, interface implementation, or override whose signature a type
/// substitution rewrites binds across the closure's assemblies. <c>Fx.Lib</c> declares <c>Sink.Take(LegacyMarker)</c>,
/// <c>IHandler</c>, and <c>HandlerBase</c>, which <c>Fx.App</c> calls, implements, and overrides; the rule substitutes
/// <c>LegacyMarker</c> with <c>ModernMarker</c>. Each binds only when both sides are rewritten, so the runner rewrites
/// a sibling consistently and fails the run with <c>CWR0001</c> when only one side is - an excluded sibling, an
/// excluded consumer, or a type marked <c>[DoNotRewrite]</c> - instead of staging a closure that throws
/// <see cref="MissingMethodException"/> or <see cref="TypeLoadException"/>. The reshape cases give <c>Fx.Lib</c> a type
/// deriving from <c>List&lt;LegacyMarker&gt;</c> and a method constrained to <c>IEnumerable&lt;LegacyMarker&gt;</c>,
/// whose base type and constraint the rewrite substitutes, so code that keeps the original ones fails the run.
/// </summary>
public sealed class ClosureBindingTests : IDisposable
{
    private const string LibSource = """
        using ClockworkFixtures.Api;

        namespace Fx.Lib
        {
            public static class Sink
            {
                public static string Take(LegacyMarker marker) => "took " + marker;
            }

            public interface IHandler
            {
                string Handle(LegacyMarker marker);
            }

            public abstract class HandlerBase
            {
                public abstract string Handle(LegacyMarker marker);
            }
        }
        """;

    private const string ImplementerAppSource = """
        using ClockworkFixtures.Api;
        using Fx.Lib;

        namespace Fx.App
        {
            public sealed class Handler : IHandler
            {
                public string Handle(LegacyMarker marker) => "handled " + marker;
            }

            public sealed class DerivedHandler : HandlerBase
            {
                public override string Handle(LegacyMarker marker) => "overrode " + marker;
            }

            public sealed class ExplicitHandler : IHandler
            {
                string IHandler.Handle(LegacyMarker marker) => "explicitly handled " + marker;
            }

            public static class Caller
            {
                public static string Run()
                {
                    var marker = new LegacyMarker();
                    IHandler handler = new Handler();
                    IHandler explicitHandler = new ExplicitHandler();
                    HandlerBase derived = new DerivedHandler();
                    return handler.Handle(marker) + ", " + explicitHandler.Handle(marker) + ", " + derived.Handle(marker);
                }
            }
        }
        """;

    // Without a call into Fx.Lib, nothing but the types' shapes crosses the boundary.
    private const string ShapeOnlyAppTemplate = """
        using ClockworkFixtures.Api;
        using Fx.Lib;

        namespace Orleans.Testing.Simulation.Instrumentation.Attributes
        {
            [System.AttributeUsage(System.AttributeTargets.Class)]
            public sealed class DoNotRewriteAttribute(string reason) : System.Attribute
            {
                public string Reason { get; } = reason;
            }
        }

        namespace Fx.App
        {
            [ATTRIBUTE]
            public sealed class Handler : IHandler
            {
                public string Handle(LegacyMarker marker) => "handled " + marker;
            }

            [ATTRIBUTE]
            public sealed class DerivedHandler : HandlerBase
            {
                public override string Handle(LegacyMarker marker) => "overrode " + marker;
            }
        }
        """;

    private const string AppSource = """
        using ClockworkFixtures.Api;
        using Fx.Lib;

        namespace Fx.App
        {
            public static class Caller
            {
                public static string Run() => Sink.Take(new LegacyMarker());
            }
        }
        """;

    private const string DoNotRewriteAppSource = """
        using ClockworkFixtures.Api;
        using Fx.Lib;

        namespace Orleans.Testing.Simulation.Instrumentation.Attributes
        {
            [System.AttributeUsage(System.AttributeTargets.Class)]
            public sealed class DoNotRewriteAttribute(string reason) : System.Attribute
            {
                public string Reason { get; } = reason;
            }
        }

        namespace Fx.App
        {
            [Orleans.Testing.Simulation.Instrumentation.Attributes.DoNotRewrite("Runs before the simulation starts.")]
            public static class LegacyCaller
            {
                public static string Run() => Sink.Take(new LegacyMarker());
            }
        }
        """;

    // Fx.Lib's Markers and Shelf.Stock<T> have a base type and a constraint composed from the substituted type.
    private const string ReshapeLibSource = """
            using System.Collections.Generic;
            using ClockworkFixtures.Api;

            namespace Fx.Lib
            {
                public class Markers : List<LegacyMarker>
                {
                }

                public static class Shelf
                {
                    public static int Stock<T>(T items)
                        where T : IEnumerable<LegacyMarker> => items is ICollection<LegacyMarker> collection ? collection.Count : 0;
                }
            }
            """;

    private const string ReshapeAppTemplate = """
            using ClockworkFixtures.Api;
            using Fx.Lib;

            namespace Orleans.Testing.Simulation.Instrumentation.Attributes
            {
                [System.AttributeUsage(System.AttributeTargets.Class)]
                public sealed class DoNotRewriteAttribute(string reason) : System.Attribute
                {
                    public string Reason { get; } = reason;
                }
            }

            namespace Fx.App
            {
                public sealed class MoreMarkers : Markers
                {
                }

                [ATTRIBUTE]
                public static class Counter
                {
                    public static string Count()
                    {
                        var markers = new MoreMarkers();
                        markers.Add(new LegacyMarker());
                        return markers.Count + " " + markers[0] + " " + Shelf.Stock(markers);
                    }
                }
            }
            """;

    private const string DoNotRewriteAttributeSource = """
        namespace Orleans.Testing.Simulation.Instrumentation.Attributes
        {
            [System.AttributeUsage(System.AttributeTargets.Class)]
            public sealed class DoNotRewriteAttribute(string reason) : System.Attribute
            {
                public string Reason { get; } = reason;
            }
        }
        """;

    // Fx.Lib's Markers derives from List<LegacyMarker>; [LIB] marks it [DoNotRewrite] in the kept-shape cases.
    private const string KeptShapeLibTemplate = $$"""
        using System.Collections.Generic;
        using ClockworkFixtures.Api;

        {{DoNotRewriteAttributeSource}}

        namespace Fx.Lib
        {
            [LIB]
            public class Markers : List<LegacyMarker>
            {
            }
        }
        """;

    // Fx.App's rewritten Counter uses MoreMarkers, which [APP] marks [DoNotRewrite], as a List<LegacyMarker>.
    private const string KeptShapeAppTemplate = $$"""
        using System.Collections.Generic;
        using ClockworkFixtures.Api;
        using Fx.Lib;

        {{DoNotRewriteAttributeSource}}

        namespace Fx.App
        {
            [APP]
            public class MoreMarkers : Markers
            {
            }

            public static class Counter
            {
                public static string Count()
                {
                    List<LegacyMarker> markers = new MoreMarkers();
                    markers.Add(new LegacyMarker());
                    return markers.Count + " " + markers[0];
                }
            }
        }
        """;

    private readonly string _root = TestArtifacts.CreateUnique("cwr-closure-binding");
    private readonly string _source;
    private readonly string _staging;

    public ClosureBindingTests()
    {
        _source = Path.Combine(_root, "source");
        _staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(_source);
    }

    [Fact]
    public void RewrittenSiblingBindsTheSubstitutedSignature()
    {
        BuildClosure(AppSource);

        Run().EnsureSuccess();

        var loadContext = new DirectoryLoadContext(_staging);
        try
        {
            Assembly app = loadContext.LoadFromAssemblyPath(Path.Combine(_staging, "Fx.App.dll"));
            MethodInfo run = app.GetType("Fx.App.Caller", throwOnError: true)!.GetMethod("Run")!;

            // Both assemblies take the substitute, so the call binds and receives it.
            Assert.Equal("took modern", run.Invoke(null, null));
        }
        finally
        {
            loadContext.Unload();
        }
    }

    [Fact]
    public void ExcludedSiblingFailsTheConsumersRewrite()
    {
        BuildClosure(AppSource);

        InstrumentationResult result = Run(new InstrumentationConfiguration { ExcludePatterns = ["Fx.Lib.dll"] });

        // The consumer's reference would take the substitute while the copied sibling keeps the original signature.
        Assert.False(result.Succeeded);
        RewriteDiagnostic error = Assert.Single(result.Errors);
        Assert.Equal(RewriteDiagnosticIds.UnresolvedReplacement, error.Id);
        Assert.Equal("Fx.App.Caller.Run", error.Method);
        Assert.Contains("'substitute-marker'", error.Message, StringComparison.Ordinal);
        Assert.Contains("Fx.Lib.Sink::Take(ClockworkFixtures.Api.LegacyMarker)", error.Message, StringComparison.Ordinal);
        Assert.Contains("'Fx.Lib', which is not rewritten in this closure", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExcludedConsumerOfARewrittenSiblingFailsTheRun()
    {
        BuildClosure(AppSource);

        InstrumentationResult result = Run(new InstrumentationConfiguration { ExcludePatterns = ["Fx.App.dll"] });

        // The sibling's signature takes the substitute while the copied consumer keeps the original reference.
        Assert.False(result.Succeeded);
        RewriteDiagnostic error = Assert.Single(result.Errors);
        Assert.Equal(RewriteDiagnosticIds.UnresolvedReplacement, error.Id);
        Assert.Equal("Fx.App.dll", error.Method);
        Assert.Contains("'substitute-marker' rewrites 'Fx.Lib'", error.Message, StringComparison.Ordinal);
        Assert.Contains("Fx.Lib.Sink::Take(ClockworkFixtures.Api.LegacyMarker)", error.Message, StringComparison.Ordinal);
        Assert.Contains("'Fx.App' is not rewritten in this closure", error.Message, StringComparison.Ordinal);
        Assert.Contains(result.Assemblies, assembly => assembly.RelativePath == "Fx.Lib.dll" && assembly.WasRewritten);
    }

    [Fact]
    public void DoNotRewriteCallerOfARewrittenSiblingFailsTheRun()
    {
        BuildClosure(DoNotRewriteAppSource);

        InstrumentationResult result = Run();

        // Both assemblies are rewritten, but the excluded caller keeps the original reference.
        Assert.False(result.Succeeded);
        RewriteDiagnostic error = Assert.Single(result.Errors);
        Assert.Equal(RewriteDiagnosticIds.UnresolvedReplacement, error.Id);
        Assert.Equal("Fx.App.dll", error.Method);
        Assert.Contains("Fx.Lib.Sink::Take(ClockworkFixtures.Api.LegacyMarker)", error.Message, StringComparison.Ordinal);
        Assert.Contains(
            "the referencing type 'Fx.App.LegacyCaller' is excluded from rewriting and keeps the original signature",
            error.Message,
            StringComparison.Ordinal);
        Assert.Contains(result.Assemblies, assembly => assembly.RelativePath == "Fx.App.dll" && assembly.WasRewritten);
    }

    [Fact]
    public void RewrittenSiblingsImplementAndOverrideEachOthersMembers()
    {
        BuildClosure(ImplementerAppSource);

        Run().EnsureSuccess();

        var loadContext = new DirectoryLoadContext(_staging);
        try
        {
            Assembly app = loadContext.LoadFromAssemblyPath(Path.Combine(_staging, "Fx.App.dll"));
            MethodInfo run = app.GetType("Fx.App.Caller", throwOnError: true)!.GetMethod("Run")!;

            // The explicit implementation's declaration is retargeted onto the sibling's rewritten signature.
            Assert.Equal("handled modern, explicitly handled modern, overrode modern", run.Invoke(null, null));
        }
        finally
        {
            loadContext.Unload();
        }
    }

    [Fact]
    public void CopiedImplementerOfARewrittenSiblingFailsTheRun()
    {
        BuildClosure(ShapeOnlyAppSource(doNotRewrite: false));

        InstrumentationResult result = Run(new InstrumentationConfiguration { ExcludePatterns = ["Fx.App.dll"] });

        // The sibling's members take the substitute while the copied implementations keep the original signatures.
        AssertShapeErrors(
            result,
            "Fx.App.dll",
            ("Fx.App.Handler::Handle", "implements 'Fx.Lib.IHandler::Handle(ClockworkFixtures.Shims.ModernMarker) : System.String' as " +
                "'Handle(ClockworkFixtures.Api.LegacyMarker) : System.String' after rewriting, because 'Fx.App.Handler' is declared " +
                "in 'Fx.App', which is not rewritten in this closure, so it keeps the original signature while 'Fx.Lib.IHandler' is rewritten"),
            ("Fx.App.DerivedHandler::Handle", "overrides 'Fx.Lib.HandlerBase::Handle(ClockworkFixtures.Shims.ModernMarker) : System.String'"));
        Assert.Contains(result.Assemblies, assembly => assembly.RelativePath == "Fx.Lib.dll" && assembly.WasRewritten);
    }

    [Fact]
    public void RewrittenImplementerOfACopiedSiblingFailsTheRun()
    {
        BuildClosure(ShapeOnlyAppSource(doNotRewrite: false));

        InstrumentationResult result = Run(new InstrumentationConfiguration { ExcludePatterns = ["Fx.Lib.dll"] });

        // The implementations take the substitute while the copied sibling's members keep the original signatures.
        AssertShapeErrors(
            result,
            method: null,
            ("Fx.App.Handler::Handle", "implements 'Fx.Lib.IHandler::Handle(ClockworkFixtures.Api.LegacyMarker) : System.String' as " +
                "'Handle(ClockworkFixtures.Shims.ModernMarker) : System.String' after rewriting, because 'Fx.Lib.IHandler' is " +
                "declared in 'Fx.Lib', which is not rewritten in this closure, so it keeps the original signature while " +
                "'Fx.App.Handler' is rewritten"),
            ("Fx.App.DerivedHandler::Handle", "overrides 'Fx.Lib.HandlerBase::Handle(ClockworkFixtures.Api.LegacyMarker) : System.String'"));
    }

    [Fact]
    public void DoNotRewriteImplementerOfARewrittenSiblingFailsTheRun()
    {
        BuildClosure(ShapeOnlyAppSource(doNotRewrite: true));

        InstrumentationResult result = Run();

        // Both assemblies are rewritten, but the excluded implementations keep the original signatures.
        AssertShapeErrors(
            result,
            "Fx.App.dll",
            ("Fx.App.Handler::Handle", "because 'Fx.App.Handler' is excluded from rewriting in 'Fx.App', so it keeps the original " +
                "signature while 'Fx.Lib.IHandler' is rewritten"),
            ("Fx.App.DerivedHandler::Handle", "because 'Fx.App.DerivedHandler' is excluded from rewriting in 'Fx.App', so it keeps " +
                "the original signature while 'Fx.Lib.HandlerBase' is rewritten"));
        Assert.Contains(result.Assemblies, assembly => assembly.RelativePath == "Fx.App.dll" && assembly.WasRewritten);
    }

    [Fact]
    public void RewrittenConsumerOfAReshapedSiblingRuns()
    {
        BuildClosure(ReshapeAppSource(doNotRewrite: false), ReshapeLibSource);

        Run().EnsureSuccess();

        var loadContext = new DirectoryLoadContext(_staging);
        try
        {
            Assembly app = loadContext.LoadFromAssemblyPath(Path.Combine(_staging, "Fx.App.dll"));
            MethodInfo count = app.GetType("Fx.App.Counter", throwOnError: true)!.GetMethod("Count")!;

            // Both assemblies name List<ModernMarker>, so the derived type and the constraint agree.
            Assert.Equal("1 modern 1", count.Invoke(null, null));
        }
        finally
        {
            loadContext.Unload();
        }
    }

    [Fact]
    public void CopiedConsumerOfAReshapedSiblingFailsTheRun()
    {
        BuildClosure(ReshapeAppSource(doNotRewrite: false), ReshapeLibSource);

        InstrumentationResult result = Run(new InstrumentationConfiguration { ExcludePatterns = ["Fx.App.dll"] });

        // The copied consumer would call List<LegacyMarker>.Add on a List<ModernMarker>, and instantiate Stock<T> with a
        // type that satisfies only the original constraint.
        AssertReshapeErrors(
            result,
            "Fx.App.dll",
            "rewrites the base type of 'Fx.Lib.Markers' to 'System.Collections.Generic.List`1<ClockworkFixtures.Shims.ModernMarker>', " +
                "but 'Fx.App' is not rewritten in this closure and still uses 'Fx.Lib.Markers' as declared",
            "rewrites the constraint on 'T' of 'System.Int32 Fx.Lib.Shelf::Stock(T)' to " +
                "'System.Collections.Generic.IEnumerable`1<ClockworkFixtures.Shims.ModernMarker>', but 'Fx.App' is not " +
                "rewritten in this closure and still uses 'System.Int32 Fx.Lib.Shelf::Stock(T)' as declared");
        Assert.Contains(result.Assemblies, assembly => assembly.RelativePath == "Fx.Lib.dll" && assembly.WasRewritten);
    }

    [Fact]
    public void DoNotRewriteConsumerOfAReshapedSiblingFailsTheRun()
    {
        BuildClosure(ReshapeAppSource(doNotRewrite: true), ReshapeLibSource);

        InstrumentationResult result = Run();

        // The excluded consumer uses a rewritten type of its own assembly that inherits the sibling's rewritten base type.
        AssertReshapeErrors(
            result,
            "Fx.App.dll",
            "rewrites the base type of 'Fx.Lib.Markers' (inherited by 'Fx.App.MoreMarkers') to " +
                "'System.Collections.Generic.List`1<ClockworkFixtures.Shims.ModernMarker>', but 'Fx.App.Counter' is excluded " +
                "from rewriting in 'Fx.App' and still uses 'Fx.App.MoreMarkers' as declared",
            "rewrites the constraint on 'T' of 'System.Int32 Fx.Lib.Shelf::Stock(T)' to " +
                "'System.Collections.Generic.IEnumerable`1<ClockworkFixtures.Shims.ModernMarker>', but 'Fx.App.Counter' is " +
                "excluded from rewriting in 'Fx.App' and still uses 'System.Int32 Fx.Lib.Shelf::Stock(T)' as declared");
        Assert.Contains(result.Assemblies, assembly => assembly.RelativePath == "Fx.App.dll" && assembly.WasRewritten);
    }

    [Fact]
    public void RewrittenConsumerOfARewrittenSiblingsListRuns()
    {
        BuildClosure(KeptShapeApp(doNotRewrite: false), KeptShapeLib(doNotRewrite: false));

        Run().EnsureSuccess();

        var loadContext = new DirectoryLoadContext(_staging);
        try
        {
            Assembly app = loadContext.LoadFromAssemblyPath(Path.Combine(_staging, "Fx.App.dll"));
            MethodInfo count = app.GetType("Fx.App.Counter", throwOnError: true)!.GetMethod("Count")!;

            // Every type on the path names List<ModernMarker>.
            Assert.Equal("1 modern", count.Invoke(null, null));
        }
        finally
        {
            loadContext.Unload();
        }
    }

    [Fact]
    public void RewrittenConsumerOfACopiedSiblingKeepingASubstitutedBaseTypeFailsTheRewrite()
    {
        BuildClosure(KeptShapeApp(doNotRewrite: false), KeptShapeLib(doNotRewrite: false));

        InstrumentationResult result = Run(new InstrumentationConfiguration { ExcludePatterns = ["Fx.Lib.dll"] });

        // The copied sibling's Markers keeps List<LegacyMarker>, which the rewritten MoreMarkers would inherit.
        Assert.False(result.Succeeded);
        RewriteDiagnostic error = Assert.Single(result.Errors);
        Assert.Equal(RewriteDiagnosticIds.UnresolvedReplacement, error.Id);
        Assert.Equal("Fx.App.MoreMarkers", error.Method);
        Assert.Equal(
            "Member-aware substitution rule 'substitute-marker' substitutes " +
                "'System.Collections.Generic.List`1<ClockworkFixtures.Api.LegacyMarker>' in 'Fx.App.MoreMarkers', but the base " +
                "type of 'Fx.Lib.Markers' keeps it, because 'Fx.Lib.Markers' is declared in 'Fx.Lib', which is not rewritten " +
                "in this closure, so 'Fx.App.MoreMarkers' would use 'Fx.Lib.Markers' through a base type, interface, or " +
                "constraint it does not have.",
            error.Message);
    }

    [Fact]
    public void RewrittenConsumerOfADoNotRewriteSiblingKeepingASubstitutedBaseTypeFailsTheRun()
    {
        BuildClosure(KeptShapeApp(doNotRewrite: false), KeptShapeLib(doNotRewrite: true));

        InstrumentationResult result = Run();

        // The rewrite of Fx.App cannot know that Fx.Lib excludes Markers; the staged closure shows it.
        Assert.False(result.Succeeded);
        RewriteDiagnostic error = Assert.Single(result.Errors);
        Assert.Equal(RewriteDiagnosticIds.UnresolvedReplacement, error.Id);
        Assert.Equal("Fx.App.dll", error.Method);
        Assert.Equal(
            "Member-aware substitution rule 'substitute-marker' substitutes " +
                "'System.Collections.Generic.List`1<ClockworkFixtures.Api.LegacyMarker>' in 'Fx.App.MoreMarkers' of 'Fx.App', " +
                "but the base type of 'Fx.Lib.Markers' keeps it, because 'Fx.Lib.Markers' is excluded from rewriting in " +
                "'Fx.Lib', so 'Fx.App.MoreMarkers' would use 'Fx.Lib.Markers' through a base type, interface, or constraint " +
                "it does not have.",
            error.Message);
        Assert.Contains(result.Assemblies, assembly => assembly.RelativePath == "Fx.Lib.dll" && assembly.WasRewritten);
        Assert.Contains(result.Assemblies, assembly => assembly.RelativePath == "Fx.App.dll" && assembly.WasRewritten);
    }

    [Fact]
    public void RewrittenUserOfAnExcludedTypeInheritingASiblingsSubstitutedBaseTypeFailsTheRun()
    {
        BuildClosure(KeptShapeApp(doNotRewrite: true), KeptShapeLib(doNotRewrite: true));

        InstrumentationResult result = Run();

        // MoreMarkers is excluded in Fx.App and inherits the List<LegacyMarker> Fx.Lib's excluded Markers keeps.
        Assert.False(result.Succeeded);
        RewriteDiagnostic error = Assert.Single(result.Errors);
        Assert.Equal(RewriteDiagnosticIds.UnresolvedReplacement, error.Id);
        Assert.Equal("Fx.App.dll", error.Method);
        Assert.Equal(
            "Member-aware substitution rule 'substitute-marker' substitutes " +
                "'System.Collections.Generic.List`1<ClockworkFixtures.Api.LegacyMarker>' in 'Fx.App.Counter' of 'Fx.App', but " +
                "the base type of 'Fx.Lib.Markers' (inherited by 'Fx.App.MoreMarkers') keeps it, because 'Fx.Lib.Markers' is " +
                "excluded from rewriting in 'Fx.Lib', so 'Fx.App.Counter' would use 'Fx.App.MoreMarkers' through a base type, " +
                "interface, or constraint it does not have.",
            error.Message);
    }

    [Fact]
    public void PreRewrittenAssemblyIsRefusedInAClosureThatRewritesDifferentAssemblies()
    {
        BuildClosure(AppSource);
        Run().EnsureSuccess();

        // The staged consumer was rewritten for a closure that also rewrites Fx.Lib, so it calls the substituted
        // signature. Reusing it as an input to a closure that copies Fx.Lib must not be a verified no-op.
        File.Copy(Path.Combine(_staging, "Fx.App.dll"), Path.Combine(_source, "Fx.App.dll"), overwrite: true);
        File.Copy(Path.Combine(_staging, "Fx.App.pdb"), Path.Combine(_source, "Fx.App.pdb"), overwrite: true);

        InstrumentationResult result = Run(new InstrumentationConfiguration { ExcludePatterns = ["Fx.Lib.dll"] });

        Assert.False(result.Succeeded);
        RewriteDiagnostic error = Assert.Single(result.Errors);
        Assert.Equal(RewriteDiagnosticIds.IncompatibleRewriteVersion, error.Id);
        Assert.Contains("'Fx.App' was already rewritten", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FrameworkFacadesOfAnotherTargetFrameworkDoNotShadowTheRuntime()
    {
        // Another target framework's build in a subdirectory carries framework facades, here of the core library that
        // the fixtures reference, forwarding System.Object to each other. The runtime does not probe the subdirectory,
        // so neither does the check of the staged closure.
        BuildClosure(AppSource);
        string nested = Directory.CreateDirectory(Path.Combine(_source, "net472")).FullName;
        FixtureCompiler.Compile(
            "Fx.Other",
            "namespace Fx.Other { public static class Other { public static int Value => 1; } }",
            nested,
            FixtureSymbols.None,
            optimize: false);
        TypeForwarders.Write(nested, "System.Private.CoreLib", "System.Object", "netstandard");
        TypeForwarders.Write(nested, "netstandard", "System.Object", "System.Private.CoreLib");

        Run(new InstrumentationConfiguration { ExcludePatterns = ["Fx.Other.dll"] }).EnsureSuccess();

        var loadContext = new DirectoryLoadContext(_staging);
        try
        {
            Assembly app = loadContext.LoadFromAssemblyPath(Path.Combine(_staging, "Fx.App.dll"));
            Assert.Equal("took modern", app.GetType("Fx.App.Caller", throwOnError: true)!.GetMethod("Run")!.Invoke(null, null));
        }
        finally
        {
            loadContext.Unload();
        }
    }

    [Fact]
    public void ForwarderCycleInTheClosureFailsTheRunWithADiagnostic()
    {
        // A copied type's base type is forwarded round a cycle, which the runtime would not load either.
        BuildClosure(AppSource);
        string cyclic = FixtureCompiler.Compile(
            "Fx.CycA", "namespace Fx.Cyc { public abstract class Base { } }", _source, FixtureSymbols.None, optimize: false);
        FixtureCompiler.Compile(
            "Fx.Derived",
            "namespace Fx.Derived { public sealed class Derived : Fx.Cyc.Base { } }",
            _source,
            FixtureSymbols.None,
            optimize: false,
            additionalReferencePaths: [cyclic]);
        TypeForwarders.Write(_source, "Fx.CycA", "Fx.Cyc.Base", "Fx.CycB");
        TypeForwarders.Write(_source, "Fx.CycB", "Fx.Cyc.Base", "Fx.CycA");

        InstrumentationResult result = Run(
            new InstrumentationConfiguration { ExcludePatterns = ["Fx.Derived.dll", "Fx.CycA.dll", "Fx.CycB.dll"] });

        Assert.False(result.Succeeded);
        RewriteDiagnostic error = Assert.Single(result.Errors);
        Assert.Equal(RewriteDiagnosticIds.UnresolvedReference, error.Id);
        Assert.Equal(
            "The type forwarders for 'Fx.Cyc.Base' form a cycle: Fx.CycA -> Fx.CycB -> Fx.CycA, so the staged closure " +
                "cannot be checked against the type.",
            error.Message);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; a loaded staged assembly can keep its file mapped on Windows.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }
    }

    private static string ShapeOnlyAppSource(bool doNotRewrite) =>
        ShapeOnlyAppTemplate.Replace(
            "[ATTRIBUTE]",
            doNotRewrite ? "[Orleans.Testing.Simulation.Instrumentation.Attributes.DoNotRewrite(\"Shared with code outside the simulation.\")]" : string.Empty,
            StringComparison.Ordinal);

    private static string ReshapeAppSource(bool doNotRewrite) =>
        ReshapeAppTemplate.Replace(
            "[ATTRIBUTE]",
            doNotRewrite ? "[Orleans.Testing.Simulation.Instrumentation.Attributes.DoNotRewrite(\"Runs before the simulation starts.\")]" : string.Empty,
            StringComparison.Ordinal);

    private static string KeptShapeLib(bool doNotRewrite) =>
        KeptShapeLibTemplate.Replace(
            "[LIB]",
            doNotRewrite ? "[Orleans.Testing.Simulation.Instrumentation.Attributes.DoNotRewrite(\"Shared with code outside the simulation.\")]" : string.Empty,
            StringComparison.Ordinal);

    private static string KeptShapeApp(bool doNotRewrite) =>
        KeptShapeAppTemplate.Replace(
            "[APP]",
            doNotRewrite ? "[Orleans.Testing.Simulation.Instrumentation.Attributes.DoNotRewrite(\"Shared with code outside the simulation.\")]" : string.Empty,
            StringComparison.Ordinal);

    private static void AssertReshapeErrors(InstrumentationResult result, string method, params string[] reasons)
    {
        Assert.False(result.Succeeded);
        List<RewriteDiagnostic> errors = [.. result.Errors];
        Assert.Equal(reasons.Length, errors.Count);
        Assert.All(errors, error =>
        {
            Assert.Equal(RewriteDiagnosticIds.UnresolvedReplacement, error.Id);
            Assert.Equal(method, error.Method);
            Assert.StartsWith("Member-aware substitution rule 'substitute-marker' ", error.Message, StringComparison.Ordinal);
            Assert.EndsWith(", through a base type, interface, or constraint it no longer has.", error.Message, StringComparison.Ordinal);
        });
        foreach (string reason in reasons)
        {
            Assert.Contains(errors, error => error.Message.Contains(reason, StringComparison.Ordinal));
        }
    }

    private static void AssertShapeErrors(
        InstrumentationResult result,
        string? method,
        params (string Member, string Reason)[] expected)
    {
        Assert.False(result.Succeeded);
        List<RewriteDiagnostic> errors = [.. result.Errors];
        Assert.Equal(expected.Length, errors.Count);
        Assert.All(errors, error =>
        {
            Assert.Equal(RewriteDiagnosticIds.UnresolvedReplacement, error.Id);
            Assert.Contains("'substitute-marker'", error.Message, StringComparison.Ordinal);
            if (method is not null)
            {
                Assert.Equal(method, error.Method);
            }
        });
        foreach ((string member, string reason) in expected)
        {
            Assert.Contains(errors, error => error.Message.Contains($"member '{member}'", StringComparison.Ordinal)
                && error.Message.Contains(reason, StringComparison.Ordinal));
        }
    }

    private static RewriteRuleSet MarkerRuleSet() =>
        new(
            "clockwork.fixtures.closure-binding",
            "1.0",
            [
                RewriteRule.SubstituteType(
                    "substitute-marker",
                    "ClockworkFixtures.Api.LegacyMarker",
                    RewriteReplacement.Type(FixtureSources.ShimAssemblyName, "ClockworkFixtures.Shims.ModernMarker")),
            ]);

    private void BuildClosure(string appSource, string libSource = LibSource)
    {
        string api = FixtureCompiler.Compile(
            FixtureSources.ApiAssemblyName, FixtureSources.Api, _source, FixtureSymbols.PortableFile, optimize: false);
        FixtureCompiler.Compile(
            FixtureSources.ShimAssemblyName, FixtureSources.Shims, _source, FixtureSymbols.PortableFile, optimize: false,
            additionalReferencePaths: [api]);
        string lib = FixtureCompiler.Compile(
            "Fx.Lib", libSource, _source, FixtureSymbols.PortableFile, optimize: false, additionalReferencePaths: [api]);
        FixtureCompiler.Compile(
            "Fx.App", appSource, _source, FixtureSymbols.PortableFile, optimize: false, additionalReferencePaths: [api, lib]);
    }

    private InstrumentationResult Run(InstrumentationConfiguration? configuration = null) =>
        InstrumentationRunner.Run(new InstrumentationRequest
        {
            SourceDirectory = _source,
            StagingDirectory = _staging,
            Configuration = (configuration ?? new InstrumentationConfiguration()) with
            {
                TargetRuntime = RewriteTestContext.TargetRuntime,
            },
            RuleSet = MarkerRuleSet(),
        });

    private sealed class DirectoryLoadContext(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            string candidate = Path.Combine(directory, assemblyName.Name + ".dll");
            return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
        }
    }
}
