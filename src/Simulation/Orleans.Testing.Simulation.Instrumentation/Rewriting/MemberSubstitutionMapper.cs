using Orleans.Testing.Simulation.Instrumentation.Rules;
using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Rewriting;

/// <summary>
/// A recursive Mono.Cecil reference mapper that projects a set of
/// <see cref="RewriteOperationKind.SubstituteType"/> rules onto whole references - not just the
/// type-reference operands the <see cref="TypeReferenceRewritingPass"/> handles, but the declaring
/// types of method and field references, generic instantiations, by-ref and array element types,
/// field types, and local-variable types. This is what makes it possible to replace a
/// compiler-generated state machine's builder and awaiter types (for example
/// <c>AsyncTaskMethodBuilder&lt;int&gt;</c> and <c>TaskAwaiter&lt;int&gt;</c>) with their controlled
/// counterparts while preserving generic arguments, by-ref-ness, and method signatures.
/// </summary>
/// <remarks>
/// <see cref="MapType"/> returns <see langword="null"/> when nothing changed, so callers can cheaply skip untouched
/// references; <see cref="MapMethod"/> and <see cref="MapField"/> return a <see cref="MemberMapping{TMember}"/> that is
/// unaffected, mapped, or unbindable for a stated reason, which the caller reports as a rewrite error. Substituted
/// declaring types are resolved back to the replacement assembly's definitions and re-imported so the produced
/// references carry a signature that binds by name at run time; when the original declaring type was a generic
/// instance the imported open reference is re-based onto the mapped generic instance. A reference whose declaring
/// type is not substituted keeps it and carries the substituted signature, which binds only where that type's
/// definition is rewritten too: in a type of this module that is not excluded from rewriting (checked after the
/// pass), or in an assembly the closure rewrites with the same rule set (<see cref="RewriteSession.RewrittenAssemblyNames"/>).
/// </remarks>
internal sealed class MemberSubstitutionMapper
{
    private readonly ModuleDefinition _module;
    private readonly Dictionary<string, TypeReference> _byName;
    private readonly Dictionary<string, string> _ruleIdByName;
    private readonly IReadOnlySet<string> _rewrittenAssemblyNames;

    private MemberSubstitutionMapper(
        ModuleDefinition module,
        Dictionary<string, TypeReference> byName,
        Dictionary<string, string> ruleIdByName,
        IReadOnlySet<string> rewrittenAssemblyNames)
    {
        _module = module;
        _byName = byName;
        _ruleIdByName = ruleIdByName;
        _rewrittenAssemblyNames = rewrittenAssemblyNames;
    }

    /// <summary>Gets a value indicating whether any substitution was resolved.</summary>
    public bool IsEmpty => _byName.Count == 0;

    /// <summary>
    /// Builds a mapper by resolving every in-range type substitution to an imported reference. Rules
    /// whose replacement type cannot be resolved are reported through <paramref name="onUnresolved"/>
    /// and omitted (the pass then leaves those references untouched, and the missing-service guard at
    /// run time surfaces the gap).
    /// </summary>
    public static MemberSubstitutionMapper Build(RewriteSession session, Action<RewriteRule, string> onUnresolved)
    {
        var byName = new Dictionary<string, TypeReference>(StringComparer.Ordinal);
        var ruleIdByName = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (KeyValuePair<string, RewriteRule> entry in session.Matcher.TypeSubstitutionRules)
        {
            if (session.Resolver.TryResolveType(session.TargetModule, entry.Value.Replacement, out TypeReference imported, out string? error))
            {
                byName[entry.Key] = imported;
                ruleIdByName[entry.Key] = entry.Value.Id;
            }
            else if (error is not null)
            {
                onUnresolved(entry.Value, error);
            }
        }

        return new MemberSubstitutionMapper(session.TargetModule, byName, ruleIdByName, session.RewrittenAssemblyNames);
    }

    /// <summary>Gets the id of the rule that substitutes <paramref name="normalizedFullName"/>, if any.</summary>
    public string? RuleIdFor(string normalizedFullName) =>
        _ruleIdByName.TryGetValue(normalizedFullName, out string? id) ? id : null;

    /// <summary>Returns the substitute for a substituted type's open full name, or <see langword="null"/>.</summary>
    public TypeReference? OpenSubstituteFor(string normalizedFullName) =>
        _byName.TryGetValue(normalizedFullName, out TypeReference? imported) ? imported : null;

