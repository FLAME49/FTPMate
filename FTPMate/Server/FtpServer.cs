using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using FTPMate.Helpers;

namespace FTPMate.Server;

public sealed class FtpServer
{
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public string Root { get; set; } = "";
    public bool ShareAllDrives { get; set; }
    public int Port { get; set; } = 2121;
    public string Username { get; set; } = "ftp";
    public string Password { get; set; } = "";

    public bool AuthRequired => Password.Length > 0;
    public int ClientCount => _clients.Count;
    public bool IsRunning => _listener is not null;

    public event Action<string>? Logged;
    public event Action? ClientsChanged;

    internal IReadOnlyList<ShareMount> Mounts { get; private set; } = Array.Empty<ShareMount>();

    public void Start()
    {
        if (IsRunning)
            return;

        var mounts = BuildMounts();
        if (mounts.Count == 0)
            throw new DirectoryNotFoundException(Root);
        if (Port < 1 || Port > 65535)
            throw new ArgumentOutOfRangeException(nameof(Port));

        var listener = new TcpListener(IPAddress.Any, Port);
        listener.Start(16);

        Mounts = mounts;
        _listener = listener;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
        Log($"Listening on port {Port}");
    }

    private List<ShareMount> BuildMounts()
    {
        if (ShareAllDrives)
        {
            var mounts = new List<ShareMount>();
            foreach (var drive in Partitions.Ready())
                mounts.Add(new ShareMount(Partitions.Letter(drive), drive.RootDirectory.FullName));
            return mounts;
        }

        var root = Path.GetFullPath(Root);
        return Directory.Exists(root)
            ? new List<ShareMount> { new("", root) }
            : new List<ShareMount>();
    }

    public void Stop()
    {
        if (_listener is null)
            return;

        try { _listener.Stop(); } catch { }
        try { _cts?.Cancel(); } catch { }

        foreach (var client in _clients.Keys)
        {
            try { client.Close(); } catch { }
            _clients.TryRemove(client, out _);
        }

        _listener = null;
        _cts?.Dispose();
        _cts = null;
        ClientsChanged?.Invoke();
        Log("Server stopped");
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }

            client.NoDelay = true;
            _clients.TryAdd(client, 0);
            ClientsChanged?.Invoke();

            var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
            Log($"Connection from {remote}");

            _ = Task.Run(async () =>
            {
                var session = new FtpSession(this, client, remote);
                try { await session.RunAsync(token); }
                catch (Exception ex) { Log($"Disconnected {remote}: {ex.Message}"); }
                finally
                {
                    try { client.Close(); } catch { }
                    _clients.TryRemove(client, out _);
                    ClientsChanged?.Invoke();
                    Log($"Connection closed {remote}");
                }
            }, token);
        }
    }

    internal void Log(string message) => Logged?.Invoke(message);
}
