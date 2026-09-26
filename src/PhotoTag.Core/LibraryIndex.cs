using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PhotoTag.Core;

public readonly record struct IndexProgress(int Done, int Total);

public sealed record IndexScanResult(int Total, int Updated, int Unchanged, int Removed, int Failed);

/// <summary>Photo and tagged-photo counts for a folder, including its subfolders.</summary>
public readonly record struct FolderCounts(int Photos, int Tagged);

/// <summary>A tag (or person) and how many photos have it, counting every spelling (case variants are one).</summary>
public sealed record KeywordCount(string Keyword, int Count)
{
    /// <summary>Other spellings in use, e.g. "beach" when <see cref="Keyword"/> is "Beach". Rarely any.</summary>
    public IReadOnlyList<string> OtherSpellings { get; init; } = [];
}

/// <summary>One photo as the index knows it: enough for a grid tile, and to sort and group by day.</summary>
public sealed record PhotoSummary
{
    public DateTime? DateTaken { get; init; }
    public bool IsFavourite { get; init; }
    public IReadOnlyList<string> Keywords { get; init; } = [];
    public string? Title { get; init; }
    public string? City { get; init; }
}

/// <summary>What to search for. Keywords and terms must all match (AND), ignoring case.</summary>
public sealed record PhotoQuery
{
    /// <summary>Whole tags only.</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>Whole names only.</summary>
    public IReadOnlyList<string> People { get; init; } = [];

    /// <summary>
    /// Each term matches a whole tag, or appears anywhere in a person's name, the title, description,
    /// place or file name.
    /// </summary>
    public IReadOnlyList<string> Terms { get; init; } = [];

    public bool UntaggedOnly { get; init; }
    public bool FavouritesOnly { get; init; }
}

/// <summary>
/// A SQLite index of every photo under the folders the user has opened: tags, favourites,
/// title and date, so counts, search and suggestions don't need to re-read files. Scans are
/// incremental (files whose size and modified time haven't changed are skipped), and
/// PhotoTag's own edits are written straight in with <see cref="UpdateAsync"/>.
/// </summary>
public sealed class LibraryIndex : IDisposable
{
    private const int SchemaVersion = 3;
    private const int BatchSize = 200;

    // Windows and macOS file systems are case-insensitive by default.
    private static readonly bool IgnoreCase = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
    private static readonly StringComparer PathComparer = IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly string PathCollation = IgnoreCase ? "COLLATE NOCASE" : "";

    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public LibraryIndex(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = true }.ToString();
        CreateSchema();
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoTag", "library.db");

    // --- Scanning --------------------------------------------------------------------------

    /// <summary>
    /// Brings the index up to date with everything under <paramref name="root"/>: new and changed
    /// photos are read, unchanged ones skipped, and photos that no longer exist removed.
    /// </summary>
    public async Task<IndexScanResult> ScanAsync(string root, IProgress<IndexProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        root = NormalizeFolder(root);
        // One entry per photo: a RAW+JPEG pair is indexed once, under its JPEG.
        var files = await Task.Run(() => PhotoFiles.EnumeratePhotosRecursive(root).Select(p => p.Path).ToList(), cancellationToken)
            .ConfigureAwait(false);
        var known = await Task.Run(() => LoadFileStamps(root), cancellationToken).ConfigureAwait(false);

        var seen = new HashSet<string>(files, PathComparer);
        var pending = new ConcurrentQueue<(string Path, PhotoMetadata Metadata, long Size, long Modified)>();
        int done = 0, updated = 0, unchanged = 0, failed = 0;

        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (path, ct) =>
            {
                try
                {
                    var stamp = PhotoFiles.GetStamp(path); // includes a RAW's sidecar
                    if (known.TryGetValue(path, out var existing) && existing == stamp)
                    {
                        Interlocked.Increment(ref unchanged);
                    }
                    else
                    {
                        pending.Enqueue((path, ReadOrEmpty(path), stamp.Size, stamp.Ticks));
                        Interlocked.Increment(ref updated);
                        if (pending.Count >= BatchSize) await FlushAsync(pending, ct).ConfigureAwait(false);
                    }
                }
                catch (IOException)
                {
                    Interlocked.Increment(ref failed); // vanished or locked mid-scan; next scan retries
                }
                progress?.Report(new IndexProgress(Interlocked.Increment(ref done), files.Count));
            }).ConfigureAwait(false);

