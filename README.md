# Better Mouse Without Borders (Windows-only, draft)

Custom two-agent tool to forward laptop keyboard/trackpad input to a Windows PC without letting the cursor escape to the laptop when locked.

## Layout
- `PROTOCOL.md`: message schema and transport rules.
- `src/Common`: shared models and helpers.
- `src/Receiver`: PC-side app that listens and injects input.
- `src/Sender`: laptop-side app that hooks input and forwards it.

## Quick start (dev, requires .NET 8 SDK)
```powershell
dotnet restore
dotnet build
dotnet run --project src/Receiver/Receiver.csproj -- --psk secret123
dotnet run --project src/Sender/Sender.csproj -- --psk secret123 --receiver 192.168.1.50
```

## Modes
- `locked`: forward everything to PC; suppress locally.
- `local`: normal laptop behavior.
- Hotkey toggles modes (default Ctrl+Alt+F12).

## Security
- Pre-shared secret validated per message (HMAC). Option to wrap in TLS later.

## Status
Draft implementation with core plumbing, hooks, and SendInput injection. Tray UI and discovery remain to be added.

