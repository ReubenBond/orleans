using System.Reflection;
using System.Runtime.Loader;
using Orleans.Testing.Simulation.Instrumentation.Diagnostics;
using Orleans.Testing.Simulation.Instrumentation.Rewriting;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;
using Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;
using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Golden;

/// <summary>
/// Golden tests for the rewrite-time check that fails a rewrite (<c>CWR0001</c>) instead of emitting a member reference
/// that does not bind: a substituted signature binds only where the declaring type is rewritten too, and code excluded
/// from rewriting keeps the original signatures, so neither side may reference the other's rewritten members. The
/// closure-level cases, where the two sides are different assemblies, are in <c>ClosureBindingTests</c>.
/// </summary>
public sealed class SubstitutionBindingGoldenTests
{
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

    public static TheoryData<string, string, string> UnboundCalls => new()
    {
        // Without a closure, no other assembly is known to be rewritten, so a sibling keeps its original signature.
        { "Sibling", "public static string Call(LegacyMarker marker) => MarkerSink.Describe(marker);", "MarkerSink::Describe" },

        // A replacement assembly is never rewritten, so the substituted signature does not exist there.
        { "Replacement", "public static string Call(LegacyMarker marker) => MarkerShim.Describe(marker);", "MarkerShim::Describe" },
        { "ReplacementGeneric", "public static LegacyMarker Call(LegacyMarker marker) => MarkerShim.Make(marker);", "MarkerShim::Make" },

        // A type excluded from rewriting keeps its original signature.
        { "Excluded", "public static string Call(LegacyMarker marker) => Excluded.Describe(marker);", "Fx.Excluded::Describe" },
    };

    public static TheoryData<string, string, string> ExcludedCallers => new()
    {
        // A definition token binds to the rewritten signature, which would receive an argument of the original type.
        { "NonGeneric", "Api.Take(marker)", "Fx.Api::Take" },
        { "GenericMethod", "Api.TakeGeneric(marker, 1)", "Fx.Api::TakeGeneric" },

        // A member of a generic instance binds by its original signature, which the rewritten type no longer declares.
        { "GenericType", "Box<int>.Take(marker)", "Fx.Box`1::Take" },
    };

