# Third-party software

BookWorm is MIT licensed (see [LICENSE](LICENSE)). It includes or uses the following software.

## Loaded by the browser from jsDelivr

The reader loads these from [jsDelivr](https://www.jsdelivr.com), pinned to exact versions, so
reading needs internet access. The versions are set in `src/BookWorm.UI/wwwroot/js/reader-pdf.js`,
`reader-foliate.js` and `files.js`.

| Library | Used for | Version | License |
|---|---|---|---|
| [foliate-js](https://github.com/johnfactotum/foliate-js) | Reading EPUB, MOBI, AZW3, FB2 and CBZ | commit `78914aef4466eb960965702401634c2cb348e9b1` (it has no releases) | MIT |
| [zip.js](https://github.com/gildas-lormeau/zip.js) (bundled with foliate-js) | Opening EPUB and CBZ archives | as bundled | BSD-3-Clause |
| [fflate](https://github.com/101arrowz/fflate) (bundled with foliate-js) | Decompressing MOBI text | as bundled | MIT |
| [PDF.js](https://github.com/mozilla/pdf.js) (`pdfjs-dist` package) | Reading PDFs | 6.3.289 | Apache-2.0 |

## NuGet packages

| Package | License |
|---|---|
| ASP.NET Core, Entity Framework Core, Microsoft.Extensions.* | MIT |
| [Npgsql](https://www.npgsql.org) and its EF Core provider | PostgreSQL License |
| [EFCore.NamingConventions](https://github.com/efcore/EFCore.NamingConventions) | Apache-2.0 |
| [MudBlazor](https://mudblazor.com) | MIT |
| [Markdig](https://github.com/xoofx/markdig) | BSD-2-Clause |
| [Scalar.AspNetCore](https://github.com/scalar/scalar) | MIT |

Tests also use xUnit (Apache-2.0) and Testcontainers (MIT).

## In the Docker image

The image is based on Microsoft's `mcr.microsoft.com/dotnet/aspnet` image and adds PostgreSQL's
client tools (`postgresql-client-18`, PostgreSQL License) from apt.postgresql.org for backups.
