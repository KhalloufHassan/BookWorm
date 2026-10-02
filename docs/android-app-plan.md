# BookWorm Android app: plan

A plan for BookWorm's Android app, written to be carried out in a separate session. Read it
all before starting; the "Decisions" section is settled and shouldn't be reopened.

## Status (2026-10-01)

Built, waiting to be built on a phone and tested. Where it differs from the plan below:

- **Uploads and file picking stay in JavaScript.** The web view supports `<input type="file">`, so
  "Add book from file" works unchanged. Uploads go straight from JS to the server with the app's
  token (`IAppHost.GetApiAccessAsync`), allowed by a CORS rule for the web view's origin only
  (`Apps:Origins`). So there's no separate `IFileTransfer`.
- **The browser (passkey) sign-in asks for confirmation** on `/Account/MobileLogin` before sending the
  code to the app, so another app can't use a signed-in browser to sign in silently.
- **Access tokens last 30 minutes** (not an hour), so "sign out everywhere" reaches apps sooner.
- **The web view's file and cover requests are answered straight away:** from the phone if downloaded,
  otherwise streamed from the server (always 200; a failure is an empty body).
- **Not done yet:** volume keys turning pages, "Open with"/"Share" into the app, status bar colours,
  and downloading notes or backups from inside the app.
- **Unverified here:** the Android-only code (`Platforms/Android`, `#if ANDROID` blocks). This machine
  can't build Android apps. The rest of the app's code was compiled against MAUI's own assemblies.

## Decisions (settled with the owner)

| Topic | Decision |
|---|---|
| Stack | .NET MAUI Blazor Hybrid (`net10.0-android`), reusing `BookWorm.UI` pages and components. iOS later, so keep platform code behind interfaces. |
| Offline | **Only reading** downloaded books: reader, position, reading time, highlights and notes. Library browsing, editing and uploads need the server. |
| Local storage | **No database on the phone.** Downloaded books are folders of plain files (JSON + the book file + cover). Everything else comes from the server. |
| Downloads | Manual, per book ("Download for offline reading" / "Remove download"). |
| Distribution | Signed APK attached to **GitHub releases**. Google Play maybe later; don't do anything Play-specific now. |
| Server address | **HTTPS only**. No cleartext HTTP, no exceptions. |
| Minimum Android | MAUI's default minimum; don't raise it. Target the latest API level MAUI supports. |
| Sign-in | **Both**: an in-app username/password form (with two-factor code and recovery code steps), and "Sign in with a passkey" through the system browser. |

## Project rules (from the owner; they apply here too)

- Never assume anything about what to store, the stack or libraries beyond this plan. **Ask first.**
- **Don't commit** anything until the owner has tested and says so.
- Nullable reference types are **disabled** (`Directory.Build.props`): no `string?` or `!` on reference types.
- One class per file for entities. `.IsRequired()` on required string columns. After model changes, run `dotnet ef migrations has-pending-model-changes`.
- Match the existing code style: comment density, naming and idioms in `BookWorm.UI` and `BookWorm.Server`.
- The owner tests with Docker. Don't leave dev servers running.

## How it fits the current code

- `BookWorm.UI` holds every page, component and the reader JS. It has no server-only code.
- The web host `BookWorm.Client` plugs in host-specific services:
  - `CookieAuthenticationStateProvider` (calls `GET /api/me`)
  - `WebAccountService : IAccountService`
  - `LocalStoragePreferenceStore : IPreferenceStore`
  - `SessionExpiredHandler`
  - an `HttpClient` with the same-origin base address
- `IAccountService` already says: "the mobile app will use the system browser and tokens".
- `GET /api/server-info` (anonymous) returns name, version and API version, for compatibility checks.
- JS talks to the server directly in a few places, all with relative `api/...` URLs and the cookie:
  - the reader's file `fetch` (`reader-foliate.js`, `reader-pdf.js` with `withCredentials`)
  - the page-close beacon (`reader.js`, `PUT`)
  - cover images (`<img src="api/books/{id}/cover?v=…">`)
  - uploads (`files.js`, XHR `PUT` with progress)
  - `coverFromUrl` (`files.js`)
