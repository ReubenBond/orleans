using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Orleans.Durable.Storage.Architecture.Tests;

/// <summary>
/// Where a type's metadata references another type. A type reached through a member reference's parent or signature,
/// a type specification, or a generic instantiation takes the site of the token or signature which leads to it.
/// </summary>
internal enum ReferenceSite
{
    /// <summary>The type's base type.</summary>
    BaseType,

    /// <summary>An interface the type implements.</summary>
    Interface,

    /// <summary>A constraint of a generic parameter of the type or of one of its methods.</summary>
    GenericConstraint,

    /// <summary>
    /// The constructor of a custom attribute of the type, a field, property, event, method, parameter, return value,
    /// or generic parameter: the attribute's type, and the constructor's signature when it is a member reference.
    /// </summary>
    AttributeConstructor,

    /// <summary>A positional argument of a custom attribute: its type, which is the enum of a boxed enum, or the type it names.</summary>
    AttributePositionalArgument,

    /// <summary>A named argument of a custom attribute: its type or the type it names.</summary>
    AttributeNamedArgument,

    /// <summary>An element of an array argument of a custom attribute: its type or the type it names.</summary>
    AttributeArrayElement,

    /// <summary>A field's type.</summary>
    FieldSignature,

    /// <summary>A property's type or the types of its parameters.</summary>
    PropertySignature,

    /// <summary>An event's delegate type.</summary>
    EventType,

    /// <summary>A method's return type or the type of one of its parameters.</summary>
    MethodSignature,

    /// <summary>A local's type in a method body.</summary>
    Local,

    /// <summary>The exception type of a catch clause; a filter's test is IL, whose operands are instruction operands.</summary>
    CatchType,

    /// <summary>
    /// An operand of an IL instruction: a type; a member's declaring type, or a member reference's signature; a generic
    /// method's type arguments; or an indirect call's signature.
    /// </summary>
    InstructionOperand,
}

/// <summary>A type which a scanned type references, by namespace and full name, and where it references it.</summary>
internal readonly record struct ReferencedType(string Namespace, string FullName, ReferenceSite Site);

/// <summary>
/// A type of another assembly which a scanned type references, by the simple name of the assembly a type reference or
/// a qualified serialized name gives, and the type's namespace and full name.
/// </summary>
internal readonly record struct AssemblyType(string Assembly, string Namespace, string FullName);

/// <summary>
/// A field or method which a scanned type references, by the full name of the type which declares it and its name. A
/// member of a generic type's instantiation is declared by the generic type definition, as in <c>List`1</c>.
/// </summary>
internal readonly record struct ReferencedMember(string DeclaringType, string Name)
{
    public override string ToString() => DeclaringType + "." + Name;
}

/// <summary>A method of a scanned assembly's type which implements an interface's member, each named as a reference to it names it.</summary>
internal readonly record struct MemberImplementation(ReferencedMember Implementation, ReferencedMember Implemented);

/// <summary>
/// A field or method which a scanned type uses, or which an assembly declares, by the full name of the type which
/// declares it, its name, and its signature with every type named by its full name and no assembly, so a use and the
/// declaration it resolves to give the same value whether one assembly or two hold them.
/// </summary>
internal readonly record struct MemberUse(string DeclaringType, string Name, string Signature)
{
    public override string ToString() => DeclaringType + "." + Name + Signature;
}

/// <summary>
/// The accessibility of an assembly's declarations: whether code of another assembly may name each type and member
/// without <c>InternalsVisibleTo</c>, and each type's base type, through which a use of an inherited member resolves.
/// A type is visible when it and each type which encloses it are public, protected, or protected internal; a member
/// when its type is visible and it is public, protected, or protected internal.
/// </summary>
internal sealed record DeclaredAccess(
    IReadOnlyDictionary<string, DeclaredType> Types,
    IReadOnlyDictionary<MemberUse, bool> Members);

/// <summary>A declared type's visibility outside its assembly, and the full name of its base type's definition.</summary>
internal readonly record struct DeclaredType(bool IsVisible, string? BaseType);

/// <summary>
/// A type of a scanned assembly, by namespace and full name, with the types and members its metadata references, and
/// the types of other assemblies among them with their assemblies. A nested type's full name joins it to its outer
/// type's with <c>+</c>, and its namespace is its outermost type's. <paramref name="MemberUses"/> gives each member
/// of <paramref name="Members"/> with its signature.
/// </summary>
internal sealed record ScannedType(
    string Namespace,
    string FullName,
    IReadOnlyList<ReferencedType> Types,
    IReadOnlyList<ReferencedMember> Members,
    IReadOnlyList<AssemblyType> AssemblyTypes,
    IReadOnlyList<MemberUse> MemberUses);

