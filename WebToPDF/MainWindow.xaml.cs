using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Threading;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using PdfSharp.Pdf.IO;

namespace WebToPDF
{
    public partial class MainWindow : Window
    {
        // 訪問済みURLを保持するHashSet
        private HashSet<string> visitedUrls = new HashSet<string>();
        // 未訪問のURLを保持するキュー
        private Queue<string> urlQueue = new Queue<string>();
        // URLのプレフィックスフィルター
        private string prefixFilter;
        // URLのサフィックスフィルター
        private string suffixFilter;

        public MainWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// クローリングを開始するボタンがクリックされたときの処理
        /// </summary>
        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            Log("準備完了");
            string startUrl = StartUrlTextBox.Text.Trim(); // 開始URL
            prefixFilter = PrefixTextBox.Text.Trim();     // プレフィックスフィルター
            suffixFilter = SuffixTextBox.Text.Trim();     // サフィックスフィルター

            if (string.IsNullOrEmpty(startUrl))
            {
                Log("開始URLを入力してください。");
                return;
            }

            // 保存先フォルダを選択するダイアログを表示
            using var dialog = new FolderBrowserDialog
            {
                Description = "PDFの保存先フォルダを選択してください",
                UseDescriptionForTitle = true
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                Log("クローリングを開始します...");
                // クローリング処理を非同期で実行
                await Task.Run(() => StartCrawling(startUrl, dialog.SelectedPath));
            }
        }

        /// <summary>
        /// クローリング開始処理
        /// </summary>
        /// <param name="startUrl">開始URL</param>
        /// <param name="outputFolder">PDFの保存先フォルダ</param>
        private void StartCrawling(string startUrl, string outputFolder)
        {
            var pagesFiles = new List<string>(); // PDFファイルのリスト
            var topPageTitle = string.Empty;     // 最初のページのタイトル

            // 進捗を更新
            UpdateProgress(visitedUrls.Count, urlQueue.Count);

            var options = new ChromeOptions();
            options.AddArgument("--headless");    // ヘッドレスモードで動作
            options.AddArgument("--disable-gpu"); // GPUを無効化

            // ChromeDriverを使用してブラウザを制御
            using (var driver = new ChromeDriver(options))
            {
                driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(30);

                // 初期URLをキューに追加
                urlQueue.Enqueue(startUrl);

                // PDF保存用の作業フォルダを作成
                var workFolder = Path.Combine(outputFolder, "pages");
                if (!Directory.Exists(workFolder))
                {
                    try
                    {
                        Directory.CreateDirectory(workFolder);
                    }
                    catch (Exception ex)
                    {
                        Log($"エラー: {ex.Message}");
                    }
                }

                int count = 0; // ページカウント
                while (urlQueue.Any())
                {
                    string currentUrl = urlQueue.Dequeue(); // キューからURLを取得
                    string normalizedUrl = NormalizeUrl(currentUrl); // URLを正規化

                    if (visitedUrls.Contains(normalizedUrl)) continue; // 訪問済みならスキップ

                    visitedUrls.Add(normalizedUrl); // 訪問済みに追加
                    Log($"ページを読み込み中: {currentUrl}");

                    try
                    {
                        driver.Navigate().GoToUrl(currentUrl); // ページに移動
                        Thread.Sleep(2000); // ページの読み込みを待機

                        // ページタイトルを取得
                        string pageTitle = driver.Title;
                        Log($"タイトル: {pageTitle}");

                        // Notionの特定の要素を待機
                        try
                        {
                            var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(10));
                            wait.Until(d => d.FindElement(By.CssSelector(".notion-page-content")));
                        }
                        catch (WebDriverTimeoutException)
                        {
                            // Notionページでない場合は無視
                        }

                        // ファイル名を作成
                        var safeTitle = string.Join("_", pageTitle.Split(Path.GetInvalidFileNameChars()));
                        var fileName = $"{++count:D3}_{safeTitle}.pdf";
                        var filePath = Path.Combine(workFolder, fileName);

                        // ページをPDFとして保存
                        SavePageAsPdf(driver, filePath);
                        Log($"PDFを保存しました: {filePath}");

                        // トップページタイトルを設定
                        if (string.IsNullOrEmpty(topPageTitle))
                        {
                            topPageTitle = safeTitle;
                        }

                        // PDFファイルリストに追加
                        pagesFiles.Add(filePath);

                        // ページ内のリンクを収集してキューに追加
                        var links = GetLinksFromPage(driver);
                        foreach (var link in links)
                        {
                            string normalizedLink = NormalizeUrl(link);
                            if (!visitedUrls.Contains(normalizedLink) && IsUrlAllowed(link))
                            {
                                urlQueue.Enqueue(link);
                            }
                        }

                        // 進捗を更新
                        UpdateProgress(visitedUrls.Count, urlQueue.Count);
                        Log($"進捗状況: {visitedUrls.Count}ページ完了, 残り{urlQueue.Count}ページ");
                    }
                    catch (Exception ex)
                    {
                        Log($"エラー: {ex.Message}");
                    }
                }
            }

