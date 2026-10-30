using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Diagnostics;
using Orleans.CodeGenerator.SyntaxGeneration;
using Orleans.CodeGenerator.Model;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using static Orleans.CodeGenerator.InvokableGenerator;
using static Orleans.CodeGenerator.SerializerGenerator;

namespace Orleans.CodeGenerator;

internal class CopierGenerator(IGeneratorServices generatorServices)
{
    private const string BaseTypeCopierFieldName = "_baseTypeCopier";
    private const string ActivatorFieldName = "_activator";
    private const string DeepCopyMethodName = "DeepCopy";
    private readonly IGeneratorServices _generatorServices = generatorServices;

    private LibraryTypes LibraryTypes => _generatorServices.LibraryTypes;

    private bool HotReloadSafe => _generatorServices.Options.HotReloadSafe;

    private bool UseHotReloadShape(ISerializableTypeDescription type)
        => HotReloadSafe && !type.IsEnumType && type.SupportsHotReloadMemberAddition;

    public ClassDeclarationSyntax? GenerateCopier(
        ISerializableTypeDescription type,
        Dictionary<ISerializableTypeDescription, TypeSyntax> defaultCopiers)
    {
        var plan = CreatePlan(type, defaultCopiers);
        return plan is null ? null : GenerateCopier(plan);
    }

    internal CopierEmissionPlan? CreatePlan(
        ISerializableTypeDescription type,
        Dictionary<ISerializableTypeDescription, TypeSyntax> defaultCopiers,
        SerializableTypeEmissionPlan? typePlan = null)
    {
        var isShallowCopyable = type.IsShallowCopyable;
        if (isShallowCopyable && !type.IsGenericType)
        {
            defaultCopiers.Add(type, LibraryTypes.ShallowCopier.ToTypeSyntax(type.TypeSyntax));
            return null;
        }

        var members = new List<ISerializableMember>();
        foreach (var member in type.Members)
        {
            if (!member.IsCopyable)
            {
                continue;
            }

            if (member is ISerializableMember serializable)
            {
                members.Add(serializable);
            }
            else if (member is IFieldDescription or IPropertyDescription)
            {
                members.Add(new SerializableMember(_generatorServices, member));
            }
            else if (member is MethodParameterFieldDescription methodParameter)
            {
                members.Add(new SerializableMethodMember(methodParameter));
            }
        }

        var isExceptionType = type.IsExceptionType && type.SerializationHooks.Count == 0;

        var baseType = isExceptionType ? QualifiedName(AliasQualifiedName("global", IdentifierName("Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper")), GenericName(Identifier("ExceptionCopier"), TypeArgumentList(SeparatedList([type.TypeSyntax, type.BaseType.ToTypeSyntax()]))))
            : (isShallowCopyable ? LibraryTypes.ShallowCopier : LibraryTypes.DeepCopier_1).ToTypeSyntax(type.TypeSyntax);

        var fields = new List<GeneratedFieldDescription>();
        var onlyDeepFields = false;
        var memberPlans = ImmutableArray<MemberEmissionPlan>.Empty;
        if (!isShallowCopyable)
        {
            fields = GetFieldDescriptions(type, members, isExceptionType, out onlyDeepFields);
            var copiers = type.IsUnsealedImmutable ? null : fields.OfType<ICopierDescription>().Concat(LibraryTypes.StaticCopiers).ToList();
            // Value-type plans omit shallow members: copying the original struct already preserves those fields.
            memberPlans = members.Where(member => !onlyDeepFields || !member.IsShallowCopyable).Select(member =>
                new MemberEmissionPlan(member.Member.FieldId, member.IsPrimaryConstructorParameter,
                    member.IsValueType,
                    member.GetGetter(MemberEmissionPlan.Instance),
                    member.GetSetter(MemberEmissionPlan.Instance, MemberEmissionPlan.Value),
                    default, default,
                    new EmissionSyntax<ExpressionSyntax>(GenerateMemberCopy(fields, member.GetGetter(MemberEmissionPlan.Instance), MemberEmissionPlan.Context, copiers, member))))
                .ToImmutableArray();
        }

        var construction = new GeneratedTypeConstruction(LibraryTypes, fields,
            !isShallowCopyable && !isExceptionType && !type.IsSealedType, isExceptionType && !isShallowCopyable);
        return new(typePlan ?? SerializableTypeEmissionPlan.Create(type, LibraryTypes), isShallowCopyable, isExceptionType,
            onlyDeepFields, baseType, LibraryTypes.BaseCopier_1.ToTypeSyntax(type.TypeSyntax),
            ConstructionEmissionPlan.Create(construction),
            fields.Select(FieldEmissionPlan.Create).ToImmutableArray(), memberPlans);
    }