    /// <summary>
    /// Maps a type reference, returning the substituted reference or <see langword="null"/> when the
    /// type (and everything it is composed of) is unaffected.
    /// </summary>
    public TypeReference? MapType(TypeReference type)
    {
        switch (type)
        {
            case GenericInstanceType generic:
                {
                    TypeReference? mappedElement = MapType(generic.ElementType);
                    TypeReference[]? mappedArguments = MapMany(generic.GenericArguments);
                    if (mappedElement is null && mappedArguments is null)
                    {
                        return null;
                    }

                    var result = new GenericInstanceType(mappedElement ?? generic.ElementType);
                    for (int i = 0; i < generic.GenericArguments.Count; i++)
                    {
                        result.GenericArguments.Add(mappedArguments?[i] ?? generic.GenericArguments[i]);
                    }

                    return result;
                }

            case ByReferenceType byReference:
                {
                    TypeReference? mapped = MapType(byReference.ElementType);
                    return mapped is null ? null : new ByReferenceType(mapped);
                }

            case ArrayType array:
                {
                    TypeReference? mapped = MapType(array.ElementType);
                    if (mapped is null)
                    {
                        return null;
                    }

                    return array.IsVector ? new ArrayType(mapped) : new ArrayType(mapped, array.Rank);
                }

            case PointerType pointer:
                {
                    TypeReference? mapped = MapType(pointer.ElementType);
                    return mapped is null ? null : new PointerType(mapped);
                }

            case RequiredModifierType modifier:
                {
                    TypeReference? mappedElement = MapType(modifier.ElementType);
                    TypeReference? mappedModifier = MapType(modifier.ModifierType);
                    return mappedElement is null && mappedModifier is null
                        ? null
                        : new RequiredModifierType(
                            mappedModifier ?? modifier.ModifierType,
                            mappedElement ?? modifier.ElementType);
                }

            case OptionalModifierType modifier:
                {
                    TypeReference? mappedElement = MapType(modifier.ElementType);
                    TypeReference? mappedModifier = MapType(modifier.ModifierType);
                    return mappedElement is null && mappedModifier is null
                        ? null
                        : new OptionalModifierType(
                            mappedModifier ?? modifier.ModifierType,
                            mappedElement ?? modifier.ElementType);
                }

            case PinnedType pinned:
                {
                    TypeReference? mapped = MapType(pinned.ElementType);
                    return mapped is null ? null : new PinnedType(mapped);
                }

            case SentinelType sentinel:
                {
                    TypeReference? mapped = MapType(sentinel.ElementType);
                    return mapped is null ? null : new SentinelType(mapped);
                }

            case GenericParameter:
                return null;

            default:
                return _byName.TryGetValue(type.FullName, out TypeReference? imported) ? imported : null;
        }
    }

    /// <summary>
    /// Maps a method reference whose declaring type, return type, parameter types, or generic arguments are
    /// substituted.
    /// </summary>
    public MemberMapping<MethodReference> MapMethod(MethodReference method)
    {
        if (method is GenericInstanceMethod generic)
        {
            // An element whose own signature cannot be mapped stays unbindable: instantiating the unmapped element
            // with mapped arguments would hide that.
            MemberMapping<MethodReference> element = MapMethod(generic.ElementMethod);
            if (element.Kind == MemberMappingKind.Unbindable)
            {
                return element;
            }

            TypeReference[]? mappedArguments = MapMany(generic.GenericArguments);
            if (element.Kind == MemberMappingKind.Unaffected && mappedArguments is null)
            {
                return MemberMapping<MethodReference>.Unaffected;
            }

            var result = new GenericInstanceMethod(element.Reference ?? generic.ElementMethod);
            for (int i = 0; i < generic.GenericArguments.Count; i++)
            {
                result.GenericArguments.Add(mappedArguments?[i] ?? generic.GenericArguments[i]);
            }

            return MemberMapping<MethodReference>.Mapped(result, element.VerifyAfterPass);
        }

        TypeReference? mappedDeclaring = MapType(method.DeclaringType);
        TypeReference? mappedReturn = MapType(method.ReturnType);
        TypeReference[]? mappedParameters = MapParameters(method.Parameters);
        if (mappedDeclaring is null && mappedReturn is null && mappedParameters is null)
        {
            return MemberMapping<MethodReference>.Unaffected;
        }

        if (!IsSubstituted(method.DeclaringType))
        {
            // The member keeps its declaring type, re-instantiated if a generic argument is substituted.
            MethodReference rebuilt = Rebuild(method, mappedDeclaring ?? method.DeclaringType, mappedReturn, mappedParameters);
            return mappedReturn is null && mappedParameters is null
                ? MemberMapping<MethodReference>.Mapped(rebuilt, verifyAfterPass: false)
                : Admit(method.DeclaringType, rebuilt);
        }

        TypeReference mappedElementType = mappedDeclaring is GenericInstanceType instance
            ? instance.ElementType
            : mappedDeclaring!;

        TypeDefinition? definition;
        try
        {
            definition = mappedElementType.Resolve();
        }
        catch (AssemblyResolutionException exception)
        {
            return MemberMapping<MethodReference>.Unbindable(
                $"has a replacement type '{mappedElementType.FullName}' that could not be resolved: {exception.Message}");
        }

        MethodDefinition? controlled = definition is null ? null : FindMatchingMethod(definition, method);
        if (controlled is null)
        {
            return MemberMapping<MethodReference>.Unbindable(
                $"has no matching member on the replacement type '{mappedElementType.FullName}'");
        }

        MethodReference importedOpen = _module.ImportReference(controlled);
        return MemberMapping<MethodReference>.Mapped(
            mappedDeclaring is GenericInstanceType genericDeclaring ? Rebase(importedOpen, genericDeclaring) : importedOpen,
            verifyAfterPass: false);
    }

