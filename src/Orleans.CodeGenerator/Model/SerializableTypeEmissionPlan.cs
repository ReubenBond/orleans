using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.SyntaxGeneration;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using static Orleans.CodeGenerator.SerializerGenerator;

namespace Orleans.CodeGenerator.Model;

// These nodes are constructed semantic fragments, never nodes from an input syntax tree.
// Equality precedes class/method construction and whole-file formatting.
internal readonly struct EmissionSyntax<T>(T syntax) : IEquatable<EmissionSyntax<T>> where T : SyntaxNode
{
    public T Syntax { get; } = syntax;

    public bool Equals(EmissionSyntax<T> other) => SyntaxFactory.AreEquivalent(Syntax, other.Syntax);
    public override bool Equals(object? obj) => obj is EmissionSyntax<T> other && Equals(other);
    public override int GetHashCode()
    {
        var hash = 17;
        if (Syntax is not null)
        {
            foreach (var token in Syntax.DescendantTokens())
            {
                hash = StructuralEquality.Combine(hash, token.RawKind);
                hash = StructuralEquality.Combine(hash, StructuralEquality.GetHashCode(token.ValueText));
            }
        }

        return hash;
    }

    public static implicit operator EmissionSyntax<T>(T syntax) => new(syntax);
}

internal sealed record EmissionLibraryTypes(
    EmissionSyntax<TypeSyntax> SystemType,
    EmissionSyntax<TypeSyntax> Writer,
    EmissionSyntax<TypeSyntax> Reader,
    EmissionSyntax<TypeSyntax> Field,
    EmissionSyntax<TypeSyntax> BufferWriter,
    EmissionSyntax<TypeSyntax> CodecProvider,
    EmissionSyntax<TypeSyntax> CopyContext,
    bool HasScopedKeyword)
{
    public static EmissionLibraryTypes Create(LibraryTypes library) => new(
        library.Type.ToTypeSyntax(),
        library.Writer.ToTypeSyntax(),
        library.Reader.ToTypeSyntax(ParseTypeName("TReaderInput")),
        library.Field.ToTypeSyntax(),
        library.IBufferWriter.ToTypeSyntax(PredefinedType(Token(SyntaxKind.ByteKeyword))),
        library.ICodecProvider.ToTypeSyntax(),
        library.CopyContext.ToTypeSyntax(),
        library.HasScopedKeyword());
}

internal sealed record GenericParameterEmissionPlan(
    string Name,
    EquatableArray<EmissionSyntax<TypeParameterConstraintSyntax>> Constraints);

internal sealed record SerializationCallbackEmissionPlan(string Method, int HookIndex, bool FirstByRef, bool SecondByRef);

