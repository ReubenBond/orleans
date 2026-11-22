using Orleans.Testing.Simulation.Instrumentation.Diagnostics;
using Orleans.Testing.Simulation.Instrumentation.Manifest;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Runtime.Policy;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Orleans.Testing.Simulation.Instrumentation.Rewriting;

/// <summary>
/// The member-aware half of <see cref="RewriteOperationKind.SubstituteType"/>: where
/// <see cref="TypeReferenceRewritingPass"/> only replaces type-reference <em>operands</em>, this pass
/// projects the same substitutions onto method signatures, field types, local-variable types, and the declaring types of
/// method and field references, and rewrites the awaiter-source calls the C# compiler emits
/// (<c>task.GetAwaiter()</c>, <c>task.ConfigureAwait(bool)</c>, and
/// <c>task.ConfigureAwait(ConfigureAwaitOptions)</c>) into a <c>newobj</c> of the controlled
/// awaitable/awaiter. Together these let a compiler-generated <c>async</c> state machine be
/// retargeted from the BCL builder/awaiter types onto Clockwork's controlled equivalents, so every
/// continuation is scheduled through the simulation coordinator instead of the thread pool.
/// </summary>
/// <remarks>
/// The pass is a no-op unless the rule set declares type substitutions (so ordinary BCL rule sets are
/// unaffected), and it only touches references composed from substituted types, so non-async code in a
/// rewritten assembly is left byte-for-byte unchanged. It fails closed, reporting
/// <see cref="RewriteDiagnosticIds.UnresolvedReplacement"/> instead of emitting a member reference that does not bind:
/// <list type="bullet">
/// <item><description>a substituted signature whose declaring type keeps its original one: a member of an assembly
/// the closure does not rewrite (the framework, a replacement assembly, or an excluded assembly), or of a type of this
/// module excluded from rewriting;</description></item>
/// <item><description>a field of an excluded type whose type is substituted, which rewritten code would otherwise use
/// as its substitute;</description></item>
/// <item><description>a reference from an excluded type to a member of this module whose signature the pass
/// rewrote;</description></item>
/// <item><description>a mention, by an excluded type, of a type of this module whose base type, interfaces, or generic
/// parameter constraints the pass rewrote, of a type deriving from one, or of a method whose generic parameter
/// constraints it rewrote: the excluded code keeps the original shape, so it could use the type through a base type or
/// interface it no longer has;</description></item>
/// <item><description>conversely, a mention, by a rewritten type, of a type that keeps a base type, interface, or generic
/// parameter constraint composed from a substituted type - an excluded type, a type of an assembly the closure does not
/// rewrite, or a type deriving from one - or of a method of such a type that keeps such a constraint: the rewritten
/// code sees that part composed from the substitute;</description></item>
/// <item><description>an interface implementation or override whose signature the pass rewrote on one side only: between
/// a rewritten type and an excluded one or a type of an assembly the closure does not rewrite, including where one of
/// them names the implemented interface or base type with a substituted generic argument, which the pass substitutes in
/// a rewritten type's base type and interfaces only;</description></item>
/// <item><description>an explicit implementation or override whose declaration names a member that does not bind with
/// the substituted signature.</description></item>
/// </list>
/// The closure runner checks references, implementations, overrides, and reshaped types between assemblies once every
/// assembly is staged.
/// </remarks>
internal sealed class MemberSubstitutionRewritingPass : RewritePass
{
    private static readonly HashSet<string> AwaiterSourceReceivers = new(StringComparer.Ordinal)
    {
        "System.Threading.Tasks.Task",
        "System.Threading.Tasks.Task`1",
        "System.Threading.Tasks.ValueTask",
        "System.Threading.Tasks.ValueTask`1",
    };

    private MemberSubstitutionMapper? _mapper;
    private readonly HashSet<string> _reportedApplicationFailures = new(StringComparer.Ordinal);
    private readonly List<PendingBinding> _pendingBindings = [];
    private readonly Dictionary<MemberReference, RewriteRule[]> _rewrittenSignatures = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IMemberDefinition, TypeReshape> _reshapes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, TypeReshape?> _keptShapes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TypeReshape?> _keptMethodShapes = new(StringComparer.Ordinal);

