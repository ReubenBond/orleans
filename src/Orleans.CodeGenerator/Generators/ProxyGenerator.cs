using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Model;

namespace Orleans.CodeGenerator.Generators
{
    /// <summary>
    /// Generates RPC stub objects called invokers.
    /// </summary>
    internal static class ProxyGenerator
    {
        public static (ClassDeclarationSyntax, IGeneratedProxyDescription) Generate(
            Compilation compilation,
            WellKnownTypes WellKnownTypes,
            IInvokableInterfaceDescription interfaceDescription,
            MetadataModel metadataModel)
        {
            var generatedClassName = GetSimpleClassName(interfaceDescription.InterfaceType);

            /*var fieldDescriptions = GetFieldDescriptions(methodDescription.Method, WellKnownTypes);
            var fields = GetFieldDeclarations(fieldDescriptions);*/
            var ctors = GenerateConstructors(generatedClassName, WellKnownTypes, interfaceDescription).ToArray();
            var proxyMethods = CreateProxyMethods(WellKnownTypes, interfaceDescription, metadataModel).ToArray();

            var classDeclaration = SyntaxFactory.ClassDeclaration(generatedClassName)
                .AddBaseListTypes(
                    SyntaxFactory.SimpleBaseType(interfaceDescription.ProxyBaseType.ToTypeSyntax()),
                    SyntaxFactory.SimpleBaseType(interfaceDescription.InterfaceType.ToTypeSyntax()))
                .AddModifiers(SyntaxFactory.Token(SyntaxKind.InternalKeyword), SyntaxFactory.Token(SyntaxKind.SealedKeyword))
                .AddAttributeLists(
                    SyntaxFactory.AttributeList(SingletonSeparatedList(CodeGenerator.GetGeneratedCodeAttributeSyntax())))
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
            public GeneratedProxyDescription(IInvokableInterfaceDescription interfaceDescription)
            {
                this.InterfaceDescription = interfaceDescription;
            }

            public TypeSyntax TypeSyntax => this.GetProxyTypeName();
            public IInvokableInterfaceDescription InterfaceDescription { get; }
        }

