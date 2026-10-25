# Verification record

## Live Windows test — 26 October 2026

- The user signed in directly to claude.ai in the app-owned WebView2 profile.
- Authenticated workspace discovery succeeded. The user selected the workspace.
- An initial generation request using the hand-entered `claude-sonnet-5.5` identifier was rejected. Improved diagnostics identified HTTP 403, `permission_error`, categorized as a model restriction.
- The identifier was corrected to `claude-sonnet-5-5`, matching Anthropic's documented model ID and the website's Sonnet 5.5 model label.
- The app was restarted, retaining its browser session and selected workspace. One explicit `--quick-test` run called the local `/v1/messages` endpoint, which forwarded `Reply with exactly OK.` through the Claude website.
- The generated reply was exactly `OK`. The app recorded `test_passed`.

This confirms signed-in web-session discovery and one local-endpoint-to-Claude generation on Windows. The user supplied the account for a free-account test. Account plan identity, allowance attribution, quota reset behavior, prolonged sessions, and sustained workloads were not independently verified. No unlimited-usage claim is made.

## Automated verification

- 26 local HTTP integration assertions against a synthetic backend: authentication, wrong/revoked keys, persisted hashes and workspace preferences, Host/Origin restrictions, text response formatting, buffered SSE termination, malformed/unsupported requests, body size, concurrency, and the OpenAI toggle.
- JavaScript stream tests: all split positions, CRLF, Unicode, legacy text events, error/malformed/truncated responses, size bounds, and repeatable page injection.
- Browser-transport tests with synthetic fetch: organization discovery, payload handling, same-origin credentials, no cookie extraction, origin validation, 401, redacted 403 classifications, verification HTML, and incomplete generation.
- Release-mode Windows build with zero warnings or errors. The initial GitHub Windows CI run also passed; subsequent commits have their own checks.

OpenAI-format live generation, full client compatibility, ARM64 hardware, Windows 10, and an installer lifecycle have not been tested. The release is an experimental x64 portable application, not an installer.
