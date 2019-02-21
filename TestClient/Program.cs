using System;
using System.Threading.Tasks;
using Orleans;

namespace TestClient
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var client = new ClientBuilder()
                .UseLocalhostClustering(gatewayPort: 60777)
                .Build();
            await client.Connect(ex => Task.FromResult(true));

            var grain = client.GetGrain<TestGrainContracts.IMyHappyLittleKestrelGrain>(50);

            while (true)
            {
                var message = await grain.SayHelloKestrel("Reuben");
                await Task.Delay(1000);
            }
        }
    }
}
