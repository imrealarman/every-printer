# Every Printer

A Windows desktop app to preview, arrange and print documents, in the spirit of priPrinter. Open PDFs, pictures and text files (or print to the built-in virtual printer), then see exactly how the sheets will come out: several pages per sheet, booklets, posters, watermarks, redactions, ink saving. Print them, or save them as one PDF or as pictures.

Everything works offline and nothing leaves your computer.

## Features

### Preview
- Every sheet shown as it will print, with a strip of thumbnails. Zoom, pan, rulers in millimetres, a loupe, and a measuring tool.
- Full screen, and a transparent window mode for lining a preview up over something else.
- Find text in the pages (read with the OCR built into Windows) with matches lit up.
- Ink estimate for the sheet you are viewing, and what ink saving saves.

### Layout
- One page per sheet, or any grid of pages per sheet (up to 12 x 12), in four reading orders.
- **Booklet**: pages in folding order, left or right binding (Persian and Arabic books bind on the right), side-by-side or top-and-bottom fold, and stacks of N sheets for thick booklets.
- **Poster**: spread one page over a grid of sheets with overlap, cut marks and row and column labels.
- **Join**: put pages side by side edge to edge. Smart mode trims empty margins first, which suits Excel tables that run across pages.
- Portrait and landscape sheets, and different paper sizes, in one job. Standard paper sizes or a custom size.
- Margins, gutters, fit, shrink-only, fill, actual size or a custom percentage, and position.
- Page borders, crop marks, and mirror printing.

### Pages
- Open several files into one job, remove, duplicate, move, rotate and insert blank pages. Undo and redo everything.
- Print a page range (`1-5, 8, 10-`), odd or even pages, reverse order, and leave out blank pages.
- Start from a blank page of any paper size (A0 to A6, Letter and others, or a custom size, portrait or landscape), add a picture to it, then drag the picture around the page, make it bigger or smaller, fit it to the page or centre it.
- Crop with the mouse, highlight, add text, and redact. A page with redactions is printed as a picture, so the covered text is really gone.

### Marks
- Text and picture watermarks with any font, size, colour, opacity and angle, placed anywhere or repeated across the page.
- Macros: `{page}` `{pages}` `{sheet}` `{sheets}` `{file}` `{date}` `{time}` `{user}` `{computer}`. One click adds page numbers.
- A letterhead or form (PDF or picture) printed behind every sheet.

### Effects
- Grayscale, invert, brightness, contrast, and ink saving.

### Output
- **Print** with copies, collate, quality, one-sided, automatic two-sided, or manual two-sided (fronts first, then the backs after you turn the stack over).
- **Save as PDF**: sheets made of PDF pages stay sharp vector pages and small; anything that needs pixels is saved as pictures at the quality you choose. Open and owner passwords, and permissions for printing, copying and editing.
- **Save as pictures**: PNG, JPEG, TIFF, BMP or TGA.
- **Themes**: save every setting as a named theme, switch with one pick, import and export as JSON files.
- **Print history** you can search.
- **Virtual printer**: install a printer called "Every Printer" (the installer offers it) and anything you print to it from any program opens here, even when the app is closed. The installer can also add "Print with Every Printer" to the right-click menu of PDFs, pictures and text files.

## Requirements

- Windows 10 (build 17763) or later, 64-bit.
- To build: the .NET 8 SDK and, for the installer, [Inno Setup](https://jrsoftware.org/isinfo.php).
- For the virtual printer: Windows' "Microsoft Print to PDF" feature turned on, and administrator permission once.

## Build and run

```powershell
dotnet build EveryPrinter.csproj -c Release -p:Platform=x64
dotnet run --project EveryPrinter.csproj
dotnet test tests\EveryPrinter.Tests.csproj
```

The app is self-contained, so end users do not need to install .NET or the Windows App SDK.

## Build the installer

```powershell
powershell -ExecutionPolicy Bypass -File build-installer.ps1
```

This publishes a self-contained build to `publish\` and compiles `installer\EveryPrinter.iss` with Inno Setup. The installer is written to `installer\Output\`. It installs for the current user and does not need administrator rights.

## Limits

- Every Printer is a previewer in front of your real printer, not a printer driver. The virtual printer works by saving what you print into a folder (through Windows' own PDF printer) and opening it here.
- PDFs with an open password can be opened here, but sheets made from them are saved as pictures, because they can't be copied as vector pages.
- Text in a PDF can't be edited, only covered, highlighted and annotated.
- There is no 3D page-flip preview.

## Project layout

| Path | What it holds |
|------|---------------|
| `MainWindow.xaml`, `MainWindow*.cs` | The window: core, preview and tools, find, inspector panels, output |
| `Models/PrintSettings.cs` | Every setting, saved as a theme |
| `Models/DocumentSet.cs` | Pages, per-page edits, undo history |
| `Models/Sheet.cs` | Sheets, placements and overlays |
| `Services/LayoutEngine.cs` | Pages and settings into sheets: N-up, booklet, poster, join |
| `Services/PageSource.cs`, `Services/PageCache.cs` | Opening files and rendering pages |
| `Services/SheetRenderer.cs`, `Services/Effects.cs`, `Services/OverlayMath.cs` | Drawing a sheet |
| `Services/PdfExporter.cs`, `Services/ImageExporter.cs`, `Services/PrintService.cs` | Saving and printing |
| `Services/PageAnalyzer.cs`, `Services/OcrService.cs` | Blank pages, content edges, text search |
| `tests/` | Unit tests for layout and the render and export pipeline |
| `installer/` | Inno Setup script |

## Author

Arman Soltani
