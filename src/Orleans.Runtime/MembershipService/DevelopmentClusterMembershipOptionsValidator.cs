// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Runtime.MembershipService;

namespace Orleans.Configuration
{
    internal class DevelopmentClusterMembershipOptionsValidator : IConfigurationValidator
    {
        private readonly DevelopmentClusterMembershipOptions options;
        private readonly IMembershipTable membershipTable;

        public DevelopmentClusterMembershipOptionsValidator(IOptions<DevelopmentClusterMembershipOptions> options, IServiceProvider serviceProvider)
        {
            this.options = options.Value;
            this.membershipTable = serviceProvider.GetService<IMembershipTable>();
        }

        public void ValidateConfiguration()
        {
            if (this.membershipTable is SystemTargetBasedMembershipTable && this.options.PrimarySiloEndpoint is null)
            {
                throw new OrleansConfigurationException("Development clustering is enabled but no value is specified ");
            }
        }
    }
}