    internal static ClassDeclarationSyntax GenerateCopier(CopierEmissionPlan plan)
    {
        var type = plan.Type;
        var simpleClassName = GetSimpleClassName(type.Name);
        var isExceptionType = plan.IsExceptionType;
        var classDeclaration = ClassDeclaration(simpleClassName)
            .AddBaseListTypes(SimpleBaseType(plan.BaseType.Syntax))
            .AddModifiers(Token(type.IsPublic ? SyntaxKind.PublicKeyword : SyntaxKind.InternalKeyword), Token(SyntaxKind.SealedKeyword))
            .AddAttributeLists(GeneratedCodeUtilities.GetGeneratedCodeAttributes());

        if (!plan.IsShallowCopyable)
        {
            var fieldDescriptions = plan.Fields.ToList();
            var members = plan.Members.ToList();
            var fieldDeclarations = GetFieldDeclarations(fieldDescriptions);
            var ctor = GenerateConstructor(simpleClassName, fieldDescriptions, isExceptionType, plan.Construction);

            classDeclaration = classDeclaration.AddMembers(fieldDeclarations);

            if (!isExceptionType)
            {
                var copyMethod = GenerateMemberwiseDeepCopyMethod(type, fieldDescriptions, members, plan.OnlyDeepFields);
                classDeclaration = classDeclaration.AddMembers(copyMethod);
            }

            if (ctor != null)
                classDeclaration = classDeclaration.AddMembers(ctor);

            if (isExceptionType || plan.Construction.HasBaseContract)
            {
                if (GenerateBaseCopierDeepCopyMethod(type, fieldDescriptions, members, isExceptionType) is { } baseCopier)
                    classDeclaration = classDeclaration.AddMembers(baseCopier);

                if (plan.Construction.HasBaseContract)
                    classDeclaration = classDeclaration.AddBaseListTypes(SimpleBaseType(plan.AdditionalInterface.Syntax));
            }
        }

        if (type.IsGenericType)
        {
            classDeclaration = type.AddTypeParameters(classDeclaration);
        }

        return classDeclaration;
    }

    internal GeneratedTypeConstruction GetConstruction(ISerializableTypeDescription type)
    {
        if (type.IsShallowCopyable)
            return new(LibraryTypes, [], hasBaseContract: false);

        var isExceptionType = type.IsExceptionType && type.SerializationHooks.Count == 0;
        return new(LibraryTypes, GetFieldDescriptions(type, [], isExceptionType, out _),
            !isExceptionType && !type.IsSealedType, isExceptionType);
    }

    public static string GetSimpleClassName(ISerializableTypeDescription serializableType) => GetSimpleClassName(serializableType.Name);

    public static string GetSimpleClassName(string name) => $"Copier_{name}";

