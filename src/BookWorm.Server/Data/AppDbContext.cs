using BookWorm.Contracts;
using BookWorm.Server.Auth;
using BookWorm.Server.Notes;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BookWorm.Server.Data;

public sealed class AppDbContext(
    DbContextOptions<AppDbContext> options,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    INotesExportQueue notesExport)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options), IDataProtectionKeyContext
{
    public DbSet<Book> Books => Set<Book>();
    public DbSet<Author> Authors => Set<Author>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<Collection> Collections => Set<Collection>();
    public DbSet<CollectionBook> CollectionBooks => Set<CollectionBook>();
    public DbSet<Read> Reads => Set<Read>();
    public DbSet<BookFile> BookFiles => Set<BookFile>();
    public DbSet<Highlight> Highlights => Set<Highlight>();
    public DbSet<ReadingSession> ReadingSessions => Set<ReadingSession>();
    public DbSet<BackupSettingsRow> BackupSettings => Set<BackupSettingsRow>();

    /// <summary>Keys that encrypt login cookies; stored here so restarts don't sign everyone out.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    /// <summary>
    /// Every library query is filtered to this user, so one user can never read or change another's
    /// books. Outside a signed-in request it is null, which matches nothing.
    /// </summary>
    public Guid? CurrentUserId => currentUser.UserId;

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampAuditColumns();
        var affectsNotes = AffectsNotesExport();
        var result = base.SaveChanges(acceptAllChangesOnSuccess);
        NotifyNotesExport(affectsNotes);
        return result;
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampAuditColumns();
        var affectsNotes = AffectsNotesExport();
        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        NotifyNotesExport(affectsNotes);
        return result;
    }

    /// <summary>Changes that show up in the exported Markdown notes (see <see cref="NotesExporter"/>).</summary>
    private bool AffectsNotesExport() => ChangeTracker.Entries().Any(entry =>
        entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
        && entry.Entity is Book or Author or Tag or BookAuthor or BookTag or Highlight);

    private void NotifyNotesExport(bool affectsNotes)
    {
        if (affectsNotes && CurrentUserId is { } userId)
        {
            notesExport.Enqueue(userId);
        }
    }

    private void StampAuditColumns()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var entry in ChangeTracker.Entries<IAuditable>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // timestamptz only accepts UTC values; normalize instead of failing on other offsets.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetConverter>();
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.HasPostgresExtension("unaccent");
        builder.HasPostgresExtension("pg_trgm");

        RenameIdentityTables(builder);

        builder.Entity<Book>(book =>
        {
            book.ToTable("books", table =>
            {
                table.HasCheckConstraint("ck_books_status", InList("status", Enum.GetNames<BookStatus>()));
                table.HasCheckConstraint("ck_books_rating", FormattableString.Invariant($"rating IS NULL OR (rating >= {RatingAttribute.Min} AND rating <= {RatingAttribute.Max})"));
            });
            book.Property(b => b.Title).IsRequired().HasMaxLength(ApiLimits.TitleMaxLength);
            book.Property(b => b.Status).HasConversion<string>().HasMaxLength(32);
            book.Property(b => b.Rating).HasPrecision(2, 1);
            book.Property(b => b.Version).IsRowVersion().HasColumnName("xmin");

            book.HasOne<AppUser>().WithMany().HasForeignKey(b => b.UserId).OnDelete(DeleteBehavior.Cascade);

            book.HasMany(b => b.Tags)
                .WithMany(t => t.Books)
                .UsingEntity<BookTag>(
                    right => right.HasOne(bt => bt.Tag).WithMany().HasForeignKey(bt => bt.TagId).OnDelete(DeleteBehavior.Cascade),
                    left => left.HasOne(bt => bt.Book).WithMany().HasForeignKey(bt => bt.BookId).OnDelete(DeleteBehavior.Cascade),
                    join =>
                    {
                        join.ToTable("book_tags");
                        join.HasKey(bt => new { bt.BookId, bt.TagId });
                        join.HasIndex(bt => bt.TagId);
                        join.HasQueryFilter(bt => bt.Book.UserId == CurrentUserId);
                    });

            book.HasQueryFilter(b => b.UserId == CurrentUserId);
        });

        builder.Entity<Author>(author =>
        {
            author.ToTable("authors", table =>
                table.HasCheckConstraint("ck_authors_dates", "birth_date IS NULL OR death_date IS NULL OR death_date >= birth_date"));
            author.Property(a => a.Name).IsRequired().HasMaxLength(ApiLimits.AuthorNameMaxLength);
            author.Property(a => a.Version).IsRowVersion().HasColumnName("xmin");

            author.HasOne<AppUser>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);

            author.HasQueryFilter(a => a.UserId == CurrentUserId);
        });

        builder.Entity<BookAuthor>(bookAuthor =>
        {
            bookAuthor.ToTable("book_authors");
            bookAuthor.HasKey(ba => new { ba.BookId, ba.AuthorId });
            bookAuthor.HasOne(ba => ba.Book).WithMany(b => b.Authors).HasForeignKey(ba => ba.BookId).OnDelete(DeleteBehavior.Cascade);
            bookAuthor.HasOne(ba => ba.Author).WithMany(a => a.Books).HasForeignKey(ba => ba.AuthorId).OnDelete(DeleteBehavior.Cascade);
            bookAuthor.HasIndex(ba => ba.AuthorId);

            bookAuthor.HasQueryFilter(ba => ba.Book.UserId == CurrentUserId);
        });

        builder.Entity<Tag>(tag =>
        {
            // Names are unique per user ignoring case: see the ux_tags_user_id_lower_name index in the migrations.
            tag.ToTable("tags");
            tag.Property(t => t.Name).IsRequired().HasMaxLength(ApiLimits.TagNameMaxLength);
            tag.Property(t => t.Version).IsRowVersion().HasColumnName("xmin");

            tag.HasOne<AppUser>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);

            tag.HasQueryFilter(t => t.UserId == CurrentUserId);
        });

        builder.Entity<Collection>(collection =>
        {
            // Names are unique per user ignoring case: see the ux_collections_user_id_lower_name index in the migrations.
            collection.ToTable("collections", table =>
                table.HasCheckConstraint("ck_collections_type", InList("type", Enum.GetNames<CollectionType>())));
            collection.Property(c => c.Name).IsRequired().HasMaxLength(ApiLimits.CollectionNameMaxLength);
            collection.Property(c => c.Type).HasConversion<string>().HasMaxLength(16);
            collection.Property(c => c.Version).IsRowVersion().HasColumnName("xmin");

            collection.HasOne<AppUser>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);

            collection.HasQueryFilter(c => c.UserId == CurrentUserId);
        });

        builder.Entity<CollectionBook>(collectionBook =>
        {
            collectionBook.ToTable("collection_books");
            collectionBook.HasKey(cb => new { cb.CollectionId, cb.BookId });
            collectionBook.HasOne(cb => cb.Collection).WithMany(c => c.Books).HasForeignKey(cb => cb.CollectionId).OnDelete(DeleteBehavior.Cascade);
            collectionBook.HasOne(cb => cb.Book).WithMany(b => b.Collections).HasForeignKey(cb => cb.BookId).OnDelete(DeleteBehavior.Cascade);
            collectionBook.HasIndex(cb => cb.BookId);

            collectionBook.HasQueryFilter(cb => cb.Book.UserId == CurrentUserId);
        });

        builder.Entity<Read>(read =>
        {
            read.ToTable("reads", table =>
            {
                table.HasCheckConstraint("ck_reads_status", InList("status", Enum.GetNames<ReadStatus>()));
                table.HasCheckConstraint("ck_reads_dates", "started_at IS NULL OR finished_at IS NULL OR finished_at >= started_at");
                table.HasCheckConstraint("ck_reads_progress", "progress IS NULL OR (progress >= 0 AND progress <= 1)");
            });
            read.Property(r => r.Status).HasConversion<string>().HasMaxLength(32);
            read.Property(r => r.Version).IsRowVersion().HasColumnName("xmin");

            read.HasOne(r => r.Book).WithMany(b => b.Reads).HasForeignKey(r => r.BookId).OnDelete(DeleteBehavior.Cascade);
            read.HasOne(r => r.File).WithMany().HasForeignKey(r => r.FileId).OnDelete(DeleteBehavior.SetNull);
            read.Property(r => r.Location).HasMaxLength(ApiLimits.LocationMaxLength);

            read.HasQueryFilter(r => r.Book.UserId == CurrentUserId);
        });

        builder.Entity<BookFile>(file =>
        {
            file.ToTable("book_files", table =>
                table.HasCheckConstraint("ck_book_files_format", InList("format", Enum.GetNames<BookFormat>())));
            file.Property(f => f.Format).HasConversion<string>().HasMaxLength(16);
            file.Property(f => f.FileName).IsRequired().HasMaxLength(ApiLimits.FileNameMaxLength);
            file.Property(f => f.Sha256).IsRequired().HasMaxLength(64);
            file.Property(f => f.BrowseLocation).HasMaxLength(ApiLimits.LocationMaxLength);
            file.HasIndex(f => new { f.BookId, f.Format }).IsUnique();

            file.HasOne(f => f.Book).WithMany(b => b.Files).HasForeignKey(f => f.BookId).OnDelete(DeleteBehavior.Cascade);

            file.HasQueryFilter(f => f.Book.UserId == CurrentUserId);
        });

        builder.Entity<Highlight>(highlight =>
        {
            highlight.ToTable("highlights", table =>
            {
                table.HasCheckConstraint("ck_highlights_format", InList("format", Enum.GetNames<BookFormat>()));
                table.HasCheckConstraint("ck_highlights_color", InList("color", Enum.GetNames<HighlightColor>()));
                table.HasCheckConstraint("ck_highlights_position", "position >= 0 AND position <= 1");
            });
            highlight.Property(h => h.Format).HasConversion<string>().HasMaxLength(16);
            highlight.Property(h => h.Color).HasConversion<string>().HasMaxLength(16);
            highlight.Property(h => h.AnchoredSha256).IsRequired().HasMaxLength(64);
            highlight.Property(h => h.Location).IsRequired().HasMaxLength(ApiLimits.LocationMaxLength);
            highlight.Property(h => h.Text).IsRequired().HasMaxLength(ApiLimits.HighlightTextMaxLength);
            highlight.Property(h => h.Prefix).HasMaxLength(ApiLimits.HighlightContextMaxLength);
            highlight.Property(h => h.Suffix).HasMaxLength(ApiLimits.HighlightContextMaxLength);
            highlight.Property(h => h.Chapter).HasMaxLength(ApiLimits.ChapterMaxLength);
            highlight.Property(h => h.PageLabel).HasMaxLength(ApiLimits.PageLabelMaxLength);
            highlight.Property(h => h.Note).HasMaxLength(ApiLimits.HighlightNoteMaxLength);
            highlight.Property(h => h.Version).IsRowVersion().HasColumnName("xmin");

            highlight.HasOne(h => h.Book).WithMany(b => b.Highlights).HasForeignKey(h => h.BookId).OnDelete(DeleteBehavior.Cascade);
            highlight.HasOne(h => h.File).WithMany().HasForeignKey(h => h.FileId).OnDelete(DeleteBehavior.SetNull);

            highlight.HasQueryFilter(h => h.Book.UserId == CurrentUserId);
        });

        builder.Entity<ReadingSession>(session =>
        {
            session.ToTable("reading_sessions", table =>
                table.HasCheckConstraint("ck_reading_sessions_times", "ended_at >= started_at"));
            session.Property(s => s.Id).ValueGeneratedNever();
            session.HasOne(s => s.Read).WithMany(r => r.Sessions).HasForeignKey(s => s.ReadId).OnDelete(DeleteBehavior.Cascade);
            session.HasOne<BookFile>().WithMany().HasForeignKey(s => s.FileId).OnDelete(DeleteBehavior.SetNull);
            session.HasIndex(s => s.StartedAt);

            session.HasQueryFilter(s => s.Read.Book.UserId == CurrentUserId);
        });

        builder.Entity<BackupSettingsRow>(settings =>
        {
            settings.ToTable("backup_settings", table =>
            {
                table.HasCheckConstraint("ck_backup_settings_singleton", $"id = {BackupSettingsRow.SingletonId}");
                table.HasCheckConstraint("ck_backup_settings_keep_count", "keep_count BETWEEN 1 AND 365");
            });
            settings.Property(s => s.Id).ValueGeneratedNever();
            settings.Property(s => s.Frequency).HasConversion<string>().HasMaxLength(16);
            settings.Property(s => s.DayOfWeek).HasConversion<string>().HasMaxLength(16);
            settings.Property(s => s.TimeZone).IsRequired().HasMaxLength(100);
            settings.Property(s => s.LastRunMessage).HasMaxLength(2000);
            settings.Property(s => s.LastRunFile).HasMaxLength(255);
            settings.HasData(new BackupSettingsRow());
        });
    }

    private static void RenameIdentityTables(ModelBuilder builder)
    {
        builder.Entity<AppUser>().ToTable("users");
        builder.Entity<IdentityRole<Guid>>().ToTable("roles");
        builder.Entity<IdentityUserRole<Guid>>().ToTable("user_roles");
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");
        builder.Entity<IdentityRoleClaim<Guid>>().ToTable("role_claims");
        builder.Entity<IdentityUserPasskey<Guid>>().ToTable("user_passkeys");
    }

    private static string InList(string column, IEnumerable<string> values) =>
        $"{column} IN ({string.Join(", ", values.Select(v => $"'{v}'"))})";
}

internal sealed class UtcDateTimeOffsetConverter() : ValueConverter<DateTimeOffset, DateTimeOffset>(
    value => value.ToUniversalTime(),
    value => value);
