using System.IO;

namespace FTPMate.Server;

internal sealed class ShareMount
{
    public ShareMount(string name, string path)
    {
        Name = name;
        Path = path;
        Prefix = path.EndsWith(System.IO.Path.DirectorySeparatorChar)
            ? path
            : path + System.IO.Path.DirectorySeparatorChar;
    }

    public string Name { get; }
    public string Path { get; }
    public string Prefix { get; }
}
