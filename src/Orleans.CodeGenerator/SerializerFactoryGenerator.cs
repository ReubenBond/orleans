using System.Text;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Model;
using Orleans.CodeGenerator.SyntaxGeneration;

namespace Orleans.CodeGenerator;

internal static class SerializerFactoryGenerator
{
    // Configuration statements are written inside the context's partial class and configuration method.
    private const int ContextStatementIndentation = 2;

    internal sealed class Registration(ITypeSymbol type, bool isProvided = false)
    {
        public ITypeSymbol Type { get; } = type;
        public bool IsProvided { get; } = isProvided;
        public string Codec { get; set; } = "";
        public string Copier { get; set; } = "";
        public string CodecConstruction { get; set; } = "";
        public string CopierConstruction { get; set; } = "";
        public bool HasBaseCodec { get; set; }
        public bool HasBaseCopier { get; set; }
        public List<string> CodecDependencies { get; } = [];
        public List<string> CopierDependencies { get; } = [];
        public List<ITypeSymbol> Dependencies { get; } = [];
        public ISerializableTypeDescription? Model { get; set; }
        public ITypeSymbol? ResponseResult { get; set; }
        public INamedTypeSymbol? ReferencedCodec { get; set; }
        public INamedTypeSymbol? ReferencedCopier { get; set; }
        public List<IArrayTypeSymbol> CanonicalArrays { get; } = [];
    }

    internal sealed class Graph(IReadOnlyDictionary<ITypeSymbol, Registration> registrations, string configurationStatements)
    {
        public IReadOnlyDictionary<ITypeSymbol, Registration> Registrations { get; } = registrations;
        public string ConfigurationStatements { get; } = configurationStatements;
    }

    internal sealed record Failure(ITypeSymbol Type, string Reason, bool RequiresImplementation = false);

    internal static bool HasSupportedModelConstruction(ISerializableTypeDescription model)
        => !model.HasComplexBaseType && !model.UseActivator && model.SerializationHooks.Count == 0;

