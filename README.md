# WinEML

A lightning-fast `.eml` file viewer for Windows. Think **IrfanView, but for email files**: it opens instantly, shows you the message, and gets out of the way. It is **not** an email client — it sends nothing, syncs nothing, and has no inbox. It just reads `.eml` files.

## Why

Double-clicking an `.eml` file on Windows usually means waiting for Outlook to launch, or fighting with a web client. WinEML opens the file and renders it — headers, body, and attachments — in the time it takes most apps to show a splash screen.

## Features

- **Cold-start optimized.** Precompiled to native code (ReadyToRun) and self-contained — no runtime to install, no JIT warm-up on the startup path. The window, headers, and text body appear immediately; the HTML engine warms up *in parallel* so its cost overlaps parsing instead of stacking on top of it.
- **Real HTML rendering** via the Edge WebView2 engine — handles real-world HTML email faithfully.
- **Private and locked down by default.** JavaScript is disabled outright. Every remote request — tracking pixels, remote images, web fonts, beacons — is **hard-blocked at the engine level**. Only images embedded *inside the file* (inline `cid:` parts, inlined as local `data:` URIs) display. Clicking a link hands it to your browser; nothing navigates or loads on its own.
- **Folder browsing.** Open one `.eml` and page through the rest of the folder with `◀ / ▶`, just like an image viewer.
- **Attachments.** Listed along the bottom; click to save.
- **Three views:** HTML · Text · Source (raw RFC 822).
- **Portable.** One executable, no installer required.

## Download

Grab the latest **`WinEML-<version>-win-x64.exe`** from the [Releases](../../releases) page and run it — that's it. It's a single self-contained, portable executable: no installer, nothing to unzip, and no .NET required on the machine. To make it your default `.eml` viewer, see [below](#make-it-your-default-eml-viewer).

- **Verify the download** (optional): each release ships a `SHA256SUMS.txt`. Check with `Get-FileHash WinEML-<version>-win-x64.exe -Algorithm SHA256`.
- **First-run SmartScreen warning:** because the exe isn't code-signed yet, Windows may show *"Windows protected your PC."* Click **More info → Run anyway**. (Signing is on the roadmap.)

## Requirements

- Windows 10 / 11 (x64)
- The [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) — preinstalled on current Windows. Without it, HTML view is disabled and Text/Source still work.

## Usage

```
WinEML.exe path\to\message.eml      Open a file
WinEML.exe                          Start empty (Ctrl+O or drag a file in)
WinEML.exe --register               Register WinEML as an .eml handler (per-user)
WinEML.exe --unregister             Undo the association
```

### Make it your default `.eml` viewer

Easiest: open any `.eml` in WinEML, then **Tools ▾ → "Set WinEML as default for .eml…"**. WinEML registers itself and pops Windows' own "Open with" chooser — pick WinEML and tick **"Always"**.

> Windows 10/11 protect the default-app choice (anti-hijacking), so no app can silently claim a file type. WinEML registers itself as an available handler; the one-click confirmation in the "Open with" dialog (or **Settings → Apps → Default apps**) is the OS-sanctioned way to finish. The CLI `--register` does the same registration headlessly.

### Keyboard

| Key | Action |
| --- | --- |
| `Ctrl+O` | Open file |
| `Ctrl+←` / `Ctrl+→` | Previous / next file in folder |
| `Ctrl+Home` / `Ctrl+End` | First / last file |
| `Alt+H` / `Alt+T` / `Alt+U` | HTML / Text / Source view |
| `F5` | Reload |
| `Esc` | Close |

## Building

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/download).

```powershell
# Run from source
dotnet run

# Produce the single self-contained native exe
dotnet publish -c Release -r win-x64
# → bin\Release\net9.0-windows\win-x64\publish\WinEML.exe
```

`dotnet publish` yields exactly one portable `WinEML.exe` (ReadyToRun, self-contained, compressed) — copy it anywhere and run it; no .NET install required on the target machine.

> **Why ReadyToRun and not NativeAOT?** AOT would shave a little more off startup, but WebView2's .NET SDK depends on built-in COM interop, which NativeAOT disables — the two are mutually exclusive. Faithful, sandboxable HTML rendering matters more here than the marginal AOT win (WebView2's own init dominates cold-open anyway), so WinEML uses ReadyToRun, which keeps WebView2 working while still precompiling to native.

### Releasing

Releases are built by GitHub Actions. Push a tag and a **draft** release is staged with the portable zip attached; review it on the Releases page, then publish.

```
git tag v0.1.0
git push origin v0.1.0
```

## Security model

Email is hostile input, so WinEML is locked down by default:

- **No scripts.** JavaScript execution is disabled in the renderer (`IsScriptEnabled = false`).
- **No network.** Every remote request — tracking pixels, remote images, web fonts, beacons — is hard-blocked at the WebView2 layer. Only resources embedded *inside the file* are shown: inline `cid:` images are inlined as local `data:` URIs.
- **Strict CSP, enforced twice.** Rendered messages are served with a `Content-Security-Policy` **response header** (`default-src 'none'; img-src data:; style-src 'unsafe-inline' data:; font-src data:; media-src data:; base-uri 'none'; form-action 'none'`) — a header governs the document no matter how malformed the email's markup is — and the same policy is injected as a meta tag for defense-in-depth. The renderer refuses remote/active content before the request blocker is even consulted.
- **Nothing touches the disk.** Rendered bodies are streamed to the engine straight from memory — no temp files, ever, for any message size.
- **No surprise navigation.** Clicking a link hands the URL to your default browser; nothing loads or navigates in-app.
- **Sandboxed engine.** HTML and images are parsed/decoded inside WebView2's Chromium multi-process sandbox. Keep the auto-updating WebView2 runtime current for the latest decoder fixes.
- **Strictly a viewer.** No inbox, no accounts, no outbound mail — WinEML never sends anything.

Found a vulnerability? Please open a private security advisory rather than a public issue.

## License

[MIT](LICENSE)
