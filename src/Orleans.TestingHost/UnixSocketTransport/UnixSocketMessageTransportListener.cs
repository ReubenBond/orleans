/*
#nullable enable

using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Connections.Transport;
using Orleans.Connections.Transport.Sockets;

namespace Orleans.TestingHost.UnixSocketTransport;

internal class UnixSocketMessageTransportListener : MessageTransportListener
{
    private Socket? _listenSocket;

    internal UnixSocketMessageTransportListener(UnixDomainSocketEndPoint localEndpoint, EndPoint endpoint, ILoggerFactory loggerFactory)
    {
        Debug.Assert(localEndpoint != null);
        Debug.Assert(loggerFactory != null);

        LocalEndpoint = localEndpoint;
        Logger = loggerFactory.CreateLogger("Orleans.Connections.Transport.Sockets");
    }

    protected ILogger Logger { get; }

    public override ValueTask BindAsync(CancellationToken cancellationToken = default)
    {
        if (_listenSocket != null)
        {
            throw new InvalidOperationException("Transport already bound");
        }

        if (LocalEndpoint is null)
        {
            throw new ArgumentNullException(nameof(LocalEndpoint));
        }

        var listenSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
        {
            LingerState = new LingerOption(true, 0),
            NoDelay = true
        };

        try
        {
            listenSocket.Bind(LocalEndpoint);
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            throw new AddressInUseException(e.Message, e);
        }

        LocalEndpoint = listenSocket.LocalEndPoint;

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
*/