/// <summary>
/// Finds what each type of an assembly references in its metadata: its base type, interfaces, generic constraints, and
/// the custom attributes of the type and its members, parameters, return values, and generic parameters, with their
/// constructors and arguments (positional, named, and array elements, including a boxed enum's type); its field,
/// property, event, and method signatures, through arrays, references, pointers, function pointers, and custom
/// modifiers; and its method bodies' locals, catch types, and IL operands, with the fields and methods those operands
/// name. A nested type, including a compiler-generated closure or state machine, belongs to the namespace of its
/// outermost type. It skips the custom attributes of interface implementations and of constraints, which C# fills
/// with nullability, and explicit implementations' interface methods, whose interfaces the type implements.
/// </summary>
internal static class MetadataReferences
{
    /// <summary>
    /// Scans the types of the assembly at <paramref name="assemblyPath"/>, nested types included, whose namespace
    /// <paramref name="isScanned"/> selects.
    /// </summary>
    public static IReadOnlyList<ScannedType> Scan(string assemblyPath, Func<string, bool> isScanned)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var image = new PEReader(stream);
        var scanner = new Scanner(image) { Directory = Path.GetDirectoryName(Path.GetFullPath(assemblyPath)) };
        var scanned = new List<ScannedType>();
        foreach (var handle in scanner.Metadata.TypeDefinitions)
        {
            var type = scanner.Key(handle);
            if (isScanned(type.Namespace))
            {
                var references = scanner.References(handle);

                // A type which a serialized name and a type reference both name may have two keys, which differ only in
                // the assembly each gives.
                ReferencedType[] types =
                [
                    .. references.Types
                        .Select(static reference => new ReferencedType(reference.Type.Namespace, reference.Type.FullName, reference.Site))
                        .Distinct(),
                ];
                AssemblyType[] assemblyTypes =
                [
                    .. references.Types
                        .Where(static reference => reference.Type.Assembly is not null)
                        .Select(static reference => new AssemblyType(
                            new System.Reflection.AssemblyName(reference.Type.Assembly!).Name!,
                            reference.Type.Namespace,
                            reference.Type.FullName))
                        .Distinct(),
                ];
                scanned.Add(new(type.Namespace, type.FullName, types, [.. references.Members], assemblyTypes, [.. references.MemberUses]));
            }
        }

