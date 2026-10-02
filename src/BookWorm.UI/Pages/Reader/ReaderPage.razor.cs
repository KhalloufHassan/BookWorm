using System.Globalization;
using System.Text.Json;
using BookWorm.Contracts;
using BookWorm.UI.Api;
using BookWorm.UI.Components;
using BookWorm.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;

namespace BookWorm.UI.Pages.Reader;

/// <summary>
/// Reads a book file. The rendering happens in JavaScript (reader.js: foliate-js or PDF.js); this
/// page owns the chrome, highlights, the reading position and the reading sessions.
/// Without a read in progress the book is only browsed: the position is remembered per file, but
/// no read is started and no reading time is counted until "Start reading" is pressed.
/// </summary>
public sealed partial class ReaderPage
{
    /// <summary>A sitting ends after this long without turning a page or touching the reader.</summary>
    private static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(60);

    private ElementReference _host;
    private IJSObjectReference _module;
    private IJSObjectReference _reader;
    private DotNetObjectReference<ReaderPage> _self;
    private Timer _saveTimer;
    private Timer _heartbeat;

    private BookDetails _book;
    private BookFileDetails _file;
    private ReadDetails _read;
    private readonly Dictionary<Guid, BrowsePositionRequest> _browsed = [];
    private bool _starting;
    private List<HighlightDetails> _highlights = [];
    private List<TocEntry> _toc = [];
    private ReaderSettings _settings = new();
    private bool _isFixedLayout;
    private Guid? _openedBookId;
    private string _openAt;
    private bool _needsOpen;
    private bool _opening = true;
    private string _error;

    private string _location;
    private double _progress;
    private string _chapter;
    private string _pageLabel;
    private int? _pageNumber;
    private int? _pageCount;
    private bool _hasPageList;
    private int _relocations;
    private bool _finishOffered;

    private Guid _sessionId;
    private DateTimeOffset _sessionStartedAt;
    private double _sessionStartProgress;
    private bool _sessionActive;
    private DateTimeOffset _lastActivity = DateTimeOffset.UtcNow;
    private bool _visible = true;
    private bool _saveErrorShown;

    private SelectionEvent _selection;
    private ClickedHighlight _clicked;
    private bool _chromeHidden;
    private bool _panelOpen;
    private ReaderPanel _panel = ReaderPanel.Contents;

    private string _searchQuery;
    private readonly List<SearchHit> _searchResults = [];
    private bool _searching;
    private bool _searchDone;
    private double _searchProgress;

    [Inject] private BookWormApiClient Api { get; set; }
    [Inject] private IJSRuntime JS { get; set; }
    [Inject] private ThemeState Theme { get; set; }
    [Inject] private IAppHost Host { get; set; }
    [Inject] private IPreferenceStore Preferences { get; set; }
    [Inject] private IDialogService Dialogs { get; set; }
    [Inject] private ISnackbar Snackbar { get; set; }

    [Parameter]
    public Guid Id { get; set; }

    /// <summary>Which of the book's files to read; by default the one last read, else the best format.</summary>
    [SupplyParameterFromQuery(Name = "file")]
    public Guid? FileId { get; set; }

    /// <summary>A location to open at instead of the saved one, e.g. a highlight's.</summary>
    [SupplyParameterFromQuery(Name = "at")]
    public string At { get; set; }

    private bool IsPdf => _file?.Format == BookFormat.Pdf;
    private bool IsFixedLayout => _isFixedLayout && !IsPdf;
    private string EffectiveTheme => _settings.EffectiveTheme;

    private string PageText
    {
        get
        {
            if (IsPdf && _pageNumber is { } page)
            {
                return _pageLabel is { } label && label != page.ToString(CultureInfo.InvariantCulture)
                    ? $"Page {label} ({page} of {_pageCount})"
                    : $"Page {page} of {_pageCount}";
            }

            if (_pageLabel is { Length: > 0 } printed)
            {
                return $"Page {printed}";
            }

            return _pageNumber is { } number && _pageCount is { } count ? $"Page {number} of {count}" : "";
        }
    }

