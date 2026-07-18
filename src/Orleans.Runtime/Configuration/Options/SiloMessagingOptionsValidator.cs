using Microsoft.Extensions.Options;

#nullable disable
namespace Orleans.Configuration
{
    internal class SiloMessagingOptionsValidator : IValidateOptions<SiloMessagingOptions>
    {
        public ValidateOptionsResult Validate(string name, SiloMessagingOptions options)
        {
            if (options.MaxForwardCount > 255)
            {
                return ValidateOptionsResult.Fail($"Value for {nameof(SiloMessagingOptions)}.{nameof(SiloMessagingOptions.MaxForwardCount)} must not be greater than 255.");
            }

            var result = DisseminationNamespaceOptionsValidator.Validate(
                $"{nameof(SiloMessagingOptions)}.{nameof(SiloMessagingOptions.ClientDirectoryDissemination)}",
                options.ClientDirectoryDissemination);
            if (result.Failed)
            {
                return result;
            }

            result = DisseminationNamespaceOptionsValidator.Validate(
                $"{nameof(SiloMessagingOptions)}.{nameof(SiloMessagingOptions.ClusterManifestDissemination)}",
                options.ClusterManifestDissemination);
            if (result.Failed)
            {
                return result;
            }

            return DisseminationNamespaceOptionsValidator.Validate(
                $"{nameof(SiloMessagingOptions)}.{nameof(SiloMessagingOptions.SiloMetadataDissemination)}",
                options.SiloMetadataDissemination);
        }
    }
}
