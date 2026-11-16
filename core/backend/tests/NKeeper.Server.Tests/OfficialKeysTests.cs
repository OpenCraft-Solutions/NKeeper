using NKeeper.Plugins.Packaging;

namespace NKeeper.Server.Tests;

public sealed class OfficialKeysTests
{
    [Fact]
    public void The_release_key_is_embedded_as_an_official_key()
    {
        var key = Assert.Single(OfficialKeys.All, k => k.KeyId == "d7c833fe094aaa46da319e3de4709850");

        Assert.Equal(TrustedKeyKind.Official, key.Kind);
        Assert.Null(key.NotAfter);
        Assert.True(key.MaySign("ssh"));
        Assert.True(key.MaySign("com.example.anything"));
    }

    [Fact]
    public void Every_embedded_key_is_official()
    {
        Assert.All(OfficialKeys.All, key => Assert.Equal(TrustedKeyKind.Official, key.Kind));
    }
}
