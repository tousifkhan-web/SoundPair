using System;
using System.Threading;
using System.Windows;

namespace SoundPair
{
    public partial class App : System.Windows.Application
    {
        private static Mutex? _mutex;
        private static bool _ownsMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            const string mutexName = "{SoundPair-Tri-Audio-Unique-Lock}";
            _mutex = new Mutex(true, mutexName, out _ownsMutex);

            if (!_ownsMutex)
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
            if (_ownsMutex)
            {
                _mutex?.ReleaseMutex();
            }
            
            base.OnExit(e);
        }
    }
}