using FTPMate.Server;

int port = args.Length > 0 ? int.Parse(args[0]) : 2121;
string root = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "ftpmate-smoke");
string password = args.Length > 2 ? args[2] : "";

Directory.CreateDirectory(root);

var server = new FtpServer
{
    Root = root,
    Port = port,
    Username = "user",
    Password = password,
    ShareAllDrives = args.Contains("--all")
};

server.Logged += message => Console.WriteLine("[log] " + message);
server.ClientsChanged += () => Console.WriteLine("[clients] " + server.ClientCount);

server.Start();
Console.WriteLine("READY " + port + " all=" + server.ShareAllDrives + " root=" + root);
Console.Out.Flush();

await Task.Delay(TimeSpan.FromSeconds(90));
server.Stop();
