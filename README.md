# BookWorm

A self-hosted book reader and library manager. Keep track of what you want to read, what you're
reading and what you thought of it, and read your books right in the app.

Every person on a BookWorm server has their own private library. An administrator creates the accounts.

- **Library:** books, authors, tags, reads, ratings and Markdown notes, in card or table views with
  search and filters. Add a book by uploading its file: title, authors and cover are read from it.
- **Reader:** EPUB, MOBI, AZW3, FB2 and CBZ (with [foliate-js](https://github.com/johnfactotum/foliate-js))
  and PDF (with [PDF.js](https://mozilla.github.io/pdf.js/)): pages or scrolling, zoom, search,
  table of contents, page numbers, and light, sepia or dark pages. It remembers where you left off.
- **Highlights and notes** on the text, stored in the database (your files are never changed), and
  exported as Markdown files you can open in any notes app.
- **Home dashboard:** continue reading, books finished, reading time, streaks, ratings.
- **Backups** of everything on a schedule, restorable from the app or the command line.
- **Android app** with the same pages, and books downloaded for reading offline. See [Android app](#android-app).

> **Status:** early development. Everything above works; the Android app is new and still being
> tested. See [the roadmap](#roadmap).

## Run it with Docker

You need Docker with the Compose plugin.

```sh
git clone <this repository> bookworm && cd bookworm
cp .env.example .env        # then set POSTGRES_PASSWORD, e.g. to the output of: openssl rand -hex 24
docker compose up -d --build
```

Open <http://localhost:8080>. The first visit asks you to create the administrator account, or to
restore a backup from another server.

- **Data** lives in three Docker volumes: `db-data` (the PostgreSQL 18 database), `app-data` (book
  files, covers, exported notes) and `backups`.
- **Upgrading** is `git pull && docker compose up -d --build`. The database schema updates itself on start.
- **Port**: set `BOOKWORM_PORT` in `.env` to use something other than 8080.
- **Upload size**: book files can be up to 500 MB. Change it with `Storage__MaxUploadMegabytes` in
  `compose.yaml`.

### Automatic updates

Every push to `main` runs the tests and, when they pass, publishes the image as
`ghcr.io/khalloufhassan/bookworm:latest` (and `:sha-<commit>`, to go back to an earlier version).
To have a server update itself, run that image instead of building it, and add
[Watchtower](https://github.com/nicholas-fedor/watchtower), which checks for a new image every few
minutes and restarts BookWorm with it. In `compose.yaml` (or a TrueNAS custom app's YAML):

```yaml
services:
  app:
    image: ghcr.io/khalloufhassan/bookworm:latest   # instead of image: bookworm:local and build: .
    labels:
      com.centurylinklabs.watchtower.enable: "true"
    # …the rest as before

  watchtower:
    image: nickfedor/watchtower:latest
    restart: unless-stopped
    environment:
      WATCHTOWER_LABEL_ENABLE: "true"     # only update containers with the label above
      WATCHTOWER_POLL_INTERVAL: "300"     # seconds
      WATCHTOWER_CLEANUP: "true"          # delete the old images
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock
```

The database stays on its pinned `postgres:18` image. To go back to an earlier version, replace
`latest` with one of the `sha-…` tags listed on the repository's Packages page.

### Your notes as Markdown

BookWorm keeps a Markdown copy of each book's notes and highlights in `/data/notes/<username>/`
(one file per book, rewritten a few seconds after every change). To open them in a notes app such
as Obsidian, map that folder to a folder on your computer in `compose.yaml`:

```yaml
    volumes:
      - app-data:/data
      - ./notes:/data/notes   # create it first, owned by the app: mkdir notes && sudo chown 1654 notes
```

Treat the folder as read-only: BookWorm overwrites its own files, and leaves any other files you
put there alone. Every book page and the Books page also have download buttons for the Markdown.

### Backups

Administrators find backups under **Administration → Backups**: a schedule (daily or weekly, at a
time you choose; daily at 03:00 UTC to start with), how many to keep, "Back up now", downloads and
restores. A backup is one `.tar.gz` file with the database and all book files and covers, in the
`backups` volume.

Keep copies somewhere else as well, since a backup on the same disk doesn't help if the disk
fails. The simplest way is to map the backups to a folder that is synced or on a NAS:

```yaml
    volumes:
      - /mnt/nas/bookworm-backups:/backups   # must be writable by the app: sudo chown 1654 <folder>
```

**Restoring** replaces all data on the server with the backup. BookWorm restarts to do it (Docker
Compose starts it again by itself), and everyone signs in again. Either:

- in the app: Backups page → the restore button next to a backup (or upload one first), or
- on a new server: the first-run setup page offers to restore a backup instead of creating an
  administrator, or
- from the command line:

  ```sh
  docker compose stop app
  docker compose run --rm app restore bookworm-backup-20260929-030000.tar.gz   # a file in the backups volume
  docker compose start app
  ```

`docker compose run --rm app backup` makes a backup from the command line, e.g. before an upgrade.
Backups from an older version of BookWorm restore fine; backups from a newer version are refused
until you upgrade.

### Behind a reverse proxy (HTTPS)

To reach BookWorm from outside your home network, put it behind a reverse proxy that handles HTTPS,
such as Caddy, Traefik or nginx:

1. Uncomment `ASPNETCORE_FORWARDEDHEADERS_ENABLED` in `compose.yaml`, so BookWorm knows the
   original request was HTTPS. It then marks its cookies secure and sends an HSTS header.
2. Make the proxy pass on the original `Host` header and set `X-Forwarded-Proto`. Caddy does both
   by default. With nginx, add `proxy_set_header Host $host;` and
   `proxy_set_header X-Forwarded-Proto $scheme;`.
3. Let large uploads through: nginx accepts only 1 MB by default, so add
   `client_max_body_size 500m;` (Caddy and Traefik have no limit by default).

A complete Caddyfile, with automatic HTTPS certificates:

```
books.example.com {
    reverse_proxy localhost:8080
}
```

**Passkeys** need HTTPS (browsers only allow them on `localhost` without it). A passkey belongs to
the address you created it on, taken from the `Host` header. So create passkeys through the address
you normally use, and don't change that address later, or the passkeys stop working. Passwords and
authenticator codes keep working either way.

### Security notes

- There is no self-registration. The administrator creates accounts and resets forgotten passwords;
  no mail server is needed.
- Everyone can turn on two-factor authentication with an authenticator app (Account settings →
  Two-factor authentication). There's no QR code: the page has an "Open in authenticator app" link
  for phones, and a key to type or paste on other devices. If someone loses their authenticator
  and their recovery codes, the administrator can turn it off for them on the Users page.
- Passkeys (Account settings → Passkeys) can replace the password at sign-in. See
  [the HTTPS notes](#behind-a-reverse-proxy-https).
- After five wrong passwords, an account is locked for five minutes.
- Changing a password, and the administrator resetting a password or two-factor authentication,
  signs that user out of their other sessions within a minute.
- The Android app signs in with a password (and two-factor code) in the app, or in the phone's
  browser, where passkeys work. It keeps a refresh token in Android's secure storage and only talks
  to servers over HTTPS. **Log out everywhere** (in the account menu, and Account settings → Sign
  out everywhere) signs out every browser and app; an app may keep working for up to 30 minutes,
  until its current access token expires. `Apps__AccessTokenMinutes` and `Apps__RefreshTokenDays`
  change those lifetimes (default 30 minutes and 90 days).
- Every response carries headers that stop other sites from framing BookWorm, stop browsers from
  guessing content types, and keep page addresses from leaking to other sites.
- The keys that encrypt login cookies are stored in the database, so restarts don't sign anyone out.
  They are stored unencrypted, like the rest of the database, so protect the database volume and
  its backups. (ASP.NET Core's warning about this is silenced in `appsettings.json`.)
- Book files are only served to their owner. Scripts inside books are disabled before a book is
  shown, and files are served with headers that stop browsers from running them as web pages.
- Backups contain everyone's data, including password hashes: store them as carefully as the server.

## Android app

The app shows the same pages as the web app, and adds reading offline: on a book's page, **Download
for offline reading**. Downloaded books (under **Downloaded** in the menu) open without a connection,
and the reading position, reading time, highlights and notes you make offline are sent to the server
when the phone is back online. Everything else needs the server.

### Install

Download the latest `BookWorm-x.y.z.apk` from the repository's
[releases](https://github.com/KhalloufHassan/BookWorm/releases) on the phone and open it (Android asks
you to allow installing apps from the browser or file manager the first time). **About** in the app
checks for newer versions; with [Obtainium](https://github.com/ImranR98/Obtainium) and this
repository's address, updates install themselves.

When the app first opens, enter your server's address (it must be HTTPS, as in
[Behind a reverse proxy](#behind-a-reverse-proxy-https)) and sign in. **Sign in with a passkey**
opens your server's login page in the browser; confirm there to come back to the app signed in.

### Build and run it yourself

You need the .NET SDK (from [Microsoft](https://dotnet.microsoft.com/download), not a Linux
distribution's package, which can't install workloads), a JDK 17 or 21 and the Android SDK:

```sh
dotnet workload install maui-android
# The Android SDK and JDK, if you don't have them (installs into the default locations):
dotnet build src/BookWorm.Mobile -t:InstallAndroidDependencies -p:AndroidSdkDirectory=$HOME/Android/Sdk -p:AcceptAndroidSdkLicenses=true

# With the phone connected over USB (developer options → USB debugging), or `adb pair`/`adb connect` over Wi-Fi:
dotnet build src/BookWorm.Mobile -t:Run -p:AndroidSdkDirectory=$HOME/Android/Sdk
```

The first build downloads the reader's libraries (PDF.js and foliate-js, the versions in
`Directory.Build.props`) into `src/BookWorm.Mobile/wwwroot/lib`, so books open offline.
`BookWorm.slnx` leaves the app out, so the server builds and tests without the Android tools; add
`src/BookWorm.Mobile/BookWorm.Mobile.csproj` to your IDE's solution to work on it.

The app needs a server with a certificate Android trusts (such as Let's Encrypt, as a reverse proxy
provides): it never uses plain HTTP.

### Releases

Pushing a tag like `android-v1.2.0` builds a signed APK and publishes it as a GitHub release
(`.github/workflows/android-release.yml`). Android only installs updates signed with the same key, so
make the key once and keep it safe (a password manager is a good place):

```sh
keytool -genkeypair -v -keystore bookworm.keystore -alias bookworm -keyalg RSA -keysize 4096 -validity 10000
base64 -w0 bookworm.keystore    # the value of the ANDROID_KEYSTORE_BASE64 secret
```

Then add the repository secrets `ANDROID_KEYSTORE_BASE64`, `ANDROID_KEYSTORE_PASSWORD`,
`ANDROID_KEY_ALIAS` (`bookworm` above) and `ANDROID_KEY_PASSWORD` (the same as the keystore's
unless you chose another), and release with:

```sh
git tag android-v1.0.0 && git push origin android-v1.0.0
```

A server can require a newer app with `Apps__MinimumVersion` (e.g. `1.2.0`); older apps then ask to
be updated.

## Develop

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and Docker.

```sh
docker compose -f compose.dev.yaml up -d   # PostgreSQL on localhost:5433
dotnet run --project src/BookWorm.Server   # http://localhost:5080
```

The development server keeps book files, covers, notes and backups in `.data/` (not committed), and
runs PostgreSQL's backup tools inside the development database container, so you don't need them
installed. A restore started from the app stops the server; start it again and the restore runs.

| Task | Command |
|---|---|
| Run the tests (the server's use Docker for a throwaway PostgreSQL) | `dotnet test` |
| Add a database migration | `dotnet tool restore` then `dotnet ef migrations add <Name> --project src/BookWorm.Server --output-dir Data/Migrations` |
| Browse and try the API | <http://localhost:5080/scalar> (sign in to the app first) |

### Layout

```
src/BookWorm.Server       ASP.NET Core host: the /api endpoints, login and account pages, database
src/BookWorm.Client       WebAssembly entry point of the web app (browser-specific services)
src/BookWorm.UI           All app pages and components; shared by the web app and the Android app
src/BookWorm.Contracts    API request/response types, shared by the server and every client
src/BookWorm.Mobile       The Android app (.NET MAUI Blazor Hybrid): BookWorm.UI in a native web view
src/BookWorm.Mobile.Core  The app's sign-in, offline reading and syncing, without MAUI (so it's testable)
tests/BookWorm.Server.Tests       Integration tests against a real PostgreSQL
tests/BookWorm.Mobile.Core.Tests  Tests of the app's offline reading, syncing and sign-in
```

The web app talks to the server only through the HTTP API. The UI library has no server-only code,
and what differs between the web app and the Android app (sign-in, storage, offline downloads) is
behind interfaces in `BookWorm.UI/Services`, so the Android app reuses every page as-is. In the
app, the reader's API calls for downloaded books are answered from the phone while offline, and
queued changes are sent later (see `BookWorm.Mobile.Core/Offline`). It uses [MudBlazor](https://mudblazor.com)
for components and [Markdig](https://github.com/xoofx/markdig) to show Markdown notes (raw HTML in
notes is not rendered). Sign-in, setup and account settings are server-rendered pages, so they
work before the web app has loaded.

The reader lives in `src/BookWorm.UI/wwwroot/js` (`reader.js` and its two engines). foliate-js and
PDF.js are imported by bare name (`pdfjs-dist/…`, `foliate-js/…`) and each host's import map says
where from: the web app loads them from jsDelivr, so reading and "Add book from file" need internet
access in the browser; the Android app bundles them. Their versions are set once, in
`Directory.Build.props`; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for licenses.

Highlights store the highlighted text with a little context around it. When you replace a book
file with a new version, the reader finds each highlight's text again in the new file; any it can't
find are kept, with their notes, and marked as not found.

## Roadmap

1. ~~Foundation: accounts, database, library API~~
2. ~~Web interface: a cozy design, and Books and Authors pages with table and card views, search and filters~~
3. ~~Security: two-factor authentication, passkeys, account settings~~
4. ~~Files and reader: upload books (EPUB, PDF, MOBI, AZW3, FB2, CBZ), covers, the reader, "continue reading"~~
5. ~~Backups: scheduled backups of the database, books and covers~~
6. ~~Highlights and notes~~
7. ~~Reading statistics dashboard~~
8. Android app with offline reading (built; being tested)

Later: an iOS app, and the Android app on Google Play.

## License

[MIT](LICENSE)