    private string PageTooltip => !IsPdf && !IsFixedLayout && _pageLabel is null && _pageNumber is not null
        ? _hasPageList ? null : "Estimated from the length of the text: this book has no printed page numbers."
        : null;

    private string PanelTitle => _panel switch
    {
        ReaderPanel.Contents => "Contents",
        ReaderPanel.Search => "Search",
        ReaderPanel.Highlights => "Highlights and notes",
        _ => "Reading settings",
    };

    protected override async Task OnInitializedAsync()
    {
        _self = DotNetObjectReference.Create(this);
        _settings = await ReaderSettings.LoadAsync(Preferences);
        Theme.SetReaderDarkMode(_settings.IsDark);
        Host.Pausing += SaveOnPauseAsync;
        Host.SetReading(true);
        _heartbeat = new Timer(_ => InvokeAsync(HeartbeatAsync), null, HeartbeatInterval, HeartbeatInterval);
    }

    protected override async Task OnParametersSetAsync()
    {
        if (_openedBookId == Id && _file is not null && (FileId is null || FileId == _file.Id))
        {
            return;
        }

        _openedBookId = Id;
        _error = null;
        _opening = true;
        try
        {
            _book = await Api.GetBookAsync(Id);
            if (_book.Files.Count == 0)
            {
                _error = "This book has no file to read yet. Upload one on the book's page.";
                _opening = false;
                return;
            }

            _highlights = await Api.GetHighlightsAsync(Id);
            _read = await Api.GetCurrentReadAsync(Id);
            _browsed.Clear();
            _file = _book.Files.FirstOrDefault(f => f.Id == FileId)
                ?? _book.Files.FirstOrDefault(f => f.Id == _read?.FileId)
                ?? _book.Files.OrderBy(f => BookFormats.ReadingPreference(f.Format)).First();
            _openAt = At;
            _needsOpen = true;
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException)
        {
            _error = ApiErrorMessages.Describe(ex);
            _opening = false;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_needsOpen)
        {
            _needsOpen = false;
            await OpenAsync();
        }
    }

    private async Task OpenAsync()
    {
        if (_book is null || _file is null)
        {
            return;
        }

        await CloseReaderAsync();
        _opening = true;
        _relocations = 0;
        _selection = null;
        _clicked = null;
        _searchResults.Clear();
        StateHasChanged();

        try
        {
            _module ??= await JS.InvokeAsync<IJSObjectReference>("import", "./_content/BookWorm.UI/js/reader.js");
            var (location, progress) = SavedPosition();
            _reader = await _module.InvokeAsync<IJSObjectReference>("open", _host, new
            {
                url = BookWormApiClient.FileUrl(Id, _file.Id),
                fileName = _file.FileName,
                format = _file.Format.ToString(),
                location = _openAt ?? location,
                progress = progress ?? 0,
                settings = JsSettings(),
                highlights = _highlights.Where(h => h.FileId == _file.Id).Select(ToJs),
            }, _self);

            var info = await _reader.InvokeAsync<ReaderInfo>("info");
            _toc = info.Toc;
            _isFixedLayout = info.IsFixedLayout;
            _hasPageList = info.HasPageList;
            _opening = false;
            MarkActivity();
            StateHasChanged();

            await ReanchorAsync();
        }
        catch (JSException ex)
        {
            _error = $"The book couldn't be opened. {ex.Message.Split('\n')[0]}";
            _opening = false;
            StateHasChanged();
        }
    }

    /// <summary>Where to open the current file: the read's position, or where it was last browsed.</summary>
    private (string Location, double? Progress) SavedPosition()
    {
        if (_read is not null)
        {
            return (_read.FileId == _file.Id ? _read.Location : null, _read.Progress);
        }

        return _browsed.TryGetValue(_file.Id, out var browsed)
            ? (browsed.Location, browsed.Progress)
            : (_file.BrowseLocation, _file.BrowseProgress);
    }

