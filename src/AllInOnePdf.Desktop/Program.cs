using AllInOnePdf.Desktop;
using AllInOnePdf.UI;
using AllInOnePdf.UI.Services;
using AllInOnePdf.UI.Services.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Photino.Blazor;
using Photino.NET;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // `AllInOnePdf --help` (or -h, /?) prints the options and the data folder rules and exits without a window.
        if (args.Length >= 1 && IsHelpFlag(args[0]))
        {
            ConsoleOutput.AttachToParentConsole();
            Console.WriteLine(HelpText());
            return 0;
        }

        // In Debug builds Razor Class Library assets (_content/...) are not copied to wwwroot; they are described by a
        // manifest instead. This provider reads it so the desktop app works straight from `dotnet run`.
        var fileProvider = StaticWebAssetsFileProvider.Create(AppContext.BaseDirectory, "AllInOnePdf");

        // Diagnostic: `AllInOnePdf --check-assets [report.txt]` verifies the webview can resolve every required asset.
        if (args.Length >= 1 && string.Equals(args[0], "--check-assets", StringComparison.OrdinalIgnoreCase))
        {
            ConsoleOutput.AttachToParentConsole();
            return CheckAssets(fileProvider, args.Length > 1 ? args[1] : null);
        }

        // `AllInOnePdf --paths` prints where data goes (portable or per-user) and exits.
        var paths = new AllInOnePdf.Core.Storage.AppPaths();
        if (args.Length >= 1 && string.Equals(args[0], "--paths", StringComparison.OrdinalIgnoreCase))
        {
            ConsoleOutput.AttachToParentConsole();
            Console.WriteLine($"portable: {paths.IsPortable}");
            Console.WriteLine($"data:     {paths.DataDirectory}");
            Console.WriteLine($"output:   {paths.DefaultOutputDirectory}");
            Console.WriteLine($"browser:  {paths.BrowserDataDirectory}");
            return 0;
        }

        ConfigureBrowserRuntime(paths);

        var appBuilder = PhotinoBlazorAppBuilder.CreateDefault(fileProvider, args);

        appBuilder.Services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Information);
#if DEBUG
            logging.AddConsole();
#endif
        });

        // Scoped so it can use the webview's IJSRuntime (clipboard); Photino.Blazor has one scope per window.
        appBuilder.Services.AddSingleton(sp => new WindowFrame(sp.GetRequiredService<PhotinoWindow>()));
        appBuilder.Services.AddScoped<IHostServices>(sp => new PhotinoHostServices(
            sp.GetRequiredService<PhotinoWindow>(),
            sp.GetRequiredService<Microsoft.JSInterop.IJSRuntime>(),
            sp.GetRequiredService<WindowFrame>()));
        appBuilder.Services.AddAllInOnePdfUI();

        appBuilder.RootComponents.Add<Routes>("app");

        var app = appBuilder.Build();

        // Keep the browser profile (cache, local storage) with the app data so a portable copy leaves nothing behind.
        try
        {
            Directory.CreateDirectory(paths.BrowserDataDirectory);
            app.MainWindow.SetTemporaryFilesPath(paths.BrowserDataDirectory);
        }
        catch (Exception)
        {
            // Fall back to the webview's default profile location.
        }

        app.MainWindow
            .SetTitle("AllInOne PDF")
            .SetUseOsDefaultSize(false)
            .SetSize(1440, 920)
            .SetMinSize(960, 600)
            .Center()
            .SetContextMenuEnabled(false)
            .SetGrantBrowserPermissions(false)
#if DEBUG
            .SetDevToolsEnabled(true)
#else
            .SetDevToolsEnabled(false)
