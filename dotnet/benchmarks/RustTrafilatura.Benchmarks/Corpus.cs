namespace RustTrafilatura.Benchmarks;

/// <summary>One saved page: its name in the corpus and the bytes exactly as the corpus stores them.</summary>
public sealed record Page(string Name, byte[] Html)
{
    /// <summary>The same bytes as the view <see cref="Extractor.ExtractFromBytes"/> takes.</summary>
    public ReadOnlyMemory<byte> Memory => Html;

    public override string ToString() => Name;
}

/// <summary>
/// The pages the benchmarks read: markusmobius/content-extractor-benchmark at a pinned commit, which
/// scripts/corpus.ps1 fetches into dotnet/benchmarks/corpus/.
/// </summary>
/// <remarks>
/// The loader reads the cache and checks it against the pin rather than fetching anything: a benchmark
/// run stays offline and measures extraction, not the network. TRAFILATURA_CORPUS points it at a
/// directory of its own .html pages, in which case the pin does not apply, and
/// TRAFILATURA_CORPUS_PAGES keeps only the first N pages in name order, which is what makes a quick
/// run cheap.
/// </remarks>
internal static class Corpus
{
    /// <summary>The pinned revision: another one is a different corpus with different numbers.</summary>
    private const string Commit = "6bb08131deacc9a32860605018f74991b076518b";

    private const int ExpectedPages = 1057;
    private const long ExpectedBytes = 151009417;

    private static readonly Lazy<IReadOnlyList<Page>> Loaded = new(LoadAll);

    /// <summary>Every page the corpus holds, or the first N in name order when the limit is set.</summary>
    public static IReadOnlyList<Page> Load()
    {
        var pages = Loaded.Value;
        var limit = Environment.GetEnvironmentVariable("TRAFILATURA_CORPUS_PAGES");
        if (!int.TryParse(limit, out var count) || count <= 0 || count >= pages.Count)
        {
            return pages;
        }

        return [.. pages.Take(count)];
    }

    /// <summary>
    /// <paramref name="count"/> pages spread evenly over the corpus's size range, smallest first, for a
    /// table that stays readable. Pages the corpus stores empty are left out: they reach the core as a
    /// too-short payload and would measure the error path instead of an extraction.
    /// </summary>
    public static IReadOnlyList<Page> Sample(int count)
    {
        var pages = Load()
            .Where(static page => page.Html.Length > 0)
            .OrderBy(static page => page.Html.Length)
            .ThenBy(static page => page.Name, StringComparer.Ordinal)
            .ToArray();
        if (pages.Length == 0)
        {
            throw new InvalidOperationException("the corpus holds no page with content");
        }

        if (count <= 1)
        {
            return [pages[pages.Length / 2]];
        }

        if (pages.Length <= count)
        {
            return pages;
        }

        return
        [
            .. Enumerable.Range(0, count).Select(index => pages[(int)((long)index * (pages.Length - 1) / (count - 1))]),
        ];
    }

    private static IReadOnlyList<Page> LoadAll()
    {
        var configured = Environment.GetEnvironmentVariable("TRAFILATURA_CORPUS");
        if (!string.IsNullOrEmpty(configured))
        {
            return Read(configured);
        }

        var directory = PagesDirectory();
        if (Measure(directory) != (ExpectedPages, ExpectedBytes))
        {
            throw new InvalidOperationException(
                $"the corpus in {directory} is missing or does not match the pinned commit {Commit}; " +
                "fetch it with scripts/corpus.ps1 (or scripts/corpus.sh)");
        }

        return Read(directory);
    }

    private static IReadOnlyList<Page> Read(string directory)
    {
        var files = Directory.GetFiles(directory, "*.html");
        if (files.Length == 0)
        {
            throw new InvalidOperationException($"no .html page in {directory}");
        }

        Array.Sort(files, StringComparer.Ordinal);
        return [.. files.Select(static path => new Page(Path.GetFileName(path), File.ReadAllBytes(path)))];
    }

    /// <summary>The checked-out `files/` directory under the repository's own cache, one per commit.</summary>
    private static string PagesDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "dotnet", "RustTrafilatura.sln")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new InvalidOperationException(
                $"the repository root was not found above {AppContext.BaseDirectory}; set TRAFILATURA_CORPUS")
            : Path.Combine(directory.FullName, "dotnet", "benchmarks", "corpus", Commit, "files");
    }

    private static (int Pages, long Bytes) Measure(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return (0, 0);
        }

        var files = Directory.GetFiles(directory, "*.html");
        return (files.Length, files.Sum(static path => new FileInfo(path).Length));
    }
}