    /// <summary>After a file was replaced, finds its highlights in the new version and records where they are now.</summary>
    private async Task ReanchorAsync()
    {
        var pending = _highlights.Where(h => h.State == HighlightState.NeedsCheck && h.FileId == _file?.Id).ToList();
        if (_reader is null || _file is null || pending.Count == 0)
        {
            return;
        }

        var results = await _reader.InvokeAsync<List<ReanchorResult>>("reanchor",
            pending.Select(h => new { id = h.Id, text = h.Text, prefix = h.Prefix, suffix = h.Suffix }));
        var found = 0;
        var missing = 0;
        foreach (var result in results)
        {
            try
            {
                var updated = await Api.ReanchorHighlightAsync(Id, result.Id, new ReanchorHighlightRequest
                {
                    FileId = _file.Id,
                    Found = result.Found,
                    Location = result.Location,
                    Chapter = Truncate(result.Chapter, ApiLimits.ChapterMaxLength),
                    PageLabel = Truncate(result.PageLabel, ApiLimits.PageLabelMaxLength),
                    Position = result.Position is { } p ? Math.Clamp(p, 0, 1) : null,
                });
                Replace(updated);
                if (updated.State == HighlightState.Anchored)
                {
                    found++;
                    await _reader.InvokeVoidAsync("addHighlight", ToJs(updated));
                }
                else
                {
                    missing++;
                }
            }
            catch (Exception ex) when (ex is ApiException or HttpRequestException)
            {
                // Tried again next time the book is opened.
            }
        }

        if (missing > 0)
        {
            Snackbar.Add($"The file changed: {Labels.Count(found, "highlight")} found again, {missing} couldn't be found. They're kept with their notes in Highlights.", Severity.Warning);
        }
        else if (found > 0)
        {
            Snackbar.Add($"The file changed: all {Labels.Count(found, "highlight")} were found again.", Severity.Info);
        }
    }

    // Callbacks from reader.js

    [JSInvokable]
    public Task OnRelocated(RelocatedEvent e)
    {
        _location = e.Location;
        _progress = Math.Clamp(e.Progress, 0, 1);
        _chapter = e.Chapter;
        _pageLabel = e.PageLabel;
        _pageNumber = e.PageNumber;
        _pageCount = e.PageCount;
        _clicked = null;
        _relocations++;

        // The first report is just where the book opened.
        if (_relocations > 1)
        {
            MarkActivity();
            ScheduleSave();
        }

        _ = UpdateBeaconAsync();

        if (e.AtEnd && _relocations > 1 && !_finishOffered && _read?.Status == ReadStatus.CurrentlyReading)
        {
            _finishOffered = true;
            _ = InvokeAsync(OfferFinishAsync);
        }

        StateHasChanged();
        return Task.CompletedTask;
    }

    [JSInvokable]
    public void OnSelection(SelectionEvent selection)
    {
        _selection = selection is { Text.Length: > 0 } ? selection : null;
        if (_selection is not null)
        {
            _clicked = null;
        }

        StateHasChanged();
    }

    [JSInvokable]
    public void OnHighlightClicked(Guid id, ClientRect rect)
    {
        var highlight = _highlights.FirstOrDefault(h => h.Id == id);
        _clicked = highlight is null ? null : new ClickedHighlight(highlight, rect);
        _selection = null;
        StateHasChanged();
    }

    [JSInvokable]
    public void OnTap()
    {
        if (_clicked is not null || _selection is not null)
        {
            _clicked = null;
            _selection = null;
        }
        else
        {
            _chromeHidden = !_chromeHidden;
        }

        StateHasChanged();
    }

    [JSInvokable]
    public void OnActivity() => MarkActivity();

    [JSInvokable]
    public void OnEscape()
    {
        if (_panelOpen)
        {
            _panelOpen = false;
        }
        else
        {
            _clicked = null;
            _selection = null;
            _chromeHidden = false;
        }

        StateHasChanged();
    }

