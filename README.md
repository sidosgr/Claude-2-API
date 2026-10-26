# Local Claude Bridge

An experimental, open-source Windows app that connects a **regular Claude web session** to a local text-generation endpoint. Sign in directly on `https://claude.ai` inside the app; no Claude Code login or Anthropic API key is used.

**Version 0.5.0:** named profiles with isolated saved logins, ordered usage-limit failover, and full-width settings when the internal browser is hidden. Also includes tabbed settings, complete model discovery including the More models submenu, request-level thinking effort, click-to-copy API URL, themed right-click controls, automatic connection, and a single Windows installer. Live discovery returned Sonnet 5.5 and Haiku 5.5; a Sonnet request with High thinking returned HTTP 200 and OK. Remaining quota and exact reasoning-token budgets are not exposed. See [TESTING.md](TESTING.md). This app uses undocumented Claude web interfaces and may be affected by website changes or account restrictions.

## Run on Windows

Requirements: Windows 10/11, .NET 8 Desktop Runtime for a framework-dependent build, and the [Microsoft Edge WebView2 Evergreen Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/). Self-contained release packages include .NET; WebView2 is still required.

From source, install the .NET 8 SDK, then run:

```powershell
.\scripts\Build-Release.ps1
.\artifacts\release\0.5.0\LocalClaudeBridge-Setup-0.5.0-x64.exe
```

1. Sign in on the Claude page inside the app. Email login may work when an identity provider blocks embedded browsers. Complete any browser verification yourself.
2. The app checks the session automatically, selects the first workspace, and refreshes models. Choose another workspace manually if needed. The browser hides when connected; **Show internal browser** reveals it. Sign-in popups use separate windows sharing the same app profile. Complete sign-in yourself; if a provider blocks embedded login, try Claude's email option.
3. Match the **Claude web model** setting to a model available to your account. Default: `claude-sonnet-5-5` (Sonnet 5.5). Machine IDs use hyphens, not decimal dots; the earlier `claude-sonnet-5.5` input is migrated automatically.
4. Click **Test local endpoint → Claude** on the **Logs** tab. It sends “Reply with exactly OK.” through the local API and creates a chat in Claude.
5. Create a local API key for your client and copy the displayed key. Only its hash is saved. To run without a key, uncheck **Require a local API key**, save settings, and restart the endpoint. Local SDKs that insist on a key can use a dummy value in this mode.

The endpoint starts on demand and binds only to `127.0.0.1`. Default base URL: `http://127.0.0.1:8081/v1`. Settings provide port selection, OpenAI compatibility, model discovery and selection, request timeout, local authentication, tool-call translation, and appearance. **Dark mode is the default**, with an optional light theme. Hover the info labels for explanations and suggested values.

## Connection, settings, and sign-in

Settings are organized into **Account, Profiles, API, Models, Keys, App, and Logs** tabs. Click the endpoint URL at the top right to copy it. The tray and window right-click menus provide model and thinking selection, dark mode, browser visibility, startup preferences, API options, update checks, and shortcuts to settings tabs.

Use **Connect Claude + start API** in the window or tray-icon right-click menu. Separate green indicators show **Claude · Connected** and **Local API · Running**. With **Resume endpoint on startup** enabled, later launches resume an endpoint that was successfully started. **Stop** disables resuming until you connect again. The browser hides after verification; its visibility preference persists. Sign-in popup handling preserves the parent page, opener relationship, and shared WebView2 environment/profile. It reopens Claude to check the saved session when the popup closes.

The single installer EXE bundles .NET, verifies its embedded ZIP, installs per user, creates a Start-menu shortcut, and opens the app. WebView2 Evergreen Runtime is still required. The portable ZIP remains available. This custom installer does not register an Add/Remove Programs uninstaller.

Thinking passthrough accepts `reasoning_effort`, `reasoning.effort`, and Messages-shaped `thinking` / `output_config.effort`. Off/none disables web thinking. Low, medium, high, xhigh, and max pass through unchanged with web thinking mode extended; minimal maps to low. A saved default on the Models tab applies only when API controls are omitted. Explicit off/none overrides it. The X-Local-Claude-Thinking response header reports the effective mode and effort. High was accepted in a live Sonnet 5.5 test; other levels have transport regression coverage. Hard thinking budgets and returned thinking blocks remain unavailable. This is experimental website parameter translation.

## Named profiles and usage-limit failover

