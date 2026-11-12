using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AllInOnePdf.Launcher;

/// <summary>
/// The one executable at the root of the portable folder. Everything else lives in <c>App\</c> (the app)
/// and <c>Data\</c> (the user's settings, themes, history and output).
/// Starts <c>App\AllInOnePdf\AllInOnePdf.exe</c> with the same command line, waits for it and returns its
/// exit code. Portable mode itself is decided by the app from <c>App\AllInOnePdf\portable.txt</c>.
/// </summary>
internal static class Program
{
    private const string Title = "AllInOne PDF Portable";
    private const string AppRelativePath = @"App\AllInOnePdf\AllInOnePdf.exe";
    private const string DataFolderName = "Data";

    private static int Main(string[] args)
    {
        var root = AppDomain.CurrentDomain.BaseDirectory;
        var exe = Path.Combine(root, AppRelativePath);
        if (!File.Exists(exe))
        {
            Fail("AllInOne PDF could not start because this file is missing:\n\n" + exe +
                 "\n\nKeep AllInOnePdfPortable.exe together with its App and Data folders.");
            return 2;
        }

        try
        {
            // Makes the layout obvious on first run; the app creates the folder as well.
            Directory.CreateDirectory(Path.Combine(root, DataFolderName));
        }
        catch (Exception)
        {
            // Read-only media: the app reports the problem itself.
        }

        // The working directory is inherited on purpose so relative paths on the command line keep working.
        var start = new ProcessStartInfo(exe, BuildArguments(args))
        {
            UseShellExecute = false,
        };

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                Fail("AllInOne PDF could not start:\n\n" + exe);
                return 3;
            }

            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Win32Exception ex)
        {
            Fail("AllInOne PDF could not start:\n\n" + exe + "\n\n" + ex.Message);
            return 3;
        }
    }

    /// <summary>Re-quotes the arguments the way CommandLineToArgvW expects, so paths with spaces and quotes survive.</summary>
    private static string BuildArguments(string[] args)
    {
        var sb = new StringBuilder();
        foreach (var arg in args)
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            AppendQuoted(sb, arg);
        }

        return sb.ToString();
    }

    private static readonly char[] NeedsQuoting = { ' ', '\t', '\n', '\v', '"' };

    private static void AppendQuoted(StringBuilder sb, string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(NeedsQuoting) < 0)
        {
            sb.Append(arg);
            return;
        }

        sb.Append('"');
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }

            sb.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }

        sb.Append('\\', backslashes * 2).Append('"');
    }

    private static void Fail(string message) =>
        _ = MessageBoxW(IntPtr.Zero, message, Title, MB_OK | MB_ICONERROR | MB_SETFOREGROUND);

    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONERROR = 0x00000010;
    private const uint MB_SETFOREGROUND = 0x00010000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