    public static TheoryData<string, string, string?, string, string, string?> BoundaryImplementations => new()
    {
        {
            "ExcludedImplementer",
            """
            [Orleans.Testing.Simulation.Instrumentation.Attributes.DoNotRewrite("Shared with code outside the simulation.")]
            public sealed class Handler : IHandler
            {
                public string Handle(LegacyMarker marker) => marker.ToString();
            }
            """,
            null,
            "Fx.Handler::Handle",
            "implements 'Fx.IHandler::Handle(ClockworkFixtures.Shims.ModernMarker) : System.String' as " +
                "'Handle(ClockworkFixtures.Api.LegacyMarker) : System.String' after rewriting, because 'Fx.Handler' is excluded " +
                "from rewriting, so it keeps the original signature while 'Fx.IHandler' is rewritten",
            null
        },
        {
            "ExcludedExplicitImplementer",
            """
            [Orleans.Testing.Simulation.Instrumentation.Attributes.DoNotRewrite("Shared with code outside the simulation.")]
            public sealed class Handler : IHandler
            {
                string IHandler.Handle(LegacyMarker marker) => marker.ToString();
            }
            """,
            null,
            "Fx.Handler::Fx.IHandler.Handle",
            "because 'Fx.Handler' is excluded from rewriting, so it keeps the original signature while 'Fx.IHandler' is rewritten",
            null
        },
        {
            "ExcludedInterface",
            """
            public sealed class Handler : IHandler
            {
                public string Handle(LegacyMarker marker) => marker.ToString();
            }
            """,
            "Fx.IHandler",
            "Fx.Handler::Handle",
            "implements 'Fx.IHandler::Handle(ClockworkFixtures.Api.LegacyMarker) : System.String' as " +
                "'Handle(ClockworkFixtures.Shims.ModernMarker) : System.String' after rewriting, because 'Fx.IHandler' is " +
                "excluded from rewriting, so it keeps the original signature while 'Fx.Handler' is rewritten",
            null
        },
        {
            "ExcludedInterfaceExplicitImplementer",
            """
            public sealed class Handler : IHandler
            {
                string IHandler.Handle(LegacyMarker marker) => marker.ToString();
            }
            """,
            "Fx.IHandler",
            "Fx.Handler::Fx.IHandler.Handle",
            "because 'Fx.IHandler' is excluded from rewriting, so it keeps the original signature while 'Fx.Handler' is rewritten",
            null
        },
        {
            "ExcludedOverride",
            """
            [Orleans.Testing.Simulation.Instrumentation.Attributes.DoNotRewrite("Shared with code outside the simulation.")]
            public sealed class Handler : HandlerBase
            {
                public override string Handle(LegacyMarker marker) => marker.ToString();
            }
            """,
            null,
            "Fx.Handler::Handle",
            "overrides 'Fx.HandlerBase::Handle(ClockworkFixtures.Shims.ModernMarker) : System.String' as " +
                "'Handle(ClockworkFixtures.Api.LegacyMarker) : System.String' after rewriting, because 'Fx.Handler' is excluded " +
                "from rewriting, so it keeps the original signature while 'Fx.HandlerBase' is rewritten",
            null
        },
        {
            "ExcludedBase",
            """
            public sealed class Handler : HandlerBase
            {
                public override string Handle(LegacyMarker marker) => marker.ToString();
            }
            """,
            "Fx.HandlerBase",
            "Fx.Handler::Handle",
            "overrides 'Fx.HandlerBase::Handle(ClockworkFixtures.Api.LegacyMarker) : System.String' as " +
                "'Handle(ClockworkFixtures.Shims.ModernMarker) : System.String' after rewriting, because 'Fx.HandlerBase' is " +
                "excluded from rewriting, so it keeps the original signature while 'Fx.Handler' is rewritten",
            null
        },
        {
            // The excluded base keeps CompareTo(LegacyMarker), while the rewritten type re-implementing the interface
            // names it with the substitute.
            "ExcludedBaseOfAReimplementer",
            """
            [Orleans.Testing.Simulation.Instrumentation.Attributes.DoNotRewrite("Shared with code outside the simulation.")]
            public class OrderedBase : IComparable<LegacyMarker>
            {
                public int CompareTo(LegacyMarker other) => 0;
            }

            public sealed class Ordered : OrderedBase, IComparable<LegacyMarker>
            {
            }
            """,
            null,
            "Fx.OrderedBase::CompareTo",
            "implements 'System.IComparable`1::CompareTo(ClockworkFixtures.Shims.ModernMarker) : System.Int32' as " +
                "'CompareTo(ClockworkFixtures.Api.LegacyMarker) : System.Int32' after rewriting, because 'Fx.OrderedBase' is " +
                "excluded from rewriting, so it keeps the original signature while 'Fx.Ordered' is rewritten and names " +
                "'System.IComparable`1<ClockworkFixtures.Shims.ModernMarker>'",
            "substitutes 'System.IComparable`1<ClockworkFixtures.Api.LegacyMarker>' in 'Fx.Ordered', but the interface of " +
                "'Fx.OrderedBase' keeps it, because 'Fx.OrderedBase' is excluded from rewriting, so 'Fx.Ordered' would use " +
                "'Fx.OrderedBase' through a base type, interface, or constraint it does not have"
        },
        {
            // The excluded intermediate base keeps naming its base with the substituted type argument.
            "ExcludedIntermediateBase",
            """
            public abstract class Slot<T>
            {
                public abstract string Put(T value);
            }

            [Orleans.Testing.Simulation.Instrumentation.Attributes.DoNotRewrite("Shared with code outside the simulation.")]
            public abstract class MarkerSlot : Slot<LegacyMarker>
            {
            }

            public sealed class Slotted : MarkerSlot
            {
                public override string Put(LegacyMarker value) => value.ToString();
            }
            """,
            null,
            "Fx.Slotted::Put",
            "overrides 'Fx.Slot`1::Put(ClockworkFixtures.Api.LegacyMarker) : System.String' as " +
                "'Put(ClockworkFixtures.Shims.ModernMarker) : System.String' after rewriting, because 'Fx.MarkerSlot' is " +
                "excluded from rewriting, so it names 'Fx.Slot`1<ClockworkFixtures.Api.LegacyMarker>' with the original type " +
                "arguments while 'Fx.Slotted' is rewritten",
            "substitutes 'Fx.Slot`1<ClockworkFixtures.Api.LegacyMarker>' in 'Fx.Slotted', but the base type of 'Fx.MarkerSlot' " +
                "keeps it, because 'Fx.MarkerSlot' is excluded from rewriting, so 'Fx.Slotted' would use 'Fx.MarkerSlot' " +
                "through a base type, interface, or constraint it does not have"
        },
        {
            // The explicit implementation's declaration is retargeted onto the interface's substituted signature, which
            // the excluded interface does not declare.
            "ExcludedGenericInterfaceExplicitImplementer",
            """
            public interface IBox<T>
            {
                string Take(LegacyMarker marker, T value);
            }

            public sealed class Boxed : IBox<int>
            {
                string IBox<int>.Take(LegacyMarker marker, int value) => marker + " " + value;
            }
            """,
            "Fx.IBox`1",
            "Fx.IBox`1<System.Int32>::Take(ClockworkFixtures.Api.LegacyMarker,T)",
            "which maps to 'System.String Fx.IBox`1<System.Int32>::Take(ClockworkFixtures.Shims.ModernMarker,T)', but its " +
                "declaring type 'Fx.IBox`1' is excluded from rewriting and keeps the original signature",
            null
        },
    };

    [Theory]
    [MemberData(nameof(UnboundCalls))]
    public void SubstitutedSignatureFailsWhereItsDeclaringTypeIsNotRewritten(string name, string call, string unbound)
    {
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture($"Fx.Binding{name}", $$"""
            using ClockworkFixtures.Api;
            using ClockworkFixtures.Shims;

            namespace Fx
            {
                public static class Excluded
                {
                    public static string Describe(LegacyMarker marker) => marker.ToString();
                }

                public static class Caller
                {
                    {{call}}
                }
            }
            """);

        RewriteResult result = context.Rewrite(fixturePath, MarkerRuleSet(), MarkerOptions(context, excluded: "Fx.Excluded"));

        RewriteDiagnostic error = AssertUnbound(result, unbound);
        Assert.Equal("Fx.Caller.Call", error.Method);
    }

