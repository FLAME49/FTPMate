using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FTPMate.Server;

internal sealed class FtpSession
{
    private readonly FtpServer _server;
    private readonly TcpClient _control;
    private readonly NetworkStream _stream;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly string _remote;
    private readonly byte[] _buffer = new byte[64 * 1024];

    private TcpListener? _passive;
    private string _cwd = "/";
    private bool _authenticated;
    private string? _renameFrom;
    private long _restartOffset;

    public FtpSession(FtpServer server, TcpClient control, string remote)
    {
        _server = server;
        _control = control;
        _remote = remote;
        _stream = control.GetStream();
        var utf8 = new UTF8Encoding(false);
        _reader = new StreamReader(_stream, utf8, false, 8192, leaveOpen: true);
        _writer = new StreamWriter(_stream, utf8, 8192, leaveOpen: true)
        {
            NewLine = "\r\n",
            AutoFlush = true
        };
    }

    public async Task RunAsync(CancellationToken token)
    {
        Reply(220, "FTPMate ready.");
        while (!token.IsCancellationRequested)
        {
            string? line;
            try { line = await _reader.ReadLineAsync(token); }
            catch { break; }
            if (line is null)
                break;

            line = line.TrimEnd('\r');
            if (line.Length == 0)
                continue;

            int space = line.IndexOf(' ');
            string cmd = (space < 0 ? line : line[..space]).ToUpperInvariant();
            string arg = space < 0 ? "" : line[(space + 1)..];

            bool keepGoing;
            try { keepGoing = await HandleAsync(cmd, arg, token); }
            catch (OperationCanceledException) { break; }
            catch (IOException) { break; }
            catch (Exception ex)
            {
                _server.Log($"{_remote}: {ex.Message}");
                Reply(451, "Requested action aborted.");
                keepGoing = true;
            }

            if (!keepGoing)
                break;
        }

        try { _passive?.Stop(); } catch { }
    }

    private async Task<bool> HandleAsync(string cmd, string arg, CancellationToken token)
    {
        if (!_authenticated && cmd is not ("USER" or "PASS" or "QUIT" or "FEAT" or "OPTS" or "SYST" or "NOOP"))
        {
            Reply(530, "Please login first.");
            return true;
        }

        switch (cmd)
        {
            case "USER":
                if (_server.AuthRequired)
                {
                    _user = arg.Trim();
                    Reply(331, "Password required.");
                }
                else
                {
                    _authenticated = true;
                    Reply(230, "Anonymous access granted.");
                }
                return true;

            case "PASS":
                if (_authenticated)
                    Reply(230, "Already logged in.");
                else if (!_server.AuthRequired)
                {
                    _authenticated = true;
                    Reply(230, "Anonymous access granted.");
                }
                else if (string.Equals(_user, _server.Username, StringComparison.OrdinalIgnoreCase)
                         && arg == _server.Password)
                {
                    _authenticated = true;
                    _server.Log($"{_remote}: login successful");
                    Reply(230, "Login successful.");
                }
                else
                {
                    _server.Log($"{_remote}: login failed");
                    Reply(530, "Login incorrect.");
                }
                return true;

            case "QUIT":
                Reply(221, "Goodbye.");
                return false;

            case "NOOP":
                Reply(200, "OK.");
                return true;

            case "SYST":
                Reply(215, "Windows_NT");
                return true;

            case "FEAT":
                _writer.Write("211-Features:\r\n UTF8\r\n EPSV\r\n PASV\r\n SIZE\r\n MDTM\r\n REST STREAM\r\n TVFS\r\n211 End\r\n");
                return true;

            case "OPTS":
                Reply(200, "OK.");
                return true;

            case "TYPE":
                Reply(200, "Type set.");
                return true;

            case "MODE":
            case "STRU":
                Reply(200, "OK.");
                return true;

            case "PWD":
            case "XPWD":
                Reply(257, $"\"{_cwd.Replace("\"", "\"\"")}\" is the current directory.");
                return true;

            case "CWD":
            case "XCWD":
            {
                var virt = NormalizeVirtual(arg);
                if (virt is null)
                {
                    Reply(550, "Directory not found.");
                    return true;
                }

                if (virt == "/")
                {
                    _cwd = "/";
                    Reply(250, "Directory changed.");
                    return true;
                }

                var path = Resolve(arg);
                if (path is null || !Directory.Exists(path))
                    Reply(550, "Directory not found.");
                else
                {
                    _cwd = ToVirtual(path);
                    Reply(250, "Directory changed.");
                }
                return true;
            }