- The reader libraries load from jsDelivr (pinned) in `reader-pdf.js`, `reader-foliate.js` and `files.js`. That can't work offline.

## Solution structure

```
src/
  BookWorm.Contracts/        (existing) + auth contracts
  BookWorm.UI/               (existing) + a few host abstractions
  BookWorm.Client/           (existing web host)
  BookWorm.Server/           (existing) + token sign-in endpoints, small API changes
  BookWorm.Mobile.Core/      NEW  net10.0 class library: offline store, sync queue, offline HTTP handler,
                                  token handling. No MAUI references, so it's unit-testable.
  BookWorm.Mobile/           NEW  net10.0-android MAUI Blazor Hybrid app: host services, WebView
                                  request interception, Android specifics.
tests/
  BookWorm.Server.Tests/     (existing) + auth and API changes
  BookWorm.Mobile.Core.Tests/ NEW
```

Add the new projects to `BookWorm.slnx`. The Docker image must **not** build the mobile projects: check `Dockerfile` and `.dockerignore`, since the Android workload isn't in the SDK image.

## Part 1: Server changes

### 1.1 Token sign-in for apps

Use ASP.NET Core Identity's bearer tokens (`AddBearerToken(IdentityConstants.BearerScheme)`, the same mechanism `MapIdentityApi` uses). Tokens are opaque and data-protected, and the data-protection keys are already persisted in the database.

**Don't** call `MapIdentityApi`: it maps registration and other endpoints BookWorm doesn't allow, because accounts are created by an admin.

- **Authentication setup:** add the bearer scheme alongside the cookies. The API group (`/api`, `RequireAuthorization`) must accept **either** the cookie or a bearer token. Use a policy scheme or `AuthorizationPolicyBuilder.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme, IdentityConstants.BearerScheme)`. A bearer request must never be redirected to the login page; it gets a 401.
- **Lifetimes:** access token about 1 hour; refresh token 90 days. Put both in configuration.
- **New endpoints**, in a new `Api/AuthEndpoints.cs`, anonymous, tagged "Auth":
  - `POST /api/auth/login`
    - Body: `{ userName, password, twoFactorCode?, recoveryCode? }`.
    - Mirror `MapIdentityApi`'s login: set `signInManager.AuthenticationScheme = IdentityConstants.BearerScheme`, then `PasswordSignInAsync` (lockout on).
    - If 2FA is required and no code was given, return `401` with a problem type or detail the app can recognise as "two-factor code needed". Then use `TwoFactorAuthenticatorSignInAsync` or `TwoFactorRecoveryCodeSignInAsync`.
    - Success returns `AccessTokenResponse` (`accessToken`, `expiresIn`, `refreshToken`).
  - `POST /api/auth/refresh`
    - Body: `{ refreshToken }`.
    - Mirror `MapIdentityApi`'s refresh: unprotect with the bearer options' `RefreshTokenProtector`, check expiry, check the security stamp with `signInManager.ValidateSecurityStampAsync`, then issue new tokens.
  - `POST /api/auth/mobile-code`: exchanges a code from the passkey browser flow (1.2) for tokens.
- **Sign out everywhere:** `POST /api/account/sign-out-everywhere` (signed in) calls `UpdateSecurityStampAsync`. That makes every refresh token fail, and web cookies fail at their next validation. Add a button for it in the web account settings (Manage pages) and in the app's account menu.
- **Existing logout:** `POST /api/account/logout` stays as it is for cookies. For the app, signing out just forgets the tokens on the device.
- **Tests** in `BookWorm.Server.Tests`:
  - login OK, wrong password, lockout
  - 2FA code required, then accepted; recovery code
  - refresh OK; refresh after security stamp change fails
  - bearer token accepted on `/api/me`
  - bearer request without a token returns 401, not a redirect
  - cookie clients unaffected

### 1.2 Passkey sign-in through the system browser (PKCE)

Passkeys only work reliably in a real browser, so the app opens the server's login page in Chrome and gets a one-time code back.

