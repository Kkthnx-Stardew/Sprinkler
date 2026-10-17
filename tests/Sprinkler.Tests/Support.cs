using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Sprinkler.Tests;

internal static class Boot
{
    [ModuleInitializer]
    public static void Init()
    {
        var dir = Path.Combine(Path.GetTempPath(), "smu-tests-data");
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("SPRINKLER_DATA", dir);
    }
}

/// <summary>A throwaway folder that is deleted when the test ends.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "smu-" + Guid.NewGuid().ToString("N")[..10]);

    public TempDir() => Directory.CreateDirectory(Path);

    public string Combine(params string[] parts) => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

    public string Write(string relative, string content)
    {
        var full = Combine(relative.Split('/'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

/// <summary>For a test that needs Windows file locking, which Linux and macOS do not enforce.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute([System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null, [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Needs Windows file locking.";
    }
}

internal static class Zips
{
    public static string Manifest(string id, string version, string name = "Test Mod", string extra = "") =>
        $$"""{ "Name": "{{name}}", "Author": "t", "Version": "{{version}}", "UniqueID": "{{id}}"{{extra}} }""";

    /// <summary>Builds a zip with the given entry names. Names are used verbatim so hostile paths can be tested.</summary>
    public static string Build(TempDir dir, string fileName, params (string Name, string Content)[] entries)
    {
        var path = dir.Combine(fileName);
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var e = zip.CreateEntry(name);
            using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
            w.Write(content);
        }
        return path;
    }
}
