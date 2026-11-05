using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using RevitElementType = Autodesk.Revit.DB.ElementType;

namespace TemporalBim
{
    /// <summary>
    /// Stores a small A-model Family/Type preview with the passport and supplies a
    /// deterministic schematic when an older passport has no embedded image.
    /// </summary>
    public static class DoorPreviewRenderer
    {
        private const string DataPrefix = "data:image/png;base64,";

        public static string CaptureTypePreview(RevitElementType type)
        {
            if (type == null) return "";
            try
            {
                using (var bitmap = type.GetPreviewImage(new Size(360, 260)))
                {
                    if (bitmap == null) return "";
                    using (var stream = new MemoryStream())
                    {
                        bitmap.Save(stream, ImageFormat.Png);
                        return DataPrefix + Convert.ToBase64String(stream.ToArray());
                    }
                }
            }
            catch
            {
                // Some system families/types do not expose a preview image. The B
                // interface will still show the generated dimensional schematic.
                return "";
            }
        }

        public static Image CreatePreview(PassportInfo passport, Size requestedSize)
        {
            var embedded = Decode(passport?.Photo);
            return embedded ?? DrawSchematic(passport ?? new PassportInfo(), requestedSize);
        }

        public static void ShowPreviewDialog(IWin32Window owner, PassportInfo passport)
        {
            var form = new Form
            {
                Text = "A 模型門示意圖｜" + (passport?.PassportId ?? ""),
                StartPosition = FormStartPosition.CenterParent,
                Size = new Size(900, 680),
                MinimumSize = new Size(680, 520),
            };
            var picture = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                SizeMode = PictureBoxSizeMode.Zoom,
                Image = CreatePreview(passport, new Size(860, 560)),
            };
            string source = HasEmbeddedImage(passport?.Photo) ? "A 模型 Family／Type 預覽" : "依護照尺寸與開啟資訊產生的示意圖（原護照未保存預覽圖）";
            var details = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 72,
                Padding = new Padding(12, 8, 12, 4),
                BackColor = Color.FromArgb(240, 248, 245),
                Text = $"{source}\n{passport?.PassportId}｜{passport?.TypeName}｜{Format(passport?.WidthMm ?? 0)} × {Format(passport?.HeightMm ?? 0)} mm｜{passport?.OperationType}／{passport?.SwingHanding}",
            };
            form.Controls.Add(picture);
            form.Controls.Add(details);
            form.FormClosed += (s, e) =>
            {
                var image = picture.Image;
                picture.Image = null;
                image?.Dispose();
            };
            using (form) form.ShowDialog(owner);
        }

        private static Image Decode(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            try
            {
                string encoded = value.Trim();
                int comma = encoded.IndexOf(',');
                if (encoded.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && comma >= 0)
                    encoded = encoded.Substring(comma + 1);
                byte[] bytes = Convert.FromBase64String(encoded);
                using (var stream = new MemoryStream(bytes))
                using (var source = Image.FromStream(stream))
                    return new Bitmap(source);
            }
            catch
            {
                return null;
            }
        }

        private static bool HasEmbeddedImage(string value) => DecodeProbe(value);

        private static bool DecodeProbe(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            try
            {
                string encoded = value.Trim();
                int comma = encoded.IndexOf(',');
                if (encoded.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && comma >= 0)
                    encoded = encoded.Substring(comma + 1);
                Convert.FromBase64String(encoded);
                return true;
            }
            catch { return false; }
        }

        private static Bitmap DrawSchematic(PassportInfo passport, Size requested)
        {
            int width = Math.Max(520, requested.Width);
            int height = Math.Max(330, requested.Height);
            var bitmap = new Bitmap(width, height);
            using (var graphics = Graphics.FromImage(bitmap))
            using (var titleFont = new Font("Microsoft JhengHei UI", 14, FontStyle.Bold))
            using (var labelFont = new Font("Microsoft JhengHei UI", 10, FontStyle.Regular))
            using (var smallFont = new Font("Microsoft JhengHei UI", 9, FontStyle.Regular))
            using (var framePen = new Pen(Color.FromArgb(28, 102, 94), 5))
            using (var leafPen = new Pen(Color.FromArgb(35, 70, 67), 2))
            using (var guidePen = new Pen(Color.FromArgb(207, 167, 55), 2))
            using (var leafBrush = new SolidBrush(Color.FromArgb(221, 239, 233)))
            using (var textBrush = new SolidBrush(Color.FromArgb(30, 58, 54)))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.Clear(Color.FromArgb(249, 252, 250));
                graphics.DrawString("門構件示意圖", titleFont, textBrush, 24, 18);
                graphics.DrawString(string.IsNullOrWhiteSpace(passport.TypeName) ? "未命名門型" : passport.TypeName, labelFont, textBrush, 24, 50);

                double doorWidth = passport.WidthMm > 0 ? passport.WidthMm : 900;
                double doorHeight = passport.HeightMm > 0 ? passport.HeightMm : 2100;
                int top = 88;
                int bottom = height - 62;
                int availableHeight = Math.Max(150, bottom - top);
                int availableWidth = Math.Max(170, (int)(width * 0.55));
                double scale = Math.Min(availableWidth / doorWidth, availableHeight / doorHeight);
                int drawnWidth = Math.Max(90, (int)Math.Round(doorWidth * scale));
                int drawnHeight = Math.Max(160, (int)Math.Round(doorHeight * scale));
                int left = 36 + Math.Max(0, (availableWidth - drawnWidth) / 2);
                int doorTop = top + Math.Max(0, (availableHeight - drawnHeight) / 2);
                var opening = new Rectangle(left, doorTop, drawnWidth, drawnHeight);

                graphics.FillRectangle(leafBrush, opening);
                graphics.DrawRectangle(framePen, opening);
                graphics.DrawRectangle(leafPen, left + 8, doorTop + 8, Math.Max(10, drawnWidth - 16), Math.Max(10, drawnHeight - 16));
                if (passport.LeafCount > 1 || Contains(passport.OperationType, "雙", "double"))
                    graphics.DrawLine(leafPen, left + drawnWidth / 2, doorTop + 8, left + drawnWidth / 2, doorTop + drawnHeight - 8);
                int handleX = Contains(passport.SwingHanding, "左", "left") ? left + 18 : left + drawnWidth - 18;
                graphics.FillEllipse(Brushes.Goldenrod, handleX - 4, doorTop + drawnHeight / 2 - 4, 8, 8);

                graphics.DrawLine(guidePen, left, doorTop + drawnHeight + 18, left + drawnWidth, doorTop + drawnHeight + 18);
                graphics.DrawLine(guidePen, left, doorTop + drawnHeight + 12, left, doorTop + drawnHeight + 24);
                graphics.DrawLine(guidePen, left + drawnWidth, doorTop + drawnHeight + 12, left + drawnWidth, doorTop + drawnHeight + 24);
                graphics.DrawString(Format(doorWidth) + " mm", labelFont, textBrush, left + drawnWidth / 2 - 36, doorTop + drawnHeight + 26);
                graphics.DrawString(Format(doorHeight) + " mm", labelFont, textBrush, left + drawnWidth + 14, doorTop + drawnHeight / 2 - 8);

                int planLeft = (int)(width * 0.64);
                int planTop = Math.Max(115, height / 3);
                int planSize = Math.Min(width - planLeft - 40, height - planTop - 80);
                planSize = Math.Max(120, planSize);
                graphics.DrawString("開啟示意", labelFont, textBrush, planLeft, planTop - 34);
                graphics.DrawLine(framePen, planLeft, planTop, planLeft + planSize, planTop);
                bool leftHand = Contains(passport.SwingHanding, "左", "left");
                int hingeX = leftHand ? planLeft : planLeft + planSize;
                int endX = leftHand ? planLeft + planSize : planLeft;
                graphics.DrawLine(leafPen, hingeX, planTop, hingeX, planTop + planSize);
                graphics.DrawArc(guidePen, planLeft, planTop - planSize, planSize * 2, planSize * 2, leftHand ? 180 : 270, 90);
                graphics.DrawLine(leafPen, hingeX, planTop, endX, planTop + planSize);
                graphics.DrawString((passport.OperationType ?? "待確認") + "／" + (passport.SwingHanding ?? "待確認"), smallFont, textBrush, planLeft, planTop + planSize + 14);

                graphics.DrawString($"材質：{ValueOrUnknown(passport.Material)}　有效淨寬：{Format(passport.ClearWidthMm)} mm", smallFont, textBrush, 24, height - 30);
            }
            return bitmap;
        }

        private static bool Contains(string text, params string[] values)
        {
            foreach (string value in values)
                if ((text ?? "").IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static string ValueOrUnknown(string value) => string.IsNullOrWhiteSpace(value) ? "待確認" : value;
        private static string Format(double value) => value > 0 ? value.ToString("0.##") : "待確認";
    }
}
