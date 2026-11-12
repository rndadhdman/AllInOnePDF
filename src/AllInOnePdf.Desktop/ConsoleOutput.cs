using System.Runtime.InteropServices;

namespace AllInOnePdf.Desktop;

/// <summary>
/// The Windows build is a GUI program (WinExe), so it has no console of its own. For the command-line options that print
/// text, attach to the console of the terminal that started it so the text is visible there.
/// </summary>
internal static class ConsoleOutput
{
    /// <summary>
    /// Attaches to the parent process's console on Windows when standard output is not already redirected (a pipe, a
    /// file or <c>dotnet run</c> keep their own handles). Does nothing on other systems or when there is no parent console.
    /// </summary>
    public static void AttachToParentConsole()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var handle = NativeMethods.GetStdHandle(NativeMethods.StdOutputHandle);
        if (handle != IntPtr.Zero && handle != NativeMethods.InvalidHandleValue)
        {
            return;
        }

        if (NativeMethods.AttachConsole(NativeMethods.AttachParentProcess))
        {
            // Console.Out was not created yet, so it picks up the attached console's handle.
            Console.WriteLine();
        }
    }

    private static class NativeMethods
    {
        public const int StdOutputHandle = -11;
        public const int AttachParentProcess = -1;
        public static readonly IntPtr InvalidHandleValue = new(-1);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AttachConsole(int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern IntPtr GetStdHandle(int stdHandle);
    }
}
