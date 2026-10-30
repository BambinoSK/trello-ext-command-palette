// The extension is found by Command Palette through its COM class id (CLSID).
// The same GUID appears in the code, the MSIX manifest (twice). If any copy drifts, the extension silently stops loading,
// so this test pins them together.

using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Xunit;

namespace TrelloExtension.Tests;

public class ClsidConsistencyTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TrelloExtension.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repo root (TrelloExtension.sln) not found.");
    }

    private static Guid CodeClsid()
    {
        var attribute = typeof(TrelloExtension).GetCustomAttribute<GuidAttribute>();
        Assert.NotNull(attribute);
        return Guid.Parse(attribute!.Value);
    }

    [Fact]
    public void Manifest_ClassIdAndCreateInstance_MatchCode()
    {
        var manifest = File.ReadAllText(Path.Combine(RepoRoot(), "TrelloExtension", "Package.appxmanifest"));

        var classId = Regex.Match(manifest, "<com:Class\\s+Id=\"([^\"]+)\"");
        var createInstance = Regex.Match(manifest, "<CreateInstance\\s+ClassId=\"([^\"]+)\"");

        Assert.True(classId.Success, "com:Class Id not found in manifest");
        Assert.True(createInstance.Success, "CreateInstance ClassId not found in manifest");
        Assert.Equal(CodeClsid(), Guid.Parse(classId.Groups[1].Value));
        Assert.Equal(CodeClsid(), Guid.Parse(createInstance.Groups[1].Value));
    }
}