Open **Profiles**, enter a unique name, and click **Add new profile**. Sign in using the internal browser. Each profile has separate browser data; adding a profile does not copy an existing login. The old login remains under **Default**. Use **Rename selected** to change a name and **Use / sign in** to switch manually. Profiles are also available from the tray menu.

Enable **Switch profiles automatically at a usage limit** and save the preference. When Claude returns a recognized HTTP/SSE usage-limit response, the request tries the next enabled, signed-in profile in the configured order. Each profile is tried at most once. Disabled, cooling-down, signed-out, or model-incompatible candidates are skipped. The successful profile stays active. Non-quota generation errors do not cause a retry. When no eligible profile remains, the API returns 429. All attempts share the original request timeout.

Limited profiles are paused locally for the configured cooldown (default 60 minutes; range 1–1440). This is not an estimate of Claude's real quota reset. The pause survives restarts. Use **Reset selected profile pause** after the allowance becomes available again. **Move up/down** controls the next-profile order. Profile names and browser sessions are saved locally and retained during updates.

Turn off **Show internal browser** to remove the browser area entirely and expand the settings across the window. Turn it back on to view Claude. **Open Claude**, profile sign-in, and Connect when sign-in is needed can reveal the internal browser.

The public repository is now [sidosgr/Claude-2-API](https://github.com/sidosgr/Claude-2-API). Version 0.5.0 checks the renamed repository for updates. Older builds with strict old-repository URL checks may need this one-time installer download after the rename.

## Update inside the app

Update status appears beside the app version. **Check for updates** reads this repository's latest public stable GitHub release. Automatic checks are enabled by default and can be disabled in settings; the app does not automatically download or install updates.

1. Click **Download update** when a newer version is available. Progress and cancellation stay in the app. Exact size and SHA-256 must match the GitHub release metadata. Valid cached packages are reused.
2. Click **Install and restart**. The app rechecks the package, saves settings, stops the endpoint, and starts its bundled standalone updater.
3. The updater waits for the app to exit, stages new files, keeps a rollback copy, and launches the new version. Startup must be acknowledged or the previous install is restored. A running endpoint is restarted with its saved configuration.

Updated files live in `%LOCALAPPDATA%\LocalClaudeBridge\App`, with a **Local Claude Bridge** Start-menu shortcut. Settings, key hashes, and browser login remain in the parent data folder. Downloads, helpers, and rollback copies live under `Updates\`. Administrator access and browser download handoffs are unnecessary.

The updater accepts the expected Windows x64 asset from this repository over approved GitHub HTTPS hosts, requires a digest, caps downloads at 512 MiB, and rejects unsafe archive paths. Extract the whole package: it includes the helper needed for installation.

For an explicitly requested scripted update, `--install-update` runs the same verified check/download/install flow. Add `--start-endpoint` to restart the endpoint afterward. Ordinary launches use the interactive controls.

## Local API

By default, `/v1` routes require `Authorization: Bearer <local-key>` or `x-api-key: <local-key>`. Keys are generated by this app and are not Anthropic credentials. The optional no-key mode accepts requests without credentials while preserving loopback binding and browser-origin restrictions. `GET /health` returns process health and authentication-mode status; it does not assert that Claude is signed in.

| Route | Behavior |
| --- | --- |
| `GET /v1/models` | Returns selectable model IDs read from the current Claude picker, excluding disabled and upgrade-only entries. Until discovery, returns `claude-web`. |
| `POST /v1/chat/completions` | OpenAI-compatible text and translated function-tool requests; optional buffered SSE. Can be disabled in settings. |
| `POST /v1/messages` | Experimental Messages-shaped text/tool endpoint with buffered SSE. Not full Anthropic SDK compatibility. |

Python OpenAI client example:

```python
import os
from openai import OpenAI

client = OpenAI(
    base_url="http://127.0.0.1:8081/v1",
    api_key=os.environ["LOCAL_CLAUDE_KEY"],
)
answer = client.chat.completions.create(
    model="claude-web",
    messages=[{"role": "user", "content": "Reply with exactly OK."}],
)
print(answer.choices[0].message.content)
```

Model selection: `claude-web` remains a backward-compatible alias for the configured default. After discovery, clients can explicitly request `claude-sonnet-5-5` or `claude-haiku-5-5` when those IDs appear in the picker. The catalog is scoped to the signed-in browser session and refreshed after workspace verification. A selectable model is not a claim about remaining quota.

Pi custom providers need `"authHeader": true` alongside `"api": "openai-completions"`, the local base URL, and their local API key. Reopen `/model` to reload `models.json`. Do not put a real local key into a public configuration example.

Harness compatibility and limits:

- Text and function tools are supported. Tool definitions/history are translated through structured prompts; the reply is checked for the expected protocol ID, declared tool names, object arguments, required properties, and call-count constraints. The harness executes returned calls. This is prompt-based translation, not native website tool execution or complete JSON Schema validation. Malformed tool replies return 502 without retrying or forwarding a tool call.
- Common Pi/OpenAI fields such as `max_tokens`, `max_completion_tokens`, `stream_options`, `store`, `metadata`, `temperature`, and tool selection are accepted. Token budgets become approximate prompt guidance; sampling and seed cannot be enforced; thinking controls use the web mapping above. The `X-Local-Claude-Compatibility` response header lists these caveats. `stop` strings are applied to the final text. `json_object` output is syntax-checked; strict JSON Schema output is unsupported.
- Images, audio, files, legacy function APIs, and the Responses API are unsupported. Unknown fields and invalid values return explicit errors.
- OpenAI SSE is buffered: generation finishes before output is emitted. It is not token-by-token delivery.
- Every request creates a new Claude chat. Full caller history is serialized into prompt context, including system/developer instructions; this does not provide native system-message priority. No existing chats are read or deleted.
- Token counts are unavailable: `usage` is null rather than fabricated. The Messages-shaped endpoint is not a complete official SDK implementation.
- One generation at a time. A recognized Claude usage-limit response can trigger the configured profile failover described above. Each eligible profile is attempted at most once per request, within the original request timeout. Other generation errors are returned without profile retry. There is no paid API fallback or quota bypass.
- The maximum request body is 1 MiB; the maximum assembled prompt is 400,000 characters. Browser-origin requests are rejected; this is intended for local SDKs and desktop tools.

## Local data

The app uses `%LOCALAPPDATA%\LocalClaudeBridge`:

- `BrowserProfile\`: the Default profile’s existing WebView2 data, preserved in place.
- `Profiles/<id>/BrowserProfile/`: isolated browser data for each additional named profile.
- `profiles.json`: custom profile names, order, enabled flags, active ID, and local cooldown times. No cookies are stored in this metadata file.
- `settings.json`: endpoint preferences, your selected workspace identifier, and SHA-256 hashes of local keys, never the full keys.
- `web-session-test.json`: diagnostic stage names and fixed status messages; no prompts, generated replies, session cookies, or authorization tokens.

Session cookies are never extracted, copied from another browser, exported, or committed. Requests run as same-origin fetches in the visible Claude page. Sign out/clear session stops the server and clears this app’s profile. Closing the app stops the endpoint while retaining its browser profile for the next launch. Do not share your browser profile or local settings directory.

## Development and checks

```powershell
dotnet run --project .\tests\Bridge.Tests.csproj -c Release
node .\tests\stream.test.cjs
node .\tests\web-transport.test.cjs
node .\tests\models.test.cjs
dotnet run --project .\tests\Updates\Updates.Tests.csproj -c Release
.\scripts\Build-Release.ps1
```

The integration suite uses a synthetic backend. It checks local authentication and revocation, origin/Host restrictions, validation, size limits, concurrency, endpoint toggling, and response formatting. The JavaScript tests cover stream framing, Unicode, truncated/error responses, and repeated injection. They do not replace a real signed-in account test.

## Inspiration and attribution

- [EvanZhouDev/openai-oauth](https://github.com/EvanZhouDev/openai-oauth), Apache-2.0: local proxy and bring-your-own-account product concept.
- [mattbnz/LLMux](https://github.com/mattbnz/LLMux), MIT, copyright 2025 Pimzino: initial OAuth feasibility research and proxy-management inspiration. Its Claude Code OAuth approach was replaced in this app.
- [diegosouzapw/OmniRoute Claude web adapter](https://github.com/diegosouzapw/OmniRoute/blob/release/v3.8.52/docs/providers/CLAUDE_WEB.md): reference for undocumented web conversation routes and payload fields. This app uses a newly written WPF/WebView2 implementation.
- [Microsoft WebView2](https://learn.microsoft.com/en-us/microsoft-edge/webview2/): embedded browser runtime and SDK.

MIT licensed; see [LICENSE](LICENSE). Independent project, not affiliated with Anthropic or OpenAI. Anthropic’s published rules restrict third-party use of subscription access; a technical test does not establish provider authorization or support.
