using System.Diagnostics;
using System.IO;

namespace WinChatCopilot.Tests;

public class FixtureDirectoryTests
{
    [Fact]
    public async Task CleanupWaitsForTransientWindowsSharingLock()
    {
        var root = Path.Combine(Path.GetTempPath(), "winchat-cleanup-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "owned-fixture.txt");
        var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        Assert.Equal(32, Assert.Throws<IOException>(() => Directory.Delete(root, recursive: true)).HResult & 0xffff);
        var release = Task.Run(async () =>
        {
            await Task.Delay(150);
            file.Dispose();
        });
        try
        {
            FixtureDirectory.Delete(root);
            Assert.False(Directory.Exists(root));
            await release;
        }
        finally
        {
            await release;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PersistentSharingLockStillFailsWithinBoundAndPreservesFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "winchat-cleanup-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "owned-fixture.txt");
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        try
        {
            var elapsed = Stopwatch.StartNew();
            var error = Assert.Throws<IOException>(() => FixtureDirectory.Delete(root));
            Assert.Equal(32, error.HResult & 0xffff);
            Assert.InRange(elapsed.Elapsed.TotalSeconds, 2, 5);
            Assert.True(File.Exists(path));
        }
        finally
        {
            file.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MissingDirectoryIsNotConvertedIntoSuccess()
    {
        Assert.Throws<DirectoryNotFoundException>(() =>
            FixtureDirectory.Delete(Path.Combine(Path.GetTempPath(), "winchat-missing-" + Guid.NewGuid())));
    }
}