    public MemberSubstitutionRewritingPass(RewriteSession session)
        : base(session)
    {
    }

    private MemberSubstitutionMapper Mapper => _mapper ??= BuildMapper();

    private bool IsActive => Session.Matcher.HasTypeSubstitutions && !Mapper.IsEmpty;

    /// <inheritdoc/>
    internal override void VisitType(TypeDefinition type)
    {
        base.VisitType(type);

        if (!IsActive)
        {
            return;
        }

        RecordReshape(type, TypeReshapes.Find(type, IsMapped));
        MapBaseTypes(type);
        MapGenericConstraints(type.GenericParameters);

        foreach (FieldDefinition field in type.Fields)
        {
            TypeReference? mapped = Mapper.MapType(field.FieldType);
            if (mapped is not null)
            {
                RecordRewrittenSignature(field, [field.FieldType]);
                RecordType(mapped, field.FieldType);
                field.FieldType = mapped;
            }
        }

        foreach (PropertyDefinition property in type.Properties)
        {
            TypeReference? mappedProperty = Mapper.MapType(property.PropertyType);
            if (mappedProperty is not null)
            {
                property.PropertyType = mappedProperty;
            }

            foreach (ParameterDefinition parameter in property.Parameters)
            {
                TypeReference? mappedParameter = Mapper.MapType(parameter.ParameterType);
                if (mappedParameter is not null)
                {
                    parameter.ParameterType = mappedParameter;
                }
            }
        }

        // A substituted type can occur directly or inside a generic method signature, such as
        // Action<Barrier> in a compiler-generated callback. Leaving these signatures unchanged would
        // produce incompatible closure and public API references after the whole-type substitution.
        foreach (MethodDefinition method in type.Methods)
        {
            RecordReshape(method, TypeReshapes.Find(method, IsMapped));
            MapGenericConstraints(method.GenericParameters);
            TypeReference? mappedReturn = Mapper.MapType(method.ReturnType);
            TypeReference?[] mappedParameters = [.. method.Parameters.Select(parameter => Mapper.MapType(parameter.ParameterType))];
            if (mappedReturn is null && mappedParameters.All(mapped => mapped is null))
            {
                continue;
            }

            RecordRewrittenSignature(method, [method.ReturnType, .. method.Parameters.Select(parameter => parameter.ParameterType)]);
            if (mappedReturn is not null)
            {
                method.ReturnType = mappedReturn;
            }

            for (int i = 0; i < mappedParameters.Length; i++)
            {
                if (mappedParameters[i] is TypeReference mappedParameter)
                {
                    method.Parameters[i].ParameterType = mappedParameter;
                }
            }
        }

        // An explicit implementation or override names the member it implements by signature, so it is retargeted
        // onto that member's rewritten signature, and onto the interface or base type as this type now names it (such
        // as IEnumerable<Barrier>.GetEnumerator), as a call to it would be. A definition of this module is rewritten in
        // place with its declaring type. Whether each implementation still matches is checked once every type is
        // visited.
        foreach (MethodDefinition method in type.Methods.Where(method => method.HasOverrides))
        {
            for (int i = 0; i < method.Overrides.Count; i++)
            {
                MethodReference implemented = method.Overrides[i];
                if (implemented is MethodDefinition)
                {
                    continue;
                }

                MemberMapping<MethodReference> mapping = Mapper.MapMethod(implemented);
                if (mapping.Kind == MemberMappingKind.Mapped)
                {
                    method.Overrides[i] = mapping.Reference!;

                    // A member of a generic instance of this module's type binds only if that type is rewritten too.
                    if (mapping.VerifyAfterPass)
                    {
                        _pendingBindings.Add(new PendingBinding(
                            MemberRules(implemented),
                            implemented.FullName,
                            mapping.Reference!,
                            CecilNames.FullyQualifiedMethodName(method),
                            Offset: -1));
                    }
                }
                else if (mapping.Kind == MemberMappingKind.Unbindable)
                {
                    ReportUnappliedMemberSubstitution(
                        MemberRules(implemented),
                        implemented.FullName,
                        CecilNames.FullyQualifiedMethodName(method),
                        offset: -1,
                        mapping.Reason!);
                }
            }
        }
    }