internal sealed record SerializableTypeEmissionPlan(
    string Name,
    bool IsPublic,
    EmissionSyntax<TypeSyntax> Type,
    bool IsValueType,
    bool IsSealedType,
    bool IsAbstractType,
    bool HasComplexBaseType,
    bool IncludePrimaryConstructorParameters,
    bool UseActivator,
    bool TrackReferences,
    bool OmitDefaultMemberValues,
    bool IsUnsealedImmutable,
    EmissionSyntax<ExpressionSyntax> ObjectCreation,
    EquatableArray<GenericParameterEmissionPlan> TypeParameters,
    EquatableArray<SerializationCallbackEmissionPlan> SerializationHooks,
    EmissionLibraryTypes Library)
{
    public TypeSyntax TypeSyntax => Type.Syntax;
    public bool IsGenericType => TypeParameters.Count > 0;
    public ExpressionSyntax GetObjectCreationExpression() => ObjectCreation.Syntax;

    public static SerializableTypeEmissionPlan Create(ISerializableTypeDescription type, LibraryTypes library)
    {
        var parameters = SyntaxFactoryUtility.GetTypeParameterConstraints(type.TypeParameters)
            .Select(static parameter => new GenericParameterEmissionPlan(parameter.Name,
                parameter.Constraints.Select(static constraint => new EmissionSyntax<TypeParameterConstraintSyntax>(constraint)).ToImmutableArray()))
            .ToImmutableArray();
        var callbacks = ImmutableArray.CreateBuilder<SerializationCallbackEmissionPlan>();
        foreach (var method in new[] { "OnSerializing", "OnSerialized", "OnDeserializing", "OnDeserialized", "OnCopying", "OnCopied" })
        {
            var arity = method is "OnCopying" or "OnCopied" ? 2 : 1;
            for (var index = 0; index < type.SerializationHooks.Count; index++)
            {
                var member = type.SerializationHooks[index].GetAllMembers<IMethodSymbol>(method, Accessibility.Public).FirstOrDefault();
                if (member is null || member.Parameters.Length != arity)
                {
                    continue;
                }

                callbacks.Add(new(method, index, member.Parameters[0].RefKind == RefKind.Ref,
                    arity == 2 && member.Parameters[1].RefKind == RefKind.Ref));
            }
        }

        return new(type.Name, type.Accessibility == Accessibility.Public, type.TypeSyntax,
            type.IsValueType, type.IsSealedType, type.IsAbstractType, type.HasComplexBaseType,
            type.IncludePrimaryConstructorParameters, type.UseActivator, type.TrackReferences,
            type.OmitDefaultMemberValues, type.IsUnsealedImmutable, type.GetObjectCreationExpression(),
            parameters, callbacks.ToImmutable(), EmissionLibraryTypes.Create(library));
    }

    public ClassDeclarationSyntax AddTypeParameters(ClassDeclarationSyntax declaration)
    {
        foreach (var parameter in TypeParameters)
        {
            if (parameter.Constraints.Count > 0)
            {
                declaration = declaration.AddConstraintClauses(TypeParameterConstraintClause(parameter.Name)
                    .AddConstraints([.. parameter.Constraints.Select(static constraint => constraint.Syntax)]));
            }
        }

        return TypeParameters.Count == 0 ? declaration : declaration.WithTypeParameterList(
            TypeParameterList(SeparatedList(TypeParameters.Select(static parameter => TypeParameter(parameter.Name)))));
    }
}

internal enum EmissionFieldKind
{
    Service,
    CodecProvider,
    Type,
    Accessor,
}

internal sealed record FieldEmissionPlan(
    EmissionSyntax<TypeSyntax> Type,
    string FieldName,
    EmissionFieldKind Kind,
    bool IsInjected,
    bool LazyInitialization,
    EmissionSyntax<ExpressionSyntax>? Initializer = null,
    EmissionSyntax<TypeSyntax>? ContainingType = null,
    string? AccessorName = null,
    bool IsUnsafeFieldAccessor = false,
    bool ContainingTypeIsValueType = false)
{
    public TypeSyntax FieldType => Type.Syntax;

    public static FieldEmissionPlan Create(GeneratedFieldDescription field) => field switch
    {
        TypeFieldDescription type => new(field.FieldType, field.FieldName, EmissionFieldKind.Type, false, false,
            TypeOfExpression(type.UnderlyingTypeSyntax)),
        CodecFieldTypeFieldDescription type => new(field.FieldType, field.FieldName, EmissionFieldKind.Type, false, false,
            TypeOfExpression(type.CodecFieldType)),
        FieldAccessorDescription accessor => new(field.FieldType, field.FieldName, EmissionFieldKind.Accessor, false,
            field.LazyInitialization, accessor.InitializationSyntax is { } initializer ? new(initializer) : default(EmissionSyntax<ExpressionSyntax>?),
            new EmissionSyntax<TypeSyntax>(accessor.ContainingType), accessor.AccessorName,
            accessor.IsUnsafeFieldAccessor, accessor.ContainingTypeIsValueType),
        CodecProviderFieldDescription => new(field.FieldType, field.FieldName, EmissionFieldKind.CodecProvider, false, false),
        _ => new(field.FieldType, field.FieldName, EmissionFieldKind.Service, field.IsInjected, field.LazyInitialization),
    };
}

