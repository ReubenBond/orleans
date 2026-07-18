namespace Orleans.Runtime.Dissemination;

internal static class DisseminationNamespaceNames
{
    public static readonly DisseminationNamespace ClientDirectory = new("client-directory");
    public static readonly DisseminationNamespace DeploymentLoad = new("load");
    public static readonly DisseminationNamespace Membership = new("membership");
    public static readonly DisseminationNamespace RebalancingReport = new("rebalancing-report");
    public static readonly DisseminationNamespace SiloMetadata = new("silo-metadata");
}
