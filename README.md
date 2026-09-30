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

> **Status:** early development; everything above works. Android and iOS apps come later.
> See [the roadmap](#roadmap).

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
- Every response carries headers that stop other sites from framing BookWorm, stop browsers from
  guessing content types, and keep page addresses from leaking to other sites.
- The keys that encrypt login cookies are stored in the database, so restarts don't sign anyone out.
  They are stored unencrypted, like the rest of the database, so protect the database volume and
  its backups. (ASP.NET Core's warning about this is silenced in `appsettings.json`.)
- Book files are only served to their owner. Scripts inside books are disabled before a book is
  shown, and files are served with headers that stop browsers from running them as web pages.
- Backups contain everyone's data, including password hashes: store them as carefully as the server.

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
| Run the tests (uses Docker for a throwaway PostgreSQL) | `dotnet test` |
| Add a database migration | `dotnet tool restore` then `dotnet ef migrations add <Name> --project src/BookWorm.Server --output-dir Data/Migrations` |
| Browse and try the API | <http://localhost:5080/scalar> (sign in to the app first) |

### Layout

```
src/BookWorm.Server       ASP.NET Core host: the /api endpoints, login and account pages, database
src/BookWorm.Client       WebAssembly entry point of the web app (browser-specific services)
src/BookWorm.UI           All app pages and components; shared with the future mobile app
src/BookWorm.Contracts    API request/response types, shared by the server and every client
tests/BookWorm.Server.Tests  Integration tests against a real PostgreSQL
```

The web app talks to the server only through the HTTP API. The UI library has no server-only code,
so the planned .NET MAUI Blazor Hybrid app can reuse it as-is. It uses [MudBlazor](https://mudblazor.com)
for components and [Markdig](https://github.com/xoofx/markdig) to show Markdown notes (raw HTML in
notes is not rendered). Sign-in, setup and account settings are server-rendered pages, so they
work before the web app has loaded.

The reader lives in `src/BookWorm.UI/wwwroot/js` (`reader.js` and its two engines). foliate-js and
PDF.js are loaded by the browser from jsDelivr, pinned to exact versions, so reading and "Add book
from file" need internet access. To update one, change its version in the imports of
`reader-pdf.js`, `reader-foliate.js` and `files.js`; see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for versions and licenses.

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

Later: Android and iOS apps with offline reading.

## License

[MIT](LICENSE)
