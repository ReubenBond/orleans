using Orleans.Runtime;

namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.ITestExtension")]
    public interface ITestExtension : IGrainExtension
    {
        [Alias("CheckExtension_1")]
        Task<string> CheckExtension_1();

        [Alias("CheckExtension_2")]
        Task<string> CheckExtension_2();
    }

    [Alias("UnitTests.GrainInterfaces.IGenericTestExtension`1")]
    public interface IGenericTestExtension<T> : IGrainExtension
    {
        [Alias("CheckExtension_1")]
        Task<T> CheckExtension_1();

        [Alias("CheckExtension_2")]
        Task<string> CheckExtension_2();
    }

    [Alias("UnitTests.GrainInterfaces.ISimpleExtension")]
    public interface ISimpleExtension : IGrainExtension
    {
        [Alias("CheckExtension_1")]
        Task<string> CheckExtension_1();
    }

    [Alias("UnitTests.GrainInterfaces.IAutoExtension")]
    public interface IAutoExtension : IGrainExtension
    {
        [Alias("CheckExtension")]
        Task<string> CheckExtension();
    }
}