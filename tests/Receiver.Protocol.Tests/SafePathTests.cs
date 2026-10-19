using Receiver.Protocol;
using Xunit;

namespace Receiver.Protocol.Tests;

/// <summary>
/// Path-safety tests. Every name here comes from a remote device whose identity we
/// cannot verify, so each case asserts that nothing can be written outside the chosen
/// destination and that no Windows-specific naming trick survives.
/// </summary>
public class SafePathTests
{
    [Theory]
    [InlineData("../../escape.txt", "escape.txt")]
    [InlineData("../evil", "evil")]
    [InlineData("a/../../b.txt", "a\\b.txt")]
    [InlineData("/absolute.txt", "absolute.txt")]
    [InlineData("//server/share/x.txt", "server\\share\\x.txt")]
    [InlineData("C:\\drive.txt", "drive.txt")]
    [InlineData("./ok.txt", "ok.txt")]
    [InlineData("dir/sub/file.txt", "dir\\sub\\file.txt")]
    public void Traversal_and_rooted_paths_are_confined(string input, string expected)
    {
        var result = SafePath.Sanitize(input);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.RelativePath);
    }

    [Theory]
    [InlineData("stream.txt:hidden", "stream.txt_hidden")]
    [InlineData("a<b>c.txt", "a_b_c.txt")]
    [InlineData("pipe|name.txt", "pipe_name.txt")]
    public void Windows_special_characters_are_replaced(string input, string expected)
    {
        var result = SafePath.Sanitize(input);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.RelativePath);
        Assert.True(result.WasRewritten);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("CON.txt")]
    [InlineData("LPT1.dat")]
    [InlineData("NUL")]
    public void Reserved_device_names_are_defused(string input)
    {
        var result = SafePath.Sanitize(input);
        Assert.NotNull(result);
        Assert.StartsWith("_", result!.RelativePath);
        Assert.True(result.WasRewritten);
    }

    [Fact]
    public void Trailing_dots_and_spaces_are_removed()
    {
        // Windows strips these itself, which would let "evil.exe." and "evil.exe"
        // collide. Doing it here makes the collision visible to the collision check.
        var result = SafePath.Sanitize("trailing.   ");
        Assert.NotNull(result);
        Assert.Equal("trailing", result!.RelativePath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData("../..")]
    [InlineData("/")]
    public void Names_with_nothing_safe_left_are_rejected(string input)
    {
        Assert.Null(SafePath.Sanitize(input));
    }

    [Fact]
    public void Sanitized_paths_always_resolve_inside_the_destination()
    {
        var root = Path.Combine(Path.GetTempPath(), "airdrop-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string[] hostile =
            [
                "../../escape.txt", "/absolute.txt", "C:\\drive.txt",
                "a/../../../b.txt", "dir/sub/../../../out.txt",
            ];

            foreach (var name in hostile)
            {
                var result = SafePath.Sanitize(name);
                Assert.NotNull(result);
                var full = Path.Combine(root, result!.RelativePath);
                Assert.True(SafePath.IsInside(root, full),
                    $"'{name}' resolved to '{full}', which is outside '{root}'");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void IsInside_rejects_a_sibling_directory_with_a_shared_prefix()
    {
        var root = Path.Combine(Path.GetTempPath(), "airdrop");
        Assert.False(SafePath.IsInside(root, Path.Combine(Path.GetTempPath(), "airdrop-evil", "x.txt")));
        Assert.True(SafePath.IsInside(root, Path.Combine(root, "x.txt")));
    }

    [Fact]
    public void Collisions_are_resolved_case_insensitively()
    {
        var dir = Path.Combine(Path.GetTempPath(), "airdrop-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var used = new HashSet<string>();
            Assert.Equal("Foto.JPG", SafePath.ResolveCollision(dir, "Foto.JPG", used));
            // NTFS treats these as the same file even though the archive may not.
            Assert.Equal("foto (1).jpg", SafePath.ResolveCollision(dir, "foto.jpg", used));
            Assert.Equal("foto (2).jpg", SafePath.ResolveCollision(dir, "foto.jpg", used));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
