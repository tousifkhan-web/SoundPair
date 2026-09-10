using System;
using System.Threading;
using System.Windows;

namespace SoundPair
{
    public partial class App : Application
    {
        private static Mutex? _mutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            const string mutexName = "{SoundPair-Tri-Audio-Unique-Lock}";
            _mutex = new Mutex(true, mutexName, out bool createdNew);

            if (!createdNew)
            {
                MessageBox.Show(
                    "Sound Pair is already running in the background!\n\nPlease check your Windows taskbar.",
                    "App Already Running",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                
                Current.Shutdown();
                return;
            }

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _mutex?.ReleaseMutex();
            base.OnExit(e);
        }
    }
}