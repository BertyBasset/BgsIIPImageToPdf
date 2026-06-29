using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.CommandLine;
using Tesseract;
using ImageSharpPoint = SixLabors.ImageSharp.Point;

namespace BgsDownloader;

// ── Data types ────────────────────────────────────────────────────────────────

record ImageInfo(int Width, int Height, int TileWidth, int TileHeight, int ResolutionLevels);
record WordPosition(float X, float Y, string Text);

// ── IIPImage client ───────────────────────────────────────────────────────────

class IipClient : IDisposable
{
    private readonly HttpClient _http;
    private const string BaseUrl = "https://pubs.bgs.ac.uk/cgi-bin/iipsrv.fcgi";
    private const int TileSize = 256;
    private const double RateLimit = 0.1;
    private const int MaxRetries = 3;

    public IipClient()
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (compatible; BGS-Publication-Downloader/1.0; research use)");
        _http.DefaultRequestHeaders.Add("Referer",
            "https://pubs.bgs.ac.uk/publications.html");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    private string FifPath(string pubId, int pageNum) =>
        $"/Publications/{pubId}//{pubId}_{pageNum:D4}.jp2";

    /// <summary>Query IIPImage for page metadata.</summary>
    public async Task<ImageInfo?> GetImageInfoAsync(string pubId, int pageNum)
    {
        var fif = FifPath(pubId, pageNum);
        var url = $"{BaseUrl}?FIF={fif}&OBJ=IIP,1.0&OBJ=Max-size&OBJ=Tile-size&OBJ=Resolution-number";

        try
        {
            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;

            var text = await response.Content.ReadAsStringAsync();
            if (!text.Contains("IIP")) return null;

            int width = 0, height = 0, tileW = 256, tileH = 256, levels = 5;

            foreach (var line in text.Split('\n'))
            {
                if (line.StartsWith("Max-size:"))
                {
                    var parts = line[9..].Trim().Split(' ');
                    width = int.Parse(parts[0]);
                    height = int.Parse(parts[1]);
                }
                else if (line.StartsWith("Tile-size:"))
                {
                    var parts = line[10..].Trim().Split(' ');
                    tileW = int.Parse(parts[0]);
                    tileH = int.Parse(parts[1]);
                }
                else if (line.StartsWith("Resolution-number:"))
                {
                    levels = int.Parse(line[18..].Trim());
                }
            }

            return width > 0 ? new ImageInfo(width, height, tileW, tileH, levels) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Calculate image dimensions at a given resolution level.</summary>
    public static (int W, int H) ResolutionDimensions(ImageInfo info, int level)
    {
        int levelsDown = (info.ResolutionLevels - 1) - level;
        double scale = Math.Pow(2, levelsDown);
        return ((int)Math.Ceiling(info.Width / scale),
                (int)Math.Ceiling(info.Height / scale));
    }

    /// <summary>Download a single tile with retry logic.</summary>
    private async Task<byte[]> DownloadTileAsync(string pubId, int pageNum,
                                                   int resolution, int tileIndex)
    {
        var fif = FifPath(pubId, pageNum);
        var url = $"{BaseUrl}?FIF={fif}&JTL={resolution},{tileIndex}";

        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                var bytes = await _http.GetByteArrayAsync(url);
                await Task.Delay(TimeSpan.FromSeconds(RateLimit));
                return bytes;
            }
            catch (Exception ex)
            {
                if (attempt < MaxRetries - 1)
                {
                    Console.Write($" [retry {attempt + 1}]");
                    await Task.Delay(TimeSpan.FromSeconds(2));
                }
                else
                {
                    throw new Exception(
                        $"Failed tile {tileIndex} page {pageNum} after {MaxRetries} attempts: {ex.Message}");
                }
            }
        }
        throw new Exception("Unreachable");
    }