    private static MemberDeclarationSyntax[] GetFieldDeclarations(List<FieldEmissionPlan> fieldDescriptions)
    {
        return [.. fieldDescriptions.Select(GetFieldDeclaration)];

        static MemberDeclarationSyntax GetFieldDeclaration(FieldEmissionPlan description)
        {
            switch (description)
            {
                case { Kind: EmissionFieldKind.Accessor, LazyInitialization: true } accessor when accessor.Initializer.HasValue:
                    return
                        FieldDeclaration(VariableDeclaration(accessor.FieldType, SingletonSeparatedList(VariableDeclarator(accessor.FieldName))))
                            .AddModifiers(Token(SyntaxKind.PrivateKeyword), Token(SyntaxKind.StaticKeyword));
                case { Kind: EmissionFieldKind.Accessor } accessor when accessor.Initializer.HasValue:
                    return
                        FieldDeclaration(VariableDeclaration(accessor.FieldType,
                            SingletonSeparatedList(VariableDeclarator(accessor.FieldName).WithInitializer(EqualsValueClause(accessor.Initializer.Value.Syntax)))))
                            .AddModifiers(Token(SyntaxKind.PrivateKeyword), Token(SyntaxKind.StaticKeyword), Token(SyntaxKind.ReadOnlyKeyword));
                case { Kind: EmissionFieldKind.Accessor } accessor:
                    return GetUnsafeAccessorDeclaration(accessor);
                default:
                    var declaration = FieldDeclaration(VariableDeclaration(description.FieldType, SingletonSeparatedList(VariableDeclarator(description.FieldName))))
                        .AddModifiers(Token(SyntaxKind.PrivateKeyword));
                    return description.LazyInitialization ? declaration : declaration.AddModifiers(Token(SyntaxKind.ReadOnlyKeyword));
            }
        }
    }