1. The app makes a random `code_verifier` and sends `code_challenge = BASE64URL(SHA256(verifier))`. It opens `https://<server>/Account/MobileLogin?challenge=<code_challenge>` with MAUI `WebAuthenticator`, callback `bookworm://auth`.
2. **`/Account/MobileLogin`** is a new server-rendered page in `Components/Account/Pages`.
   - If the browser isn't signed in, it goes through the normal `/Account/Login` (password, 2FA or passkey) with `ReturnUrl` back to itself.
   - Once signed in (cookie), it issues a **code**: a data-protected payload (`IDataProtector` purpose `"BookWorm.MobileCode"`) holding user id, security stamp, `code_challenge` and expiry (2 minutes).
   - It then redirects to `bookworm://auth?code=<code>`.
   - The redirect target is fixed: never take it from the query string. Show a short "You can return to the app" page as a fallback.
3. The app posts `{ code, codeVerifier }` to `POST /api/auth/mobile-code`. The server:
   - unprotects the code
   - checks expiry
   - checks `SHA256(verifier)` matches the challenge
   - checks the security stamp still matches
   - signs in with the bearer scheme (returns `AccessTokenResponse`)

   No table is needed: PKCE means an intercepted code alone is useless.
4. **Tests:**
   - valid exchange
   - wrong verifier
   - expired code
   - tampered code
   - stamp changed between issue and exchange

### 1.3 IDs created on the phone (offline sync)

So that retries after a lost connection never create duplicates:

- **`CreateHighlightRequest`:** add an optional `Id` (Guid).
  - If it's given and a highlight with that id already exists for this user and book, return it (200) instead of creating another.
  - If it exists for a different book or user, return 409.
- **Reads started offline.** "Start reading" while offline creates the read on the phone.
  - Let `POST /api/books/{id}/reads/current` take an optional body `{ id, startedAt }`.
  - If a read is already in progress, return it (existing behaviour).
  - Otherwise create one with the given id and start time. `startedAt` is clamped to be no later than now.
- **`FinishReadRequest.FinishedAt`** already exists; the queue sends the offline time.
- **`ReadProgressRequest`** already carries `SessionId`/`SessionStartedAt`, and sessions are already idempotent by id.
  - Add an optional `SavedAt` (client time) so a stale queued position never overwrites a newer one.
  - The server ignores a save whose `SavedAt` is older than the read's `LastOpenedAt`. Clamp `SavedAt` to now.
- **Tests** for each of these.

### 1.4 Server info for the app

`GET /api/server-info` exists. Add `minimumAppVersion` (config, default `null`) so the app can say "update the app". Make sure the app checks `apiVersion`.

## Part 2: Changes in `BookWorm.UI` (shared)

Keep the web app behaving exactly as today. Every new abstraction gets a web implementation that does what the code does now.

1. **Reader library location.** Replace the hard-coded jsDelivr URLs with bare module names resolved by an **import map**: `pdfjs-dist/…` and `foliate-js/…`.
   - Web: map them to the same pinned jsDelivr URLs. Merge into Blazor's `<ImportMap />` in `Server/Components/App.razor` with an `ImportMapDefinition` (`ImportMapDefinition.Combine`), so there's one import map.
   - Mobile: map them to bundled local copies (Part 3.6).
   - In `reader-pdf.js`, derive `BASE` with `import.meta.resolve('pdfjs-dist/')`.
   - Verify the web app still loads all six formats afterwards.
2. **Host capabilities service** (`IHostCapabilities`), web implementation:
   - `SupportsOfflineDownloads = false`
   - `UsesPageCloseBeacon = true`
   - `CanReachServer` always true

   The reader only arms the beacon when `UsesPageCloseBeacon`. The mobile app instead saves on Android pause/stop (3.4).
3. **Offline downloads UI** (`IOfflineLibrary`; web implementation is a no-op and hides everything):
   - Book details page: "Download for offline reading" / "Remove download", with download progress and size. Only for books with a file.
   - A "Downloaded" nav item and page, listing downloaded books with cover, title, progress and an open button.
   - An offline banner in `MainLayout`. When offline, pages other than the reader and Downloaded show a friendly "You're offline" state instead of an error. Home and Books show a link to Downloaded.
4. **File picking and uploads.** `BrowserFiles` (JS picker + XHR) becomes an interface, `IFileTransfer`.
   - Keep the current class as the web implementation.
   - Its users are `BooksPage`, `BookEditPage`, `BookFilesSection`, `BookCoverMenu` and `BackupsPage`. Keep them working through the interface.
   - The mobile implementation is Part 3.3.
