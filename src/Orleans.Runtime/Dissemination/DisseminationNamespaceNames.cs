namespace Orleans.Runtime.Dissemination;

internal static class DisseminationNamespaceNames
{
    public static readonly DisseminationNamespace ClientDirectory = new("client-directory");
    public static readonly DisseminationNamespace ClusterManifest = new("cluster-manifest");
    public static readonly DisseminationNamespace DeploymentLoad = new("load");
    public static readonly DisseminationNamespace GrainManifest = new("grain-manifest");
    public static readonly DisseminationNamespace Membership = new("membership");
}
