using Orleans.Testing.Simulation.Instrumentation.Closure;
using Orleans.Testing.Simulation.Instrumentation.Diagnostics;
using Orleans.Testing.Simulation.Instrumentation.Rewriting;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Orchestration;

/// <summary>
/// Checks, once every assembly of a closure is staged, that the code of each staged assembly still binds to its
/// rewritten assemblies. A rewrite substitutes the types in its assembly's member signatures, so code the closure does
/// not rewrite - an assembly it copies, or a type excluded from rewriting - keeps the original signatures, and neither
/// a member reference from it into a rewritten assembly nor an interface implementation or override across that
/// boundary binds any more. Each such reference, implementation, and override whose signature mentions a substituted or
/// substitute type is checked against the staged closure, and one that does not bind fails the run with
/// <see cref="RewriteDiagnosticIds.UnresolvedReplacement"/> instead of throwing <see cref="MissingMemberException"/> or
/// <see cref="TypeLoadException"/> when the runtime binds it. A rewrite also substitutes the base types, interfaces, and
/// generic parameter constraints composed from a substituted type, so such code fails the run too where it mentions a
/// type or method so reshaped: it could use the type through a base type or interface it no longer has. Conversely,
/// rewritten code fails the run where it mentions a type that keeps such a part - an excluded type of another rewritten
/// assembly, or a type deriving from one - which the rewrite could not know was excluded.
/// </summary>
internal static class ClosureBindingValidator
{
    /// <summary>Checks every staged non-framework managed assembly against the rewritten assemblies.</summary>
    /// <param name="plan">The closure plan.</param>
    /// <param name="stagingDirectory">The directory the closure is staged in.</param>
    /// <param name="frameworkClassifier">The classifier that kept framework assemblies out of the rewrite.</param>
    /// <param name="runtimeSupport">The rule set the closure is rewritten with, evaluated for its target runtime.</param>
    /// <param name="rewrittenAssemblyNames">The simple names of the assemblies the closure rewrites.</param>
    /// <param name="excludedTypesByAssembly">
    /// The full names of the types each rewritten assembly excluded from rewriting, by assembly simple name, where known.
    /// </param>
    /// <returns>An error for every reference, implementation, and override that does not bind.</returns>
    public static IReadOnlyList<RewriteDiagnostic> Validate(
        ClosurePlan plan,
        string stagingDirectory,
        FrameworkAssemblyClassifier frameworkClassifier,
        RuleSetRuntimeSupport runtimeSupport,
        IReadOnlyCollection<string> rewrittenAssemblyNames,
        IReadOnlyDictionary<string, IReadOnlySet<string>> excludedTypesByAssembly)
    {
        var diagnostics = new List<RewriteDiagnostic>();
        var matcher = new RewriteRuleMatcher(runtimeSupport);
        IReadOnlySet<string> signatureTypes = matcher.GetSubstitutionTypeNames();
        if (signatureTypes.Count == 0 || rewrittenAssemblyNames.Count == 0)
        {
            return diagnostics;
        }

        var ruleIdByType = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, RewriteRule> entry in matcher.TypeSubstitutionRules)
        {
            ruleIdByType.TryAdd(entry.Key, entry.Value.Id);
            ruleIdByType.TryAdd(entry.Value.Replacement.DeclaringTypeFullName, entry.Value.Id);
        }

