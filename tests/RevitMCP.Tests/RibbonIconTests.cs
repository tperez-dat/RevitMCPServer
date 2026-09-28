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

    [Theory]
    [MemberData(nameof(ExpectedIcons))]
    public void IconDoesNotClaimADpiOtherThan96(string fileName, int expectedSize)
    {
        // WPF sizes an image by pixels * 96 / DPI, so a PNG exported at 72 DPI - which several
        // editors do by default - asks for 32 * 96/72 = 42.7 device-independent pixels. Revit gives
        // a large ribbon button 32, so WPF resamples it down and the icon looks blurred and
        // misplaced even though its pixel dimensions are correct. Correct dimensions are therefore
        // not enough to check.
        //
        // With no pHYs chunk WPF uses exactly 96 DPI and renders 1:1, so the rule is: either no
        // pHYs at all, or one that genuinely means 96 DPI.
        var bytes = File.ReadAllBytes(Path.Combine(ResourceDirectory, fileName));

        var position = 8;   // past the PNG signature
        while (position + 8 <= bytes.Length)
        {
            var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(position, 4));
            var tag = System.Text.Encoding.ASCII.GetString(bytes, position + 4, 4);

            if (tag == "pHYs")
            {
                var perUnitX = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position + 8, 4));
                var unit = bytes[position + 16];

                // unit 1 is pixels per metre; 0 means "aspect ratio only", which imposes no DPI.
                if (unit == 1)
                {
                    var dpi = Math.Round(perUnitX * 0.0254);
                    Assert.True(Math.Abs(dpi - 96) < 1,
                        $"{fileName} declares {dpi} DPI. Revit's ribbon needs 96 DPI, or no DPI " +
                        "metadata at all. Re-export at 96 DPI, or strip the pHYs chunk.");
                }

                return;
            }

            if (tag == "IDAT" || tag == "IEND") return;   // pHYs must precede IDAT
            position += 12 + length;
        }
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