        await FlushAsync(pending, cancellationToken).ConfigureAwait(false);
        var removed = await RemoveMissingAsync(root, known.Keys.Where(p => !seen.Contains(p)).ToList()).ConfigureAwait(false);
        return new IndexScanResult(files.Count, updated, unchanged, removed, failed);
    }

    /// <summary>Records metadata PhotoTag has just written, without waiting for the next scan.</summary>
    public async Task UpdateAsync(IEnumerable<(string Path, PhotoMetadata Metadata)> photos)
    {
        var rows = photos
            .Where(p => File.Exists(p.Path))
            .Select(p => (p.Path, p.Metadata, Stamp: PhotoFiles.GetStamp(p.Path)))
            .Select(p => (p.Path, p.Metadata, p.Stamp.Size, p.Stamp.Ticks))
            .ToList();
        if (rows.Count > 0) await WriteAsync(rows).ConfigureAwait(false);
    }

    // --- Queries ---------------------------------------------------------------------------

    public Task<FolderCounts> GetFolderCountsAsync(string folder) => Task.Run(() =>
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COUNT(*), COALESCE(SUM(keyword_count > 0), 0) FROM photos WHERE {UnderFolder("folder")}
            """;
        AddFolderParameters(command, NormalizeFolder(folder));
        using var reader = command.ExecuteReader();
        reader.Read();
        return new FolderCounts(reader.GetInt32(0), reader.GetInt32(1));
    });

    /// <summary>Paths of matching photos under <paramref name="root"/>, sorted by folder then name.</summary>
    public Task<IReadOnlyList<string>> SearchAsync(string root, PhotoQuery query) => Task.Run<IReadOnlyList<string>>(() =>
    {
        using var connection = Open();
        using var command = connection.CreateCommand();

        var conditions = new List<string> { UnderFolder("p.folder") };
        foreach (var (field, values) in new[] { (ListField.Tags, query.Keywords), (ListField.People, query.People) })
        {
            var wanted = PhotoMetadataWriter.NormalizeKeywords(values);
            if (wanted.Count == 0) continue;
            var (table, column) = ListTable(field);
            var names = wanted.Select((_, i) => $"@{column}{i}").ToList();
            conditions.Add($"(SELECT COUNT(*) FROM {table} l WHERE l.photo_id = p.id AND l.{column} IN ({string.Join(",", names)})) = {wanted.Count}");
            for (var i = 0; i < wanted.Count; i++) command.Parameters.AddWithValue(names[i], wanted[i]);
        }
        var terms = PhotoMetadataWriter.NormalizeKeywords(query.Terms);
        for (var i = 0; i < terms.Count; i++)
        {
            var name = $"@t{i}";
            conditions.Add($"""
                (EXISTS (SELECT 1 FROM photo_keywords k WHERE k.photo_id = p.id AND k.keyword = {name})
                 OR contains_text(p.title, {name}) OR contains_text(p.description, {name}) OR contains_text(p.file_name, {name})
                 OR contains_text(p.location, {name}) OR contains_text(p.city, {name}) OR contains_text(p.state, {name})
                 OR contains_text(p.country, {name})
                 OR EXISTS (SELECT 1 FROM photo_people pp WHERE pp.photo_id = p.id AND contains_text(pp.name, {name})))
                """);
            command.Parameters.AddWithValue(name, terms[i]);
        }
        if (query.UntaggedOnly) conditions.Add("p.keyword_count = 0");
        if (query.FavouritesOnly)
        {
            conditions.Add("p.rating >= @favouriteRating");
            command.Parameters.AddWithValue("@favouriteRating", PhotoMetadataWriter.FavouriteRating);
        }

        command.CommandText = $"SELECT p.path FROM photos p WHERE {string.Join(" AND ", conditions)} ORDER BY p.folder, p.file_name";
        AddFolderParameters(command, NormalizeFolder(root));

        var results = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) results.Add(reader.GetString(0));
        return results;
    });

    /// <summary>Paths of every favourite under <paramref name="root"/>, so the grid can mark them without reading files.</summary>
    public Task<IReadOnlySet<string>> GetFavouritesAsync(string root) => Task.Run<IReadOnlySet<string>>(() =>
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT path FROM photos WHERE {UnderFolder("folder")} AND rating >= @favouriteRating";
        AddFolderParameters(command, NormalizeFolder(root));
        command.Parameters.AddWithValue("@favouriteRating", PhotoMetadataWriter.FavouriteRating);

        var paths = new HashSet<string>(PathComparer);
        using var reader = command.ExecuteReader();
        while (reader.Read()) paths.Add(reader.GetString(0));
        return paths;
    });

    /// <summary>
    /// What the grid shows on each tile (favourite, tags, title) and sorts and groups by (date taken, city),
    /// for every photo under <paramref name="root"/>: two queries rather than a file read per tile.
    /// </summary>
    public Task<IReadOnlyDictionary<string, PhotoSummary>> GetSummariesAsync(string root) =>
        Task.Run<IReadOnlyDictionary<string, PhotoSummary>>(() =>
        {
            using var connection = Open();
            var folder = NormalizeFolder(root);

            var keywords = new Dictionary<long, List<string>>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"""
                    SELECT k.photo_id, k.keyword FROM photo_keywords k JOIN photos p ON p.id = k.photo_id
                    WHERE {UnderFolder("p.folder")}
                    """;
                AddFolderParameters(command, folder);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetInt64(0);
                    if (!keywords.TryGetValue(id, out var list)) keywords[id] = list = [];
                    list.Add(reader.GetString(1));
                }
            }

            var summaries = new Dictionary<string, PhotoSummary>(PathComparer);
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"SELECT id, path, date_taken, rating, title, city FROM photos WHERE {UnderFolder("folder")}";
                AddFolderParameters(command, folder);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    DateTime? date = reader.IsDBNull(2)
                        ? null
                        : DateTime.TryParse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
                    summaries[reader.GetString(1)] = new PhotoSummary
                    {
                        DateTaken = date,
                        IsFavourite = !reader.IsDBNull(3) && reader.GetInt32(3) >= PhotoMetadataWriter.FavouriteRating,
                        Keywords = keywords.TryGetValue(reader.GetInt64(0), out var list) ? list : [],
                        Title = reader.IsDBNull(4) ? null : reader.GetString(4),
                        City = reader.IsDBNull(5) ? null : reader.GetString(5),
                    };
                }
            }
            return summaries;
        });

    /// <summary>Every value of a place field in the index (every city, say), for suggestions.</summary>
    public Task<IReadOnlyList<string>> GetPlaceValuesAsync(TextField field) => Task.Run<IReadOnlyList<string>>(() =>
    {
        var column = field switch
        {
            TextField.Location => "location",
            TextField.City => "city",
            TextField.State => "state",
            TextField.Country => "country",
            _ => throw new ArgumentOutOfRangeException(nameof(field), "Only place fields are indexed for suggestions."),
        };
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT DISTINCT {column} FROM photos WHERE {column} IS NOT NULL";
        var values = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values;
    });

    /// <summary>Every tag in the index (or under <paramref name="root"/>) with how many photos have it.</summary>
    public Task<IReadOnlyList<KeywordCount>> GetKeywordsAsync(string? root = null) => GetValuesAsync(ListField.Tags, root);

    /// <summary>Every tag or person in the index (or under <paramref name="root"/>) with how many photos have it.</summary>
    public Task<IReadOnlyList<KeywordCount>> GetValuesAsync(ListField field, string? root = null) => Task.Run<IReadOnlyList<KeywordCount>>(() =>
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        var (table, column) = ListTable(field);
        var where = root is null ? "" : $"WHERE {UnderFolder("p.folder")}";
        // Count each exact spelling, then merge case variants in code, keeping the most used spelling.
        command.CommandText = $"""
            SELECT l.{column} COLLATE BINARY AS spelling, COUNT(*) FROM {table} l JOIN photos p ON p.id = l.photo_id
            {where} GROUP BY spelling
            """;
        if (root is not null) AddFolderParameters(command, NormalizeFolder(root));

        var spellings = new List<(string Spelling, int Count)>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) spellings.Add((reader.GetString(0), reader.GetInt32(1)));

        return spellings
            .GroupBy(s => s.Spelling, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var ranked = g.OrderByDescending(s => s.Count).ThenBy(s => s.Spelling, StringComparer.Ordinal).Select(s => s.Spelling).ToList();
                return new KeywordCount(ranked[0], g.Sum(s => s.Count)) { OtherSpellings = ranked[1..] };
            })
            .OrderByDescending(k => k.Count)
            .ThenBy(k => k.Keyword, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    });

    // --- Internals -------------------------------------------------------------------------

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragmas = connection.CreateCommand();
        pragmas.CommandText = "PRAGMA foreign_keys = ON; PRAGMA synchronous = NORMAL;";
        pragmas.ExecuteNonQuery();
        // SQLite's LIKE only ignores case for ASCII; this handles "Café" and "CAFÉ" too.
        connection.CreateFunction("contains_text", (string? text, string term) =>
            text is not null && CultureInfo.InvariantCulture.CompareInfo.IndexOf(text, term, CompareOptions.IgnoreCase) >= 0,
            isDeterministic: true);
        return connection;
    }

    private static (string Table, string Column) ListTable(ListField field) => field switch
    {
        ListField.Tags => ("photo_keywords", "keyword"),
        ListField.People => ("photo_people", "name"),
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    private void CreateSchema()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(command.ExecuteScalar());
        if (version == SchemaVersion) return;
        if (version > SchemaVersion)
            throw new InvalidOperationException($"The photo index was created by a newer version of PhotoTag ({version}).");

        if (version > 0)
        {
            // Upgrades, one version at a time. Marking every photo as changed makes the next scan
            // read the new fields, while counts and search keep working until then.
            var steps = new List<string>();
            if (version < 2)
            {
                steps.Add("""
                    ALTER TABLE photos ADD COLUMN location TEXT;
                    ALTER TABLE photos ADD COLUMN city TEXT;
                    ALTER TABLE photos ADD COLUMN state TEXT;
                    ALTER TABLE photos ADD COLUMN country TEXT;
                    """);
            }
            if (version < 3) steps.Add(PeopleTable);
            command.CommandText = $"""
                {string.Join("\n", steps)}
                UPDATE photos SET modified_ticks = -1;
                PRAGMA user_version = {SchemaVersion};
                """;
            command.ExecuteNonQuery();
            return;
        }

        // Version 0 = new database.
        command.CommandText = $"""
            PRAGMA journal_mode = WAL;
            CREATE TABLE photos (
                id INTEGER PRIMARY KEY,
                path TEXT NOT NULL {PathCollation} UNIQUE,
                folder TEXT NOT NULL {PathCollation},
                file_name TEXT NOT NULL {PathCollation},
                size INTEGER NOT NULL,
                modified_ticks INTEGER NOT NULL,
                date_taken TEXT,
                rating INTEGER,
                title TEXT,
                description TEXT,
                keyword_count INTEGER NOT NULL,
                location TEXT,
                city TEXT,
                state TEXT,
                country TEXT
            );
            CREATE INDEX photos_folder ON photos (folder);
            CREATE TABLE photo_keywords (
                photo_id INTEGER NOT NULL REFERENCES photos (id) ON DELETE CASCADE,
                keyword TEXT NOT NULL COLLATE NOCASE,
                PRIMARY KEY (photo_id, keyword)
            ) WITHOUT ROWID;
            CREATE INDEX photo_keywords_keyword ON photo_keywords (keyword);
            {PeopleTable}
            PRAGMA user_version = {SchemaVersion};
            """;
        command.ExecuteNonQuery();
    }

    private const string PeopleTable = """
        CREATE TABLE IF NOT EXISTS photo_people (
            photo_id INTEGER NOT NULL REFERENCES photos (id) ON DELETE CASCADE,
            name TEXT NOT NULL COLLATE NOCASE,
            PRIMARY KEY (photo_id, name)
        ) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS photo_people_name ON photo_people (name);
        """;

    private Dictionary<string, (long Size, long Ticks)> LoadFileStamps(string root)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT path, size, modified_ticks FROM photos WHERE {UnderFolder("folder")}";
        AddFolderParameters(command, root);

        var stamps = new Dictionary<string, (long, long)>(PathComparer);
        using var reader = command.ExecuteReader();
        while (reader.Read()) stamps[reader.GetString(0)] = (reader.GetInt64(1), reader.GetInt64(2));
        return stamps;
    }

    private async Task FlushAsync(ConcurrentQueue<(string, PhotoMetadata, long, long)> pending, CancellationToken cancellationToken)
    {
        var batch = new List<(string, PhotoMetadata, long, long)>();
        while (pending.TryDequeue(out var row)) batch.Add(row);
        if (batch.Count > 0) await WriteAsync(batch, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteAsync(IReadOnlyList<(string Path, PhotoMetadata Metadata, long Size, long Ticks)> rows,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction();

                using var upsert = connection.CreateCommand();
                upsert.CommandText = """
                    INSERT INTO photos (path, folder, file_name, size, modified_ticks, date_taken, rating, title, description,
                                        keyword_count, location, city, state, country)
                    VALUES (@path, @folder, @name, @size, @ticks, @date, @rating, @title, @description,
                            @count, @location, @city, @state, @country)
                    ON CONFLICT (path) DO UPDATE SET
                        size = excluded.size, modified_ticks = excluded.modified_ticks, date_taken = excluded.date_taken,
                        rating = excluded.rating, title = excluded.title, description = excluded.description,
                        keyword_count = excluded.keyword_count, location = excluded.location, city = excluded.city,
                        state = excluded.state, country = excluded.country
                    RETURNING id
                    """;
                using var clearKeywords = connection.CreateCommand();
                clearKeywords.CommandText = "DELETE FROM photo_keywords WHERE photo_id = @id";
                using var addKeyword = connection.CreateCommand();
                addKeyword.CommandText = "INSERT OR IGNORE INTO photo_keywords (photo_id, keyword) VALUES (@id, @keyword)";
                using var clearPeople = connection.CreateCommand();
                clearPeople.CommandText = "DELETE FROM photo_people WHERE photo_id = @id";
                using var addPerson = connection.CreateCommand();
                addPerson.CommandText = "INSERT OR IGNORE INTO photo_people (photo_id, name) VALUES (@id, @name)";

                foreach (var (path, m, size, ticks) in rows)
                {
                    upsert.Parameters.Clear();
                    upsert.Parameters.AddWithValue("@path", path);
                    upsert.Parameters.AddWithValue("@folder", Path.GetDirectoryName(path) ?? "");
                    upsert.Parameters.AddWithValue("@name", Path.GetFileName(path));
                    upsert.Parameters.AddWithValue("@size", size);
                    upsert.Parameters.AddWithValue("@ticks", ticks);
                    upsert.Parameters.AddWithValue("@date", (object?)m.DateTaken?.ToString("s") ?? DBNull.Value);
                    upsert.Parameters.AddWithValue("@rating", (object?)m.Rating ?? DBNull.Value);
                    upsert.Parameters.AddWithValue("@title", (object?)m.Title ?? DBNull.Value);
                    upsert.Parameters.AddWithValue("@description", (object?)m.Description ?? DBNull.Value);
                    upsert.Parameters.AddWithValue("@count", m.Keywords.Count);
                    upsert.Parameters.AddWithValue("@location", (object?)m.Location ?? DBNull.Value);
                    upsert.Parameters.AddWithValue("@city", (object?)m.City ?? DBNull.Value);
                    upsert.Parameters.AddWithValue("@state", (object?)m.State ?? DBNull.Value);
                    upsert.Parameters.AddWithValue("@country", (object?)m.Country ?? DBNull.Value);
                    var id = Convert.ToInt64(upsert.ExecuteScalar());

                    clearKeywords.Parameters.Clear();
                    clearKeywords.Parameters.AddWithValue("@id", id);
                    clearKeywords.ExecuteNonQuery();
                    foreach (var keyword in m.Keywords)
                    {
                        addKeyword.Parameters.Clear();
                        addKeyword.Parameters.AddWithValue("@id", id);
                        addKeyword.Parameters.AddWithValue("@keyword", keyword);
                        addKeyword.ExecuteNonQuery();
                    }

                    clearPeople.Parameters.Clear();
                    clearPeople.Parameters.AddWithValue("@id", id);
                    clearPeople.ExecuteNonQuery();
                    foreach (var person in m.People)
                    {
                        addPerson.Parameters.Clear();
                        addPerson.Parameters.AddWithValue("@id", id);
                        addPerson.Parameters.AddWithValue("@name", person);
                        addPerson.ExecuteNonQuery();
                    }
                }
                transaction.Commit();
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<int> RemoveMissingAsync(string root, IReadOnlyList<string> missing)
    {
        if (missing.Count == 0) return 0;
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                using var delete = connection.CreateCommand();
                delete.CommandText = "DELETE FROM photos WHERE path = @path";
                var removed = 0;
                foreach (var path in missing)
                {
                    delete.Parameters.Clear();
                    delete.Parameters.AddWithValue("@path", path);
                    removed += delete.ExecuteNonQuery();
                }
                transaction.Commit();
                return removed;
            }).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static PhotoMetadata ReadOrEmpty(string path)
    {
        try
        {
            return PhotoMetadata.Read(path);
        }
        catch (Exception e) when (e is MetadataExtractor.ImageProcessingException or MetadataExtractor.MetadataException
                                      or InvalidDataException)
        {
            return new PhotoMetadata(); // damaged: indexed as untagged so it still counts
        }
        // MetadataExtractor reports truncated data as a plain IOException, the same type as a
        // locked or vanished file. If we can still open the file, the contents are the problem.
        catch (IOException) when (CanOpen(path))
        {
            return new PhotoMetadata();
        }
    }

    private static bool CanOpen(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string NormalizeFolder(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    // "In this folder or below it", as a range so it can use the folder index:
    // folder = root, or root\ <= folder < root] (']' sorts just after '\'; '0' just after '/').
    private static string UnderFolder(string column) =>
        $"({column} = @root OR ({column} >= @rootPrefix AND {column} < @rootPrefixEnd))";

    private static void AddFolderParameters(SqliteCommand command, string root)
    {
        var separator = Path.DirectorySeparatorChar;
        var prefix = root.EndsWith(separator) ? root : root + separator;
        command.Parameters.AddWithValue("@root", root);
        command.Parameters.AddWithValue("@rootPrefix", prefix);
        command.Parameters.AddWithValue("@rootPrefixEnd", prefix[..^1] + (char)(separator + 1));
    }

    public void Dispose()
    {
        _writeGate.Dispose();
        SqliteConnection.ClearAllPools(); // release the file so it can be moved or deleted
    }
}