    /// <inheritdoc/>
    protected override void VisitMethodBody(MethodBody body)
    {
        if (!IsActive)
        {
            return;
        }

        foreach (VariableDefinition variable in body.Variables)
        {
            TypeReference? mapped = Mapper.MapType(variable.VariableType);
            if (mapped is not null)
            {
                variable.VariableType = mapped;
                IsMethodBodyModified = true;
            }
        }
    }

    /// <inheritdoc/>
    protected override Instruction VisitInstruction(Instruction instruction)
    {
        if (!IsActive)
        {
            return instruction;
        }

        switch (instruction.Operand)
        {
            case MethodReference method:
                return VisitMethodOperand(instruction, method);

            case FieldDefinition definition:
                {
                    // A field of this module is rewritten in place when its declaring type is visited, so the operand
                    // is kept; a field of a type excluded from rewriting keeps its type, which this rewritten code
                    // would use as the substitute, so that is checked once every type has been visited.
                    if (Mapper.MapType(definition.FieldType) is not null)
                    {
                        _pendingBindings.Add(PendingFor(definition, definition, instruction));
                    }

                    return instruction;
                }

            case FieldReference field:
                {
                    MemberMapping<FieldReference> mapping = Mapper.MapField(field);
                    if (mapping.Kind == MemberMappingKind.Mapped)
                    {
                        FieldReference mapped = mapping.Reference!;
                        instruction.Operand = mapped;
                        IsMethodBodyModified = true;
                        RecordType(mapped.FieldType, field.FieldType, instruction);
                        TrackBinding(field, mapping, instruction);
                    }
                    else if (mapping.Kind == MemberMappingKind.Unbindable)
                    {
                        ReportUnappliedMemberSubstitution(field, instruction, mapping.Reason!);
                    }

                    return instruction;
                }

            // TypeReferenceRewritingPass substitutes operands whose own type is substituted; this maps the
            // remaining operands composed from one, such as the constrained. List<Barrier>.Enumerator prefix of a
            // foreach, which must name the type of the mapped local it constrains.
            case TypeReference type:
                {
                    TypeReference? mapped = Mapper.MapType(type);
                    if (mapped is not null)
                    {
                        instruction.Operand = mapped;
                        IsMethodBodyModified = true;
                        RecordType(mapped, type, instruction);
                    }

                    return instruction;
                }

            default:
                return instruction;
        }
    }

    private Instruction VisitMethodOperand(Instruction instruction, MethodReference method)
    {
        // The compiler produces an awaiter from task.GetAwaiter() / task.ConfigureAwait(bool) /
        // task.ConfigureAwait(ConfigureAwaitOptions). The controlled awaiter/awaitable is constructed
        // instead, through the constructor whose parameter types are the receiver followed by the call's
        // own, so it consumes the identical stack and pushes the controlled value, and a one-for-one
        // instruction swap to newobj keeps the surrounding IL valid.
        if ((instruction.OpCode.Code is Code.Call or Code.Callvirt)
            && (method.Name is "GetAwaiter" or "ConfigureAwait")
            && AwaiterSourceReceivers.Contains(CecilNames.NormalizedTypeFullName(method.DeclaringType)))
        {
            return TryBuildAwaiterSource(instruction, method) ?? instruction;
        }

        MemberMapping<MethodReference> mapping = Mapper.MapMethod(method);
        if (mapping.Kind == MemberMappingKind.Mapped)
        {
            RecordMethod(method, instruction);
            instruction.Operand = mapping.Reference!;
            IsMethodBodyModified = true;
            TrackBinding(method, mapping, instruction);
        }
        else if (mapping.Kind == MemberMappingKind.Unbindable)
        {
            ReportUnappliedMemberSubstitution(method, instruction, mapping.Reason!);
        }

        return instruction;
    }

