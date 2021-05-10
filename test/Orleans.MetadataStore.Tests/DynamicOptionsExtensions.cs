using System;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Orleans.MetadataStore.Tests
{
    public static class DynamicOptionsExtensions
    {
        public static void AddDynamicOptions<T>(this IServiceCollection services, string name = "") where T : class
        {
            services.AddOptions();

            var configureOptions = new OptionsRefresher<T>(name);
            services.AddSingleton(configureOptions);
            services.AddSingleton<IOptionsChangeTokenSource<T>>(configureOptions);
            services.AddSingleton<IConfigureOptions<T>>(configureOptions);
        }

        public static OptionsRefresher<T> GetOptionsUpdater<T>(this IServiceProvider serviceProvider, string name = "") where T : class
        {
            return serviceProvider.GetServices<OptionsRefresher<T>>().Single(o => string.Equals(o.Name, name));
        }

        public class OptionsRefresher<T> : IConfigureNamedOptions<T>, IOptionsChangeTokenSource<T> where T : class
        {
            private CancellationTokenSource reloadToken = new CancellationTokenSource();

            internal OptionsRefresher(string name)
            {
                this.Name = name;
            }

            public string Name { get; private set; }

            public Action<T> ConfigureOptions { get; set; }

            public void Reload()
            {
                var previousToken = Interlocked.Exchange(ref this.reloadToken, new CancellationTokenSource());
                previousToken.Cancel();
            }

            void IConfigureOptions<T>.Configure(T options) => this.ConfigureOptions?.Invoke(options);

            IChangeToken IOptionsChangeTokenSource<T>.GetChangeToken() => new CancellationChangeToken(reloadToken.Token);

            void IConfigureNamedOptions<T>.Configure(string name, T options)
            {
                if (string.Equals(this.Name, name))
                {
                    this.ConfigureOptions?.Invoke(options);
                }
            }
        }
    }
}
