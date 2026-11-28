using System;
using System.Threading.Tasks;
using Avalonia;

namespace SoundPair;

/// <summary>
/// Application entry point and runtime configuration.
/// </summary>
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppLogger.Initialize();

        // 1. Trap unhandled AppDomain exceptions (e.g. unexpected COM audio driver errors)
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            try
            {
                var ex = e.ExceptionObject as Exception;
                AppLogger.LogError($"CRITICAL UNHANDLED EXCEPTION (Terminating={e.IsTerminating})", ex);
                System.Diagnostics.Debug.WriteLine($"[CRITICAL UNHANDLED EXCEPTION] {ex?.Message}\n{ex?.StackTrace}");
            }
            catch { }
        };

        // 2. Prevent unobserved background task exceptions from terminating the process
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            try
            {
                AppLogger.LogError("UNOBSERVED TASK EXCEPTION", e.Exception);
                System.Diagnostics.Debug.WriteLine($"[UNOBSERVED TASK EXCEPTION] {e.Exception?.Message}");
                e.SetObserved();
            }
            catch { }
        };

        // 3. Start Avalonia Desktop application lifetime
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            AppLogger.LogError("FATAL MAIN LOOP EXCEPTION", ex);
            throw;
        }
        finally
        {
            AppLogger.Shutdown();
        }
    }

    /// <summary>
    /// Avalonia configuration with Skia GPU acceleration and Inter font rendering.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new SkiaOptions
            {
                // Allocate 256MB GPU memory cache for fluid 60/120Hz ball drop & ripple canvas rendering
                MaxGpuResourceSizeBytes = 256 * 1024 * 1024
            })
            .WithInterFont()
            .LogToTrace();
}