    /// <inheritdoc/>
    internal override void CompleteVisit()
    {
        base.CompleteVisit();

        // Every type of the module has now been visited, so every signature the pass rewrites is rewritten: a
        // reference into a type excluded from rewriting, or from one into a rewritten signature, no longer binds.
        foreach (PendingBinding binding in _pendingBindings)
        {
            if (BindingFailure(binding.Mapped) is string failure)
            {
                ReportUnappliedMemberSubstitution(binding.Rules, binding.Original, binding.Method, binding.Offset, failure);
            }
        }

        _pendingBindings.Clear();
        if (IsActive)
        {
            CheckExcludedTypes();
            CheckTypeShapes();
            CheckRewrittenTypes();
        }
    }

    // A mapped reference that keeps a declaring type of this module binds only if that type was rewritten too.
    private string? BindingFailure(MemberReference mapped)
    {
        MemberReference element = mapped is GenericInstanceMethod generic ? generic.ElementMethod : mapped;
        try
        {
            TypeDefinition? declaring = element.DeclaringType.Resolve();
            if (declaring is not null && Session.IsExcluded(declaring))
            {
                return element is FieldDefinition field
                    ? $"is declared by '{declaring.FullName}', which is excluded from rewriting, so it keeps type " +
                        $"'{field.FieldType.FullName}' while rewritten code uses it as '{Mapper.MapType(field.FieldType)!.FullName}'"
                    : $"maps to '{mapped.FullName}', but its declaring type '{declaring.FullName}' is excluded from " +
                        "rewriting and keeps the original signature";
            }

            return element is IMemberDefinition || element.Resolve() is not null
                ? null
                : $"maps to '{mapped.FullName}', which does not bind to a member of '{declaring?.FullName}'";
        }
        catch (AssemblyResolutionException exception)
        {
            return $"maps to '{mapped.FullName}', which could not be resolved to check that it binds: {exception.Message}";
        }
    }

    // Excluded types are not visited, so their references keep the original signatures. A reference to a member of
    // this module whose signature the pass rewrote therefore no longer binds by signature, or, through a definition
    // token, binds to the rewritten signature with arguments of the original types. Nor do they keep a type's original
    // base type, interfaces, or generic constraints, so one that mentions a type or method the pass reshaped fails too.
    private void CheckExcludedTypes()
    {
        foreach (TypeDefinition type in Module!.GetTypes().Where(Session.IsExcluded))
        {
            foreach (MethodDefinition method in type.Methods.Where(method => method.HasBody))
            {
                foreach (Instruction instruction in method.Body.Instructions)
                {
                    if (instruction.Operand is MemberReference member and (MethodReference or FieldReference)
                        && RewrittenSignatureRules(member) is { Length: > 0 } rules)
                    {
                        ReportUnappliedMemberSubstitution(
                            rules,
                            $"{CecilNames.NormalizedTypeFullName(member.DeclaringType)}::{member.Name}",
                            CecilNames.FullyQualifiedMethodName(method),
                            instruction.Offset,
                            $"has a rewritten signature, but '{type.FullName}' is excluded from rewriting and still uses the original one");
                    }
                }
            }

            foreach (ReshapedReference reference in TypeReshapes.FindReferences(type, ReshapeOf, MethodReshapeOf)
                .DistinctBy(reference => reference.Mentioned, StringComparer.Ordinal))
            {
                ReportReshapedReference(type, reference);
            }
        }
    }

    // A rewritten type of this module whose shape the pass rewrote, or which derives from one, is reshaped.
    private TypeReshape? ReshapeOf(TypeReference type)
    {
        for (TypeDefinition? current = type as TypeDefinition;
            current is not null && !Session.IsExcluded(current);
            current = current.BaseType?.GetElementType() as TypeDefinition)
        {
            if (_reshapes.TryGetValue(current, out TypeReshape? reshape))
            {
                return reshape;
            }
        }

        return null;
    }