    public static TheoryData<string, string, string, string, string> RewrittenUsesOfKeptShapes => new()
    {
        {
            // The rewritten code adds a ModernMarker to what is a List<LegacyMarker>.
            "ExcludedBaseType",
            "",
            """
            List<LegacyMarker> markers = new Old();
            markers.Add(new LegacyMarker());
            return markers.Count + " " + markers[0];
            """,
            "Fx.Caller.Run",
            "substitutes 'System.Collections.Generic.List`1<ClockworkFixtures.Api.LegacyMarker>' in 'Fx.Caller', but the base " +
                "type of 'Fx.Old' keeps it, because 'Fx.Old' is excluded from rewriting, so 'Fx.Caller' would use 'Fx.Old'"
        },
        {
            "InheritedFromAnExcludedType",
            "",
            """
            List<LegacyMarker> markers = new Older();
            markers.Add(new LegacyMarker());
            return markers.Count + " " + markers[0];
            """,
            "Fx.Caller.Run",
            "substitutes 'System.Collections.Generic.List`1<ClockworkFixtures.Api.LegacyMarker>' in 'Fx.Caller', but the base " +
                "type of 'Fx.Old' (inherited by 'Fx.Older') keeps it, because 'Fx.Old' is excluded from rewriting, so " +
                "'Fx.Caller' would use 'Fx.Older'"
        },
        {
            // A rewritten type deriving from the excluded one inherits its base type as declared.
            "RewrittenDerivedType",
            """
            public sealed class Newer : Old
            {
            }
            """,
            """
            List<LegacyMarker> markers = new Newer();
            markers.Add(new LegacyMarker());
            return markers.Count + " " + markers[0];
            """,
            "Fx.Newer",
            "substitutes 'System.Collections.Generic.List`1<ClockworkFixtures.Api.LegacyMarker>' in 'Fx.Newer', but the base " +
                "type of 'Fx.Old' keeps it, because 'Fx.Old' is excluded from rewriting, so 'Fx.Newer' would use 'Fx.Old'"
        },
        {
            // An assembly the closure does not rewrite keeps its types' shapes.
            "UnrewrittenAssembly",
            "",
            """
            List<LegacyMarker> markers = new MarkerList();
            markers.Add(new LegacyMarker());
            return markers.Count + " " + markers[0];
            """,
            "Fx.Caller.Run",
            "substitutes 'System.Collections.Generic.List`1<ClockworkFixtures.Api.LegacyMarker>' in 'Fx.Caller', but the base " +
                "type of 'ClockworkFixtures.Api.MarkerList' keeps it, because 'ClockworkFixtures.Api.MarkerList' is declared in " +
                "'ClockworkFixtures.Api', which is not rewritten in this closure, so 'Fx.Caller' would use " +
                "'ClockworkFixtures.Api.MarkerList'"
        },
        {
            // The rewritten code calls IEnumerable<ModernMarker>.GetEnumerator on a type implementing only
            // IEnumerable<LegacyMarker>.
            "ExcludedInterface",
            """
            [Orleans.Testing.Simulation.Instrumentation.Attributes.DoNotRewrite("Shared with code outside the simulation.")]
            public sealed class OldBag : IEnumerable<LegacyMarker>
            {
                public IEnumerator<LegacyMarker> GetEnumerator()
                {
                    yield return new LegacyMarker();
                }

                System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
            }
            """,
            """
            IEnumerable<LegacyMarker> markers = new OldBag();
            foreach (LegacyMarker marker in markers)
            {
                return marker.ToString();
            }

            return "none";
            """,
            "Fx.Caller.Run",
            "substitutes 'System.Collections.Generic.IEnumerable`1<ClockworkFixtures.Api.LegacyMarker>' in 'Fx.Caller', but " +
                "the interface of 'Fx.OldBag' keeps it, because 'Fx.OldBag' is excluded from rewriting, so 'Fx.Caller' would " +
                "use 'Fx.OldBag'"
        },
        {
            // The rewritten code instantiates T with List<ModernMarker>, which satisfies only the substituted constraint.
            "ExcludedTypeConstraint",
            """
            [Orleans.Testing.Simulation.Instrumentation.Attributes.DoNotRewrite("Shared with code outside the simulation.")]
            public sealed class Holder<T>
                where T : IEnumerable<LegacyMarker>
            {
                public string Describe() => typeof(T).Name;
            }
            """,
            """
            return new Holder<List<LegacyMarker>>().Describe();
            """,
            "Fx.Caller.Run",
            "substitutes 'System.Collections.Generic.IEnumerable`1<ClockworkFixtures.Api.LegacyMarker>' in 'Fx.Caller', but " +
                "the constraint on 'T' of 'Fx.Holder`1' keeps it, because 'Fx.Holder`1' is excluded from rewriting, so " +
                "'Fx.Caller' would use 'Fx.Holder`1'"
        },
        {
            "ExcludedMethodConstraint",
            """
            [Orleans.Testing.Simulation.Instrumentation.Attributes.DoNotRewrite("Shared with code outside the simulation.")]
            public static class Shelf
            {
                public static string Stock<T>(T items)
                    where T : IEnumerable<LegacyMarker> => typeof(T).Name;
            }
            """,
            """
            return Shelf.Stock(new List<LegacyMarker>());
            """,
            "Fx.Caller.Run",
            "substitutes 'System.Collections.Generic.IEnumerable`1<ClockworkFixtures.Api.LegacyMarker>' in 'Fx.Caller', but " +
                "the constraint on 'T' of 'System.String Fx.Shelf::Stock(T)' keeps it, because 'Fx.Shelf' is excluded from " +
                "rewriting, so 'Fx.Caller' would use 'System.String Fx.Shelf::Stock(T)'"
        },
    };

