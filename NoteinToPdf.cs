using Avalonia.Controls;
using Microsoft.Data.Sqlite;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NoteinDesktopViewer
{
    internal class NoteinToPdf
    {
        private const double A4_WIDTH_PT = 595.28;
        private const double A4_HEIGHT_PT = 841.89;
        private const double DEFAULT_NOTEIN_WIDTH = 1240.0;
        private const double DEFAULT_NOTEIN_HEIGHT = 1754.0;
        private static readonly byte[] SQLITE_MAGIC = Encoding.ASCII.GetBytes("SQLite format 3\0");
        private static readonly byte[] INK_MAGIC_BYTES = Encoding.ASCII.GetBytes("NIPB");
        private static readonly string tempdir = Path.Combine(Path.GetTempPath(), "NoteinToPdf");


        private struct PointData
        {
            public double X { get; set; }
            public double Y { get; set; }
            public double Pressure { get; set; }
        }


        private struct StrokeRecord
        {
            public int Color { get; set; }
            public double Width { get; set; }
            public List<PointData> Points { get; set; }
        }

        private static (double r, double g, double b, double a) AndroidColorToRgba(long colorVal)
        {
            uint unsigned;
            if (colorVal > 0xFFFFFFFF)
                unsigned = (uint)((colorVal >> 32) & 0xFFFFFFFF);
            else
                unsigned = (uint)(colorVal & 0xFFFFFFFF);

            double a = ((unsigned >> 24) & 0xFF) / 255.0;
            double r = ((unsigned >> 16) & 0xFF) / 255.0;
            double g = ((unsigned >> 8) & 0xFF) / 255.0;
            double b = (unsigned & 0xFF) / 255.0;
            return (r, g, b, a);
        }

        private static (ulong value, int newPos) DecodeVarint(byte[] data, int pos)
        {
            ulong result = 0;
            int shift = 0;
            while (pos < data.Length)
            {
                byte b = data[pos];
                result |= (ulong)(b & 0x7F) << shift;
                pos++;
                if ((b & 0x80) == 0)
                    break;
                shift += 7;
            }
            return (result, pos);
        }

        private static double[] AndroidMult(double[] a, double[] b)
        {
            var res = new double[9];
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                {
                    double sum = 0;
                    for (int k = 0; k < 3; k++)
                        sum += a[r * 3 + k] * b[k * 3 + c];
                    res[r * 3 + c] = sum;
                }
            return res;
        }

        private static (double x, double y) AndroidMap(double[] m, double x, double y)
        {
            double d = m[6] * x + m[7] * y + m[8];
            if (d == 0) d = 1.0;
            double nx = (m[0] * x + m[1] * y + m[2]) / d;
            double ny = (m[3] * x + m[4] * y + m[5]) / d;
            return (nx, ny);
        }

        private static StrokeRecord ParseInkStrokeProto(byte[] data)
        {
            var s2w = new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
            var w2v = new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
            var rawXy = new List<float>();
            var inputAttrs = new List<float>();
            double brushSize = 3.0;
            ulong brushColor = 0xFF000000;

            int pos = 0;
            while (pos < data.Length)
            {
                ulong tag;
                try
                {
                    (tag, pos) = DecodeVarint(data, pos);
                }
                catch { break; }
                int fieldNumber = (int)(tag >> 3);
                int wireType = (int)(tag & 7);

                if (wireType == 0) // Varint
                {
                    ulong val;
                    (val, pos) = DecodeVarint(data, pos);
                    if (fieldNumber == 5)
                    {
                        if (val > 0xFFFFFFFF)
                            brushColor = (val >> 32) & 0xFFFFFFFF;
                        else
                            brushColor = val & 0xFFFFFFFF;
                    }
                }
                else if (wireType == 5) // 32-bit float
                {
                    if (pos + 4 <= data.Length)
                    {
                        float val = BitConverter.ToSingle(data, pos);
                        pos += 4;
                        if (fieldNumber == 4) brushSize = val;
                    }
                    else break;
                }
                else if (wireType == 1) // 64-bit
                {
                    pos += 8;
                }
                else if (wireType == 2) // Length-delimited
                {
                    ulong length;
                    (length, pos) = DecodeVarint(data, pos);
                    if (pos + (int)length > data.Length) break;
                    byte[] chunk = data[pos..(pos + (int)length)];
                    pos += (int)length;

                    if (fieldNumber == 10) // input_xy
                    {
                        int n = chunk.Length / 4;
                        rawXy.Clear();
                        for (int i = 0; i < n; i++)
                            rawXy.Add(BitConverter.ToSingle(chunk, i * 4));
                    }
                    else if (fieldNumber == 11) // input_attrs
                    {
                        int n = chunk.Length / 4;
                        inputAttrs.Clear();
                        for (int i = 0; i < n; i++)
                            inputAttrs.Add(BitConverter.ToSingle(chunk, i * 4));
                    }
                    else if (fieldNumber == 12) // stroke_to_world
                    {
                        if (chunk.Length == 36)
                        {
                            s2w = new double[9];
                            for (int i = 0; i < 9; i++)
                                s2w[i] = BitConverter.ToSingle(chunk, i * 4);
                        }
                    }
                    else if (fieldNumber == 13) // world_to_view
                    {
                        if (chunk.Length == 36)
                        {
                            w2v = new double[9];
                            for (int i = 0; i < 9; i++)
                                w2v[i] = BitConverter.ToSingle(chunk, i * 4);
                        }
                    }
                }
                else
                {
                    break;
                }
            }

            var concat = AndroidMult(w2v, s2w);
            var points = new List<PointData>();
            int pointCount = rawXy.Count / 2;
            for (int i = 0; i < pointCount; i++)
            {
                double rawX = rawXy[i * 2];
                double rawY = rawXy[i * 2 + 1];
                (double x, double y) = AndroidMap(concat, rawX, rawY);
                double pressure = 0.5;
                if (i * 5 + 1 < inputAttrs.Count)
                    pressure = inputAttrs[i * 5 + 1];
                points.Add(new PointData { X = x, Y = y, Pressure = pressure });
            }

            return new StrokeRecord
            {
                Color = (int)brushColor,
                Width = brushSize,
                Points = points
            };
        }

        private static FileInfo FindSqliteDb(DirectoryInfo directory)
        {
            var candidates = directory.GetFiles("*.sqlite").ToList();
            if (candidates.Count == 0)
            {
                foreach (var file in directory.GetFiles())
                {
                    try
                    {
                        using var fs = file.OpenRead();
                        byte[] header = new byte[16];
                        fs.ReadExactly(header, 0, 16);
                        if (header.SequenceEqual(SQLITE_MAGIC))
                            candidates.Add(file);
                    }
                    catch { }
                }
            }

            if (candidates.Count == 0)
                throw new FileNotFoundException($"No SQLite database found in {directory.FullName}");
            return candidates[0];
        }

        private static string GetNoteTitle(DirectoryInfo directory)
        {
            var metaFile = Path.Combine(directory.FullName, "note_meta.json");
            if (File.Exists(metaFile))
            {
                try
                {
                    var json = File.ReadAllText(metaFile);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("title", out var title))
                        return title.GetString();
                }
                catch { }
            }
            return "Notein Note";
        }

        private static (double x, double y) ToPdfCoords(double x, double y, double scaleX, double scaleY)
        {
            return (x * scaleX, y * scaleY);
        }

        private static void DrawGridBackground(XGraphics gfx, JsonElement paperTheme, double pageW, double pageH,
                                       double scaleX, double scaleY, double canvasW, double canvasH)
        {
            // Background color
            long bgColor = -1;
            if (paperTheme.TryGetProperty("baseTheme", out var baseTheme) &&
                baseTheme.TryGetProperty("color", out var colorProp))
                bgColor = colorProp.GetInt64();

            var (r, g, b, a) = AndroidColorToRgba(bgColor);
            gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb((int)(a * 255), (int)(r * 255), (int)(g * 255), (int)(b * 255))),
                              0, 0, pageW, pageH);

            // Grid lines
            if (paperTheme.TryGetProperty("paperStyle", out var style) &&
                style.TryGetProperty("type", out var typeProp))
            {
                string styleType = typeProp.GetString() ?? "";
                if (styleType.Contains("Square") || styleType.Contains("Grid"))
                {
                    double spacing = style.TryGetProperty("requiredItemSpace", out var sp) ? sp.GetDouble() : 53.125;
                    double leftPad = style.TryGetProperty("leftPadding", out var lp) ? lp.GetDouble() : 38.25;
                    double rightPad = style.TryGetProperty("rightPadding", out var rp) ? rp.GetDouble() : 38.25;
                    double topPad = style.TryGetProperty("topPadding", out var tp) ? tp.GetDouble() : 19.125;
                    double bottomPad = style.TryGetProperty("bottomPadding", out var bp) ? bp.GetDouble() : 19.125;
                    double fgAlpha = style.TryGetProperty("foregroundAlpha", out var fa) ? fa.GetDouble() : 1.0;

                    var pen = new XPen(XColor.FromArgb((int)(fgAlpha * 0.5 * 255), 191, 191, 191), 0.3);

                    double x = leftPad;
                    while (x <= canvasW - rightPad)
                    {
                        double px = x * scaleX;
                        double pyTop = topPad * scaleY;
                        double pyBot = (canvasH - bottomPad) * scaleY;
                        gfx.DrawLine(pen, px, pyTop, px, pyBot);
                        x += spacing;
                    }

                    double y = topPad;
                    while (y <= canvasH - bottomPad)
                    {
                        double py = y * scaleY;
                        double pxLeft = leftPad * scaleX;
                        double pxRight = (canvasW - rightPad) * scaleX;
                        gfx.DrawLine(pen, pxLeft, py, pxRight, py);
                        y += spacing;
                    }
                }
            }
        }

        private static void DrawStroke(XGraphics gfx, StrokeRecord stroke, double pageH, double scaleX, double scaleY)
        {
            if (stroke.Points.Count < 2) return;

            var (r, g, b, a) = AndroidColorToRgba(stroke.Color);
            double baseWidth = stroke.Width;

            XColor color = XColor.FromArgb((int)(a * 255), (int)(r * 255), (int)(g * 255), (int)(b * 255));
            var pen = new XPen(color, 1)
            {
                LineCap = XLineCap.Round,
                LineJoin = XLineJoin.Round
            }; // width set per segment

            for (int i = 0; i < stroke.Points.Count - 1; i++)
            {
                var p0 = stroke.Points[i];
                var p1 = stroke.Points[i + 1];
                double pressure = (p0.Pressure + p1.Pressure) / 2.0;
                double width = baseWidth * Math.Max(pressure, 0.2) * scaleX;
                pen.Width = width;

                double x0 = p0.X * scaleX;
                double y0 = p0.Y * scaleY;
                double x1 = p1.X * scaleX;
                double y1 = p1.Y * scaleY;
                gfx.DrawLine(pen, x0, y0, x1, y1);
            }
        }

        private static void DrawShape(XGraphics gfx, Dictionary<string, object> shapeRow, double pageH, double scaleX, double scaleY)
        {
            long color = shapeRow.TryGetValue("color", out var c) ? Convert.ToInt64(c) : 0xFF000000;
            double width = (shapeRow.TryGetValue("width", out var w) ? Convert.ToDouble(w) : 3.0) * scaleX;
            string? pointsStr = shapeRow.TryGetValue("points", out var p) ? p as string : null;
            if (string.IsNullOrEmpty(pointsStr)) return;

            List<PointData> points;
            try
            {
                points = JsonSerializer.Deserialize<List<PointData>>(pointsStr, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch { return; }
            if (points.Count < 2) return;

            var (r, g, b, a) = AndroidColorToRgba(color);
            var pen = new XPen(XColor.FromArgb((int)(a * 255), (int)(r * 255), (int)(g * 255), (int)(b * 255)), width)
            {
                LineCap = XLineCap.Round,
                LineJoin = XLineJoin.Round
            };

            int shapeType = shapeRow.TryGetValue("type", out var t) ? Convert.ToInt32(t) : 0;

            if (shapeType == 7 && points.Count >= 2)
            {
                // Straight line
                double x0 = points[0].X * scaleX, y0 = points[0].Y * scaleY;
                double x1 = points[1].X * scaleX, y1 = points[1].Y * scaleY;
                gfx.DrawLine(pen, x0, y0, x1, y1);
            }
            else
            {
                var path = new XGraphicsPath();
                path.StartFigure();
                path.AddLine(points[0].X * scaleX, points[0].Y * scaleY, points[0].X * scaleX, points[0].Y * scaleY);
                for (int i = 1; i < points.Count; i++)
                {
                    path.AddLine(points[i - 1].X * scaleX, points[i - 1].Y * scaleY,
                                 points[i].X * scaleX, points[i].Y * scaleY);
                }
                gfx.DrawPath(pen, path);
            }
        }

        private static void DrawImage(XGraphics gfx, Dictionary<string, object> imgRow, DirectoryInfo noteDir, double pageH, double scaleX, double scaleY)
        {
            string? imgId = imgRow.TryGetValue("id", out var id) ? id as string : "";
            string? uri = imgRow.TryGetValue("uri", out var u) ? u as string : "";

            // Search for image file
            string[] candidates =
            {
                Path.Combine(noteDir.FullName, $"note_image_{imgId}.png"),
                Path.Combine(noteDir.FullName, $"note_image_{imgId}.jpg"),
                Path.Combine(noteDir.FullName, Path.GetFileName(uri))
            };
            string imgFile = candidates.FirstOrDefault(File.Exists);
            if (imgFile == null)
            {
                foreach (var f in noteDir.GetFiles("*.png"))
                    if (f.Name.Contains(imgId))
                    {
                        imgFile = f.FullName;
                        break;
                    }
            }
            if (imgFile == null) return;

            double left = imgRow.TryGetValue("left", out var l) ? Convert.ToDouble(l) : 0;
            double top = imgRow.TryGetValue("top", out var tp) ? Convert.ToDouble(tp) : 0;
            double right = imgRow.TryGetValue("right", out var rt) ? Convert.ToDouble(rt) : 0;
            double bottom = imgRow.TryGetValue("bottom", out var bm) ? Convert.ToDouble(bm) : 0;

            double pxLeft = left * scaleX;
            double pyTop = top * scaleY;
            double pxRight = right * scaleX;
            double pyBottom = bottom * scaleY;
            double w = pxRight - pxLeft;
            double h = pyBottom - pyTop;

            try
            {
                using var img = XImage.FromFile(imgFile);
                gfx.DrawImage(img, pxLeft, pyTop, w, h);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    Warning: Could not draw image {Path.GetFileName(imgFile)}: {ex.Message}");
            }
        }

        private static (FileInfo pdf, int pageIndex) FindBackgroundPdf(DirectoryInfo noteDir, JsonElement paperTheme)
        {
            if (!paperTheme.TryGetProperty("type", out var typeProp) ||
                !typeProp.GetString()?.Contains("PdfPaperTheme") == true)
                return (null, 0);

            string pdfPath = "";
            if (paperTheme.TryGetProperty("pdfInfo", out var pdfInfo) &&
                pdfInfo.TryGetProperty("pdfPath", out var pathProp))
                pdfPath = pathProp.GetString();

            int pageNum = paperTheme.TryGetProperty("pageNum", out var pg) ? pg.GetInt32() : 0;

            string targetName = Path.GetFileName(pdfPath);
            var candidates = new List<string>
            {
                Path.Combine(noteDir.FullName, targetName),
                Path.Combine(noteDir.FullName, $"note_local_uri_theme_{targetName}")
            };

            foreach (var c in candidates)
                if (File.Exists(c))
                    return (new FileInfo(c), pageNum);

            // Fallback: any pdf file
            var pdfFiles = noteDir.GetFiles("*.pdf");
            if (pdfFiles.Length > 0)
                return (pdfFiles[0], pageNum);

            return (null, 0);
        }

        private static Dictionary<string, object> ReadRow(SqliteDataReader reader)
        {
            var dict = new Dictionary<string, object>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                string name = reader.GetName(i);
                object value = reader.GetValue(i);
                dict[name] = value is DBNull ? null : value;
            }
            return dict;
        }

        private static string SanitizeFilename(string name)
        {
            var cleaned = Regex.Replace(name, @"[\\/*?:""><|]", "_").Trim();
            return string.IsNullOrEmpty(cleaned) ? "Note" : cleaned;
        }

        private static bool IsSqliteFile(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                byte[] header = new byte[16];
                fs.ReadExactly(header, 0, 16);
                return header.SequenceEqual(SQLITE_MAGIC);
            }
            catch { return false; }
        }


        private static void ConvertNoteToPdf(DirectoryInfo noteDir, string outputPdfPath)
        {
            var dbFile = FindSqliteDb(noteDir);
            string noteTitle = GetNoteTitle(noteDir);

            using var conn = new SqliteConnection($"Data Source={dbFile.FullName};Mode=ReadOnly;");
            conn.Open();


            var pageIds = new List<string>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT page_list FROM NoteContentEntity";
                using var reader = cmd.ExecuteReader();
                if (reader.Read() && !reader.IsDBNull(0))
                {
                    string pageListJson = reader.GetString(0);
                    using var doc = JsonDocument.Parse(pageListJson);
                    foreach (var item in doc.RootElement.EnumerateArray())
                        pageIds.Add(item.GetString() ?? "");
                }
            }

            if (pageIds.Count == 0)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT id FROM PageEntity WHERE is_in_trash_bin = 0 ORDER BY source DESC";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    pageIds.Add(reader.GetString(0));
            }


            var pages = new Dictionary<string, Dictionary<string, object>>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT * FROM PageEntity";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var row = ReadRow(reader);
                    string id = row["id"].ToString() ?? "";
                    pages[id] = row;
                }
            }

            var strokesByPage = new Dictionary<string, List<StrokeRecord>>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT * FROM StrokeEntity ORDER BY creation_time";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var row = ReadRow(reader);
                    string pageId = row["page_id"].ToString() ?? "";
                    StrokeRecord? parsed = null;

                    if (row.TryGetValue("record_json", out var rj) && rj != null)
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(rj.ToString());
                            var points = new List<PointData>();
                            if (doc.RootElement.TryGetProperty("points", out var pts))
                            {
                                foreach (var pt in pts.EnumerateArray())
                                {
                                    points.Add(new PointData
                                    {
                                        X = pt.GetProperty("x").GetDouble(),
                                        Y = pt.GetProperty("y").GetDouble(),
                                        Pressure = pt.TryGetProperty("p", out var p) ? p.GetDouble() : 0.5
                                    });
                                }
                            }
                            parsed = new StrokeRecord
                            {
                                Color = doc.RootElement.TryGetProperty("color", out var col) ? col.GetInt32() : -16777216,
                                Width = doc.RootElement.TryGetProperty("width", out var w) ? w.GetDouble() : 3.0,
                                Points = points
                            };
                        }
                        catch { }
                    }
                    else if (row.TryGetValue("ink_stroke_blob", out var blob) && blob is byte[] blobBytes)
                    {
                        try { parsed = ParseInkStrokeProto(blobBytes); }
                        catch (Exception ex) { Console.WriteLine($"    Warning: Error parsing ink_stroke_blob: {ex.Message}"); }
                    }
                    else if (row.TryGetValue("ink_stroke_json", out var jsonStr) && jsonStr != null)
                    {
                        try
                        {
                            byte[] data = Convert.FromBase64String(jsonStr.ToString());
                            if (data.AsSpan().StartsWith(INK_MAGIC_BYTES))
                                data = data[INK_MAGIC_BYTES.Length..];
                            parsed = ParseInkStrokeProto(data);
                        }
                        catch (Exception ex) { Console.WriteLine($"    Warning: Error parsing ink_stroke_json: {ex.Message}"); }
                    }

                    if (parsed != null)
                    {
                        if (!strokesByPage.ContainsKey(pageId)) strokesByPage[pageId] = new List<StrokeRecord>();
                        strokesByPage[pageId].Add((StrokeRecord)parsed);
                    }
                }
            }

            var shapesByPage = new Dictionary<string, List<Dictionary<string, object>>>();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT * FROM ShapeEntity ORDER BY creation_time";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var row = ReadRow(reader);
                    string pageId = row["page_id"].ToString() ?? "";
                    if (!shapesByPage.ContainsKey(pageId)) shapesByPage[pageId] = new List<Dictionary<string, object>>();
                    shapesByPage[pageId].Add(row);
                }
            }
            catch (SqliteException) { /* table may not exist */ }

            var imagesByPage = new Dictionary<string, List<Dictionary<string, object>>>();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT * FROM ImageEntity ORDER BY creation_time";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var row = ReadRow(reader);
                    string pageId = row["page_id"].ToString() ?? "";
                    if (!imagesByPage.ContainsKey(pageId)) imagesByPage[pageId] = new List<Dictionary<string, object>>();
                    imagesByPage[pageId].Add(row);
                }
            }
            catch (SqliteException) { }

            conn.Close();

            CreatePDF(noteTitle, pageIds, pages, noteDir, imagesByPage, shapesByPage, strokesByPage, outputPdfPath);

        }

        private static void CreatePDF(string title, List<string> pageIds, Dictionary<string, Dictionary<string, object>> pages, DirectoryInfo noteDir, Dictionary<string, List<Dictionary<string, object>>> imagesByPage, Dictionary<string, List<Dictionary<string, object>>> shapesByPage, Dictionary<string, List<StrokeRecord>> strokesByPage, string outputPdf)
        {
            using var document = new PdfDocument();
            document.Info.Title = title;

            for (int i = 0; i < pageIds.Count; i++)
            {
                string pageId = pageIds[i];
                var pageInfo = pages.GetValueOrDefault(pageId, new Dictionary<string, object>());
                string? paperSpecStr = pageInfo.TryGetValue("paper_spec", out var ps) ? ps as string : null;
                double canvasW = DEFAULT_NOTEIN_WIDTH, canvasH = DEFAULT_NOTEIN_HEIGHT;
                if (!string.IsNullOrEmpty(paperSpecStr))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(paperSpecStr);
                        if (doc.RootElement.TryGetProperty("width", out var w)) canvasW = w.GetDouble();
                        if (doc.RootElement.TryGetProperty("height", out var h)) canvasH = h.GetDouble();
                    }
                    catch { }
                }

                double pageW = A4_WIDTH_PT, pageH = A4_HEIGHT_PT;
                FileInfo? bgPdfFile = null;
                int bgPageIndex = 0;
                JsonElement paperTheme = default;
                if (pageInfo.TryGetValue("paper_theme", out var pt) && pt != null)
                {
                    using var doc = JsonDocument.Parse(pt.ToString());
                    paperTheme = doc.RootElement.Clone();
                    (bgPdfFile, bgPageIndex) = FindBackgroundPdf(noteDir, paperTheme);
                    if (bgPdfFile != null)
                    {
                        // Get page size from background PDF
                        using var bgDoc = PdfReader.Open(bgPdfFile.FullName, PdfDocumentOpenMode.Import);
                        var bgPage = bgDoc.Pages[Math.Min(bgPageIndex, bgDoc.PageCount - 1)];
                        pageW = bgPage.Width.Point;
                        pageH = bgPage.Height.Point;
                    }
                }

                double scaleX = pageW / canvasW;
                double scaleY = pageH / canvasH;

                var page = document.AddPage();
                page.Width = pageW;
                page.Height = pageH;
                using var gfx = XGraphics.FromPdfPage(page);

                if (bgPdfFile != null)
                {
                    try
                    {
                        // Loads the first page of the background PDF
                        var form = XPdfForm.FromFile(bgPdfFile.FullName);
                        gfx.DrawImage(form, 0, 0, pageW, pageH);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"    Warning: Could not merge template PDF: {ex.Message}");
                        // Fallback to grid background
                        DrawGridBackground(gfx, paperTheme, pageW, pageH, scaleX, scaleY, canvasW, canvasH);
                    }
                }
                else
                {
                    DrawGridBackground(gfx, paperTheme, pageW, pageH, scaleX, scaleY, canvasW, canvasH);
                }

                if (imagesByPage.TryGetValue(pageId, out var images))
                    foreach (var img in images)
                        DrawImage(gfx, img, noteDir, pageH, scaleX, scaleY);

                // Shapes
                if (shapesByPage.TryGetValue(pageId, out var shapes))
                    foreach (var shape in shapes)
                        DrawShape(gfx, shape, pageH, scaleX, scaleY);

                // Strokes
                if (strokesByPage.TryGetValue(pageId, out var strokes))
                    foreach (var stroke in strokes)
                        DrawStroke(gfx, stroke, pageH, scaleX, scaleY);

                Console.WriteLine($"  Page {i + 1}/{pageIds.Count}: {(strokes?.Count ?? 0)} strokes, {(shapes?.Count ?? 0)} shapes, {(images?.Count ?? 0)} images" +
                                  (bgPdfFile != null ? $" (template PDF: {bgPdfFile.Name})" : ""));
            }

            document.Save(outputPdf);
        }




        public static bool ConvertSingleNote(FileInfo source, string output)
        {
            string noteTempDir = Path.Combine(tempdir, Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(noteTempDir);
            try
            {
                Console.WriteLine($"\nExtracting {source.Name}...");
                ZipFile.ExtractToDirectory(source.FullName, noteTempDir);
                var tempPath = new DirectoryInfo(noteTempDir);
                // Find sqlite file recursively
                var sqliteFiles = tempPath.GetFiles("*", SearchOption.AllDirectories)
                                         .Where(f => IsSqliteFile(f.FullName))
                                         .ToList();
                if (sqliteFiles.Count > 0)
                {
                    ConvertNoteToPdf(sqliteFiles[0].Directory, output);
                    return true;
                }
                else
                {
                    Console.Error.WriteLine($"Error: No SQLite database found inside {source.Name}");
                    return false;
                }
            }
            finally
            {
                try { Directory.Delete(noteTempDir, true); } catch { }
            }
        }
    }
}