            // PDFファイルを結合
            if (pagesFiles.Count > 0)
            {
                Dispatcher.Invoke(() =>
                {
                    // 保存ダイアログを表示
                    var dialog = new SaveFileDialog
                    {
                        Filter = "PDFファイル|*.pdf",
                        FileName = $"{topPageTitle}.pdf",
                        InitialDirectory = outputFolder,
                        Title = "PDFファイルの保存先を選択してください"
                    };
                    if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    {
                        Log("PDFファイルを結合しています...");
                        MergePDFs(pagesFiles, dialog.FileName);
                        Log($"PDFファイルを保存しました: {dialog.FileName}");
                    }
                });
            }

            Log("すべてのPDFの生成が完了しました。");
        }

        /// <summary>
        /// ページをPDFとして保存します。
        /// </summary>
        /// <param name="driver">使用するWebDriver</param>
        /// <param name="outputFileName">保存先のPDFファイル名</param>
        /// <exception cref="InvalidOperationException">ChromeDriverが必要な場合に発生します</exception>
        /// <exception cref="Exception">PDFデータの取得に失敗した場合に発生します</exception>
        private void SavePageAsPdf(IWebDriver driver, string outputFileName)
        {
            var chromeDriver = driver as ChromeDriver;
            if (chromeDriver == null) throw new InvalidOperationException("ChromeDriver が必要です。");

            var printOptions = new Dictionary<string, object>
            {
                { "paperWidth", 8.27 }, // A4サイズ幅
                { "paperHeight", 11.69 }, // A4サイズ高さ
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

        /// <summary>
        /// ページ内のリンクをすべて取得します。
        /// </summary>
        /// <param name="driver">使用するWebDriver</param>
        /// <returns>取得したリンクのリスト</returns>
        private IEnumerable<string> GetLinksFromPage(IWebDriver driver)
        {
            return driver.FindElements(By.TagName("a"))
                         .Select(e => e.GetAttribute("href"))
                         .Where(href => !string.IsNullOrEmpty(href));
        }

        /// <summary>
        /// URLを正規化します。
        /// </summary>
        /// <param name="url">正規化するURL</param>
        /// <returns>正規化されたURL</returns>
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

        /// <summary>
        /// URLが指定されたフィルタに適合するかを確認します。
        /// </summary>
        /// <param name="url">確認対象のURL</param>
        /// <returns>適合する場合はtrue、それ以外はfalse</returns>
        private bool IsUrlAllowed(string url)
        {
            return (string.IsNullOrEmpty(prefixFilter) || url.StartsWith(prefixFilter)) &&
                   (string.IsNullOrEmpty(suffixFilter) || url.EndsWith(suffixFilter));
        }

        /// <summary>
        /// クローリングの進捗を更新します。
        /// </summary>
        /// <param name="visited">訪問済みのページ数</param>
        /// <param name="remaining">未訪問のページ数</param>
        private void UpdateProgress(int visited, int remaining)
        {
            var total = Math.Max(visited + remaining, 1);
            var progress = (double)visited / total * 100;
            Dispatcher.Invoke(() =>
            {
                ProgressBar.Value = progress;
                StatusTextBlock.Text = $"処理中... {visited}/{total} ページ完了";
            });
        }

        /// <summary>
        /// ログメッセージを出力します。
        /// </summary>
        /// <param name="message">ログに記録するメッセージ</param>
        private void Log(string message)
        {
            Dispatcher.Invoke(() =>
            {
                LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
                LogTextBox.ScrollToEnd();
            });
        }

        /// <summary>
        /// 複数のPDFファイルを1つのPDFに結合します。
        /// </summary>
        /// <param name="inputFiles">結合するPDFファイルのリスト</param>
        /// <param name="outputPath">結合後のPDF保存先</param>
        public void MergePDFs(List<string> inputFiles, string outputPath)
        {
            using (PdfSharp.Pdf.PdfDocument outputDocument = new PdfSharp.Pdf.PdfDocument())
            {
                foreach (string file in inputFiles)
                {
                    using (PdfSharp.Pdf.PdfDocument inputDocument = PdfReader.Open(file, PdfDocumentOpenMode.Import))
                    {
                        foreach (PdfSharp.Pdf.PdfPage page in inputDocument.Pages)
                        {
                            outputDocument.AddPage(page);
                        }
                    }
                }
                outputDocument.Save(outputPath);
            }
        }
    }
}