            case "CDUP":
            case "XCUP":
            {
                if (_cwd != "/")
                {
                    int slash = _cwd.LastIndexOf('/');
                    _cwd = slash <= 0 ? "/" : _cwd[..slash];
                }
                Reply(250, "Directory changed.");
                return true;
            }

            case "PASV":
                EnterPassive();
                return true;

            case "EPSV":
                EnterExtendedPassive();
                return true;

            case "PORT":
            case "EPRT":
                Reply(502, "Only passive mode is supported.");
                return true;

            case "LIST":
            case "NLST":
                await SendListingAsync(cmd == "NLST", arg, token);
                return true;

            case "RETR":
                await SendFileAsync(arg, token);
                return true;

            case "STOR":
            case "APPE":
                await ReceiveFileAsync(arg, cmd == "APPE", token);
                return true;

            case "DELE":
            {
                var path = Resolve(arg);
                if (path is null || !File.Exists(path))
                    Reply(550, "File not found.");
                else
                {
                    File.Delete(path);
                    Reply(250, "File deleted.");
                }
                return true;
            }

            case "MKD":
            case "XMKD":
            {
                var path = Resolve(arg);
                if (path is null || arg.Trim().Length == 0)
                    Reply(550, "Invalid path.");
                else
                {
                    Directory.CreateDirectory(path);
                    Reply(257, $"\"{ToVirtual(path)}\" created.");
                }
                return true;
            }

            case "RMD":
            case "XRMD":
            {
                var path = Resolve(arg);
                if (path is null || !Directory.Exists(path) || IsMountRoot(path))
                    Reply(550, "Directory not found.");
                else
                {
                    Directory.Delete(path, false);
                    Reply(250, "Directory removed.");
                }
                return true;
            }

            case "RNFR":
            {
                var path = Resolve(arg);
                if (path is null || IsMountRoot(path) || (!File.Exists(path) && !Directory.Exists(path)))
                    Reply(550, "Path not found.");
                else
                {
                    _renameFrom = path;
                    Reply(350, "Ready for RNTO.");
                }
                return true;
            }

            case "RNTO":
            {
                if (_renameFrom is null)
                {
                    Reply(503, "Send RNFR first.");
                    return true;
                }
                var to = Resolve(arg);
                if (to is null)
                {
                    Reply(550, "Invalid target.");
                    return true;
                }
                try
                {
                    if (Directory.Exists(_renameFrom)) Directory.Move(_renameFrom, to);
                    else File.Move(_renameFrom, to, overwrite: true);
                    Reply(250, "Renamed.");
                }
                finally { _renameFrom = null; }
                return true;
            }

            case "SIZE":
            {
                var path = Resolve(arg);
                if (path is null || !File.Exists(path))
                    Reply(550, "File not found.");
                else
                    Reply(213, new FileInfo(path).Length.ToString(CultureInfo.InvariantCulture));
                return true;
            }

