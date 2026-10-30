using System.Collections.Immutable;
using Orleans.CodeGenerator.Model;
using Orleans.CodeGenerator.SyntaxGeneration;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.CodeGenerator;

internal class ActivatorGenerator(IGeneratorServices generatorServices)
{
    private readonly IGeneratorServices _generatorServices = generatorServices;

    private struct ConstructorArgument
    {
        public TypeSyntax Type { get; set; }
        public string FieldName { get; set; }
        public string ParameterName { get; set; }
    }

    public ClassDeclarationSyntax GenerateActivator(ISerializableTypeDescription type)
        => GenerateActivator(CreatePlan(type));

    internal ActivatorEmissionPlan CreatePlan(ISerializableTypeDescription type, SerializableTypeEmissionPlan? typePlan = null)
        => new(typePlan ?? SerializableTypeEmissionPlan.Create(type, _generatorServices.LibraryTypes),
            _generatorServices.LibraryTypes.IActivator_1.ToTypeSyntax(type.TypeSyntax),
            type.ActivatorConstructorParameters.Select(static parameter => new EmissionSyntax<TypeSyntax>(parameter)).ToImmutableArray());

    internal static ClassDeclarationSyntax GenerateActivator(ActivatorEmissionPlan plan)
    {
        var type = plan.Type;
        var simpleClassName = GetSimpleClassName(type.Name);

        var baseInterface = plan.BaseInterface.Syntax;

        var orderedFields = new List<ConstructorArgument>();
        var index = 0;
        if (plan.ConstructorParameters is { Count: > 0 } parameters)
        {
            foreach (var arg in parameters)
            {
                orderedFields.Add(new ConstructorArgument { Type = arg.Syntax, FieldName = $"_arg{index}", ParameterName = $"arg{index}" });
                index++;
            }
        }

        var members = new List<MemberDeclarationSyntax>();
        foreach (var field in orderedFields)
        {
            members.Add(
                FieldDeclaration(VariableDeclaration(field.Type, SingletonSeparatedList(VariableDeclarator(field.FieldName))))
                .AddModifiers(
                    Token(SyntaxKind.PrivateKeyword),
                    Token(SyntaxKind.ReadOnlyKeyword)));
        }

        if (orderedFields.Count > 0)
            members.Add(GenerateConstructor(simpleClassName, orderedFields));

        members.Add(GenerateCreateMethod(type, orderedFields));

        var classDeclaration = ClassDeclaration(simpleClassName)
            .AddBaseListTypes(SimpleBaseType(baseInterface))
            .AddModifiers(Token(SyntaxKind.InternalKeyword), Token(SyntaxKind.SealedKeyword))
            .AddAttributeLists(GeneratedCodeUtilities.GetGeneratedCodeAttributes())
            .AddMembers([.. members]);

        if (type.IsGenericType)
        {
            classDeclaration = type.AddTypeParameters(classDeclaration);
        }

        return classDeclaration;
    }

    public static string GetSimpleClassName(ISerializableTypeDescription serializableType) => GetSimpleClassName(serializableType.Name);

    public static string GetSimpleClassName(string name) => $"Activator_{name}";

    /// <summary>
    /// Determines whether an activator should be generated for the specified type.
    /// </summary>
    internal static bool ShouldGenerateActivator(ISerializableTypeDescription type)
    {
        return !type.IsAbstractType
            && !type.IsEnumType
            && (!type.IsValueType
                && type.IsEmptyConstructable
                && !type.UseActivator
                && type is not GeneratedInvokableDescription
                || type.HasActivatorConstructor);
    }

    private static ConstructorDeclarationSyntax GenerateConstructor(
        string simpleClassName,
        List<ConstructorArgument> orderedFields)
    {
        var parameters = new List<ParameterSyntax>();
        var body = new List<StatementSyntax>();
        foreach (var field in orderedFields)
        {
            parameters.Add(Parameter(field.ParameterName.ToIdentifier()).WithType(field.Type));

            body.Add(ExpressionStatement(
                        AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            field.FieldName.ToIdentifierName(),
                            Unwrapped(field.ParameterName.ToIdentifierName()))));
        }

        var constructorDeclaration = ConstructorDeclaration(simpleClassName)
            .AddModifiers(Token(SyntaxKind.PublicKeyword))
            .AddParameterListParameters([.. parameters])
            .AddBodyStatements([.. body]);

        return constructorDeclaration;

        static ExpressionSyntax Unwrapped(ExpressionSyntax expr)
        {
            return InvocationExpression(
                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("OrleansGeneratedCodeHelper"), IdentifierName("UnwrapService")),
                ArgumentList(SeparatedList([Argument(ThisExpression()), Argument(expr)])));
        }
    }

    private static MemberDeclarationSyntax GenerateCreateMethod(SerializableTypeEmissionPlan type, List<ConstructorArgument> orderedFields)
    {
        ExpressionSyntax createObject;
        if (orderedFields.Count > 0)
        {
            var argList = new List<ArgumentSyntax>();
            foreach (var field in orderedFields)
            {
                argList.Add(Argument(field.FieldName.ToIdentifierName()));
            }

            createObject = ObjectCreationExpression(type.TypeSyntax).WithArgumentList(ArgumentList(SeparatedList(argList)));
        }
        else
        {
            createObject = type.GetObjectCreationExpression();
        }

        return MethodDeclaration(type.TypeSyntax, "Create")
            .WithExpressionBody(ArrowExpressionClause(createObject))
            .WithSemicolonToken(Token(SyntaxKind.SemicolonToken))
            .AddModifiers(Token(SyntaxKind.PublicKeyword));
    }
}
