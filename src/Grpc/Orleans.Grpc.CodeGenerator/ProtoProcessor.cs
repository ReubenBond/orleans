using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.CodeAnalysis.Text;
using Google.Protobuf.Reflection;
using System.Text;
using Orleans.Grpc.CodeGenerator.Internal;
using ProtoBuf.Reflection;
using Fluid;
using System.Globalization;

namespace Orleans.Grpc.CodeGenerator;

internal static class ProtoProcessor
{
    internal static IEnumerable<GeneratorOutput> Generate(
        GeneratorInput input,
        CancellationToken cancellationToken)
    {
        var file = input.File;
        var protos = new FileDescriptorSet();
        protos.AddImportPath(Path.GetDirectoryName(file.Path)!);
        foreach (var path in input.FileOptions.GetMetadataValues("AdditionalImportDirs"))
        {
            protos.AddImportPath(path);
        }

        protos.Add(Path.GetFileName(file.Path), includeInOutput: true, new AdditionalFileTextReader(file.GetText(cancellationToken)!));
        protos.Process();
        var errors = protos.GetErrors();
        if (errors is { Length: > 0 })
        {
            yield return new GeneratorOutput(file.Path, null, new ErrorList(errors));
            yield break;
        }

        var generator = new ProtobufNetCodeGenerator(input);
        foreach (var output in generator.Generate(protos))
        {
            yield return new GeneratorOutput(output.Name, SourceText.From(output.Text, Encoding.UTF8), default);
        }
    }
}

file sealed class AdditionalFileTextReader(SourceText sourceText) : TextReader
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

file sealed class ProtobufNetCodeGenerator(GeneratorInput input) : CommonCodeGenerator
{
    public override string Name => "Orleans.ProtocolBuffers.SourceGenerator";
    protected override string DefaultFileExtension => ".cs";

    protected override void WriteFile(GeneratorContext ctx, FileDescriptorProto file)
    {
        var grainServices = input.FileOptions.GetMetadataValues("GrainServices");
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
                    InputType: DescriptorNames.Create(inputType),
                    OutputType: DescriptorNames.Create(outputType),
                    IsClientStreaming: method.ClientStreaming,
                    IsServerStreaming: method.ServerStreaming));
            }

            services.Add(new ServiceModel(service.Name, methods));
        }

        var ns = file.Options?.CsharpNamespace ?? file.Package;
        var model = new FileModel(file.Name, file.Package, ns, services, grainServices);
        var templateContext = new TemplateContext(model);
        templateContext.Options.MemberAccessStrategy = new UnsafeMemberAccessStrategy();
        var templates = new Templates();
        ctx.Output.Write(templates.ClientProxy.Render(templateContext));

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

file record class FileModel(string Name, string Package, string Namespace, List<ServiceModel> Services, string[] GrainServices);
file record class ServiceModel(string Name, List<MethodModel> Methods);
file record class MethodModel(
    string Name,
    DescriptorNames InputType,
    DescriptorNames OutputType,
    bool IsClientStreaming,
    bool IsServerStreaming)
{
    public bool IsVoidReturn => IsVoid(InputType.PbQualifiedName);
    public bool IsVoidInput => IsVoid(InputType.PbQualifiedName);
    private static bool IsVoid(string type) => type.Equals(".google.protobuf.Empty", StringComparison.Ordinal);
}

file record class DescriptorNames(string PbName, string CsName, string PbQualifiedName, string CsQualifiedName)
{
    public static DescriptorNames Create(DescriptorProto descriptor)
    {
        var pbName = descriptor.Name;
        var csName = pbName.ToPascalCase();
        var pbQualifiedName = descriptor.GetFullyQualifiedName();
        var csQualifiedName = pbQualifiedName.ToPascalCase();
        return new DescriptorNames(pbName, csName, pbQualifiedName, csQualifiedName);
    }
}

file static class StringExtensions
{
    public static string ToPascalCase(this string snakeCaseString)
    {
        if (string.IsNullOrEmpty(snakeCaseString))
        {
            return snakeCaseString;
        }

        var words = snakeCaseString.Split('_');
        var result = new StringBuilder();

        foreach (var word in words)
        {
            if (word.Length > 0)
            {
                result.Append(char.ToUpper(word[0], CultureInfo.InvariantCulture));
                if (word.Length > 1)
                {
                    result.Append(word.Substring(1).ToLower(CultureInfo.InvariantCulture));
                }
            }
        }

        return result.ToString();
    }
}
