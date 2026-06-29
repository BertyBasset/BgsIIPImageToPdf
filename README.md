# BGS Publication Downloader — User Guide

## What It Does

Downloads publications from the British Geological Survey (BGS) website and assembles them into a searchable PDF. The BGS serves its historical publications as tiled images via an IIPImage server — this tool downloads those tiles, stitches them into pages, runs Tesseract OCR to create an invisible text layer, and saves the result as a standard PDF you can search, copy text from and read offline.

Publications produced before 1975 are Crown Copyright expired and are in the public domain.

---

## Requirements

### 1. .NET 8 Runtime
Download from https://dotnet.microsoft.com/download

### 2. Tesseract OCR
Download the Windows installer from:
https://github.com/UB-Mannheim/tesseract/wiki

During installation:
- Make sure **English language data** is selected
- Note the install path — default is `C:\Program Files\Tesseract-OCR`
- Optionally tick **Add to PATH** and **Set TESSDATA_PREFIX environment variable** to avoid needing the `--tessdata` argument

### 3. NuGet Packages (already in the .csproj)
These are restored automatically on first build:
- `PdfSharp` — PDF construction
- `SixLabors.ImageSharp` — image processing and tile stitching
- `Tesseract` — OCR wrapper
- `System.CommandLine` — command line argument handling

---

## Finding a Publication ID

1. Go to https://pubs.bgs.ac.uk/publications.html
2. Search for the publication you want
3. The publication ID appears in the URL when you open it:
   `https://pubs.bgs.ac.uk/publications.html?pubID=B00848`
   The ID here is `B00848`

---

## Basic Usage

```
BgsDownloader.exe <pub-id> [options]
```

### Download a complete publication
```
BgsDownloader.exe B00848
```
Output PDF saved as `B00848.pdf` in the current directory.

### Download with a custom output filename
```
BgsDownloader.exe B00848 --output "Wales Lead Zinc Mines.pdf"
```

### Download a page range only
Useful for testing, or resuming after an interruption:
```
BgsDownloader.exe B00848 --pages 1-20
BgsDownloader.exe B00848 --pages 25
```

### List pages without downloading
Probes the server and reports all available pages and their dimensions without downloading anything:
```
BgsDownloader.exe B00848 --list-pages
```

### Specify Tesseract data path
Only needed if Tesseract is not installed in the default location or `TESSDATA_PREFIX` is not set:
```
BgsDownloader.exe B00848 --tessdata "C:\Program Files\Tesseract-OCR\tessdata"
```

---

## All Options

| Option | Default | Description |
|---|---|---|
| `pub-id` | *(required)* | BGS publication ID e.g. `B00848` |
| `--resolution` | `4` | IIPImage resolution level 0–4. Level 4 is full resolution and gives the best OCR results. Lower levels download faster but reduce OCR quality. |
| `--output` | `{pub-id}.pdf` | Output PDF file path |
| `--pages` | *(all pages)* | Page range to download e.g. `1-50` or `25` |
| `--list-pages` | `false` | Discover and list pages only, do not download |
| `--tessdata` | `C:\Program Files\Tesseract-OCR\tessdata` | Path to Tesseract tessdata directory |

---

## How the PDF Is Built

The tool balances file size against quality and OCR accuracy:

- **Tiles are downloaded** at the requested resolution (default: full resolution, ~1800×2900px per page)
- **OCR runs** on the full resolution image for best accuracy
- **The image saved to the PDF** is downsampled to a target height of 900px and converted to greyscale at JPEG quality 40 — sufficient for comfortable reading while keeping file sizes manageable
- **An invisible text layer** is placed behind each page image, enabling text search and copy-paste in any PDF viewer

Typical output file sizes for a 100-page publication are 4–8MB.

---

## Tips

**Testing before a full run**
Always test a few pages first to confirm the publication downloads correctly:
```
BgsDownloader.exe B00848 --pages 1-5 --output test.pdf
```

**Resuming after interruption**
If a download fails partway through, use `--pages` to resume from where it stopped:
```
BgsDownloader.exe B00848 --pages 45-116 --output B00848_remainder.pdf
```
You can then merge the two PDFs with any PDF tool.

**Higher quality if needed**
If OCR results are poor on a particular publication, the resolution argument is already at maximum (4) by default. Improving OCR on difficult pages is better addressed by upgrading to a better Tesseract language model (`eng.traineddata` from tessdata_best on the Tesseract GitHub) rather than changing the resolution argument.

**Rate limiting**
The tool requests tiles at a polite rate with a 0.1 second delay between requests and 3 retries on failure. Do not remove these delays.

---

## Troubleshooting

**No pages found for [pub-id]**
Check the publication ID is correct by opening the BGS viewer URL in a browser. Some publications may use a different path structure not yet supported.

**Tesseract engine failed to initialise**
The `--tessdata` path is wrong or `eng.traineddata` is missing. Check the Tesseract install directory contains a `tessdata` folder with `eng.traineddata` inside it.

**Download crashes partway through**
Use `--pages` to re-run just the failed section. The server occasionally returns errors on individual tiles — the tool retries 3 times before giving up.

**PDF text search not finding words**
OCR on scanned historical documents is imperfect. Tesseract will miss words on damaged, faded or unusual typefaces. This is expected behaviour — the tool faithfully reports what Tesseract finds without attempting to fill gaps.