    private TypeReshape? MethodReshapeOf(MethodReference method)
    {
        MethodReference element = method is GenericInstanceMethod generic ? generic.ElementMethod : method;
        MethodDefinition? definition = element as MethodDefinition;
        if (definition is null && element.DeclaringType.GetElementType() is TypeDefinition)
        {
            try
            {
                definition = element.Resolve();
            }
            catch (AssemblyResolutionException)
            {
                return null;
            }
        }

        return definition is not null && _reshapes.TryGetValue(definition, out TypeReshape? reshape) ? reshape : null;
    }

    private void ReportReshapedReference(TypeDefinition excluded, ReshapedReference reference)
    {
        foreach (RewriteRule rule in MatchingRules([reference.Reshape.Part]))
        {
            if (!_reportedApplicationFailures.Add($"{rule.Id}|{reference.Containing}|{reference.Offset}"))
            {
                continue;
            }

            Session.AddDiagnostic(RewriteDiagnostic.Error(
                RewriteDiagnosticIds.UnresolvedReplacement,
                $"Member-aware substitution rule '{rule.Id}' substitutes '{reference.Reshape.Part.FullName}' in " +
                $"{reference.Reshape.Describe(reference.Mentioned)}, but '{excluded.FullName}' is excluded from rewriting " +
                $"and still uses '{reference.Mentioned}' as declared, through a base type, interface, or constraint it no " +
                "longer has.",
                reference.Containing,
                reference.Offset));
            Session.AddUnresolvedReference(rule.Replacement.ToCanonicalString());
        }
    }

    private bool IsMapped(TypeReference type) => Mapper.MapType(type) is not null;

    // Rewritten code sees a base type, interface, or constraint composed from a substituted type as composed from its
    // substitute, so it cannot use a type that keeps the original one: a type excluded from rewriting, of an assembly
    // the closure does not rewrite, or deriving from one. Whether a type of another assembly the closure rewrites is
    // excluded there is known once the closure is staged, so the closure runner checks those.
    private void CheckRewrittenTypes()
    {
        foreach (TypeDefinition type in Module!.GetTypes().Where(type => !Session.IsExcluded(type)))
        {
            foreach (ReshapedReference reference in TypeReshapes.FindReferences(type, KeptShapeOf, KeptMethodShapeOf)
                .DistinctBy(reference => reference.Mentioned, StringComparer.Ordinal))
            {
                ReportKeptShapeReference(type, reference);
            }
        }
    }

    private TypeReshape? KeptShapeOf(TypeReference type)
    {
        string key = $"{type.Scope?.Name}|{type.FullName}";
        if (!_keptShapes.TryGetValue(key, out TypeReshape? kept))
        {
            TypeDefinition? definition = type.GetElementType() as TypeDefinition
                ?? (type.Scope is AssemblyNameReference ? TryResolve(type) : null);
            kept = definition is null ? null : KeptShape(definition);
            _keptShapes[key] = kept;
        }

        return kept;
    }

    // The part composed from a substituted type that a type which is not rewritten keeps, in its own shape or that of
    // a base type it derives from through types which are not rewritten either.
    private TypeReshape? KeptShape(TypeDefinition definition)
    {
        if (!IsFinal(definition) || Unrewritten(definition) is not string cause)
        {
            return null;
        }

        return TypeReshapes.Find(definition, NamesSubstitutedType) is { } own
            ? own with { Cause = cause }
            : definition.BaseType is { } baseType ? KeptShapeOf(baseType) : null;
    }