    internal static Graph? CreateRpcModelRoot(
        IGeneratorServices services,
        INamedTypeSymbol type,
        CancellationToken cancellationToken)
        => CreateRpcModelRoot(services, type, cancellationToken, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default), includeResponse: true);

    private static Graph? CreateRpcModelRoot(
        IGeneratorServices services,
        INamedTypeSymbol type,
        CancellationToken cancellationToken,
        HashSet<ITypeSymbol> constructionTypes,
        bool includeResponse)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var originalBinding = services.Compilation.GetSemanticModel(services.Compilation.SyntaxTrees.First());
        type = originalBinding.GetSpeculativeTypeInfo(0, type.ToTypeSyntax(), SpeculativeBindingOption.BindAsTypeOrNamespace).Type as INamedTypeSymbol
            ?? throw new InvalidOperationException($"Unable to bind serialization construction type {type}.");
        if (!constructionTypes.Add(type) || ContainsTypeParameter(type) || type.ContainingType is { IsGenericType: true }
            || type.IsAbstract || type.TypeKind == TypeKind.Interface
            || !type.HasAttribute(services.LibraryTypes.GenerateSerializerAttribute))
        {
            return null;
        }

        var registration = new Registration(type);
        var inspectionCompilation = services.Compilation;
        var inspectionType = type;
        var inspectionLibrary = services.LibraryTypes;
        if (!SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, services.Compilation.Assembly))
        {
            inspectionCompilation = services.Compilation.WithOptions(services.Compilation.Options.WithMetadataImportOptions(MetadataImportOptions.All));
            var binding = inspectionCompilation.GetSemanticModel(inspectionCompilation.SyntaxTrees.First());
            inspectionType = binding.GetSpeculativeTypeInfo(0, type.ToTypeSyntax(), SpeculativeBindingOption.BindAsTypeOrNamespace).Type as INamedTypeSymbol
                ?? throw new InvalidOperationException($"Unable to bind referenced serialization type {type}.");
            inspectionLibrary = LibraryTypes.FromCompilation(inspectionCompilation, services.Options);
        }
        ISerializableTypeDescription? constructionModel;
        try
        {
            if (SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, services.Compilation.Assembly))
            {
                constructionModel = SerializableSourceOutputGenerator.CreateSerializableTypeDescription(services, type);
            }
            else
            {
                // Reference assemblies preserve constructor contracts, but can omit serialized private fields.
                var fields = new FieldIdAssignmentHelper(inspectionType, [], GenerateFieldIds.None, inspectionLibrary);
                constructionModel = new SerializableTypeDescription(inspectionCompilation, inspectionType, false,
                    SerializableSourceOutputGenerator.GetDataMembers(fields), inspectionLibrary);
            }
        }
        catch (OrleansGeneratorDiagnosticAnalysisException)
        {
            return null;
        }
        if (constructionModel is null)
        {
            return null;
        }

        var requiresActivator = false;
        INamedTypeSymbol? referencedActivator = null;
        var codecDependencies = new List<string>();
        var copierDependencies = new List<string>();
        if (SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, services.Compilation.Assembly))
        {
            var model = type.IsGenericType
                ? SerializableSourceOutputGenerator.CreateSerializableTypeDescription(services, type.OriginalDefinition)!
                : constructionModel;
            DescribeGeneratedModel(registration, type, model);
            var codecConstruction = new SerializerGenerator(services).GetConstruction(model);
            var copierConstruction = model.IsShallowCopyable ? null : new CopierGenerator(services).GetConstruction(model);
            registration.CodecConstruction = ConstructGenerated(registration.Codec, codecConstruction, model, type);
            registration.CopierConstruction = copierConstruction is null
                ? $"new {registration.Copier}()"
                : ConstructGenerated(registration.Copier, copierConstruction, model, type);
            requiresActivator = model.UseActivator;
            codecDependencies.AddRange(ConstructorDependencies(codecConstruction, model, type));
            if (copierConstruction is not null)
                copierDependencies.AddRange(ConstructorDependencies(copierConstruction, model, type));
        }
        else
        {
            var generatedNamespace = SerializerGenerator.GetGeneratedNamespaceName(type);
            var codecType = ResolveModelImplementation(SerializerGenerator.GetSimpleClassName(type.Name));
            if (codecType is null)
            {
                return null;
            }

            registration.Codec = Name(codecType);
            registration.CodecConstruction = ConstructReferenced(registration.Codec, codecType);
            codecDependencies.AddRange(ConstructorDependencies(codecType));
            requiresActivator = HasActivatorDependency(codecType);
            if (services.LibraryTypes.IsShallowCopyable(type))
            {
                registration.Copier = $"global::Orleans.Serialization.Cloning.ShallowCopier<{Name(type)}>";
                registration.CopierConstruction = $"new {registration.Copier}()";
            }
            else
            {
                var copierType = ResolveModelImplementation(CopierGenerator.GetSimpleClassName(type.Name));
                if (copierType is null)
                {
                    return null;
                }

                registration.Copier = Name(copierType);
                registration.CopierConstruction = ConstructReferenced(registration.Copier, copierType);
                copierDependencies.AddRange(ConstructorDependencies(copierType));
                requiresActivator |= HasActivatorDependency(copierType);
            }

            INamedTypeSymbol? ResolveModelImplementation(string name)
            {
                var arity = type.TypeArguments.Length;
                var definition = inspectionCompilation.GetTypeByMetadataName($"{generatedNamespace}.{name}{(arity > 0 ? $"`{arity}" : "")}");
                return definition is null || arity == 0 ? definition : definition.Construct([.. type.TypeArguments]);
            }
            if (requiresActivator)
                referencedActivator = ResolveModelImplementation(ActivatorGenerator.GetSimpleClassName(constructionModel));
        }

        if (requiresActivator
            && (type.HasAttribute(services.LibraryTypes.UseActivatorAttribute) && !constructionModel.HasActivatorConstructor && referencedActivator is null
                || constructionModel.HasActivatorConstructor && !SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, services.Compilation.Assembly)
                    && (referencedActivator is null || !inspectionCompilation.IsSymbolAccessibleWithin(referencedActivator, inspectionCompilation.Assembly))))
        {
            return null;
        }

        var responseType = services.Compilation.GetTypeByMetadataName("Orleans.Serialization.Invocation.Response`1")!.Construct(type);
        var codec = $"global::Orleans.Serialization.Invocation.PooledResponseCodec<{Name(type)}, {registration.Codec}>";
        var copier = $"global::Orleans.Serialization.Invocation.PooledResponseCopier<{Name(type)}, {registration.Copier}>";
        var result = new StringBuilder();
        foreach (var member in constructionModel.Members)
        {
            var memberName = Name(member.Type);
            if (member.IsSerializable)
                codecDependencies.Add($"global::Orleans.Serialization.Codecs.IFieldCodec<{memberName}>");
            if (member.IsCopyable && !services.LibraryTypes.IsShallowCopyable(type))
                copierDependencies.Add($"global::Orleans.Serialization.Cloning.IDeepCopier<{memberName}>");
        }
        foreach (var memberType in constructionModel.Members
            .Where(static member => member.IsSerializable || member.IsCopyable)
            .Select(static member => member.Type)
            .Distinct<ITypeSymbol>(SymbolEqualityComparer.Default))
        {
            var originalType = originalBinding.GetSpeculativeTypeInfo(0, memberType.ToTypeSyntax(), SpeculativeBindingOption.BindAsTypeOrNamespace).Type
                ?? throw new InvalidOperationException($"Unable to bind serialization construction dependency {memberType}.");
            AppendConstructionDependency(services, originalType, cancellationToken, constructionTypes, result);
        }

        if (referencedActivator is not null)
        {
            result.Append("options.AddDefaultSerializerService<global::Orleans.Serialization.Activators.IActivator<")
                .Append(Name(type)).Append(">, ").Append(Name(referencedActivator)).Append(">(static provider => ")
                .Append(ConstructReferenced(Name(referencedActivator), referencedActivator))
                .Append(", dependencies: ").Append(DefaultServiceDependencies(ConstructorDependencies(referencedActivator))).AppendLine(");");
        }
        else if (requiresActivator && constructionModel.HasActivatorConstructor
            && SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, services.Compilation.Assembly))
        {
            var activatorName = $"global::{constructionModel.GeneratedNamespace}.{ActivatorGenerator.GetSimpleClassName(constructionModel)}";
            if (type.IsGenericType)
                activatorName += $"<{string.Join(", ", type.TypeArguments.Select(Name))}>";
            var activatorModel = type.IsGenericType
                ? SerializableSourceOutputGenerator.CreateSerializableTypeDescription(services, type.OriginalDefinition)!
                : constructionModel;
            var activatorParams = activatorModel.ActivatorConstructorParameters
                .Select(parameter => SpecializeGeneratedSyntax(parameter, activatorModel, type))
                .ToList();
            var activatorConstruction = $"new {activatorName}({string.Join(", ", activatorParams.Select(static parameter => Resolve(CodeText.Type(parameter))))})";
            result.Append("options.AddDefaultSerializerService<global::Orleans.Serialization.Activators.IActivator<")
                .Append(Name(type)).Append(">, ").Append(activatorName).Append(">(static provider => ")
                .Append(activatorConstruction)
                .Append(", dependencies: ").Append(DefaultServiceDependencies(activatorParams.Select(static parameter => CodeText.Type(parameter)))).AppendLine(");");
        }
        else if (requiresActivator && !type.HasAttribute(services.LibraryTypes.UseActivatorAttribute)
            && !constructionModel.HasActivatorConstructor)
        {
            var factory = type.IsValueType ? "CreateDefaultValueTypeActivator" : "CreateDefaultReferenceTypeActivator";
            result.Append("options.AddDefaultSerializerService<global::Orleans.Serialization.Activators.IActivator<")
                .Append(Name(type)).Append(">, global::Orleans.Serialization.Activators.IActivator<").Append(Name(type))
                .Append(">>(static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.")
                .Append(factory).Append('<').Append(Name(type)).AppendLine(">());");
        }

        result.Append("options.AddDefaultSerializerService<").Append(registration.Codec).Append(">(static provider => ")
            .Append(registration.CodecConstruction)
            .Append(", dependencies: ").Append(DefaultServiceDependencies(codecDependencies)).AppendLine(");");
        result.Append("options.AddDefaultSerializerService<").Append(registration.Copier).Append(">(static provider => ")
            .Append(registration.CopierConstruction)
            .Append(", dependencies: ").Append(DefaultServiceDependencies(copierDependencies)).AppendLine(");");
        result.Append("options.AddDefaultSerializerService<global::Orleans.Serialization.Codecs.IFieldCodec<")
            .Append(Name(type)).Append(">, ").Append(registration.Codec).Append(">(static provider => ")
            .Append(Resolve(registration.Codec))
            .Append(", dependencies: ").Append(DefaultServiceDependencies(codecDependencies)).AppendLine(");");
        result.Append("options.AddDefaultSerializerService<global::Orleans.Serialization.Cloning.IDeepCopier<")
            .Append(Name(type)).Append(">, ").Append(registration.Copier).Append(">(static provider => ")
            .Append(Resolve(registration.Copier))
            .Append(", dependencies: ").Append(DefaultServiceDependencies(copierDependencies)).AppendLine(");");
        if (type.IsValueType && type.TypeKind != TypeKind.Enum)
        {
            result.Append("options.AddDefaultSerializerService<global::Orleans.Serialization.Serializers.IValueSerializer<")
                .Append(Name(type)).Append(">, ").Append(registration.Codec).Append(">(static provider => ")
                .Append(Resolve(registration.Codec))
                .Append(", dependencies: ").Append(DefaultServiceDependencies(codecDependencies)).AppendLine(");");
        }
        if (!includeResponse)
        {
            return new Graph(new Dictionary<ITypeSymbol, Registration>(SymbolEqualityComparer.Default) { [type] = registration }, result.ToString());
        }

        result.Append("options.AddDefaultSerializerService<").Append(codec).Append(">(static provider => new ")
            .Append(codec).Append("(caller => ").Append(Resolve(registration.Codec, "caller"))
            .Append("), dependencies: ").Append(DefaultServiceDependencies([registration.Codec])).AppendLine(");");
        result.Append("options.AddDefaultSerializerService<").Append(copier).Append(">(static provider => new ")
            .Append(copier).Append("(caller => ").Append(Resolve(registration.Copier, "caller"))
            .Append("), dependencies: ").Append(DefaultServiceDependencies([registration.Copier])).AppendLine(");");
        result.Append("options.AddDefaultSerializer<").Append(Name(responseType)).Append(", ").Append(codec).Append(", ").Append(copier).Append(">(static provider => ")
            .Append(Resolve(codec)).Append(", static provider => ").Append(Resolve(copier))
            .Append(", codecDependencies: ").Append(DefaultServiceDependencies([registration.Codec]))
            .Append(", copierDependencies: ").Append(DefaultServiceDependencies([registration.Copier])).AppendLine(");");
        result.Append("options.AddAllowedType(typeof(").Append(Name(responseType)).AppendLine("));");
        return new Graph(new Dictionary<ITypeSymbol, Registration>(SymbolEqualityComparer.Default) { [type] = registration }, result.ToString());

        bool HasActivatorDependency(INamedTypeSymbol implementation)
            => implementation.InstanceConstructors.SelectMany(static constructor => constructor.Parameters)
                .Any(parameter => parameter.Type is INamedTypeSymbol dependency
                    && SymbolEqualityComparer.Default.Equals(dependency.OriginalDefinition, inspectionLibrary.IActivator_1)
                    && SymbolEqualityComparer.Default.Equals(dependency.TypeArguments[0], inspectionType));

    }

    internal static Graph CreateRpcConstructionRoot(IGeneratorServices services, ITypeSymbol type, CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        AppendConstructionDependency(services, type, cancellationToken, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default), result);
        return new Graph(new Dictionary<ITypeSymbol, Registration>(SymbolEqualityComparer.Default), result.ToString());
    }

    private static void AppendConstructionDependency(IGeneratorServices services, ITypeSymbol dependency, CancellationToken cancellationToken,
        HashSet<ITypeSymbol> constructionTypes, StringBuilder result)
    {
        cancellationToken.ThrowIfCancellationRequested();
        dependency = dependency.WithNullableAnnotation(NullableAnnotation.None);
        if (constructionTypes.Contains(dependency)) return;
        if (TryCreate(services, [dependency], cancellationToken, out var finite, out _, useDefaultFactories: true)
            && !finite.Registrations.Keys.OfType<INamedTypeSymbol>().Any(named =>
                SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, services.Compilation.GetTypeByMetadataName("System.Collections.Generic.Dictionary`2"))))
        {
            foreach (var registered in finite.Registrations.Keys) constructionTypes.Add(registered);
            result.AppendLine(finite.ConfigurationStatements);
            return;
        }

        if (dependency is INamedTypeSymbol modelType && modelType.HasAttribute(services.LibraryTypes.GenerateSerializerAttribute)
            && CreateRpcModelRoot(services, modelType, cancellationToken, constructionTypes, includeResponse: false) is { } modelGraph)
        {
            result.AppendLine(modelGraph.ConfigurationStatements);
            return;
        }
        else
        {
            constructionTypes.Add(dependency);
        }

        if (dependency is INamedTypeSymbol tupleType && TryGetTupleServices(services, tupleType, out var tupleCodec, out var tupleCopier))
        {
            foreach (var element in tupleType.TypeArguments) AppendConstructionDependency(services, element, cancellationToken, constructionTypes, result);
            result.Append("options.AddDefaultSerializerService<").Append(Name(tupleCodec)).Append(">(static provider => ")
                .Append(ConstructReferenced(Name(tupleCodec), tupleCodec))
                .Append(", dependencies: ").Append(DefaultServiceDependencies(ConstructorDependencies(tupleCodec))).AppendLine(");");
            result.Append("options.AddDefaultSerializerService<").Append(Name(tupleCopier)).Append(">(static provider => ")
                .Append(ConstructReferenced(Name(tupleCopier), tupleCopier))
                .Append(", dependencies: ").Append(DefaultServiceDependencies(ConstructorDependencies(tupleCopier))).AppendLine(");");
            result.Append("options.AddDefaultSerializer<").Append(Name(tupleType)).Append(", ").Append(Name(tupleCodec)).Append(", ").Append(Name(tupleCopier)).Append(">(static provider => ")
                .Append(Resolve(Name(tupleCodec))).Append(", static provider => ").Append(Resolve(Name(tupleCopier)))
                    .Append(", codecDependencies: ").Append(DefaultServiceDependencies(ConstructorDependencies(tupleCodec)))
                    .Append(", copierDependencies: ").Append(DefaultServiceDependencies(ConstructorDependencies(tupleCopier))).AppendLine(");");
            return;
        }

        if (dependency is IArrayTypeSymbol { IsSZArray: true } arrayType)
        {
            AppendConstructionDependency(services, arrayType.ElementType, cancellationToken, constructionTypes, result);
            var codec = services.LibraryTypes.ArrayCodec.Construct(arrayType.ElementType);
            var copier = services.LibraryTypes.ArrayCopier.Construct(arrayType.ElementType);
            result.Append("options.AddDefaultSerializerService<").Append(Name(codec)).Append(">(static provider => ")
                .Append(ConstructReferenced(Name(codec), codec))
                .Append(", dependencies: ").Append(DefaultServiceDependencies(ConstructorDependencies(codec))).AppendLine(");");
            result.Append("options.AddDefaultSerializerService<").Append(Name(copier)).Append(">(static provider => ")
                .Append(ConstructReferenced(Name(copier), copier))
                .Append(", dependencies: ").Append(DefaultServiceDependencies(ConstructorDependencies(copier))).AppendLine(");");
            result.Append("options.AddDefaultSerializer<").Append(Name(arrayType)).Append(", ").Append(Name(codec)).Append(", ").Append(Name(copier)).Append(">(static provider => ")
                .Append(Resolve(Name(codec))).Append(", static provider => ").Append(Resolve(Name(copier)))
                    .Append(", codecDependencies: ").Append(DefaultServiceDependencies(ConstructorDependencies(codec)))
                    .Append(", copierDependencies: ").Append(DefaultServiceDependencies(ConstructorDependencies(copier))).AppendLine(");");
            return;
        }

        if (dependency is INamedTypeSymbol collection
            && TryGetConstructionCollectionServices(services, collection, out var collectionCodec, out var collectionCopier))
        {
            var codec = collectionCodec;
            var copier = collectionCopier;
            foreach (var argument in collection.TypeArguments) AppendConstructionDependency(services, argument, cancellationToken, constructionTypes, result);
            foreach (var parameter in codec.InstanceConstructors.SelectMany(static constructor => constructor.Parameters))
            {
                if (parameter.Type is INamedTypeSymbol contract
                    && (SymbolEqualityComparer.Default.Equals(contract.OriginalDefinition, services.LibraryTypes.FieldCodec_1)
                        || SymbolEqualityComparer.Default.Equals(contract.OriginalDefinition, services.LibraryTypes.ValueSerializer)))
                    AppendConstructionDependency(services, contract.TypeArguments[0], cancellationToken, constructionTypes, result);
            }
            result.Append("options.AddDefaultSerializerService<").Append(Name(codec)).Append(">(static provider => ")
                .Append(ConstructReferenced(Name(codec), codec, preferCompleteConstructor: true))
                .Append(", dependencies: ").Append(DefaultServiceDependencies(ConstructorDependencies(codec, preferCompleteConstructor: true))).AppendLine(");");
            result.Append("options.AddDefaultSerializerService<").Append(Name(copier)).Append(">(static provider => ")
                .Append(ConstructReferenced(Name(copier), copier))
                .Append(", dependencies: ").Append(DefaultServiceDependencies(ConstructorDependencies(copier))).AppendLine(");");
            result.Append("options.AddDefaultSerializer<").Append(Name(collection)).Append(", ").Append(Name(codec)).Append(", ").Append(Name(copier)).Append(">(static provider => ")
                .Append(Resolve(Name(codec))).Append(", static provider => ").Append(Resolve(Name(copier)))
                .Append(", codecDependencies: ").Append(DefaultServiceDependencies(ConstructorDependencies(codec, preferCompleteConstructor: true)))
                .Append(", copierDependencies: ").Append(DefaultServiceDependencies(ConstructorDependencies(copier))).AppendLine(");");
            return;
        }

        var name = Name(dependency);
        IEnumerable<ITypeSymbol> bridgeDependencies = dependency switch
        {
            INamedTypeSymbol bridge => bridge.TypeArguments.AsEnumerable(),
            IArrayTypeSymbol bridge => [bridge.ElementType],
            _ => []
        };
        // Metadata bridges add service contracts, while type dispatch stays with ordinary metadata.
        result.Append("options.AddDefaultSerializerService<global::Orleans.Serialization.Codecs.IFieldCodec<")
            .Append(name).Append(">>(static provider => provider.GetCodec<").Append(name)
            .Append(">(), dependencies: ").Append(DefaultDependencyServices(bridgeDependencies, codec: true)).AppendLine(");");
        result.Append("options.AddDefaultSerializerService<global::Orleans.Serialization.Cloning.IDeepCopier<")
            .Append(name).Append(">>(static provider => provider.GetDeepCopier<").Append(name)
            .Append(">(), dependencies: ").Append(DefaultDependencyServices(bridgeDependencies, codec: false)).AppendLine(");");
        if (dependency is INamedTypeSymbol named)
        {
            foreach (var argument in named.TypeArguments) AppendConstructionDependency(services, argument, cancellationToken, constructionTypes, result);
            if (services.LibraryTypes.WellKnownCodecs.FindByUnderlyingType(named.OriginalDefinition) is { } knownCodec
                && knownCodec.CodecType.IsGenericType)
            {
                var codec = knownCodec.CodecType.Construct([.. named.TypeArguments]);
                foreach (var parameter in codec.InstanceConstructors.SelectMany(static constructor => constructor.Parameters))
                {
                    if (parameter.Type is INamedTypeSymbol service
                        && (SymbolEqualityComparer.Default.Equals(service.OriginalDefinition, services.LibraryTypes.FieldCodec_1)
                            || SymbolEqualityComparer.Default.Equals(service.OriginalDefinition, services.LibraryTypes.DeepCopier_1)))
                    {
                        AppendConstructionDependency(services, service.TypeArguments[0], cancellationToken, constructionTypes, result);
                    }
                }
            }
        }
        else if (dependency is IArrayTypeSymbol array)
        {
            AppendConstructionDependency(services, array.ElementType, cancellationToken, constructionTypes, result);
        }
    }

    internal static bool TryCreate(
        IGeneratorServices services,
        IEnumerable<ITypeSymbol> roots,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out Graph? graph,
        [NotNullWhen(false)] out Failure? failure,
        bool useDefaultFactories = false)
        => TryCreate(services, roots, [], cancellationToken, out graph, out failure, useDefaultFactories);

    internal static bool TryCreate(
        IGeneratorServices services,
        IEnumerable<ITypeSymbol> roots,
        IReadOnlyCollection<ITypeSymbol> providedRoots,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out Graph? graph,
        [NotNullWhen(false)] out Failure? failure,
        bool useDefaultFactories = false)
    {
        graph = null;
        failure = null;
        var implementationCompilation = services.Compilation is CSharpCompilation csharp
            ? csharp.WithOptions(csharp.Options.WithMetadataImportOptions(MetadataImportOptions.All))
            : services.Compilation;
        var registrations = new Dictionary<ITypeSymbol, Registration>(SymbolEqualityComparer.Default);
        foreach (var root in providedRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var type = root.WithNullableAnnotation(NullableAnnotation.None);
            if (registrations.ContainsKey(type)) continue;
            if (registrations.Count >= 1024)
            {
                failure = new(type, "the dependency graph exceeds 1024 closed types; declare a finite serialization graph");
                return false;
            }
            registrations.Add(type, new Registration(type, isProvided: true));
        }
        var pending = new Queue<ITypeSymbol>(roots);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var type = pending.Dequeue().WithNullableAnnotation(NullableAnnotation.None);
            if (registrations.ContainsKey(type)) continue;
            if (registrations.Count >= 1024)
            {
                failure = new(type, "the dependency graph exceeds 1024 closed types; declare a finite serialization graph");
                return false;
            }
            var registration = new Registration(type);
            if (Describe(registration, services, implementationCompilation, cancellationToken, out var requiresImplementation) is { } reason)
            {
                if (!requiresImplementation)
                {
                    failure = new(type, reason);
                    return false;
                }

                // Keep checking contracts whose types are available even when runtime metadata is omitted.
                failure ??= new(type, reason, RequiresImplementation: true);
            }
            registrations.Add(type, registration);
            foreach (var dependency in registration.Dependencies) pending.Enqueue(dependency);
        }

        if (failure is not null)
        {
            if (!useDefaultFactories)
            {
                var metadata = new SourceWriter(initialIndentation: ContextStatementIndentation);
                var visited = new HashSet<ITypeSymbol>(providedRoots, SymbolEqualityComparer.Default);
                foreach (var type in registrations.Keys)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (AppendTypeMetadata(metadata, type, services.LibraryTypes, visited) is { } metadataFailure)
                    {
                        failure = metadataFailure;
                        break;
                    }
                }
            }

            return false;
        }

        var result = new SourceWriter(initialIndentation: ContextStatementIndentation);
        foreach (var registration in registrations.Values) ResolveResponseImplementations(registration);
        var addService = useDefaultFactories ? "AddDefaultSerializerService" : "AddSerializerService";
        var addSerializer = useDefaultFactories ? "AddDefaultSerializer" : "AddSerializer";
        var metadataTypes = new HashSet<ITypeSymbol>(providedRoots, SymbolEqualityComparer.Default);
        var auxiliaryServices = new HashSet<string>(StringComparer.Ordinal);

        foreach (var registration in registrations.Values.OrderBy(value => Name(value.Type), StringComparer.Ordinal))
        {
            if (registration.IsProvided) continue;
            var typeName = Name(registration.Type);
            var hasBaseCodec = registration.HasBaseCodec;
            var hasBaseCopier = registration.HasBaseCopier;
            var codecDependencies = new List<string>(registration.CodecDependencies);
            var copierDependencies = new List<string>(registration.CopierDependencies);
            if (registration.ReferencedCodec is { } referencedCodec)
            {
                var implementationLibrary = LibraryTypes.FromCompilation(implementationCompilation, services.Options);
                hasBaseCodec = referencedCodec.AllInterfaces.Any(contract =>
                    SymbolEqualityComparer.Default.Equals(contract.OriginalDefinition, implementationLibrary.BaseCodec_1));
                hasBaseCopier = registration.ReferencedCopier?.AllInterfaces.Any(contract =>
                    SymbolEqualityComparer.Default.Equals(contract.OriginalDefinition, implementationLibrary.BaseCopier_1)) == true;
                registration.CodecConstruction = ConstructReferenced(registration.Codec, referencedCodec);
                codecDependencies.AddRange(ConstructorDependencies(referencedCodec));
                registration.CopierConstruction = registration.ReferencedCopier is { } referencedCopier
                    ? ConstructReferenced(registration.Copier, referencedCopier)
                    : $"new {registration.Copier}()";
                if (registration.ReferencedCopier is { } copierType)
                    copierDependencies.AddRange(ConstructorDependencies(copierType));
            }
            else if (registration.Model is null && registration.Dependencies.Count > 0)
            {
                var codecArguments = new List<string>();
                var copierArguments = new List<string>();
                foreach (var dependency in registration.Dependencies)
                {
                    var target = registrations[dependency.WithNullableAnnotation(NullableAnnotation.None)];
                    var cyclic = MayReach(target, registration.Type, registrations, new(SymbolEqualityComparer.Default));
                    var consumesConcreteService = (!cyclic && useDefaultFactories) || registration.ResponseResult is not null;
                    codecDependencies.Add(consumesConcreteService
                        ? target.Codec
                        : $"global::Orleans.Serialization.Codecs.IFieldCodec<{Name(dependency)}>");
                    copierDependencies.Add(consumesConcreteService
                        ? target.Copier
                        : $"global::Orleans.Serialization.Cloning.IDeepCopier<{Name(dependency)}>");
                    codecArguments.Add(cyclic && registration.ResponseResult is not null
                        ? $"caller => {Resolve(target.Codec, "caller")}"
                        : cyclic
                        ? $"CreateCodecHolder<{Name(dependency)}>(provider)"
                        : useDefaultFactories || registration.ResponseResult is not null ? Resolve(target.Codec) : $"provider.GetCodec<{Name(dependency)}>()");
                    copierArguments.Add(cyclic && registration.ResponseResult is not null
                        ? $"caller => {Resolve(target.Copier, "caller")}"
                        : cyclic
                        ? $"CreateCopierHolder<{Name(dependency)}>(provider)"
                        : useDefaultFactories || registration.ResponseResult is not null ? Resolve(target.Copier) : $"provider.GetDeepCopier<{Name(dependency)}>()");
                }

                registration.CodecConstruction = $"new {registration.Codec}({string.Join(", ", codecArguments)})";
                registration.CopierConstruction = $"new {registration.Copier}({string.Join(", ", copierArguments)})";
            }

            var codecServiceDeps = useDefaultFactories && codecDependencies.Count > 0
                ? $", dependencies: {DefaultServiceDependencies(codecDependencies)}"
                : "";
            var copierServiceDeps = useDefaultFactories && copierDependencies.Count > 0
                ? $", dependencies: {DefaultServiceDependencies(copierDependencies)}"
                : "";
            result.WriteLine($"options.{addService}<{registration.Codec}>(static provider => {registration.CodecConstruction}{codecServiceDeps});");
            result.WriteLine($"options.{addService}<{registration.Copier}>(static provider => {registration.CopierConstruction}{copierServiceDeps});");
            var serializerTypeArgs = useDefaultFactories
                ? $"{typeName}, {registration.Codec}, {registration.Copier}"
                : typeName;
            var serializerDeps = useDefaultFactories && (codecDependencies.Count > 0 || copierDependencies.Count > 0)
                ? $", codecDependencies: {DefaultServiceDependencies(codecDependencies)}, copierDependencies: {DefaultServiceDependencies(copierDependencies)}"
                : "";
            result.WriteLine($"options.{addSerializer}<{serializerTypeArgs}>(static provider => {Resolve(registration.Codec)}, static provider => {Resolve(registration.Copier)}{serializerDeps});");
            if (registration.Model is { IsValueType: true, IsEnumType: false })
            {
                var valueServiceArgs = useDefaultFactories
                    ? $"global::Orleans.Serialization.Serializers.IValueSerializer<{typeName}>, {registration.Codec}"
                    : $"global::Orleans.Serialization.Serializers.IValueSerializer<{typeName}>";
                result.WriteLine($"options.{addService}<{valueServiceArgs}>(static provider => {Resolve(registration.Codec)});");
            }
            if (hasBaseCodec)
            {
                var baseCodecArgs = useDefaultFactories
                    ? $"global::Orleans.Serialization.Serializers.IBaseCodec<{typeName}>, {registration.Codec}"
                    : $"global::Orleans.Serialization.Serializers.IBaseCodec<{typeName}>";
                var baseCodecDeps = useDefaultFactories
                    ? $", dependencies: {DefaultServiceDependencies(codecDependencies)}"
                    : "";
                result.WriteLine($"options.{addService}<{baseCodecArgs}>(static provider => {Resolve(registration.Codec)}{baseCodecDeps});");
            }
            if (hasBaseCopier)
            {
                var baseCopierArgs = useDefaultFactories
                    ? $"global::Orleans.Serialization.Cloning.IBaseCopier<{typeName}>, {registration.Copier}"
                    : $"global::Orleans.Serialization.Cloning.IBaseCopier<{typeName}>";
                var baseCopierDeps = useDefaultFactories
                    ? $", dependencies: {DefaultServiceDependencies(copierDependencies)}"
                    : "";
                result.WriteLine($"options.{addService}<{baseCopierArgs}>(static provider => {Resolve(registration.Copier)}{baseCopierDeps});");
            }

            foreach (var array in registration.CanonicalArrays)
            {
                var arrayRegistration = registrations[array.WithNullableAnnotation(NullableAnnotation.None)];
                var element = registrations[array.ElementType.WithNullableAnnotation(NullableAnnotation.None)];
                var canonicalCodec = Name(services.LibraryTypes.ArrayCodec.Construct(array.ElementType));
                var canonicalCopier = Name(services.LibraryTypes.ArrayCopier.Construct(array.ElementType));
                var cyclic = MayReach(element, registration.Type, registrations, new(SymbolEqualityComparer.Default));
                if (arrayRegistration.Codec != canonicalCodec && auxiliaryServices.Add(canonicalCodec))
                {
                    var codecDependency = cyclic ? $"CreateCodecHolder<{Name(array.ElementType)}>(provider)" : $"provider.GetCodec<{Name(array.ElementType)}>()";
                    result.WriteLine($"options.{addService}<{canonicalCodec}>(static provider => new {canonicalCodec}({codecDependency}));");
                }
                if (arrayRegistration.Copier != canonicalCopier && auxiliaryServices.Add(canonicalCopier))
                {
                    var copierDependency = cyclic ? $"CreateCopierHolder<{Name(array.ElementType)}>(provider)" : $"provider.GetDeepCopier<{Name(array.ElementType)}>()";
                    result.WriteLine($"options.{addService}<{canonicalCopier}>(static provider => new {canonicalCopier}({copierDependency}));");
                }
            }

            result.WriteLine($"options.AddAllowedType(typeof({typeName}));");
            if (!useDefaultFactories && AppendTypeMetadata(result, registration.Type, services.LibraryTypes, metadataTypes) is { } metadataFailure)
            {
                failure = metadataFailure;
                return false;
            }
        }

        graph = new Graph(registrations, result.ToString());
        return true;

        void ResolveResponseImplementations(Registration registration)
        {
            if (registration.ResponseResult is not { } resultType || registration.Codec.Length > 0) return;
            var target = registrations[resultType.WithNullableAnnotation(NullableAnnotation.None)];
            ResolveResponseImplementations(target);
            registration.Codec = $"global::Orleans.Serialization.Invocation.PooledResponseCodec<{Name(resultType)}, {target.Codec}>";
            registration.Copier = $"global::Orleans.Serialization.Invocation.PooledResponseCopier<{Name(resultType)}, {target.Copier}>";
        }
    }

    private static string DefaultDependencyServices(IEnumerable<ITypeSymbol> dependencies, bool codec)
        => DefaultServiceDependencies(dependencies.Select(dependency =>
            $"global::Orleans.Serialization.{(codec ? "Codecs.IFieldCodec" : "Cloning.IDeepCopier")}<{Name(dependency)}>"));

    private static string DefaultServiceDependencies(IEnumerable<string> dependencies)
        => "new global::System.Type[] { " + string.Join(", ", dependencies
            .Where(static type => type != "global::Orleans.Serialization.Serializers.ICodecProvider")
            .Distinct(StringComparer.Ordinal).Select(static type => $"typeof({type})")) + " }";

    private static IEnumerable<string> ConstructorDependencies(INamedTypeSymbol implementation, bool preferCompleteConstructor = false)
        => GetReferencedConstructor(implementation, preferCompleteConstructor).Parameters.Select(static parameter => Name(parameter.Type));

    private static IEnumerable<string> ConstructorDependencies(
        GeneratedTypeConstruction construction,
        ISerializableTypeDescription definition,
        INamedTypeSymbol closedType)
        => construction.GetServiceDependencies()
            .Select(dependency => CodeText.Type(SpecializeGeneratedSyntax(dependency, definition, closedType)));

    private static string? Describe(
        Registration registration, IGeneratorServices services, Compilation implementationCompilation,
        CancellationToken cancellationToken, out bool requiresImplementation)
    {
        requiresImplementation = false;
        var type = registration.Type;
        if (type is INamedTypeSymbol { IsUnboundGenericType: true } || ContainsTypeParameter(type))
            return "use a closed type with explicit generic arguments";
        if (services.LibraryTypes.StaticCodecs.FindByUnderlyingType(type) is { } primitive
            && type.SpecialType != SpecialType.System_Object)
        {
            if (primitive.CodecType.InstanceConstructors.All(constructor => constructor.Parameters.Length > 0))
                return "this built-in type requires runtime service configuration";
            var copier = services.LibraryTypes.StaticCopiers.FindByUnderlyingType(type);
            if (copier is null && !services.LibraryTypes.IsShallowCopyable(type))
                return "this built-in type requires a statically registered deep copier";
            registration.Codec = Name(primitive.CodecType);
            registration.Copier = copier is { }
                ? Name(copier.CopierType)
                : $"global::Orleans.Serialization.Cloning.ShallowCopier<{Name(type)}>";
            registration.CodecConstruction = $"new {registration.Codec}()";
            registration.CopierConstruction = $"new {registration.Copier}()";
            return null;
        }

        if (type is IArrayTypeSymbol array)
        {
            if (!array.IsSZArray) return "use a single-dimensional zero-based array";
            registration.Codec = $"global::Orleans.Serialization.Codecs.ArrayCodec<{Name(array.ElementType)}>";
            registration.Copier = $"global::Orleans.Serialization.Codecs.ArrayCopier<{Name(array.ElementType)}>";
            registration.Dependencies.Add(array.ElementType);
            return null;
        }

        if (type is not INamedTypeSymbol named || named.IsRefLikeType)
            return "use a serializable class, struct, enum, or supported collection";
        if (named.ContainingType is { IsGenericType: true })
            return "use a model declared outside a generic containing type";
        var definition = named.OriginalDefinition.ToDisplayString();
        if (definition == "Orleans.Serialization.Invocation.Response<TResult>")
        {
            registration.ResponseResult = named.TypeArguments[0];
            registration.Dependencies.Add(named.TypeArguments[0]);
            return null;
        }

        if (TryGetTupleServices(services, named, out var tupleCodec, out var tupleCopier))
        {
            registration.Codec = Name(tupleCodec);
            registration.Copier = Name(tupleCopier);
            registration.Dependencies.AddRange(named.TypeArguments);
            return null;
        }

        var collection = definition switch
        {
            "System.Collections.Generic.List<T>" => "List",
            "System.Collections.Generic.Dictionary<TKey, TValue>" => "Dictionary",
            "System.Collections.Generic.KeyValuePair<TKey, TValue>" => "KeyValuePair",
            "System.Nullable<T>" => "Nullable",
            _ => null
        };
        if (collection is not null)
        {
            var arguments = string.Join(", ", named.TypeArguments.Select(Name));
            registration.Codec = $"global::Orleans.Serialization.Codecs.{collection}Codec<{arguments}>";
            registration.Copier = $"global::Orleans.Serialization.Codecs.{collection}Copier<{arguments}>";
            registration.Dependencies.AddRange(named.TypeArguments);
            return null;
        }

        if (!named.HasAttribute(services.LibraryTypes.GenerateSerializerAttribute))
            return "apply GenerateSerializerAttribute to the model, or use List<T>, Dictionary<TKey, TValue>, Nullable<T>, or T[]";
        if (named.IsAbstract || named.TypeKind == TypeKind.Interface)
            return "declare concrete model types";
        if (LanguageFeatureSymbols.IsUnion(named))
            return "use a model which is not a union type";
        if (!SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, services.Compilation.Assembly))
            return DescribeReferenced(registration, named, services, implementationCompilation, cancellationToken, out requiresImplementation);
        ISerializableTypeDescription? model;
        try
        {
            model = SerializableSourceOutputGenerator.CreateSerializableTypeDescription(services, named);
        }
        catch (OrleansGeneratorDiagnosticAnalysisException exception)
        {
            return exception.Diagnostic.GetMessage();
        }
        if (model is null) return "the model has no generated serialization implementation";
        if (!HasSupportedModelConstruction(model))
            return "this context supports models with default construction, an object base, and generated member serialization";
        foreach (var member in model.Members)
        {
            if (!member.IsSerializable && !member.IsCopyable) continue;
            if (SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, services.Compilation.Assembly))
            {
                var generatedMember = new SerializerGenerator.SerializableMember(services, member);
                if (generatedMember.GetGetterFieldDescription() is { Initialization: not null }
                    || generatedMember.GetSetterFieldDescription() is { Initialization: not null })
                    return $"serialized member '{member.Symbol.Name}' requires statically generated field accessor support";
            }
            registration.Dependencies.Add(member.Type);
        }

        var argumentsSuffix = named.IsGenericType
            ? $"<{string.Join(", ", named.TypeArguments.Select(Name))}>"
            : "";
        var generatedNamespace = SerializerGenerator.GetGeneratedNamespaceName(named);
        registration.Codec = $"global::{generatedNamespace}.{SerializerGenerator.GetSimpleClassName(named.Name)}{argumentsSuffix}";
        var definitionModel = named.IsGenericType
            ? SerializableSourceOutputGenerator.CreateSerializableTypeDescription(services, named.OriginalDefinition)!
            : model;
        registration.Copier = definitionModel.IsShallowCopyable
            ? $"global::Orleans.Serialization.Cloning.ShallowCopier<{Name(type)}>"
            : $"global::{generatedNamespace}.{CopierGenerator.GetSimpleClassName(named.Name)}{argumentsSuffix}";
        registration.Model = definitionModel;
        var codecConstruction = new SerializerGenerator(services).GetConstruction(definitionModel);
        var copierConstruction = definitionModel.IsShallowCopyable ? null : new CopierGenerator(services).GetConstruction(definitionModel);
        registration.CodecConstruction = ConstructGenerated(registration.Codec, codecConstruction, definitionModel, named);
        registration.CopierConstruction = copierConstruction is null
            ? $"new {registration.Copier}()"
            : ConstructGenerated(registration.Copier, copierConstruction, definitionModel, named);
        registration.CodecDependencies.AddRange(ConstructorDependencies(codecConstruction, definitionModel, named));
        if (copierConstruction is not null)
            registration.CopierDependencies.AddRange(ConstructorDependencies(copierConstruction, definitionModel, named));
        registration.HasBaseCodec = codecConstruction.HasBaseContract;
        registration.HasBaseCopier = copierConstruction?.HasBaseContract == true;
        return AddCanonicalArrays(registration, named, definitionModel, codecConstruction, copierConstruction ?? new CopierGenerator(services).GetConstruction(definitionModel), services, cancellationToken);
    }

    private static string? AddCanonicalArrays(
        Registration registration, INamedTypeSymbol named, ISerializableTypeDescription definitionModel,
        GeneratedTypeConstruction codecConstruction, GeneratedTypeConstruction copierConstruction,
        IGeneratorServices services, CancellationToken cancellationToken)
    {
        var substitutions = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var (parameter, argument) in definitionModel.TypeParameters.Zip(named.GetAllTypeArguments(),
            static (parameter, argument) => (parameter.Parameter, argument)))
            substitutions.Add(parameter, argument);
        foreach (var request in codecConstruction.CanonicalArrays.Concat(copierConstruction.CanonicalArrays))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var binding = services.Compilation.GetSemanticModel(services.Compilation.SyntaxTrees.First());
            var canonicalArray = binding.GetSpeculativeTypeInfo(0, CloseType(request).ToTypeSyntax(),
                SpeculativeBindingOption.BindAsTypeOrNamespace).Type as IArrayTypeSymbol;
            if (canonicalArray is null)
            {
                return "provide complete model metadata for its canonical array service requests";
            }
            if (!registration.CanonicalArrays.Any(existing => SymbolEqualityComparer.Default.Equals(existing, canonicalArray)))
                registration.CanonicalArrays.Add(canonicalArray);
            registration.Dependencies.Add(canonicalArray);
            registration.Dependencies.Add(canonicalArray.ElementType);
        }

        return null;

        ITypeSymbol CloseType(ITypeSymbol symbol)
        {
            switch (symbol)
            {
                case ITypeParameterSymbol parameter:
                    return substitutions[parameter];
                case IArrayTypeSymbol array:
                    return services.Compilation.CreateArrayTypeSymbol(CloseType(array.ElementType), array.Rank);
                case INamedTypeSymbol { IsGenericType: true } generic:
                    var definition = generic is { TypeKind: not TypeKind.Error, ContainingType.IsGenericType: true }
                        ? ((INamedTypeSymbol)CloseType(generic.ContainingType)).GetTypeMembers(generic.Name, generic.Arity)
                            .Single(candidate => SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, generic.OriginalDefinition))
                        : generic.ConstructedFrom;
                    return generic.Arity > 0
                        ? definition.Construct([.. generic.TypeArguments.Select(CloseType)])
                        : definition;
                default:
                    return symbol;
            }
        }
    }

    private static string? DescribeReferenced(
        Registration registration, INamedTypeSymbol named, IGeneratorServices services,
        Compilation implementationCompilation,
        CancellationToken cancellationToken, out bool requiresImplementation)
    {
        requiresImplementation = false;
        var binding = implementationCompilation.GetSemanticModel(implementationCompilation.SyntaxTrees.First());
        var implementationType = binding.GetSpeculativeTypeInfo(0, named.ToTypeSyntax(),
            SpeculativeBindingOption.BindAsTypeOrNamespace).Type as INamedTypeSymbol;
        if (implementationType is null)
        {
            requiresImplementation = true;
            return "provide the referenced model's implementation metadata";
        }
        if (TypeMetadataIdentity.Create(implementationType) != TypeMetadataIdentity.Create(named))
        {
            return $"type '{named}' cannot be named unambiguously from generated code";
        }
        var library = LibraryTypes.FromCompilation(implementationCompilation, services.Options);
        // Contexts inspect only retained model header/base facts; all serialized dependencies below
        // come from complete generated C# services. This also covers F# model reference images,
        // whose compiler omits ReferenceAssemblyAttribute while retaining these declaration facts.
        if (SerializableSourceOutputGenerator.HasReferenceAssemblyAttribute(named.ContainingAssembly))
        {
            return "provide full implementation metadata for external serializer-context generation";
        }

        var model = new SerializableTypeDescription(implementationCompilation, implementationType,
            SerializableSourceOutputGenerator.ShouldIncludePrimaryConstructorParameters(implementationType, library), [], library);
        if (!HasSupportedModelConstruction(model))
            return "this context supports models with default construction, an object base, and generated member serialization";
        registration.Model = model;
        registration.ReferencedCodec = ResolveImplementation(SerializerGenerator.GetSimpleClassName(named.Name));
        registration.ReferencedCopier = ResolveImplementation(CopierGenerator.GetSimpleClassName(named.Name));
        var consumerBinding = services.Compilation.GetSemanticModel(services.Compilation.SyntaxTrees.First());
        if (registration.ReferencedCodec is null || registration.ReferencedCopier is null && !model.IsShallowCopyable)
        {
            requiresImplementation = true;
            var constructorParameters = SerializableSourceOutputGenerator.ResolveConstructorParameters(
                implementationType, model.IncludePrimaryConstructorParameters, library);
            var members = new FieldIdAssignmentHelper(
                implementationType,
                constructorParameters,
                SerializableSourceOutputGenerator.GetGenerateFieldIdsOptionFromType(implementationType, library),
                library);
            foreach (var member in SerializableSourceOutputGenerator.GetDataMembers(members))
            {
                if (!member.IsSerializable && !member.IsCopyable) continue;
                cancellationToken.ThrowIfCancellationRequested();
                var dependency = consumerBinding.GetSpeculativeTypeInfo(0, member.Type.ToTypeSyntax(),
                    SpeculativeBindingOption.BindAsTypeOrNamespace).Type;
                if (dependency is null)
                    return "provide complete referenced model metadata to discover the producer's serialization dependencies";
                registration.Dependencies.Add(dependency);
            }
            return "provide the referenced assembly's generated codec and copier implementations";
        }
        registration.Codec = Name(registration.ReferencedCodec);
        registration.Copier = registration.ReferencedCopier is { } referencedCopier
            ? Name(referencedCopier)
            : Name(services.LibraryTypes.ShallowCopier.Construct(named));

        foreach (var implementation in new[] { registration.ReferencedCodec, registration.ReferencedCopier })
        {
            if (implementation is null) continue;
            cancellationToken.ThrowIfCancellationRequested();
            if (SerializableSourceOutputGenerator.HasReferenceAssemblyAttribute(implementation.ContainingAssembly))
            {
                return "provide full generated codec and copier implementation metadata for external serializer-context generation";
            }

            foreach (var member in implementation.GetMembers())
            {
                IEnumerable<ITypeSymbol> serviceTypes = member switch
                {
                    IFieldSymbol field => new[] { field.Type },
                    IPropertySymbol property => new[] { property.Type },
                    IMethodSymbol method => method.Parameters.Select(static parameter => parameter.Type).Prepend(method.ReturnType),
                    _ => []
                };
                foreach (var serviceType in serviceTypes.OfType<INamedTypeSymbol>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var service = consumerBinding.GetSpeculativeTypeInfo(0, serviceType.ToTypeSyntax(),
                        SpeculativeBindingOption.BindAsTypeOrNamespace).Type as INamedTypeSymbol;
                    if (service is null) continue;
                    foreach (var contract in service.AllInterfaces.Prepend(service))
                    {
                        if (SymbolEqualityComparer.Default.Equals(contract.OriginalDefinition, services.LibraryTypes.FieldCodec_1)
                            || SymbolEqualityComparer.Default.Equals(contract.OriginalDefinition, services.LibraryTypes.DeepCopier_1))
                        {
                            registration.Dependencies.Add(contract.TypeArguments[0]);
                        }
                    }

                    if (SymbolEqualityComparer.Default.Equals(service.OriginalDefinition, services.LibraryTypes.ArrayCodec)
                        || SymbolEqualityComparer.Default.Equals(service.OriginalDefinition, services.LibraryTypes.ArrayCopier))
                    {
                        var array = services.Compilation.CreateArrayTypeSymbol(service.TypeArguments[0]);
                        if (!registration.CanonicalArrays.Any(existing => SymbolEqualityComparer.Default.Equals(existing, array)))
                            registration.CanonicalArrays.Add(array);
                        registration.Dependencies.Add(array);
                        registration.Dependencies.Add(array.ElementType);
                    }
                }
            }
        }

        return null;

        INamedTypeSymbol? ResolveImplementation(string name)
        {
            var metadataName = $"{SerializerGenerator.GetGeneratedNamespaceName(named)}.{name}"
                + (named.TypeArguments.Length > 0 ? $"`{named.TypeArguments.Length}" : "");
            var implementation = implementationCompilation.GetTypeByMetadataName(metadataName);
            return implementation is { IsGenericType: true } ? implementation.Construct([.. implementationType.TypeArguments]) : implementation;
        }
    }

    private static void DescribeGeneratedModel(Registration registration, INamedTypeSymbol named, ISerializableTypeDescription model)
    {
        var argumentsSuffix = named.IsGenericType ? $"<{string.Join(", ", named.TypeArguments.Select(Name))}>" : "";
        var generatedNamespace = SerializerGenerator.GetGeneratedNamespaceName(named);
        registration.Codec = $"global::{generatedNamespace}.{SerializerGenerator.GetSimpleClassName(named.Name)}{argumentsSuffix}";
        registration.Copier = model.IsShallowCopyable
            ? $"global::Orleans.Serialization.Cloning.ShallowCopier<{Name(named)}>"
            : $"global::{generatedNamespace}.{CopierGenerator.GetSimpleClassName(named.Name)}{argumentsSuffix}";
        registration.Model = model;
    }

    private static bool TryGetTupleServices(IGeneratorServices services, INamedTypeSymbol type,
        [NotNullWhen(true)] out INamedTypeSymbol? codec, [NotNullWhen(true)] out INamedTypeSymbol? copier)
    {
        codec = null;
        copier = null;
        var arity = type.TypeArguments.Length;
        if (arity == 0)
        {
            return false;
        }

        var family = SymbolEqualityComparer.Default.Equals(type.OriginalDefinition,
            services.Compilation.GetTypeByMetadataName($"System.Tuple`{arity}")) ? "Tuple"
            : SymbolEqualityComparer.Default.Equals(type.OriginalDefinition,
                services.Compilation.GetTypeByMetadataName($"System.ValueTuple`{arity}")) ? "ValueTuple" : null;
        if (family is null) return false;
        codec = services.Compilation.GetTypeByMetadataName($"Orleans.Serialization.Codecs.{family}Codec`{arity}")!.Construct([.. type.TypeArguments]);
        copier = services.Compilation.GetTypeByMetadataName($"Orleans.Serialization.Codecs.{family}Copier`{arity}")!.Construct([.. type.TypeArguments]);
        return true;
    }

    private static bool TryGetConstructionCollectionServices(IGeneratorServices services, INamedTypeSymbol type,
        [NotNullWhen(true)] out INamedTypeSymbol? codec, [NotNullWhen(true)] out INamedTypeSymbol? copier)
    {
        codec = null;
        copier = null;
        var codecDefinition = services.LibraryTypes.WellKnownCodecs.FindByUnderlyingType(type.OriginalDefinition)?.CodecType;
        var copierDefinition = services.LibraryTypes.WellKnownCopiers.FindByUnderlyingType(type.OriginalDefinition)?.CopierType;
        if (codecDefinition is null || copierDefinition is null)
        {
            var name = type.OriginalDefinition.ToDisplayString() switch
            {
                "System.Collections.Generic.SortedDictionary<TKey, TValue>" => "SortedDictionary",
                "System.Collections.Generic.SortedList<TKey, TValue>" => "SortedList",
                "System.Collections.Generic.SortedSet<T>" => "SortedSet",
                "System.Collections.Generic.Queue<T>" => "Queue",
                "System.Collections.Generic.Stack<T>" => "Stack",
                "System.Collections.Concurrent.ConcurrentDictionary<TKey, TValue>" => "ConcurrentDictionary",
                "System.Collections.Concurrent.ConcurrentQueue<T>" => "ConcurrentQueue",
                "System.Collections.ObjectModel.Collection<T>" => "Collection",
                "System.Collections.ObjectModel.ReadOnlyCollection<T>" => "ReadOnlyCollection",
                "System.Collections.ObjectModel.ReadOnlyDictionary<TKey, TValue>" => "ReadOnlyDictionary",
                "System.Collections.Frozen.FrozenDictionary<TKey, TValue>" => "FrozenDictionary",
                "System.Collections.Frozen.FrozenSet<T>" => "FrozenSet",
                "System.ArraySegment<T>" => "ArraySegment",
                "System.Memory<T>" => "Memory",
                "System.ReadOnlyMemory<T>" => "ReadOnlyMemory",
                _ => null
            };
            if (name is null) return false;
            codecDefinition = services.Compilation.GetTypeByMetadataName($"Orleans.Serialization.Codecs.{name}Codec`{type.Arity}");
            copierDefinition = services.Compilation.GetTypeByMetadataName($"Orleans.Serialization.Codecs.{name}Copier`{type.Arity}");
        }
        if (codecDefinition is not { IsGenericType: true } || copierDefinition is not { IsGenericType: true }) return false;
        codec = codecDefinition.Construct([.. type.TypeArguments]);
        copier = copierDefinition.Construct([.. type.TypeArguments]);
        return true;
    }

    private static string ConstructReferenced(string name, INamedTypeSymbol implementation, bool preferCompleteConstructor = false)
    {
        var constructor = GetReferencedConstructor(implementation, preferCompleteConstructor);
        var arguments = constructor.Parameters.Select(parameter =>
            parameter.Type.ToDisplayString() == "Orleans.Serialization.Serializers.ICodecProvider"
                ? "provider"
                : Resolve(Name(parameter.Type)));
        return $"new {name}({string.Join(", ", arguments)})";
    }

    private static IMethodSymbol GetReferencedConstructor(INamedTypeSymbol implementation, bool preferCompleteConstructor)
    {
        var constructors = implementation.InstanceConstructors.Where(static constructor => constructor.DeclaredAccessibility == Accessibility.Public);
        return preferCompleteConstructor
            ? constructors.OrderByDescending(static constructor => constructor.Parameters.Length).First()
            : constructors.Single();
    }

    private static string ConstructGenerated(
        string name,
        GeneratedTypeConstruction construction,
        ISerializableTypeDescription definition,
        INamedTypeSymbol closedType)
    {
        var arguments = construction.Parameters.Select(parameter =>
            parameter.IsCodecProvider
                ? "provider"
                : Resolve(CodeText.Type(SpecializeGeneratedSyntax(parameter.Type, definition, closedType))));
        return $"new {name}({string.Join(", ", arguments)})";
    }

    private static TSyntax SpecializeGeneratedSyntax<TSyntax>(TSyntax syntax, ISerializableTypeDescription definition, INamedTypeSymbol type)
        where TSyntax : SyntaxNode
    {
        if (definition.TypeParameters.Count == 0)
            return syntax;
        var substitutions = definition.TypeParameters
            .Zip(type.GetAllTypeArguments(), static (parameter, argument) => (parameter.Parameter.Name, Type: argument.ToTypeSyntax()))
            .ToDictionary(static entry => entry.Name, static entry => entry.Type, StringComparer.Ordinal);
        return syntax.ReplaceNodes(syntax.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
            .Where(identifier => identifier.Parent is not QualifiedNameSyntax and not AliasQualifiedNameSyntax
                && substitutions.ContainsKey(identifier.Identifier.ValueText)),
            (original, _) => substitutions[original.Identifier.ValueText]);
    }

    private static Failure? AppendTypeMetadata(SourceWriter result, ITypeSymbol symbol, LibraryTypes library, HashSet<ITypeSymbol> visited, int depth = 0)
    {
        if (visited.Contains(symbol)) return null;
        if (visited.Count >= 1024)
            return new Failure(symbol, "the metadata dependency graph exceeds 1024 closed types; declare finite type metadata");
        if (depth >= 128)
            return new Failure(symbol, "the metadata dependency graph exceeds 128 nested dependencies; declare finite type metadata");
        visited.Add(symbol);
        if (symbol is IArrayTypeSymbol array)
        {
            return AppendTypeMetadata(result, array.ElementType, library, visited, depth + 1);
        }
        if (symbol is not INamedTypeSymbol type) return null;
        if (!library.Compilation.IsSymbolAccessibleWithin(type.OriginalDefinition, library.Compilation.Assembly))
        {
            return type.HasAttribute(library.AliasAttribute)
                || type.HasAttribute(library.CompoundTypeAliasAttribute)
                || GeneratedCodeUtilities.GetId(library, type) is not null
                ? new Failure(type, "required alias or type-identifier metadata is inaccessible to the generated context")
                : null;
        }
        if (type.ContainingType is { } declaring)
        {
            if (AppendTypeMetadata(result, declaring, library, visited, depth + 1) is { } declaringFailure) return declaringFailure;
        }
        foreach (var contract in type.AllInterfaces)
        {
            if (AppendTypeMetadata(result, contract, library, visited, depth + 1) is { } contractFailure) return contractFailure;
        }
        foreach (var argument in type.TypeArguments)
        {
            if (!ContainsTypeParameter(argument))
            {
                if (library.Compilation.IsSymbolAccessibleWithin(argument, library.Compilation.Assembly))
                    result.WriteLine($"options.AddAllowedType(typeof({Name(argument)}));");
                if (AppendTypeMetadata(result, argument, library, visited, depth + 1) is { } argumentFailure) return argumentFailure;
            }
        }
        // Only metadata-bearing types need their open spelling; predefined types have no generic definition to spell.
        string? openTypeText = null;
        string OpenType() => openTypeText ??= CodeText.Type(type.ToOpenTypeSyntax());
        if (type.HasAttribute(library.AliasAttribute) || type.HasAttribute(library.CompoundTypeAliasAttribute)
            || GeneratedCodeUtilities.GetId(library, type) is not null)
        {
            result.WriteLine($"options.AddAllowedType(typeof({OpenType()}));");
        }
        if (GeneratedCodeUtilities.GetAlias(library, type) is { } alias)
        {
            MetadataGenerator.WriteTypeMetadataRegistration(result, "options.WellKnownTypeAliases", CodeText.QuotedText(alias), $"typeof({OpenType()})");
        }

        if (GeneratedCodeUtilities.GetId(library, type) is { } id)
        {
            MetadataGenerator.WriteTypeMetadataRegistration(result, "options.WellKnownTypeIds", CodeText.Literal(id), $"typeof({OpenType()})");
        }

        foreach (var compound in type.GetAttributes().Where(attribute =>
            SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, library.CompoundTypeAliasAttribute)
            && attribute.ConstructorArguments.Length == 1))
        {
            var components = compound.ConstructorArguments[0].Values;
            foreach (var componentType in components.Select(component => component.Value).OfType<ITypeSymbol>())
            {
                if (!library.Compilation.IsSymbolAccessibleWithin(componentType, library.Compilation.Assembly))
                    return new Failure(componentType, "required compound-alias component metadata is inaccessible to the generated context");
                result.WriteLine($"options.AddAllowedType(typeof({Name(componentType)}));");
                if (AppendTypeMetadata(result, componentType, library, visited, depth + 1) is { } componentFailure) return componentFailure;
            }

            var compoundAlias = new StringBuilder("options.CompoundTypeAliases");
            for (var index = 0; index < components.Length; index++)
            {
                var component = components[index].Value switch
                {
                    string value => CodeText.QuotedText(value),
                    ITypeSymbol componentSymbol => $"typeof({Name(componentSymbol)})",
                    _ => throw new InvalidOperationException("Compound aliases contain type or string components.")
                };
                compoundAlias.Append(index == components.Length - 1 ? ".Add(" : ".GetOrAdd(").Append(component);
                if (index == components.Length - 1)
                    compoundAlias.Append(", typeof(").Append(OpenType()).Append(')');
                compoundAlias.Append(')');
            }

            result.WriteLine(compoundAlias.Append(';').ToString());
        }
        return null;
    }

    private static bool MayReach(Registration current, ITypeSymbol target,
        Dictionary<ITypeSymbol, Registration> registrations, HashSet<ITypeSymbol> visited)
    {
        if (SymbolEqualityComparer.Default.Equals(current.Type, target)) return true;
        // Linked graphs are opaque. A closed holder preserves construction safety across hidden back-edges.
        if (current.IsProvided) return true;
        if (!visited.Add(current.Type)) return false;
        return current.Dependencies.Any(dependency =>
            MayReach(registrations[dependency.WithNullableAnnotation(NullableAnnotation.None)], target, registrations, visited));
    }

    private static bool ContainsTypeParameter(ITypeSymbol type)
        => type is ITypeParameterSymbol
            || type is INamedTypeSymbol named && named.TypeArguments.Any(ContainsTypeParameter)
            || type is IArrayTypeSymbol array && ContainsTypeParameter(array.ElementType);

    private static string Resolve(string type, string caller = "null!")
        => $"global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<{type}>({caller}, provider)";

    private static string Name(ITypeSymbol type)
        => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

}
