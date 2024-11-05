using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ProtoBuf.Reflection;
using Google.Protobuf.Reflection;
using Fluid;

namespace Orleans.Grpc.CodeGenerator;

[Generator(LanguageNames.CSharp)]
public sealed class GrpcGrainSourceGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var sourceProtoFiles = context.AdditionalTextsProvider
            .Where(static file => file.Path.EndsWith(".proto"))
            .SelectMany(static (file, ct) => GrpcGrainGeneratorCore.GenerateSourceForInput(file, ct));

        context.RegisterSourceOutput(
            sourceProtoFiles,
            static (context, output) =>
            {
                var errors = output.Errors.Errors;
                if (errors.Length > 0)
                {
                    foreach (var error in errors)
                    {
                        Console.WriteLine("Error: " + error);
                        //context.ReportDiagnostic(Diagnostic.Create(error.ErrorNumber, error.Message, error.LineNumber, error.ColumnNumber, error.IsError));
                    }

                    return;
                }

                context.AddSource(output.HintName, output.SourceText!);
            });
    }
}

internal readonly record struct GeneratorOutput(
    string HintName,
    SourceText? SourceText,
    ErrorList Errors);

internal readonly struct ErrorList(Error[] errors)
{
    public Error[] Errors { get; } = errors;

    public override int GetHashCode()
    {
        if (Errors == null)
        {
            return 0;
        }

        var hashCode = new HashCode();
        hashCode.Add(Errors.Length);
        foreach (var value in Errors)
        {
            hashCode.Add(value.ErrorNumber);
            hashCode.Add(value.LineNumber);
            hashCode.Add(value.ColumnNumber);
            hashCode.Add(value.IsError);
            hashCode.Add(value.Text);
            hashCode.Add(value.File);
            hashCode.Add(value.Message);
        }

        return hashCode.ToHashCode();
    }

    public override bool Equals(object obj) => obj is ErrorList other && Equals(other);

    public bool Equals(ErrorList other)
    {
        if (ReferenceEquals(Errors, other.Errors))
        {
            return true;
        }

        if (Errors == null || other.Errors == null)
        {
            return false;
        }

        if (Errors.Length != other.Errors.Length)
        {
            return false;
        }

        for (var i = 0; i < Errors.Length; i++)
        {
            var left = Errors[i];
            var right = other.Errors[i];
            if (!Equals(left, right))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Equals(Error left, Error right) =>
        left.ErrorNumber == right.ErrorNumber
        && left.LineNumber == right.LineNumber
        && left.ColumnNumber == right.ColumnNumber
        && left.IsError == right.IsError
        && string.Equals(left.Text, right.Text, StringComparison.Ordinal)
        && string.Equals(left.File, right.File, StringComparison.Ordinal)
        && string.Equals(left.Message, right.Message, StringComparison.Ordinal);
}

internal readonly record struct FileModel(string Name, string Package, string Namespace, List<ServiceModel> Services);
internal readonly record struct ServiceModel(string Name, List<MethodModel> Methods);
internal readonly record struct MethodModel(
    string Name,
    string InputType,
    string OutputType,
    string QualifiedInputType,
    string QualifiedOutputType,
    bool IsClientStreaming,
    bool IsServerStreaming)
{
    public bool IsVoidReturn => IsVoid(OutputType);
    public bool IsVoidInput => IsVoid(InputType);
    private static bool IsVoid(string type) => type.Equals(".google.protobuf.Empty", StringComparison.Ordinal); 
}

internal static class GrpcGrainGeneratorCore
{
    internal static IEnumerable<GeneratorOutput> GenerateSourceForInput(AdditionalText file, CancellationToken cancellationToken)
    {
        var protos = new FileDescriptorSet();
        protos.Add(file.Path, includeInOutput: true, new AdditionalFileTextReader(file.GetText(cancellationToken)!));
        protos.Process();
        var errors = protos.GetErrors();
        if (errors is { Length: > 0 })
        {
            yield return new GeneratorOutput(file.Path, null, new ErrorList(errors));
        }

        var generator = new ProtobufCodeGenerator();
        foreach (var output in generator.Generate(protos))
        {
            yield return new GeneratorOutput(output.Name, SourceText.From(output.Text), default);
        }
    }

    private sealed class ProtobufCodeGenerator : CommonCodeGenerator
    {
        public override string Name => "Orleans.Grpc";
        protected override string DefaultFileExtension => ".cs";

        protected override void WriteFile(GeneratorContext ctx, FileDescriptorProto file)
        {
            var services = new List<ServiceModel>(file.Services.Count);
            foreach (var service in file.Services)
            {
                // TODO: check if service is a 'grain' via custom options.
                var methods = new List<MethodModel>(service.Methods.Count);

                foreach (var method in service.Methods)
                {
                    if (method.ClientStreaming || method.ServerStreaming)
                    {
                        throw new InvalidOperationException($"Service '{service.Name}' method '{method.Name}' is a streaming methods, which is not supported.");
                    }

                    var inputType = GetType(method.InputType);
                    var outputType = GetType(method.OutputType);
                    methods.Add(new MethodModel(
                        Name: method.Name,
                        InputType: method.InputType,
                        OutputType: method.OutputType,
                        QualifiedInputType: inputType.GetFullyQualifiedName(),
                        QualifiedOutputType: outputType.GetFullyQualifiedName(),
                        IsClientStreaming: method.ClientStreaming,
                        IsServerStreaming: method.ServerStreaming));
                }

                services.Add(new ServiceModel(service.Name, methods));
            }

            var ns = file.Options.CsharpNamespace;
            var model = new FileModel(file.Name, file.Package, ns, services);
            var templateContext = new TemplateContext(model);
            ctx.Output.Write(Templates.ClientProxy.Render(templateContext));

            DescriptorProto GetType(string typeName) => ctx.TryFind<DescriptorProto>(typeName) ?? throw new InvalidOperationException($"Unable to find type, '{typeName}'.");
        }

        protected override string Escape(string identifier) => identifier;
        protected override void WriteEnumFooter(GeneratorContext ctx, EnumDescriptorProto @enum, ref object state) { }
        protected override void WriteEnumHeader(GeneratorContext ctx, EnumDescriptorProto @enum, ref object state) { }
        protected override void WriteEnumValue(GeneratorContext ctx, EnumValueDescriptorProto @enum, ref object state) { }
        protected override void WriteField(GeneratorContext ctx, FieldDescriptorProto field, ref object state, OneOfStub[] oneOfs) { }
        protected override void WriteMessageFooter(GeneratorContext ctx, DescriptorProto message, ref object state) { }
        protected override void WriteMessageHeader(GeneratorContext ctx, DescriptorProto message, ref object state) { }
        protected override void WriteNamespaceFooter(GeneratorContext ctx, string @namespace) { }
        protected override void WriteNamespaceHeader(GeneratorContext ctx, string @namespace) { }
    }
}

internal sealed class AdditionalFileTextReader(SourceText sourceText) : TextReader
{
    private int _position;
    public override int Peek() => _position >= sourceText.Length ? -1 : sourceText[_position];
    public override int Read()
    {
        var res = Peek();
        ++_position;
        return res;
    }

    public override int Read(char[] buffer, int index, int count)
    {
        var len = sourceText.Length - _position;
        if (len <= 0)
        {
            return 0;
        }

        sourceText.CopyTo(_position, buffer, index, Math.Min(buffer.Length - index, count));
        _position += len;
        return len;
    }
}