        return scanned;
    }

    /// <summary>The full names of the types the assembly at <paramref name="assemblyPath"/> declares, each with the names of its fields and methods.</summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> Declarations(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var image = new PEReader(stream);
        var scanner = new Scanner(image);
        var metadata = scanner.Metadata;
        return metadata.TypeDefinitions.ToDictionary(
            handle => scanner.Key(handle).FullName,
            IReadOnlySet<string> (handle) =>
            {
                var type = metadata.GetTypeDefinition(handle);
                return type.GetFields().Select(field => metadata.GetString(metadata.GetFieldDefinition(field).Name))
                    .Concat(type.GetMethods().Select(method => metadata.GetString(metadata.GetMethodDefinition(method).Name)))
                    .ToHashSet(StringComparer.Ordinal);
            },
            StringComparer.Ordinal);
    }

    /// <summary>The accessibility of every type, method, and field the assembly at <paramref name="assemblyPath"/> declares.</summary>
    public static DeclaredAccess Accessibility(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var image = new PEReader(stream);
        var scanner = new Scanner(image);
        var metadata = scanner.Metadata;
        var types = new Dictionary<string, DeclaredType>(StringComparer.Ordinal);
        var members = new Dictionary<MemberUse, bool>();
        foreach (var handle in metadata.TypeDefinitions)
        {
            var typeName = scanner.Key(handle).FullName;
            var type = metadata.GetTypeDefinition(handle);
            var isVisible = IsVisible(metadata, handle);
            types.Add(typeName, new(isVisible, type.BaseType.IsNil ? null : scanner.DeclaringType(type.BaseType)));
            foreach (var methodHandle in type.GetMethods())
            {
                var method = metadata.GetMethodDefinition(methodHandle);
                members[new(typeName, metadata.GetString(method.Name), scanner.Signature(method))] =
                    isVisible && IsVisibleAccess((int)(method.Attributes & MethodAttributes.MemberAccessMask));
            }

            foreach (var fieldHandle in type.GetFields())
            {
                var field = metadata.GetFieldDefinition(fieldHandle);
                members[new(typeName, metadata.GetString(field.Name), scanner.Signature(field))] =
                    isVisible && IsVisibleAccess((int)(field.Attributes & FieldAttributes.FieldAccessMask));
            }
        }

        return new(types, members);

        static bool IsVisible(MetadataReader metadata, TypeDefinitionHandle handle)
        {
            var type = metadata.GetTypeDefinition(handle);
            var visibility = type.Attributes & TypeAttributes.VisibilityMask;
            if (visibility == TypeAttributes.Public)
            {
                return true;
            }

            return visibility is TypeAttributes.NestedPublic or TypeAttributes.NestedFamily or TypeAttributes.NestedFamORAssem
                && IsVisible(metadata, type.GetDeclaringType());
        }

        // Method and field access share their encoding (ECMA-335 II.23.1.5, II.23.1.10): 4 is family, 5 family or
        // assembly, 6 public.
        static bool IsVisibleAccess(int access) => access is 4 or 5 or 6;
    }

    /// <summary>
    /// The methods of the types the assembly at <paramref name="assemblyPath"/> declares which implement a member
    /// <paramref name="interfaceMembers"/> lists, by the full name of its interface's type definition and its name. A
    /// type which implements the interface implements the member with each method it declares under the member's
    /// name, which covers an implicit implementation and conservatively its overloads, and with the body of each of
    /// its method implementations whose declaration is the member, an explicit implementation. A call of an inherited
    /// method names the type which declares it, so a derived type is reported only when it implements the interface
    /// itself.
    /// </summary>
    public static IReadOnlyList<MemberImplementation> Implementations(string assemblyPath, IReadOnlySet<ReferencedMember> interfaceMembers)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var image = new PEReader(stream);
        var scanner = new Scanner(image);
        var metadata = scanner.Metadata;
        var implementations = new HashSet<MemberImplementation>();
        foreach (var handle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(handle);
            var typeName = scanner.Key(handle).FullName;
            var interfaces = type.GetInterfaceImplementations()
                .Select(implementation => scanner.DeclaringType(metadata.GetInterfaceImplementation(implementation).Interface))
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal);
            foreach (var methodHandle in type.GetMethods())
            {
                var name = metadata.GetString(metadata.GetMethodDefinition(methodHandle).Name);
                foreach (var member in interfaceMembers)
                {
                    if (interfaces.Contains(member.DeclaringType) && string.Equals(member.Name, name, StringComparison.Ordinal))
                    {
                        implementations.Add(new(new(typeName, name), member));
                    }
                }
            }

            foreach (var implementationHandle in type.GetMethodImplementations())
            {
                var implementation = metadata.GetMethodImplementation(implementationHandle);
                if (scanner.Member(implementation.MethodDeclaration) is { } declaration
                    && interfaceMembers.Contains(declaration)
                    && scanner.Member(implementation.MethodBody) is { } body)
                {
                    implementations.Add(new(body, declaration));
                }
            }
        }

        return [.. implementations];
    }

    /// <summary>
    /// A type by namespace and full name. <paramref name="Assembly"/> is the assembly which the reference gives: a type
    /// reference's resolution scope, or a serialized name's qualification in a custom attribute. It is null for a
    /// type this assembly defines, and for a serialized name without one, which names a type of the attribute's
    /// assembly.
    /// </summary>
    private readonly record struct TypeKey(string Namespace, string FullName, string? Assembly = null);

    /// <summary>What one type references, as the scanner collects it.</summary>
    private sealed class References
    {
        public HashSet<(TypeKey Type, ReferenceSite Site)> Types { get; } = [];

        public HashSet<ReferencedMember> Members { get; } = [];

        public HashSet<MemberUse> MemberUses { get; } = [];

        public void Add(TypeKey type, ReferenceSite site) => Types.Add((type, site));

        public void Add(ImmutableArray<TypeKey> types, ReferenceSite site)
        {
            foreach (var type in types)
            {
                Add(type, site);
            }
        }

        public void Add(MethodSignature<ImmutableArray<TypeKey>> signature, ReferenceSite site)
        {
            Add(signature.ReturnType, site);
            foreach (var parameter in signature.ParameterTypes)
            {
                Add(parameter, site);
            }
        }
    }

    /// <summary>
    /// Writes a signature with each type by its full name, generic parameters by position, and custom modifiers kept, so
    /// a member reference's signature equals its declaration's whichever assembly holds each.
    /// </summary>
    private sealed class SignatureKeys(Scanner scanner) : ISignatureTypeProvider<string, object?>
    {
        public static string Method(MethodSignature<string> signature) =>
            "`" + signature.GenericParameterCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "(" + string.Join(",", signature.ParameterTypes) + ")" + signature.ReturnType;

        public static string Field(string type) => ":" + type;

        public string GetArrayType(string elementType, ArrayShape shape) =>
            elementType + "[" + shape.Rank.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";

        public string GetByReferenceType(string elementType) => elementType + "&";

        public string GetFunctionPointerType(MethodSignature<string> signature) => "method " + Method(signature);

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
            genericType + "<" + string.Join(",", typeArguments) + ">";

        public string GetGenericMethodParameter(object? genericContext, int index) =>
            "!!" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public string GetGenericTypeParameter(object? genericContext, int index) =>
            "!" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) =>
            unmodifiedType + (isRequired ? " modreq(" : " modopt(") + modifier + ")";

        public string GetPinnedType(string elementType) => elementType;

        public string GetPointerType(string elementType) => elementType + "*";

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            scanner.Key(handle).FullName;

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
            scanner.Key(handle).FullName;

        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }

    private sealed class Scanner(PEReader image)
        : ISignatureTypeProvider<ImmutableArray<TypeKey>, object?>, ICustomAttributeTypeProvider<ImmutableArray<TypeKey>>
    {
        /// <summary>The directory of the scanned assembly, where an attribute's enum of an assembly the test process does not load lies.</summary>
        public string? Directory { get; init; }

        /// <summary>The operand type of every IL opcode, keyed by its one- or two-byte encoding.</summary>
        private static readonly FrozenDictionary<ushort, OperandType> OperandTypes = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(static field => field.GetValue(null))
            .OfType<OpCode>()
            .ToFrozenDictionary(static opCode => unchecked((ushort)opCode.Value), static opCode => opCode.OperandType);

        /// <summary>The operands which are metadata tokens: a type, member, method specification, or signature.</summary>
        private static readonly FrozenSet<OperandType> TokenOperands = new[]
        {
            OperandType.InlineField,
            OperandType.InlineMethod,
            OperandType.InlineSig,
            OperandType.InlineTok,
            OperandType.InlineType,
        }.ToFrozenSet();

        /// <summary>The size of every other operand except a switch's, which is a count followed by its targets.</summary>
        private static readonly FrozenDictionary<OperandType, int> OperandSizes = new Dictionary<OperandType, int>
        {
            [OperandType.InlineNone] = 0,
            [OperandType.ShortInlineBrTarget] = 1,
            [OperandType.ShortInlineI] = 1,
            [OperandType.ShortInlineVar] = 1,
            [OperandType.InlineVar] = 2,
            [OperandType.InlineBrTarget] = 4,
            [OperandType.InlineI] = 4,
            [OperandType.InlineString] = 4,
            [OperandType.ShortInlineR] = 4,
            [OperandType.InlineI8] = 8,
            [OperandType.InlineR] = 8,
        }.ToFrozenDictionary();

        /// <summary>The encoding of each integral type an enum may have, to decode an enum argument of an attribute.</summary>
        private static readonly FrozenDictionary<Type, PrimitiveTypeCode> EnumUnderlyingTypes = new Dictionary<Type, PrimitiveTypeCode>
        {
            [typeof(byte)] = PrimitiveTypeCode.Byte,
            [typeof(sbyte)] = PrimitiveTypeCode.SByte,
            [typeof(short)] = PrimitiveTypeCode.Int16,
            [typeof(ushort)] = PrimitiveTypeCode.UInt16,
            [typeof(int)] = PrimitiveTypeCode.Int32,
            [typeof(uint)] = PrimitiveTypeCode.UInt32,
            [typeof(long)] = PrimitiveTypeCode.Int64,
            [typeof(ulong)] = PrimitiveTypeCode.UInt64,
            [typeof(bool)] = PrimitiveTypeCode.Boolean,
            [typeof(char)] = PrimitiveTypeCode.Char,
        }.ToFrozenDictionary();

        public MetadataReader Metadata { get; } = image.GetMetadataReader();

        public TypeKey Key(TypeDefinitionHandle handle)
        {
            var type = Metadata.GetTypeDefinition(handle);
            var declaring = type.GetDeclaringType();
            if (!declaring.IsNil)
            {
                var outer = Key(declaring);
                return new(outer.Namespace, outer.FullName + "+" + Metadata.GetString(type.Name));
            }

            return Qualified(Metadata.GetString(type.Namespace), Metadata.GetString(type.Name));
        }

        public TypeKey Key(TypeReferenceHandle handle)
        {
            var type = Metadata.GetTypeReference(handle);
            var scope = type.ResolutionScope;
            if (scope.Kind == HandleKind.TypeReference)
            {
                var outer = Key((TypeReferenceHandle)scope);
                return outer with { FullName = outer.FullName + "+" + Metadata.GetString(type.Name) };
            }

            return Qualified(Metadata.GetString(type.Namespace), Metadata.GetString(type.Name)) with
            {
                Assembly = scope.Kind == HandleKind.AssemblyReference
                    ? Metadata.GetAssemblyReference((AssemblyReferenceHandle)scope).GetAssemblyName().FullName
                    : null,
            };
        }

        public References References(TypeDefinitionHandle handle)
        {
            var references = new References();
            var type = Metadata.GetTypeDefinition(handle);
            AddEntity(type.BaseType, ReferenceSite.BaseType, references);
            foreach (var implementation in type.GetInterfaceImplementations())
            {
                AddEntity(Metadata.GetInterfaceImplementation(implementation).Interface, ReferenceSite.Interface, references);
            }

            AddGenericParameters(type.GetGenericParameters(), references);
            AddAttributes(type.GetCustomAttributes(), references);
            foreach (var fieldHandle in type.GetFields())
            {
                var field = Metadata.GetFieldDefinition(fieldHandle);
                references.Add(field.DecodeSignature(this, genericContext: null), ReferenceSite.FieldSignature);
                AddAttributes(field.GetCustomAttributes(), references);
            }

            foreach (var propertyHandle in type.GetProperties())
            {
                var property = Metadata.GetPropertyDefinition(propertyHandle);
                references.Add(property.DecodeSignature(this, genericContext: null), ReferenceSite.PropertySignature);
                AddAttributes(property.GetCustomAttributes(), references);
            }

            foreach (var eventHandle in type.GetEvents())
            {
                var definition = Metadata.GetEventDefinition(eventHandle);
                AddEntity(definition.Type, ReferenceSite.EventType, references);
                AddAttributes(definition.GetCustomAttributes(), references);
            }

            foreach (var methodHandle in type.GetMethods())
            {
                AddMethod(Metadata.GetMethodDefinition(methodHandle), references);
            }

            return references;
        }

        /// <summary>
        /// The full name of the type which declares the member a member reference's parent gives: the type, or the generic
        /// type definition of an instantiation. An array's members, a global member of another module, and a vararg
        /// call site, whose parent is the method it calls and which <see cref="AddEntity"/> records, have none. An
        /// interface implementation's interface, a type, is given the same way.
        /// </summary>
        public string? DeclaringType(EntityHandle parent) => parent switch
        {
            { Kind: HandleKind.TypeDefinition } => Key((TypeDefinitionHandle)parent).FullName,
            { Kind: HandleKind.TypeReference } => Key((TypeReferenceHandle)parent).FullName,
            { Kind: HandleKind.TypeSpecification } => GenericTypeDefinition((TypeSpecificationHandle)parent),
            _ => null,
        };

        /// <summary>
        /// The method a method definition or a member reference names, as a call's operand names it; null for a member
        /// reference whose parent gives no declaring type (<see cref="DeclaringType"/>).
        /// </summary>
        public ReferencedMember? Member(EntityHandle method)
        {
            if (method.Kind == HandleKind.MethodDefinition)
            {
                var definition = Metadata.GetMethodDefinition((MethodDefinitionHandle)method);
                return new(Key(definition.GetDeclaringType()).FullName, Metadata.GetString(definition.Name));
            }

            var reference = Metadata.GetMemberReference((MemberReferenceHandle)method);
            return DeclaringType(reference.Parent) is { } declaringType
                ? new(declaringType, Metadata.GetString(reference.Name))
                : null;
        }

        /// <summary>A method's signature, as <see cref="MemberUse.Signature"/> gives it.</summary>
        public string Signature(MethodDefinition method) => SignatureKeys.Method(method.DecodeSignature(new SignatureKeys(this), genericContext: null));

        /// <summary>A field's signature, as <see cref="MemberUse.Signature"/> gives it.</summary>
        public string Signature(FieldDefinition field) => SignatureKeys.Field(field.DecodeSignature(new SignatureKeys(this), genericContext: null));

        /// <summary>The signature of the method or field a member reference names, as its declaration gives it.</summary>
        public string Signature(MemberReference member) => member.GetKind() == MemberReferenceKind.Method
            ? SignatureKeys.Method(member.DecodeMethodSignature(new SignatureKeys(this), genericContext: null))
            : SignatureKeys.Field(member.DecodeFieldSignature(new SignatureKeys(this), genericContext: null));

        public ImmutableArray<TypeKey> GetArrayType(ImmutableArray<TypeKey> elementType, ArrayShape shape) => elementType;

        public ImmutableArray<TypeKey> GetByReferenceType(ImmutableArray<TypeKey> elementType) => elementType;

        public ImmutableArray<TypeKey> GetFunctionPointerType(MethodSignature<ImmutableArray<TypeKey>> signature) =>
            [.. signature.ReturnType, .. signature.ParameterTypes.SelectMany(static parameter => parameter)];

        public ImmutableArray<TypeKey> GetGenericInstantiation(
            ImmutableArray<TypeKey> genericType,
            ImmutableArray<ImmutableArray<TypeKey>> typeArguments) =>
            [.. genericType, .. typeArguments.SelectMany(static argument => argument)];

        public ImmutableArray<TypeKey> GetGenericMethodParameter(object? genericContext, int index) => [];

        public ImmutableArray<TypeKey> GetGenericTypeParameter(object? genericContext, int index) => [];

        // C# writes only modifiers of the core library (IsVolatile, InAttribute, IsExternalInit, OutAttribute, and
        // calling conventions), so a modifier itself never names a layer's type.
        public ImmutableArray<TypeKey> GetModifiedType(
            ImmutableArray<TypeKey> modifier,
            ImmutableArray<TypeKey> unmodifiedType,
            bool isRequired) => [.. modifier, .. unmodifiedType];

        public ImmutableArray<TypeKey> GetPinnedType(ImmutableArray<TypeKey> elementType) => elementType;

        public ImmutableArray<TypeKey> GetPointerType(ImmutableArray<TypeKey> elementType) => elementType;

        public ImmutableArray<TypeKey> GetPrimitiveType(PrimitiveTypeCode typeCode) => [];

        public ImmutableArray<TypeKey> GetSZArrayType(ImmutableArray<TypeKey> elementType) => elementType;

        public ImmutableArray<TypeKey> GetTypeFromDefinition(
            MetadataReader reader,
            TypeDefinitionHandle handle,
            byte rawTypeKind) => [Key(handle)];

        public ImmutableArray<TypeKey> GetTypeFromReference(
            MetadataReader reader,
            TypeReferenceHandle handle,
            byte rawTypeKind) => [Key(handle)];

        // A signature encodes its types in place, and a type specification only as a custom modifier, which C# writes only
        // for core-library types (GetModifiedType).
        public ImmutableArray<TypeKey> GetTypeFromSpecification(
            MetadataReader reader,
            object? genericContext,
            TypeSpecificationHandle handle,
            byte rawTypeKind) => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        public ImmutableArray<TypeKey> GetSystemType() => [new("System", "System.Type")];

        public bool IsSystemType(ImmutableArray<TypeKey> type) => type is [{ FullName: "System.Type" }];

        public ImmutableArray<TypeKey> GetTypeFromSerializedName(string name) =>
            SerializedTypes(System.Reflection.Metadata.TypeName.Parse(name.AsSpan()));

        public PrimitiveTypeCode GetUnderlyingEnumType(ImmutableArray<TypeKey> type)
        {
            var key = type.Single();
            foreach (var handle in Metadata.TypeDefinitions)
            {
                if (string.Equals(Key(handle).FullName, key.FullName, StringComparison.Ordinal))
                {
                    return UnderlyingType(Metadata.GetTypeDefinition(handle));
                }
            }

            // C# qualifies the serialized name of every type outside the attribute's assembly, and a type reference names
            // its assembly, so an enum this assembly does not define is in the assembly its key gives: one the test
            // process loads, or else one beside the scanned assembly, as a simulation project's kit is.
            var assembly = key.Assembly
                ?? throw new InvalidOperationException(
                    $"The enum {key.FullName} of an attribute argument is not defined by this assembly and names no other.");
            var runtimeType = Type.GetType(key.FullName + ", " + assembly, throwOnError: false);
            if (runtimeType is null)
            {
                var beside = Path.Combine(Directory ?? throw new FileNotFoundException($"{assembly} is not loaded.", assembly), new System.Reflection.AssemblyName(assembly).Name + ".dll");
                using var stream = File.OpenRead(beside);
                using var image = new PEReader(stream);
                return new Scanner(image).GetUnderlyingEnumType([key with { Assembly = null }]);
            }

            return runtimeType.IsEnum
                ? EnumUnderlyingTypes[Enum.GetUnderlyingType(runtimeType)]
                : throw new InvalidOperationException($"The attribute argument type {runtimeType} is not an enum.");
        }

        private static TypeKey Qualified(string typeNamespace, string name) =>
            new(typeNamespace, typeNamespace.Length == 0 ? name : typeNamespace + "." + name);

        private static ImmutableArray<TypeKey> SerializedTypes(System.Reflection.Metadata.TypeName name)
        {
            if (name.IsArray || name.IsPointer || name.IsByRef)
            {
                return SerializedTypes(name.GetElementType());
            }

            if (name.IsConstructedGenericType)
            {
                return
                [
                    .. SerializedTypes(name.GetGenericTypeDefinition()),
                    .. name.GetGenericArguments().SelectMany(static argument => SerializedTypes(argument)),
                ];
            }

            var outermost = name.FullName.Split('+')[0];
            var lastDot = outermost.LastIndexOf('.');
            return [new(lastDot < 0 ? string.Empty : outermost[..lastDot], name.FullName, name.AssemblyName?.FullName)];
        }

        private static void AddAttributeArgument(
            ImmutableArray<TypeKey> type,
            object? value,
            ReferenceSite site,
            References references)
        {
            references.Add(type, site);
            switch (value)
            {
                case ImmutableArray<TypeKey> namedType:
                    references.Add(namedType, site);
                    break;
                case ImmutableArray<CustomAttributeTypedArgument<ImmutableArray<TypeKey>>> elements:
                    foreach (var element in elements)
                    {
                        AddAttributeArgument(element.Type, element.Value, ReferenceSite.AttributeArrayElement, references);
                    }

                    break;
                default:
                    // A primitive, string, enum value, or null names no type beyond the argument's type.
                    break;
            }
        }

        private PrimitiveTypeCode UnderlyingType(TypeDefinition enumType)
        {
            foreach (var fieldHandle in enumType.GetFields())
            {
                var field = Metadata.GetFieldDefinition(fieldHandle);
                if (!field.Attributes.HasFlag(FieldAttributes.Static))
                {
                    var signature = Metadata.GetBlobReader(field.Signature);
                    _ = signature.ReadSignatureHeader();

                    // SignatureTypeCode and PrimitiveTypeCode share the ECMA-335 element-type values (II.23.1.16).
                    return (PrimitiveTypeCode)signature.ReadSignatureTypeCode();
                }
            }

            throw new InvalidOperationException($"The enum {Metadata.GetString(enumType.Name)} has no value field.");
        }

        private void AddMethod(MethodDefinition method, References references)
        {
            references.Add(method.DecodeSignature(this, genericContext: null), ReferenceSite.MethodSignature);
            AddGenericParameters(method.GetGenericParameters(), references);
            AddAttributes(method.GetCustomAttributes(), references);

            // The parameters include the return value, as sequence number 0, when it has attributes.
            foreach (var parameter in method.GetParameters())
            {
                AddAttributes(Metadata.GetParameter(parameter).GetCustomAttributes(), references);
            }

            if (method.RelativeVirtualAddress == 0)
            {
                return;
            }

            var body = image.GetMethodBody(method.RelativeVirtualAddress);
            if (!body.LocalSignature.IsNil)
            {
                foreach (var local in Metadata.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(this, genericContext: null))
                {
                    references.Add(local, ReferenceSite.Local);
                }
            }

            foreach (var region in body.ExceptionRegions)
            {
                AddEntity(region.CatchType, ReferenceSite.CatchType, references);
            }

            AddInstructions(body.GetILReader(), references);
        }

        private void AddInstructions(BlobReader il, References references)
        {
            while (il.RemainingBytes > 0)
            {
                int code = il.ReadByte();
                if (code == 0xFE)
                {
                    code = 0xFE00 | il.ReadByte();
                }

                var operand = OperandTypes[(ushort)code];
                if (TokenOperands.Contains(operand))
                {
                    AddEntity(MetadataTokens.EntityHandle(il.ReadInt32()), ReferenceSite.InstructionOperand, references);
                }
                else if (operand == OperandType.InlineSwitch)
                {
                    var targets = il.ReadInt32();
                    il.Offset += targets * sizeof(int);
                }
                else
                {
                    il.Offset += OperandSizes[operand];
                }
            }
        }

        private void AddEntity(EntityHandle handle, ReferenceSite site, References references)
        {
            switch (handle)
            {
                case { IsNil: true }:
                    // No base type, or a clause which catches no type.
                    break;
                case { Kind: HandleKind.TypeDefinition }:
                    references.Add(Key((TypeDefinitionHandle)handle), site);
                    break;
                case { Kind: HandleKind.TypeReference }:
                    references.Add(Key((TypeReferenceHandle)handle), site);
                    break;
                case { Kind: HandleKind.TypeSpecification }:
                    references.Add(Metadata.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(this, genericContext: null), site);
                    break;
                case { Kind: HandleKind.MethodDefinition }:
                    var method = Metadata.GetMethodDefinition((MethodDefinitionHandle)handle);
                    AddDefinedMember(method.GetDeclaringType(), method.Name, Signature(method), site, references);
                    break;
                case { Kind: HandleKind.FieldDefinition }:
                    var field = Metadata.GetFieldDefinition((FieldDefinitionHandle)handle);
                    AddDefinedMember(field.GetDeclaringType(), field.Name, Signature(field), site, references);
                    break;
                case { Kind: HandleKind.MemberReference }:
                    AddMemberReference(Metadata.GetMemberReference((MemberReferenceHandle)handle), site, references);
                    break;
                case { Kind: HandleKind.MethodSpecification }:
                    var specification = Metadata.GetMethodSpecification((MethodSpecificationHandle)handle);
                    AddEntity(specification.Method, site, references);
                    foreach (var argument in specification.DecodeSignature(this, genericContext: null))
                    {
                        references.Add(argument, site);
                    }

                    break;
                case { Kind: HandleKind.StandaloneSignature }:
                    // An indirect call's signature. C# calls through a function pointer only in unsafe code, which the
                    // fixtures' analyzers forbid; the pointer's type also appears where the pointer comes from.
                    references.Add(Metadata.GetStandaloneSignature((StandaloneSignatureHandle)handle).DecodeMethodSignature(this, genericContext: null), site);
                    break;
                case { Kind: HandleKind.ModuleReference }:
                    // The parent of a global member of another module names no type.
                    break;
                default:
                    // No other kind reaches here from a token, a member's parent, a constructor, a constraint, or a catch
                    // type.
                    throw new UnreachableException($"A metadata reference of kind {handle.Kind} is not scanned.");
            }
        }

        private void AddDefinedMember(TypeDefinitionHandle declaringType, StringHandle name, string signature, ReferenceSite site, References references)
        {
            var declaring = Key(declaringType);
            references.Add(declaring, site);
            references.Members.Add(new(declaring.FullName, Metadata.GetString(name)));
            references.MemberUses.Add(new(declaring.FullName, Metadata.GetString(name), signature));
        }

        private void AddMemberReference(MemberReference member, ReferenceSite site, References references)
        {
            AddEntity(member.Parent, site, references);
            if (DeclaringType(member.Parent) is { } declaringType)
            {
                references.Members.Add(new(declaringType, Metadata.GetString(member.Name)));
                references.MemberUses.Add(new(declaringType, Metadata.GetString(member.Name), Signature(member)));
            }

            if (member.GetKind() == MemberReferenceKind.Method)
            {
                references.Add(member.DecodeMethodSignature(this, genericContext: null), site);
            }
            else
            {
                references.Add(member.DecodeFieldSignature(this, genericContext: null), site);
            }
        }

        private string? GenericTypeDefinition(TypeSpecificationHandle handle)
        {
            var signature = Metadata.GetBlobReader(Metadata.GetTypeSpecification(handle).Signature);
            if (signature.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance)
            {
                return null;
            }

            // CLASS or VALUETYPE, then the generic type definition (ECMA-335 II.23.2.12).
            _ = signature.ReadSignatureTypeCode();
            return DeclaringType(signature.ReadTypeHandle());
        }

        private void AddGenericParameters(GenericParameterHandleCollection parameters, References references)
        {
            foreach (var parameterHandle in parameters)
            {
                var parameter = Metadata.GetGenericParameter(parameterHandle);
                AddAttributes(parameter.GetCustomAttributes(), references);
                foreach (var constraint in parameter.GetConstraints())
                {
                    AddEntity(Metadata.GetGenericParameterConstraint(constraint).Type, ReferenceSite.GenericConstraint, references);
                }
            }
        }

        private void AddAttributes(CustomAttributeHandleCollection attributes, References references)
        {
            foreach (var handle in attributes)
            {
                var attribute = Metadata.GetCustomAttribute(handle);
                AddEntity(attribute.Constructor, ReferenceSite.AttributeConstructor, references);
                var value = attribute.DecodeValue(this);
                foreach (var argument in value.FixedArguments)
                {
                    AddAttributeArgument(argument.Type, argument.Value, ReferenceSite.AttributePositionalArgument, references);
                }

                foreach (var argument in value.NamedArguments)
                {
                    AddAttributeArgument(argument.Type, argument.Value, ReferenceSite.AttributeNamedArgument, references);
                }
            }
        }
    }
}
