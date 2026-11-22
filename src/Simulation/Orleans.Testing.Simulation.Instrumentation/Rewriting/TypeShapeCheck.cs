using System.Text;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Rewriting;

/// <summary>
/// Finds a type's interface implementations and method overrides that no longer match the member they implement or
/// override because a type substitution rewrote the signature on one side of the excluded/rewritten boundary only. The
/// runtime matches an implicit implementation, an explicit implementation's declaration, and an override to its member
/// by name and signature when it loads the type, so such a mismatch is not a member reference that fails to bind but a
/// <see cref="TypeLoadException"/> when the type loads, or an implementation or override that silently stops being one.
/// </summary>
/// <remarks>
/// Signatures are compared as the implementing type sees them, with the generic arguments of the implemented interface
/// or base type substituted in. A pair that matches once each substituted type is replaced by its substitute, but not
/// exactly, is a pair the rewrite separated; a pair that matches exactly binds. The search for an implementation or an
/// overridden member stops at the first type whose definition does not show its signatures as they are after the
/// rewrite, since nothing can be concluded from it.
/// </remarks>
internal sealed class TypeShapeCheck
{
    private readonly Dictionary<string, string> _substitutes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RewriteRule> _rules = new(StringComparer.Ordinal);
    private readonly Dictionary<TypeDefinition, bool> _declaresMentioningMember = new(ReferenceEqualityComparer.Instance);

    /// <summary>Initializes a new instance of the <see cref="TypeShapeCheck"/> class.</summary>
    /// <param name="substitutions">The in-range type substitutions, keyed by the substituted type's Cecil full name.</param>
    public TypeShapeCheck(IEnumerable<KeyValuePair<string, RewriteRule>> substitutions)
    {
        foreach (KeyValuePair<string, RewriteRule> substitution in substitutions)
        {
            string substitute = substitution.Value.Replacement.DeclaringTypeFullName;
            _substitutes.TryAdd(substitution.Key, substitute);
            _rules.TryAdd(substitution.Key, substitution.Value);
            _rules.TryAdd(substitute, substitution.Value);
        }
    }

    /// <summary>Returns the interface implementations and overrides of <paramref name="type"/> that no longer match.</summary>
    /// <param name="type">The implementing or overriding type, with its signatures as they are after the rewrite.</param>
    /// <param name="resolve">Resolves a type reference to its definition, or returns <see langword="null"/>.</param>
    /// <param name="canInspect">Whether a definition shows its signatures as they are after the rewrite.</param>
    /// <returns>The mismatches.</returns>
    public List<TypeShapeMismatch> Check(
        TypeDefinition type,
        Func<TypeReference, TypeDefinition?> resolve,
        Func<TypeDefinition, bool> canInspect)
    {
        var mismatches = new List<TypeShapeMismatch>();
        if (_rules.Count == 0)
        {
            return mismatches;
        }

        Rendered[] self = Plain(type);
        foreach (InterfaceImplementation implementation in type.Interfaces)
        {
            if ((!Mentions(implementation.InterfaceType) && !DeclaresMentioningMember(implementation.InterfaceType, resolve))
                || resolve(implementation.InterfaceType) is not { } declaring
                || !canInspect(declaring))
            {
                continue;
            }

            Rendered[] arguments = Arguments(implementation.InterfaceType, self);
            foreach (MethodDefinition declared in declaring.Methods.Where(method => method.IsVirtual && !method.IsFinal))
            {
                Rendered expected = Signature(declared, arguments);
                if (expected.Mentioned.Count > 0
                    && FindImplementationMismatch(type, self, implementation.InterfaceType, declaring, declared, expected, resolve, canInspect)
                        is { } mismatch)
                {
                    mismatches.Add(mismatch);
                }
            }
        }

        foreach (MethodDefinition method in type.Methods.Where(method => method.IsVirtual && !method.IsNewSlot && !method.IsStatic))
        {
            if (MentionsAny(method)
                && FindOverrideMismatch(type, self, method, resolve, canInspect) is { } mismatch)
            {
                mismatches.Add(mismatch);
            }
        }

        return mismatches;
    }

    /// <summary>Returns the substitution rules for the types a mismatch mentions.</summary>
    /// <param name="mismatch">The mismatch.</param>
    /// <returns>The rules, each once.</returns>
    public RewriteRule[] RulesFor(TypeShapeMismatch mismatch) =>
        [.. mismatch.MentionedTypes.Select(name => _rules[name]).DistinctBy(rule => rule.Id, StringComparer.Ordinal)];