    private TypeReshape? KeptMethodShapeOf(MethodReference method)
    {
        MethodReference element = method is GenericInstanceMethod generic ? generic.ElementMethod : method;
        if (!element.HasGenericParameters)
        {
            return null;
        }

        string key = element.FullName;
        if (!_keptMethodShapes.TryGetValue(key, out TypeReshape? kept))
        {
            MethodDefinition? definition = element as MethodDefinition;
            if (definition is null)
            {
                try
                {
                    definition = element.Resolve();
                }
                catch (AssemblyResolutionException)
                {
                    definition = null;
                }
            }

            kept = definition is not null
                && IsFinal(definition.DeclaringType)
                && Unrewritten(definition.DeclaringType) is string cause
                && TypeReshapes.Find(definition, NamesSubstitutedType) is { } constraint
                    ? constraint with { Cause = cause }
                    : null;
            _keptMethodShapes[key] = kept;
        }

        return kept;
    }

    private bool NamesSubstitutedType(TypeReference type) => Session.Matcher.GetMatchingTypeRules(type).Count > 0;

    private void ReportKeptShapeReference(TypeDefinition type, ReshapedReference reference)
    {
        foreach (RewriteRule rule in MatchingRules([reference.Reshape.Part]))
        {
            if (!_reportedApplicationFailures.Add($"{rule.Id}|{reference.Containing}|{reference.Offset}"))
            {
                continue;
            }

            Session.AddDiagnostic(RewriteDiagnostic.Error(
                RewriteDiagnosticIds.UnresolvedReplacement,
                $"Member-aware substitution rule '{rule.Id}' substitutes '{reference.Reshape.Part.FullName}' in " +
                $"'{type.FullName}', but {reference.Reshape.Describe(reference.Mentioned)} keeps it, because " +
                $"{reference.Reshape.Cause}, so '{type.FullName}' would use '{reference.Mentioned}' through a base type, " +
                "interface, or constraint it does not have.",
                reference.Containing,
                reference.Offset));
            Session.AddUnresolvedReference(rule.Replacement.ToCanonicalString());
        }
    }

    private void RecordReshape(IMemberDefinition definition, TypeReshape? reshape)
    {
        if (reshape is not null)
        {
            _reshapes[definition] = reshape;
        }
    }

    // A type implements and overrides members by signature, so an implementation or override whose signature the pass
    // rewrote on one side only - between a rewritten type and an excluded one, or a type of an assembly the closure
    // does not rewrite - no longer matches, and the type fails to load. Definitions of this module and of an assembly
    // the closure does not rewrite are final here; one of another assembly the closure rewrites is checked once the
    // closure is staged.
    private void CheckTypeShapes()
    {
        var check = new TypeShapeCheck(Session.Matcher.TypeSubstitutionRules);
        foreach (TypeDefinition type in Module!.GetTypes())
        {
            foreach (TypeShapeMismatch mismatch in check.Check(type, TryResolve, IsFinal))
            {
                ReportUnappliedMemberSubstitution(
                    check.RulesFor(mismatch),
                    mismatch.MemberName,
                    CecilNames.FullyQualifiedMethodName(mismatch.Member),
                    offset: -1,
                    mismatch.Describe(Unrewritten));
            }
        }
    }

    private bool IsFinal(TypeDefinition definition) =>
        ReferenceEquals(definition.Module, Module)
        || !Session.RewrittenAssemblyNames.Contains(definition.Module.Assembly.Name.Name);

    private string? Unrewritten(TypeDefinition definition) =>
        !ReferenceEquals(definition.Module, Module)
            ? $"'{definition.FullName}' is declared in '{definition.Module.Assembly.Name.Name}', which is not rewritten in this closure"
            : Session.IsExcluded(definition)
                ? $"'{definition.FullName}' is excluded from rewriting"
                : null;

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

