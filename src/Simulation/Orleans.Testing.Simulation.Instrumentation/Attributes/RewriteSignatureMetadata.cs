using System.Text.Json;

namespace Orleans.Testing.Simulation.Instrumentation.Attributes;

internal static class RewriteSignatureMetadata
{
    public const string Key = "Clockwork.RewriteSignature";

    public static string Encode(
        string engineVersion,
        string ruleSetId,
        string ruleSetVersion,
        string signature,
        string optionsFingerprint) =>
        JsonSerializer.Serialize(
            new[] { engineVersion, ruleSetId, ruleSetVersion, signature, optionsFingerprint });

    public static bool TryDecode(
        string? value,
        out string engineVersion,
        out string ruleSetId,
        out string ruleSetVersion,
        out string signature,
        out string optionsFingerprint)
    {
        engineVersion = string.Empty;
        ruleSetId = string.Empty;
        ruleSetVersion = string.Empty;
        signature = string.Empty;
        optionsFingerprint = string.Empty;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        try
        {
            string[]? values = JsonSerializer.Deserialize<string[]>(value);
            if (values is not { Length: 5 } || values.Any(item => item is null))
            {
                return false;
            }

            engineVersion = values[0];
            ruleSetId = values[1];
            ruleSetVersion = values[2];
            signature = values[3];
            optionsFingerprint = values[4];
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
