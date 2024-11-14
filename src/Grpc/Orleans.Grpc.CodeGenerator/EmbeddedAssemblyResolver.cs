using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Orleans.Grpc.CodeGenerator;

internal static class EmbeddedAssemblyResolver
{
    public static Assembly AssemblyResolve(object sender, ResolveEventArgs args)
    {
        if (args.Name.Contains("System.Runtime.Loader"))
        {
            return null!;
        }

        return LoadFromResource(args);
    }

    private static Assembly LoadFromResource(ResolveEventArgs args)
    {
        var type = typeof(ProtoProcessor);
        var assemblyName = new AssemblyName(args.Name);
        using var stream = type.Assembly.GetManifestResourceStream($"{type.Namespace}.{assemblyName.Name}.dll");
        if (stream is null)
        {
            return null!;
        }

        var isFramework = RuntimeInformation.FrameworkDescription.Contains(".NET Framework");
        if (isFramework)
        {
            return LoadNetFramework(stream);
        }
        else
        {
            return LoadNetCore(stream);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Assembly LoadNetCore(Stream asm) => System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(asm);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Assembly LoadNetFramework(Stream asm)
    {
        using var memStream = new MemoryStream();
        asm.CopyTo(memStream);
        return (Assembly)typeof(Assembly).InvokeMember(
            "Load",
            BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Static,
            null,
            null,
            [memStream.ToArray()]);
    }
}