    private RewriteRule[] RewrittenSignatureRules(MemberReference member)
    {
        MemberReference element = member is GenericInstanceMethod generic ? generic.ElementMethod : member;
        if (_rewrittenSignatures.TryGetValue(element, out RewriteRule[]? rules))
        {
            return rules;
        }

        // Any other reference into this module names a member of a generic instance of one of its types. It binds by
        // its own signature, which is the definition's original one, so it no longer binds once that type is rewritten.
        if (element is IMemberDefinition
            || element.DeclaringType.Scope is not ModuleDefinition module
            || !ReferenceEquals(module, Module)
            || element.DeclaringType.Resolve() is not TypeDefinition declaring
            || Session.IsExcluded(declaring))
        {
            return [];
        }

        return element switch
        {
            MethodReference method => MatchingRules([method.ReturnType, .. method.Parameters.Select(parameter => parameter.ParameterType)]),
            FieldReference field => MatchingRules([field.FieldType]),
            _ => [],
        };
    }

    private void RecordRewrittenSignature(MemberReference definition, IEnumerable<TypeReference> originalTypes) =>
        _rewrittenSignatures[definition] = MatchingRules(originalTypes);

    private void TrackBinding<TMember>(MemberReference original, MemberMapping<TMember> mapping, Instruction instruction)
        where TMember : MemberReference
    {
        if (mapping.VerifyAfterPass)
        {
            _pendingBindings.Add(PendingFor(original, mapping.Reference!, instruction));
        }
    }

    // The rules and the original's name are taken now: a definition's signature may be rewritten in place later.
    private PendingBinding PendingFor(MemberReference original, MemberReference mapped, Instruction instruction) =>
        new(
            MemberRules(original),
            original.FullName,
            mapped,
            CecilNames.FullyQualifiedMethodName(Method!),
            instruction.Offset);

    private Instruction? TryBuildAwaiterSource(Instruction instruction, MethodReference method)
    {
        MethodReference? constructor = Mapper.BuildAwaiterSourceConstructor(method);
        if (constructor is null)
        {
            if (Session.Matcher.GetMatchingTypeRules(method.ReturnType).Count > 0)
            {
                ReportUnappliedMemberSubstitution(method, instruction, "could not be mapped to the replacement type");
            }

            return null;
        }

        Instruction replacement = Processor!.Create(OpCodes.Newobj, constructor);
        RecordType(constructor.DeclaringType, method.ReturnType, instruction);
        Replace(instruction, replacement);
        return replacement;
    }

    private MemberSubstitutionMapper BuildMapper() =>
        MemberSubstitutionMapper.Build(Session, (rule, error) =>
        {
            Session.AddDiagnostic(RewriteDiagnostic.Error(
                RewriteDiagnosticIds.UnresolvedReplacement,
                $"{error} Member-aware substitution rule '{rule.Id}' could not be applied."));
            Session.AddUnresolvedReference(rule.Replacement.ToCanonicalString());
        });

    // A base type or interface composed from a substituted type, such as List<Barrier>, is mapped like any other
    // signature. A substituted base type itself is not: the derived type keeps its layout and base constructor calls,
    // so retargeting its members onto the replacement would invoke the replacement's members on an object of a
    // different type. That fails the rewrite instead.
    private void MapBaseTypes(TypeDefinition type)
    {
        if (type.BaseType is TypeReference baseType)
        {
            if (Session.Matcher.TryMatchType(baseType, out RewriteRule rule))
            {
                Session.AddDiagnostic(RewriteDiagnostic.Error(
                    RewriteDiagnosticIds.UnresolvedReplacement,
                    $"Type '{type.FullName}' derives from '{baseType.FullName}', which substitution rule '{rule.Id}' replaces; a type derived from a substituted type cannot be rewritten.",
                    type.FullName));
                Session.AddUnresolvedReference(rule.Replacement.ToCanonicalString());
            }
            else if (Mapper.MapType(baseType) is TypeReference mappedBase)
            {
                type.BaseType = mappedBase;
            }
        }

        foreach (InterfaceImplementation implementation in type.Interfaces)
        {
            TypeReference? mappedInterface = Mapper.MapType(implementation.InterfaceType);
            if (mappedInterface is not null)
            {
                implementation.InterfaceType = mappedInterface;
            }
        }
    }

