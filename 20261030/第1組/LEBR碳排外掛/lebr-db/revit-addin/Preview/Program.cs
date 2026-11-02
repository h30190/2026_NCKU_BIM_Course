using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LebrCarbon.UI;
using LebrCarbon;

namespace LebrCarbon
{
    /// <summary>
    /// 用手冊附錄三範例開啟兩個視窗並存成 PNG。
    /// 用法：LebrCarbon.Preview.exe &lt;lebr-db 資料夾&gt; &lt;截圖輸出資料夾&gt; [--interactive]
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            var root = args[0];
            var outDir = args[1];
            var interactive = args.Length > 2 && args[2] == "--interactive";
            Directory.CreateDirectory(outDir);

            var db = LebrDatabase.Load(Path.Combine(root, "dist", "lebr_2025.json"));
            var calc = new LebrCalculator(db);
            var sample = Path.Combine(root, "samples", "appendix3_quantities.json");
            if (args.Length > 2 && args[2] == "--export")
            {
                // 給網頁組的範例：手冊附錄三的計算結果，格式與「匯出結果 JSON」相同
                File.WriteAllText(Path.Combine(outDir, "sample_result.json"), calc.Calculate(QuantityInput.Load(sample)).ToJson(),
                                  new System.Text.UTF8Encoding(false));
                return 0;
            }

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

            var precheck = new PrecheckWindow(calc.Calculate, sample);
            if (interactive)
            {
                if (precheck.ShowDialog() == true)
                    new ResultWindow(precheck.Result, ids => false).ShowDialog();
                return 0;
            }

            Snap(precheck, Path.Combine(outDir, "1_precheck.png"));
            precheck.Close();

            var result = new ResultWindow(calc.Calculate(QuantityInput.Load(sample)), ids => false);
            var tabs = (TabControl)result.FindName("Tabs");
            for (var i = 0; i < tabs.Items.Count; i++)
            {
                tabs.SelectedIndex = i;
                Snap(result, Path.Combine(outDir, "2_result_tab" + (i + 1) + ".png"));
            }
            result.Close();
            app.Shutdown();
            return 0;
        }

        private static void Snap(Window window, string path)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000;
            window.Top = 0;
            window.ShowInTaskbar = false;
            if (!window.IsVisible) window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var content = (FrameworkElement)window.Content;
            content.UpdateLayout();
            var w = (int)content.ActualWidth;
            var h = (int)content.ActualHeight;
            var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(window.Background, null, new Rect(0, 0, w, h));
                dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, w, h));
            }
            bmp.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(path)) encoder.Save(fs);
        }
    }
}
