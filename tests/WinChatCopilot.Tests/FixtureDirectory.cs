using System.Diagnostics;
using System.IO;

namespace WinChatCopilot.Tests;

internal static class FixtureDirectory
{
    public static void Delete(string path)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException error) when (
                (error.HResult & 0xffff) is 32 or 33 && elapsed.Elapsed < TimeSpan.FromSeconds(2))
            {
                // Windows may signal process exit before releasing its working-directory handles.
                Thread.Sleep(20);
            }
        }
    }
}