// Access expressions use annotated holes, so binding never rewrites a user's member/type identifier.
internal sealed record MemberEmissionPlan(
    uint FieldId,
    bool IsPrimaryConstructorParameter,
    bool IsValueType,
    bool IsShallowCopyable,
    EmissionSyntax<ExpressionSyntax> Getter,
    EmissionSyntax<ExpressionSyntax> Setter,
    EmissionSyntax<ExpressionSyntax>? Codec,
    EmissionSyntax<ExpressionSyntax>? ExpectedType,
    EmissionSyntax<ExpressionSyntax>? Copy)
{
    private static readonly SyntaxAnnotation InstanceHole = new("Orleans.Emission.Instance");
    private static readonly SyntaxAnnotation ValueHole = new("Orleans.Emission.Value");
    private static readonly SyntaxAnnotation ContextHole = new("Orleans.Emission.Context");

    internal static IdentifierNameSyntax Instance => IdentifierName("instance").WithAdditionalAnnotations(InstanceHole);
    internal static IdentifierNameSyntax Value => IdentifierName("value").WithAdditionalAnnotations(ValueHole);
    internal static IdentifierNameSyntax Context => IdentifierName("context").WithAdditionalAnnotations(ContextHole);

    public ExpressionSyntax GetGetter(ExpressionSyntax instance) => Bind(Getter.Syntax, instance, null, null);
    public ExpressionSyntax GetSetter(ExpressionSyntax instance, ExpressionSyntax value) => Bind(Setter.Syntax, instance, value, null);
    public ExpressionSyntax GetCopy(ExpressionSyntax instance, ExpressionSyntax context)
    {
        Debug.Assert(Copy.HasValue);
        return Bind(Copy!.Value.Syntax, instance, null, context);
    }

    private static ExpressionSyntax Bind(ExpressionSyntax expression, ExpressionSyntax instance, ExpressionSyntax? value, ExpressionSyntax? context)
        => expression.ReplaceNodes(expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
            .Where(static node => node.HasAnnotation(InstanceHole) || node.HasAnnotation(ValueHole) || node.HasAnnotation(ContextHole)),
            (node, _) => node.HasAnnotation(InstanceHole) ? instance : node.HasAnnotation(ValueHole) ? value! : context!);
}

internal sealed record SerializerEmissionPlan(
    SerializableTypeEmissionPlan Type,
    EmissionSyntax<TypeSyntax> BaseType,
    EmissionSyntax<TypeSyntax>? AdditionalInterface,
    EquatableArray<FieldEmissionPlan> Fields,
    EquatableArray<MemberEmissionPlan> Members,
    EnumSerializerPlan? Enum);

internal sealed record CopierEmissionPlan(
    SerializableTypeEmissionPlan Type,
    bool IsShallowCopyable,
    bool IsExceptionType,
    bool OnlyDeepFields,
    EmissionSyntax<TypeSyntax> BaseType,
    EmissionSyntax<TypeSyntax> AdditionalInterface,
    EquatableArray<FieldEmissionPlan> Fields,
    EquatableArray<MemberEmissionPlan> Members);

internal sealed record ActivatorEmissionPlan(
    SerializableTypeEmissionPlan Type,
    EmissionSyntax<TypeSyntax> BaseInterface,
    EquatableArray<EmissionSyntax<TypeSyntax>> ConstructorParameters);

internal sealed record DtoSerializerOutputPlan(
    SerializerEmissionPlan Serializer,
    CopierEmissionPlan? Copier,
    ActivatorEmissionPlan? Activator,
    string AssemblyName,
    TypeMetadataIdentity MetadataIdentity,
    string GeneratedNamespace);