    /// <summary>Maps a field reference whose declaring type or field type is substituted.</summary>
    public MemberMapping<FieldReference> MapField(FieldReference field)
    {
        TypeReference? mappedFieldType = MapType(field.FieldType);
        TypeReference? mappedDeclaring = MapType(field.DeclaringType);
        if (mappedFieldType is null && mappedDeclaring is null)
        {
            return MemberMapping<FieldReference>.Unaffected;
        }

        var mapped = new FieldReference(
            field.Name,
            mappedFieldType ?? field.FieldType,
            mappedDeclaring ?? field.DeclaringType);
        return mappedFieldType is null || IsSubstituted(field.DeclaringType)
            ? MemberMapping<FieldReference>.Mapped(mapped, verifyAfterPass: false)
            : Admit(field.DeclaringType, mapped);
    }

    private bool IsSubstituted(TypeReference type) =>
        _byName.ContainsKey(CecilNames.NormalizedTypeFullName(type));

    // A rebuilt reference keeps its declaring type but carries a substituted signature, so it binds only where the
    // declaring type's definition is rewritten by the same mapping: in this module, unless the type is excluded from
    // rewriting, which is known once the pass has visited every type; or in another assembly the closure rewrites
    // with the same rule set. Any other assembly - the framework, a replacement assembly, or one the closure excludes
    // or that a direct engine caller did not name - keeps its original signatures, so the reference is unbindable
    // and the pass fails the rewrite rather than emit a member reference that throws MissingMethodException or
    // MissingFieldException when the runtime binds it. Array methods are provided by the runtime for every element
    // type, so they always bind.
    private MemberMapping<TMember> Admit<TMember>(TypeReference declaringType, TMember rebuilt)
        where TMember : MemberReference
    {
        if (declaringType is ArrayType)
        {
            return MemberMapping<TMember>.Mapped(rebuilt, verifyAfterPass: false);
        }

        return declaringType.Scope switch
        {
            ModuleDefinition module when ReferenceEquals(module, _module) =>
                MemberMapping<TMember>.Mapped(rebuilt, verifyAfterPass: true),
            AssemblyNameReference assembly when _rewrittenAssemblyNames.Contains(assembly.Name) =>
                MemberMapping<TMember>.Mapped(rebuilt, verifyAfterPass: false),
            AssemblyNameReference assembly => MemberMapping<TMember>.Unbindable(
                $"is declared by '{CecilNames.NormalizedTypeFullName(declaringType)}' in '{assembly.Name}', which is not " +
                "rewritten in this closure, so the member keeps its original signature"),
            _ => MemberMapping<TMember>.Unbindable(
                $"is declared by '{CecilNames.NormalizedTypeFullName(declaringType)}' in module '{declaringType.Scope?.Name}', " +
                "which this rewrite cannot check binds after rewriting"),
        };
    }

