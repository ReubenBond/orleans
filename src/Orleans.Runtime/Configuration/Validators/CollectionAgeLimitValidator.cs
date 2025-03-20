// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Options;

namespace Orleans.Configuration
{
    internal class GrainCollectionOptionsValidator : IConfigurationValidator
    {
        private readonly IOptions<GrainCollectionOptions> options;

        public GrainCollectionOptionsValidator(IOptions<GrainCollectionOptions> options)
        {
            this.options = options;
        }

        public void ValidateConfiguration()
        {
            if (this.options.Value.CollectionQuantum <= TimeSpan.Zero)
            {
                throw new OrleansConfigurationException(
                    $"{nameof(GrainCollectionOptions.CollectionQuantum)} is set to {options.Value.CollectionQuantum}. " +
                    $"{nameof(GrainCollectionOptions.CollectionQuantum)} must be greater than 0");
            }

            if (this.options.Value.CollectionAge <= this.options.Value.CollectionQuantum)
            {
                throw new OrleansConfigurationException(
                    $"{nameof(GrainCollectionOptions.CollectionAge)} is set to {options.Value.CollectionAge}. " +
                    $"{nameof(GrainCollectionOptions.CollectionAge)} must be greater than {nameof(GrainCollectionOptions.CollectionQuantum)}, " +
                    $"which is set to {this.options.Value.CollectionQuantum}");
            }
            foreach(var classSpecificCollectionAge in this.options.Value.ClassSpecificCollectionAge)
            {
                if (classSpecificCollectionAge.Value <= this.options.Value.CollectionQuantum)
                {
                    throw new OrleansConfigurationException(
                        $"{classSpecificCollectionAge.Key} CollectionAgeLimit is set to {classSpecificCollectionAge.Value}. " +
                        $"CollectionAgeLimit must be greater than {nameof(GrainCollectionOptions.CollectionQuantum)}, " +
                        $"which is set to {this.options.Value.CollectionQuantum}");
                }
            }
        }
    }
}
