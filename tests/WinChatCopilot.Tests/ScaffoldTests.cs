using System.Reflection;
using System.Windows;

namespace WinChatCopilot.Tests;

[Collection("WPF")]
public class ScaffoldTests
{
    [Fact]
    public void DisplayedVersionMatchesGeneratedAssemblyMetadata()
    {
        var assembly = typeof(App).Assembly;
        var fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>();

        Assert.NotNull(fileVersion);
        Assert.Equal(assembly.GetName().Version!.ToString(3), ApplicationInfo.Version);
        Assert.Equal(new Version(fileVersion.Version).ToString(3), ApplicationInfo.Version);
    }

    [Fact]
    public void MainWindowLoadsWithVersionedTitleAndUsableDimensions()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new MainWindow();

                Assert.Equal($"WinChat-Copilot {ApplicationInfo.Version}", window.Title);
                Assert.True(window.Width >= window.MinWidth);
                Assert.True(window.Height >= window.MinHeight);
                Assert.NotNull(window.Content);
                Assert.Equal(WindowStyle.None, window.WindowStyle);
                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Scaffold window initialization timed out.");
        Assert.Null(failure);
    }
}
