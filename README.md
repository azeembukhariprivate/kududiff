<p align="center">
  <img src="docs/logo-512.png" alt="KUDUDIFF logo" width="128">
</p>

<h1 align="center">KUDUDIFF</h1>

<p align="center">Compare two text or code files on Windows: see exactly what changed, copy changes across, and save. Free and open source.</p>

<p align="center">
  <a href="https://github.com/azeembukhariprivate/kududiff/releases/latest/download/KUDUDIFF-setup.exe"><img src="https://img.shields.io/badge/Download-KUDUDIFF%20for%20Windows-7c3aed?style=for-the-badge" alt="Download KUDUDIFF for Windows"></a>
  <br>
  <sub>The installer for Windows 10 and 11, always the latest version · <a href="https://github.com/azeembukhariprivate/kududiff/releases/latest">portable zip and what's new</a></sub>
</p>

---

## Features

- **Side by side**: both files with line numbers, the added, removed and changed lines coloured, and bands between the two sides joining each change on the left to where it is on the right. Both sides scroll together, keeping matching lines level.
- **The words or characters that changed** are marked inside changed lines, not just the whole line.
- **Moved code**: blocks of three or more lines that moved elsewhere in the file are shown as moved, not as removed here and added there.
- **Go through the changes** with Previous and Next (Alt+Up and Alt+Down, or F7), or click anywhere on the map of all changes beside the scrollbar. The status bar says which change you're on.
- **Edit both sides**: type in either file, or copy a change across with the arrows between the two sides (Alt+Left and Alt+Right). Undo and redo work as in any editor, and the comparison updates as you type.
- **Save safely**: saving keeps the previous version as `name.bak` (can be turned off in Settings), keeps the file's own encoding and line endings, writes to a temporary file first so a failed save never leaves half a file, and warns when another program changed the file since it was opened.
- **Ignore what doesn't matter**:
  - spaces: at line ends, the amount of spaces, or all of them
  - upper and lower case
  - blank lines
  - Windows (CRLF) or Unix (LF) line endings
  - your own patterns (regular expressions), such as dates, version numbers or comments
- **Two ways to compare**: the standard method (Myers, as in diff and git) or **patience**, which keeps functions and blocks together in code.
- **Export** the differences as a **patch** (a unified diff for `patch` or `git apply`) or a **web page report** with both files side by side and the changes coloured, to read in any browser or send to someone.
- **Syntax colouring** for C#, C and C++, Java, JavaScript and TypeScript, Python, PowerShell, SQL, HTML, CSS, XML, JSON, YAML, Markdown, INI, PHP, batch files and Bash, in light and dark.
- **Every kind of text file**: UTF-8 (with or without BOM), UTF-16, UTF-32 and Windows code pages are recognised, and files of up to 200 MB open quickly.
- **Open files your way**: drop files on either side (or two at once), use Open, pick a recent comparison, or right-click in File Explorer → *Compare with KUDUDIFF* (optional, chosen when installing or in Settings). Select two files and right-click, or pick one now and the second one later.
- Light and dark themes, following Windows. Text size, word wrap and showing spaces and tabs can be changed in Settings.

## Download

**[Download the installer](https://github.com/azeembukhariprivate/kududiff/releases/latest/download/KUDUDIFF-setup.exe)** (`KUDUDIFF-setup.exe`, always the latest version), or get any of these from the [Releases page](https://github.com/azeembukhariprivate/kududiff/releases/latest):

- `KUDUDIFF-x.y.z-setup.exe` installs KUDUDIFF for your Windows account (no administrator rights needed to install).
- `KUDUDIFF-x.y.z-portable.zip` runs without installing: unzip it anywhere and open `KUDUDIFF.exe`.

Requires Windows 10 or 11 (the .NET Framework 4.8 it uses is already part of Windows).

You can also start it from a command line: `KUDUDIFF.exe left.txt right.txt`.

## Privacy

KUDUDIFF works on your computer only. Files are changed only when you save them. The only network request it makes is an optional check for a newer version on GitHub, which can be turned off in Settings.

## Build from source

You need the [.NET SDK](https://dotnet.microsoft.com/download) (8 or later). The screens are built in C# and the themes are loaded at startup, so the project builds with `dotnet` on any operating system; the app itself runs on Windows.

```
dotnet run --project tests/KUDUDIFF.Tests
dotnet build src/KUDUDIFF/KUDUDIFF.csproj -c Release -o out/KUDUDIFF
out\KUDUDIFF\KUDUDIFF.exe --self-test
```

The tests check both comparison methods against a brute-force answer on hundreds of random files, the ignore options, changed words, moved code, line positions for scrolling, patches (applied with GNU `patch` when it's installed), reports, encodings, saving with a backup, copying changes, and big files. `--self-test` opens real comparison windows and uses them: going through changes, copying changes across and undoing, saving with a `.bak`, every option, exporting, other encodings, a 60,000-line file, swapping and reloading, the right-click menu, two starts of the app handing their files to one comparison, every language's colours, and every window in both themes; it writes `self-test.log` and exits. To build the installer, install [Inno Setup 6](https://jrsoftware.org/isinfo.php) and compile `installer/KUDUDIFF.iss`.

### Shared code

Some files (the look, dialogs, the update check, administrator helpers) are shared by the KUDU apps and come from
[kudu-common](https://github.com/azeembukhariprivate/kudu-common); each starts with a comment saying so, and `.kudu-common`
lists them. You don't need kudu-common to build this app, but change those files there, not here: the build checks that
they match.

## Publishing a new version

1. Change `<Version>` in `src/KUDUDIFF/KUDUDIFF.csproj` (and `MyAppVersion` in `installer/KUDUDIFF.iss`).
2. Commit and push to `main`.
3. GitHub Actions tests and builds the app. When no release exists yet for that version, it creates the `v` tag and publishes the installer and portable zip on the Releases page. Pushes that keep the same version only build.

## License

[MIT](LICENSE). See [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
