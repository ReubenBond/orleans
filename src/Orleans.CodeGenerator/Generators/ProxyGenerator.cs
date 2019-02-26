using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Model;
using Orleans.CodeGenerator.Utilities;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.CodeGenerator.Generators
{
    /// <summary>
    /// Generates RPC stub objects called invokers.
    /// </summary>
    internal static class ProxyGenerator
    {
        public static (ClassDeclarationSyntax, IGeneratedProxyDescription) Generate(
            Compilation compilation,
            WellKnownTypes wellKnownTypes,
            GrainInterfaceDescription interfaceDescription)
        {
            var generatedClassName = GetSimpleClassName(interfaceDescription.InterfaceType);

            /*var fieldDescriptions = GetFieldDescriptions(methodDescription.Method, WellKnownTypes);
            var fields = GetFieldDeclarations(fieldDescriptions);*/
            var ctors = GenerateConstructors(generatedClassName, wellKnownTypes, interfaceDescription).ToArray();
            var proxyMethods = CreateProxyMethods(wellKnownTypes, interfaceDescription).ToArray();

            var classDeclaration = ClassDeclaration(generatedClassName)
                .AddBaseListTypes(
                    SimpleBaseType(wellKnownTypes.Invokable.ToTypeSyntax()),
                    SimpleBaseType(interfaceDescription.InterfaceType.ToTypeSyntax()))
                .AddModifiers(Token(SyntaxKind.InternalKeyword), Token(SyntaxKind.SealedKeyword))
                .AddMembers(ctors)
                .AddMembers(proxyMethods);

            if (interfaceDescription.InterfaceType.TypeParameters.Length > 0)
            {
                classDeclaration = AddGenericTypeConstraints(classDeclaration, interfaceDescription.InterfaceType);
            }

            return (classDeclaration, new GeneratedProxyDescription(interfaceDescription));
        }

        private class GeneratedProxyDescription : IGeneratedProxyDescription
        {
            public GeneratedProxyDescription(GrainInterfaceDescription interfaceDescription)
            {
                this.InterfaceDescription = interfaceDescription;
            }

            public TypeSyntax TypeSyntax => this.GetProxyTypeName();
            public GrainInterfaceDescription InterfaceDescription { get; }

            private TypeSyntax GetProxyTypeName()
            {
                var interfaceType = this.InterfaceDescription.InterfaceType;
                var genericArity = interfaceType.TypeParameters.Length;
                var name = ProxyGenerator.GetSimpleClassName(interfaceType);
                if (genericArity > 0)
                {
                    name += $"<{new string(',', genericArity - 1)}>";
                }

                return ParseTypeName(name);
            }
        }

        public static string GetSimpleClassName(INamedTypeSymbol type) => $"{CodeGenerator.ToolName}_Proxy_{type.Name}";

        private static ClassDeclarationSyntax AddGenericTypeConstraints(
            ClassDeclarationSyntax classDeclaration,
            INamedTypeSymbol type)
        {
            var typeParameters = GetTypeParametersWithConstraints(type.TypeParameters);
            foreach (var (name, constraints) in typeParameters)
            {
                if (constraints.Count > 0)
                {
                    classDeclaration = classDeclaration.AddConstraintClauses(
                        TypeParameterConstraintClause(name).AddConstraints(constraints.ToArray()));
                }
            }

            if (typeParameters.Count > 0)
            {
                classDeclaration = classDeclaration.WithTypeParameterList(
                    TypeParameterList(SeparatedList(typeParameters.Select(tp => TypeParameter(tp.Item1)))));
            }

            return classDeclaration;
        }

        private static List<(string, List<TypeParameterConstraintSyntax>)> GetTypeParametersWithConstraints(ImmutableArray<ITypeParameterSymbol> typeParameter)
        {
            var allConstraints = new List<(string, List<TypeParameterConstraintSyntax>)>();
            foreach (var tp in typeParameter)
            {
                var constraints = new List<TypeParameterConstraintSyntax>();
                if (tp.HasReferenceTypeConstraint)
                {
                    constraints.Add(ClassOrStructConstraint(SyntaxKind.ClassConstraint));
                }

                if (tp.HasValueTypeConstraint)
                {
                    constraints.Add(ClassOrStructConstraint(SyntaxKind.StructConstraint));
                }

                foreach (var c in tp.ConstraintTypes)
                {
                    constraints.Add(TypeConstraint(c.ToTypeSyntax()));
                }

                if (tp.HasConstructorConstraint)
                {
                    constraints.Add(ConstructorConstraint());
                }

                allConstraints.Add((tp.Name, constraints));
            }

            return allConstraints;
        }

        private static MemberDeclarationSyntax[] GetFieldDeclarations(List<FieldDescription> fieldDescriptions)
        {
            return fieldDescriptions.Select(GetFieldDeclaration).ToArray();

            MemberDeclarationSyntax GetFieldDeclaration(FieldDescription description)
            {
                switch (description)
                {
                    case MethodParameterFieldDescription serializable:
                        return FieldDeclaration(
                                VariableDeclaration(
                                    description.FieldType.ToTypeSyntax(),
                                    SingletonSeparatedList(VariableDeclarator(description.FieldName))))
                            .AddModifiers(Token(SyntaxKind.PublicKeyword));
                    default:
                        return FieldDeclaration(
                                VariableDeclaration(
                                    description.FieldType.ToTypeSyntax(),
                                    SingletonSeparatedList(VariableDeclarator(description.FieldName))))
                            .AddModifiers(Token(SyntaxKind.PrivateKeyword), Token(SyntaxKind.ReadOnlyKeyword));
                }
            }
        }

        private static IEnumerable<MemberDeclarationSyntax> GenerateConstructors(
            string simpleClassName,
            WellKnownTypes wellKnownTypes,
            IInvokableInterfaceDescription interfaceDescription)
        {
            var baseType = wellKnownTypes.GrainReference;
            foreach (var member in baseType.GetMembers())
            {
                if (!(member is IMethodSymbol method)) continue;
                if (method.MethodKind != MethodKind.Constructor) continue;
                if (method.DeclaredAccessibility == Accessibility.Private) continue;
                yield return CreateConstructor(method);
            }

            ConstructorDeclarationSyntax CreateConstructor(IMethodSymbol baseConstructor)
            {
                return ConstructorDeclaration(simpleClassName)
                    .AddParameterListParameters(baseConstructor.Parameters.Select(GetParameterSyntax).ToArray())
                    .WithModifiers(TokenList(GetModifiers(baseConstructor)))
                    .WithInitializer(
                        ConstructorInitializer(
                            SyntaxKind.BaseConstructorInitializer,
                            ArgumentList(
                                SeparatedList(baseConstructor.Parameters.Select(GetBaseInitializerArgument)))))
                    .WithBody(Block());
            }

            IEnumerable<SyntaxToken> GetModifiers(IMethodSymbol method)
            {
                switch (method.DeclaredAccessibility)
                {
                    case Accessibility.Public:
                    case Accessibility.Protected:
                        yield return Token(SyntaxKind.PublicKeyword);
                        break;
                    case Accessibility.Internal:
                    case Accessibility.ProtectedOrInternal:
                    case Accessibility.ProtectedAndInternal:
                        yield return Token(SyntaxKind.InternalKeyword);
                        break;
                    default:
                        break;
                }
            }

            ArgumentSyntax GetBaseInitializerArgument(IParameterSymbol parameter)
            {
                var result = Argument(IdentifierName(parameter.Name));
                switch (parameter.RefKind)
                {
                    case RefKind.None:
                        break;
                    case RefKind.Ref:
                        result = result.WithRefOrOutKeyword(Token(SyntaxKind.RefKeyword));
                        break;
                    case RefKind.Out:
                        result = result.WithRefOrOutKeyword(Token(SyntaxKind.OutKeyword));
                        break;
                    default:
                        break;
                }

                return result;
            }
        }

        private static IEnumerable<MemberDeclarationSyntax> CreateProxyMethods(
            WellKnownTypes wellKnownTypes,
            GrainInterfaceDescription interfaceDescription)
        {
            foreach (var methodDescription in interfaceDescription.Methods)
            {
                yield return CreateProxyMethod(methodDescription);
            }

            MethodDeclarationSyntax CreateProxyMethod(GrainMethodDescription methodDescription)
            {
                var method = methodDescription.Method;
                var declaration = MethodDeclaration(method.ReturnType.ToTypeSyntax(), method.Name)
                    .AddModifiers(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.AsyncKeyword))
                    .AddParameterListParameters(method.Parameters.Select(GetParameterSyntax).ToArray())
                    .WithBody(
                        CreateProxyMethodBody(wellKnownTypes, interfaceDescription, methodDescription));

                var typeParameters = GetTypeParametersWithConstraints(method.TypeParameters);
                foreach (var (name, constraints) in typeParameters)
                {
                    if (constraints.Count > 0)
                    {
                        declaration = declaration.AddConstraintClauses(
                            TypeParameterConstraintClause(name).AddConstraints(constraints.ToArray()));
                    }
                }

                if (typeParameters.Count > 0)
                {
                    declaration = declaration.WithTypeParameterList(
                        TypeParameterList(SeparatedList(typeParameters.Select(tp => TypeParameter(tp.Item1)))));
                }

                return declaration;
            }
        }

        private static BlockSyntax CreateProxyMethodBody(
            WellKnownTypes wellKnownTypes,
            GrainInterfaceDescription interfaceDescription,
            GrainMethodDescription methodDescription)
        {
            var statements = new List<StatementSyntax>();

            // Create request object
            var requestVar = IdentifierName("request");

            var requestDescription = interfaceDescription.Invokers[methodDescription];
            var createRequestExpr = ObjectCreationExpression(requestDescription.TypeSyntax)
                .WithArgumentList(ArgumentList(SeparatedList<ArgumentSyntax>()));

            statements.Add(
                LocalDeclarationStatement(
                    VariableDeclaration(
                        ParseTypeName("var"),
                        SingletonSeparatedList(
                            VariableDeclarator(
                                    Identifier("request"))
                                .WithInitializer(
                                    EqualsValueClause(createRequestExpr))))));

            // Set request object fields from method parameters.
            var parameterIndex = 0;
            foreach (var parameter in methodDescription.Method.Parameters)
            {
                statements.Add(
                    ExpressionStatement(
                        AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            requestVar.Member($"arg{parameterIndex}"),
                            IdentifierName(parameter.Name))));

                parameterIndex++;
            }

            // Issue request
            statements.Add(
                ExpressionStatement(
                    AwaitExpression(
                        InvocationExpression(
                            BaseExpression().Member("Invoke"),
                            ArgumentList(SingletonSeparatedList(Argument(requestVar)))))));

            // Return result
            if (methodDescription.Method.ReturnType is INamedTypeSymbol named && named.TypeParameters.Length == 1)
            {
                statements.Add(ReturnStatement(requestVar.Member("result")));
            }

            return Block(statements);
        }

        private static ParameterSyntax GetParameterSyntax(IParameterSymbol parameter)
        {
            var result = Parameter(Identifier(parameter.Name)).WithType(parameter.Type.ToTypeSyntax());
            switch (parameter.RefKind)
            {
                case RefKind.None:
                    break;
                case RefKind.Ref:
                    result = result.WithModifiers(TokenList(Token(SyntaxKind.RefKeyword)));
                    break;
                case RefKind.Out:
                    result = result.WithModifiers(TokenList(Token(SyntaxKind.OutKeyword)));
                    break;
                case RefKind.In:
                    result = result.WithModifiers(TokenList(Token(SyntaxKind.InKeyword)));
                    break;
                default:
                    break;
            }

            return result;
        }

        private static List<FieldDescription> GetFieldDescriptions(IMethodSymbol method, WellKnownTypes wellKnownTypes)
        {
            var fields = new List<FieldDescription>();

            uint fieldId = 0;
            foreach (var parameter in method.Parameters)
            {
                fields.Add(new MethodParameterFieldDescription(parameter, $"arg{fieldId}", fieldId));
                fieldId++;
            }

            return fields;
        }

        /// <summary>
        /// Returns the "expected" type for <paramref name="type"/> which is used for selecting the correct codec.
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        private static ITypeSymbol GetExpectedType(ITypeSymbol type)
        {
            if (type is IArrayTypeSymbol)
                return type;
            if (type is IPointerTypeSymbol pointerType)
                throw new NotSupportedException($"Cannot serialize pointer type {pointerType.Name}");
            return type;
        }

        internal abstract class FieldDescription
        {
            protected FieldDescription(ITypeSymbol fieldType, string fieldName)
            {
                this.FieldType = fieldType;
                this.FieldName = fieldName;
            }

            public ITypeSymbol FieldType { get; }
            public string FieldName { get; }
            public abstract bool IsInjected { get; }
        }

        internal class InjectedFieldDescription : FieldDescription
        {
            public InjectedFieldDescription(ITypeSymbol fieldType, string fieldName) : base(fieldType, fieldName)
            {
            }

            public override bool IsInjected => true;
        }

        internal class MethodParameterFieldDescription : FieldDescription, IMemberDescription
        {
            public MethodParameterFieldDescription(IParameterSymbol parameter, string fieldName, uint fieldId)
                : base(parameter.Type, fieldName)
            {
                this.FieldId = fieldId;
                this.Parameter = parameter;
            }

            public override bool IsInjected => false;
            public uint FieldId { get; }
            public ISymbol Member => this.Parameter;
            public ITypeSymbol Type => this.FieldType;
            public IParameterSymbol Parameter { get; }
            public string Name => this.FieldName;
        }
    }
}