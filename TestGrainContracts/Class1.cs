using System;
using System.Threading.Tasks;
using Orleans;

namespace TestGrainContracts
{
    public interface IMyHappyLittleKestrelGrain : IGrainWithIntegerKey
    {
        Task<string> SayHelloKestrel(string name);
        Task<string> HelloChain(int id);
    }
}
