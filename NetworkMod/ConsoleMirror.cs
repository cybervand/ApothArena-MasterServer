using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ApotheonArena.NetworkMod
{
    internal static class ConsoleMirror
    {
        static readonly object _lock = new object();
        static bool _initialized;
        static bool _attached;

        public static bool IsAttached { get { return _attached; } }

        public static void Enable(string title)
        {
            lock (_lock)
            {
                if (_initialized) return;
                _initialized = true;

                try
                {
                    if (!AllocConsole())
                        return;

                    var stdout = Console.OpenStandardOutput();
                    var writer = new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true };
                    Console.SetOut(writer);

                    var stderr = Console.OpenStandardError();
                    var errWriter = new StreamWriter(stderr, new UTF8Encoding(false)) { AutoFlush = true };
                    Console.SetError(errWriter);

                    if (!string.IsNullOrEmpty(title))
                        SetConsoleTitle(title);

                    _attached = true;
                }
                catch
                {
                    _attached = false;
                }
            }
        }

        public static void WriteLine(string line)
        {
            if (!_attached) return;
            try { Console.Out.WriteLine(line); }
            catch { }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool AllocConsole();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetConsoleTitle(string lpConsoleTitle);
    }
}
