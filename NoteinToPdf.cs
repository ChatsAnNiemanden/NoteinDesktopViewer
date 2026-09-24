using Avalonia.Controls;
using Microsoft.Data.Sqlite;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
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

        static NoteinToPdf()
        {
            if (GlobalFontSettings.FontResolver == null)
            {
                GlobalFontSettings.FontResolver = new NoteinFontResolver();
            }
        }


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
            public string BrushType { get; set; }
        }

        private static (double r, double g, double b, double a) AndroidColorToRgba(long colorVal, double darkenFactor = 1.0)
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

            if (darkenFactor != 1.0)
            {
                r = Math.Pow(r, darkenFactor);
                g = Math.Pow(g, darkenFactor);
                b = Math.Pow(b, darkenFactor);
            }

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
            string brushType = "notein-ballpoint-v1";

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

                    if (fieldNumber == 7) // brush type name
                    {
                        try { brushType = Encoding.UTF8.GetString(chunk); } catch { }
                    }
                    else if (fieldNumber == 10) // input_xy
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
                Points = points,
                BrushType = brushType
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

        private static void DrawStroke(XGraphics gfx, StrokeRecord stroke, double pageH, double scaleX, double scaleY, double darkenFactor)
        {
            if (stroke.Points.Count < 2) return;

            var (r, g, b, a) = AndroidColorToRgba(stroke.Color, darkenFactor);
            double baseWidth = stroke.Width;
            double scale = Math.Sqrt(scaleX * scaleY);
            bool isPencil = stroke.BrushType != null && stroke.BrushType.Contains("pencil");
            bool isHighlighter = stroke.BrushType != null && (stroke.BrushType.Contains("highlighter") || stroke.BrushType.Contains("marker"));
            bool isTape = stroke.BrushType != null && stroke.BrushType.Contains("tape");

            var pts = stroke.Points.Select(p => new XPoint(p.X * scaleX, p.Y * scaleY)).ToList();

            if (isPencil)
                DrawPencilStroke(gfx, stroke, pts, r, g, b, a, baseWidth, scale);
            else if (isHighlighter)
                DrawHighlighterStroke(gfx, stroke, pts, r, g, b, a, baseWidth, scale);
            else if (isTape)
                DrawTapeStroke(gfx, stroke, pts, r, g, b, a, baseWidth, scale);
            else
                DrawBallpointStroke(gfx, stroke, pts, r, g, b, a, baseWidth, scale);
        }

        private static void DrawHighlighterStroke(XGraphics gfx, StrokeRecord stroke, List<XPoint> pts,
            double r, double g, double b, double a, double baseWidth, double scale)
        {
            // Text markers/highlighters are semi-transparent (~48% opacity)
            double highlighterAlpha = Math.Clamp(a * 0.48, 0.05, 0.70);
            XColor color = XColor.FromArgb((int)(highlighterAlpha * 255), (int)(r * 255), (int)(g * 255), (int)(b * 255));
            double strokeWidth = baseWidth * scale;

            var pen = new XPen(color, strokeWidth)
            {
                LineCap = XLineCap.Round,
                LineJoin = XLineJoin.Round
            };

            var path = new XGraphicsPath();
            path.AddLines(pts.ToArray());
            gfx.DrawPath(pen, path);
        }

        private static void DrawTapeStroke(XGraphics gfx, StrokeRecord stroke, List<XPoint> pts,
            double r, double g, double b, double a, double baseWidth, double scale)
        {
            // Study tape is 100% opaque
            XColor color = XColor.FromArgb(255, (int)(r * 255), (int)(g * 255), (int)(b * 255));
            double strokeWidth = baseWidth * scale;

            var pen = new XPen(color, strokeWidth)
            {
                LineCap = XLineCap.Round,
                LineJoin = XLineJoin.Round
            };

            var path = new XGraphicsPath();
            path.AddLines(pts.ToArray());
            gfx.DrawPath(pen, path);
        }

        private static void DrawBallpointStroke(XGraphics gfx, StrokeRecord stroke, List<XPoint> pts,
            double r, double g, double b, double a, double baseWidth, double scale)
        {
            XColor color = XColor.FromArgb((int)(a * 255), (int)(r * 255), (int)(g * 255), (int)(b * 255));
            var pen = new XPen(color, 1) { LineCap = XLineCap.Round, LineJoin = XLineJoin.Round };

            for (int i = 0; i < pts.Count - 1; i++)
            {
                double pressure = (stroke.Points[i].Pressure + stroke.Points[i + 1].Pressure) / 2.0;
                pen.Width = baseWidth * Math.Max(pressure, 0.2) * scale;
                gfx.DrawLine(pen, pts[i], pts[i + 1]);
            }
        }

        private static void DrawPencilStroke(XGraphics gfx, StrokeRecord stroke, List<XPoint> pts,
            double r, double g, double b, double a, double baseWidth, double scale)
        {
            Random rand = new Random(stroke.GetHashCode());
            double avgPressure = stroke.Points.Average(p => p.Pressure);
            double strokeWidth = baseWidth * Math.Max(avgPressure, 0.2) * scale;

            // Layer 1: a few faint, jittered hair-lines — ONE continuous path per pass
            // (avoids round-cap blobs stacking at every point)
            int passes = 5;
            for (int pass = 0; pass < passes; pass++)
            {
                double passAlpha = a * (0.15 + rand.NextDouble() * 0.25); // faint per pass
                var color = XColor.FromArgb((int)(passAlpha * 255), (int)(r * 255), (int)(g * 255), (int)(b * 255));
                double lineWidth = strokeWidth * (0.55 + rand.NextDouble() * 0.35); // stay near target width, don't inflate

                var pen = new XPen(color, lineWidth) { LineCap = XLineCap.Round, LineJoin = XLineJoin.Round };

                var jittered = new XPoint[pts.Count];
                for (int i = 0; i < pts.Count; i++)
                {
                    double jx = (rand.NextDouble() - 0.5) * strokeWidth * 0.35;
                    double jy = (rand.NextDouble() - 0.5) * strokeWidth * 0.35;
                    jittered[i] = new XPoint(pts[i].X + jx, pts[i].Y + jy);
                }

                var path = new XGraphicsPath();
                path.AddLines(jittered);
                gfx.DrawPath(pen, path);
            }

            // Layer 2: graphite grain — sparse dots scattered across the stroke corridor.
            // This is the part that actually reads as "pencil texture".
            int grainCount = (int)(pts.Count * 5 * (strokeWidth / 2.0 + 1));
            for (int i = 0; i < grainCount; i++)
            {
                int idx = rand.Next(pts.Count - 1);
                var p0 = pts[idx];
                var p1 = pts[idx + 1];
                double t = rand.NextDouble();
                double x = p0.X + (p1.X - p0.X) * t;
                double y = p0.Y + (p1.Y - p0.Y) * t;

                double angle = rand.NextDouble() * Math.PI * 2;
                double radius = rand.NextDouble() * strokeWidth * 0.55;
                x += Math.Cos(angle) * radius;
                y += Math.Sin(angle) * radius;

                double dotAlpha = a * (0.15 + rand.NextDouble() * 0.45);
                var dotColor = XColor.FromArgb((int)(dotAlpha * 255), (int)(r * 255), (int)(g * 255), (int)(b * 255));
                double dotSize = 0.25 + rand.NextDouble() * 0.35;
                gfx.DrawEllipse(new XSolidBrush(dotColor), x - dotSize / 2, y - dotSize / 2, dotSize, dotSize);
            }
        }

        private static void DrawShape(XGraphics gfx, Dictionary<string, object> shapeRow, double pageH, double scaleX, double scaleY, double darkenFactor)
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

            var (r, g, b, a) = AndroidColorToRgba(color, darkenFactor);
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

        private static void DrawTextBox(XGraphics gfx, Dictionary<string, object> tbRow, double scaleX, double scaleY, double darkenFactor)
        {
            string? rawText = tbRow.TryGetValue("text", out var t) ? t as string : null;
            if (string.IsNullOrWhiteSpace(rawText)) return;

            // Coordinates
            double left = tbRow.TryGetValue("left", out var l) && l != null ? Convert.ToDouble(l) : 0;
            double top = tbRow.TryGetValue("top", out var tp) && tp != null ? Convert.ToDouble(tp) : 0;
            double right = tbRow.TryGetValue("right", out var rt) && rt != null ? Convert.ToDouble(rt) : 0;
            double bottom = tbRow.TryGetValue("bottom", out var bm) && bm != null ? Convert.ToDouble(bm) : 0;

            if (right <= left && tbRow.TryGetValue("bounds", out var bObj) && bObj != null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(bObj.ToString()!);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("left", out var bl)) left = bl.GetDouble();
                    if (root.TryGetProperty("top", out var bt)) top = bt.GetDouble();
                    if (root.TryGetProperty("right", out var br)) right = br.GetDouble();
                    if (root.TryGetProperty("bottom", out var bb)) bottom = bb.GetDouble();
                }
                catch { }
            }

            double width = right - left;
            double height = bottom - top;
            if (width <= 0 && tbRow.TryGetValue("box_width", out var bw) && bw != null) width = Convert.ToDouble(bw);
            if (height <= 0 && tbRow.TryGetValue("box_height", out var bh) && bh != null) height = Convert.ToDouble(bh);

            double px = left * scaleX;
            double py = top * scaleY;
            double pw = width * scaleX;
            double ph = height * scaleY;

            // Background color if any
            if (tbRow.TryGetValue("background_color", out var bg) && bg != null)
            {
                long bgColor = Convert.ToInt64(bg);
                if (bgColor != 0)
                {
                    var (bgR, bgG, bgB, bgA) = AndroidColorToRgba(bgColor);
                    if (bgA > 0.01)
                    {
                        var bgBrush = new XSolidBrush(XColor.FromArgb((int)(bgA * 255), (int)(bgR * 255), (int)(bgG * 255), (int)(bgB * 255)));
                        gfx.DrawRectangle(bgBrush, px, py, pw, ph);
                    }
                }
            }

            // Extract font size
            double scale = Math.Sqrt(scaleX * scaleY);
            double fontSizeCanvas = 0;
            var sizeMatch = Regex.Match(rawText, @"font-size\s*:\s*(\d+(\.\d+)?)px", RegexOptions.IgnoreCase);
            if (sizeMatch.Success && double.TryParse(sizeMatch.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsedFs))
            {
                fontSizeCanvas = parsedFs;
            }
            else if (tbRow.TryGetValue("text_size", out var ts) && ts != null)
            {
                fontSizeCanvas = Convert.ToDouble(ts);
            }

            if (fontSizeCanvas <= 0) fontSizeCanvas = 30.0;
            double fontSizePt = fontSizeCanvas * scale;
            if (fontSizePt < 6.0) fontSizePt = 6.0;

            // Extract text color
            XColor textColor = XColor.FromArgb(255, 0, 0, 0);
            var colorMatch = Regex.Match(rawText, @"color\s*:\s*#([0-9a-fA-F]{6}|[0-9a-fA-F]{8})", RegexOptions.IgnoreCase);
            if (colorMatch.Success)
            {
                string hex = colorMatch.Groups[1].Value;
                if (hex.Length == 6)
                {
                    double cr = Convert.ToByte(hex.Substring(0, 2), 16) / 255.0;
                    double cg = Convert.ToByte(hex.Substring(2, 2), 16) / 255.0;
                    double cb = Convert.ToByte(hex.Substring(4, 2), 16) / 255.0;
                    if (darkenFactor != 1.0) { cr = Math.Pow(cr, darkenFactor); cg = Math.Pow(cg, darkenFactor); cb = Math.Pow(cb, darkenFactor); }
                    textColor = XColor.FromArgb(255, (int)(cr * 255), (int)(cg * 255), (int)(cb * 255));
                }
                else if (hex.Length == 8)
                {
                    byte ca = Convert.ToByte(hex.Substring(0, 2), 16);
                    double cr = Convert.ToByte(hex.Substring(2, 2), 16) / 255.0;
                    double cg = Convert.ToByte(hex.Substring(4, 2), 16) / 255.0;
                    double cb = Convert.ToByte(hex.Substring(6, 2), 16) / 255.0;
                    if (darkenFactor != 1.0) { cr = Math.Pow(cr, darkenFactor); cg = Math.Pow(cg, darkenFactor); cb = Math.Pow(cb, darkenFactor); }
                    textColor = XColor.FromArgb(ca, (int)(cr * 255), (int)(cg * 255), (int)(cb * 255));
                }
            }
            else if (tbRow.TryGetValue("default_text_color", out var dtc) && dtc != null)
            {
                var (tr, tg, tb, ta) = AndroidColorToRgba(Convert.ToInt64(dtc), darkenFactor);
                textColor = XColor.FromArgb((int)(ta * 255), (int)(tr * 255), (int)(tg * 255), (int)(tb * 255));
            }

            // Font style
            bool isBold = Regex.IsMatch(rawText, @"<(b|strong)\b", RegexOptions.IgnoreCase) || Regex.IsMatch(rawText, @"font-weight\s*:\s*bold", RegexOptions.IgnoreCase);
            bool isItalic = Regex.IsMatch(rawText, @"<(i|em)\b", RegexOptions.IgnoreCase) || Regex.IsMatch(rawText, @"font-style\s*:\s*italic", RegexOptions.IgnoreCase);
            var fontStyle = (isBold && isItalic) ? XFontStyleEx.BoldItalic : isBold ? XFontStyleEx.Bold : isItalic ? XFontStyleEx.Italic : XFontStyleEx.Regular;

            // Strip style blocks and HTML tags to extract clean text
            string cleanText = Regex.Replace(rawText, @"<style[^>]*>[\s\S]*?</style>", "", RegexOptions.IgnoreCase);
            cleanText = Regex.Replace(cleanText, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            cleanText = Regex.Replace(cleanText, @"</(p|div|li|tr|h[1-6])>", "\n", RegexOptions.IgnoreCase);
            cleanText = Regex.Replace(cleanText, @"<[^>]+>", "");
            cleanText = System.Net.WebUtility.HtmlDecode(cleanText).Trim('\r', '\n');

            if (string.IsNullOrWhiteSpace(cleanText)) return;

            var font = new XFont("Arial", fontSizePt, fontStyle);
            var brush = new XSolidBrush(textColor);
            var rect = new XRect(px, py, Math.Max(pw, 20), Math.Max(ph, fontSizePt * 1.2));

            // Wrap lines if multiline or long text
            var tf = new PdfSharp.Drawing.Layout.XTextFormatter(gfx);
            tf.DrawString(cleanText, font, brush, rect);
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
                            int strokeType = doc.RootElement.TryGetProperty("type", out var tProp) ? tProp.GetInt32() : 0;
                            string brushType = strokeType switch
                            {
                                9 => "notein-highlighter-v1",
                                10 => "notein-pencil-v2",
                                11 => "notein-tape-v1",
                                _ => "notein-ballpoint-v1"
                            };

                            parsed = new StrokeRecord
                            {
                                Color = doc.RootElement.TryGetProperty("color", out var col) ? col.GetInt32() : -16777216,
                                Width = doc.RootElement.TryGetProperty("width", out var w) ? w.GetDouble() : 3.0,
                                Points = points,
                                BrushType = brushType
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

            var textBoxesByPage = new Dictionary<string, List<Dictionary<string, object>>>();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT * FROM TextBoxEntity ORDER BY creation_time";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var row = ReadRow(reader);
                    string pageId = row["page_id"].ToString() ?? "";
                    if (!textBoxesByPage.ContainsKey(pageId)) textBoxesByPage[pageId] = new List<Dictionary<string, object>>();
                    textBoxesByPage[pageId].Add(row);
                }
            }
            catch (SqliteException) { }

            conn.Close();

            var darkenFactor = AppSettings.Load().StrokeDarkenFactor;
            CreatePDF(noteTitle, pageIds, pages, noteDir, imagesByPage, shapesByPage, strokesByPage, textBoxesByPage, outputPdfPath, darkenFactor);

        }

        private static void CreatePDF(string title, List<string> pageIds, Dictionary<string, Dictionary<string, object>> pages, DirectoryInfo noteDir, Dictionary<string, List<Dictionary<string, object>>> imagesByPage, Dictionary<string, List<Dictionary<string, object>>> shapesByPage, Dictionary<string, List<StrokeRecord>> strokesByPage, Dictionary<string, List<Dictionary<string, object>>> textBoxesByPage, string outputPdf, double darkenFactor = 1.0)
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

                // Text boxes
                if (textBoxesByPage.TryGetValue(pageId, out var textBoxes))
                    foreach (var tb in textBoxes)
                        DrawTextBox(gfx, tb, scaleX, scaleY, darkenFactor);

                // Shapes
                if (shapesByPage.TryGetValue(pageId, out var shapes))
                    foreach (var shape in shapes)
                        DrawShape(gfx, shape, pageH, scaleX, scaleY, darkenFactor);

                // Strokes (draw highlighters first so they sit under opaque pen/pencil strokes)
                if (strokesByPage.TryGetValue(pageId, out var strokes))
                {
                    foreach (var stroke in strokes.Where(s => s.BrushType != null && (s.BrushType.Contains("highlighter") || s.BrushType.Contains("marker"))))
                        DrawStroke(gfx, stroke, pageH, scaleX, scaleY, darkenFactor);

                    foreach (var stroke in strokes.Where(s => s.BrushType == null || (!s.BrushType.Contains("highlighter") && !s.BrushType.Contains("marker"))))
                        DrawStroke(gfx, stroke, pageH, scaleX, scaleY, darkenFactor);
                }

                Console.WriteLine($"  Page {i + 1}/{pageIds.Count}: {(strokes?.Count ?? 0)} strokes, {(shapes?.Count ?? 0)} shapes, {(images?.Count ?? 0)} images, {(textBoxes?.Count ?? 0)} text boxes" +
                                  (bgPdfFile != null ? $" (template PDF: {bgPdfFile.Name})" : ""));
            }

            document.Save(outputPdf);
            CreateNotePreview.CreatePreview(outputPdf);
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

    internal class NoteinFontResolver : IFontResolver
    {
        public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            string suffix = (isBold && isItalic) ? "-BoldItalic" : isBold ? "-Bold" : isItalic ? "-Italic" : "-Regular";
            return new FontResolverInfo(familyName + suffix);
        }

        public byte[]? GetFont(string faceName)
        {
            // 1. Try Windows fonts folder
            try
            {
                string winFonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
                if (!string.IsNullOrEmpty(winFonts) && Directory.Exists(winFonts))
                {
                    if (faceName.Contains("BoldItalic", StringComparison.OrdinalIgnoreCase))
                    {
                        string path = Path.Combine(winFonts, "arialbi.ttf");
                        if (File.Exists(path)) return File.ReadAllBytes(path);
                    }
                    else if (faceName.Contains("Bold", StringComparison.OrdinalIgnoreCase))
                    {
                        string path = Path.Combine(winFonts, "arialbd.ttf");
                        if (File.Exists(path)) return File.ReadAllBytes(path);
                    }
                    else if (faceName.Contains("Italic", StringComparison.OrdinalIgnoreCase))
                    {
                        string path = Path.Combine(winFonts, "ariali.ttf");
                        if (File.Exists(path)) return File.ReadAllBytes(path);
                    }

                    string arial = Path.Combine(winFonts, "arial.ttf");
                    if (File.Exists(arial)) return File.ReadAllBytes(arial);
                }
            }
            catch { }

            return null;
        }
    }
}
