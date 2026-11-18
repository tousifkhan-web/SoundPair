using System;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace SoundPair;

public partial class App : Application
{
    private static Mutex? _mutex;
    private static bool _ownsMutex;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Don't run single-instance mutex check or shutdown in XAML designer preview
        if (Avalonia.Controls.Design.IsDesignMode)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        const string mutexName = @"Local\{SoundPair-Tri-Audio-Unique-Lock}";
        try
        {
            _mutex = new Mutex(true, mutexName, out _ownsMutex);
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
        }

        if (!_ownsMutex)
        {
            MessageBoxW(
                IntPtr.Zero,
                "Sound Pair is already running in the background!\n\nPlease check your Windows taskbar.",
                "App Already Running",
                0x00000040 /* MB_ICONINFORMATION */ | 0x00000000 /* MB_OK */);

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktopExit)
            {
                desktopExit.Shutdown();
            }
            return;
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
            desktop.Exit += OnAppExit;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnAppExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow is MainWindow mw)
        {
            mw.RestoreOriginalDefaultDevice();
        }

        if (_ownsMutex)
        {
            try
            {
                _mutex?.ReleaseMutex();
            }
            catch { }
        }
        _mutex?.Dispose();
        _mutex = null;
    }
}