    /// <summary>
    /// Builds a constructor reference for the controlled awaitable/awaiter produced by an awaiter-source
    /// call (<c>Task.GetAwaiter()</c>, <c>Task.ConfigureAwait(bool)</c>, or
    /// <c>Task.ConfigureAwait(ConfigureAwaitOptions)</c>). The BCL return type
    /// (<c>TaskAwaiter&lt;!0&gt;</c> / <c>ConfiguredTaskAwaitable&lt;!0&gt;</c>) parameterises over the
    /// awaited task's own generic parameter, so its concrete argument is taken from the receiver's
    /// declaring generic instance rather than mapped directly (mapping would carry the unbound <c>!0</c>
    /// into a non-generic state machine and produce a dangling reference).
    /// </summary>
    public MethodReference? BuildAwaiterSourceConstructor(MethodReference source)
    {
        TypeReference returnType = source.ReturnType;
        string openName = returnType is GenericInstanceType generic ? generic.ElementType.FullName : returnType.FullName;
        if (!_byName.TryGetValue(openName, out TypeReference? openSubstitute))
        {
            return null;
        }

        TypeReference mappedResult;
        if (returnType is GenericInstanceType)
        {
            if (source.DeclaringType is not GenericInstanceType declaring || declaring.GenericArguments.Count == 0)
            {
                return null;
            }

            var instance = new GenericInstanceType(openSubstitute);
            instance.GenericArguments.Add(declaring.GenericArguments[0]);
            mappedResult = instance;
        }
        else
        {
            mappedResult = openSubstitute;
        }

        return BuildConstructor(mappedResult, source);
    }

    /// <summary>
    /// Builds a reference to the constructor on a mapped value type that consumes an awaiter-source call's
    /// stack - the receiver followed by the call's arguments - so the call becomes a <c>newobj</c> that
    /// pushes the controlled value. The constructor is selected by parameter types, so each overload (for
    /// example <c>ConfigureAwait(bool)</c> and <c>ConfigureAwait(ConfigureAwaitOptions)</c>) binds to the
    /// constructor taking exactly its arguments, and an overload without one is left unmapped.
    /// </summary>
    private MethodReference? BuildConstructor(TypeReference mappedType, MethodReference source)
    {
        TypeReference elementType = mappedType is GenericInstanceType instance ? instance.ElementType : mappedType;
        TypeDefinition? definition = elementType.Resolve();
        TypeDefinition? receiverDefinition = TryResolve(source.DeclaringType);
        if (definition is null || receiverDefinition is null)
        {
            return null;
        }

        bool receiverIsValueType = receiverDefinition.IsValueType;
        MethodDefinition? constructor = definition.Methods
            .FirstOrDefault(m => m.IsConstructor && !m.IsStatic && ConsumesAwaiterSourceStack(m, source, receiverIsValueType));
        if (constructor is null)
        {
            return null;
        }

        MethodReference importedOpen = _module.ImportReference(constructor);
        return mappedType is GenericInstanceType genericType ? Rebase(importedOpen, genericType) : importedOpen;
    }

    private static TypeDefinition? TryResolve(TypeReference type)
    {
        try
        {
            return type.Resolve();
        }
        catch (AssemblyResolutionException)
        {
            return null;
        }
    }