    public static TheoryData<string, string, string, string> ExcludedUsesOfReshapedTypes => new()
    {
        {
            // The excluded code calls List<LegacyMarker>.Add on a type whose base type is now List<ModernMarker>.
            "BaseType",
            """
            public static int Use()
            {
                var markers = new Markers();
                markers.Add(new LegacyMarker());
                return markers.Count;
            }
            """,
            "Fx.Legacy.Use",
            "substitutes 'System.Collections.Generic.List`1<ClockworkFixtures.Api.LegacyMarker>' in the base type of " +
                "'Fx.Markers', but 'Fx.Legacy' is excluded from rewriting and still uses 'Fx.Markers' as declared"
        },
        {
            "InheritedBaseType",
            """
            public static int Use() => new DerivedMarkers().Count;
            """,
            "Fx.Legacy.Use",
            "substitutes 'System.Collections.Generic.List`1<ClockworkFixtures.Api.LegacyMarker>' in the base type of " +
                "'Fx.Markers' (inherited by 'Fx.DerivedMarkers'), but 'Fx.Legacy' is excluded from rewriting and still uses " +
                "'Fx.DerivedMarkers' as declared"
        },
        {
            "Interface",
            """
            public static IEnumerable<LegacyMarker> Use(IMarkers markers) => markers;
            """,
            "Fx.Legacy.Use",
            "substitutes 'System.Collections.Generic.IEnumerable`1<ClockworkFixtures.Api.LegacyMarker>' in the interface of " +
                "'Fx.IMarkers', but 'Fx.Legacy' is excluded from rewriting and still uses 'Fx.IMarkers' as declared"
        },
        {
            // List<LegacyMarker> satisfies only the original constraint.
            "TypeConstraint",
            """
            public static object Use() => new Holder<List<LegacyMarker>>();
            """,
            "Fx.Legacy.Use",
            "substitutes 'System.Collections.Generic.IEnumerable`1<ClockworkFixtures.Api.LegacyMarker>' in the constraint on " +
                "'T' of 'Fx.Holder`1', but 'Fx.Legacy' is excluded from rewriting and still uses 'Fx.Holder`1' as declared"
        },
        {
            "MethodConstraint",
            """
            public static int Use() => Shelf.Stock(new List<LegacyMarker>());
            """,
            "Fx.Legacy.Use",
            "substitutes 'System.Collections.Generic.IEnumerable`1<ClockworkFixtures.Api.LegacyMarker>' in the constraint on " +
                "'T' of 'System.Int32 Fx.Shelf::Stock(T)', but 'Fx.Legacy' is excluded from rewriting and still uses " +
                "'System.Int32 Fx.Shelf::Stock(T)' as declared"
        },
    };