#endif
            .SetLogVerbosity(0);

        var iconPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", OperatingSystem.IsWindows() ? "app.ico" : "app.png");
        if (File.Exists(iconPath))
        {
            app.MainWindow.SetIconFile(iconPath);
        }

        ConfigureTitleBar(app);

        AppDomain.CurrentDomain.UnhandledException += (_, error) =>
        {
            var details = error.ExceptionObject.ToString() ?? "Unknown error";
            var logPath = WriteCrashLog(details);
            try
            {
                app.MainWindow.Invoke(() => app.MainWindow.ShowMessage(
                    "AllInOne PDF stopped because of an error",
                    (logPath is null ? string.Empty : $"Details were written to {logPath}\n\n") + Truncate(details, 1500)));
            }
            catch (Exception)
            {
                // The window may already be gone; the log file is the record.
            }
        };

        app.Run();
        return 0;
    }

    /// <summary>
    /// The app's own title bar (Windows): read the preference before the window exists so it opens in the right mode,
    /// hook the frame as soon as the native window is created, and follow the Settings switch at runtime.
    /// </summary>
    private static void ConfigureTitleBar(PhotinoBlazorApp app)
    {
        if (!WindowFrame.IsCustomTitleBarAvailable)
        {
            return;
        }

        var preferences = app.Services.GetRequiredService<IPreferencesService>();
        var useSystemTitleBar = false;
        try
        {
            preferences.LoadAsync().GetAwaiter().GetResult();
            useSystemTitleBar = preferences.Current.UseSystemTitleBar;
        }
        catch (Exception)
        {
            // Unreadable preferences: the app's title bar is the default; the UI reports load problems itself.
        }

        var frame = app.Services.GetRequiredService<WindowFrame>();
        app.MainWindow.RegisterWindowCreatedHandler((_, _) => frame.Attach(!useSystemTitleBar));
        preferences.Changed += () =>
        {
            var custom = !preferences.Current.UseSystemTitleBar;
            if (custom != frame.UseCustomTitleBar)
            {
                app.MainWindow.Invoke(() => frame.SetUseCustomTitleBar(custom));
            }
        };
    }

    private static bool IsHelpFlag(string arg) =>
        arg is "-h" or "/?" or "-?" || string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase);

    private static string HelpText()
    {
        var nl = Environment.NewLine;
        return string.Join(nl,
            $"{BuildInfo.ProductName} {BuildInfo.DisplayVersion}",
            string.Empty,
            "Usage: AllInOnePdf [option]",
            "Without an option the app window opens.",
            string.Empty,
            "Options:",
            "  --paths                 Print where settings, themes, history and output go, then exit.",
            "  --check-assets [file]   Check that the window can load every file it needs, then exit.",
            "                          Writes the report to [file], or to the console. Exit code 1 if anything is missing.",
            "  --help, -h, /?          Show this help, then exit.",
            string.Empty,
            "Data folder (the first rule that applies wins):",
            $"  1. The {AllInOnePdf.Core.Storage.AppPaths.DataDirectoryVariable} environment variable, when it is set.",
            $"  2. Portable mode: a {AllInOnePdf.Core.Storage.AppPaths.PortableMarkerFileName} file next to the program. Data goes to a Data folder beside it,",
            "     or to the relative folder named on its first line.",
            "  3. Portable mode: the PortableApps.com layout (the program in App\\AllInOnePdf, with a sibling Data folder",
            "     or App\\AppInfo). Data goes to that Data folder.",
            "  4. Otherwise %APPDATA%\\AllInOnePdf on Windows, or ~/.config/AllInOnePdf on Linux.",
            string.Empty,
            "To start fresh, close the app and delete the data folder. Crash reports are written to its logs folder.",
            $"Help and problem reports: {BuildInfo.IssuesUrl}");
    }

    /// <summary>
    /// Windows: a WebView2 fixed-version runtime dropped into <c>WebView2Runtime</c> beside the executable makes the
    /// app run on machines without the Evergreen runtime. The WebView2 loader honours this environment variable.
    /// </summary>
    private static void ConfigureBrowserRuntime(AllInOnePdf.Core.Storage.AppPaths paths)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundled = Path.Combine(AppContext.BaseDirectory, "WebView2Runtime");
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER")) && File.Exists(Path.Combine(bundled, "msedgewebview2.exe")))
        {
            Environment.SetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER", bundled);
        }

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER")))
        {
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", paths.BrowserDataDirectory);
        }
    }

    private static string? WriteCrashLog(string details)
    {
        try
        {
            var logs = Path.Combine(new AllInOnePdf.Core.Storage.AppPaths().DataDirectory, "logs");
            Directory.CreateDirectory(logs);
            var path = Path.Combine(logs, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path, details);
            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    private static int CheckAssets(Microsoft.Extensions.FileProviders.IFileProvider provider, string? reportPath)
    {
        string[] required =
        [
            "index.html",
            "AllInOnePdf.styles.css",
            "_content/AllInOnePdf.UI/css/app.css",
            "_content/AllInOnePdf.UI/js/app.js",
            "_content/AllInOnePdf.UI/fonts/Inter-Variable.ttf",
            "_content/AllInOnePdf.UI/Layout/NavMenu.razor.js",
            "_content/AllInOnePdf.UI/Pages/Reader.razor.js",
            "_content/AllInOnePdf.UI/Pages/ThemeBuilder.razor.js",
            "_content/AllInOnePdf.UI/Components/Reader/ReaderSurface.razor.js",
            "_content/AllInOnePdf.UI/Components/Reader/ReaderPages.razor.js",
            "_content/AllInOnePdf.UI/Components/ThemeBuilder/BuilderPreview.razor.js",
            "_content/Microsoft.FluentUI.AspNetCore.Components/css/reboot.css",
            "_content/Microsoft.FluentUI.AspNetCore.Components/Microsoft.FluentUI.AspNetCore.Components.lib.module.js",
        ];

        var lines = new List<string>();
        var missing = 0;
        var queue = new Queue<string>(required);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (queue.Count > 0)
        {
            var path = queue.Dequeue();
            if (!seen.Add(path))
            {
                continue;
            }

            var info = provider.GetFileInfo(path);
            var ok = info.Exists && !info.IsDirectory;
            if (!ok)
            {
                missing++;
            }

            lines.Add($"{(ok ? "OK      " : "MISSING ")} {path}{(ok ? $"  ({info.Length} bytes)" : string.Empty)}");

            // Scoped-CSS bundles import the (fingerprinted) RCL bundles; follow those imports so they are checked too.
            if (ok && path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) && info.Length < 64 * 1024)
            {
                using var reader = new StreamReader(info.CreateReadStream());
                var css = reader.ReadToEnd();
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(css, @"@import\s+(?:url\()?['""]?([^'"")\s;]+)['""]?\)?"))
                {
                    var target = m.Groups[1].Value.TrimStart('/');
                    if (!target.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        queue.Enqueue(target);
                    }
                }
            }
        }

        lines.Add(missing == 0 ? "All assets resolved." : $"{missing} asset(s) missing.");
        var report = string.Join(Environment.NewLine, lines);
        if (reportPath is not null)
        {
            File.WriteAllText(reportPath, report);
        }
        else
        {
            Console.WriteLine(report);
        }

        return missing == 0 ? 0 : 1;
    }
}
