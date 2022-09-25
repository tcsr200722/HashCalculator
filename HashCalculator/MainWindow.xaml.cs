﻿using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HashCalculator
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<HashModel> HashModels =
            new ObservableCollection<HashModel>();
        private int QueuedFilesCount = 0;
        private readonly Queue<PathExpP> FilesPathExpsQueue = new Queue<PathExpP>();
        private readonly Action<Task> AUpdate;
        private readonly HashNameItems hashNameItems = new HashNameItems();
        private readonly List<PathExpP> FilesPathExpsDraggedInto = new List<PathExpP>();

        public MainWindow()
        {
            this.InitializeComponent();
            this.Title = $"{Info.Title} v{Info.Version} — {Info.Author} @ {Info.Published}";
            this.uiDataGrid_HashFiles.ItemsSource = this.HashModels;
            this.AUpdate = new Action<Task>(this.UpdateProgress);
            new Thread(this.ThreadAddHashModel) { IsBackground = true }.Start();
            this.InitializeFromConfigure();
        }

        private void InitializeFromConfigure()
        {
            Configure config = Settings.Current;
            this.Topmost = config.MainWindowTopmost;
            this.uiCheckBox_WindowTopMost.IsChecked = config.MainWindowTopmost;
            this.uiComboBox_HashAlgorithm.SelectedIndex = (int)config.SelectedAlgo;
            this.uiComboBox_HashAlgorithm.SelectionChanged +=
                this.ComboBox_HashAlgorithm_SelectionChanged;
            if (config.RemMainWindowPosition)
            {
                this.Top = config.MainWindowTop;
                this.Left = config.MainWindowLeft;
            }
            if (config.RembMainWindowSize)
            {
                this.Width = config.MainWindowWidth;
                this.Height = config.MainWindowHeight;
            }
        }

        private void Window_MainWindow_Closing(object sender, CancelEventArgs e)
        {
            Configure config = Settings.Current;
            if (config.RemMainWindowPosition)
            {
                config.MainWindowTop = this.Top;
                config.MainWindowLeft = this.Left;
            }
            if (config.RembMainWindowSize)
            {
                config.MainWindowWidth = this.Width;
                config.MainWindowHeight = this.Height;
            }
            Settings.SaveConfigure();
        }

        private void SearchFoldersByPolicy(string[] data, List<string> DataPaths)
        {
            switch (Settings.Current.FolderSearchPolicy)
            {
                default:
                case 0:
                    foreach (string p in data)
                    {
                        if (Directory.Exists(p))
                        {
                            DirectoryInfo di = new DirectoryInfo(p);
                            DataPaths.AddRange(di.GetFiles().Select(i => i.FullName));
                        }
                        else if (File.Exists(p)) DataPaths.Add(p);
                    }
                    break;
                case 1:
                    foreach (string p in data)
                    {
                        if (Directory.Exists(p))
                        {
                            DirectoryInfo di = new DirectoryInfo(p);
                            DataPaths.AddRange(
                                di.GetFiles("*", SearchOption.AllDirectories).Select(i => i.FullName)
                            );
                        }
                        else if (File.Exists(p)) DataPaths.Add(p);
                    }
                    break;
                case 2:
                    foreach (string p in data) if (File.Exists(p)) DataPaths.Add(p);
                    break;
            }
        }

        private void DataGrid_FilesToCalculate_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                return;
            if (!(e.Data.GetData(DataFormats.FileDrop) is string[] data) || data.Length == 0)
                return;
            List<string> searchedPaths = new List<string>();
            this.SearchFoldersByPolicy(data, searchedPaths);
            if (searchedPaths.Count == 0)
                return;
            IEnumerable<PathExpP> pathsExps = searchedPaths.Select(s => new PathExpP(s));
            this.FilesPathExpsDraggedInto.AddRange(pathsExps);
            Thread thread = new Thread(new ParameterizedThreadStart(this.EnqueueFilesPath))
            {
                IsBackground = true
            };
            thread.Start(pathsExps);
        }

        private void EnqueueFilesPath(object data)
        {
            IEnumerable<PathExpP> pexps = data as IEnumerable<PathExpP>;
            lock (Locks.MainLock)
            {
                foreach (PathExpP pe in pexps)
                    this.FilesPathExpsQueue.Enqueue(pe);
                this.QueuedFilesCount += pexps.Count();
                Application.Current.Dispatcher.Invoke(this.AfterFilesQueued);
            }
        }

        private void ShowProgressInfo()
        {
            this.uiButton_RefreshCopy.IsEnabled = false;
            this.uiButton_RefreshCurrentHash.IsEnabled = false;
            this.uiButton_StartCompare.IsEnabled = false;
            this.uiButton_ClearModels.IsEnabled = false;
            this.uiButton_ExportAsText.IsEnabled = false;
            this.uiTextBlock_CompletedTotal.Visibility = Visibility.Visible;
            this.uiTextBlock_CompletedCount.Text = "0";
            this.uiTextBlock_CompletedCount.Visibility = Visibility.Visible;
            this.uiTextBlock_Delimiter.Visibility = Visibility.Visible;
            this.uiTextBlock_TotalTaskCount.Visibility = Visibility.Visible;
            this.uiProgressbar_TaskProgress.Value = 0;
            this.uiProgressbar_TaskProgress.Visibility = Visibility.Visible;
        }

        private void HideProgressInfo()
        {
            this.uiButton_RefreshCopy.IsEnabled = true;
            this.uiButton_RefreshCurrentHash.IsEnabled = true;
            this.uiButton_StartCompare.IsEnabled = true;
            this.uiButton_ClearModels.IsEnabled = true;
            this.uiButton_ExportAsText.IsEnabled = true;
            this.uiTextBlock_CompletedTotal.Visibility = Visibility.Hidden;
            this.uiTextBlock_CompletedCount.Visibility = Visibility.Hidden;
            this.uiTextBlock_Delimiter.Visibility = Visibility.Hidden;
            this.uiTextBlock_TotalTaskCount.Visibility = Visibility.Hidden;
            this.uiProgressbar_TaskProgress.Visibility = Visibility.Hidden;
        }

        private void AfterFilesQueued()
        {
            if (this.QueuedFilesCount < 1)
                return;
            this.uiProgressbar_TaskProgress.Maximum = this.QueuedFilesCount;
            this.uiTextBlock_TotalTaskCount.Text = this.QueuedFilesCount.ToString();
            if (CompletionCounter.Count() <= 0)
            {
                this.ShowProgressInfo();
                Locks.UpdateComputeLock(); // 更新“同时计算多少个文件”的信号量
            }
        }

        private void AddHashModel(int serial, FileInfo fi, string expected)
        {
            HashModel hashModel = new HashModel(serial, fi, expected);
            this.HashModels.Add(hashModel);
            Task.Run(hashModel.EnterGenerateUnderLimit).ContinueWith(this.AUpdate);
        }

        private void UpdateProgress(Task task)
        {
            int completed = CompletionCounter.Count();
            Application.Current.Dispatcher.Invoke(
                () =>
                {
                    this.uiProgressbar_TaskProgress.Value = completed;
                    this.uiTextBlock_CompletedCount.Text = completed.ToString();
                }
            );
            if (completed >= this.QueuedFilesCount)
            {
                CompletionCounter.ResetCount();
                this.QueuedFilesCount = 0;
                Application.Current.Dispatcher.Invoke(this.HideProgressInfo);
            }
        }

        private void RefreshCurrentDataGridLines(bool deleteLines)
        {
            lock (Locks.MainLock)
            {
                if (deleteLines)
                {
                    this.HashModels.Clear();
                    SerialGenerator.Reset();
                }
                // 与 DataGrid_FilesToCalculate_Drop 方法类似
                Thread thread = new Thread(new ParameterizedThreadStart(this.EnqueueFilesPath))
                {
                    IsBackground = true
                };
                thread.Start(this.FilesPathExpsDraggedInto);
            }
        }

        private void ThreadAddHashModel()
        {
            int serial;
            FileInfo fi;
            Action<int, FileInfo, string> ahm = new Action<int, FileInfo, string>(this.AddHashModel);
            while (true)
            {
                lock (Locks.MainLock)
                {
                    if (this.FilesPathExpsQueue.Count == 0)
                        goto pause;
                    PathExpP pep = this.FilesPathExpsQueue.Dequeue();
                    fi = new FileInfo(pep.filePath);
                    serial = SerialGenerator.GetSerial();
                    Application.Current.Dispatcher.Invoke(ahm, serial, fi, pep.expected);
                }
            pause:
                Thread.Sleep(10);
            }
        }

        // TODO 使用以下方法清空列表后，计算任务并未停下，需要增加停止计算任务的逻辑
        private void Button_ClearFileList_Click(object sender, RoutedEventArgs e)
        {
            this.HashModels.Clear();
            this.FilesPathExpsDraggedInto.Clear();
            SerialGenerator.Reset();
        }

        private void Button_ExportAsTextFile_Click(object sender, RoutedEventArgs e)
        {
            if (this.HashModels.Count == 0)
            {
                MessageBox.Show("列表中没有任何需要导出的条目。", "提示");
                return;
            }
            Configure config = Settings.Current;
            SaveFileDialog sf = new SaveFileDialog()
            {
                ValidateNames = true,
                Filter = "文本文件|*.txt|所有文件|*.*",
                FileName = "hashsums.txt",
                InitialDirectory = config.SavedPath,
            };
            if (sf.ShowDialog() != true)
                return;
            config.SavedPath = Path.GetDirectoryName(sf.FileName);
            //Settings.SaveConfigure(); // 窗口关闭时会 SaveConfigure
            try
            {
                using (StreamWriter sw = File.CreateText(sf.FileName))
                {
                    foreach (HashModel hm in this.HashModels)
                        if (hm.Completed && hm.Export)
                            sw.WriteLine($"{hm.Hash} *{hm.Name}");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"结果导出为文本文件失败：\n{ex.Message}", "错误");
                return;
            }
        }

        private void GlobalUpdateHashNameItems(string pathOrHash)
        {
            this.hashNameItems.Clear();
            if (this.uiComboBox_ComparisonMethod.SelectedIndex == 0)
            {
                if (!File.Exists(pathOrHash))
                {
                    MessageBox.Show("校验依据来源文件不存在。", "提示");
                    return;
                }
                try
                {
                    foreach (string line in File.ReadAllLines(pathOrHash))
                    {
                        string[] items = line.Split(
                            new char[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                        if (items.Length < 2)
                        {
                            if (MessageBox.Show(
                                    "哈希值文件行读取错误，可能该行格式不正确，是否继续？",
                                    "错误",
                                    MessageBoxButton.YesNo) == MessageBoxResult.No)
                                return;
                            else
                                continue;
                        }
                        this.hashNameItems.Add(items);
                    }
                    return;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"校验依据来源文件打开失败：\n{ex.Message}", "错误");
                    return;
                }
            }
            else
                this.hashNameItems.Add(new string[] { pathOrHash.Trim(), "" });
        }

        private void CompareHashWithExpectedHash(string path)
        {
            FileInfo fileInfo = new FileInfo(path);
            string parent = fileInfo.DirectoryName;
            List<string[]> pathExpList = new List<string[]>();
            try
            {
                foreach (string line in File.ReadAllLines(path))
                {
                    string[] items = line.Split(
                            new char[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                    if (items.Length < 2)
                    {
                        if (MessageBox.Show(
                                "哈希值文件行读取错误，可能该行格式不正确，是否继续？",
                                "错误",
                                MessageBoxButton.YesNo) == MessageBoxResult.No)
                            return;
                        else
                            continue;
                    }
                    items[1] = Path.Combine(
                        parent, items[1].Trim(new char[] { '*', ' ', '\n' }));
                    if (!File.Exists(items[1])) continue;
                    pathExpList.Add(items);
                }
                IEnumerable<PathExpP> pathsExps = pathExpList.Select(hp => new PathExpP(hp));
                this.FilesPathExpsDraggedInto.AddRange(pathsExps);
                // 与 DataGrid_FilesToCalculate_Drop 方法类似
                Thread thread = new Thread(new ParameterizedThreadStart(this.EnqueueFilesPath))
                {
                    IsBackground = true
                };
                thread.Start(pathsExps);
            }
            catch { return; }
        }

        private void Button_StartCompare_Click(object sender, RoutedEventArgs e)
        {
            string pathOrHash = this.uiTextBox_HashValueOrFilePath.Text;
            if (string.IsNullOrEmpty(pathOrHash))
            {
                MessageBox.Show("未输入哈希值校验依据。", "提示");
                return;
            }
            if (Settings.Current.SearchForComparison
                && File.Exists(pathOrHash)
                && this.HashModels.Count == 0)
            {
                this.CompareHashWithExpectedHash(pathOrHash);
            }
            else
            {
                this.GlobalUpdateHashNameItems(pathOrHash);
                foreach (HashModel hm in this.HashModels)
                {
                    hm.CmpResult = this.hashNameItems.Compare(hm.Hash, hm.Name);
                }
            }
        }

        private void TextBox_HashValueOrFilePath_PreviewDragOver(object sender, DragEventArgs e)
        {
            e.Handled = true;
        }

        private void TextBox_HashValueOrFilePath_PreviewDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                return;
            if (!(e.Data.GetData(DataFormats.FileDrop) is string[] data) || data.Length == 0)
                return;
            this.uiTextBox_HashValueOrFilePath.Text = data[0];
        }

        private void TextBox_HashValueOrFilePath_Changed(object sender, TextChangedEventArgs e)
        {
            this.uiComboBox_ComparisonMethod.SelectedIndex =
                File.Exists(this.uiTextBox_HashValueOrFilePath.Text) ? 0 : 1;
        }

        private void CheckBox_WindowTopmost_Click(object sender, RoutedEventArgs e)
        {
            this.Topmost = this.uiCheckBox_WindowTopMost.IsChecked == true;
            Settings.Current.MainWindowTopmost = this.Topmost;
            //Settings.SaveConfigure(); // 窗口关闭时会 SaveConfigure
        }

        private void ComboBox_HashAlgorithm_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            lock (Locks.AlgoSelectionLock)
            {
                Settings.Current.SelectedAlgo = (AlgoType)this.uiComboBox_HashAlgorithm.SelectedIndex;
                //Settings.SaveConfigure(); // 窗口关闭时会 SaveConfigure
            }
        }

        private void Button_CopyHashValue_Click(object sender, RoutedEventArgs e)
        {
            Button button = sender as Button;
            HashModel hashModel = button.DataContext as HashModel;
            Clipboard.SetText(hashModel.Hash);
        }

        private void Button_CopyRefreshHash_Click(object sender, RoutedEventArgs e)
        {
            this.RefreshCurrentDataGridLines(false);
        }

        private void Button_RefreshCurrentHash_Click(object sender, RoutedEventArgs e)
        {
            this.RefreshCurrentDataGridLines(true);
        }

        private void MenuItem_Settings_Click(object sender, RoutedEventArgs e)
        {
            Window settingsWindow = new SettingsPanel() { Owner = this };
            settingsWindow.ShowDialog();
        }

        private void DataGrid_HashFiles_PrevKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                this.uiDataGrid_HashFiles.SelectedIndex = -1;
            }
        }

        private void Button_SelectFiles_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog askFilePaths = new OpenFileDialog
            {
                Multiselect = true,
                Filter = "所有文件|*.*",
                DereferenceLinks = false,
            };
            if (askFilePaths.ShowDialog() == true)
            {
                IEnumerable<PathExpP> pathsExps = askFilePaths.FileNames
                    .Select(s => new PathExpP(s));
                this.FilesPathExpsDraggedInto.AddRange(pathsExps);
                // 与 DataGrid_FilesToCalculate_Drop 方法类似
                Thread thread = new Thread(new ParameterizedThreadStart(this.EnqueueFilesPath))
                {
                    IsBackground = true
                };
                thread.Start(pathsExps);
            }
        }

        private void Button_SelectHashSetFile_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog askUserFilePath = new OpenFileDialog
            {
                Filter = "所有文件|*.*",
            };
            if (askUserFilePath.ShowDialog() == true)
            {
                this.uiTextBox_HashValueOrFilePath.Text = askUserFilePath.FileName;
                // TextBox_HashValueOrFilePath_Changed 已实现
                //this.uiComboBox_ComparisonMethod.SelectedIndex = 0;
            }
        }
    }
}