    [JSInvokable]
    public async Task OnVisibilityChanged(bool visible)
    {
        _visible = visible;
        if (!visible)
        {
            await SaveAsync();
            _sessionActive = false;
        }
        else
        {
            MarkActivity();
        }
    }

    [JSInvokable]
    public void OnSearchResults(List<SearchHit> hits)
    {
        _searchResults.AddRange(hits);
        StateHasChanged();
    }

    [JSInvokable]
    public void OnSearchProgress(double progress)
    {
        _searchProgress = progress;
        StateHasChanged();
    }

    [JSInvokable]
    public void OnSearchDone(int count)
    {
        _searching = false;
        _searchDone = true;
        StateHasChanged();
    }

    // Navigation

    private async Task NextAsync()
    {
        if (_reader is not null)
        {
            await _reader.InvokeVoidAsync("next");
        }
    }

    private async Task PrevAsync()
    {
        if (_reader is not null)
        {
            await _reader.InvokeVoidAsync("prev");
        }
    }

    private async Task OnSliderChangedAsync(double fraction)
    {
        _progress = fraction;
        if (_reader is not null)
        {
            await _reader.InvokeVoidAsync("goToFraction", fraction);
        }
    }

    private async Task GoToAsync(string target, bool closePanel = true)
    {
        if (_reader is null)
        {
            return;
        }

        if (closePanel)
        {
            _panelOpen = false;
        }

        await _reader.InvokeVoidAsync("goTo", target);
    }

    private async Task ZoomAsync(int direction)
    {
        if (_reader is not null)
        {
            await _reader.InvokeAsync<int?>("zoom", direction);
        }
    }

    private async Task SwitchFileAsync(BookFileDetails file)
    {
        await SaveAsync();
        _file = file;
        _openAt = null;
        await OpenAsync();
    }

    private void OpenPanel(ReaderPanel panel)
    {
        _panel = panel;
        _panelOpen = true;
        _selection = null;
        _clicked = null;
    }

    private void ClosePanel() => _panelOpen = false;

    // Search

    private async Task SearchAsync()
    {
        if (_reader is null)
        {
            return;
        }

        _searchResults.Clear();
        _searchDone = false;
        _searchProgress = 0;
        if (string.IsNullOrWhiteSpace(_searchQuery))
        {
            await _reader.InvokeVoidAsync("clearSearch");
            return;
        }

        _searching = true;
        StateHasChanged();
        try
        {
            await _reader.InvokeVoidAsync("search", _searchQuery.Trim());
        }
        catch (JSException)
        {
            _searching = false;
            Snackbar.Add("Searching this book failed.", Severity.Error);
        }
    }

    // Highlights

    private async Task HighlightSelectionAsync(HighlightColor color, bool withNote)
    {
        if (_selection is not { } selection || _file is null || _reader is null)
        {
            return;
        }

        _selection = null;
        if (selection.Text.Length > ApiLimits.HighlightTextMaxLength)
        {
            Snackbar.Add("That passage is too long to highlight. Select a shorter one.", Severity.Warning);
            return;
        }

        try
        {
            var created = await Api.CreateHighlightAsync(Id, new CreateHighlightRequest
            {
                FileId = _file.Id,
                Location = selection.Location,
                Text = selection.Text,
                Prefix = Truncate(selection.Prefix, ApiLimits.HighlightContextMaxLength),
                Suffix = Truncate(selection.Suffix, ApiLimits.HighlightContextMaxLength),
                Chapter = Truncate(selection.Chapter, ApiLimits.ChapterMaxLength),
                PageLabel = Truncate(selection.PageLabel, ApiLimits.PageLabelMaxLength),
                Position = Math.Clamp(selection.Position, 0, 1),
                Color = color,
            });
            _highlights.Add(created);
            _highlights = _highlights.OrderBy(h => h.Position).ThenBy(h => h.CreatedAt).ToList();
            await _reader.InvokeVoidAsync("clearSelection");
            await _reader.InvokeVoidAsync("addHighlight", ToJs(created));
            if (withNote)
            {
                await EditNoteAsync(created);
            }
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException)
        {
            Snackbar.Add(ApiErrorMessages.Describe(ex), Severity.Error);
        }
    }

