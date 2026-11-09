namespace NKeeper.Plugins.Host.Tests;

/// <summary>docs/plugin-security.md §4: the ownership and mode checks, with a fake and with the real Linux file system.</summary>
public sealed class LocationCheckTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("nkeeper-location-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void A_root_owned_unwritable_tree_passes()
    {
        File.WriteAllText(Path.Combine(_root, "a.nkp"), "x");

        Assert.Null(LocationChecks.Check(_root, 0, new FakeFileSystem()));
    }

    [Theory]
    [InlineData(0xA1FFu, "is a symlink")]
    [InlineData(0x81A4u, "is not a directory")]
    [InlineData(0x41FDu, "writable by its group or by others")]
    public void A_bad_directory_fails(uint mode, string problem)
    {
        var fs = new FakeFileSystem();
        fs.Overrides[Path.GetFullPath(_root)] = new FileStatus(0, mode);

        Assert.Contains(problem, LocationChecks.Check(_root, 0, fs), StringComparison.Ordinal);
    }

    [Fact]
    public void A_writable_ancestor_fails()
    {
        var fs = new FakeFileSystem();
        fs.Overrides[Path.GetFullPath(Path.Combine(_root, ".."))] = new FileStatus(0, 0x43FF); // like /tmp: drwxrwxrwt

        Assert.Contains("writable", LocationChecks.Check(_root, 0, fs), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1000u, 0x81A4u, "owned by user 1000")]
    [InlineData(0u, 0x81B4u, "writable by its group")]
    [InlineData(0u, 0xA1FFu, "is a symlink")]
    [InlineData(0u, 0x41EDu, "is not a regular file")]
    public void A_bad_entry_fails(uint owner, uint mode, string problem)
    {
        var entry = Path.Combine(_root, "a.nkp");
        File.WriteAllText(entry, "x");
        var fs = new FakeFileSystem();
        fs.Overrides[Path.GetFullPath(entry)] = new FileStatus(owner, mode);

        Assert.Contains(problem, LocationChecks.Check(_root, 0, fs), StringComparison.Ordinal);
    }

    [Fact]
    public void The_expected_owner_may_replace_root_but_never_allows_writable_bits()
    {
        var entry = Path.Combine(_root, "a.nkp");
        File.WriteAllText(entry, "x");
        var fs = new FakeFileSystem();
        fs.Overrides[Path.GetFullPath(entry)] = new FileStatus(1000, 0x81A4);

        Assert.Null(LocationChecks.Check(_root, 1000, fs));
        fs.Overrides[Path.GetFullPath(entry)] = new FileStatus(1000, 0x81B6);
        Assert.NotNull(LocationChecks.Check(_root, 1000, fs));
    }

    [Fact]
    public void The_real_file_system_is_checked_on_Linux()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "statx is Linux only.");
        var fs = new LinuxFileSystem();
        var me = fs.EffectiveUserId;

        // A private directory inside the build output (not /tmp, whose sticky world-writable mode fails).
        var safe = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "location-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(safe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

                // While still empty, the private directory passes only if its ancestors do. If this
                // machine's build directory sits under a group- or world-writable path, the rest can't run.
                var ancestors = LocationChecks.Check(safe, me, fs);
                Assert.SkipUnless(ancestors is null, $"The build directory's ancestors aren't safe on this machine: {ancestors}");

                var package = Path.Combine(safe, "a.nkp");
                File.WriteAllText(package, "x");
                File.SetUnixFileMode(package, UnixFileMode.UserRead | UnixFileMode.UserWrite);

                Assert.Null(LocationChecks.Check(safe, me, fs));
                Assert.NotNull(LocationChecks.Check(safe, 0, fs)); // owned by this user, not root

                File.SetUnixFileMode(package, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherWrite);
                Assert.Contains("writable", LocationChecks.Check(safe, me, fs), StringComparison.Ordinal);

                File.Delete(package);
                File.CreateSymbolicLink(package, "/etc/hostname");
                Assert.Contains("symlink", LocationChecks.Check(safe, me, fs), StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(safe, recursive: true);
        }
    }
}
