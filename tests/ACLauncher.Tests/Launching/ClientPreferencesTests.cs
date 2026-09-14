using ACLauncher.Core.Launching;

namespace ACLauncher.Tests.Launching;

public sealed class ClientPreferencesTests
{
    [Fact]
    public void FlipsFullScreenInsideDisplaySectionKeepingCrlf()
    {
        const string text = "[Render]\r\nFullScreen=True\r\n[Display]\r\nRefreshRate=Auto\r\nFullScreen=True\r\nSyncToRefresh=False\r\n[UI]\r\nChatFontSize=Small\r\n";

        var result = ClientPreferences.SetWindowed(text);

        Assert.Equal("[Render]\r\nFullScreen=True\r\n[Display]\r\nRefreshRate=Auto\r\nFullScreen=False\r\nSyncToRefresh=False\r\n[UI]\r\nChatFontSize=Small\r\n", result);
    }

    [Fact]
    public void LeavesAlreadyWindowedFileUntouched()
    {
        const string text = "[Display]\nfullscreen = false\n";
        Assert.Same(text, ClientPreferences.SetWindowed(text));
    }

    [Fact]
    public void AddsKeyToExistingSectionWithoutIt() =>
        Assert.Equal("[Display]\nFullScreen=False\nResolution=800x600\n[Sound]\n",
            ClientPreferences.SetWindowed("[Display]\nResolution=800x600\n[Sound]\n"));

    [Fact]
    public void CreatesSectionWhenFileIsEmptyOrLacksIt()
    {
        Assert.Equal("[Display]\r\nFullScreen=False\r\n", ClientPreferences.SetWindowed(""));
        Assert.Equal("[Sound]\nSoundDisabled=True\n[Display]\nFullScreen=False\n", ClientPreferences.SetWindowed("[Sound]\nSoundDisabled=True\n"));
    }

    [Fact]
    public void EnsureWindowedWritesThroughMissingDirectories()
    {
        var root = Directory.CreateTempSubdirectory("aclauncher-prefs-").FullName;
        try
        {
            var path = ClientPreferences.PathFor(root);
            Assert.True(ClientPreferences.EnsureWindowed(path));
            Assert.False(ClientPreferences.EnsureWindowed(path));
            Assert.Equal(Path.Combine(root, "drive_c", "users", "steamuser", "Documents", "Asheron's Call", "UserPreferences.ini"), path);
            Assert.Contains("FullScreen=False", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