    private static ConstructorDeclarationSyntax? GenerateConstructor(
        string simpleClassName, List<FieldEmissionPlan> fieldDescriptions, bool isExceptionType, ConstructionEmissionPlan construction)
    {
        var statements = new List<StatementSyntax>();

        foreach (var field in fieldDescriptions)
        {
            switch (field)
            {
                case { IsInjected: true }:
                    statements.Add(ExpressionStatement(
                        AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            ThisExpression().Member(field.FieldName.ToIdentifierName()),
                            Unwrapped(field.FieldName.ToIdentifierName()))));
                    break;
                case { Kind: EmissionFieldKind.CodecProvider }:
                    statements.Add(ExpressionStatement(AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, field.FieldName.ToIdentifierName(), IdentifierName("codecProvider"))));
                    break;
                case { Kind: EmissionFieldKind.Service, IsInjected: false }:
                    statements.Add(ExpressionStatement(AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, field.FieldName.ToIdentifierName(), GetServiceExpression(field.FieldType, IdentifierName("codecProvider")))));
                    break;
            }
        }

        return statements.Count == 0 && !isExceptionType ? null : ConstructorDeclaration(simpleClassName)
            .AddModifiers(Token(SyntaxKind.PublicKeyword))
            .AddParameterListParameters([.. construction.Parameters.Select(static parameter => parameter.ToSyntax())])
            .AddBodyStatements([.. statements])
            .WithInitializer(isExceptionType ? ConstructorInitializer(SyntaxKind.BaseConstructorInitializer, ArgumentList(SingletonSeparatedList(Argument(IdentifierName("codecProvider"))))) : null);

        static ExpressionSyntax Unwrapped(ExpressionSyntax expr)
        {
            return InvocationExpression(
                IdentifierName("OrleansGeneratedCodeHelper").Member("UnwrapService"),
                ArgumentList(SeparatedList([Argument(ThisExpression()), Argument(expr)])));
        }
    }

    private List<GeneratedFieldDescription> GetFieldDescriptions(
        ISerializableTypeDescription serializableTypeDescription,
        List<ISerializableMember> members,
        bool isExceptionType,
        out bool onlyDeepFields)
    {
        var serializationHooks = serializableTypeDescription.SerializationHooks;
        onlyDeepFields = serializableTypeDescription.IsValueType && serializationHooks.Count == 0;

        var fields = new List<GeneratedFieldDescription>();

        var hotReloadShape = UseHotReloadShape(serializableTypeDescription);
        if (hotReloadShape)
        {
            fields.Add(new CodecProviderFieldDescription(LibraryTypes.ICodecProvider.ToTypeSyntax()));
        }

        if (!isExceptionType && serializableTypeDescription.HasComplexBaseType)
        {
            fields.Add(GetBaseTypeField(serializableTypeDescription));
        }

        if (!serializableTypeDescription.IsImmutable)
        {
            if (!isExceptionType && serializableTypeDescription.UseActivator && !serializableTypeDescription.IsAbstractType)
            {
                onlyDeepFields = false;
                fields.Add(new ActivatorFieldDescription(LibraryTypes.IActivator_1.ToTypeSyntax(serializableTypeDescription.TypeSyntax), ActivatorFieldName));
            }

            // Add a copier field for any field in the target which does not have a static copier.
            GetCopierFieldDescriptions(serializableTypeDescription.Members, fields, hotReloadShape);
        }

        foreach (var member in members)
        {
            if (onlyDeepFields && member.IsShallowCopyable) continue;

            AddFieldAccessorDescriptions(fields, member);
        }

        for (var hookIndex = 0; hookIndex < serializationHooks.Count; ++hookIndex)
        {
            var hookType = serializationHooks[hookIndex];
            fields.Add(new SerializationHookFieldDescription(hookType.ToTypeSyntax(), $"_hook{hookIndex}"));
        }

        return fields;
    }

    private BaseCopierFieldDescription GetBaseTypeField(ISerializableTypeDescription serializableTypeDescription)
    {
        var baseType = serializableTypeDescription.BaseType;
        if (baseType.HasAttribute(LibraryTypes.GenerateSerializerAttribute)
            && (SymbolEqualityComparer.Default.Equals(baseType.ContainingAssembly, LibraryTypes.Compilation.Assembly) || baseType.ContainingAssembly.HasAttribute(LibraryTypes.TypeManifestProviderAttribute))
            && baseType is not INamedTypeSymbol { IsGenericType: true })
        {
            // Use the concrete generated type and avoid expensive interface dispatch (except for generic types that will fall back to IBaseCopier<T>)
            return new(QualifiedName(ParseName(GetGeneratedNamespaceName(baseType)), IdentifierName(GetSimpleClassName(baseType.Name))), true);
        }

        return new(LibraryTypes.BaseCopier_1.ToTypeSyntax(serializableTypeDescription.BaseTypeSyntax));
    }

    public void GetCopierFieldDescriptions(IEnumerable<IMemberDescription> members, List<GeneratedFieldDescription> fields, bool lazyInitialization = false)
    {
        var copierMembers = new List<IMemberDescription>();
        var uniqueTypes = new HashSet<IMemberDescription>(MemberDescriptionTypeComparer.Default);
        foreach (var member in members)
        {
            if (!member.IsCopyable)
            {
                continue;
            }

            var t = member.Type;

            if (LibraryTypes.IsShallowCopyable(t))
                continue;

            if (LibraryTypes.StaticCopiers.Any(c => SymbolEqualityComparer.Default.Equals(c.UnderlyingType, t)))
                continue;

            if (member.Symbol.HasAttribute(LibraryTypes.ImmutableAttribute))
                continue;

            if (!uniqueTypes.Add(member))
                continue;

            copierMembers.Add(member);
        }

        var fieldNames = GeneratedFieldNames.ForTypes("_copier", copierMembers);
        for (var i = 0; i < copierMembers.Count; i++)
        {
            var member = copierMembers[i];
            fields.Add(new CopierFieldDescription(GetCopierType(member), fieldNames[i], member.Type, lazyInitialization));
        }
    }

    private TypeSyntax GetCopierType(IMemberDescription member)
    {
        var t = member.Type;
        if (t.HasAttribute(LibraryTypes.GenerateSerializerAttribute)
            && (SymbolEqualityComparer.Default.Equals(t.ContainingAssembly, LibraryTypes.Compilation.Assembly) || t.ContainingAssembly.HasAttribute(LibraryTypes.TypeManifestProviderAttribute))
            && t is not INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 0 })
        {
            // Use the concrete generated type and avoid expensive interface dispatch (except for complex nested cases that will fall back to IDeepCopier<T>)
            SimpleNameSyntax name;
            if (t is INamedTypeSymbol namedTypeSymbol && namedTypeSymbol.IsGenericType)
            {
                // Construct the full generic type name
                name = GenericName(Identifier(GetSimpleClassName(t.Name)), TypeArgumentList(SeparatedList(namedTypeSymbol.TypeArguments.Select(arg => arg.ToTypeSyntax()))));
            }
            else
            {
                name = IdentifierName(GetSimpleClassName(t.Name));
            }

            return QualifiedName(ParseName(GetGeneratedNamespaceName(t)), name);
        }

        if (t is IArrayTypeSymbol { IsSZArray: true } array)
        {
            return LibraryTypes.ArrayCopier.Construct(array.ElementType).ToTypeSyntax();
        }

        if (LibraryTypes.WellKnownCopiers.FindByUnderlyingType(t) is { } copier)
        {
            // The copier is not a static copier and is also not a generic copiers.
            return copier.CopierType.ToTypeSyntax();
        }

        if (t is INamedTypeSymbol { ConstructedFrom: ISymbol unboundFieldType } named && LibraryTypes.WellKnownCopiers.FindByUnderlyingType(unboundFieldType) is { } genericCopier)
        {
            // Construct the generic copier type using the field's type arguments.
            return genericCopier.CopierType.Construct([.. named.TypeArguments]).ToTypeSyntax();
        }

        // Use the IDeepCopier<T> interface
        return LibraryTypes.DeepCopier_1.ToTypeSyntax(member.TypeSyntax);
    }

    private static MemberDeclarationSyntax GenerateMemberwiseDeepCopyMethod(
        SerializableTypeEmissionPlan type,
        List<FieldEmissionPlan> copierFields,
        List<MemberEmissionPlan> members,
        bool onlyDeepFields)
    {
        var returnType = type.TypeSyntax;

        var originalParam = "original".ToIdentifierName();
        var contextParam = "context".ToIdentifierName();
        var resultVar = "result".ToIdentifierName();

        var body = new List<StatementSyntax>();

        var membersCopied = false;
        if (type.IsAbstractType)
        {
            // C#: return context.DeepCopy(original)
            body.Add(ReturnStatement(InvocationExpression(contextParam.Member("DeepCopy"), ArgumentList(SingletonSeparatedList(Argument(originalParam))))));
            membersCopied = true;
        }
        else if (type.IsUnsealedImmutable)
        {
            // C#: return original is null || original.GetType() == typeof(T) ? original : context.DeepCopy(original);
            var exactTypeMatch = BinaryExpression(SyntaxKind.EqualsExpression, InvocationExpression(originalParam.Member("GetType")), TypeOfExpression(type.TypeSyntax));
            var nullOrTypeMatch = BinaryExpression(SyntaxKind.LogicalOrExpression, BinaryExpression(SyntaxKind.IsExpression, originalParam, LiteralExpression(SyntaxKind.NullLiteralExpression)), exactTypeMatch);
            var contextCopy = InvocationExpression(contextParam.Member("DeepCopy"), ArgumentList(SingletonSeparatedList(Argument(originalParam))));
            body.Add(ReturnStatement(ConditionalExpression(nullOrTypeMatch, originalParam, contextCopy)));
            membersCopied = true;
        }
        else if (!type.IsValueType)
        {
            if (type.TrackReferences)
            {
                // C#: if (context.TryGetCopy(original, out T existing)) return existing;
                var tryGetCopy = InvocationExpression(
                    contextParam.Member("TryGetCopy"),
                    ArgumentList(SeparatedList(
                    [
                        Argument(originalParam),
                        Argument(DeclarationExpression(
                            type.TypeSyntax,
                            SingleVariableDesignation(Identifier("existing"))))
                                    .WithRefKindKeyword(Token(SyntaxKind.OutKeyword))
                    ])));
                body.Add(IfStatement(tryGetCopy, ReturnStatement("existing".ToIdentifierName())));
            }
            else
            {
                // C#: if (original is null) return null;
                body.Add(IfStatement(BinaryExpression(SyntaxKind.IsExpression, originalParam, LiteralExpression(SyntaxKind.NullLiteralExpression)), ReturnStatement(LiteralExpression(SyntaxKind.NullLiteralExpression))));
            }

            if (!type.IsSealedType)
            {
                // C#: if (original.GetType() != typeof(T)) { return context.DeepCopy(original); }
                var exactTypeMatch = BinaryExpression(SyntaxKind.NotEqualsExpression, InvocationExpression(originalParam.Member("GetType")), TypeOfExpression(type.TypeSyntax));
                var contextCopy = InvocationExpression(contextParam.Member("DeepCopy"), ArgumentList(SingletonSeparatedList(Argument(originalParam))));
                body.Add(IfStatement(exactTypeMatch, ReturnStatement(contextCopy)));
            }

            // C#: var result = _activator.Create();
            body.Add(LocalDeclarationStatement(
                VariableDeclaration(
                    IdentifierName("var"),
                    SingletonSeparatedList(VariableDeclarator(
                        resultVar.Identifier,
                        argumentList: null,
                        initializer: EqualsValueClause(GetCreateValueExpression(type, copierFields)))))));

            if (type.TrackReferences)
            {
                // C#: context.RecordCopy(original, result);
                body.Add(ExpressionStatement(InvocationExpression(contextParam.Member("RecordCopy"), ArgumentList(SeparatedList(
                [
                    Argument(originalParam),
                    Argument(resultVar)
                ])))));
            }

            if (!type.IsSealedType)
            {
                // C#: DeepCopy(original, result, context);
                body.Add(ExpressionStatement(InvocationExpression(IdentifierName("DeepCopy"),
                    ArgumentList(SeparatedList([Argument(originalParam), Argument(resultVar), Argument(contextParam)])))));
                body.Add(ReturnStatement(resultVar));
                membersCopied = true;
            }
            else if (type.HasComplexBaseType)
            {
                // C#: _baseTypeCopier.DeepCopy(original, result, context);
                body.Add(
                    ExpressionStatement(
                        InvocationExpression(
                            BaseTypeCopierFieldName.ToIdentifierName().Member(DeepCopyMethodName),
                            ArgumentList(SeparatedList(
                            [
                                Argument(originalParam),
                                Argument(resultVar),
                                Argument(contextParam)
                            ])))));
            }
        }
        else if (!onlyDeepFields)
        {
            // C#: var result = _activator.Create();
            // or C#: var result = new TField();
            // or C#: var result = default(TField);
            body.Add(LocalDeclarationStatement(
                VariableDeclaration(
                    IdentifierName("var"),
                    SingletonSeparatedList(VariableDeclarator(resultVar.Identifier)
                    .WithInitializer(EqualsValueClause(GetCreateValueExpression(type, copierFields)))))));
        }
        else
        {
            originalParam = resultVar;
        }

        if (!membersCopied)
        {
            GenerateMemberwiseCopy(type, members, originalParam, contextParam, resultVar, body);
            body.Add(ReturnStatement(resultVar));
        }

        var parameters = new[]
        {
            Parameter(originalParam.Identifier).WithType(type.TypeSyntax),
            Parameter(contextParam.Identifier).WithType(type.Library.CopyContext.Syntax)
        };

        return MethodDeclaration(returnType, DeepCopyMethodName)
            .AddModifiers(Token(SyntaxKind.PublicKeyword))
            .AddParameterListParameters(parameters)
            .AddAttributeLists(AttributeList(SingletonSeparatedList(GeneratedCodeUtilities.GetMethodImplAttributeSyntax())))
            .AddBodyStatements([.. body]);
    }

    private static ExpressionSyntax GetCreateValueExpression(SerializableTypeEmissionPlan type, List<FieldEmissionPlan> copierFields)
    {
        return type.UseActivator switch
        {
            true => InvocationExpression(GetActivatorField(copierFields).FieldName.ToIdentifierName().Member("Create")),
            false => type.GetObjectCreationExpression()
        };

        static FieldEmissionPlan GetActivatorField(List<FieldEmissionPlan> copierFields)
        {
            var result = copierFields.Find(static field => field.Kind == EmissionFieldKind.Service && field.FieldName == ActivatorFieldName);
            Debug.Assert(result is not null);
            return result!;
        }
    }

    private static MemberDeclarationSyntax? GenerateBaseCopierDeepCopyMethod(
        SerializableTypeEmissionPlan type,
        List<FieldEmissionPlan> copierFields,
        List<MemberEmissionPlan> members,
        bool isExceptionType)
    {
        var inputParam = "input".ToIdentifierName();
        var resultParam = "output".ToIdentifierName();
        var contextParam = "context".ToIdentifierName();

        var body = new List<StatementSyntax>();

        if (type.HasComplexBaseType)
        {
            // C#: _baseTypeCopier.DeepCopy(original, result, context);
            body.Add(
                ExpressionStatement(
                    InvocationExpression(
                        (isExceptionType ? (ExpressionSyntax)BaseExpression() : IdentifierName(BaseTypeCopierFieldName)).Member(DeepCopyMethodName),
                        ArgumentList(SeparatedList(
                        [
                            Argument(inputParam),
                            Argument(resultParam),
                            Argument(contextParam)
                        ])))));
        }

        var emptyBodyCount = body.Count;

        GenerateMemberwiseCopy(
            type,
            members,
            inputParam,
            contextParam,
            resultParam,
            body);

        if (isExceptionType && body.Count == emptyBodyCount)
            return null;

        var parameters = new[]
        {
            Parameter(inputParam.Identifier).WithType(type.TypeSyntax),
            Parameter(resultParam.Identifier).WithType(type.TypeSyntax),
            Parameter(contextParam.Identifier).WithType(type.Library.CopyContext.Syntax)
        };

        var method = MethodDeclaration(PredefinedType(Token(SyntaxKind.VoidKeyword)), DeepCopyMethodName)
            .AddModifiers(Token(SyntaxKind.PublicKeyword))
            .AddParameterListParameters(parameters)
            .AddAttributeLists(AttributeList(SingletonSeparatedList(GeneratedCodeUtilities.GetMethodImplAttributeSyntax())))
            .AddBodyStatements([.. body]);

        if (isExceptionType)
            method = method.AddModifiers(Token(SyntaxKind.OverrideKeyword));

        return method;
    }

    private static void GenerateMemberwiseCopy(
        SerializableTypeEmissionPlan type,
        List<MemberEmissionPlan> members,
        IdentifierNameSyntax sourceVar,
        IdentifierNameSyntax contextVar,
        IdentifierNameSyntax destinationVar,
        List<StatementSyntax> body)
    {
        AddSerializationCallbacks(type, sourceVar, destinationVar, "OnCopying", body);

        var orderedMembers = members.OrderBy(m => m.FieldId);
        foreach (var member in orderedMembers)
        {
            var getValueExpression = member.GetCopy(sourceVar, contextVar);
            var memberAssignment = ExpressionStatement(member.GetSetter(destinationVar, getValueExpression));
            body.Add(memberAssignment);
        }

        AddSerializationCallbacks(type, sourceVar, destinationVar, "OnCopied", body);
    }

    public ExpressionSyntax GenerateMemberCopy(
        List<GeneratedFieldDescription> copierFields,
        ExpressionSyntax inputValue,
        ExpressionSyntax copyContextVar,
        List<ICopierDescription>? copiers,
        ISerializableMember member)
    {
        if (copiers is null || member.IsShallowCopyable)
            return inputValue;

        var description = member.Member;

        // Copiers can either be static classes or injected into the constructor.
        // Either way, the member signatures are the same.
        var memberType = description.Type;
        var copier = copiers.Find(f => SymbolEqualityComparer.Default.Equals(f.UnderlyingType, memberType));
        ExpressionSyntax getValueExpression;

        if (copier is null)
        {
            getValueExpression = InvocationExpression(
                copyContextVar.Member(DeepCopyMethodName),
                ArgumentList(SeparatedList([Argument(inputValue)])));
        }
        else
        {
            ExpressionSyntax copierExpression;
            var staticCopier = LibraryTypes.StaticCopiers.FindByUnderlyingType(memberType);
            if (staticCopier != null)
            {
                copierExpression = staticCopier.CopierType.ToNameSyntax();
            }
            else
            {
                var instanceCopier = copierFields.OfType<CopierFieldDescription>().First(f => SymbolEqualityComparer.Default.Equals(f.UnderlyingType, memberType));
                copierExpression = GetCodecExpression(instanceCopier);
            }

            getValueExpression = InvocationExpression(
                copierExpression.Member(DeepCopyMethodName),
                ArgumentList(SeparatedList([Argument(inputValue), Argument(copyContextVar)])));
            if (!SymbolEqualityComparer.Default.Equals(copier.UnderlyingType, member.Member.Type))
            {
                // If the member type type differs from the copier type (eg because the member is an array), cast the result.
                getValueExpression = CastExpression(description.TypeSyntax, getValueExpression);
            }
        }

        return getValueExpression;
    }

    private static void AddSerializationCallbacks(SerializableTypeEmissionPlan type, IdentifierNameSyntax originalInstance, IdentifierNameSyntax resultInstance, string callbackMethodName, List<StatementSyntax> body)
    {
        var serializationHooks = type.SerializationHooks;
        foreach (var callback in serializationHooks)
        {
            if (callback.Method != callbackMethodName)
            {
                continue;
            }

            var originalArgument = Argument(originalInstance);
            if (callback.FirstByRef)
            {
                originalArgument = originalArgument.WithRefOrOutKeyword(Token(SyntaxKind.RefKeyword));
            }

            var resultArgument = Argument(resultInstance);
            if (callback.SecondByRef)
            {
                resultArgument = resultArgument.WithRefOrOutKeyword(Token(SyntaxKind.RefKeyword));
            }

            body.Add(ExpressionStatement(InvocationExpression(
                IdentifierName($"_hook{callback.HookIndex}").Member(callbackMethodName),
                ArgumentList(SeparatedList([originalArgument, resultArgument])))));
        }
    }

    internal sealed class BaseCopierFieldDescription(TypeSyntax fieldType, bool concreteType = false) : GeneratedFieldDescription(fieldType, BaseTypeCopierFieldName)
    {
        public override bool IsInjected { get; } = !concreteType;
    }

    internal sealed class CopierFieldDescription(TypeSyntax fieldType, string fieldName, ITypeSymbol underlyingType, bool lazyInitialization = false) : GeneratedFieldDescription(fieldType, fieldName), ICopierDescription
    {
        public ITypeSymbol UnderlyingType { get; } = underlyingType;
        public override bool IsInjected => false;
        public override bool LazyInitialization { get; } = lazyInitialization;
    }

}