    private TypeShapeMismatch? FindImplementationMismatch(
        TypeDefinition type,
        Rendered[] self,
        TypeReference interfaceReference,
        TypeDefinition declaring,
        MethodDefinition declared,
        Rendered expected,
        Func<TypeReference, TypeDefinition?> resolve,
        Func<TypeDefinition, bool> canInspect)
    {
        // An explicit implementation names the member it implements: its declaration must still bind to that member,
        // and its own signature must still match it.
        string interfaceName = Render(interfaceReference, self).Loose;
        foreach (MethodDefinition method in type.Methods.Where(method => method.HasOverrides))
        {
            foreach (MethodReference implemented in method.Overrides)
            {
                if (implemented.Name != declared.Name || Render(implemented.DeclaringType, self).Loose != interfaceName)
                {
                    continue;
                }

                Rendered named = Signature(implemented, Arguments(implemented.DeclaringType, self));
                if (named.Loose != expected.Loose)
                {
                    continue;
                }

                Rendered actual = Signature(method, self);
                return named.Exact != expected.Exact
                    ? Mismatch(method, type, declaring, interfaceReference, declared, isOverride: false, expected, named)
                    : actual.Exact != expected.Exact
                        ? Mismatch(method, type, declaring, interfaceReference, declared, isOverride: false, expected, actual)
                        : null;
            }
        }

        if (type.IsInterface)
        {
            return null;
        }

        // Otherwise the most derived method with the member's name and signature implements it; a method that matched
        // it before the rewrite, more derived than any that matches it after, no longer does.
        TypeDefinition current = type;
        Rendered[] context = self;
        while (true)
        {
            Rendered? looseMatch = null;
            MethodDefinition? looseMethod = null;
            foreach (MethodDefinition method in current.Methods.Where(method => IsCandidate(method, declared) && (method.IsStatic || method.IsVirtual)))
            {
                Rendered actual = Signature(method, context);
                if (actual.Exact == expected.Exact)
                {
                    return null;
                }

                if (actual.Loose == expected.Loose && looseMethod is null)
                {
                    looseMatch = actual;
                    looseMethod = method;
                }
            }

            if (looseMethod is not null)
            {
                return Mismatch(looseMethod, type, declaring, interfaceReference, declared, isOverride: false, expected, looseMatch!.Value);
            }

            if (current.BaseType is not { } baseReference || resolve(baseReference) is not { } baseType || !canInspect(baseType))
            {
                return null;
            }

            context = Arguments(baseReference, context);
            current = baseType;
        }
    }

    private TypeShapeMismatch? FindOverrideMismatch(
        TypeDefinition type,
        Rendered[] self,
        MethodDefinition method,
        Func<TypeReference, TypeDefinition?> resolve,
        Func<TypeDefinition, bool> canInspect)
    {
        Rendered actual = Signature(method, self);
        TypeReference? baseReference = type.BaseType;
        TypeDefinition naming = type;
        Rendered[] context = self;
        while (baseReference is not null && resolve(baseReference) is { } baseType && canInspect(baseType))
        {
            context = Arguments(baseReference, context);
            (MethodDefinition Method, Rendered Signature)[] candidates =
            [
                .. baseType.Methods
                    .Where(candidate => candidate.IsVirtual && IsCandidate(candidate, method))
                    .Select(candidate => (candidate, Signature(candidate, context))),
            ];
            if (candidates.Any(candidate => candidate.Signature.Exact == actual.Exact))
            {
                return null;
            }

            foreach ((MethodDefinition candidate, Rendered expected) in candidates)
            {
                if (expected.Loose == actual.Loose)
                {
                    return Mismatch(method, naming, baseType, baseReference, candidate, isOverride: true, expected, actual);
                }
            }

            baseReference = baseType.BaseType;
            naming = baseType;
        }

        return null;
    }

    private static bool IsCandidate(MethodDefinition method, MethodDefinition declared) =>
        method.Name == declared.Name
        && method.IsStatic == declared.IsStatic
        && method.Parameters.Count == declared.Parameters.Count
        && method.GenericParameters.Count == declared.GenericParameters.Count;

