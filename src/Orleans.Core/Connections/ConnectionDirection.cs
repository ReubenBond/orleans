namespace Orleans.Messaging
{
    internal enum ConnectionDirection : byte
    {
        SiloToSilo,
        ClientToGateway,
        GatewayToClient
    }

    internal interface IConnectionDirectionFeature
    {
        public ConnectionDirection Direction { get; }
    }

    internal class ConnectionDirectionFeature : IConnectionDirectionFeature
    {
        public static ConnectionDirectionFeature GatewayToClient { get; } = new ConnectionDirectionFeature(ConnectionDirection.GatewayToClient);
        public static ConnectionDirectionFeature SiloToSilo { get; } = new ConnectionDirectionFeature(ConnectionDirection.SiloToSilo);
        public static ConnectionDirectionFeature ClientToGateway { get; } = new ConnectionDirectionFeature(ConnectionDirection.ClientToGateway);

        private ConnectionDirectionFeature(ConnectionDirection direction)
        {
            Direction = direction;
        }

        public ConnectionDirection Direction { get; }
    }
}