    private async Task CopySelectionAsync()
    {
        if (_selection is { } selection && _reader is not null)
        {
            await _reader.InvokeVoidAsync("copyText", selection.Text);
            Snackbar.Add("Copied.", Severity.Success);
        }
    }

    private async Task RecolorAsync(HighlightDetails highlight, HighlightColor color)
    {
        _clicked = null;
        await UpdateHighlightAsync(highlight, color, highlight.Note);
    }

    private async Task EditNoteAsync(HighlightDetails highlight)
    {
        _clicked = null;
        var current = _highlights.FirstOrDefault(h => h.Id == highlight.Id) ?? highlight;
        var (saved, note) = await HighlightNoteDialog.ShowAsync(Dialogs, current.Text, current.Note, current.Color);
        if (saved)
        {
            await UpdateHighlightAsync(current, current.Color, note);
        }
    }

    private async Task UpdateHighlightAsync(HighlightDetails highlight, HighlightColor color, string note)
    {
        try
        {
            var updated = await Api.UpdateHighlightAsync(Id, highlight.Id, new UpdateHighlightRequest { Color = color, Note = note, Version = highlight.Version });
            Replace(updated);
            if (_reader is not null && updated.FileId == _file?.Id)
            {
                await _reader.InvokeVoidAsync("removeHighlight", updated.Id);
                await _reader.InvokeVoidAsync("addHighlight", ToJs(updated));
            }
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException)
        {
            Snackbar.Add(ApiErrorMessages.Describe(ex), Severity.Error);
        }
    }

    private async Task DeleteHighlightAsync(HighlightDetails highlight)
    {
        _clicked = null;
        try
        {
            await Api.DeleteHighlightAsync(Id, highlight.Id);
            _highlights.RemoveAll(h => h.Id == highlight.Id);
            if (_reader is not null)
            {
                await _reader.InvokeVoidAsync("removeHighlight", highlight.Id);
            }

            Snackbar.Add("Highlight deleted.", Severity.Info);
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException)
        {
            Snackbar.Add(ApiErrorMessages.Describe(ex), Severity.Error);
        }
    }

    private void Replace(HighlightDetails updated)
    {
        var index = _highlights.FindIndex(h => h.Id == updated.Id);
        if (index >= 0)
        {
            _highlights[index] = updated;
        }
    }

    private string HighlightWhere(HighlightDetails highlight)
    {
        var parts = new List<string>();
        if (highlight.Chapter is { } chapter)
        {
            parts.Add(chapter);
        }

        if (highlight.PageLabel is { } page)
        {
            parts.Add($"p. {page}");
        }

        if (highlight.State == HighlightState.Missing)
        {
            parts.Add("not found in the current file");
        }
        else if (highlight.Format != _file?.Format)
        {
            parts.Add($"in the {BookFormats.Label(highlight.Format)}");
        }

        return string.Join(" · ", parts);
    }

    // Settings

    private async Task UpdateSettingsAsync(Action<ReaderSettings> change)
    {
        change(_settings);
        Theme.SetReaderDarkMode(_settings.IsDark);
        await _settings.SaveAsync(Preferences);
        if (_reader is not null)
        {
            await _reader.InvokeVoidAsync("applySettings", JsSettings());
        }
    }

    private object JsSettings() => new
    {
        theme = EffectiveTheme,
        fontSize = _settings.FontSize,
        fontFamily = _settings.FontFamily,
        lineHeight = _settings.LineHeight,
        margin = _settings.Margin,
        flow = _settings.Flow,
        justify = _settings.Justify,
        columns = _settings.Columns,
        zoom = IsPdf ? _settings.PdfZoom : _settings.FixedZoom,
    };

    private Task ToggleDarkAsync() => UpdateSettingsAsync(s => s.Theme = s.IsDark ? "light" : "dark");

    // Reading position and sessions

