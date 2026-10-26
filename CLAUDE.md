# KUDUDIFF

Compare two text or code files side by side (Meld-like): changes, copy across, save with .bak, patch/HTML export.
Part of the KUDU family of free, open-source (MIT) Windows apps by Azeem Bukhari.

## Working with Azeem (the owner)

- **Branches:** work on `main` and push to `main`. Ask before creating any other branch.
- **New features:** first say what you understood, and ask your doubts and suggestions, one question at a time.
  Only start building when Azeem says "build".
- **Look:** keep the app's current look. No redesigns unless asked.
- **About window credit:** "Made by Azeem, with Claude and ♥" (the heart in the accent colour). Keep it.
- **Commits:** author `Azeem Bukhari <12558389+azeembukhariprivate@users.noreply.github.com>`. Clear messages that say
  what changed and why. Don't put AI model names or versions in commits, code or docs.
- **Words:** the app's text, README and release notes use plain everyday words, not technical jargon.
- **Before pushing:** build and run the tests locally, and re-read the diff. After pushing, watch the GitHub Actions
  build until it's green; fix failures, never skip or disable a test to get green.

## How the app is built

- .NET Framework 4.8 WPF app. The screens are built in C# (no XAML compiler); the themes (`Themes/Light.xaml`,
  `Dark.xaml`, `Styles.xaml`) are XAML loaded at startup as embedded resources, using resource keys such as `Accent`,
  `Text`, `SurfaceBg`. Use the helpers in `Controls/Ui.cs` and existing keys rather than hard-coded colours.
- It builds with the plain .NET SDK on any OS (Microsoft.NETFramework.ReferenceAssemblies); it runs on Windows 10/11.
- Logic that doesn't need Windows lives in `Core/` and is tested by `tests/KUDUDIFF.Tests` (a .NET 8 console program).
- `KUDUDIFF.exe --self-test` drives the real app on Windows (GitHub Actions runs it, keeping screenshots of every window as
  a build download). Add a self-test step for new screens and features.
- Settings live in `HKEY_CURRENT_USER\Software\KUDUDIFF`. The Inno Setup installer (`installer/KUDUDIFF.iss`) installs per
  user, without administrator rights.

```
dotnet run --project tests/KUDUDIFF.Tests -c Release
dotnet build src/KUDUDIFF/KUDUDIFF.csproj -c Release -o out/KUDUDIFF
out\KUDUDIFF\KUDUDIFF.exe --self-test        (Windows only)
```

## Shared code (kudu-common)

The files listed in `.kudu-common` (the look and controls in `Controls/`, `Core/Admin.cs`, `AppInfo.cs`, `Args.cs`,
`ThemeManager.cs`, `Unelevated.cs`, `UpdateChecker.cs`, `Views/Dialogs.cs`, `Views/Screenshots.cs`, ...) are copied from
[kudu-common](https://github.com/azeembukhariprivate/kudu-common) and start with a comment saying so.
**Don't edit them here**: change them in kudu-common, then run `python sync.py ../kududiff` there and commit both repos.
The build fails when a copy differs from kudu-common. App-specific details go in `Core/AppDetails.cs`.

## Releasing

1. Raise `<Version>` in `src/KUDUDIFF/KUDUDIFF.csproj` (and `MyAppVersion` in `installer/KUDUDIFF.iss`).
2. Commit and push to `main`.
3. GitHub Actions (`.github/workflows/build.yml`, Windows) tests and builds the app. When no release exists yet for that
   version it creates the `v` tag and publishes the installer and portable zip on the Releases page, plus a copy named
   `KUDUDIFF-setup.exe` that the README's download button links to (`releases/latest/download/KUDUDIFF-setup.exe`).
   Pushes that keep the same version only build.

## Notes

- The editor is AvalonEdit (MIT, listed in THIRD-PARTY-NOTICES.txt). Syntax colours: `Views/Syntax.cs`; own
  definitions in `Highlighting/*.xshd`.
- The comparison engine (`Core/Lcs.cs`, `Core/Comparison.cs`, `Core/Export.cs`) is the app's own code and is tested
  against brute force; keep it that way.
- Scope agreed for v1: two files only, text and code only, no folder compare, no 3-way merge, no Git features.
