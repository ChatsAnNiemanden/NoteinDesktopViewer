using System.IO;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig.Rendering.Skia;

namespace NoteinDesktopViewer
{
    internal class CreateNotePreview
    {
        internal static void CreatePreview(string pdfFilePath)
        {

            if (!File.Exists(pdfFilePath))
            {
                throw new FileNotFoundException($"PDF file not found: {pdfFilePath}");
            }

            var pathWithoutExtension = Path.Combine(Path.GetDirectoryName(pdfFilePath) ?? string.Empty, Path.GetFileNameWithoutExtension(pdfFilePath));

            using (var pdf = PdfDocument.Open(pdfFilePath))
            {
                pdf.AddSkiaPageFactory();
                using SKBitmap bitmap = pdf.GetPageAsSKBitmap(1, scale: 2.0f);
                using SKImage image = SKImage.FromBitmap(bitmap);
                using SKData data = image.Encode(SKEncodedImageFormat.Png, quality: 100);
                using var stream = File.OpenWrite(pathWithoutExtension + ".png");
                data.SaveTo(stream);
            }
        }
    }
}