    private void MarkActivity()
    {
        var now = DateTimeOffset.UtcNow;
        _lastActivity = now;
        if (!_sessionActive && _visible && _read is not null)
        {
            _sessionActive = true;
            _sessionId = Guid.NewGuid();
            _sessionStartedAt = now;
            _sessionStartProgress = _progress;
        }
    }

    private void ScheduleSave()
    {
        _saveTimer ??= new Timer(_ => InvokeAsync(SaveAsync));
        _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
    }

    private async Task HeartbeatAsync()
    {
        if (!_sessionActive)
        {
            return;
        }

        if (DateTimeOffset.UtcNow - _lastActivity > IdleAfter || !_visible)
        {
            // The reader was left alone: the sitting ended with the last save.
            _sessionActive = false;
            return;
        }

        await SaveAsync();
    }

    private ReadProgressRequest CurrentProgress() => _file is null || _location is null
        ? null
        : new ReadProgressRequest
        {
            FileId = _file.Id,
            Location = Truncate(_location, ApiLimits.LocationMaxLength),
            Progress = _progress,
            SessionId = _sessionActive ? _sessionId : null,
            SessionStartedAt = _sessionActive ? _sessionStartedAt : null,
            SessionStartProgress = _sessionActive ? _sessionStartProgress : null,
        };

    private BrowsePositionRequest CurrentBrowsePosition() => _file is null || _location is null
        ? null
        : new BrowsePositionRequest { Location = Truncate(_location, ApiLimits.LocationMaxLength), Progress = _progress };

    private async Task SaveAsync()
    {
        try
        {
            if (_read is not null && CurrentProgress() is { } progress)
            {
                await Api.SaveProgressAsync(Id, _read.Id, progress);
            }
            else if (_read is null && _relocations > 1 && CurrentBrowsePosition() is { } position)
            {
                _browsed[_file.Id] = position;
                await Api.SaveBrowsePositionAsync(Id, _file.Id, position);
            }
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException)
        {
            if (!_saveErrorShown)
            {
                _saveErrorShown = true;
                Snackbar.Add("Your reading position couldn't be saved. " + ApiErrorMessages.Describe(ex), Severity.Warning);
            }
        }
    }

    /// <summary>The mobile app is going to the background (it can't rely on the page-close beacon).</summary>
    private Task SaveOnPauseAsync() => InvokeAsync(async () =>
    {
        await SaveAsync();
        _sessionActive = false;
    });

    /// <summary>Lets reader.js save the position itself if the page is closed before the next save.</summary>
    private async Task UpdateBeaconAsync()
    {
        if (_reader is null || _relocations < 2 || !Host.UsesPageCloseBeacon)
        {
            return;
        }

        try
        {
            if (_read is not null && CurrentProgress() is { } progress)
            {
                await _reader.InvokeVoidAsync("setBeacon", BookWormApiClient.ProgressUrl(Id, _read.Id), JsonSerializer.Serialize(progress, BookWormJson.Options));
            }
            else if (_read is null && CurrentBrowsePosition() is { } position)
            {
                await _reader.InvokeVoidAsync("setBeacon", BookWormApiClient.BrowsePositionUrl(Id, _file.Id), JsonSerializer.Serialize(position, BookWormJson.Options));
            }
        }
        catch (JSDisconnectedException)
        {
            // Closing.
        }
    }

    private async Task StartReadingAsync()
    {
        _starting = true;
        try
        {
            _read = await Api.StartOrResumeReadAsync(Id);
            _finishOffered = false;

            // The read starts where the book is open now.
            MarkActivity();
            await SaveAsync();
            await UpdateBeaconAsync();
            Snackbar.Add("Started a new read.", Severity.Success);
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException)
        {
            Snackbar.Add(ApiErrorMessages.Describe(ex), Severity.Error);
        }
        finally
        {
            _starting = false;
        }
    }

