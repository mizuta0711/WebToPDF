using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

using Microsoft.Win32;

using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;

namespace WebToPDF
{
    public partial class MainWindow : Window
    {
        private HashSet<string> visitedUrls = new HashSet<string>();
        private Queue<string> urlQueue = new Queue<string>();
        private string prefixFilter;
        private string suffixFilter;

        public MainWindow()
        {
            InitializeComponent();
        }

        private async void StartButton_Clicked(object sender, RoutedEventArgs e)
        {
            Log("準備完了");
            string startUrl = StartUrlTextBox.Text.Trim();
            prefixFilter = PrefixTextBox.Text.Trim();
            suffixFilter = SuffixTextBox.Text.Trim();

            if (string.IsNullOrEmpty(startUrl))
            {
                Log("開始URLを入力してください。");
                return;
            }

            Log("クローリングを開始します...");
            await Task.Run(() => StartCrawling(startUrl));
        }

        private void StartCrawling(string startUrl)
        {
            var options = new ChromeOptions();
            options.AddArgument("--headless"); // ヘッドレスモード
            options.AddArgument("--disable-gpu"); // GPU を無効化（Windows環境では推奨）
            using (var driver = new ChromeDriver(options))
            {
                driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(30);

                urlQueue.Enqueue(startUrl);

                while (urlQueue.Any())
                {
                    string currentUrl = urlQueue.Dequeue();
                    string normalizedUrl = NormalizeUrl(currentUrl);

                    if (visitedUrls.Contains(normalizedUrl)) continue;

                    visitedUrls.Add(normalizedUrl);
                    Log($"ページを読み込み中: {currentUrl}");

                    try
                    {
                        driver.Navigate().GoToUrl(currentUrl);
                        string title = driver.Title;
                        Log($"タイトル: {title}");

                        string pdfFileName = GetPdfFileName(title);
                        SavePageAsPdf(driver, pdfFileName);
                        Log($"PDFを保存しました: {pdfFileName}");

                        var links = GetLinksFromPage(driver);
                        foreach (var link in links)
                        {
                            string normalizedLink = NormalizeUrl(link);
                            if (!visitedUrls.Contains(normalizedLink) &&
                                IsUrlAllowed(link))
                            {
                                urlQueue.Enqueue(link);
                            }
                        }

                        Log($"進捗状況: {visitedUrls.Count}ページ完了, 残り{urlQueue.Count}ページ");
                    }
                    catch (Exception ex)
                    {
                        Log($"エラー: {ex.Message}");
                    }
                }
            }

            Log("すべてのPDFの生成が完了しました。");
        }

        private void SavePageAsPdf(IWebDriver driver, string outputFileName)
        {
            var chromeDriver = driver as ChromeDriver;
            if (chromeDriver == null) throw new InvalidOperationException("ChromeDriver が必要です。");

            var printOptions = new Dictionary<string, object>
            {
                { "paperWidth", 8.27 },
                { "paperHeight", 11.69 },
                { "marginTop", 0 },
                { "marginBottom", 0 },
                { "marginLeft", 0 },
                { "marginRight", 0 },
                { "printBackground", true },
            };

            var result = chromeDriver.ExecuteCdpCommand("Page.printToPDF", printOptions) as Dictionary<string, object>;
            if (result != null && result.TryGetValue("data", out var data))
            {
                var pdfData = Convert.FromBase64String(data.ToString());
                File.WriteAllBytes(outputFileName, pdfData);
            }
            else
            {
                throw new Exception("PDF データの取得に失敗しました。");
            }
        }

        private IEnumerable<string> GetLinksFromPage(IWebDriver driver)
        {
            return driver.FindElements(By.TagName("a"))
                         .Select(e => e.GetAttribute("href"))
                         .Where(href => !string.IsNullOrEmpty(href));
        }

        private string NormalizeUrl(string url)
        {
            try
            {
                var uri = new Uri(url);
                return $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}".TrimEnd('/');
            }
            catch
            {
                return url;
            }
        }

        private bool IsUrlAllowed(string url)
        {
            return (string.IsNullOrEmpty(prefixFilter) || url.StartsWith(prefixFilter)) &&
                   (string.IsNullOrEmpty(suffixFilter) || url.EndsWith(suffixFilter));
        }

        private string GetPdfFileName(string title)
        {
            string sanitizedTitle = string.Join("_", title.Split(Path.GetInvalidFileNameChars()));
            string defaultFileName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{sanitizedTitle}.pdf";

            var saveFileDialog = new SaveFileDialog
            {
                FileName = defaultFileName,
                Filter = "PDF Files (*.pdf)|*.pdf",
                Title = "保存するPDFファイルを選択してください"
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                return saveFileDialog.FileName;
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), defaultFileName);
        }

        private void Log(string message)
        {
            Dispatcher.Invoke(() =>
            {
                LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
                LogTextBox.ScrollToEnd();
            });
        }
    }
}