    /// <summary>Download and stitch all tiles for a page into one image.</summary>
    public async Task<Image<Rgb24>> StitchPageAsync(string pubId, int pageNum,
                                                     int resolution, int imgW, int imgH)
    {
        int cols = (int)Math.Ceiling((double)imgW / TileSize);
        int rows = (int)Math.Ceiling((double)imgH / TileSize);
        int totalTiles = cols * rows;

        Console.Write($"    Stitching {cols}×{rows} = {totalTiles} tiles ...");

        var pageImage = new Image<Rgb24>(imgW, imgH, new Rgb24(255, 255, 255));

        for (int tileIdx = 0; tileIdx < totalTiles; tileIdx++)
        {
            int col = tileIdx % cols;
            int row = tileIdx / cols;
            int x = col * TileSize;
            int y = row * TileSize;

            var tileBytes = await DownloadTileAsync(pubId, pageNum, resolution, tileIdx);
            using var tileImage = SixLabors.ImageSharp.Image.Load<Rgb24>(tileBytes);

            pageImage.Mutate(ctx => ctx.DrawImage(tileImage, new ImageSharpPoint(x, y), 1f));

            if ((tileIdx + 1) % 10 == 0)
                Console.Write($" {tileIdx + 1}/{totalTiles}");
        }

        Console.WriteLine(" done");
        return pageImage;
    }

    /// <summary>Probe server to find all page numbers for a publication.</summary>
    public async Task<List<(int PageNum, ImageInfo Info)>> DiscoverPagesAsync(string pubId)
    {
        Console.WriteLine($"  Discovering pages for {pubId} ...");
        var pages = new List<(int, ImageInfo)>();
        int pageNum = 1;
        int consecutiveMisses = 0;

        while (consecutiveMisses < 3)
        {
            var info = await GetImageInfoAsync(pubId, pageNum);
            if (info != null)
            {
                pages.Add((pageNum, info));
                consecutiveMisses = 0;
                Console.WriteLine($"    Found page {pageNum:D4}: {info.Width}×{info.Height}");
            }
            else
            {
                consecutiveMisses++;
            }
            pageNum++;
            await Task.Delay(TimeSpan.FromSeconds(RateLimit));
        }

        Console.WriteLine($"  Found {pages.Count} pages total");
        return pages;
    }

    public void Dispose() => _http.Dispose();
}

// ── OCR ───────────────────────────────────────────────────────────────────────

class OcrProcessor : IDisposable
{
    private readonly TesseractEngine _engine;

    public OcrProcessor(string tessDataPath = "./tessdata")
    {
        _engine = new TesseractEngine(tessDataPath, "eng", EngineMode.Default);
    }

    /// <summary>
    /// Run Tesseract on an ImageSharp image and return word positions
    /// scaled to PDF coordinate space (origin top-left, matching PdfSharp).
    /// </summary>
    public List<WordPosition> GetWordPositions(Image<Rgb24> image,
                                                float pdfWidthPt, float pdfHeightPt)
    {
        var words = new List<WordPosition>();

        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        ms.Position = 0;

        using var pix = Pix.LoadFromMemory(ms.ToArray());
        using var page = _engine.Process(pix);

        float scaleX = pdfWidthPt / image.Width;
        float scaleY = pdfHeightPt / image.Height;

        using var iter = page.GetIterator();
        iter.Begin();

        do
        {
            if (iter.TryGetBoundingBox(PageIteratorLevel.Word, out var bbox))
            {
                var text = iter.GetText(PageIteratorLevel.Word)?.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    // PdfSharp origin is top-left — use Y1 (top of word box), no flip
                    float pdfX = bbox.X1 * scaleX;
                    float pdfY = bbox.Y1 * scaleY;
                    words.Add(new WordPosition(pdfX, pdfY, text));
                }
            }
        }
        while (iter.Next(PageIteratorLevel.Word));

        return words;
    }

    public void Dispose() => _engine.Dispose();
}

// ── PDF builder ───────────────────────────────────────────────────────────────

class PdfBuilder
{
    private const float Dpi = 150f;

    // Target height in pixels for PDF image storage — resolution-independent.
    // OCR always runs on the full downloaded image regardless of this value.
    private const int TargetPdfImageHeight = 900;

