using System.IO;
using System.Reflection;
using Fluid;

namespace Orleans.Grpc.CodeGenerator;

internal class Templates
{
    public Templates()
    {
        var parser = new FluidParser();
        ClientProxy = parser.Parse(GetTemplate("ClientProxy.liquid"));

        static string GetTemplate(string name)
        {
            var type = typeof(Templates);
            using var stream = type.Assembly.GetManifestResourceStream($"{type.Namespace}.{type.Name}.{name}");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    public IFluidTemplate ClientProxy { get; }
}