    private TypeShapeMismatch Mismatch(
        MethodDefinition member,
        TypeDefinition namingType,
        TypeDefinition declaringType,
        TypeReference declaringReference,
        MethodDefinition declared,
        bool isOverride,
        Rendered expected,
        Rendered actual)
    {
        var mentioned = new SortedSet<string>(expected.Mentioned, StringComparer.Ordinal);
        mentioned.UnionWith(actual.Mentioned);
        return new TypeShapeMismatch(
            member,
            namingType,
            declaringType,
            declaringReference,
            isOverride,
            declared.Name + expected.Exact,
            member.Name + actual.Exact,
            [.. mentioned],
            DeclaredSignatureMentions: Signature(declared, Plain(declaringType)).Mentioned.Count > 0);
    }

    private bool DeclaresMentioningMember(TypeReference interfaceReference, Func<TypeReference, TypeDefinition?> resolve)
    {
        if (resolve(interfaceReference) is not { } definition)
        {
            return false;
        }

        if (!_declaresMentioningMember.TryGetValue(definition, out bool declares))
        {
            declares = definition.Methods.Any(method => method.IsVirtual && MentionsAny(method));
            _declaresMentioningMember[definition] = declares;
        }

        return declares;
    }

    private bool MentionsAny(MethodReference method) =>
        Mentions(method.ReturnType) || method.Parameters.Any(parameter => Mentions(parameter.ParameterType));

    private bool Mentions(TypeReference type) => type switch
    {
        GenericParameter => false,
        GenericInstanceType generic => Mentions(generic.ElementType) || generic.GenericArguments.Any(Mentions),
        IModifierType modifier => Mentions(modifier.ModifierType) || Mentions(modifier.ElementType),
        FunctionPointerType pointer => Mentions(pointer.ReturnType) || pointer.Parameters.Any(parameter => Mentions(parameter.ParameterType)),
        TypeSpecification specification => Mentions(specification.ElementType),
        _ => _rules.ContainsKey(type.FullName),
    };

    private static Rendered[] Plain(TypeDefinition type) =>
        [.. type.GenericParameters.Select(parameter => Rendered.Plain("!" + parameter.Position))];

    private Rendered[] Arguments(TypeReference reference, Rendered[] context) =>
        reference is GenericInstanceType generic
            ? [.. generic.GenericArguments.Select(argument => Render(argument, context))]
            : [];

    private Rendered Signature(MethodReference method, Rendered[] context)
    {
        var text = new SignatureText();
        if (method.GenericParameters.Count > 0)
        {
            text.Append("`" + method.GenericParameters.Count);
        }

        text.Append("(");
        for (int i = 0; i < method.Parameters.Count; i++)
        {
            if (i > 0)
            {
                text.Append(",");
            }

            Render(method.Parameters[i].ParameterType, context, text);
        }

        text.Append(") : ");
        Render(method.ReturnType, context, text);
        return text.ToRendered();
    }

    private Rendered Render(TypeReference type, Rendered[] context)
    {
        var text = new SignatureText();
        Render(type, context, text);
        return text.ToRendered();
    }

    private void Render(TypeReference type, Rendered[] context, SignatureText text)
    {
        switch (type)
        {
            case GenericParameter { Type: GenericParameterType.Type } parameter when parameter.Position < context.Length:
                text.Append(context[parameter.Position]);
                return;
            case GenericParameter parameter:
                text.Append((parameter.Type == GenericParameterType.Method ? "!!" : "!") + parameter.Position);
                return;
            case GenericInstanceType generic:
                Render(generic.ElementType, context, text);
                text.Append("<");
                for (int i = 0; i < generic.GenericArguments.Count; i++)
                {
                    if (i > 0)
                    {
                        text.Append(",");
                    }

                    Render(generic.GenericArguments[i], context, text);
                }

                text.Append(">");
                return;
            case ByReferenceType byReference:
                Render(byReference.ElementType, context, text);
                text.Append("&");
                return;
            case PointerType pointer:
                Render(pointer.ElementType, context, text);
                text.Append("*");
                return;
            case ArrayType array:
                Render(array.ElementType, context, text);
                text.Append(array.IsVector ? "[]" : "[" + new string(',', array.Rank - 1) + "]");
                return;
            case IModifierType modifier:
                Render(modifier.ElementType, context, text);
                text.Append(modifier is RequiredModifierType ? " modreq(" : " modopt(");
                Render(modifier.ModifierType, context, text);
                text.Append(")");
                return;
            case TypeSpecification specification and not FunctionPointerType:
                Render(specification.ElementType, context, text);
                return;
            default:
                string name = type.FullName;
                text.Append(name, _substitutes.TryGetValue(name, out string? substitute) ? substitute : name);
                if (_rules.ContainsKey(name))
                {
                    text.Mentioned.Add(name);
                }

                return;
        }
    }