    public static async Task BuildAsync(
        string pubId,
        List<(int PageNum, ImageInfo Info)> pages,
        int resolution,
        string outputPath,
        (int Start, int End)? pageRange,
        OcrProcessor ocr)
    {
        if (pageRange.HasValue)
            pages = pages.Where(p => p.PageNum >= pageRange.Value.Start
                                  && p.PageNum <= pageRange.Value.End).ToList();

        Console.WriteLine($"\n  Building PDF: {outputPath}");
        Console.WriteLine($"  Pages to process: {pages.Count}");

        using var client = new IipClient();
        using var pdfDoc = new PdfDocument();
        pdfDoc.Info.Title = pubId;
        pdfDoc.Info.Author = "British Geological Survey";
        pdfDoc.Info.Subject = $"BGS Publication {pubId}";

        for (int i = 0; i < pages.Count; i++)
        {
            var (pageNum, fullInfo) = pages[i];
            Console.WriteLine($"\n  Page {i + 1}/{pages.Count} (file page {pageNum:D4})");

            var (imgW, imgH) = IipClient.ResolutionDimensions(fullInfo, resolution);
            Console.WriteLine($"    Image size at resolution {resolution}: {imgW}×{imgH}px");

            // Download and stitch at requested resolution
            using var pageImage = await client.StitchPageAsync(pubId, pageNum,
                                                                resolution, imgW, imgH);

            // PDF page dimensions at 150 DPI
            float pdfW = (imgW / Dpi) * 72f;
            float pdfH = (imgH / Dpi) * 72f;

            var pdfPage = pdfDoc.AddPage();
            pdfPage.Width = pdfW;
            pdfPage.Height = pdfH;

            using var gfx = XGraphics.FromPdfPage(pdfPage);

            // Target fixed height for PDF storage, preserving aspect ratio.
            // If source is already smaller than target, keep original size — no upsampling.
            int pdfImgH = Math.Min(imgH, TargetPdfImageHeight);
            int pdfImgW = (int)(imgW * ((float)pdfImgH / imgH));

            Console.WriteLine($"    PDF image size: {pdfImgW}×{pdfImgH}px (grayscale, quality 40)");

            using var downsampledImage = pageImage.Clone(ctx => ctx
                .Grayscale()
                .Resize(pdfImgW, pdfImgH));

            using var ms = new MemoryStream();
            downsampledImage.SaveAsJpeg(ms, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder
            {
                Quality = 40
            });
            ms.Position = 0;
            using var xImage = XImage.FromStream(ms);
            gfx.DrawImage(xImage, 0, 0, pdfW, pdfH);

            // OCR at full downloaded resolution, then draw invisible text layer
            Console.Write($"    Running OCR ...");
            try
            {
                var wordPositions = ocr.GetWordPositions(pageImage, pdfW, pdfH);
                Console.WriteLine($" {wordPositions.Count} words found");

                // Invisible text — size 1, white on white
                var font = new XFont("Arial", 1);
                var brush = new XSolidBrush(XColor.FromArgb(1, 255, 255, 255));
                foreach (var word in wordPositions)
                {
                    gfx.DrawString(word.Text, font, brush,
                                   new XPoint(word.X, word.Y));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($" OCR failed: {ex.Message} — image only");
            }

            Console.WriteLine($"    Page {i + 1} complete");
        }

        pdfDoc.Save(outputPath);
        Console.WriteLine($"\n  PDF saved: {outputPath}");
    }
}

// ── Entry point ───────────────────────────────────────────────────────────────

class Program
{
    static void ShowHelp()
    {
        Console.WriteLine(@"
BGS Publication Downloader
==========================

Downloads BGS publications from the IIPImage tile server and builds
a searchable PDF with Tesseract OCR text layer.

Publications produced before 1975 are Crown Copyright expired and
are in the public domain.

FINDING A PUBLICATION ID
  Open the BGS publications page:
    https://pubs.bgs.ac.uk/publications.html
  The ID appears in the URL when you open a publication:
    https://pubs.bgs.ac.uk/publications.html?pubID=B00848
                                                      ^^^^^^
USAGE
  BgsDownloader.exe <pub-id> [options]

EXAMPLES
  Download complete publication:
    BgsDownloader.exe B00848

  Custom output filename:
    BgsDownloader.exe B00848 --output ""Wales Lead Zinc Mines.pdf""

  Test first 5 pages before full run:
    BgsDownloader.exe B00848 --pages 1-5 --output test.pdf

  List available pages without downloading:
    BgsDownloader.exe B00848 --list-pages

  Resume after interruption from page 45:
    BgsDownloader.exe B00848 --pages 45-116

OPTIONS
  pub-id              BGS publication ID e.g. B00848  (required)
  --resolution        IIPImage level 0-4, default 4 (full, best OCR)
  --output            Output PDF path, default {pub-id}.pdf
  --pages             Page range e.g. 1-50 or 25
  --list-pages        Discover pages only, do not download
  --tessdata          Path to Tesseract tessdata directory
                      Default: C:\Program Files\Tesseract-OCR\tessdata

HOW THE PDF IS BUILT
  Tiles downloaded at full resolution (level 4) for best OCR accuracy.
  Each page is saved to PDF at a fixed 900px height, greyscale, JPEG
  quality 40 — giving typical file sizes of 4-8MB per 100 pages.
  This target height is resolution-independent: if a lower --resolution
  level is used the source image is never upsampled beyond its native size.
  An invisible Tesseract OCR text layer enables search and copy-paste.

TROUBLESHOOTING
  No pages found       Check pub-id is correct in the BGS viewer URL
  Tesseract failed     Check --tessdata path contains eng.traineddata
  Download crashed     Use --pages to resume from the failed page
  Poor OCR results     Expected on damaged/unusual typefaces in
                       historical documents — Tesseract reports only
                       what it can reliably identify

For full documentation see BGS_Downloader_Instructions.md
");
    }

    static async Task<int> Main(string[] args)
    {
        // Show full help if no arguments passed
        if (args.Length == 0)
        {
            ShowHelp();
            return 0;
        }

        var pubIdArg = new Argument<string>("pub-id",
            "BGS publication ID e.g. B00848 or B04471");

        var resolutionOpt = new Option<int>(
            "--resolution", () => 4,
            "IIPImage resolution level 0-4 (4 = full resolution, best for OCR)");

        var outputOpt = new Option<string?>(
            "--output", () => null,
            "Output PDF path (default: {pub-id}.pdf)");

        var pagesOpt = new Option<string?>(
            "--pages", () => null,
            "Page range to download e.g. 1-50 or 25");

        var listPagesOpt = new Option<bool>(
            "--list-pages", () => false,
            "Discover and list pages only, do not download");

        var tessDataOpt = new Option<string>(
            "--tessdata",
            () => Environment.GetEnvironmentVariable("TESSDATA_PREFIX")
                  ?? @"C:\Program Files\Tesseract-OCR\tessdata",
            "Path to Tesseract tessdata directory");

        var rootCommand = new RootCommand("BGS IIPImage Publication Downloader")
        {
            pubIdArg, resolutionOpt, outputOpt, pagesOpt, listPagesOpt, tessDataOpt
        };

        rootCommand.SetHandler(async (pubId, resolution, output, pages,
                                       listPages, tessData) =>
        {
            pubId = pubId.ToUpper();
            output ??= $"{pubId}.pdf";

            (int Start, int End)? pageRange = null;
            if (pages != null)
            {
                var parts = pages.Split('-');
                pageRange = parts.Length == 2
                    ? (int.Parse(parts[0]), int.Parse(parts[1]))
                    : (int.Parse(parts[0]), int.Parse(parts[0]));
            }

            Console.WriteLine($"\nBGS Publication Downloader");
            Console.WriteLine($"  Publication : {pubId}");
            Console.WriteLine($"  Resolution  : {resolution} (0=smallest, 4=full)");
            Console.WriteLine($"  Output      : {output}");
            if (pageRange.HasValue)
                Console.WriteLine($"  Page range  : {pageRange.Value.Start}–{pageRange.Value.End}");
            Console.WriteLine();

            using var client = new IipClient();
            var discoveredPages = await client.DiscoverPagesAsync(pubId);

            if (discoveredPages.Count == 0)
            {
                Console.WriteLine($"ERROR: No pages found for {pubId}.");
                return;
            }

            if (listPages)
            {
                Console.WriteLine($"\nPages found for {pubId}:");
                foreach (var (pn, info) in discoveredPages)
                    Console.WriteLine($"  {pn:D4}: {info.Width}×{info.Height}px");
                return;
            }

            using var ocr = new OcrProcessor(tessData);
            await PdfBuilder.BuildAsync(pubId, discoveredPages, resolution,
                                         output, pageRange, ocr);

            Console.WriteLine($"\nDone. Output: {output}");
        },
        pubIdArg, resolutionOpt, outputOpt, pagesOpt, listPagesOpt, tessDataOpt);

        return await rootCommand.InvokeAsync(args);
    }
}
