namespace Orleans.CodeGenerator
{
    using System;
    using System.CodeDom.Compiler;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Reflection;

    using Microsoft.CodeAnalysis.CSharp.Syntax;

    using Orleans.Async;
    using Orleans.CodeGeneration;
    using Orleans.Runtime;
    using Orleans.Serialization;

    using GrainInterfaceData = Orleans.CodeGeneration.GrainInterfaceData;
    using SF = Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

    public class CodeGenerator : IRuntimeCodeGenerator, ISourceCodeGenerator
    {
        /// <summary>
        /// The compiled assemblies.
        /// </summary>
        private static readonly ConcurrentDictionary<Assembly, bool> CompiledAssemblies =
            new ConcurrentDictionary<Assembly, bool>();

        /// <summary>
        /// The logger.
        /// </summary>
        private static readonly Logger Logger = TraceLogger.GetLogger("CodeGenerator");

        /// <summary>
        /// The static instance.
        /// </summary>
        private static readonly CodeGenerator StaticInstance = new CodeGenerator();

        /// <summary>
        /// The assembly name of the core Orleans assembly.
        /// </summary>
        private static readonly string OrleansCoreAssembly = typeof(IGrain).Assembly.GetName().FullName;

        /// <summary>
        /// Gets or sets the static instance.
        /// </summary>
        public static CodeGenerator Instance
        {
            get
            {
                return StaticInstance;
            }
        }

        public void GenerateAndLoadForAllAssemblies()
        {
            this.GenerateAndLoadForAssemblies(AppDomain.CurrentDomain.GetAssemblies());
        }

        public void GenerateAndLoadForAssemblies(params Assembly[] inputs)
        {
            if (inputs == null)
            {
                throw new ArgumentNullException("inputs");
            }

            var timer = Stopwatch.StartNew();
            var grainAssemblies = inputs.Where(ShouldGenerateCodeForAssembly).ToList();
            if (grainAssemblies.Count == 0)
            {
                // Already up to date.
                return;
            }

            // Generate code for newly loaded assemblies.
            var generated = GenerateForAssemblies(grainAssemblies, true);

            if (generated.Syntax != null)
            {
                CompileAndLoad(generated);
            }

            foreach (var assembly in generated.SourceAssemblies)
            {
                CompiledAssemblies.TryAdd(assembly, true);
            }

            Logger.Info(
                (int)ErrorCode.CodeGenCompilationSucceeded,
                "Generated code for {0} assemblies in {1}ms",
                generated.SourceAssemblies.Count,
                timer.ElapsedMilliseconds);
        }

        private static bool ShouldGenerateCodeForAssembly(Assembly assembly)
        {
            return !assembly.IsDynamic && !CompiledAssemblies.ContainsKey(assembly)
                   && IsAssemblyEqualOrReferences(OrleansCoreAssembly, assembly)
                   && assembly.GetCustomAttribute<GeneratedCodeAttribute>() == null;
        }

        public void GenerateAndLoadForAssembly(Assembly input)
        {
            if (!ShouldGenerateCodeForAssembly(input))
            {
                return;
            }

            var timer = Stopwatch.StartNew();
            var generated = GenerateForAssemblies(new List<Assembly> { input }, true);

            if (generated.Syntax != null)
            {
                CompileAndLoad(generated);
            }

            foreach (var assembly in generated.SourceAssemblies)
            {
                CompiledAssemblies.TryAdd(assembly, true);
            }

            Logger.Info(
                (int)ErrorCode.CodeGenCompilationSucceeded,
                "Generated code for 1 assembly in {0}ms",
                timer.ElapsedMilliseconds);
        }

        public string GenerateSourceForAssembly(Assembly input)
        {
            if (!ShouldGenerateCodeForAssembly(input))
            {
                return string.Empty;
            }

            var generated = GenerateForAssemblies(new List<Assembly> { input }, false);
            if (generated.Syntax == null)
            {
                return string.Empty;
            }

            return
                CodeGeneratorCommon.GenerateSourceCode(CodeGeneratorCommon.AddGeneratedCodeAttribute(generated.Syntax));
        }

        private static void CompileAndLoad(GeneratedSyntax generatedSyntax)
        {
            var generatedAssembly = CodeGeneratorCommon.CompileAssembly(generatedSyntax.Syntax, "OrleansCodeGen.dll");

            GrainReferenceGenerator.RegisterGrainReferenceSerializers(generatedAssembly);
            SerializationManager.FindSerializationInfo(generatedAssembly);
        }