    /// <summary>A signature rendered exactly and with each substituted type replaced by its substitute.</summary>
    private readonly record struct Rendered(string Exact, string Loose, IReadOnlySet<string> Mentioned)
    {
        public static Rendered Plain(string text) => new(text, text, new HashSet<string>(StringComparer.Ordinal));
    }

    private sealed class SignatureText
    {
        private readonly StringBuilder _exact = new();
        private readonly StringBuilder _loose = new();

        public HashSet<string> Mentioned { get; } = new(StringComparer.Ordinal);

        public void Append(string text) => Append(text, text);

        public void Append(string exact, string loose)
        {
            _exact.Append(exact);
            _loose.Append(loose);
        }

        public void Append(Rendered rendered)
        {
            Append(rendered.Exact, rendered.Loose);
            Mentioned.UnionWith(rendered.Mentioned);
        }

        public Rendered ToRendered() => new(_exact.ToString(), _loose.ToString(), Mentioned);
    }
}

/// <summary>An implementation or override that no longer matches the member it implements or overrides.</summary>
/// <param name="Member">The implementing or overriding method.</param>
/// <param name="NamingType">
/// The type which names the interface or base type declaring the implemented or overridden member: the implementing
/// type, or for an override found through intermediate base types, the most derived of them which names it.
/// </param>
/// <param name="DeclaringType">The interface or base type declaring the implemented or overridden member.</param>
/// <param name="DeclaringReference">How <paramref name="NamingType"/> names that interface or base type.</param>
/// <param name="IsOverride">Whether <paramref name="Member"/> overrides rather than implements.</param>
/// <param name="Expected">The implemented or overridden member's name and signature, as the implementing type sees it.</param>
/// <param name="Actual">The implementation's or override's name and signature.</param>
/// <param name="MentionedTypes">The substituted and substitute types the two signatures mention.</param>
/// <param name="DeclaredSignatureMentions">
/// Whether the implemented or overridden member's own signature mentions one of them, rather than only through the
/// generic arguments with which <paramref name="NamingType"/> names its declaring type.
/// </param>
internal sealed record TypeShapeMismatch(
    MethodDefinition Member,
    TypeDefinition NamingType,
    TypeDefinition DeclaringType,
    TypeReference DeclaringReference,
    bool IsOverride,
    string Expected,
    string Actual,
    IReadOnlyList<string> MentionedTypes,
    bool DeclaredSignatureMentions)
{
    /// <summary>Gets the implementing or overriding member's name, qualified by its declaring type.</summary>
    public string MemberName => $"{Member.DeclaringType.FullName}::{Member.Name}";

    /// <summary>Describes the mismatch, given why a type keeps its original signatures.</summary>
    /// <param name="unrewritten">
    /// Returns why a type keeps its original signatures (for example that it is excluded from rewriting), or
    /// <see langword="null"/> when it is rewritten or that is not known.
    /// </param>
    /// <returns>A clause completing "which ...".</returns>
    public string Describe(Func<TypeDefinition, string?> unrewritten)
    {
        TypeDefinition implementer = Member.DeclaringType;

        // When the implemented member's own signature mentions no substituted type, the two sides differ through the
        // generic arguments with which a type names the interface or base type, which the rewrite substitutes in a
        // rewritten type's base type and interfaces; the implementer and that type are then on either side of the
        // boundary.
        TypeDefinition other = DeclaredSignatureMentions ? DeclaringType : NamingType;
        string cause = (unrewritten(implementer), unrewritten(other)) switch
        {
            ({ } reason, null) when DeclaredSignatureMentions =>
                $"{reason}, so it keeps the original signature while '{other.FullName}' is rewritten",
            ({ } reason, null) =>
                $"{reason}, so it keeps the original signature while '{other.FullName}' is rewritten and names '{DeclaringReference.FullName}'",
            (null, { } reason) when DeclaredSignatureMentions =>
                $"{reason}, so it keeps the original signature while '{implementer.FullName}' is rewritten",
            (null, { } reason) =>
                $"{reason}, so it names '{DeclaringReference.FullName}' with the original type arguments while " +
                $"'{implementer.FullName}' is rewritten",
            _ => "only one of the two signatures is rewritten",
        };
        return $"{(IsOverride ? "overrides" : "implements")} '{DeclaringType.FullName}::{Expected}' as '{Actual}' after " +
            $"rewriting, because {cause}; the runtime would fail to load '{implementer.FullName}' or bind a different member";
    }
}