            case "MDTM":
            {
                var path = Resolve(arg);
                if (path is null || !File.Exists(path))
                    Reply(550, "File not found.");
                else
                    Reply(213, File.GetLastWriteTime(path).ToUniversalTime().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));
                return true;
            }

            case "REST":
                _restartOffset = long.TryParse(arg.Trim(), out var offset) && offset > 0 ? offset : 0;
                Reply(350, "Restarting at requested offset.");
                return true;

            case "ABOR":
                Reply(226, "Aborted.");
                return true;

            default:
                Reply(502, "Command not implemented.");
                return true;
        }
    }

    private string _user = "";

    private void EnterPassive()
    {
        try { _passive?.Stop(); } catch { }

        var address = ((IPEndPoint)_control.Client.LocalEndPoint!).Address;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            Reply(522, "Use EPSV instead.");
            return;
        }

        var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start(1);
        _passive = listener;

        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var b = address.GetAddressBytes();
        Reply(227, $"Entering Passive Mode ({b[0]},{b[1]},{b[2]},{b[3]},{port / 256},{port % 256}).");
    }

    private void EnterExtendedPassive()
    {
        try { _passive?.Stop(); } catch { }

        var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start(1);
        _passive = listener;

        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Reply(229, $"Entering Extended Passive Mode (|||{port}|).");
    }

    private async Task<TcpClient?> AcceptDataAsync()
    {
        var listener = _passive;
        _passive = null;
        if (listener is null)
            return null;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            return await listener.AcceptTcpClientAsync(timeout.Token);
        }
        catch
        {
            return null;
        }
        finally
        {
            try { listener.Stop(); } catch { }
        }
    }

    private async Task SendListingAsync(bool namesOnly, string arg, CancellationToken token)
    {
        string raw = arg.Trim();
        if (raw.StartsWith('-'))
            raw = "";

        var virt = NormalizeVirtual(raw);
        if (virt is null)
        {
            Reply(550, "Path not found.");
            return;
        }

        string target = "";
        bool virtualRoot = false;
        bool singleFile = false;

        if (virt == "/" && !IsSingleRoot)
        {
            virtualRoot = true;
        }
        else
        {
            var resolved = Resolve(raw);
            if (resolved is null)
            {
                Reply(550, "Path not found.");
                return;
            }

            target = resolved;
            singleFile = File.Exists(target);
            if (!singleFile && !Directory.Exists(target))
            {
                Reply(550, "Path not found.");
                return;
            }
        }

        Reply(150, "Opening data connection.");
        using var data = await AcceptDataAsync();
        if (data is null)
        {
            Reply(425, "Cannot open data connection.");
            return;
        }

        try
        {
            var sb = new StringBuilder();
            if (virtualRoot)
            {
                AppendVirtualEntry(sb, ".", DateTime.Now, namesOnly);
                foreach (var mount in _server.Mounts)
                    AppendVirtualEntry(sb, mount.Name, MountTime(mount.Path), namesOnly);
            }
            else if (singleFile)
            {
                AppendEntry(sb, new FileInfo(target), namesOnly);
            }
            else
            {
                var dir = new DirectoryInfo(target);
                AppendEntry(sb, dir, namesOnly, isDot: true, name: ".");
                var parent = Directory.GetParent(dir.FullName);
                if (parent is not null && IsInsideAnyRoot(parent.FullName))
                    AppendEntry(sb, parent, namesOnly, isDot: true, name: "..");

                foreach (var entry in dir.EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                    AppendEntry(sb, entry, namesOnly);
            }

            var bytes = new UTF8Encoding(false).GetBytes(sb.ToString());
            await data.GetStream().WriteAsync(bytes, token);
            data.Close();
            Reply(226, "Directory listing sent.");
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            Reply(426, "Transfer aborted.");
        }
    }

    private static void AppendVirtualEntry(StringBuilder sb, string name, DateTime writeTime, bool namesOnly)
    {
        if (namesOnly)
        {
            sb.Append(name).Append("\r\n");
            return;
        }

        string date = writeTime.ToString("MMM dd HH:mm", CultureInfo.InvariantCulture);
        sb.Append("drwxr-xr-x   1 ftp      ftp      ")
          .Append("0".PadLeft(12))
          .Append(' ').Append(date).Append(' ').Append(name).Append("\r\n");
    }

    private static DateTime MountTime(string path)
    {
        try { return Directory.GetLastWriteTime(path); }
        catch { return DateTime.Now; }
    }

    private static void AppendEntry(StringBuilder sb, FileSystemInfo entry, bool namesOnly, bool isDot = false, string? name = null)
    {
        name ??= entry.Name;
        if (namesOnly)
        {
            sb.Append(name).Append("\r\n");
            return;
        }

        bool isDir = entry is DirectoryInfo;
        long size = entry is FileInfo f ? f.Length : 0;
        string date = entry.LastWriteTime.ToString("MMM dd HH:mm", CultureInfo.InvariantCulture);
        string perms = isDir ? "drwxr-xr-x" : "-rw-r--r--";
        sb.Append(perms).Append("   1 ftp      ftp      ")
          .Append(size.ToString(CultureInfo.InvariantCulture).PadLeft(12))
          .Append(' ').Append(date).Append(' ').Append(name).Append("\r\n");
    }

    private async Task SendFileAsync(string arg, CancellationToken token)
    {
        var path = Resolve(arg);
        if (path is null || !File.Exists(path))
        {
            Reply(550, "File not found.");
            return;
        }

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (_restartOffset > 0 && _restartOffset <= stream.Length)
                stream.Seek(_restartOffset, SeekOrigin.Begin);
            _restartOffset = 0;

            Reply(150, "Opening data connection.");
            using var data = await AcceptDataAsync();
            if (data is null)
            {
                Reply(425, "Cannot open data connection.");
                return;
            }

            await stream.CopyToAsync(data.GetStream(), _buffer.Length, token);
            data.Close();
            Reply(226, "Transfer complete.");
            _server.Log($"{_remote}: downloaded {Path.GetFileName(path)}");
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            Reply(426, "Transfer aborted.");
        }
        finally
        {
            await stream.DisposeAsync();
        }
    }

    private async Task ReceiveFileAsync(string arg, bool append, CancellationToken token)
    {
        var path = Resolve(arg);
        if (path is null)
        {
            Reply(550, "Invalid path.");
            return;
        }

        var stream = new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);
        try
        {
            if (!append && _restartOffset > 0)
                stream.Seek(_restartOffset, SeekOrigin.Begin);
            _restartOffset = 0;

            Reply(150, "Opening data connection.");
            using var data = await AcceptDataAsync();
            if (data is null)
            {
                Reply(425, "Cannot open data connection.");
                return;
            }

            await data.GetStream().CopyToAsync(stream, _buffer.Length, token);
            data.Close();
            Reply(226, "Transfer complete.");
            _server.Log($"{_remote}: uploaded {Path.GetFileName(path)}");
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            Reply(426, "Transfer aborted.");
        }
        finally
        {
            await stream.DisposeAsync();
        }
    }

    private string? NormalizeVirtual(string arg)
    {
        string normalized = arg.Replace('\\', '/').Trim();
        var segments = new List<string>();

        if (!normalized.StartsWith('/'))
            segments.AddRange(_cwd.Split('/', StringSplitOptions.RemoveEmptyEntries));

        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
                continue;

            if (segment == "..")
            {
                if (segments.Count == 0)
                    return null;
                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        return "/" + string.Join('/', segments);
    }

    private bool IsSingleRoot => _server.Mounts.Count == 1 && _server.Mounts[0].Name.Length == 0;

    private bool IsMountRoot(string physical) =>
        _server.Mounts.Any(m => physical.Equals(m.Path, StringComparison.OrdinalIgnoreCase));

    private bool IsInsideAnyRoot(string full) =>
        _server.Mounts.Any(m => full.Equals(m.Path, StringComparison.OrdinalIgnoreCase)
                                || full.StartsWith(m.Prefix, StringComparison.OrdinalIgnoreCase));

    private (ShareMount Mount, string Remainder)? MapMount(string virtualPath)
    {
        var mounts = _server.Mounts;
        string trimmed = virtualPath.TrimStart('/');

        if (mounts.Count == 1 && mounts[0].Name.Length == 0)
            return (mounts[0], trimmed);

        if (trimmed.Length == 0)
            return null;

        int slash = trimmed.IndexOf('/');
        string first = (slash < 0 ? trimmed : trimmed[..slash]).TrimEnd(':');
        string rest = slash < 0 ? "" : trimmed[(slash + 1)..];

        foreach (var mount in mounts)
        {
            if (string.Equals(mount.Name, first, StringComparison.OrdinalIgnoreCase))
                return (mount, rest);
        }

        return null;
    }

    private string? Resolve(string arg)
    {
        var virt = NormalizeVirtual(arg);
        if (virt is null)
            return null;

        var mapped = MapMount(virt);
        if (mapped is null)
            return null;

        var (mount, rest) = mapped.Value;
        string physical = rest.Length == 0
            ? mount.Path
            : Path.Combine(mount.Path, rest.Replace('/', Path.DirectorySeparatorChar));

        string full;
        try { full = Path.GetFullPath(physical); }
        catch { return null; }

        bool inside = full.Equals(mount.Path, StringComparison.OrdinalIgnoreCase)
                      || full.StartsWith(mount.Prefix, StringComparison.OrdinalIgnoreCase);
        return inside ? full : null;
    }

    private string ToVirtual(string physical)
    {
        foreach (var mount in _server.Mounts)
        {
            if (physical.Equals(mount.Path, StringComparison.OrdinalIgnoreCase))
                return mount.Name.Length == 0 ? "/" : "/" + mount.Name;

            if (physical.StartsWith(mount.Prefix, StringComparison.OrdinalIgnoreCase))
            {
                string relative = physical[mount.Prefix.Length..].Replace(Path.DirectorySeparatorChar, '/');
                return mount.Name.Length == 0 ? "/" + relative : "/" + mount.Name + "/" + relative;
            }
        }

        return "/";
    }

    private void Reply(int code, string text)
    {
        try
        {
            _writer.Write($"{code} {text}\r\n");
        }
        catch { }
    }
}
