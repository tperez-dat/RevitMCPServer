using Xunit;

namespace RevitMCP.Tests;

/// <summary>
/// The ribbon icons are meant to be replaced with real artwork, so these guard the contract Revit
/// expects: a 32px and a 16px PNG per button. A file swapped in at the wrong size or in a format
/// Revit cannot decode would otherwise only show up as a blank or distorted button in the ribbon.
/// </summary>
public class RibbonIconTests
{
    private static string ResourceDirectory
    {
        get
        {
            // Walk up from the test binaries to the repository root.
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RevitMCPServer.sln")))
                directory = directory.Parent;

            Assert.NotNull(directory);
            return Path.Combine(directory!.FullName, "src", "RevitMCPBridge", "Resources");
        }
    }

    public static TheoryData<string, int> ExpectedIcons => new()
    {
        { "WriteToggle32.png", 32 },
        { "WriteToggle16.png", 16 },
        { "BridgeStatus32.png", 32 },
        { "BridgeStatus16.png", 16 }
    };

    [Theory]
    [MemberData(nameof(ExpectedIcons))]
    public void IconIsAPngOfTheSizeRevitExpects(string fileName, int expectedSize)
    {
        var path = Path.Combine(ResourceDirectory, fileName);
        Assert.True(File.Exists(path), $"{fileName} is missing from src/RevitMCPBridge/Resources.");

        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length > 24, $"{fileName} is too small to be a PNG.");

        // PNG signature, then IHDR carries width and height as big-endian 32-bit values.
        ReadOnlySpan<byte> signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        Assert.True(bytes.AsSpan(0, 8).SequenceEqual(signature),
            $"{fileName} is not a PNG. Revit's ribbon needs PNG; re-export it.");

        var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
        var height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));

        Assert.Equal(expectedSize, width);
        Assert.Equal(expectedSize, height);
    }

    [Fact]
    public void TheProjectEmbedsEveryIcon()
    {
        // The loader looks these up by name at run time, so an icon present on disk but not
        // embedded would fail silently in Revit rather than at build time.
        var project = Path.Combine(
            Directory.GetParent(ResourceDirectory)!.FullName, "RevitMCPBridge.csproj");

        var text = File.ReadAllText(project);
        Assert.Contains("Resources\\*.png", text);
    }
}
