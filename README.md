# Terminal Noosphere Soundpad

Pure `C# / .NET 9` Windows terminal soundpad with a `Mechanicus cathedral` synth bank.

## Features

- background keyboard watcher instead of `Console.ReadKey()`
- reacts only when a terminal window is foreground
- letters mapped to organ glyph tones
- vowels use stronger choir-like formants
- digits use lower bell-organ tones
- Spanish keys supported: `ñ Ñ ç Ç á é í ó ú Á É Í Ó Ú ü Ü ¡ ¿ º ª`
- common ASCII symbols supported: `` !"#$%&'()*+,-./:;<=>?@[\]^_`{|}~ ``
- richer cathedral reverb
- `Alt+N` toggles sound on or off while a terminal window is focused
- `Ctrl+C` exits the watcher

## Terminal Targets

The watcher currently reacts when the foreground process or window class matches common terminal hosts such as:

- `WindowsTerminal`
- `wt`
- `OpenConsole`
- `conhost`
- `cmd`
- `powershell`
- `pwsh`

## Run

```powershell
cd C:\Users\Kdt_T\terminal-noosphere-soundpad
dotnet run --no-build
```

Or use the shortcut command:

```powershell
Omnissiah
```

Keep the watcher running in one terminal, then code in another terminal window or tab.

## Utility commands

Warm the cache:

```powershell
Omnissiah --warm-cache
```

Emit one key:

```powershell
Omnissiah --emit a
```
