#nullable enable

using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Diagnostics;
using Orleans.Networking.Sockets;
using System.Diagnostics.CodeAnalysis;

namespace Orleans.Networking.Transport.Sockets;

public class TcpMessageTransportListener : MessageTransportListener
{
    private Socket? _listenSocket;

    [SetsRequiredMembers]
    internal TcpMessageTransportListener(IPEndPoint localEndpoint, ILoggerFactory loggerFactory)
    {
        Debug.Assert(localEndpoint != null);
        Debug.Assert(loggerFactory != null);

        LocalEndpoint = localEndpoint ?? throw new ArgumentNullException(nameof(localEndpoint));
        Logger = loggerFactory.CreateLogger("Orleans.Networking.Transport.Sockets");
    }

    protected ILogger Logger { get; }

    public override EndPoint LocalEndpoint { get; }

    protected virtual Socket CreateListenSocket()
    {
        var listenSocket = new Socket(LocalEndpoint!.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            LingerState = new LingerOption(true, 0),
            NoDelay = true
        };
        listenSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listenSocket.EnableFastPath();

        // IPv6Any is expected to bind to both IPv6 and IPv4
        if (LocalEndpoint is IPEndPoint ip && ip.Address == IPAddress.IPv6Any)
        {
            listenSocket.DualMode = true;
        }

        return listenSocket;
    }

    protected virtual void OnAcceptSocket(Socket socket)
    {
        socket.NoDelay = true;
    }

    public override ValueTask BindAsync(CancellationToken cancellationToken = default)
    {
        if (_listenSocket != null)
        {
            throw new InvalidOperationException("Transport already bound");
        }

        var listenSocket = CreateListenSocket();

        try
        {
            listenSocket.Bind(LocalEndpoint);
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            throw new AddressInUseException(e.Message, e);
        }

        listenSocket.Listen(512);

        _listenSocket = listenSocket;
        return default;
    }

    public override async ValueTask<MessageTransport?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            try
            {
                var acceptSocket = await _listenSocket!.AcceptAsync();
                OnAcceptSocket(acceptSocket);

                var connection = new TcpMessageTransport(acceptSocket, Logger);
                connection.Start();

                return connection;
            }
            catch (ObjectDisposedException)
            {
                // A call was made to UnbindAsync/DisposeAsync just return null which signals we're done
                return null;
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.OperationAborted)
            {
                // A call was made to UnbindAsync/DisposeAsync just return null which signals we're done
                return null;
            }
            catch (SocketException)
            {
                // The connection got reset while it was in the backlog, so we try again.
                SocketsLog.ConnectionReset(Logger, connection: "(null)");
            }
        }
    }

    public override ValueTask UnbindAsync(CancellationToken cancellationToken)
    {
        _listenSocket?.Dispose();
        return default;
    }

    public override async ValueTask DisposeAsync()
    {
        _listenSocket?.Dispose();
        GC.SuppressFinalize(this);
        await base.DisposeAsync();
    }
}