5. **Sign-in page for hosts that have no server login pages.** `IAccountService.RedirectToLogin()` in the app navigates to a new `BookWorm.UI` route, `/app-login`. It only appears in the app, and uses `MainLayout`-free styling like the account pages. It has:
   - server address (step 1, only if none is set)
   - user name
   - password
   - "Sign in"
   - the two-factor code step (with "use a recovery code")
   - "Sign in with a passkey" (starts 1.2)
   - clear errors: wrong password, locked out, server unreachable, not HTTPS, incompatible version

   Keep the page host-agnostic by calling an `IAppSignIn` interface that the mobile host implements.

## Part 3: `BookWorm.Mobile` (MAUI app)

### 3.1 Project

- MAUI Blazor Hybrid, `net10.0-android`.
  - Application id: `com.khallouflabs.bookworm`. **Confirm with the owner before creating the project.**
  - Display name: "BookWorm". Icon from `src/BookWorm.Server/wwwroot/favicon.svg`, via MAUI `MauiIcon` with the SVG and a cream background.
- The `BlazorWebView` hosts `BookWorm.UI`'s `Routes` and the same CSS (`_content/BookWorm.UI/css/bookworm.css`), plus MudBlazor.
- `MauiProgram` registers the same UI services as `BookWorm.Client/Program.cs`, but with mobile implementations of:
  - `IPreferenceStore` (MAUI `Preferences`)
  - `IAccountService`, `IAppSignIn`
  - `AuthenticationStateProvider` (token-based, still calls `/api/me`)
  - `IFileTransfer`, `IOfflineLibrary`, `IHostCapabilities`
- **HTTPS only:** `android:usesCleartextTraffic="false"` and a network security config with no cleartext. The server address must start with `https://`.
- **Server address:** stored in `Preferences`. Validated on entry by calling `/api/server-info` (name, API version, `minimumAppVersion`). Changing it signs out.

### 3.2 Tokens and the API `HttpClient`

- **`TokenStore`:** MAUI `SecureStorage` for the access token, refresh token and expiry.
- **`BearerTokenHandler`** (a `DelegatingHandler`, in `Mobile.Core` with an `ITokenStorage` abstraction):
  - Adds `Authorization: Bearer`.
  - Refreshes shortly before expiry, and once on a 401, with a single-flight lock so parallel requests refresh once.
  - If refresh fails, clears the tokens and triggers `RedirectToLogin`.
- **Pipeline:** `BookWormApiClient`'s `HttpClient` has `BaseAddress` = server address. Handler order: offline handler (3.5) → bearer handler → `SocketsHttpHandler`.
- **Passkey flow:** `WebAuthenticator.AuthenticateAsync` with the `bookworm://auth` callback. Needs a `WebAuthenticatorCallbackActivity` subclass with the intent filter for scheme `bookworm`, host `auth`.

### 3.3 WebView requests: files, covers, picked files

JS and `<img>` use relative `api/...` URLs. In the app these hit the WebView's app origin, which has no cookie and isn't the server. Intercept them in .NET:

- Use BlazorWebView's web request interception (`WebResourceRequested`, new in .NET 10 MAUI). **Check the exact API first**; if it isn't available, fall back to the Android `WebViewClient.ShouldInterceptRequest` through a BlazorWebView handler customisation.
- **`GET api/books/{id}/files/{fileId}` and `GET api/books/{id}/cover…`:**
  - If the book is downloaded, serve the local file.
  - Otherwise proxy to the server with the bearer token and stream the response back.
  - Support `Range` requests for PDF.js: pass `Range` through when proxying, and answer 206 from local files.
- **Picked files:** the native picker's file is exposed at `api/_picked/{key}` for `files.js`'s `coverFromUrl`-style metadata and cover reading, so "Add book from file" can pre-fill the form.
- **Bodies can't be intercepted.** Android's interception can't see request bodies, so **`PUT`/`POST` from JS can't be proxied**. That's why uploads (below) and the beacon (3.4) are handled in .NET, not JS.
- **Uploads** (`IFileTransfer` mobile implementation):
  - Pick with MAUI `FilePicker`.
  - Upload from .NET with `HttpClient`, streaming a `StreamContent` with a progress-reporting wrapper.
  - The same endpoints and response handling as `BrowserFiles.UploadAsync`, including a 413 message.
  - Covers: take the picked image, scale it down in .NET or through JS on the `api/_picked` URL, then upload.