    [Theory]
    [MemberData(nameof(ExcludedCallers))]
    public void ExcludedCallerOfARewrittenSignatureFailsTheRewrite(string name, string call, string unbound)
    {
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture($"Fx.ExcludedCaller{name}", $$"""
            using ClockworkFixtures.Api;

            {{DoNotRewriteAttributeSource}}

            namespace Fx
            {
                public static class Api
                {
                    public static string Take(LegacyMarker marker) => marker.ToString();

                    public static string TakeGeneric<T>(LegacyMarker marker, T value) => marker + " " + value;
                }

                public static class Box<T>
                {
                    public static string Take(LegacyMarker marker) => marker.ToString();
                }

                [Orleans.Testing.Simulation.Instrumentation.Attributes.DoNotRewrite("Runs before the simulation starts.")]
                public static class Legacy
                {
                    public static string Call(LegacyMarker marker) => {{call}};
                }
            }
            """);

        RewriteResult result = context.Rewrite(fixturePath, MarkerRuleSet(), MarkerOptions(context));

        RewriteDiagnostic error = AssertUnbound(result, unbound);
        Assert.Equal("Fx.Legacy.Call", error.Method);
        Assert.Contains("'Fx.Legacy' is excluded from rewriting", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ExcludedUsesOfReshapedTypes))]
    public void ExcludedUseOfAReshapedTypeFailsTheRewrite(string name, string use, string method, string reason)
    {
        // The rewrite substitutes the base types, interfaces, and constraints of the types and methods it rewrites, but
        // excluded code keeps the original ones and could use the type through a base type or interface it no longer has.
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture($"Fx.ExcludedReshape{name}", $$"""
            using System.Collections.Generic;
            using ClockworkFixtures.Api;

            {{DoNotRewriteAttributeSource}}

            namespace Fx
            {
                public class Markers : List<LegacyMarker>
                {
                }

                public sealed class DerivedMarkers : Markers
                {
                }

                public interface IMarkers : IEnumerable<LegacyMarker>
                {
                }

                public sealed class Holder<T>
                    where T : IEnumerable<LegacyMarker>
                {
                }

                public static class Shelf
                {
                    public static int Stock<T>(T items)
                        where T : IEnumerable<LegacyMarker> => 1;
                }

                [Orleans.Testing.Simulation.Instrumentation.Attributes.DoNotRewrite("Runs before the simulation starts.")]
                public static class Legacy
                {
                    {{use}}
                }
            }
            """);

        RewriteResult result = context.Rewrite(fixturePath, MarkerRuleSet(), MarkerOptions(context));

        RewriteDiagnostic error = AssertUnbound(result, reason);
        Assert.Equal(method, error.Method);
    }

    [Fact]
    public void ExcludedTypeDerivingFromAReshapedTypeFailsTheRewrite()
    {
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.ExcludedReshapeDerived", $$"""
            using System.Collections.Generic;
            using ClockworkFixtures.Api;

            {{DoNotRewriteAttributeSource}}

            namespace Fx
            {
                public class Markers : List<LegacyMarker>
                {
                }

                [Orleans.Testing.Simulation.Instrumentation.Attributes.DoNotRewrite("Shared with code outside the simulation.")]
                public sealed class LegacyMarkers : Markers
                {
                    public LegacyMarker First() => this[0];
                }
            }
            """);

        RewriteResult result = context.Rewrite(fixturePath, MarkerRuleSet(), MarkerOptions(context));

        // The excluded type's own members use its base type's original members; it is reported once.
        RewriteDiagnostic error = AssertUnbound(
            result,
            "substitutes 'System.Collections.Generic.List`1<ClockworkFixtures.Api.LegacyMarker>' in the base type of " +
                "'Fx.Markers', but 'Fx.LegacyMarkers' is excluded from rewriting and still uses 'Fx.Markers' as declared, " +
                "through a base type, interface, or constraint it no longer has");
        Assert.Equal("Fx.LegacyMarkers", error.Method);
    }

    [Fact]
    public void TypesWhoseShapesMentionNoSubstitutedTypeCrossTheBoundary()
    {
        // A type whose shape mentions no substituted type is the same on both sides of the boundary, whichever side
        // declares it.
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.ExcludedUnreshaped", $$"""
            using System.Collections.Generic;
            using ClockworkFixtures.Api;

            {{DoNotRewriteAttributeSource}}

            namespace Fx
            {
                public class Markers : List<LegacyMarker>
                {
                }

                public class Names : List<string>
                {
                }

                [Orleans.Testing.Simulation.Instrumentation.Attributes.DoNotRewrite("Shared with code outside the simulation.")]
                public class Labels : List<string>
                {
                }

                [Orleans.Testing.Simulation.Instrumentation.Attributes.DoNotRewrite("Runs before the simulation starts.")]
                public static class Legacy
                {
                    public static int Use()
                    {
                        var names = new Names();
                        names.Add("name");
                        return names.Count;
                    }
                }

                public static class Caller
                {
                    public static int Run()
                    {
                        var labels = new Labels();
                        labels.Add("label");
                        return new Markers().Count + Legacy.Use() + labels.Count;
                    }
                }
            }
            """);

        context.Rewrite(fixturePath, MarkerRuleSet(), MarkerOptions(context)).EnsureSuccess();
    }

    [Theory]
    [MemberData(nameof(RewrittenUsesOfKeptShapes))]
    public void RewrittenUseOfATypeKeepingASubstitutedShapeFailsTheRewrite(string name, string types, string run, string method, string reason)
    {
        // Rewritten code sees a base type, interface, or constraint composed from the substituted type as composed from
        // its substitute, while a type the rewrite does not change keeps the original.
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture($"Fx.KeptShape{name}", $$"""
            using System.Collections.Generic;
            using ClockworkFixtures.Api;

            {{DoNotRewriteAttributeSource}}

            namespace Fx
            {
                [Orleans.Testing.Simulation.Instrumentation.Attributes.DoNotRewrite("Shared with code outside the simulation.")]
                public class Old : List<LegacyMarker>
                {
                }

                [Orleans.Testing.Simulation.Instrumentation.Attributes.DoNotRewrite("Shared with code outside the simulation.")]
                public class Older : Old
                {
                }

                {{types}}

                public static class Caller
                {
                    public static string Run()
                    {
                        {{run}}
                    }
                }
            }
            """);

        RewriteResult result = context.Rewrite(fixturePath, MarkerRuleSet(), MarkerOptions(context));

        RewriteDiagnostic error = AssertUnbound(result, reason);
        Assert.Equal(method, error.Method);
        Assert.EndsWith(" through a base type, interface, or constraint it does not have.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RewrittenCodeUsingAFieldOfAnExcludedTypeFailsTheRewrite()
    {
        // The excluded type keeps a real Lock in its field, which the rewritten lock statement would enter as a
        // ControlledLock.
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.ExcludedField", $$"""
            using System.Threading;

            {{DoNotRewriteAttributeSource}}

            namespace Fx
            {
                [Orleans.Testing.Simulation.Instrumentation.Attributes.DoNotRewrite("Shared with code outside the simulation.")]
                public static class Globals
                {
                    public static readonly Lock Gate = new();
                }

                public static class Locker
                {
                    public static int Run()
                    {
                        lock (Globals.Gate)
                        {
                            return 1;
                        }
                    }
                }
            }
            """);
        string runtimePath = typeof(ControlledLock).Assembly.Location;
        var options = new RewriteOptions
        {
            TargetRuntime = RewriteTestContext.TargetRuntime,
            ReplacementAssemblyPaths = [runtimePath],
            ReferenceSearchDirectories = [context.Directory, Path.GetDirectoryName(runtimePath)!],
        };

        RewriteResult result = context.Rewrite(
            fixturePath,
            BuiltInRuleSets.BuildControlledTasks(BuiltInRuleSets.AllFamilies),
            options);

        Assert.False(result.Succeeded);
        RewriteDiagnostic error = Assert.Single(result.Errors);
        Assert.Equal(RewriteDiagnosticIds.UnresolvedReplacement, error.Id);
        Assert.Equal("Fx.Locker.Run", error.Method);
        Assert.Contains("'clockwork.lock.type'", error.Message, StringComparison.Ordinal);
        Assert.Contains("Fx.Globals::Gate", error.Message, StringComparison.Ordinal);
        Assert.Contains("'Fx.Globals', which is excluded from rewriting", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExcludedTypeIsNotRewrittenWithItsCompilerGeneratedTypes(bool byAttribute)
    {
        // The async method's state machine, the iterator, the lambda closures, and the async lambda's state machine
        // nested in its closure are compiler-generated types nested in the excluded type, so they are excluded with
        // it and keep the BCL machinery its stub methods use. An explicitly declared nested type is rewritten.
        using var context = RewriteTestContext.Create();
        string attribute = byAttribute ? "[Orleans.Testing.Simulation.Instrumentation.Attributes.DoNotRewrite(\"Runs outside the simulation.\")]" : string.Empty;
        string fixturePath = context.CompileFixture("Fx.ExcludedHelpers", $$"""
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;

            {{DoNotRewriteAttributeSource}}

            namespace Fx
            {
                {{attribute}}
                public static class Legacy
                {
                    public static async Task<int> AwaitAsync()
                    {
                        await Task.Yield();
                        return 1;
                    }

                    public static IEnumerable<int> Iterate()
                    {
                        yield return 1;
                        yield return 2;
                    }

                    public static int Lambda(int offset)
                    {
                        Func<int, int> add = value => value + offset;
                        Func<int, int> twice = static value => value * 2;
                        Func<Task<int>> later = async () =>
                        {
                            await Task.Yield();
                            return add(1);
                        };
                        return twice(later().GetAwaiter().GetResult());
                    }

                    public static class Declared
                    {
                        public static async Task<int> AwaitAsync()
                        {
                            await Task.Yield();
                            return 2;
                        }
                    }
                }
            }
            """);
        string runtimePath = typeof(ControlledLock).Assembly.Location;
        var options = new RewriteOptions
        {
            TargetRuntime = RewriteTestContext.TargetRuntime,
            ReplacementAssemblyPaths = [runtimePath],
            ReferenceSearchDirectories = [context.Directory, Path.GetDirectoryName(runtimePath)!],
            ExcludedTypeFullNames = byAttribute ? [] : ["Fx.Legacy"],
        };

        RewriteResult result = context.Rewrite(
            fixturePath,
            BuiltInRuleSets.BuildControlledTasks(BuiltInRuleSets.AllFamilies),
            options);

        result.EnsureSuccess();
        using ModuleDefinition module = context.LoadModule(Path.Combine(context.Directory, "Fx.ExcludedHelpers.rewritten.dll"));
        TypeDefinition legacy = module.GetType("Fx.Legacy");
        TypeDefinition declared = legacy.NestedTypes.Single(type => type.Name == "Declared");
        TypeDefinition[] generated = [.. NestedTypes(legacy).Except([declared, .. NestedTypes(declared)])];
        string[] excluded = [.. result.Manifest.Exclusions.Select(exclusion => exclusion.TypeFullName)];

        Assert.Contains(generated, type => type.Name.StartsWith("<AwaitAsync>", StringComparison.Ordinal));
        Assert.Contains(generated, type => type.Name.StartsWith("<Iterate>", StringComparison.Ordinal));
        Assert.Contains(generated, type => type.Name == "<>c");
        Assert.Contains(generated, type => type.DeclaringType.Name.StartsWith("<>c__DisplayClass", StringComparison.Ordinal));
        Assert.Equal(
            generated.Select(type => type.FullName).Append("Fx.Legacy").Order(StringComparer.Ordinal),
            excluded.Order(StringComparer.Ordinal));
        Assert.All(generated.Where(IsAsyncStateMachine), type => Assert.StartsWith(
            "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1",
            type.Fields.Single(field => field.Name == "<>t__builder").FieldType.FullName,
            StringComparison.Ordinal));
        Assert.All(NestedTypes(declared).Where(IsAsyncStateMachine), type => Assert.StartsWith(
            "Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices.ControlledAsyncTaskMethodBuilder`1",
            type.Fields.Single(field => field.Name == "<>t__builder").FieldType.FullName,
            StringComparison.Ordinal));
        Assert.Equal(2, generated.Count(IsAsyncStateMachine));
        Assert.Single(NestedTypes(declared), IsAsyncStateMachine);
    }

    [Fact]
    public void RewrittenCallerCrossingIntoAnExcludedTypeWithASubstitutedTypeFailsTheRewrite()
    {
        // The excluded type and its state machine are not rewritten, so its method still returns the BCL configured
        // awaitable, which the rewritten caller would await as the controlled one.
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.ExcludedBoundary", $$"""
            using System.Runtime.CompilerServices;
            using System.Threading.Tasks;

            {{DoNotRewriteAttributeSource}}

            namespace Fx
            {
                [Orleans.Testing.Simulation.Instrumentation.Attributes.DoNotRewrite("Runs outside the simulation.")]
                public static class Legacy
                {
                    public static async Task<int> AwaitAsync()
                    {
                        await Task.Yield();
                        return 1;
                    }

                    public static ConfiguredTaskAwaitable<int> Configured() => AwaitAsync().ConfigureAwait(false);
                }

                public static class Caller
                {
                    public static async Task<int> Run() => await Legacy.Configured();
                }
            }
            """);
        string runtimePath = typeof(ControlledLock).Assembly.Location;
        var options = new RewriteOptions
        {
            TargetRuntime = RewriteTestContext.TargetRuntime,
            ReplacementAssemblyPaths = [runtimePath],
            ReferenceSearchDirectories = [context.Directory, Path.GetDirectoryName(runtimePath)!],
        };

        RewriteResult result = context.Rewrite(
            fixturePath,
            BuiltInRuleSets.BuildControlledTasks(BuiltInRuleSets.AllFamilies),
            options);

        Assert.False(result.Succeeded);
        RewriteDiagnostic error = Assert.Single(result.Errors);
        Assert.Equal(RewriteDiagnosticIds.UnresolvedReplacement, error.Id);
        Assert.StartsWith("Fx.Caller/<Run>", error.Method, StringComparison.Ordinal);
        Assert.Contains("'clockwork.tasks.configured.awaitable.generic'", error.Message, StringComparison.Ordinal);
        Assert.Contains("Fx.Legacy::Configured()", error.Message, StringComparison.Ordinal);
        Assert.Contains("'Fx.Legacy' is excluded from rewriting", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BoundaryImplementations))]
    public void ImplementationOrOverrideAcrossTheExclusionBoundaryFailsTheRewrite(
        string name,
        string types,
        string? excluded,
        string member,
        string reason,
        string? kept)
    {
        // The runtime matches an implementation or override to its member by signature when it loads the type, so a
        // signature rewritten on one side only would throw TypeLoadException.
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture($"Fx.Boundary{name}", $$"""
            using System;
            using ClockworkFixtures.Api;

            {{DoNotRewriteAttributeSource}}

            namespace Fx
            {
                public interface IHandler
                {
                    string Handle(LegacyMarker marker);
                }

                public abstract class HandlerBase
                {
                    public abstract string Handle(LegacyMarker marker);
                }

                {{types}}
            }
            """);

        RewriteResult result = context.Rewrite(fixturePath, MarkerRuleSet(), MarkerOptions(context, excluded));

        if (kept is null)
        {
            RewriteDiagnostic error = AssertUnbound(result, member);
            Assert.Contains(reason, error.Message, StringComparison.Ordinal);
            return;
        }

        // A rewritten type deriving from an excluded base which keeps a part composed from the substituted type also
        // fails as a use of that base.
        Assert.False(result.Succeeded);
        Assert.Equal(2, result.Errors.Count());
        Assert.All(result.Errors, error => Assert.Equal(RewriteDiagnosticIds.UnresolvedReplacement, error.Id));
        Assert.Contains(result.Errors, error => error.Message.Contains(member, StringComparison.Ordinal)
            && error.Message.Contains(reason, StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Message.Contains(kept, StringComparison.Ordinal));
    }

    [Fact]
    public void ImplementationsAndOverridesInsideTheRewriteBind()
    {
        // An explicit implementation names the member it implements by signature, so it is retargeted with it; that of a
        // generic interface is a member reference whose signature the rewrite substitutes.
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.Implementations", """
            using System;
            using ClockworkFixtures.Api;

            namespace Fx
            {
                public interface IHandler
                {
                    string Handle(LegacyMarker marker);
                }

                public interface IBox<T>
                {
                    string Take(LegacyMarker marker, T value);
                }

                public abstract class HandlerBase
                {
                    public abstract string Handle(LegacyMarker marker);
                }

                public sealed class Handler : IHandler
                {
                    public string Handle(LegacyMarker marker) => "handled " + marker;
                }

                public sealed class ExplicitHandler : IHandler, IBox<int>
                {
                    string IHandler.Handle(LegacyMarker marker) => "explicitly handled " + marker;

                    string IBox<int>.Take(LegacyMarker marker, int value) => "took " + marker + " " + value;
                }

                public sealed class DerivedHandler : HandlerBase
                {
                    public override string Handle(LegacyMarker marker) => "overrode " + marker;
                }

                public sealed class Ordered : IComparable<LegacyMarker>
                {
                    public int CompareTo(LegacyMarker other) => 7;
                }

                public abstract class Slot<T>
                {
                    public abstract string Put(T value);
                }

                public sealed class MarkerSlot : Slot<LegacyMarker>
                {
                    public override string Put(LegacyMarker value) => "put " + value;
                }

                public static class Caller
                {
                    public static string Run()
                    {
                        var marker = new LegacyMarker();
                        IHandler handler = new Handler();
                        IHandler explicitHandler = new ExplicitHandler();
                        IBox<int> box = new ExplicitHandler();
                        HandlerBase derived = new DerivedHandler();
                        IComparable<LegacyMarker> ordered = new Ordered();
                        Slot<LegacyMarker> slot = new MarkerSlot();
                        return handler.Handle(marker) + ", " + explicitHandler.Handle(marker) + ", " + box.Take(marker, 1) +
                            ", " + derived.Handle(marker) + ", " + ordered.CompareTo(marker) + ", " + slot.Put(marker);
                    }
                }
            }
            """);

        context.Rewrite(fixturePath, MarkerRuleSet(), MarkerOptions(context)).EnsureSuccess();

        var loadContext = new DirectoryLoadContext(context.Directory);
        try
        {
            string rewrittenPath = Path.Combine(context.Directory, "Fx.Implementations.rewritten.dll");
            Type caller = loadContext.LoadFromAssemblyPath(rewrittenPath).GetType("Fx.Caller", throwOnError: true)!;
            Assert.Equal(
                "handled modern, explicitly handled modern, took modern 1, overrode modern, 7, put modern",
                caller.GetMethod("Run")!.Invoke(null, null));
        }
        finally
        {
            loadContext.Unload();
        }
    }

    [Fact]
    public void ExplicitImplementationOfAMemberTheSubstituteLacksFailsTheRewrite()
    {
        // An explicit implementation names the member it implements, so it is retargeted onto the substitute's member,
        // which must exist for the type to load.
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.ProbeImplementation", """
            using ClockworkFixtures.Api;

            namespace Fx
            {
                public sealed class Probe : IProbe
                {
                    int IProbe.Probe() => 1;
                }
            }
            """);
        var rules = new RewriteRuleSet(
            "clockwork.fixtures.binding",
            "1.0",
            [
                RewriteRule.SubstituteType(
                    "substitute-probe",
                    "ClockworkFixtures.Api.IProbe",
                    RewriteReplacement.Type(FixtureSources.ShimAssemblyName, "ClockworkFixtures.Shims.IModernProbe")),
            ]);

        RewriteResult result = context.Rewrite(fixturePath, rules, MarkerOptions(context));

        Assert.False(result.Succeeded, "The rewrite succeeded: " + string.Join(" | ", result.Diagnostics));
        Assert.False(result.WasWritten);
        RewriteDiagnostic error = Assert.Single(result.Errors);
        Assert.Equal(RewriteDiagnosticIds.UnresolvedReplacement, error.Id);
        Assert.Equal("Fx.Probe.ClockworkFixtures.Api.IProbe.Probe", error.Method);
        Assert.Contains("'substitute-probe'", error.Message, StringComparison.Ordinal);
        Assert.Contains("ClockworkFixtures.Api.IProbe::Probe()", error.Message, StringComparison.Ordinal);
        Assert.Contains(
            "has no matching member on the replacement type 'ClockworkFixtures.Shims.IModernProbe'",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MultidimensionalArrayOfASubstitutedTypeBinds()
    {
        // The runtime provides an array type's constructor and accessors, so they bind with the substituted element type.
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.Arrays", """
            using ClockworkFixtures.Api;

            namespace Fx
            {
                public static class Arrays
                {
                    public static string Describe()
                    {
                        var markers = new LegacyMarker[1, 1];
                        markers[0, 0] = new LegacyMarker();
                        return markers[0, 0].ToString();
                    }
                }
            }
            """);

        context.Rewrite(fixturePath, MarkerRuleSet(), MarkerOptions(context)).EnsureSuccess();

        string rewrittenPath = Path.Combine(context.Directory, "Fx.Arrays.rewritten.dll");
        using (ModuleDefinition module = context.LoadModule(rewrittenPath))
        {
            List<string> targets = CecilInspect.CallTargets(CecilInspect.GetMethod(module, "Fx.Arrays", "Describe"));
            Assert.Contains(targets, target => target.Contains("ClockworkFixtures.Shims.ModernMarker[", StringComparison.Ordinal)
                && target.EndsWith("]::Get(System.Int32,System.Int32)", StringComparison.Ordinal));
            Assert.DoesNotContain(targets, target => target.Contains("LegacyMarker", StringComparison.Ordinal));
        }

        var loadContext = new DirectoryLoadContext(context.Directory);
        try
        {
            Type arrays = loadContext.LoadFromAssemblyPath(rewrittenPath).GetType("Fx.Arrays", throwOnError: true)!;
            Assert.Equal("modern", arrays.GetMethod("Describe")!.Invoke(null, null));
        }
        finally
        {
            loadContext.Unload();
        }
    }

    private static IEnumerable<TypeDefinition> NestedTypes(TypeDefinition type) =>
        type.NestedTypes.SelectMany(nested => NestedTypes(nested).Prepend(nested));

    private static bool IsAsyncStateMachine(TypeDefinition type) =>
        type.Interfaces.Any(implementation =>
            implementation.InterfaceType.FullName == "System.Runtime.CompilerServices.IAsyncStateMachine");

    private static RewriteDiagnostic AssertUnbound(RewriteResult result, string unbound)
    {
        Assert.False(result.Succeeded, "The rewrite succeeded: " + string.Join(" | ", result.Diagnostics));
        Assert.False(result.WasWritten);
        RewriteDiagnostic error = Assert.Single(result.Errors);
        Assert.Equal(RewriteDiagnosticIds.UnresolvedReplacement, error.Id);
        Assert.Contains("'substitute-marker'", error.Message, StringComparison.Ordinal);
        Assert.Contains(unbound, error.Message, StringComparison.Ordinal);
        return error;
    }

    private static RewriteRuleSet MarkerRuleSet() =>
        new(
            "clockwork.fixtures.binding",
            "1.0",
            [
                RewriteRule.SubstituteType(
                    "substitute-marker",
                    "ClockworkFixtures.Api.LegacyMarker",
                    RewriteReplacement.Type(FixtureSources.ShimAssemblyName, "ClockworkFixtures.Shims.ModernMarker")),
            ]);

    private static RewriteOptions MarkerOptions(RewriteTestContext context, string? excluded = null) =>
        new()
        {
            TargetRuntime = RewriteTestContext.TargetRuntime,
            ReplacementAssemblyPaths = [context.ShimPath],
            ReferenceSearchDirectories = [context.Directory],
            ExcludedTypeFullNames = excluded is null ? [] : [excluded],
        };

    private sealed class DirectoryLoadContext(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            string candidate = Path.Combine(directory, assemblyName.Name + ".dll");
            return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
        }
    }
}
