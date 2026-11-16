---
name: office-docs
description: "Use when the user calls office-docs, /office-docs or $office-docs, or asks for a Word document, PDF, Excel spreadsheet or slide deck as a file, or to convert between office formats (docx, pdf, xlsx, csv, odt, pptx)."
---

# /office-docs

## Purpose
Produce real office files (Word, PDF, Excel, PowerPoint) and convert between formats, with LibreOffice running headless.

## Find LibreOffice
`C:\Program Files\LibreOffice\program\soffice.exe` (or `where soffice`). If it is missing, tell the user (`winget install TheDocumentFoundation.LibreOffice`); do not download it yourself.

## Run it
Always with a private profile folder inside the project, so it never collides with a LibreOffice the user has open and never writes their AppData (the sandbox cannot):

```
"C:\Program Files\LibreOffice\program\soffice.exe" -env:UserInstallation=file:///Y:/proj/.lo-profile --headless --norestore --convert-to <target> --outdir out <input>
```

- The profile URL uses forward slashes. Add `.lo-profile/` to `.gitignore`.
- The first run creates the profile (about 10 s); later runs are quicker. A line "Could not find platform independent libraries" is harmless.
- One command can convert several inputs (`*.docx`).
- Success prints `convert <in> -> <out> using filter : ...`. Check the output file exists.

## Recipes
| Want | Write | `--convert-to` |
|---|---|---|
| Word document | an HTML file (below) | `"docx:MS Word 2007 XML"` |
| PDF of a document | the HTML, or the .docx | `pdf:writer_web_pdf_Export` (from HTML), `pdf` (from docx/odt) |
| Excel sheet with formulas | a CSV whose cells may be formulas (`=B2*C2`, `=SUM(B2:B3)`) | `xlsx` with `--infilter="CSV:44,34,76,1"` (comma, quote, UTF-8, from row 1) |
| CSV from a sheet | — | `csv` (the first sheet; formulas come out as values) |
| PDF of a deck or sheet | the .pptx / .xlsx | `pdf` |
| Old formats | .doc / .xls / .ppt / .odt | `docx` / `xlsx` / `pptx` |
| Preview page 1 as an image | the .docx / .pdf / .pptx | `png` |

## HTML that converts well to Word
LibreOffice ignores much CSS. What carries over:
- Fonts and colours: a `<style>` block naming elements (`body,p,li,td,th{font-family:Calibri,Arial,sans-serif;font-size:11pt} h1,h2,h3{font-family:Calibri,Arial,sans-serif;color:#1F4E79}`). Style every heading level you use, or it falls back to a serif font.
- Tables: HTML attributes, not CSS: `<table border="1" cellpadding="6" cellspacing="0">`, header colour with `<th bgcolor="#DDEBF7">`. Column widths from CSS are often ignored.
- Page breaks: `style="page-break-before:always"` on the heading that starts the new page.
- Headings `h1`–`h3` become Word's Heading styles (so the user can add a table of contents), lists stay lists, `<b>`/`<i>` stay, `<img src="chart.png">` embeds a picture from the project.
- Set `<title>`: it becomes the document title.

## Slide decks
LibreOffice cannot build a deck from text. If Python has `python-pptx` (`python -c "import pptx"`), write the deck with it (title and bullet layouts, `add_picture` for images), then convert to PDF to check it. If it is missing, ask the user before running `pip install --user python-pptx` (it needs the network). The same goes for `python-docx` and `openpyxl` when a document or sheet needs features HTML or CSV cannot express (comments, tracked changes, charts, several sheets, cell styles).

## Check the result
Never hand over a file you have not looked at: convert page 1 to PNG (`--convert-to png`) and open the image, and for a spreadsheet convert it back to CSV and read the computed values.

## Output
- The files written (paths), how they were made, and what the check showed (a page image, the computed totals).
