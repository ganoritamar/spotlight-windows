# Spotlight for Windows

A small Mac-style search bar for Windows 10 and 11. Press **Ctrl+Space**, type a few letters, press **Enter**.
It doesn't use PowerToys or the Windows Search indexer: it keeps its own small index of your apps and your personal folders.

## Using it

| Key | What it does |
|---|---|
| **Ctrl+Space** | Open or close the search bar |
| Type | Search apps and files (loose matching: `vsc` finds *Visual Studio Code*, `chr` finds *Chrome*) |
| **↑ / ↓** (or Tab) | Move through results |
| **Enter** | Open the selected item |
| **Ctrl+Enter** | Show it in File Explorer instead |
| **Esc** or click elsewhere | Hide the bar |

Things you open often or recently move up the list. If you pick something for a certain search
(e.g. "ch" → Chrome), it shows up first the next time you type that search.

**Tray icon** (magnifying glass near the clock): left-click opens the bar; right-click has
*Start with Windows*, *Edit settings…*, *Reload settings and rebuild index* and *Quit*.

## Download

Works on **Windows 10 (version 1903 or newer) and Windows 11**. Get a zip from the Releases page:

| File | Pick it if |
|---|---|
| `Spotlight-…-win-x64-standalone.zip` | Not sure. Works on any normal Windows PC, nothing else to install (~60 MB) |
| `Spotlight-…-win-x64.zip` | You already have the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (tiny download; if you don't, Windows offers a link to it on first start) |
| `…-win-arm64…` | Your laptop has a Snapdragon / ARM processor |

1. Unzip it somewhere permanent, for example `C:\Users\<you>\AppData\Local\Programs`.
2. Open the `Spotlight` folder and double-click `Spotlight.exe`.
3. Windows may show **"Windows protected your PC"**, because the app isn't signed with a paid certificate.
   Click **More info → Run anyway**. This only happens the first time.
4. Right-click the tray icon → **Start with Windows** if you want it on every boot.

To update, quit Spotlight from the tray and replace the folder. Your settings are kept.

Notes:
- On Windows 11 22H2 and newer the bar is frosted glass; on older versions it's solid dark gray.
- The bar opens on the screen your mouse is on.
- **Ctrl+Space** switches the input language on some Chinese/Japanese/Korean setups. If Spotlight
  can't get it, it says so; pick another shortcut in the settings (below).

## Build from source

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). In PowerShell, from this folder:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1
```

This builds an optimized copy into `%LOCALAPPDATA%\Programs\Spotlight` and starts it.
`scripts\package.ps1` builds the release zips into `dist\`.

## Settings

Right-click the tray icon → **Edit settings…** opens `%APPDATA%\Spotlight\settings.json` in Notepad:

- `hotkey`: e.g. `"Ctrl+Space"`, `"Alt+Space"`, `"Ctrl+Shift+K"`
- `folders`: which folders to index (default: Desktop, Documents, Downloads, Pictures, Videos, Music)
- `excludeFolderNames`: folder names to skip anywhere (`node_modules`, `.git`, `bin`, ...)
- `skipDotFolders`: skip folders starting with a dot
- `maxResults`: how many results to show

After saving, right-click the tray icon → **Reload settings and rebuild index**.

## Where things are stored

- `%APPDATA%\Spotlight\settings.json`: your settings
- `%APPDATA%\Spotlight\usage.json`: what you've opened (for ranking)
- `%LOCALAPPDATA%\Spotlight\`: the saved index and a small log (`spotlight.log`). Safe to delete; it's rebuilt.

## Performance

Measured on an i5-10500 / 16 GB with ~41,500 files and 158 apps indexed:

- Idle CPU: effectively 0% (no timers; it only wakes when you press the hotkey or a file changes)
- Memory shown in Task Manager while hidden: ~18 MB (about 46 MB working set right after use)
- Full rescan of your folders: ~0.15 s at low priority; loading the saved index: under 0.1 s
- Each search: a few milliseconds

To check for yourself: `.\scripts\measure.ps1` (samples for 60 seconds).

## How it's built (for the curious)

C# / .NET 8 with WPF. Projects: `src\Spotlight` (the app) and `tests\Spotlight.Tests` (matching tests, run with `dotnet test`).

- `Indexing\AppIndex.cs`: apps from `shell:AppsFolder` (desktop + Store apps) and Start Menu shortcuts
- `Indexing\FileIndex.cs`: folder scan, live updates via FileSystemWatcher, saved to disk
- `Search\FuzzyMatcher.cs`, `Search\SearchEngine.cs`, `Search\UsageTracker.cs`: matching and ranking
- `MainWindow.xaml`: the frosted search bar; `Interop\`: hotkey, tray icon, icons, acrylic

## Ideas for later

Whole-drive search like *Everything* (reads the NTFS file table; needs a small admin helper),
an inline calculator, Quick Look-style preview, web search when nothing matches, and then
optionally turning off the Windows Search indexer.