        private static GeneratedSyntax GenerateForAssemblies(List<Assembly> assemblies, bool runtime)
        {
            Logger.Info("Generating code for assemblies: {0}", string.Join(", ", assemblies.Select(_ => _.FullName)));

            Assembly targetAssembly;
            HashSet<Type> ignoreTypes;
            if (runtime)
            {
                // Ignore types which have already been accounted for.
                ignoreTypes = CodeGeneratorCommon.GetTypesWithImplementations(
                    typeof(MethodInvokerAttribute),
                    typeof(GrainReferenceAttribute),
                    typeof(GrainStateAttribute),
                    typeof(SerializerAttribute));
                targetAssembly = null;
            }
            else
            {
                ignoreTypes = new HashSet<Type>();
                targetAssembly = assemblies.FirstOrDefault();
            }

            var members = new List<MemberDeclarationSyntax>();

            // Get types from assemblies which reference Orleans and are not generated assemblies.
            var grainTypes = new HashSet<Type>();
            foreach (var type in assemblies.SelectMany(_ => _.GetTypes()))
            {
                // The module containing the serializer.
                var module = runtime ? null : type.Module;

                // Every type which is encountered must be considered for serialization.
                if (!type.IsNested && !type.IsGenericParameter && type.IsSerializable)
                {
                    // If a type was encountered which can be accessed, process it for serialization.
                    var isAccessibleForSerialization =
                        !TypeUtilities.IsTypeIsInaccessibleForSerialization(type, module, targetAssembly);
                    if (isAccessibleForSerialization)
                    {
                        SerializerGenerationManager.RecordTypeToGenerate(type);
                    }
                }

                // Collect the types which require code generation.
                if (GrainInterfaceData.IsGrainInterface(type))
                {
                    Logger.Info("Will generate code for: {0}", type.GetParseableName());
                    grainTypes.Add(type);
                }
            }

            grainTypes.RemoveWhere(_ => ignoreTypes.Contains(_));

            // Group the types by namespace and generate the required code in each namespace.
            foreach (var group in grainTypes.GroupBy(_ => CodeGeneratorCommon.GetGeneratedNamespace(_)))
            {
                var namespaceMembers = new List<MemberDeclarationSyntax>();
                foreach (var type in group)
                {
                    // The module containing the serializer.
                    var module = runtime ? null : type.Module;

                    // Every type which is encountered must be considered for serialization.
                    Action<Type> onEncounteredType = encounteredType =>
                    {
                        // If a type was encountered which can be accessed, process it for serialization.
                        var isAccessibleForSerialization =
                            !TypeUtilities.IsTypeIsInaccessibleForSerialization(encounteredType, module, targetAssembly);
                        if (isAccessibleForSerialization)
                        {
                            SerializerGenerationManager.RecordTypeToGenerate(encounteredType);
                        }
                    };

                    Logger.Info("Generating code for: {0}", type.GetParseableName());
                    if (GrainInterfaceData.IsGrainInterface(type))
                    {
                        Logger.Info("Generating GrainReference and MethodInvoker for {0}", type.GetParseableName());

                        namespaceMembers.Add(GrainReferenceGenerator.GenerateClass(type, onEncounteredType));
                        namespaceMembers.Add(GrainMethodInvokerGenerator.GenerateClass(type));
                    }
                    
                    // Generate serializers.
                    Type toGen;
                    ConsoleText.WriteStatus("ClientGenerator - Generating serializer classes for types:");
                    while (SerializerGenerationManager.GetNextTypeToProcess(out toGen))
                    {
                        // Filter types which are inaccessible by the serialzation module/assembly.
                        var skipSerialzerGeneration =
                            toGen.GetAllFields()
                                .Any(
                                    field =>
                                    TypeUtilities.IsTypeIsInaccessibleForSerialization(
                                        field.FieldType,
                                        module,
                                        targetAssembly));
                        if (skipSerialzerGeneration)
                        {
                            continue;
                        }

                        ConsoleText.WriteStatus(
                            "\ttype " + toGen.FullName + " in namespace " + toGen.Namespace + " defined in Assembly "
                            + toGen.Assembly.GetName());
                        Logger.Info("Generating & Registering Serializer for Type {0}", toGen.GetParseableName());
                        namespaceMembers.AddRange(SerializerGenerator.GenerateClass(toGen, onEncounteredType));
                    }
                }

                if (namespaceMembers.Count == 0)
                {
                    Logger.Info("Skipping namespace: {0}", group.Key);

                    continue;
                }

                members.Add(
                    SF.NamespaceDeclaration(SF.ParseName(group.Key))
                        .AddUsings(
                            TypeUtils.GetNamespaces(typeof(TaskUtility), typeof(GrainExtensions))
                                .Select(_ => SF.UsingDirective(SF.ParseName(_)))
                                .ToArray())
                        .AddMembers(namespaceMembers.ToArray()));
            }

            return new GeneratedSyntax
            {
                SourceAssemblies = assemblies,
                Syntax = members.Count > 0 ? SF.CompilationUnit().AddMembers(members.ToArray()) : null
            };
        }

        private static bool IsAssemblyEqualOrReferences(string expected, Assembly actual)
        {
            if (actual.GetName().FullName == expected)
            {
                return true;
            }

            return
                actual.GetReferencedAssemblies()
                    .Any(asm => string.Equals(asm.FullName, expected, StringComparison.InvariantCulture));
        }

        private class GeneratedSyntax
        {
            public List<Assembly> SourceAssemblies { get; set; }
            public CompilationUnitSyntax Syntax { get; set; }
        }
    }
}