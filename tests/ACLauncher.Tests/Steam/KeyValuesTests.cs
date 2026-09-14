using ACLauncher.Core.Steam;

namespace ACLauncher.Tests.Steam;

public sealed class KeyValuesTextTests
{
    [Fact]
    public void ParsesNestedSectionsCaseInsensitively()
    {
        const string text = """
            "libraryfolders"
            {
            	"0"
            	{
            		"path"		"/home/someone/.local/share/Steam"
            		"apps"
            		{
            			"228980"		"1234"
            		}
            	}
            	"1"
            	{
            		"path"		"/mnt/games/SteamLibrary"
            	}
            }
            """;

        var root = KeyValuesText.Parse(text);

        var folders = root["LibraryFolders"];
        Assert.NotNull(folders);
        Assert.Equal(2, folders.Children.Count);
        Assert.Equal("/home/someone/.local/share/Steam", folders.Find("0", "path")?.Text);
        Assert.Equal(1234, folders.Find("0", "apps")?.GetInteger("228980"));
        Assert.Equal("/mnt/games/SteamLibrary", folders["1"]?.GetString("PATH"));
    }

    [Fact]
    public void HandlesEscapesCommentsConditionalsAndUnquotedTokens()
    {
        const string text = """
            // leading comment
            manifest
            {
                "commandline" "/proton %verb%" // trailing comment
                "quoted"      "a \"b\" c\\d"
                "platform"    "linux" [$LINUX]
            }
            """;

        var manifest = KeyValuesText.Parse(text)["manifest"];

        Assert.NotNull(manifest);
        Assert.Equal("/proton %verb%", manifest.GetString("commandline"));
        Assert.Equal("a \"b\" c\\d", manifest.GetString("quoted"));
        Assert.Equal("linux", manifest.GetString("platform"));
        Assert.Equal(3, manifest.Children.Count);
    }

    [Theory]
    [InlineData("\"a\" {")]
    [InlineData("\"a\" \"unterminated")]
    [InlineData("}")]
    [InlineData("\"lonely\"")]
    public void RejectsMalformedText(string text) =>
        Assert.Throws<FormatException>(() => KeyValuesText.Parse(text));
}