    // A constraint such as where T : IEnumerable<Barrier> must name the mapped type: call sites rewritten to the
    // replacement instantiate T with a type which satisfies only the mapped constraint.
    private void MapGenericConstraints(Mono.Collections.Generic.Collection<GenericParameter> parameters)
    {
        foreach (GenericParameter parameter in parameters)
        {
            foreach (GenericParameterConstraint constraint in parameter.Constraints)
            {
                TypeReference? mapped = Mapper.MapType(constraint.ConstraintType);
                if (mapped is not null)
                {
                    constraint.ConstraintType = mapped;
                }
            }
        }
    }

    private void ReportUnappliedMemberSubstitution(MemberReference member, Instruction instruction, string failure) =>
        ReportUnappliedMemberSubstitution(
            MemberRules(member),
            member.FullName,
            CecilNames.FullyQualifiedMethodName(Method!),
            instruction.Offset,
            failure);

    private void ReportUnappliedMemberSubstitution(
        RewriteRule[] rules,
        string member,
        string containing,
        int offset,
        string failure)
    {
        foreach (RewriteRule rule in rules)
        {
            string key = $"{rule.Id}|{containing}|{offset}";
            if (!_reportedApplicationFailures.Add(key))
            {
                continue;
            }

            Session.AddDiagnostic(RewriteDiagnostic.Error(
                RewriteDiagnosticIds.UnresolvedReplacement,
                $"Member-aware substitution rule '{rule.Id}' targets member '{member}', which {failure}.",
                containing,
                offset));
            Session.AddUnresolvedReference(rule.Replacement.ToCanonicalString());
        }
    }

    private RewriteRule[] MemberRules(MemberReference member) => member switch
    {
        GenericInstanceMethod generic => MatchingRules(
            [generic.DeclaringType, generic.ReturnType, .. generic.Parameters.Select(parameter => parameter.ParameterType), .. generic.GenericArguments]),
        MethodReference method => MatchingRules(
            [method.DeclaringType, method.ReturnType, .. method.Parameters.Select(parameter => parameter.ParameterType)]),
        FieldReference field => MatchingRules([field.DeclaringType, field.FieldType]),
        _ => [],
    };

    private RewriteRule[] MatchingRules(IEnumerable<TypeReference> types)
    {
        var rules = new Dictionary<string, RewriteRule>(StringComparer.Ordinal);
        foreach (TypeReference type in types)
        {
            foreach (RewriteRule rule in Session.Matcher.GetMatchingTypeRules(type))
            {
                rules.TryAdd(rule.Id, rule);
            }
        }

        return [.. rules.Values];
    }

    private void RecordMethod(MethodReference original, Instruction instruction)
    {
        string normalized = CecilNames.NormalizedTypeFullName(
            original is GenericInstanceMethod generic ? generic.ElementMethod.DeclaringType : original.DeclaringType);
        Record(normalized, original.DeclaringType.FullName, instruction);
    }

    private void RecordType(TypeReference mapped, TypeReference original, Instruction? instruction = null)
    {
        string normalized = CecilNames.NormalizedTypeFullName(original);
        Record(normalized, mapped.FullName, instruction);
    }

    private void Record(string normalizedOriginal, string replacement, Instruction? instruction)
    {
        string? ruleId = Mapper.RuleIdFor(normalizedOriginal);
        if (ruleId is null || Method is null)
        {
            return;
        }

        string? file = null;
        int line = -1;
        if (instruction is not null)
        {
            RewriteSession.TryGetSequencePoint(Method, instruction, out file, out line);
        }

        Session.AddTransformation(new ManifestTransformation(
            ruleId,
            RewriteOperationKind.SubstituteType,
            TransformationOutcome.Transformed,
            SimulationApiPolicy.Controlled,
            normalizedOriginal,
            replacement,
            CecilNames.FullyQualifiedMethodName(Method),
            instruction?.Offset ?? -1,
            file,
            line));
    }

    private readonly record struct PendingBinding(RewriteRule[] Rules, string Original, MemberReference Mapped, string Method, int Offset);
}
