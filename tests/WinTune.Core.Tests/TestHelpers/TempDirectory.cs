using System.IO;

namespace WinTune.Core.Tests.TestHelpers;

public sealed class TempDirectory : IDisposable
{
    public string Path { get; }

    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"wintune-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string CreateFile(string name, byte[] content)
    {
        var full = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    public string CreateFile(string name, string content) =>
        CreateFile(name, System.Text.Encoding.UTF8.GetBytes(content));

    public string CreateSubdirectory(string name)
    {
        var dir = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void CreateJunction(string linkName, string targetDir)
    {
        var linkPath = System.IO.Path.Combine(Path, linkName);
        Directory.CreateSymbolicLink(linkPath, targetDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