### 3.4 Android behaviour

- **Back button:** navigate back in the Blazor app. On the start page, leave the app.
- **Saving on pause:** on `OnPause`/`OnStop`, ask the open reader page to save its position (replaces the JS beacon), via an event that `ReaderPage` subscribes to through `IHostCapabilities` or a small `IAppLifecycle` service.
- **Reader:** immersive full screen (hide status and navigation bars) while in `ReaderLayout`. Keep the screen on while reading. Make both reader settings, stored with the other reader settings.
- **Volume keys** turn pages (reader setting, off by default).
- **Status bar colour** follows the app theme, or the reader theme in the reader.
- **"Open with" / "Share":** intent filters for EPUB, PDF, MOBI, AZW3, FB2 and CBZ files open "Add book from file" pre-filled.

### 3.5 Offline reading (no database)

**Storage layout** (`FileSystem.AppDataDirectory/offline/{userId}/{bookId}/`):

```
book.json          BookDetails snapshot at download time (refreshed when online)
file.<ext>         the downloaded book file (one file: the one the reader would open, or the one picked)
file.json          BookFileDetails of that file (id, format, sha256…)
cover.jpg          cover, if any
highlights.json    List<HighlightDetails>, kept in sync
read.json          the current ReadDetails, or null (browsing position lives in file.json's BrowseLocation)
```

The sync queue lives in `offline/{userId}/queue/`, one JSON file per change, named by a sortable timestamp + GUID.

**`Mobile.Core` components (unit-tested):**

- **`OfflineStore`:** read and write the folders above, with atomic writes (write a temp file, then rename). Lists downloaded books and their sizes.
- **`OfflineQueue`:** append, read in order, remove on success, plus retry/backoff state. Merges redundant entries: only the latest progress save per read is kept. Sessions are kept per session id, latest wins.
- **`OfflineHttpHandler`** (`DelegatingHandler`): the key to keeping `BookWorm.UI` pages unchanged.
  - **Online:** passes requests through. After successful reader-related calls for downloaded books, it updates the local snapshots (`highlights.json`, `read.json`, `book.json`).
  - **Offline (or on network failure) for a downloaded book, it handles exactly these, the reader's calls:**
    - `GET /api/books/{id}` → `book.json`
    - `GET …/highlights` → `highlights.json`
    - `GET …/reads/current` → `read.json` or 204
    - `PUT …/reads/{readId}/progress` → queue + update `read.json` → 204
    - `PUT …/files/{fileId}/position` → queue + update `file.json` → 204
    - `POST …/reads/current` → create the read locally with a new GUID and `startedAt` = now, queue it (1.3) → 201
    - `POST …/reads/{readId}/finish` → queue + update → 200
    - `POST/PUT/DELETE …/highlights…` → queue + update `highlights.json`. New ids are made on the phone (1.3), and the response is built from the request.
    - `POST …/highlights/{id}/reanchor` → queue + update
  - Anything else while offline → a "you're offline" failure that the UI shows nicely (Part 2.3).
- **`SyncService`:**
  - Runs when connectivity returns (MAUI `Connectivity`), on app start and on resume.
  - Sends the queue in order.
  - On 409 or 404 for a highlight, keeps the server's state and drops the entry. Log it, and tell the user once if something couldn't be synced.
  - Then refreshes each downloaded book's snapshots.

**Downloads:**
- **Download:** fetch `book.json`, the file (streamed to disk, with progress, checking the SHA-256 against `file.json`), the cover, highlights and the current read.
- **Remove download:** deletes the folder. Refuse, or ask, while it still has queued changes.
- **File replaced on the server:** if the SHA-256 changed, mark the download "update available" and offer to re-download. Highlights re-anchor as they do today.

### 3.6 Bundled reader libraries