    private static bool ConsumesAwaiterSourceStack(
        MethodDefinition constructor,
        MethodReference source,
        bool receiverIsValueType)
    {
        if (constructor.Parameters.Count != source.Parameters.Count + 1)
        {
            return false;
        }

        // A Task receiver is pushed as an object reference, which binds only to a by-value parameter; a
        // ValueTask receiver is pushed as its address, which binds only to a by-reference (`in`) parameter.
        TypeReference parameter = constructor.Parameters[0].ParameterType;
        if ((parameter is ByReferenceType) != receiverIsValueType)
        {
            return false;
        }

        TypeReference receiver = parameter is ByReferenceType byReference ? byReference.ElementType : parameter;
        if (!string.Equals(
                CecilNames.NormalizedTypeFullName(receiver),
                CecilNames.NormalizedTypeFullName(source.DeclaringType),
                StringComparison.Ordinal)
            || !TakesOwnGenericParameters(receiver, constructor.DeclaringType, source.DeclaringType))
        {
            return false;
        }

        for (int i = 0; i < source.Parameters.Count; i++)
        {
            if (!string.Equals(
                    CecilNames.ParameterFullName(constructor.Parameters[i + 1].ParameterType),
                    CecilNames.ParameterFullName(source.Parameters[i].ParameterType),
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The constructor is rebased onto the controlled type instantiated with the receiver's generic
    /// arguments, so its receiver parameter must take exactly that type's own generic parameters, in order
    /// (<c>Task&lt;TResult&gt;</c> on <c>ControlledTaskAwaiter&lt;TResult&gt;</c>). Any other instantiation
    /// would bind a receiver of a different type.
    /// </summary>
    private static bool TakesOwnGenericParameters(
        TypeReference receiverParameter,
        TypeDefinition constructorType,
        TypeReference receiver)
    {
        int arity = receiver is GenericInstanceType receiverInstance ? receiverInstance.GenericArguments.Count : 0;
        if (constructorType.GenericParameters.Count != arity)
        {
            return false;
        }

        if (receiverParameter is not GenericInstanceType parameterInstance)
        {
            return arity == 0;
        }

        if (parameterInstance.GenericArguments.Count != arity)
        {
            return false;
        }

        for (int i = 0; i < arity; i++)
        {
            if (parameterInstance.GenericArguments[i] is not GenericParameter { Type: GenericParameterType.Type } argument
                || argument.Position != i)
            {
                return false;
            }
        }

        return true;
    }

    private MethodDefinition? FindMatchingMethod(TypeDefinition definition, MethodReference original)
    {
        MethodReference elementOriginal = original is GenericInstanceMethod generic ? generic.ElementMethod : original;

        MethodDefinition? arityMatch = null;
        foreach (MethodDefinition candidate in definition.Methods)
        {
            if (candidate.Name != elementOriginal.Name
                || candidate.HasThis != elementOriginal.HasThis
                || candidate.Parameters.Count != elementOriginal.Parameters.Count
                || candidate.GenericParameters.Count != elementOriginal.GenericParameters.Count)
            {
                continue;
            }

            TypeReference expectedReturn = MapType(elementOriginal.ReturnType) ?? elementOriginal.ReturnType;
            if (!SameTypeShape(candidate.ReturnType, expectedReturn))
            {
                continue;
            }

            bool parametersMatch = true;
            for (int i = 0; i < candidate.Parameters.Count; i++)
            {
                ParameterDefinition expectedParameter = elementOriginal.Parameters[i];
                ParameterDefinition candidateParameter = candidate.Parameters[i];
                TypeReference expectedType = MapType(expectedParameter.ParameterType) ?? expectedParameter.ParameterType;
                if (!SameTypeShape(candidateParameter.ParameterType, expectedType))
                {
                    parametersMatch = false;
                    break;
                }
            }

            if (parametersMatch)
            {
                arityMatch ??= candidate;

                // When the substitute declares several same-arity overloads (for example SpinWait's
                // SpinUntil(Func<bool>, int) vs SpinUntil(Func<bool>, TimeSpan)), matching on arity alone
                // would bind every call site to the first overload and emit invalid IL. Disambiguate by
                // parameter type in that case.
                if (ParametersMatch(candidate, elementOriginal))
                {
                    return candidate;
                }
            }
        }

        // A single same-arity candidate is unambiguous (and parameter types may legitimately differ, e.g.
        // a parameter of the substituted type maps to the controlled type). Fall back to the first match.
        return arityMatch;
    }

    private bool ParametersMatch(MethodDefinition candidate, MethodReference original)
    {
        for (int i = 0; i < original.Parameters.Count; i++)
        {
            TypeReference originalParameter = original.Parameters[i].ParameterType;
            string expected = (MapType(originalParameter) ?? originalParameter).FullName;
            if (!string.Equals(candidate.Parameters[i].ParameterType.FullName, expected, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameTypeShape(TypeReference left, TypeReference right) =>
        TypeReferenceStructure.AreEquivalent(left, null, right, null);

    private static MethodReference Rebase(MethodReference importedOpen, GenericInstanceType declaringType)
    {
        var rebased = new MethodReference(importedOpen.Name, importedOpen.ReturnType, declaringType)
        {
            HasThis = importedOpen.HasThis,
            ExplicitThis = importedOpen.ExplicitThis,
            CallingConvention = importedOpen.CallingConvention,
        };

        foreach (GenericParameter parameter in importedOpen.GenericParameters)
        {
            rebased.GenericParameters.Add(new GenericParameter(parameter.Name, rebased));
        }

        foreach (ParameterDefinition parameter in importedOpen.Parameters)
        {
            rebased.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, parameter.ParameterType));
        }

        return rebased;
    }

    private static MethodReference Rebuild(
        MethodReference original,
        TypeReference declaringType,
        TypeReference? mappedReturn,
        TypeReference[]? mappedParameters)
    {
        var rebuilt = new MethodReference(
            original.Name,
            mappedReturn ?? original.ReturnType,
            declaringType)
        {
            HasThis = original.HasThis,
            ExplicitThis = original.ExplicitThis,
            CallingConvention = original.CallingConvention,
        };

        var genericParameters = new Dictionary<GenericParameter, GenericParameter>();
        foreach (GenericParameter parameter in original.GenericParameters)
        {
            var replacement = new GenericParameter(parameter.Name, rebuilt);
            rebuilt.GenericParameters.Add(replacement);
            genericParameters.Add(parameter, replacement);
        }

        rebuilt.ReturnType = RebindMethodGenericParameters(rebuilt.ReturnType, genericParameters);
        for (int i = 0; i < original.Parameters.Count; i++)
        {
            ParameterDefinition parameter = original.Parameters[i];
            rebuilt.Parameters.Add(new ParameterDefinition(
                parameter.Name,
                parameter.Attributes,
                RebindMethodGenericParameters(
                    mappedParameters?[i] ?? parameter.ParameterType,
                    genericParameters)));
        }

        return rebuilt;
    }

    private static TypeReference RebindMethodGenericParameters(
        TypeReference type,
        IReadOnlyDictionary<GenericParameter, GenericParameter> genericParameters) =>
        type switch
        {
            GenericParameter parameter when genericParameters.TryGetValue(parameter, out GenericParameter? replacement) =>
                replacement,
            GenericInstanceType generic => RebindGenericInstance(generic, genericParameters),
            ByReferenceType byReference => new ByReferenceType(
                RebindMethodGenericParameters(byReference.ElementType, genericParameters)),
            ArrayType array when array.IsVector => new ArrayType(
                RebindMethodGenericParameters(array.ElementType, genericParameters)),
            ArrayType array => new ArrayType(
                RebindMethodGenericParameters(array.ElementType, genericParameters),
                array.Rank),
            PointerType pointer => new PointerType(
                RebindMethodGenericParameters(pointer.ElementType, genericParameters)),
            RequiredModifierType modifier => new RequiredModifierType(
                RebindMethodGenericParameters(modifier.ModifierType, genericParameters),
                RebindMethodGenericParameters(modifier.ElementType, genericParameters)),
            OptionalModifierType modifier => new OptionalModifierType(
                RebindMethodGenericParameters(modifier.ModifierType, genericParameters),
                RebindMethodGenericParameters(modifier.ElementType, genericParameters)),
            PinnedType pinned => new PinnedType(
                RebindMethodGenericParameters(pinned.ElementType, genericParameters)),
            SentinelType sentinel => new SentinelType(
                RebindMethodGenericParameters(sentinel.ElementType, genericParameters)),
            _ => type,
        };

    private static GenericInstanceType RebindGenericInstance(
        GenericInstanceType generic,
        IReadOnlyDictionary<GenericParameter, GenericParameter> genericParameters)
    {
        var result = new GenericInstanceType(
            RebindMethodGenericParameters(generic.ElementType, genericParameters));
        foreach (TypeReference argument in generic.GenericArguments)
        {
            result.GenericArguments.Add(RebindMethodGenericParameters(argument, genericParameters));
        }

        return result;
    }

    private TypeReference[]? MapParameters(Mono.Collections.Generic.Collection<ParameterDefinition> parameters)
    {
        TypeReference[]? mapped = null;
        for (int i = 0; i < parameters.Count; i++)
        {
            TypeReference? result = MapType(parameters[i].ParameterType);
            if (result is not null)
            {
                mapped ??= new TypeReference[parameters.Count];
                mapped[i] = result;
            }
        }

        return mapped;
    }

    private TypeReference[]? MapMany(Mono.Collections.Generic.Collection<TypeReference> types)
    {
        TypeReference[]? mapped = null;
        for (int i = 0; i < types.Count; i++)
        {
            TypeReference? result = MapType(types[i]);
            if (result is not null)
            {
                mapped ??= new TypeReference[types.Count];
                mapped[i] = result;
            }
        }

        return mapped;
    }
}