    private async Task OfferFinishAsync()
    {
        if (_book is null || _read is null)
        {
            return;
        }

        var dialog = await Dialogs.ShowAsync<FinishReadDialog>("The end", new DialogParameters<FinishReadDialog>
        {
            { d => d.Title, _book.Title },
            { d => d.BookStatus, _book.Status },
        }, new DialogOptions { MaxWidth = MaxWidth.ExtraSmall, FullWidth = true });
        var result = await dialog.Result;
        if (result is not { Canceled: false, Data: bool updateBook })
        {
            return;
        }

        try
        {
            await SaveAsync();
            await Api.FinishReadAsync(Id, _read.Id, new FinishReadRequest { Status = ReadStatus.Finished, UpdateBookStatus = updateBook });

            // From here on the book is only browsed, until another read is started.
            _read = null;
            _sessionActive = false;
            await UpdateBeaconAsync();
            Snackbar.Add(updateBook ? "Marked the read and the book as finished." : "Marked the read as finished.", Severity.Success);
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException)
        {
            Snackbar.Add(ApiErrorMessages.Describe(ex), Severity.Error);
        }
    }

    // Helpers

    private static object ToJs(HighlightDetails h) => new
    {
        id = h.Id,
        location = h.Location,
        color = h.Color.ToString(),
        text = h.Text,
        prefix = h.Prefix,
        suffix = h.Suffix,
        state = h.State.ToString(),
    };

    private static string Truncate(string value, int length) =>
        value is null ? null : value.Length <= length ? value : value[..length];

    private static string Clamp(string text, int length) => text.Length <= length ? text : text[..length].TrimEnd() + "…";

    private static string Px(double value) => value.ToString("0", CultureInfo.InvariantCulture) + "px";

    /// <summary>Places a floating menu above the given box (or below it near the top of the screen).</summary>
    private static string FloatStyle(ClientRect rect)
    {
        var top = rect.Y > 72 ? rect.Y - 56 : rect.Y + rect.Height + 10;
        return $"left: clamp(8.5rem, {Px(rect.X + rect.Width / 2)}, calc(100vw - 8.5rem)); top: {Px(top)};";
    }

    private static string NoteStyle(ClientRect rect)
    {
        var below = rect.Y + rect.Height + 12;
        return $"left: clamp(10rem, {Px(rect.X + rect.Width / 2)}, calc(100vw - 10rem)); top: {Px(rect.Y > 72 ? below : below + 50)};";
    }

    private async Task CloseReaderAsync()
    {
        if (_reader is null)
        {
            return;
        }

        try
        {
            await _reader.InvokeVoidAsync("destroy");
            await _reader.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            // The page is already gone.
        }

        _reader = null;
    }

    public async ValueTask DisposeAsync()
    {
        Theme.SetReaderDarkMode(null);
        Host.Pausing -= SaveOnPauseAsync;
        Host.SetReading(false);
        _heartbeat?.Dispose();
        _saveTimer?.Dispose();
        await SaveAsync();
        await CloseReaderAsync();
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // The page is already gone.
            }
        }

        _self?.Dispose();
    }

    private enum ReaderPanel
    {
        Contents,
        Search,
        Highlights,
        Settings,
    }

    private sealed record ClickedHighlight(HighlightDetails Highlight, ClientRect Rect);
}

public sealed record ClientRect(double X, double Y, double Width, double Height);

public sealed record TocEntry(string Label, string Href, int Depth);

public sealed record ReaderInfo(List<TocEntry> Toc, bool IsFixedLayout, bool HasPageList, string Direction);

public sealed record RelocatedEvent(string Location, double Progress, string Chapter, string PageLabel, int? PageNumber, int? PageCount, bool AtEnd);

public sealed record SelectionEvent(
    string Text,
    string Location,
    string Prefix,
    string Suffix,
    string Chapter,
    string PageLabel,
    double Position,
    ClientRect Rect);

public sealed record SearchHit(string Label, string Location, string Before, string Match, string After);

public sealed record ReanchorResult(Guid Id, bool Found, string Location, string Chapter, string PageLabel, double? Position);