- Ship pdfjs-dist 6.3.289 and foliate-js at commit `78914aef4466eb960965702401634c2cb348e9b1` inside the app. Use the same files that were in `src/BookWorm.UI/wwwroot/lib` before the CDN switch; jsDelivr serves identical bytes.
- Put them in `BookWorm.Mobile/wwwroot/lib/…`, fetched at build time by a script or MSBuild target from jsDelivr with pinned versions, and **git-ignored**. Ask the owner if unsure whether to commit them instead.
- The app's `wwwroot/index.html` import map points `pdfjs-dist/` and `foliate-js/` at them (Part 2.1).
- Update `THIRD-PARTY-NOTICES.md`: in the app these are bundled, so include their licenses in the app, e.g. an "Open-source licenses" page in the app's about screen.

### 3.7 Account menu in the app

- "Account settings" (password, 2FA, passkeys) opens the server's `/Account/Manage` in the system browser (`Browser.OpenAsync`). Those pages already exist.
- "Sign out" forgets the tokens.
- "Sign out everywhere" calls 1.1.
- "Change server" signs out.
- About: app version, server version, licenses, "Check for updates" (3.8).

### 3.8 Release through GitHub

- **Versioning:** `ApplicationDisplayVersion` from `Directory.Build.props`'s `<Version>`. `ApplicationVersion` (the integer) is the CI run number or a value derived from the version; it must always increase.
- **Signing:** a release keystore, created once by the owner and **kept out of git**. The CI secrets hold the keystore (base64), alias and passwords. Document how to create it with `keytool`.
- **GitHub Actions workflow** `.github/workflows/android-release.yml`, on tags `android-v*`:
  - set up .NET 10 + the `maui-android` workload + Java 17/21
  - `dotnet publish -f net10.0-android -c Release` producing a signed APK
  - create a GitHub release with the APK attached
- **Prerequisites:** there's no git remote yet. The owner must create the GitHub repository and push first. Nothing is committed yet (see the rules). Whether the repo is public decides whether update checks need a token; ask.
- **Update check:** the app compares its version with the latest GitHub release (`/repos/{owner}/{repo}/releases/latest`). If newer, it offers to open the release page. No in-app APK installer, which avoids the install-packages permission; Obtainium users get updates automatically.
- **README:** a new "Android app" section covering install from GitHub releases, first launch (server address, sign in), offline downloads, and building locally.

## Part 4: Development setup (Fedora)

- `dotnet workload install maui-android`, a JDK 17 or 21, and the Android SDK (command-line tools; `dotnet build -t:InstallAndroidDependencies` can install it). Document the exact steps that worked in the README's development section.
- Test on an emulator and on the owner's phone (`adb`).
- The app needs an HTTPS server:
  - Real server: use the TrueNAS deployment (`https://bookworm.khallouflabs.org`, behind the reverse proxy).
  - Local dev: something with a trusted certificate, e.g. a tunnel, or a dev certificate installed on the emulator. **Never** relax HTTPS in the app to make dev easier.

## Part 5: Testing checklist

**Automated:**
- Server tests for everything in Part 1.
- `BookWorm.Mobile.Core.Tests`:
  - `OfflineStore` atomic writes
  - `OfflineQueue` ordering and merging
  - `OfflineHttpHandler` offline responses for each handled endpoint, matching the server's JSON shapes (use the `BookWorm.Contracts` types)
  - `SyncService` retry, 404/409 handling
  - `BearerTokenHandler` refresh single-flight

**Manual, on a phone:**
- first launch (server address → sign in)
- password sign-in; 2FA code; recovery code; passkey sign-in through the browser
- token refresh after an hour (shorten the lifetime in config to test)
- browse and edit the library
- add a book from a file on the phone
- open-with from a file manager
- read every format online; download a book
- airplane mode:
  - read, highlight, add a note, start and finish a read
  - app killed and reopened offline
- back online: everything synced, dashboard reading time updated
- change the book's file on the server → "update available"
- remove the download
- sign out everywhere (web session and app both end)
- web app still works exactly as before, all six formats, with the import map change

## Out of scope (for now)

- iOS: keep code behind interfaces and put no Android APIs in `Mobile.Core`.
- Google Play (store listing, AAB, Play signing).
- Offline library browsing or editing, offline uploads.
- Automatic downloads.
