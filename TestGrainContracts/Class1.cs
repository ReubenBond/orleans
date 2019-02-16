using System;
using System.Threading.Tasks;
using Orleans;

namespace TestGrainContracts
{
    public interface IMyHappyLittleKestrelGrain : IGrainWithStringKey
    {
        Task<string> SayHelloKestrel(string name);
    }
}
