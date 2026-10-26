# Verification record

## Release 0.4.2

- 56 API integration assertions passed, including saved thinking defaults, explicit overrides, unchanged low/medium/high/xhigh/max forwarding, and the effective-thinking response header.
- Model tests cover delayed picker hydration, the More models submenu merged with the primary model, exclusion of upgrade-only entries, cancellation, and DOM extraction. Stream and transport suites pass. The updater suite has 28 passing assertions.
- Live authenticated GET /v1/models returned Sonnet 5.5, Haiku 5.5, Sonnet 5, Sonnet 4.6, and Haiku 4.5 from the current account. A Sonnet 5.5 request using reasoning_effort high returned HTTP 200 and OK with mode=extended;effort=high. Acceptance does not establish an exact reasoning budget or expose private thinking.
- Settings are tabbed and the URL has a copy action. The running app recorded URL-copy actions. Full visual checks and clipboard-content inspection were not completed; no claim is made about small-screen layout validation.


## Sign-in popup regression — 0.4.1

- `dotnet run --project tests/Browser/Browser.Tests.csproj -c Release` passes against the installed WebView2 runtime using an isolated temporary profile. It opens a real child WebView, checks `window.opener.postMessage`, verifies `window.close`, confirms the parent document was retained, and checks popup-host cleanup. It uses no Claude/Google account, credentials, external page, or cookie extraction.
- Main application builds with zero warnings/errors. All 48 API assertions pass, including thinking mappings and conflict handling. Earlier updater and JavaScript suites remain applicable.
- The user retried login in the installed v0.4.1 app and confirmed success. Local diagnostics verified the session and endpoint startup; the user's quick test returned OK from the live Claude web session. Installation preserved key hashes, port, model and authentication mode; the existing browser profile remained present and the Start-menu shortcut was created. A post-login picker hydration race was observed and model discovery now waits for the control to appear. Live thinking support remains unverified.

## In-app updater — 0.3.0

- 28 updater assertions cover stable-version selection, draft/preview exclusion, digest and size requirements, URL/redirect restrictions, cache reuse/replacement, truncated/corrupt downloads, cancellation, staging, settings preservation, ZIP traversal rejection, and rollback after failed startup confirmation.
- A separate self-contained Windows helper runs outside the replaceable application folder. Application files use an owned per-user directory; browser data and settings remain in the parent data folder.
- The 39 API integration assertions and JavaScript suites remain in the Windows build workflow. Release packaging includes the helper and a SHA-256 checksum file.
- Live download/installation results against the published release are recorded in its release notes after publication; offline assertions alone do not establish a real upgrade.

## Local 0.2.0 update — 26 October 2026

- Dark appearance is enabled by default and applied to both the WPF interface and WebView2's preferred color scheme. An optional light theme is available.
- The embedded Claude page itself displayed `Free plan`. The website picker exposed actual `data-model-id` values for Sonnet 5.5 and Haiku 5.5. Disabled/upgrade entries were excluded, including an Opus label whose DOM text joined `5.5Pro...Upgrade` without spaces. The live local `/v1/models` response returned exactly `claude-sonnet-5-5` and `claude-haiku-5-5`.
- The user's installed Pi transport was loaded from an isolated copy of its installed application code. Against the real local endpoint and signed-in Claude web session, it streamed an `echo` function call, supplied a synthetic string result, and received final text exactly `OK`. The test tool performed no filesystem or shell operations.
- The actual Pi request included `stream_options`, `store`, `max_completion_tokens`, `tools`, and `tool_choice`. The bridge accepted and translated it successfully.
- A direct live request to `claude-haiku-5-5` returned HTTP 200 and exactly `OK`, verifying explicit model routing.
- Local integration tests cover authenticated and no-key modes, preservation of Host/Origin restrictions without keys, actual model routing, compatibility-warning headers, buffered tool-call SSE, a follow-up tool-result turn, rejection of invented tools, native `x-api-key`, and native buffered SSE. JavaScript tests cover picker extraction, disabled/upgrade rows, explicit model IDs, framing, transport errors, and repeated injection.

The user approved publishing these changes together with the updater. Full coding workloads, every Pi/OpenCode tool schema, strict schema enforcement, real-time token streaming, and remaining-quota accounting remain unverified.

## Live Windows test — 26 October 2026

- The user signed in directly to claude.ai in the app-owned WebView2 profile.
- Authenticated workspace discovery succeeded. The user selected the workspace.
- An initial generation request using the hand-entered `claude-sonnet-5.5` identifier was rejected. Improved diagnostics identified HTTP 403, `permission_error`, categorized as a model restriction.
- The identifier was corrected to `claude-sonnet-5-5`, matching Anthropic's documented model ID and the website's Sonnet 5.5 model label.
- The app was restarted, retaining its browser session and selected workspace. One explicit `--quick-test` run called the local `/v1/messages` endpoint, which forwarded `Reply with exactly OK.` through the Claude website.
- The generated reply was exactly `OK`. The app recorded `test_passed`.

This confirms signed-in web-session discovery and one local-endpoint-to-Claude generation on Windows. The user supplied the account for a free-account test. Account plan identity, allowance attribution, quota reset behavior, prolonged sessions, and sustained workloads were not independently verified. No unlimited-usage claim is made.

The user also opened Claude in the Codex browser. Its visible page explicitly displayed `Free plan` and `Sonnet 5.5`. This is a separate browser profile; account identity was not programmatically correlated with the app session.

## Automated verification

- 26 local HTTP integration assertions against a synthetic backend: authentication, wrong/revoked keys, persisted hashes and workspace preferences, Host/Origin restrictions, text response formatting, buffered SSE termination, malformed/unsupported requests, body size, concurrency, and the OpenAI toggle.
- JavaScript stream tests: all split positions, CRLF, Unicode, legacy text events, error/malformed/truncated responses, size bounds, and repeatable page injection.
- Browser-transport tests with synthetic fetch: organization discovery, payload handling, same-origin credentials, no cookie extraction, origin validation, 401, redacted 403 classifications, verification HTML, and incomplete generation.
- Release-mode Windows build with zero warnings or errors. The initial GitHub Windows CI run also passed; subsequent commits have their own checks.

OpenAI-format live generation, full client compatibility, ARM64 hardware, Windows 10, and an installer lifecycle have not been tested. The release is an experimental x64 portable application, not an installer.
