using Orleans.Testing.Simulation.Instrumentation.Diagnostics;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Orleans.Testing.Simulation.Instrumentation.Rewriting;

/// <summary>
/// Post-rewrite validation. Reads the written assembly back with Mono.Cecil (proving it is
/// structurally loadable), checks per-method that every branch target and exception-handler
/// boundary points at an instruction that still exists in the method body, and checks that every member reference
/// whose binding depends on a type substitution binds. Any failure is reported as a deterministic
/// <see cref="RewriteDiagnosticIds.ValidationFailed"/> error rather than throwing.
/// </summary>
internal static class RewriteValidator
{
    public static IReadOnlyList<RewriteDiagnostic> Validate(
        string assemblyPath,
        IEnumerable<string> searchDirectories,
        BindingScope bindings)
    {
        var diagnostics = new List<RewriteDiagnostic>();

        using var resolver = new GuardedAssemblyResolver();
        string? directory = Path.GetDirectoryName(Path.GetFullPath(assemblyPath));
        if (!string.IsNullOrEmpty(directory))
        {
            resolver.AddSearchDirectory(directory);
        }

        foreach (string dir in searchDirectories)
        {
            if (!string.IsNullOrEmpty(dir))
            {
                resolver.AddSearchDirectory(dir);
            }
        }

        try
        {
            using AssemblyDefinition definition = AssemblyDefinition.ReadAssembly(assemblyPath, new ReaderParameters
            {
                AssemblyResolver = resolver,
                MetadataResolver = resolver.Guard,
                ReadSymbols = false,
                InMemory = true,
            });

            foreach (ModuleDefinition module in definition.Modules)
            {
                foreach (TypeDefinition type in module.GetTypes())
                {
                    foreach (MethodDefinition method in type.Methods)
                    {
                        if (method.HasBody)
                        {
                            ValidateBody(method, diagnostics);
                        }
                    }
                }

                ValidateBindings(module, bindings, diagnostics);
            }
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or Mono.Cecil.Cil.SymbolsNotMatchingException)
        {
            diagnostics.Add(RewriteDiagnostic.Error(
                RewriteDiagnosticIds.ValidationFailed,
                $"Read-back of rewritten assembly '{Path.GetFileName(assemblyPath)}' failed: {ex.Message}"));
        }

        return diagnostics;
    }

    // A member reference whose signature mentions a substituted or substitute type binds only if the rewrite produced
    // a signature its declaring type declares. One into this module, or into an assembly the closure does not rewrite
    // (the framework, a replacement assembly, or an excluded assembly), can be resolved now, because neither changes
    // after this rewrite; one into another rewritten assembly is checked by the closure runner once it is staged.
    private static void ValidateBindings(ModuleDefinition module, BindingScope bindings, List<RewriteDiagnostic> diagnostics)
    {
        foreach (MemberReference member in module.GetMemberReferences())
        {
            // The runtime provides the methods of every array type, which Cecil does not resolve.
            bool resolvable = member.DeclaringType is not ArrayType && member.DeclaringType.Scope switch
            {
                ModuleDefinition scope => ReferenceEquals(scope, module),
                AssemblyNameReference assembly => !bindings.RewrittenAssemblyNames.Contains(assembly.Name)
                    || string.Equals(assembly.Name, module.Assembly.Name.Name, StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
            if (!resolvable || SignatureMentions.Find(member, bindings.SignatureTypeNames).Count == 0)
            {
                continue;
            }

            string? failure;
            try
            {
                failure = member.Resolve() is null
                    ? $"Member reference '{member.FullName}' does not bind after rewriting: " +
                        $"'{CecilNames.NormalizedTypeFullName(member.DeclaringType)}' declares no member with that signature."
                    : null;
            }
            catch (AssemblyResolutionException exception)
            {
                failure = $"Member reference '{member.FullName}' could not be resolved to check that it binds after " +
                    $"rewriting: {exception.InnerException?.Message ?? exception.Message}";
            }

            if (failure is not null)
            {
                diagnostics.Add(RewriteDiagnostic.Error(RewriteDiagnosticIds.ValidationFailed, failure));
            }
        }
    }

    private static void ValidateBody(MethodDefinition method, List<RewriteDiagnostic> diagnostics)
    {
        MethodBody body = method.Body;
        var instructions = new HashSet<Instruction>(body.Instructions);
        string name = CecilNames.FullyQualifiedMethodName(method);

        foreach (Instruction instruction in body.Instructions)
        {
            if (instruction.Operand is Instruction target && !instructions.Contains(target))
            {
                diagnostics.Add(RewriteDiagnostic.Error(
                    RewriteDiagnosticIds.ValidationFailed,
                    "A branch targets an instruction that is not part of the method body after rewriting.",
                    name,
                    instruction.Offset));
            }
            else if (instruction.Operand is Instruction[] targets)
            {
                foreach (Instruction switchTarget in targets)
                {
                    if (!instructions.Contains(switchTarget))
                    {
                        diagnostics.Add(RewriteDiagnostic.Error(
                            RewriteDiagnosticIds.ValidationFailed,
                            "A switch branch targets an instruction that is not part of the method body after rewriting.",
                            name,
                            instruction.Offset));
                    }
                }
            }
        }

        if (!body.HasExceptionHandlers)
        {
            return;
        }

        foreach (ExceptionHandler handler in body.ExceptionHandlers)
        {
            CheckBoundary(handler.TryStart, instructions, name, "try-start", diagnostics);
            CheckBoundary(handler.TryEnd, instructions, name, "try-end", diagnostics);
            CheckBoundary(handler.HandlerStart, instructions, name, "handler-start", diagnostics);
            CheckBoundary(handler.HandlerEnd, instructions, name, "handler-end", diagnostics);
            CheckBoundary(handler.FilterStart, instructions, name, "filter-start", diagnostics);
        }
    }

    private static void CheckBoundary(
        Instruction? boundary,
        HashSet<Instruction> instructions,
        string method,
        string label,
        List<RewriteDiagnostic> diagnostics)
    {
        if (boundary is not null && !instructions.Contains(boundary))
        {
            diagnostics.Add(RewriteDiagnostic.Error(
                RewriteDiagnosticIds.ValidationFailed,
                $"An exception-handler {label} boundary points at an instruction that is not part of the method body after rewriting.",
                method,
                boundary.Offset));
        }
    }

    /// <summary>The member references whose binding the validator checks.</summary>
    /// <param name="SignatureTypeNames">The substituted and substitute types whose mention makes binding depend on the rewrite.</param>
    /// <param name="RewrittenAssemblyNames">The other assemblies the closure rewrites, whose members are checked when staged.</param>
    internal sealed record BindingScope(IReadOnlySet<string> SignatureTypeNames, IReadOnlySet<string> RewrittenAssemblyNames);
}
