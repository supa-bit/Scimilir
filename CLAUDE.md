# Working on Scimilir

## Who this is for

The owner is learning to code. Explain things in plain language, step by
step, without jargon (say exactly where to click and what to type). They use
Windows, GitHub Desktop and VS Code. README.md is written for them: keep it
current when anything they see or do changes.

## What it is

A chat app that runs on the owner's Windows PC and is used from the PC's
browser or a phone on the same Wi-Fi. Models run either on the PC (LLamaSharp,
with the Vulkan backend for graphics cards and the CPU backend as fallback) or
in the cloud through OpenRouter. ASP.NET Core minimal API (.NET 10) serving a
plain HTML/CSS/JS page with no build step and no outside scripts (it must work
offline).

## Layout

- `Scimilir/Program.cs`: start-up, login cookies, listening on all networks.
- `Scimilir/Api.cs`: every `/api` address; replies stream as server-sent events.
- `Scimilir/Providers/`: `LocalProvider` (LLamaSharp), `OpenRouterProvider`,
  `ModelDownloads` (Hugging Face search and download).
- `Scimilir/Storage/`: settings, password hashing, chat files.
- `Scimilir/wwwroot/`: the page (`app.js`, `markdown.js`, `app.css`).
- `Scimilir/TrayIcon.cs`: the icon by the clock; only in the Windows app.
- `installer/Scimilir.iss` + `.github/workflows/installer.yml`: the Windows
  installer (Inno Setup), built on GitHub for every push to `main` and put on
  Releases as `v<Version>`.
- User data lives in `%LOCALAPPDATA%\Scimilir` (`Startup.DefaultDataFolder`),
  never next to the program.

## The Windows app

The owner installs Scimilir with `Scimilir-Setup-<version>.exe` from the
Releases page; that is how they use it. It is built with
`dotnet publish Scimilir -c Release -r win-x64 --self-contained -p:WindowsApp=true`,
which targets `net10.0-windows`, has no console window (WinExe), shows the
tray icon, and logs to `scimilir.log` in the data folder. For a new release,
raise `<Version>` in `Scimilir.csproj` and push to `main`.

## Rules

- Anything shown to the user (errors, labels, hints) is plain, friendly English.
- `markdown.js` escapes everything before adding formatting; keep it that way.
- Only the PC itself (loopback) may set the first password.
- Never send the OpenRouter key back to the page; only a hint of it.

## Building and testing

- Build: `dotnet build`. Run: `dotnet run --project Scimilir` (or `start.bat`).
- Tests: `dotnet test`. All must pass before pushing; GitHub runs them too.
- The tests use `Scimilir.Tests/Assets/tiny-chat.gguf`, made by
  `tools/make_tiny_model.py` (needs `pip install gguf numpy`).
- Check visible changes in a browser (PC and phone sizes, light and dark) with
  screenshots before saying they work.
