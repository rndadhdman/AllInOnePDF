AllInOne PDF Portable (Windows x64)
===================================

Run:        AllInOnePdfPortable.exe
App:        App\AllInOnePdf\     (the program and its runtime; nothing to install)
Settings:   Data\                (preferences, themes, history, browser cache) - delete it to reset
Output:     Data\Output\         (default folder for converted files; "Save next to source" is on by default)

Nothing is installed and nothing is written outside this folder. AllInOnePdfPortable.exe is a small launcher
that starts App\AllInOnePdf\AllInOnePdf.exe and passes the command line through; it needs only the .NET
Framework 4.8 that is part of Windows 10 and 11. The app itself carries its own .NET runtime.

Requirements
  - Windows 10 / 11 x64.
  - The Microsoft Edge WebView2 Runtime, which is part of Windows 11 and most Windows 10 machines.
    For a machine without it, download the "Fixed Version" WebView2 runtime from Microsoft, extract it,
    and drop the folder (the one containing msedgewebview2.exe) into App\AllInOnePdf\WebView2Runtime\.
    The app then uses that copy automatically.

PortableApps.com
  This folder follows the PortableApps.com format (App\AppInfo\appinfo.ini). Copy it into
  <drive>\PortableApps\ and it appears in the platform menu, which starts AllInOnePdfPortable.exe.

Diagnostics (from a command prompt in this folder)
  App\AllInOnePdf\AllInOnePdf.exe --paths          prints where data is stored
  App\AllInOnePdf\AllInOnePdf.exe --check-assets   verifies the bundled interface files

Licence: MIT (App\AllInOnePdf\LICENSE.txt). Third-party notices are listed in the project README.