        public static string GetSimpleClassName(INamedTypeSymbol type)
        {
            return $"{CodeGenerator.CodeGeneratorName}_Proxy_{type.Name}";
        }

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
                        SyntaxFactory.TypeParameterConstraintClause(name).AddConstraints(constraints.ToArray()));
                }
            }

            if (typeParameters.Count > 0)
            {
                classDeclaration = classDeclaration.WithTypeParameterList(
                    SyntaxFactory.TypeParameterList(SyntaxFactory.SeparatedList(typeParameters.Select(tp => SyntaxFactory.TypeParameter(tp.Item1)))));
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
                    constraints.Add(SyntaxFactory.ClassOrStructConstraint(SyntaxKind.ClassConstraint));
                }

                if (tp.HasValueTypeConstraint)
                {
                    constraints.Add(SyntaxFactory.ClassOrStructConstraint(SyntaxKind.StructConstraint));
                }

                foreach (var c in tp.ConstraintTypes)
                {
                    constraints.Add(SyntaxFactory.TypeConstraint(c.ToTypeSyntax()));
                }

                if (tp.HasConstructorConstraint)
                {
                    constraints.Add(SyntaxFactory.ConstructorConstraint());
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
                        return SyntaxFactory.FieldDeclaration(
                                SyntaxFactory.VariableDeclaration(
                                    description.FieldType.ToTypeSyntax(),
                                    SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(description.FieldName))))
                            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword));
                    default:
                        return SyntaxFactory.FieldDeclaration(
                                SyntaxFactory.VariableDeclaration(
                                    description.FieldType.ToTypeSyntax(),
                                    SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(description.FieldName))))
                            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PrivateKeyword), SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword));
                }
            }
        }

        private static IEnumerable<MemberDeclarationSyntax> GenerateConstructors(
            string simpleClassName,
            WellKnownTypes WellKnownTypes,
            IInvokableInterfaceDescription interfaceDescription)
        {
            var baseType = interfaceDescription.ProxyBaseType;
            foreach (var member in baseType.GetMembers())
            {
                if (!(member is IMethodSymbol method)) continue;
                if (method.MethodKind != MethodKind.Constructor) continue;
                if (method.DeclaredAccessibility == Accessibility.Private) continue;
                yield return CreateConstructor(method);
            }

            ConstructorDeclarationSyntax CreateConstructor(IMethodSymbol baseConstructor)
            {
                return SyntaxFactory.ConstructorDeclaration(simpleClassName)
                    .AddParameterListParameters(baseConstructor.Parameters.Select(GetParameterSyntax).ToArray())
                    .WithModifiers(SyntaxFactory.TokenList(GetModifiers(baseConstructor)))
                    .WithInitializer(
                        SyntaxFactory.ConstructorInitializer(
                            SyntaxKind.BaseConstructorInitializer,
                            SyntaxFactory.ArgumentList(
                                SyntaxFactory.SeparatedList(baseConstructor.Parameters.Select(GetBaseInitializerArgument)))))
                    .WithBody(SyntaxFactory.Block());
            }

            IEnumerable<SyntaxToken> GetModifiers(IMethodSymbol method)
            {
                switch (method.DeclaredAccessibility)
                {
                    case Accessibility.Public:
                    case Accessibility.Protected:
                        yield return SyntaxFactory.Token(SyntaxKind.PublicKeyword);
                        break;
                    case Accessibility.Internal:
                    case Accessibility.ProtectedOrInternal:
                    case Accessibility.ProtectedAndInternal:
                        yield return SyntaxFactory.Token(SyntaxKind.InternalKeyword);
                        break;
                    default:
                        break;
                }
            }

            ArgumentSyntax GetBaseInitializerArgument(IParameterSymbol parameter)
            {
                var result = SyntaxFactory.Argument(SyntaxFactory.IdentifierName(parameter.Name));
                switch (parameter.RefKind)
                {
                    case RefKind.None:
                        break;
                    case RefKind.Ref:
                        result = result.WithRefOrOutKeyword(SyntaxFactory.Token(SyntaxKind.RefKeyword));
                        break;
                    case RefKind.Out:
                        result = result.WithRefOrOutKeyword(SyntaxFactory.Token(SyntaxKind.OutKeyword));
                        break;
                    default:
                        break;
                }

                return result;
            }
        }

        private static IEnumerable<MemberDeclarationSyntax> CreateProxyMethods(
            WellKnownTypes WellKnownTypes,
            IInvokableInterfaceDescription interfaceDescription,
            MetadataModel metadataModel)
        {
            foreach (var methodDescription in interfaceDescription.Methods)
            {
                yield return CreateProxyMethod(methodDescription);
            }

            MethodDeclarationSyntax CreateProxyMethod(MethodDescription methodDescription)
            {
                var method = methodDescription.Method;
                var declaration = MethodDeclaration(method.ReturnType.ToTypeSyntax(), method.Name)
                    .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.AsyncKeyword))
                    .AddParameterListParameters(method.Parameters.Select(GetParameterSyntax).ToArray())
                    .WithBody(
                        CreateProxyMethodBody(WellKnownTypes, metadataModel, interfaceDescription, methodDescription));

                var typeParameters = GetTypeParametersWithConstraints(method.TypeParameters);
                foreach (var (name, constraints) in typeParameters)
                {
                    if (constraints.Count > 0)
                    {
                        declaration = declaration.AddConstraintClauses(
                            SyntaxFactory.TypeParameterConstraintClause(name).AddConstraints(constraints.ToArray()));
                    }
                }

                if (typeParameters.Count > 0)
                {
                    declaration = declaration.WithTypeParameterList(
                        SyntaxFactory.TypeParameterList(SyntaxFactory.SeparatedList(typeParameters.Select(tp => SyntaxFactory.TypeParameter(tp.Item1)))));
                }

                return declaration;
            }
        }

        private static BlockSyntax CreateProxyMethodBody(
            WellKnownTypes WellKnownTypes,
            MetadataModel metadataModel,
            IInvokableInterfaceDescription interfaceDescription,
            MethodDescription methodDescription)
        {
            var statements = new List<StatementSyntax>();

            // Create request object
            var requestVar = SyntaxFactory.IdentifierName("request");

            var requestDescription = metadataModel.GeneratedInvokables[methodDescription];
            var createRequestExpr = SyntaxFactory.ObjectCreationExpression(requestDescription.TypeSyntax)
                .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList<ArgumentSyntax>()));

            statements.Add(
                SyntaxFactory.LocalDeclarationStatement(
                    SyntaxFactory.VariableDeclaration(
                        SyntaxFactory.ParseTypeName("var"),
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.VariableDeclarator(
                                    SyntaxFactory.Identifier("request"))
                                .WithInitializer(
                                    SyntaxFactory.EqualsValueClause(createRequestExpr))))));

            // Set request object fields from method parameters.
            var parameterIndex = 0;
            foreach (var parameter in methodDescription.Method.Parameters)
            {
                statements.Add(
                    SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            requestVar.Member($"arg{parameterIndex}"),
                            IdentifierName(parameter.Name))));

                parameterIndex++;
            }

            // Issue request
            statements.Add(
                SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AwaitExpression(
                        SyntaxFactory.InvocationExpression(
                            SyntaxFactory.BaseExpression().Member("Invoke"),
                            SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(requestVar)))))));

            // Return result
            if (methodDescription.Method.ReturnType is INamedTypeSymbol named && named.TypeParameters.Length == 1)
            {
                statements.Add(SyntaxFactory.ReturnStatement(requestVar.Member("result")));
            }

            return SyntaxFactory.Block(statements);
        }

        private static ParameterSyntax GetParameterSyntax(IParameterSymbol parameter)
        {
            var result = SyntaxFactory.Parameter(SyntaxFactory.Identifier(parameter.Name)).WithType(parameter.Type.ToTypeSyntax());
            switch (parameter.RefKind)
            {
                case RefKind.None:
                    break;
                case RefKind.Ref:
                    result = result.WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.RefKeyword)));
                    break;
                case RefKind.Out:
                    result = result.WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.OutKeyword)));
                    break;
                case RefKind.In:
                    result = result.WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.InKeyword)));
                    break;
                default:
                    break;
            }

            return result;
        }

        private static List<FieldDescription> GetFieldDescriptions(IMethodSymbol method, WellKnownTypes WellKnownTypes)
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

        internal class CodecFieldDescription : FieldDescription, ICodecDescription
        {
            public CodecFieldDescription(ITypeSymbol fieldType, string fieldName, ITypeSymbol underlyingType)
                : base(fieldType, fieldName)
            {
                this.UnderlyingType = underlyingType;
            }

            public ITypeSymbol UnderlyingType { get; }
            public override bool IsInjected => true;
        }

        internal class TypeFieldDescription : FieldDescription
        {
            public TypeFieldDescription(ITypeSymbol fieldType, string fieldName, ITypeSymbol underlyingType) : base(
                fieldType,
                fieldName)
            {
                this.UnderlyingType = underlyingType;
            }

            public ITypeSymbol UnderlyingType { get; }
            public override bool IsInjected => false;
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