        var closure = new Closure(
            new HashSet<string>(rewrittenAssemblyNames, StringComparer.OrdinalIgnoreCase),
            excludedTypesByAssembly,
            signatureTypes,
            matcher.TypeSubstitutionRules.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal));
        var typeShapes = new TypeShapeCheck(matcher.TypeSubstitutionRules);
        ClosureAsset[] candidates =
        [
            .. plan.Assets.Where(asset => asset.Kind == AssetKind.ManagedAssembly
                && !frameworkClassifier.IsFrameworkAssembly(Path.GetFileNameWithoutExtension(asset.RelativePath))),
        ];

        using var resolver = new StagedClosureAssemblyResolver(stagingDirectory, frameworkClassifier);
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (ClosureAsset asset in candidates)
        {
            string stagedPath = StagedPath(stagingDirectory, asset);
            if (!File.Exists(stagedPath))
            {
                continue;
            }

            using ModuleDefinition module = ModuleDefinition.ReadModule(stagedPath, resolver.CreateReaderParameters());
            string consumer = module.Assembly?.Name.Name ?? Path.GetFileNameWithoutExtension(stagedPath);
            foreach (MemberReference member in module.GetMemberReferences())
            {
                // The runtime provides the methods of every array type, which Cecil does not resolve.
                if (member.DeclaringType is ArrayType
                    || member.DeclaringType.Scope is not AssemblyNameReference declaring
                    || !closure.IsRewritten(declaring.Name)
                    || string.Equals(declaring.Name, consumer, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                IReadOnlyList<string> mentioned = SignatureMentions.Find(member, signatureTypes);
                if (mentioned.Count == 0 || closure.BindingFailure(member, module, consumer, declaring.Name) is not string failure)
                {
                    continue;
                }

                foreach (string ruleId in mentioned.Select(type => ruleIdByType[type]).Distinct(StringComparer.Ordinal))
                {
                    if (reported.Add($"{ruleId}|{asset.RelativePath}|{member.FullName}"))
                    {
                        diagnostics.Add(RewriteDiagnostic.Error(
                            RewriteDiagnosticIds.UnresolvedReplacement,
                            $"Member-aware substitution rule '{ruleId}' rewrites '{declaring.Name}', but '{asset.RelativePath}' " +
                            $"references its member '{member.FullName}', which {failure}.",
                            asset.RelativePath));
                    }
                }
            }

            // The rewrite checked each type whose interfaces and base types it could see final; one reaching into
            // another assembly the closure rewrites, or of an assembly it does not rewrite, is checked here.
            foreach (TypeDefinition type in module.GetTypes().Where(type => closure.ReachesOtherRewrittenAssembly(type, consumer)))
            {
                foreach (TypeShapeMismatch mismatch in typeShapes.Check(type, TryResolve, _ => true))
                {
                    string failure = mismatch.Describe(closure.Unrewritten);
                    foreach (RewriteRule rule in typeShapes.RulesFor(mismatch))
                    {
                        if (reported.Add($"{rule.Id}|{asset.RelativePath}|{mismatch.MemberName}|{mismatch.Expected}"))
                        {
                            diagnostics.Add(RewriteDiagnostic.Error(
                                RewriteDiagnosticIds.UnresolvedReplacement,
                                $"Member-aware substitution rule '{rule.Id}' targets member '{mismatch.MemberName}' of " +
                                $"'{asset.RelativePath}', which {failure}.",
                                asset.RelativePath));
                        }
                    }
                }
            }

            // A rewrite substitutes the base types, interfaces, and generic parameter constraints of the types and
            // methods it rewrites, and code the closure does not rewrite keeps relying on the original ones.
            if (!closure.IsRewritten(consumer))
            {
                foreach (TypeReference reference in module.GetTypeReferences())
                {
                    if (closure.ReshapeOf(reference) is { } reshape)
                    {
                        ReportReshape(asset, reference.FullName, reshape, $"'{consumer}' is not rewritten in this closure");
                    }
                }

                foreach (MethodReference method in module.GetMemberReferences().OfType<MethodReference>())
                {
                    if (closure.MethodReshapeOf(method) is { } reshape)
                    {
                        ReportReshape(asset, reshape.Owner, reshape, $"'{consumer}' is not rewritten in this closure");
                    }
                }
            }
            else
            {
                foreach (TypeDefinition type in module.GetTypes().Where(type => closure.IsExcluded(consumer, type.FullName)))
                {
                    foreach (ReshapedReference reference in TypeReshapes.FindReferences(type, closure.ReshapeOf, closure.MethodReshapeOf))
                    {
                        ReportReshape(
                            asset,
                            reference.Mentioned,
                            reference.Reshape,
                            $"'{type.FullName}' is excluded from rewriting in '{consumer}'");
                    }
                }

                // Conversely, the rewritten code sees such a part as composed from the substitute, so it cannot use a
                // type that keeps the original one. The rewrite checked the types it knew were not rewritten; whether
                // a type of another assembly the closure rewrites is excluded there is known only now.
                if (closure.HasExclusions)
                {
                    foreach (TypeDefinition type in module.GetTypes().Where(type => !closure.IsExcluded(consumer, type.FullName)))
                    {
                        foreach (ReshapedReference reference in TypeReshapes.FindReferences(type, closure.KeptShapeOf, closure.KeptMethodShapeOf)
                            .DistinctBy(reference => reference.Mentioned, StringComparer.Ordinal))
                        {
                            ReportKeptShape(asset, consumer, type.FullName, reference);
                        }
                    }
                }
            }
        }

        // A resolution that met a cycle of type forwarders failed, so whatever depended on it was not checked; the
        // runtime would not load the type either.
        foreach (string cycle in resolver.Guard.Cycles)
        {
            diagnostics.Add(RewriteDiagnostic.Error(
                RewriteDiagnosticIds.UnresolvedReference,
                $"{cycle}, so the staged closure cannot be checked against the type."));
        }

        return diagnostics;

        void ReportReshape(ClosureAsset asset, string mentioned, TypeReshape reshape, string referrer)
        {
            foreach (string ruleId in SignatureMentions.Find(reshape.Part, signatureTypes)
                .Select(type => ruleIdByType[type])
                .Distinct(StringComparer.Ordinal))
            {
                if (reported.Add($"{ruleId}|{asset.RelativePath}|{mentioned}"))
                {
                    diagnostics.Add(RewriteDiagnostic.Error(
                        RewriteDiagnosticIds.UnresolvedReplacement,
                        $"Member-aware substitution rule '{ruleId}' rewrites {reshape.Describe(mentioned)} to " +
                        $"'{reshape.Part.FullName}', but {referrer} and still uses '{mentioned}' as declared, through a base " +
                        "type, interface, or constraint it no longer has.",
                        asset.RelativePath));
                }
            }
        }

        void ReportKeptShape(ClosureAsset asset, string consumer, string user, ReshapedReference reference)
        {
            TypeReshape kept = reference.Reshape;
            foreach (string ruleId in SignatureMentions.Find(kept.Part, signatureTypes)
                .Select(type => ruleIdByType[type])
                .Distinct(StringComparer.Ordinal))
            {
                if (reported.Add($"kept|{ruleId}|{asset.RelativePath}|{reference.Mentioned}"))
                {
                    diagnostics.Add(RewriteDiagnostic.Error(
                        RewriteDiagnosticIds.UnresolvedReplacement,
                        $"Member-aware substitution rule '{ruleId}' substitutes '{kept.Part.FullName}' in '{user}' of " +
                        $"'{consumer}', but {kept.Describe(reference.Mentioned)} keeps it, because {kept.Cause}, so '{user}' " +
                        $"would use '{reference.Mentioned}' through a base type, interface, or constraint it does not have.",
                        asset.RelativePath));
                }
            }
        }
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

    private static string StagedPath(string stagingDirectory, ClosureAsset asset) =>
        Path.Combine(stagingDirectory, asset.RelativePath.Replace('/', Path.DirectorySeparatorChar));

    private sealed class Closure(
        HashSet<string> rewrittenAssemblyNames,
        IReadOnlyDictionary<string, IReadOnlySet<string>> excludedTypesByAssembly,
        IReadOnlySet<string> signatureTypes,
        IReadOnlySet<string> substitutedTypes)
    {
        private readonly Dictionary<TypeDefinition, TypeReshape?> _reshapes = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<TypeDefinition, TypeReshape?> _keptShapes = new(ReferenceEqualityComparer.Instance);

        public bool HasExclusions { get; } = excludedTypesByAssembly.Values.Any(excluded => excluded.Count > 0);

        public bool IsRewritten(string assemblyName) => rewrittenAssemblyNames.Contains(assemblyName);

        public bool IsExcluded(string assemblyName, string typeFullName) =>
            excludedTypesByAssembly.TryGetValue(assemblyName, out IReadOnlySet<string>? excluded) && excluded.Contains(typeFullName);

        /// <summary>
        /// The reshape of a type of a rewritten assembly - one the rewrite does not exclude whose base type, interfaces,
        /// or generic parameter constraints name a substituted or substitute type, or which derives from one - or null.
        /// </summary>
        public TypeReshape? ReshapeOf(TypeReference type)
        {
            if (!IsInRewrittenScope(type.Scope) || TryResolve(type) is not { } definition)
            {
                return null;
            }

            if (_reshapes.TryGetValue(definition, out TypeReshape? known))
            {
                return known;
            }

            string assembly = definition.Module.Assembly.Name.Name;
            TypeReshape? reshape = IsRewritten(assembly) && !IsExcluded(assembly, definition.FullName)
                ? TypeReshapes.Find(definition, MentionsSignatureType) ?? (definition.BaseType is { } baseType ? ReshapeOf(baseType) : null)
                : null;
            _reshapes[definition] = reshape;
            return reshape;
        }

        /// <summary>The reshape of a generic method of a rewritten assembly whose constraints name a substituted or substitute type, or null.</summary>
        public TypeReshape? MethodReshapeOf(MethodReference method)
        {
            MethodReference element = method is GenericInstanceMethod generic ? generic.ElementMethod : method;
            if (!element.HasGenericParameters || !IsInRewrittenScope(element.DeclaringType.Scope))
            {
                return null;
            }

            MethodDefinition? definition;
            try
            {
                definition = element.Resolve();
            }
            catch (AssemblyResolutionException)
            {
                return null;
            }

            string? assembly = definition?.Module.Assembly.Name.Name;
            return assembly is not null && IsRewritten(assembly) && !IsExcluded(assembly, definition!.DeclaringType.FullName)
                ? TypeReshapes.Find(definition, MentionsSignatureType)
                : null;
        }

        /// <summary>
        /// The part composed from a substituted type that a type of a rewritten assembly keeps - being excluded there,
        /// or deriving from such a type through types that are not rewritten either - or null.
        /// </summary>
        public TypeReshape? KeptShapeOf(TypeReference type) =>
            IsInRewrittenScope(type.Scope) && TryResolve(type) is { } definition ? KeptShape(definition) : null;

        /// <summary>
        /// The constraint composed from a substituted type that a generic method of an excluded type of a rewritten
        /// assembly keeps, or null.
        /// </summary>
        public TypeReshape? KeptMethodShapeOf(MethodReference method)
        {
            MethodReference element = method is GenericInstanceMethod generic ? generic.ElementMethod : method;
            if (!element.HasGenericParameters || !IsInRewrittenScope(element.DeclaringType.Scope))
            {
                return null;
            }

            MethodDefinition? definition;
            try
            {
                definition = element.Resolve();
            }
            catch (AssemblyResolutionException)
            {
                return null;
            }

            return definition is not null
                && Unrewritten(definition.DeclaringType) is string cause
                && TypeReshapes.Find(definition, NamesSubstitutedType) is { } kept
                    ? kept with { Cause = cause }
                    : null;
        }

        public bool ReachesOtherRewrittenAssembly(TypeDefinition type, string assemblyName)
        {
            foreach (InterfaceImplementation implementation in type.Interfaces)
            {
                if (TryResolve(implementation.InterfaceType) is { } declaring && IsOtherRewritten(declaring, assemblyName))
                {
                    return true;
                }
            }

            for (TypeReference? baseType = type.BaseType; baseType is not null && TryResolve(baseType) is { } definition; baseType = definition.BaseType)
            {
                if (IsOtherRewritten(definition, assemblyName))
                {
                    return true;
                }
            }

            return false;
        }

        public string? Unrewritten(TypeDefinition definition)
        {
            string assembly = definition.Module.Assembly.Name.Name;
            return !IsRewritten(assembly)
                ? $"'{definition.FullName}' is declared in '{assembly}', which is not rewritten in this closure"
                : IsExcluded(assembly, definition.FullName)
                    ? $"'{definition.FullName}' is excluded from rewriting in '{assembly}'"
                    : null;
        }

        public string? BindingFailure(MemberReference member, ModuleDefinition consumerModule, string consumer, string declaringAssembly)
        {
            try
            {
                if (member.Resolve() is not null)
                {
                    return null;
                }
            }
            catch (AssemblyResolutionException exception)
            {
                return $"could not be resolved against the staged closure: {exception.InnerException?.Message ?? exception.Message}";
            }

            string declaringType = member.DeclaringType.GetElementType().FullName;
            if (!IsRewritten(consumer))
            {
                return $"does not bind, because '{consumer}' is not rewritten in this closure and keeps the original signature";
            }

            if (IsExcluded(declaringAssembly, declaringType))
            {
                return $"does not bind, because its declaring type '{declaringType}' is excluded from rewriting in " +
                    $"'{declaringAssembly}' and keeps the original signature";
            }

            return ExcludedReferencingType(member, consumerModule, consumer) is string referencing
                ? $"does not bind, because the referencing type '{referencing}' is excluded from rewriting and keeps the original signature"
                : $"does not bind to a member of '{declaringType}' after rewriting";
        }

        private bool IsOtherRewritten(TypeDefinition definition, string assemblyName)
        {
            string declaring = definition.Module.Assembly.Name.Name;
            return IsRewritten(declaring) && !string.Equals(declaring, assemblyName, StringComparison.OrdinalIgnoreCase);
        }

        // Only a type of a rewritten assembly, or of the module being checked, can be reshaped; the scope check avoids
        // resolving every reference into the framework and the assemblies the closure copies.
        private bool IsInRewrittenScope(IMetadataScope scope) => scope switch
        {
            AssemblyNameReference assembly => IsRewritten(assembly.Name),
            ModuleDefinition => true,
            _ => false,
        };

        private bool MentionsSignatureType(TypeReference type) => SignatureMentions.Find(type, signatureTypes).Count > 0;

        private bool NamesSubstitutedType(TypeReference type) => SignatureMentions.Find(type, substitutedTypes).Count > 0;

        private TypeReshape? KeptShape(TypeDefinition definition)
        {
            if (_keptShapes.TryGetValue(definition, out TypeReshape? known))
            {
                return known;
            }

            TypeReshape? kept = null;
            if (Unrewritten(definition) is string cause)
            {
                kept = TypeReshapes.Find(definition, NamesSubstitutedType) is { } own
                    ? own with { Cause = cause }
                    : definition.BaseType is { } baseType && TryResolve(baseType) is { } baseDefinition ? KeptShape(baseDefinition) : null;
            }

            _keptShapes[definition] = kept;
            return kept;
        }

        private string? ExcludedReferencingType(MemberReference member, ModuleDefinition consumerModule, string consumer)
        {
            if (!excludedTypesByAssembly.TryGetValue(consumer, out IReadOnlySet<string>? excluded))
            {
                return null;
            }

            foreach (TypeDefinition type in consumerModule.GetTypes().Where(type => excluded.Contains(type.FullName)))
            {
                foreach (MethodDefinition method in type.Methods)
                {
                    if (method.Overrides.Any(implemented => implemented.FullName == member.FullName)
                        || (method.HasBody && method.Body.Instructions.Any(instruction =>
                            instruction.Operand is MemberReference operand && operand.FullName == member.FullName)))
                    {
                        return type.FullName;
                    }
                }
            }

            return null;
        }
    }
}
