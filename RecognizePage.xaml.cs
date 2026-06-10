using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using MusicBox.Models;
using MusicBox.Services;
using MusicBox.ViewModels;

namespace MusicBox
{
    public sealed partial class RecognizePage : Page
    {
        private static AudioPageStateCache? s_audioCache;
        private static readonly object s_audioRunSync = new();
        private static Task<IReadOnlyList<DetectedAudioNote>>? s_runningAudioTask;
        private static string? s_runningAudioInputPath;
        private static AudioRecognitionMode s_runningAudioMode = AudioRecognitionMode.MelodyFocus;
        private static string s_runningAudioProgressText = string.Empty;
        private static IReadOnlyList<DetectedAudioNote>? s_runningAudioResult;
        private static CancellationTokenSource? s_runningAudioCancellation;

        private static OmrPageStateCache? s_cache;
        private static readonly object s_omrRunSync = new();
        private static Task<OmrRecognitionResult>? s_runningOmrTask;
        private static string? s_runningOmrInputPath;
        private static string s_runningOmrProgressText = string.Empty;
        private static OmrRecognitionResult? s_runningOmrResult;

        private MainViewModel? _viewModel;
        private string? _selectedAudioPath;
        private IReadOnlyList<DetectedAudioNote> _detectedNotes = Array.Empty<DetectedAudioNote>();
        private AudioRecognitionMode _audioRecognitionMode = AudioRecognitionMode.MelodyFocus;
        private bool _audioAnalyzing;
        private Task<IReadOnlyList<DetectedAudioNote>>? _attachedAudioWatchingTask;

        private readonly OmrRuntimeManager _omrRuntimeManager = new();
        private readonly ScoreOmrService _omrService;
        private readonly OmrImportPostProcessor _omrPostProcessor = new();
        private string? _selectedSheetPath;
        private string? _recognizedMusicXmlPath;
        private OmrRecognitionResult? _lastOmrResult;
        private Task<OmrRecognitionResult>? _attachedWatchingTask;

        private readonly ObservableCollection<OmrCandidateListItem> _omrCandidates = new();

        public RecognizePage()
        {
            InitializeComponent();
            _omrService = new ScoreOmrService(_omrRuntimeManager);
            OmrCandidatesListView.ItemsSource = _omrCandidates;
            AudioRecognitionModeComboBox.SelectedIndex = 0;
            UpdateAudioResultEmptyState();
            UpdateOmrCandidatesEmptyState();
            Loaded += RecognizePage_Loaded;
        }

        private async void RecognizePage_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyStaticLocalizedText();
            await RestoreAudioStateFromCacheAsync();
            AttachRunningAudioIfNeeded();
            await RefreshRuntimeStatusAsync();
            await RestoreOmrStateFromCacheAsync();
            AttachRunningOmrIfNeeded();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is MainViewModel vm)
            {
                _viewModel = vm;
                DataContext = vm;
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            SaveAudioStateToCache();
            SaveOmrStateToCache();
        }

        private async Task RefreshRuntimeStatusAsync(string? extraMessage = null)
        {
            OmrRuntimeStatus status = _omrRuntimeManager.GetRuntimeStatus();
            string text =
                $"Python 3.11: {(status.Python311Ready ? Loc("就绪", "Ready") : Loc("缺失", "Missing"))}, " +
                $"homr: {(status.HomrReady ? Loc("就绪", "Ready") : Loc("缺失", "Missing"))}, " +
                $"Audiveris: {(status.AudiverisReady ? Loc("就绪", "Ready") : Loc("缺失", "Missing"))}";

            if (!string.IsNullOrWhiteSpace(status.HomrCommand))
            {
                text += $"\n{Loc("HOMR命令", "HOMR command")}: {status.HomrCommand}";
            }

            if (!string.IsNullOrWhiteSpace(status.AudiverisPath))
            {
                text += $"\n{Loc("Audiveris路径", "Audiveris path")}: {status.AudiverisPath}";
            }

            if (!string.IsNullOrWhiteSpace(extraMessage))
            {
                text += $"\n{extraMessage}";
            }

            OmrRuntimeStatusText.Text = text;
            await Task.CompletedTask;
        }

        private async void RefreshRuntimeStatusButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshRuntimeStatusAsync();
            SaveOmrStateToCache();
        }

        private void SetOmrProgress(bool busy, string? text = null)
        {
            OmrProgressRing.IsActive = busy;
            OmrProgressRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            OmrProgressText.Text = text ?? string.Empty;
        }

        private void SetAudioProgress(bool busy, string? text = null)
        {
            _audioAnalyzing = busy;
            AudioAnalyzeProgressRing.IsActive = busy;
            AudioAnalyzeProgressRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            AudioAnalyzeProgressText.Text = text ?? string.Empty;
            AnalyzeAudioButton.IsEnabled = !busy;
            SelectAudioButton.IsEnabled = !busy;
            AudioRecognitionModeComboBox.IsEnabled = !busy;
            ImportDetectedNotesButton.IsEnabled = !busy && _detectedNotes.Count > 0;
            UpdateAudioResultEmptyState();
        }

        private void UpdateAudioResultEmptyState()
        {
            if (AudioEmptyStatePanel == null || DetectedNotesPreviewTextBox == null)
            {
                return;
            }

            AudioEmptyStatePanel.Visibility = string.IsNullOrWhiteSpace(DetectedNotesPreviewTextBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void UpdateOmrCandidatesEmptyState()
        {
            if (OmrCandidatesEmptyStatePanel == null)
            {
                return;
            }

            OmrCandidatesEmptyStatePanel.Visibility = _omrCandidates.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void ApplyStaticLocalizedText()
        {
            bool isEnglish = IsEnglishUi();
            RecognizePageTitleText.Text = isEnglish ? "Recognition Center" : "识别中心";
            RecognizePageSubtitleText.Text = isEnglish
                ? "Convert audio or images into staff notation with multiple formats and high-precision recognition."
                : "将音频或图片转换为五线谱，支持多种格式与高精度识别。";
            AudioSectionTitleText.Text = isEnglish ? "Audio -> Staff (MP3/WAV)" : "音频识别 -> 五线谱（MP3/WAV）";
            AudioSectionDescText.Text = isEnglish
                ? "Offline MP3/WAV recognition: detect monophonic pitch and generate draft notes for direct import."
                : "支持 MP3 / WAV 离线识别：提取主旋律音高并生成草稿音符，可直接导入五线谱页。";
            AudioStepTitleText.Text = isEnglish ? "1. Select audio and parameters" : "1. 选择音频与参数";
            AudioResultTitleText.Text = isEnglish ? "2. Recognition result" : "2. 识别结果";
            FrequencyLabelText.Text = isEnglish ? "Frequency (Hz)" : "频率（Hz）";
            FrequencyResultLabelText.Text = isEnglish ? "Converted" : "换算";
            SetIconButtonContent(SelectAudioButton, "\uE8A5", isEnglish ? "Pick Audio (MP3/WAV)" : "选择音频 (MP3/WAV)");
            SetIconButtonContent(AnalyzeAudioButton, "\uE768", isEnglish ? "Start Recognition" : "开始识别");
            SetIconButtonContent(ClearAudioButton, "\uE74D", isEnglish ? "Clear" : "清空");
            SetIconButtonContent(ImportDetectedNotesButton, "\uE8B5", isEnglish ? "Import to Staff" : "导入到五线谱页");
            SetIconButtonContent(AudioResultImportButton, "\uE8B5", isEnglish ? "Import to Staff" : "导入到五线谱页");
            SetIconButtonContent(ExportAudioMusicXmlButton, "\uE896", isEnglish ? "Export MusicXML" : "导出 MusicXML");
            AudioModeLabelText.Text = isEnglish ? "Mode:" : "识别模式:";
            SetComboBoxItemContent(AudioModeMelodyItem, "\uEC4F", isEnglish ? "Melody Focus" : "旋律优先");
            SetComboBoxItemContent(AudioModeBalancedItem, "\uE8D4", isEnglish ? "Balanced" : "平衡");
            SetComboBoxItemContent(AudioModeDenseItem, "\uE9D2", isEnglish ? "High Recall / Low Notes" : "高召回/低音保留");
            RefreshAudioModeSelectionBox();
            UpdateAudioModeHelpText();
            AudioSupportText.Text = isEnglish
                ? "Supports MP3/WAV with three recognition modes. Melody Focus is best for full songs with accompaniment."
                : "支持 MP3 / WAV，提供三种识别模式。完整歌曲或有伴奏时建议先用“旋律优先”。";

            OmrSectionTitleText.Text = isEnglish ? "Image/PDF -> Staff (Beta)" : "图片 / PDF 识别 -> 五线谱（Beta）";
            OmrSectionDescText.Text = isEnglish
                ? "Supports PDF/PNG/JPG/BMP/TIFF and converts to MusicXML via multi-engine ranking."
                : "支持 PDF、PNG、JPG、BMP、TIFF。内部通过多引擎 + 多候选评分转换成 MusicXML。";
            OmrBetaBadgeText.Text = isEnglish ? "Beta" : "Beta 版本";
            OmrSelectTitleText.Text = isEnglish ? "1. Select image/PDF" : "1. 选择图片/PDF";
            OmrStatusInfoTitleText.Text = isEnglish ? "Status" : "状态信息";
            OmrEngineTitleText.Text = isEnglish ? "3. Engine and environment" : "3. 引擎与环境信息";
            SetIconButtonContent(SelectSheetFileButton, "\uEB9F", isEnglish ? "Pick Image/PDF" : "选择图片/PDF");
            SetIconButtonContent(RunOmrButton, "\uE768", isEnglish ? "Recognize to MusicXML" : "识别为谱面 (MusicXML)");
            SetIconButtonContent(ImportBestOmrButton, "\uE73E", isEnglish ? "Import Best Candidate" : "导入最优候选");
            SetIconButtonContent(ImportSelectedOmrButton, "\uE8B5", isEnglish ? "Import Selected Candidate" : "导入选中候选");
            SetIconButtonContent(OpenOmrArtifactsButton, "\uE8A7", isEnglish ? "Open Artifacts" : "查看中间层");
            SetIconButtonContent(RefreshRuntimeStatusButton, "\uE72C", isEnglish ? "Refresh Status" : "刷新状态");
            OmrEmptyCandidatesText.Text = isEnglish ? "No candidates yet" : "暂无候选结果";
            CandidatesTitleText.Text = isEnglish ? "2. Candidates (sorted by score)" : "2. 候选列表（按评分排序）";
            CandidatesEngineHeaderText.Text = "#";
            CandidatesInputHeaderText.Text = isEnglish ? "Input Variant" : "输入版本";
            CandidatesNotesHeaderText.Text = isEnglish ? "Notes" : "音符数";
            CandidatesMeasuresHeaderText.Text = isEnglish ? "Measures" : "小节数";
            CandidatesScoreHeaderText.Text = isEnglish ? "Quality" : "质量分";
            CandidatesStatusHeaderText.Text = isEnglish ? "Status" : "状态";
            CandidatesPreviewTitleText.Text = isEnglish ? "Candidate Preview (MusicXML)" : "候选预览（MusicXML）";
            RealtimeNextButton.Content = isEnglish ? "Realtime Recognition (Next Phase)" : "开始实时识别（下一阶段）";
            MicNextButton.Content = isEnglish ? "Mic to Staff (Next Phase)" : "麦克风监听转谱（下一阶段）";

            if (string.IsNullOrWhiteSpace(SelectedAudioPathText.Text) || SelectedAudioPathText.Text.Contains("No audio selected", StringComparison.OrdinalIgnoreCase) || SelectedAudioPathText.Text.Contains("未选择"))
            {
                SelectedAudioPathText.Text = isEnglish ? "No audio selected" : "未选择音频文件";
            }

            if (string.IsNullOrWhiteSpace(RecognizeSummaryText.Text)
                || RecognizeSummaryText.Text.Contains("Select audio", StringComparison.OrdinalIgnoreCase)
                || RecognizeSummaryText.Text.Contains("选择音频")
                || RecognizeSummaryText.Text.Contains("Result", StringComparison.OrdinalIgnoreCase)
                || RecognizeSummaryText.Text.Contains("识别结果"))
            {
                RecognizeSummaryText.Text = isEnglish
                    ? "Select audio and click \"Start Recognition\" to view results."
                    : "选择音频并点击“开始识别”以查看结果。";
            }

            if (string.IsNullOrWhiteSpace(SelectedSheetPathText.Text) || SelectedSheetPathText.Text.Contains("No sheet selected", StringComparison.OrdinalIgnoreCase) || SelectedSheetPathText.Text.Contains("未选择"))
            {
                SelectedSheetPathText.Text = isEnglish ? "No image/PDF selected" : "未选择图片/PDF文件";
            }

            if (string.IsNullOrWhiteSpace(SheetRecognizeSummaryText.Text) || SheetRecognizeSummaryText.Text.Contains("OMR Result", StringComparison.OrdinalIgnoreCase) || SheetRecognizeSummaryText.Text.Contains("OMR 结果"))
            {
                SheetRecognizeSummaryText.Text = isEnglish ? "OMR Result: -" : "OMR 结果: -";
            }

            if (string.IsNullOrWhiteSpace(OmrBestReasonText.Text) || OmrBestReasonText.Text.Contains("Best Candidate", StringComparison.OrdinalIgnoreCase) || OmrBestReasonText.Text.Contains("最优候选"))
            {
                OmrBestReasonText.Text = isEnglish ? "Best Candidate: -" : "最优候选: -";
            }

            UpdateAudioResultEmptyState();
            UpdateOmrCandidatesEmptyState();
        }

        private static void SetIconButtonContent(Button button, string glyph, string text)
        {
            button.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new FontIcon
                    {
                        Glyph = glyph,
                        FontSize = 15,
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = text,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            };
        }

        private static void SetComboBoxItemContent(ComboBoxItem item, string glyph, string text)
        {
            item.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon
                    {
                        Glyph = glyph,
                        FontSize = 14,
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = text,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            };
        }

        private void UpdateAudioModeHelpText()
        {
            if (AudioModeHelpText == null)
            {
                return;
            }

            bool isEnglish = IsEnglishUi();
            string helpText = _audioRecognitionMode switch
            {
                AudioRecognitionMode.Balanced => isEnglish
                    ? "General single-instrument or light accompaniment. Medium note count and moderate filtering."
                    : "适合单乐器或轻伴奏。音符数量中等，过滤和保留比较折中。",
                AudioRecognitionMode.Dense => isEnglish
                    ? "Keeps more notes and low register material. Use when missing notes matter more than extra noise."
                    : "保留更多音和中低音。适合漏音严重时尝试，但杂音会更多。",
                _ => isEnglish
                    ? "Best for full songs with drums/accompaniment. Prioritizes the lead melody and suppresses bass octave errors."
                    : "适合完整歌曲/有鼓和伴奏。优先保主旋律，减少被低音伴奏拉低八度。"
            };
            AudioModeHelpText.Text = helpText;
            AudioModeInfoBar.Message = helpText;
        }

        private AudioRecognitionMode GetSelectedAudioRecognitionMode()
        {
            if (AudioRecognitionModeComboBox.SelectedItem is ComboBoxItem item)
            {
                string tag = item.Tag?.ToString() ?? string.Empty;
                if (Enum.TryParse(tag, out AudioRecognitionMode mode))
                {
                    return mode;
                }
            }

            return AudioRecognitionMode.MelodyFocus;
        }

        private void SetSelectedAudioRecognitionMode(AudioRecognitionMode mode)
        {
            _audioRecognitionMode = mode;
            foreach (object obj in AudioRecognitionModeComboBox.Items)
            {
                if (obj is ComboBoxItem item
                    && string.Equals(item.Tag?.ToString(), mode.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    AudioRecognitionModeComboBox.SelectedItem = item;
                    break;
                }
            }

            UpdateAudioModeHelpText();
        }

        private void RefreshAudioModeSelectionBox()
        {
            ComboBoxItem? selectedItem = AudioRecognitionModeComboBox.SelectedItem as ComboBoxItem;
            if (selectedItem == null)
            {
                return;
            }

            AudioRecognitionModeComboBox.SelectedItem = null;
            AudioRecognitionModeComboBox.SelectedItem = selectedItem;
        }

        private static string GetAudioRecognitionModeName(AudioRecognitionMode mode)
        {
            bool isEnglish = IsEnglishUi();
            return mode switch
            {
                AudioRecognitionMode.Balanced => isEnglish ? "Balanced" : "平衡",
                AudioRecognitionMode.Dense => isEnglish ? "High Recall / Low Notes" : "高召回/低音保留",
                _ => isEnglish ? "Melody Focus" : "旋律优先"
            };
        }

        private void AudioRecognitionModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _audioRecognitionMode = GetSelectedAudioRecognitionMode();
            UpdateAudioModeHelpText();
            if (RecognizeSummaryText == null || DetectedNotesPreviewTextBox == null || SelectedAudioPathText == null)
            {
                return;
            }

            SaveAudioStateToCache();
        }

        private void AttachRunningAudioIfNeeded()
        {
            Task<IReadOnlyList<DetectedAudioNote>>? task;
            string? input;
            AudioRecognitionMode mode;
            string text;
            IReadOnlyList<DetectedAudioNote>? finishedResult;
            lock (s_audioRunSync)
            {
                task = s_runningAudioTask;
                input = s_runningAudioInputPath;
                mode = s_runningAudioMode;
                text = s_runningAudioProgressText;
                finishedResult = s_runningAudioResult;
            }

            if (task == null)
            {
                if (finishedResult != null)
                {
                    SetSelectedAudioRecognitionMode(mode);
                    ApplyDetectedAudioNotes(finishedResult, input);
                    lock (s_audioRunSync)
                    {
                        s_runningAudioResult = null;
                        s_runningAudioProgressText = string.Empty;
                    }
                }

                SetAudioProgress(false, string.IsNullOrWhiteSpace(text) ? null : text);
                return;
            }

            if (!task.IsCompleted)
            {
                if (!string.IsNullOrWhiteSpace(input))
                {
                    SetSelectedAudioRecognitionMode(mode);
                    _selectedAudioPath = input;
                    SelectedAudioPathText.Text = $"{Loc("已选择", "Selected")}: {input}";
                }

                SetAudioProgress(true, string.IsNullOrWhiteSpace(text) ? $"{Loc("识别进度", "Progress")}: 0%" : text);
                if (!ReferenceEquals(_attachedAudioWatchingTask, task))
                {
                    _attachedAudioWatchingTask = task;
                    _ = WatchRunningAudioTaskForCurrentPageAsync(task);
                }
                return;
            }

            SetAudioProgress(false, string.IsNullOrWhiteSpace(text) ? null : text);
            if (finishedResult != null)
            {
                ApplyDetectedAudioNotes(finishedResult, input);
                lock (s_audioRunSync)
                {
                    s_runningAudioResult = null;
                    s_runningAudioProgressText = string.Empty;
                }
            }
        }

        private Task<IReadOnlyList<DetectedAudioNote>> GetOrStartRunningAudioTask(
            string inputPath,
            AudioRecognitionMode mode,
            IProgress<AudioRecognitionProgress> progress)
        {
            lock (s_audioRunSync)
            {
                if (s_runningAudioTask != null && !s_runningAudioTask.IsCompleted)
                {
                    return s_runningAudioTask;
                }

                s_runningAudioInputPath = inputPath;
                s_runningAudioMode = mode;
                s_runningAudioProgressText = $"{Loc("识别进度", "Progress")}: 0%";
                s_runningAudioResult = null;
                s_runningAudioCancellation?.Dispose();
                s_runningAudioCancellation = new CancellationTokenSource();
                CancellationToken cancellationToken = s_runningAudioCancellation.Token;
                s_runningAudioTask = Task.Run(
                    () => AudioPitchRecognizer.DetectNotesFromAudio(inputPath, progress, mode, cancellationToken),
                    cancellationToken);
                return s_runningAudioTask;
            }
        }

        private async Task ObserveRunningAudioTaskAsync(Task<IReadOnlyList<DetectedAudioNote>> task)
        {
            IReadOnlyList<DetectedAudioNote> result = await task;
            lock (s_audioRunSync)
            {
                if (ReferenceEquals(s_runningAudioTask, task))
                {
                    s_runningAudioResult = result;
                    s_runningAudioProgressText = $"{Loc("识别进度", "Progress")}: 100%";
                    s_runningAudioTask = null;
                    s_runningAudioCancellation?.Dispose();
                    s_runningAudioCancellation = null;
                }
            }
        }

        private async Task WatchRunningAudioTaskForCurrentPageAsync(Task<IReadOnlyList<DetectedAudioNote>> task)
        {
            try
            {
                while (!task.IsCompleted)
                {
                    string progressText;
                    bool isCurrentTask;
                    lock (s_audioRunSync)
                    {
                        isCurrentTask = ReferenceEquals(s_runningAudioTask, task);
                        progressText = s_runningAudioProgressText;
                    }

                    if (!isCurrentTask)
                    {
                        return;
                    }

                    SetAudioProgress(true, string.IsNullOrWhiteSpace(progressText) ? $"{Loc("识别进度", "Progress")}: 0%" : progressText);
                    await Task.Delay(120);
                }

                await ObserveRunningAudioTaskAsync(task);
                lock (s_audioRunSync)
                {
                    if (s_runningAudioResult == null && !ReferenceEquals(s_runningAudioTask, task))
                    {
                        return;
                    }
                }

                IReadOnlyList<DetectedAudioNote> result = await task;
                ApplyDetectedAudioNotes(result, _selectedAudioPath);
            }
            catch (OperationCanceledException)
            {
                lock (s_audioRunSync)
                {
                    if (ReferenceEquals(s_runningAudioTask, task))
                    {
                        s_runningAudioTask = null;
                        s_runningAudioResult = null;
                        s_runningAudioProgressText = string.Empty;
                        s_runningAudioInputPath = null;
                        s_runningAudioCancellation?.Dispose();
                        s_runningAudioCancellation = null;
                    }
                }
            }
            catch (Exception ex)
            {
                RecognizeSummaryText.Text = $"{Loc("分析失败", "Analysis failed")}: {ex.Message}";
                SaveAudioStateToCache();
            }
            finally
            {
                if (ReferenceEquals(_attachedAudioWatchingTask, task))
                {
                    _attachedAudioWatchingTask = null;
                }

                bool stillRunning;
                string text;
                lock (s_audioRunSync)
                {
                    stillRunning = s_runningAudioTask != null && !s_runningAudioTask.IsCompleted;
                    text = s_runningAudioProgressText;
                }

                SetAudioProgress(stillRunning, stillRunning ? text : null);
                SaveAudioStateToCache();
            }
        }

        private void ApplyDetectedAudioNotes(IReadOnlyList<DetectedAudioNote> notes, string? sourcePath)
        {
            _detectedNotes = notes?.ToList() ?? new List<DetectedAudioNote>();
            if (_detectedNotes.Count == 0)
            {
                RecognizeSummaryText.Text = $"{Loc("识别结果", "Result")}: {Loc("未检测到有效音符（请尝试更干净的单旋律音频）", "No valid notes were detected (try cleaner monophonic audio)")}";
                DetectedNotesPreviewTextBox.Text = string.Empty;
                ImportDetectedNotesButton.IsEnabled = false;
                UpdateAudioResultEmptyState();
                SaveAudioStateToCache();
                return;
            }

            if (!string.IsNullOrWhiteSpace(sourcePath))
            {
                _selectedAudioPath = sourcePath;
                SelectedAudioPathText.Text = $"{Loc("已选择", "Selected")}: {sourcePath}";
            }

            int ppq = Math.Max(96, _viewModel?.Project.Ppq ?? 480);
            AudioStructureInference inference = InferAudioStructureSafe(_detectedNotes, ppq);
            RecognizeSummaryText.Text =
                $"{Loc("识别结果", "Result")}: {Loc("检测到", "Detected")} {_detectedNotes.Count} {Loc("个音符片段", "note segments")} · " +
                $"{Loc("模式", "Mode")} {GetAudioRecognitionModeName(_audioRecognitionMode)} · " +
                $"{Loc("拍号", "Meter")} {inference.Numerator}/{inference.Denominator}, 1={inference.KeyName}";

            var lines = new List<string>
            {
                Loc("序号\t音名\t频率(Hz)\t起始(s)\t时长(s)", "No.\tPitch\tFreq(Hz)\tStart(s)\tDuration(s)")
            };

            int index = 1;
            foreach (var note in _detectedNotes.Take(120))
            {
                lines.Add($"{index}\t{PitchUtils.MidiToName(note.Midi)}\t{note.FrequencyHz:F1}\t{note.StartSeconds:F2}\t{note.DurationSeconds:F2}");
                index++;
            }

            if (_detectedNotes.Count > 120)
            {
                lines.Add(Loc($"... 其余 {_detectedNotes.Count - 120} 条已省略", $"... {_detectedNotes.Count - 120} more rows omitted"));
            }

            DetectedNotesPreviewTextBox.Text = string.Join(Environment.NewLine, lines);
            ImportDetectedNotesButton.IsEnabled = true;
            UpdateAudioResultEmptyState();
            SaveAudioStateToCache();
        }

        private void AttachRunningOmrIfNeeded()
        {
            Task<OmrRecognitionResult>? task;
            string? input;
            string text;
            OmrRecognitionResult? finishedResult;
            lock (s_omrRunSync)
            {
                task = s_runningOmrTask;
                input = s_runningOmrInputPath;
                text = s_runningOmrProgressText;
                finishedResult = s_runningOmrResult;
            }

            if (task == null)
            {
                if (finishedResult != null)
                {
                    _ = ApplyOmrResultAsync(finishedResult, showLowQualityHint: false);
                    lock (s_omrRunSync)
                    {
                        s_runningOmrResult = null;
                        s_runningOmrProgressText = string.Empty;
                    }
                }
                SetOmrProgress(false);
                RunOmrButton.IsEnabled = true;
                return;
            }

            if (!task.IsCompleted)
            {
                if (!string.IsNullOrWhiteSpace(input))
                {
                    _selectedSheetPath = input;
                    SelectedSheetPathText.Text = $"已选择: {input}";
                }

                SetOmrProgress(true, string.IsNullOrWhiteSpace(text) ? "识别中..." : text);
                RunOmrButton.IsEnabled = false;
                if (!ReferenceEquals(_attachedWatchingTask, task))
                {
                    _attachedWatchingTask = task;
                    _ = WatchRunningOmrTaskForCurrentPageAsync(task);
                }
                return;
            }

            // Task exists but already completed.
            SetOmrProgress(false);
            RunOmrButton.IsEnabled = true;
            if (finishedResult != null)
            {
                _ = ApplyOmrResultAsync(finishedResult, showLowQualityHint: false);
                lock (s_omrRunSync)
                {
                    s_runningOmrResult = null;
                    s_runningOmrProgressText = string.Empty;
                }
            }
        }

        private Task<OmrRecognitionResult> GetOrStartRunningOmrTask(string inputPath, IProgress<OmrProgressInfo> progress)
        {
            lock (s_omrRunSync)
            {
                if (s_runningOmrTask != null
                    && !s_runningOmrTask.IsCompleted
                    && string.Equals(s_runningOmrInputPath, inputPath, StringComparison.OrdinalIgnoreCase))
                {
                    return s_runningOmrTask;
                }

                s_runningOmrInputPath = inputPath;
                s_runningOmrProgressText = "准备识别...";
                s_runningOmrResult = null;
                s_runningOmrTask = _omrService.RecognizeToMusicXmlAsync(inputPath, progress);
                return s_runningOmrTask;
            }
        }

        private async Task ObserveRunningOmrTaskAsync(Task<OmrRecognitionResult> task)
        {
            OmrRecognitionResult result = await task;
            lock (s_omrRunSync)
            {
                if (ReferenceEquals(s_runningOmrTask, task))
                {
                    s_runningOmrResult = result;
                    s_runningOmrProgressText = "识别完成";
                    s_runningOmrTask = null;
                }
            }
        }

        private async Task WatchRunningOmrTaskForCurrentPageAsync(Task<OmrRecognitionResult> task)
        {
            try
            {
                while (!task.IsCompleted)
                {
                    string progressText;
                    lock (s_omrRunSync)
                    {
                        progressText = s_runningOmrProgressText;
                    }

                    SetOmrProgress(true, string.IsNullOrWhiteSpace(progressText) ? "识别中..." : progressText);
                    await Task.Delay(150);
                }

                await ObserveRunningOmrTaskAsync(task);
                OmrRecognitionResult result = await task;
                await ApplyOmrResultAsync(result, showLowQualityHint: false);
            }
            catch (Exception ex)
            {
                SheetRecognizeSummaryText.Text = $"OMR 结果: 识别失败 - {ex.Message}";
                SaveOmrStateToCache();
            }
            finally
            {
                if (ReferenceEquals(_attachedWatchingTask, task))
                {
                    _attachedWatchingTask = null;
                }

                RunOmrButton.IsEnabled = true;
                bool stillRunning;
                string progressText;
                lock (s_omrRunSync)
                {
                    stillRunning = s_runningOmrTask != null && !s_runningOmrTask.IsCompleted;
                    progressText = s_runningOmrProgressText;
                }

                SetOmrProgress(stillRunning, stillRunning ? progressText : null);
                await RefreshRuntimeStatusAsync();
            }
        }

        private void SaveOmrStateToCache()
        {
            s_cache = new OmrPageStateCache
            {
                SelectedSheetPath = _selectedSheetPath,
                RecognizedMusicXmlPath = _recognizedMusicXmlPath,
                LastResult = _lastOmrResult,
                SelectedCandidatePath = (OmrCandidatesListView.SelectedItem as OmrCandidateListItem)?.MusicXmlPath,
                SheetSummary = SheetRecognizeSummaryText.Text,
                BestReason = OmrBestReasonText.Text,
                Preview = OmrPreviewTextBox.Text,
                RuntimeStatus = OmrRuntimeStatusText.Text,
                IsOmrRunning = OmrProgressRing.IsActive,
                OmrProgress = OmrProgressText.Text
            };
        }

        private async Task RestoreOmrStateFromCacheAsync()
        {
            OmrPageStateCache? cache = s_cache;
            if (cache == null)
            {
                return;
            }

            _selectedSheetPath = cache.SelectedSheetPath;
            _recognizedMusicXmlPath = cache.RecognizedMusicXmlPath;
            _lastOmrResult = cache.LastResult;

            SelectedSheetPathText.Text = string.IsNullOrWhiteSpace(_selectedSheetPath)
                ? Loc("未选择图片/PDF文件", "No image/PDF selected")
                : $"{Loc("已选择", "Selected")}: {_selectedSheetPath}";
            SheetRecognizeSummaryText.Text = string.IsNullOrWhiteSpace(cache.SheetSummary)
                ? $"{Loc("OMR 结果", "OMR Result")}: -"
                : cache.SheetSummary;
            RecognizedMusicXmlPathText.Text = string.IsNullOrWhiteSpace(_recognizedMusicXmlPath)
                ? "MusicXML: -"
                : $"MusicXML: {_recognizedMusicXmlPath}";
            OmrBestReasonText.Text = string.IsNullOrWhiteSpace(cache.BestReason)
                ? $"{Loc("最优候选", "Best Candidate")}: -"
                : cache.BestReason;
            if (!string.IsNullOrWhiteSpace(cache.RuntimeStatus))
            {
                OmrRuntimeStatusText.Text = cache.RuntimeStatus;
            }

            BindOmrCandidates(_lastOmrResult?.Diagnostics);
            if (!string.IsNullOrWhiteSpace(cache.SelectedCandidatePath))
            {
                OmrCandidateListItem? item = _omrCandidates.FirstOrDefault(i => string.Equals(i.MusicXmlPath, cache.SelectedCandidatePath, StringComparison.OrdinalIgnoreCase));
                if (item != null)
                {
                    OmrCandidatesListView.SelectedItem = item;
                }
            }

            OmrPreviewTextBox.Text = cache.Preview ?? string.Empty;
            SetOmrProgress(cache.IsOmrRunning, cache.OmrProgress);
            ImportBestOmrButton.IsEnabled = _lastOmrResult?.Diagnostics.BestCandidate != null;
            ImportSelectedOmrButton.IsEnabled = OmrCandidatesListView.SelectedItem is OmrCandidateListItem selected && File.Exists(selected.MusicXmlPath);
            string? artifacts = _lastOmrResult?.Diagnostics.ArtifactRoot;
            OpenOmrArtifactsButton.IsEnabled = !string.IsNullOrWhiteSpace(artifacts) && Directory.Exists(artifacts);
            await Task.CompletedTask;
        }

        private void SaveAudioStateToCache()
        {
            bool isRunning;
            lock (s_audioRunSync)
            {
                isRunning = s_runningAudioTask != null && !s_runningAudioTask.IsCompleted;
            }

            s_audioCache = new AudioPageStateCache
            {
                SelectedAudioPath = _selectedAudioPath,
                DetectedNotes = _detectedNotes?.ToList() ?? new List<DetectedAudioNote>(),
                Mode = _audioRecognitionMode,
                Summary = RecognizeSummaryText.Text,
                Preview = DetectedNotesPreviewTextBox.Text,
                SelectedPathText = SelectedAudioPathText.Text,
                IsRunning = isRunning || _audioAnalyzing,
                Progress = AudioAnalyzeProgressText.Text
            };
        }

        private async Task RestoreAudioStateFromCacheAsync()
        {
            AudioPageStateCache? cache = s_audioCache;
            if (cache == null)
            {
                SetAudioProgress(false);
                ImportDetectedNotesButton.IsEnabled = false;
                return;
            }

            _selectedAudioPath = cache.SelectedAudioPath;
            _detectedNotes = cache.DetectedNotes?.ToList() ?? new List<DetectedAudioNote>();
            SetSelectedAudioRecognitionMode(cache.Mode);
            SelectedAudioPathText.Text = string.IsNullOrWhiteSpace(cache.SelectedPathText)
                ? Loc("未选择音频文件", "No audio selected")
                : cache.SelectedPathText!;
            RecognizeSummaryText.Text = string.IsNullOrWhiteSpace(cache.Summary)
                ? $"{Loc("识别结果", "Result")}: -"
                : cache.Summary!;
            DetectedNotesPreviewTextBox.Text = cache.Preview ?? string.Empty;
            SetAudioProgress(cache.IsRunning, cache.Progress);
            ImportDetectedNotesButton.IsEnabled = _detectedNotes.Count > 0;
            UpdateAudioResultEmptyState();

            await Task.CompletedTask;
        }

        private void ClearAudioButton_Click(object sender, RoutedEventArgs e)
        {
            CancelRunningAudioRecognition();
            _selectedAudioPath = null;
            _detectedNotes = Array.Empty<DetectedAudioNote>();
            SelectedAudioPathText.Text = Loc("未选择音频文件", "No audio selected");
            RecognizeSummaryText.Text = Loc("选择音频并点击“开始识别”以查看结果。", "Select audio and click Analyze to view results.");
            DetectedNotesPreviewTextBox.Text = string.Empty;
            AudioAnalyzeProgressText.Text = string.Empty;
            ImportDetectedNotesButton.IsEnabled = false;
            SetAudioProgress(false);
            UpdateAudioResultEmptyState();
            SaveAudioStateToCache();
        }

        private static void CancelRunningAudioRecognition()
        {
            lock (s_audioRunSync)
            {
                s_runningAudioCancellation?.Cancel();
                s_runningAudioTask = null;
                s_runningAudioInputPath = null;
                s_runningAudioProgressText = string.Empty;
                s_runningAudioResult = null;
            }
        }

        private async void SelectAudioButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (App.MainWindow == null) return;

                var picker = new FileOpenPicker
                {
                    SuggestedStartLocation = PickerLocationId.MusicLibrary,
                    ViewMode = PickerViewMode.List
                };
                picker.FileTypeFilter.Add(".mp3");
                picker.FileTypeFilter.Add(".wav");
                picker.FileTypeFilter.Add(".wave");

                WinRT.Interop.InitializeWithWindow.Initialize(
                    picker,
                    WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

                StorageFile? file = await picker.PickSingleFileAsync();
                if (file == null)
                {
                    return;
                }

                _selectedAudioPath = file.Path;
                SelectedAudioPathText.Text = $"{Loc("已选择", "Selected")}: {_selectedAudioPath}";
                RecognizeSummaryText.Text = $"{Loc("识别结果", "Result")}: {Loc("等待分析", "Ready to analyze")}";
                DetectedNotesPreviewTextBox.Text = string.Empty;
                _detectedNotes = Array.Empty<DetectedAudioNote>();
                ImportDetectedNotesButton.IsEnabled = false;
                UpdateAudioResultEmptyState();
                SaveAudioStateToCache();
            }
            catch (Exception ex)
            {
                RecognizeSummaryText.Text = $"{Loc("选择失败", "Selection failed")}: {ex.Message}";
                SaveAudioStateToCache();
            }
        }

        private async void AnalyzeAudioButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_selectedAudioPath) || !File.Exists(_selectedAudioPath))
            {
                RecognizeSummaryText.Text = $"{Loc("识别结果", "Result")}: {Loc("请先选择 MP3/WAV 音频文件", "Please select an MP3/WAV file first")}";
                return;
            }

            try
            {
                AudioRecognitionMode currentMode = GetSelectedAudioRecognitionMode();
                lock (s_audioRunSync)
                {
                    if (s_runningAudioTask != null
                        && !s_runningAudioTask.IsCompleted
                        && (!string.Equals(s_runningAudioInputPath, _selectedAudioPath, StringComparison.OrdinalIgnoreCase)
                            || s_runningAudioMode != currentMode))
                    {
                        RecognizeSummaryText.Text = $"{Loc("识别结果", "Result")}: {Loc("已有音频在分析中，请稍候完成。", "Another audio is being analyzed. Please wait.")}";
                        return;
                    }
                }

                SetAudioProgress(true, $"{Loc("识别进度", "Progress")}: 0%");
                string currentPath = _selectedAudioPath!;
                var progress = new Progress<AudioRecognitionProgress>(p =>
                {
                    lock (s_audioRunSync)
                    {
                        if (s_runningAudioTask == null
                            || !string.Equals(s_runningAudioInputPath, currentPath, StringComparison.OrdinalIgnoreCase)
                            || s_runningAudioMode != currentMode)
                        {
                            return;
                        }
                    }

                    string stage = p.Stage switch
                    {
                        "Analyzing" => Loc("分析中", "Analyzing"),
                        "Grouping" => Loc("合并中", "Grouping"),
                        "Done" => Loc("完成", "Done"),
                        _ => p.Stage
                    };
                    string progressText = $"{Loc("识别进度", "Progress")}: {Math.Clamp(p.Percent, 0, 100)}% ({stage})";
                    lock (s_audioRunSync)
                    {
                        if (s_runningAudioTask == null
                            || !string.Equals(s_runningAudioInputPath, currentPath, StringComparison.OrdinalIgnoreCase)
                            || s_runningAudioMode != currentMode)
                        {
                            return;
                        }

                        s_runningAudioProgressText = progressText;
                    }
                    SetAudioProgress(true, progressText);
                });

                Task<IReadOnlyList<DetectedAudioNote>> runningTask = GetOrStartRunningAudioTask(currentPath, currentMode, progress);
                _attachedAudioWatchingTask = runningTask;
                await WatchRunningAudioTaskForCurrentPageAsync(runningTask);
            }
            catch (Exception ex)
            {
                RecognizeSummaryText.Text = $"{Loc("分析失败", "Analysis failed")}: {ex.Message}";
                SaveAudioStateToCache();
            }
        }

        private async void ImportDetectedNotesButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel == null)
            {
                RecognizeSummaryText.Text = $"{Loc("导入失败", "Import failed")}: {Loc("ViewModel 未初始化", "ViewModel is not initialized")}";
                return;
            }

            if (_detectedNotes.Count == 0)
            {
                RecognizeSummaryText.Text = $"{Loc("导入失败", "Import failed")}: {Loc("请先完成音频分析", "Analyze audio first")}";
                return;
            }

            if (ProjectHasMeaningfulContent(_viewModel.Project))
            {
                bool confirm = await ConfirmClearBeforeImportAsync();
                if (!confirm)
                {
                    RecognizeSummaryText.Text = $"{Loc("已取消导入", "Import canceled")}";
                    return;
                }
            }

            int ppq = Math.Max(96, _viewModel.Project.Ppq);
            AudioStructureInference inference = InferAudioStructureSafe(_detectedNotes, ppq);
            ResetProjectForAudioImport(_viewModel.Project, ppq, inference);
            _viewModel.Project.LayoutMeasuresPerSystemOverride = EstimateAudioImportMeasuresPerSystem(_detectedNotes, inference);

            double bpm = Math.Max(20, _viewModel.Project.Bpm);
            double ticksPerSecond = (bpm / 60.0) * ppq;
            int grid = Math.Max(1, ppq / 8);
            var importNotes = SelectMelodyPriorityImportNotes(_detectedNotes, ticksPerSecond, grid);
            foreach (var detected in importNotes)
            {
                int startTick = QuantizeTick((int)Math.Round(detected.StartSeconds * ticksPerSecond), grid);
                int endTick = QuantizeTick((int)Math.Round((detected.StartSeconds + detected.DurationSeconds) * ticksPerSecond), grid);
                int durationTicks = Math.Max(grid, endTick - startTick);
                int midi = Math.Clamp(detected.Midi, 24, 108);
                int keyFifths = GetEffectiveKeySignatureFifthsAtTick(_viewModel.Project, startTick);
                _viewModel.Project.Notes.Add(new NoteEvent
                {
                    Midi = midi,
                    StartTick = Math.Max(0, startTick),
                    DurationTicks = durationTicks,
                    BaseDurationTicks = durationTicks,
                    AugmentationDots = 0,
                    IsRest = false,
                    Voice = 1,
                    Accidental = ResolveImportedAccidental(midi, keyFifths),
                    IsStaccato = false,
                    IsStaccatissimo = false,
                    BeamGroupId = 0,
                    PreferTrebleStaff = midi >= 60
                });
            }

            _viewModel.Project.Notes = _viewModel.Project.Notes
                .OrderBy(n => n.StartTick)
                .ThenByDescending(n => n.Midi)
                .ToList();

            if (!string.IsNullOrWhiteSpace(_selectedAudioPath))
            {
                _viewModel.Project.Title = Path.GetFileNameWithoutExtension(_selectedAudioPath);
            }

            _viewModel.TouchProject();
            _viewModel.SetStatus($"{Loc("已导入识别音符", "Imported detected notes")}: {_viewModel.Project.Notes.Count}");
            RecognizeSummaryText.Text =
                $"{Loc("已导入到五线谱页", "Imported to staff")}: {_viewModel.Project.Notes.Count} {Loc("个音符", "notes")} · " +
                $"{inference.Numerator}/{inference.Denominator}, 1={inference.KeyName}";
            SaveAudioStateToCache();

            if (App.MainWindow is MainWindow mainWindow)
            {
                mainWindow.NavigateToPage("editor");
            }
        }

        private static IReadOnlyList<DetectedAudioNote> SelectMelodyPriorityImportNotes(
            IReadOnlyList<DetectedAudioNote> notes,
            double ticksPerSecond,
            int grid)
        {
            if (notes.Count <= 1)
            {
                return notes;
            }

            return notes
                .GroupBy(note => QuantizeTick((int)Math.Round(note.StartSeconds * ticksPerSecond), grid))
                .Select(group => group
                    .OrderByDescending(ScoreImportMelodyCandidate)
                    .ThenByDescending(note => note.DurationSeconds)
                    .ThenByDescending(note => note.Midi)
                    .First())
                .OrderBy(note => note.StartSeconds)
                .ToList();
        }

        private static double ScoreImportMelodyCandidate(DetectedAudioNote note)
        {
            int midi = Math.Clamp(note.Midi, 24, 108);
            double durationScore = Math.Clamp(note.DurationSeconds, 0.04d, 0.7d) * 0.25d;
            double registerScore = midi switch
            {
                >= 60 and <= 88 => 0.42d + Math.Max(0d, 1d - Math.Abs(midi - 76) / 28d) * 0.20d,
                >= 55 and < 60 => 0.10d,
                > 88 and <= 96 => 0.18d,
                _ => -0.20d
            };
            return registerScore + durationScore;
        }

        private static bool ProjectHasMeaningfulContent(ScoreProject project)
        {
            if (project == null)
            {
                return false;
            }

            if (project.Notes.Count > 0 || project.ExpressionMarks.Count > 0 || project.TimeSignatureChanges.Count > 0 || project.KeySignatureChanges.Count > 0)
            {
                return true;
            }

            if (project.KeySignature.Fifths != 0 || project.KeySignature.Mode != KeyMode.Major)
            {
                return true;
            }

            if (project.TimeSignature.Numerator != 4 || project.TimeSignature.Denominator != 4)
            {
                return true;
            }

            if (project.Bpm != 120)
            {
                return true;
            }

            return false;
        }

        private async Task<bool> ConfirmClearBeforeImportAsync()
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = Loc("五线谱页已有内容", "Staff page already has content"),
                Content = Loc("导入会先清空当前五线谱页（包含音符、谱面记号、调号、拍号和速度）。是否继续？", "Import will clear current staff page data first (notes, score marks, key, meter, tempo). Continue?"),
                PrimaryButtonText = Loc("继续导入", "Import"),
                CloseButtonText = Loc("取消", "Cancel"),
                DefaultButton = ContentDialogButton.Close
            };

            ContentDialogResult result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary;
        }

        private static void ResetProjectForAudioImport(ScoreProject project, int ppq, AudioStructureInference inference)
        {
            project.Notes.Clear();
            project.ExpressionMarks.Clear();
            project.TimeSignatureChanges.Clear();
            project.KeySignatureChanges.Clear();
            project.StaffClefs.Clear();
            project.LayoutSystemMeasureCounts.Clear();
            project.LayoutBarlineOffsets.Clear();
            project.LayoutMeasuresPerSystemOverride = 0;
            project.LayoutAutoMeasuresPerSystem = 0;
            project.Ppq = Math.Max(96, ppq);

            project.Bpm = Math.Clamp(inference.Bpm, 20, 300);
            project.TimeSignature = new TimeSignature(inference.Numerator, inference.Denominator);
            project.KeySignature = new KeySignature(inference.KeyFifths, inference.KeyMode);

            if (inference.TimeSignatureChanges.Count > 0)
            {
                project.TimeSignatureChanges.AddRange(inference.TimeSignatureChanges);
            }

            if (inference.KeySignatureChanges.Count > 0)
            {
                project.KeySignatureChanges.AddRange(inference.KeySignatureChanges);
            }
        }

        private static int EstimateAudioImportMeasuresPerSystem(IReadOnlyList<DetectedAudioNote> notes, AudioStructureInference inference)
        {
            if (notes.Count == 0)
            {
                return 0;
            }

            double endSeconds = notes.Max(n => Math.Max(0d, n.StartSeconds + n.DurationSeconds));
            double beatsPerSecond = Math.Clamp(inference.Bpm, 20, 300) / 60d;
            double quarterBeatsPerMeasure = Math.Max(0.25d, inference.Numerator * (4d / Math.Max(1, inference.Denominator)));
            int estimatedMeasures = Math.Max(1, (int)Math.Ceiling(endSeconds * beatsPerSecond / quarterBeatsPerMeasure));
            if (estimatedMeasures >= 160)
            {
                return 6;
            }

            if (estimatedMeasures >= 96)
            {
                return 5;
            }

            if (estimatedMeasures >= 60)
            {
                return 4;
            }

            return 0;
        }

        private static int GetEffectiveKeySignatureFifthsAtTick(ScoreProject project, int tick)
        {
            int fifths = project.KeySignature.Fifths;
            foreach (var change in project.KeySignatureChanges.OrderBy(c => c.Tick))
            {
                if (change.Tick > tick)
                {
                    break;
                }

                fifths = change.Fifths;
            }

            return Math.Clamp(fifths, -7, 7);
        }

        private static NoteAccidental ResolveImportedAccidental(int midi, int keySignatureFifths)
        {
            foreach (NoteAccidental accidental in GetPreferredAccidentalOrder(keySignatureFifths))
            {
                if (GetEffectiveMidiForAccidental(midi, accidental, keySignatureFifths) == midi)
                {
                    return accidental;
                }
            }

            return NoteAccidental.Natural;
        }

        private static IEnumerable<NoteAccidental> GetPreferredAccidentalOrder(int keySignatureFifths)
        {
            yield return NoteAccidental.None;
            yield return NoteAccidental.Natural;

            if (keySignatureFifths < 0)
            {
                yield return NoteAccidental.Flat;
                yield return NoteAccidental.Sharp;
            }
            else
            {
                yield return NoteAccidental.Sharp;
                yield return NoteAccidental.Flat;
            }

            yield return NoteAccidental.DoubleSharp;
            yield return NoteAccidental.DoubleFlat;
        }

        private static int GetEffectiveMidiForAccidental(int midi, NoteAccidental accidental, int keySignatureFifths)
        {
            int naturalMidi = QuantizeToNaturalMidi(midi - GetAccidentalSemitoneOffset(accidental));
            int keyOffset = GetKeySignatureSemitoneOffset(naturalMidi, keySignatureFifths);
            int offset = accidental switch
            {
                NoteAccidental.DoubleSharp => 2,
                NoteAccidental.Sharp => keyOffset > 0 ? keyOffset + 1 : 1,
                NoteAccidental.Flat => keyOffset < 0 ? keyOffset - 1 : -1,
                NoteAccidental.DoubleFlat => -2,
                NoteAccidental.Natural => 0,
                _ => keyOffset
            };

            return Math.Clamp(naturalMidi + offset, 0, 127);
        }

        private static int GetAccidentalSemitoneOffset(NoteAccidental accidental)
        {
            return accidental switch
            {
                NoteAccidental.DoubleSharp => 2,
                NoteAccidental.Sharp => 1,
                NoteAccidental.Flat => -1,
                NoteAccidental.DoubleFlat => -2,
                _ => 0
            };
        }

        private static int QuantizeToNaturalMidi(int midi)
        {
            int clamped = Math.Clamp(midi, 0, 127);
            int pitchClass = clamped % 12;
            int[] naturalPitchClasses = { 0, 2, 4, 5, 7, 9, 11 };
            int best = naturalPitchClasses[0];
            int bestDiff = Math.Abs(pitchClass - best);
            for (int i = 1; i < naturalPitchClasses.Length; i++)
            {
                int candidate = naturalPitchClasses[i];
                int diff = Math.Abs(pitchClass - candidate);
                if (diff < bestDiff)
                {
                    best = candidate;
                    bestDiff = diff;
                }
            }

            return clamped + (best - pitchClass);
        }

        private static int GetKeySignatureSemitoneOffset(int naturalMidi, int fifths)
        {
            int pitchClass = QuantizeToNaturalMidi(naturalMidi) % 12;
            if (fifths > 0)
            {
                int[] sharpOrder = { 5, 0, 7, 2, 9, 4, 11 }; // F C G D A E B
                int count = Math.Min(fifths, sharpOrder.Length);
                for (int i = 0; i < count; i++)
                {
                    if (pitchClass == sharpOrder[i]) return 1;
                }
            }
            else if (fifths < 0)
            {
                int[] flatOrder = { 11, 4, 9, 2, 7, 0, 5 }; // B E A D G C F
                int count = Math.Min(Math.Abs(fifths), flatOrder.Length);
                for (int i = 0; i < count; i++)
                {
                    if (pitchClass == flatOrder[i]) return -1;
                }
            }

            return 0;
        }

        private static AudioStructureInference InferAudioStructureSafe(IReadOnlyList<DetectedAudioNote> notes, int ppq)
        {
            try
            {
                return InferAudioStructure(notes, ppq);
            }
            catch
            {
                return new AudioStructureInference();
            }
        }

        private static AudioStructureInference InferAudioStructure(IReadOnlyList<DetectedAudioNote> notes, int ppq)
        {
            var inference = new AudioStructureInference();
            if (notes == null || notes.Count == 0)
            {
                return inference;
            }

            double beatSeconds = EstimateBeatSeconds(notes);
            inference.Bpm = Math.Clamp((int)Math.Round(60d / Math.Max(0.24d, beatSeconds)), 20, 300);
            (inference.Numerator, inference.Denominator) = EstimateTimeSignature(notes, beatSeconds);
            (inference.KeyFifths, inference.KeyMode, inference.KeyName) = EstimateKeySignature(notes);

            int ticksPerSecond = Math.Max(1, (int)Math.Round(inference.Bpm / 60d * Math.Max(96, ppq)));
            foreach (var change in EstimateKeySignatureChanges(notes, ticksPerSecond, inference.KeyFifths, inference.KeyMode))
            {
                inference.KeySignatureChanges.Add(change);
            }

            foreach (var change in EstimateTimeSignatureChanges(notes, beatSeconds, ticksPerSecond, inference.Numerator, inference.Denominator))
            {
                inference.TimeSignatureChanges.Add(change);
            }

            return inference;
        }

        private static double EstimateBeatSeconds(IReadOnlyList<DetectedAudioNote> notes)
        {
            var onsets = notes
                .Select(n => Math.Round(Math.Max(0d, n.StartSeconds), 3))
                .Distinct()
                .OrderBy(t => t)
                .ToList();
            if (onsets.Count < 3)
            {
                return 0.5d;
            }

            var intervals = new List<double>(onsets.Count);
            for (int i = 1; i < onsets.Count; i++)
            {
                double delta = onsets[i] - onsets[i - 1];
                if (delta >= 0.07d && delta <= 1.8d)
                {
                    intervals.Add(delta);
                }
            }

            if (intervals.Count == 0)
            {
                return 0.5d;
            }

            var candidates = new List<double>(intervals.Count * 3);
            foreach (double ioi in intervals)
            {
                candidates.Add(NormalizeBeatCandidate(ioi));
                candidates.Add(NormalizeBeatCandidate(ioi * 2d));
                candidates.Add(NormalizeBeatCandidate(ioi * 0.5d));
            }

            double best = 0.5d;
            double bestScore = double.MinValue;
            foreach (double candidate in candidates.DistinctBy(c => Math.Round(c, 3)))
            {
                double score = 0d;
                foreach (double onset in onsets)
                {
                    double phase = onset / candidate;
                    double dist = Math.Abs(phase - Math.Round(phase));
                    score += Math.Max(0d, 1d - dist * 4d);
                }

                foreach (double interval in intervals)
                {
                    double ratio = interval / candidate;
                    double dist = Math.Abs(ratio - Math.Round(ratio));
                    score += Math.Max(0d, 0.75d - dist * 3d);
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            return Math.Clamp(best, 0.24d, 1.4d);
        }

        private static double NormalizeBeatCandidate(double seconds)
        {
            double value = Math.Clamp(seconds, 0.08d, 2.4d);
            while (value < 0.24d) value *= 2d;
            while (value > 1.4d) value *= 0.5d;
            return value;
        }

        private static (int Numerator, int Denominator) EstimateTimeSignature(IReadOnlyList<DetectedAudioNote> notes, double beatSeconds)
        {
            if (notes.Count < 5)
            {
                return (4, 4);
            }

            double start = Math.Max(0d, notes.Min(n => n.StartSeconds));
            var candidates = new List<(int Numerator, int Denominator)> { (4, 4), (3, 4), (6, 8) };
            double bestScore = double.MinValue;
            (int Numerator, int Denominator) best = (4, 4);

            foreach (var meter in candidates)
            {
                double beatsPerMeasure = meter.Numerator == 6 && meter.Denominator == 8 ? 2d : meter.Numerator;
                double measureSeconds = Math.Max(beatSeconds * beatsPerMeasure, 0.5d);
                double score = 0d;
                foreach (var note in notes)
                {
                    double local = (note.StartSeconds - start) / measureSeconds;
                    double downbeatDist = Math.Abs(local - Math.Round(local));
                    double weight = Math.Max(0.25d, note.DurationSeconds);
                    score += Math.Max(0d, 1d - downbeatDist * 3.6d) * weight;
                }

                if (meter == (6, 8))
                {
                    double shortRatio = notes.Count(n => n.DurationSeconds <= beatSeconds * 0.55d) / (double)Math.Max(1, notes.Count);
                    score += shortRatio * 2.6d;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = meter;
                }
            }

            return best;
        }

        private static IEnumerable<TimeSignatureChange> EstimateTimeSignatureChanges(
            IReadOnlyList<DetectedAudioNote> notes,
            double beatSeconds,
            int ticksPerSecond,
            int baseNumerator,
            int baseDenominator)
        {
            if (notes.Count < 24)
            {
                return Array.Empty<TimeSignatureChange>();
            }

            double windowSeconds = Math.Max(beatSeconds * 16d, 7.5d);
            double stepSeconds = Math.Max(beatSeconds * 8d, 3.6d);
            double first = notes.Min(n => n.StartSeconds);
            double last = notes.Max(n => n.StartSeconds + n.DurationSeconds);
            var windows = new List<(double Start, int Num, int Den)>();
            for (double t = first; t + windowSeconds <= last; t += stepSeconds)
            {
                var subset = notes.Where(n => n.StartSeconds >= t && n.StartSeconds < t + windowSeconds).ToList();
                if (subset.Count < 10)
                {
                    continue;
                }

                var meter = EstimateTimeSignature(subset, beatSeconds);
                windows.Add((t, meter.Numerator, meter.Denominator));
            }

            if (windows.Count < 2)
            {
                return Array.Empty<TimeSignatureChange>();
            }

            var output = new List<TimeSignatureChange>();
            int currentNum = baseNumerator;
            int currentDen = baseDenominator;
            int run = 0;
            for (int i = 0; i < windows.Count; i++)
            {
                bool changed = windows[i].Num != currentNum || windows[i].Den != currentDen;
                if (changed)
                {
                    run++;
                    if (run >= 2)
                    {
                        int tick = Math.Max(0, (int)Math.Round(windows[i - 1].Start * ticksPerSecond));
                        output.Add(new TimeSignatureChange
                        {
                            Tick = tick,
                            Numerator = windows[i].Num,
                            Denominator = windows[i].Den
                        });
                        currentNum = windows[i].Num;
                        currentDen = windows[i].Den;
                        run = 0;
                    }
                }
                else
                {
                    run = 0;
                }
            }

            return output
                .GroupBy(c => c.Tick)
                .Select(g => g.First())
                .OrderBy(c => c.Tick)
                .ToList();
        }

        private static (int Fifths, KeyMode Mode, string Name) EstimateKeySignature(IReadOnlyList<DetectedAudioNote> notes)
        {
            if (notes.Count == 0)
            {
                return (0, KeyMode.Major, "C");
            }

            double[] profileMajor = { 6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88 };
            double[] profileMinor = { 6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17 };
            var histogram = new double[12];
            foreach (var note in notes)
            {
                int pc = ((note.Midi % 12) + 12) % 12;
                histogram[pc] += Math.Max(0.1d, note.DurationSeconds);
            }

            double bestScore = double.MinValue;
            int bestPc = 0;
            KeyMode bestMode = KeyMode.Major;
            for (int tonic = 0; tonic < 12; tonic++)
            {
                double majorScore = ScoreKeyCorrelation(histogram, profileMajor, tonic);
                if (majorScore > bestScore)
                {
                    bestScore = majorScore;
                    bestPc = tonic;
                    bestMode = KeyMode.Major;
                }

                double minorScore = ScoreKeyCorrelation(histogram, profileMinor, tonic);
                if (minorScore > bestScore)
                {
                    bestScore = minorScore;
                    bestPc = tonic;
                    bestMode = KeyMode.Minor;
                }
            }

            int fifths = bestMode == KeyMode.Major
                ? MajorPcToFifths(bestPc)
                : MinorPcToFifths(bestPc);
            string keyName = KeyNameFromFifths(fifths, bestMode);
            return (Math.Clamp(fifths, -7, 7), bestMode, keyName);
        }

        private static IEnumerable<KeySignatureChange> EstimateKeySignatureChanges(
            IReadOnlyList<DetectedAudioNote> notes,
            int ticksPerSecond,
            int baseFifths,
            KeyMode baseMode)
        {
            if (notes.Count < 20)
            {
                return Array.Empty<KeySignatureChange>();
            }

            double medianDuration = notes
                .Select(n => Math.Max(0.06d, n.DurationSeconds))
                .OrderBy(v => v)
                .ElementAt(notes.Count / 2);
            double windowSeconds = Math.Clamp(medianDuration * 24d, 5d, 18d);
            double stepSeconds = windowSeconds * 0.5d;
            double first = notes.Min(n => n.StartSeconds);
            double last = notes.Max(n => n.StartSeconds + n.DurationSeconds);

            var windows = new List<(double Start, int Fifths, KeyMode Mode)>();
            for (double t = first; t + windowSeconds <= last; t += stepSeconds)
            {
                var subset = notes.Where(n => n.StartSeconds >= t && n.StartSeconds < t + windowSeconds).ToList();
                if (subset.Count < 8)
                {
                    continue;
                }

                var key = EstimateKeySignature(subset);
                windows.Add((t, key.Fifths, key.Mode));
            }

            if (windows.Count < 3)
            {
                return Array.Empty<KeySignatureChange>();
            }

            var output = new List<KeySignatureChange>();
            int currentFifths = baseFifths;
            KeyMode currentMode = baseMode;
            int run = 0;

            for (int i = 0; i < windows.Count; i++)
            {
                bool changed = windows[i].Fifths != currentFifths || windows[i].Mode != currentMode;
                if (changed)
                {
                    run++;
                    if (run >= 2)
                    {
                        int tick = Math.Max(0, (int)Math.Round(windows[i - 1].Start * ticksPerSecond));
                        output.Add(new KeySignatureChange
                        {
                            Tick = tick,
                            Fifths = windows[i].Fifths,
                            Mode = windows[i].Mode
                        });
                        currentFifths = windows[i].Fifths;
                        currentMode = windows[i].Mode;
                        run = 0;
                    }
                }
                else
                {
                    run = 0;
                }
            }

            return output
                .GroupBy(c => c.Tick)
                .Select(g => g.First())
                .OrderBy(c => c.Tick)
                .ToList();
        }

        private static double ScoreKeyCorrelation(IReadOnlyList<double> histogram, IReadOnlyList<double> profile, int tonic)
        {
            double sum = 0d;
            for (int i = 0; i < 12; i++)
            {
                int pc = (i + tonic) % 12;
                sum += histogram[pc] * profile[i];
            }

            return sum;
        }

        private static int MajorPcToFifths(int tonicPc)
        {
            return tonicPc switch
            {
                0 => 0,
                7 => 1,
                2 => 2,
                9 => 3,
                4 => 4,
                11 => 5,
                6 => 6,
                1 => 7,
                5 => -1,
                10 => -2,
                3 => -3,
                8 => -4,
                _ => 0
            };
        }

        private static int MinorPcToFifths(int tonicPc)
        {
            return tonicPc switch
            {
                9 => 0,
                4 => 1,
                11 => 2,
                6 => 3,
                1 => 4,
                8 => 5,
                3 => 6,
                10 => 7,
                2 => -1,
                7 => -2,
                0 => -3,
                5 => -4,
                _ => 0
            };
        }

        private static string KeyNameFromFifths(int fifths, KeyMode mode)
        {
            var major = new Dictionary<int, string>
            {
                [-7] = "Cb",
                [-6] = "Gb",
                [-5] = "Db",
                [-4] = "Ab",
                [-3] = "Eb",
                [-2] = "Bb",
                [-1] = "F",
                [0] = "C",
                [1] = "G",
                [2] = "D",
                [3] = "A",
                [4] = "E",
                [5] = "B",
                [6] = "F#",
                [7] = "C#"
            };
            var minor = new Dictionary<int, string>
            {
                [-7] = "Abm",
                [-6] = "Ebm",
                [-5] = "Bbm",
                [-4] = "Fm",
                [-3] = "Cm",
                [-2] = "Gm",
                [-1] = "Dm",
                [0] = "Am",
                [1] = "Em",
                [2] = "Bm",
                [3] = "F#m",
                [4] = "C#m",
                [5] = "G#m",
                [6] = "D#m",
                [7] = "A#m"
            };

            int safe = Math.Clamp(fifths, -7, 7);
            return mode == KeyMode.Major ? major[safe] : minor[safe];
        }

        private sealed class AudioStructureInference
        {
            public int Bpm { get; set; } = 120;
            public int Numerator { get; set; } = 4;
            public int Denominator { get; set; } = 4;
            public int KeyFifths { get; set; }
            public KeyMode KeyMode { get; set; } = KeyMode.Major;
            public string KeyName { get; set; } = "C";
            public List<TimeSignatureChange> TimeSignatureChanges { get; } = new();
            public List<KeySignatureChange> KeySignatureChanges { get; } = new();
        }

        private static int QuantizeTick(int tick, int grid)
        {
            if (grid <= 1) return Math.Max(0, tick);
            return Math.Max(0, (int)Math.Round(tick / (double)grid) * grid);
        }

        private static string Loc(string zh, string en)
        {
            if (IsEnglishUi())
            {
                return en;
            }

            return zh;
        }

        private static bool IsEnglishUi()
        {
            try
            {
                string lang = AppSettingsService.Instance.ResolveLanguageTag();
                if (!string.IsNullOrWhiteSpace(lang) && lang.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private async void SelectSheetFileButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (App.MainWindow == null) return;

                var picker = new FileOpenPicker
                {
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                    ViewMode = PickerViewMode.List
                };
                picker.FileTypeFilter.Add(".pdf");
                picker.FileTypeFilter.Add(".png");
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".bmp");
                picker.FileTypeFilter.Add(".tif");
                picker.FileTypeFilter.Add(".tiff");
                picker.FileTypeFilter.Add(".musicxml");
                picker.FileTypeFilter.Add(".xml");
                picker.FileTypeFilter.Add(".mxl");

                WinRT.Interop.InitializeWithWindow.Initialize(
                    picker,
                    WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

                StorageFile? file = await picker.PickSingleFileAsync();
                if (file == null)
                {
                    return;
                }

                _selectedSheetPath = file.Path;
                _recognizedMusicXmlPath = null;
                _lastOmrResult = null;
                _omrCandidates.Clear();
                UpdateOmrCandidatesEmptyState();

                SelectedSheetPathText.Text = $"{Loc("已选择", "Selected")}: {_selectedSheetPath}";
                SheetRecognizeSummaryText.Text = $"{Loc("OMR 结果", "OMR Result")}: {Loc("等待识别", "Waiting")}";
                RecognizedMusicXmlPathText.Text = "MusicXML: -";
                OmrBestReasonText.Text = $"{Loc("最优候选", "Best Candidate")}: -";
                OmrPreviewTextBox.Text = string.Empty;
                SetOmrProgress(false);
                ImportBestOmrButton.IsEnabled = false;
                ImportSelectedOmrButton.IsEnabled = false;
                OpenOmrArtifactsButton.IsEnabled = false;
                SaveOmrStateToCache();
            }
            catch (Exception ex)
            {
                SheetRecognizeSummaryText.Text = $"{Loc("OMR 结果", "OMR Result")}: {Loc("选择失败", "Selection failed")} - {ex.Message}";
            }
        }

        private async void RunOmrButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_selectedSheetPath) || !File.Exists(_selectedSheetPath))
            {
                SheetRecognizeSummaryText.Text = $"{Loc("OMR 结果", "OMR Result")}: {Loc("请先选择图片/PDF文件", "Please select image/PDF first")}";
                return;
            }

            RunOmrButton.IsEnabled = false;
            ImportBestOmrButton.IsEnabled = false;
            ImportSelectedOmrButton.IsEnabled = false;
            OpenOmrArtifactsButton.IsEnabled = false;
            SheetRecognizeSummaryText.Text = $"{Loc("OMR 结果", "OMR Result")}: {Loc("识别中", "Recognizing")}...";
            OmrPreviewTextBox.Text = string.Empty;
            SetOmrProgress(true, Loc("准备识别...", "Preparing..."));

            try
            {
                await RefreshRuntimeStatusAsync();

                var progress = new Progress<OmrProgressInfo>(p =>
                {
                    int total = Math.Max(1, p.Total);
                    int current = Math.Clamp(p.Current, 0, total);
                    string progressText = $"{p.Stage} {current}/{total} {p.Detail}".Trim();
                    lock (s_omrRunSync)
                    {
                        s_runningOmrProgressText = progressText;
                    }
                    SetOmrProgress(true, progressText);
                });

                Task<OmrRecognitionResult> runningTask = GetOrStartRunningOmrTask(_selectedSheetPath, progress);
                await ObserveRunningOmrTaskAsync(runningTask);
                OmrRecognitionResult result = await runningTask;

                if (!result.Success && IsEngineMissing(result.Message))
                {
                    bool installed = await PromptInstallOmrEnginesAsync();
                    if (installed)
                    {
                        Task<OmrRecognitionResult> retryTask = GetOrStartRunningOmrTask(_selectedSheetPath, progress);
                        await ObserveRunningOmrTaskAsync(retryTask);
                        result = await retryTask;
                    }
                }
                await ApplyOmrResultAsync(result, showLowQualityHint: true);
            }
            catch (Exception ex)
            {
                _recognizedMusicXmlPath = null;
                SheetRecognizeSummaryText.Text = $"OMR 结果: 识别失败 - {ex.Message}";
                RecognizedMusicXmlPathText.Text = "MusicXML: -";
                OmrBestReasonText.Text = "最优候选: -";
                SaveOmrStateToCache();
            }
            finally
            {
                RunOmrButton.IsEnabled = true;
                bool stillRunning;
                lock (s_omrRunSync)
                {
                    stillRunning = s_runningOmrTask != null && !s_runningOmrTask.IsCompleted;
                }
                if (stillRunning)
                {
                    SetOmrProgress(true, s_runningOmrProgressText);
                }
                else
                {
                    SetOmrProgress(false);
                }
                await RefreshRuntimeStatusAsync();
            }
        }

        private async Task ApplyOmrResultAsync(OmrRecognitionResult result, bool showLowQualityHint)
        {
            _lastOmrResult = result;
            BindOmrCandidates(_lastOmrResult.Diagnostics);

            if (!result.Success)
            {
                _recognizedMusicXmlPath = null;
                SheetRecognizeSummaryText.Text = $"OMR 结果: {result.Message}";
                RecognizedMusicXmlPathText.Text = "MusicXML: -";
                OmrBestReasonText.Text = "最优候选: -";
                SaveOmrStateToCache();
                return;
            }

            _recognizedMusicXmlPath = result.MusicXmlPath;
            SheetRecognizeSummaryText.Text = $"OMR 结果: {result.Message}";
            RecognizedMusicXmlPathText.Text = $"MusicXML: {_recognizedMusicXmlPath}";

            OmrCandidateInfo? best = result.Diagnostics.BestCandidate;
            OmrBestReasonText.Text = best == null
                ? "最优候选: -"
                : $"最优候选: {best.Engine}/{best.InputVariant}, notes={best.PitchedNotes}, measures={best.Measures}, score={best.QualityScore}";

            ImportBestOmrButton.IsEnabled = best != null && File.Exists(best.MusicXmlPath);
            OpenOmrArtifactsButton.IsEnabled = !string.IsNullOrWhiteSpace(result.Diagnostics.ArtifactRoot) && Directory.Exists(result.Diagnostics.ArtifactRoot);

            if (!string.IsNullOrWhiteSpace(_recognizedMusicXmlPath) && File.Exists(_recognizedMusicXmlPath))
            {
                await FillOmrPreviewAsync(_recognizedMusicXmlPath);
            }

            if (showLowQualityHint && best != null && best.QualityScore < 20)
            {
                await ShowLowQualityHintAsync(best);
            }

            SaveOmrStateToCache();
        }

        private void BindOmrCandidates(OmrRecognitionDiagnostics? diagnostics)
        {
            _omrCandidates.Clear();
            if (diagnostics == null)
            {
                ImportSelectedOmrButton.IsEnabled = false;
                OpenOmrArtifactsButton.IsEnabled = false;
                UpdateOmrCandidatesEmptyState();
                return;
            }

            int rowNumber = 1;
            foreach (OmrCandidateInfo candidate in diagnostics.Candidates
                .OrderByDescending(c => c.QualityScore)
                .ThenBy(c => c.PageIndex.HasValue ? 1 : 0))
            {
                _omrCandidates.Add(new OmrCandidateListItem(rowNumber, candidate));
                rowNumber++;
            }

            OmrCandidateInfo? best = diagnostics.BestCandidate;
            if (best != null)
            {
                OmrCandidateListItem? bestItem = _omrCandidates.FirstOrDefault(i => string.Equals(i.MusicXmlPath, best.MusicXmlPath, StringComparison.OrdinalIgnoreCase));
                if (bestItem != null)
                {
                    OmrCandidatesListView.SelectedItem = bestItem;
                }
            }

            OpenOmrArtifactsButton.IsEnabled = !string.IsNullOrWhiteSpace(diagnostics.ArtifactRoot) && Directory.Exists(diagnostics.ArtifactRoot);
            ImportSelectedOmrButton.IsEnabled = OmrCandidatesListView.SelectedItem is OmrCandidateListItem selected && File.Exists(selected.MusicXmlPath);
            UpdateOmrCandidatesEmptyState();
            SaveOmrStateToCache();
        }

        private async void OmrCandidatesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (OmrCandidatesListView.SelectedItem is not OmrCandidateListItem selected)
            {
                ImportSelectedOmrButton.IsEnabled = false;
                return;
            }

            ImportSelectedOmrButton.IsEnabled = File.Exists(selected.MusicXmlPath);
            if (File.Exists(selected.MusicXmlPath))
            {
                await FillOmrPreviewAsync(selected.MusicXmlPath);
                RecognizedMusicXmlPathText.Text = $"MusicXML: {selected.MusicXmlPath}";
            }
            SaveOmrStateToCache();
        }

        private void ImportBestOmrButton_Click(object sender, RoutedEventArgs e)
        {
            if (_lastOmrResult?.Diagnostics.BestCandidate == null)
            {
                SheetRecognizeSummaryText.Text = "OMR 结果: 当前没有可导入的最优候选";
                return;
            }

            ImportMusicXmlCandidate(_lastOmrResult.Diagnostics.BestCandidate.MusicXmlPath, "最优候选");
        }

        private void ImportSelectedOmrButton_Click(object sender, RoutedEventArgs e)
        {
            if (OmrCandidatesListView.SelectedItem is not OmrCandidateListItem selected)
            {
                SheetRecognizeSummaryText.Text = "OMR 结果: 请先选择一个候选";
                return;
            }

            ImportMusicXmlCandidate(selected.MusicXmlPath, "选中候选");
        }

        private void ImportMusicXmlCandidate(string candidatePath, string label)
        {
            if (_viewModel == null)
            {
                SheetRecognizeSummaryText.Text = "OMR 结果: 导入失败，ViewModel 未初始化";
                return;
            }

            if (string.IsNullOrWhiteSpace(candidatePath) || !File.Exists(candidatePath))
            {
                SheetRecognizeSummaryText.Text = "OMR 结果: 候选文件不存在";
                return;
            }

            try
            {
                _viewModel.ImportMusicXmlFromPath(candidatePath);
                OmrPostProcessReport postReport = _omrPostProcessor.Apply(_viewModel.Project);
                if (!string.IsNullOrWhiteSpace(_selectedSheetPath))
                {
                    _viewModel.Project.Title = Path.GetFileNameWithoutExtension(_selectedSheetPath);
                }
                _viewModel.TouchProject();
                _viewModel.SetStatus($"已导入 OMR {label}: {Path.GetFileName(candidatePath)}");
                if (postReport.HasChanges)
                {
                    SheetRecognizeSummaryText.Text =
                        $"OMR 结果: 已导入{label}，补全: 休止符+{postReport.AddedMeasureRests}、终止线+{postReport.AddedFinalBarline}、连音+{postReport.AddedSlurs}";
                }
                else
                {
                    SheetRecognizeSummaryText.Text = $"OMR 结果: 已导入{label}到当前工程";
                }
                _recognizedMusicXmlPath = candidatePath;
                RecognizedMusicXmlPathText.Text = $"MusicXML: {_recognizedMusicXmlPath}";
                SaveOmrStateToCache();
            }
            catch (Exception ex)
            {
                SheetRecognizeSummaryText.Text = $"OMR 结果: 导入失败 - {ex.Message}";
                SaveOmrStateToCache();
            }
        }

        private async void OpenOmrArtifactsButton_Click(object sender, RoutedEventArgs e)
        {
            string? artifactRoot = _lastOmrResult?.Diagnostics.ArtifactRoot;
            if (string.IsNullOrWhiteSpace(artifactRoot) || !Directory.Exists(artifactRoot))
            {
                SheetRecognizeSummaryText.Text = "OMR 结果: 暂无中间文件目录";
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = artifactRoot,
                    UseShellExecute = true
                });
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                SheetRecognizeSummaryText.Text = $"OMR 结果: 无法打开目录 - {ex.Message}";
            }
        }

        private async Task FillOmrPreviewAsync(string musicXmlPath)
        {
            if (string.IsNullOrWhiteSpace(musicXmlPath) || !File.Exists(musicXmlPath))
            {
                OmrPreviewTextBox.Text = string.Empty;
                return;
            }

            var lines = new List<string>(128);
            await using var stream = File.OpenRead(musicXmlPath);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (!reader.EndOfStream && lines.Count < 120)
            {
                string? line = await reader.ReadLineAsync();
                if (line == null) break;
                lines.Add(line);
            }

            if (!reader.EndOfStream)
            {
                lines.Add("...（预览已截断）");
            }

            OmrPreviewTextBox.Text = string.Join(Environment.NewLine, lines);
        }

        private static bool IsEngineMissing(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            return message.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
                || message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                || message.Contains("No usable OMR candidate", StringComparison.OrdinalIgnoreCase)
                || message.Contains("No input", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<bool> PromptInstallOmrEnginesAsync()
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "未检测到 OMR 运行时",
                Content =
                    "将尝试安装 Python 3.11 环境下的 homr 以及 Audiveris。\n\n" +
                    "将执行以下 PowerShell 命令：\n" +
                    "1) py -3.11 -m pip install --upgrade pip\n" +
                    "2) py -3.11 -m pip install homr==0.4.0\n" +
                    "3) winget install --id audiveris.org.Audiveris -e --accept-package-agreements --accept-source-agreements\n\n" +
                    "是否继续自动安装？",
                PrimaryButtonText = "自动安装",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };

            ContentDialogResult choice = await dialog.ShowAsync();
            if (choice != ContentDialogResult.Primary)
            {
                return false;
            }

            SheetRecognizeSummaryText.Text = "OMR 结果: 正在安装识别引擎，请稍候...";
            await RunPowerShellCommandAsync("py -3.11 -m pip install --upgrade pip");
            await RunPowerShellCommandAsync("py -3.11 -m pip install homr==0.4.0");
            await RunPowerShellCommandAsync("winget install --id audiveris.org.Audiveris -e --accept-package-agreements --accept-source-agreements");

            OmrRuntimeStatus status = await _omrRuntimeManager.EnsureReadyAsync();
            await RefreshRuntimeStatusAsync(status.Message);
            return status.HomrReady || status.AudiverisReady;
        }

        private async Task ShowLowQualityHintAsync(OmrCandidateInfo best)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "识别质量提示",
                Content = $"当前最优候选分数较低（{best.QualityScore}）。建议先点击“查看中间图”检查预处理结果，再决定导入。",
                PrimaryButtonText = "知道了"
            };
            await dialog.ShowAsync();
        }

        private static async Task<(int ExitCode, string Stdout, string Stderr)> RunPowerShellCommandAsync(string command)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command.Replace("\"", "\\\"")}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.Start();
            string stdout = await process.StandardOutput.ReadToEndAsync();
            string stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, stdout, stderr);
        }

        private sealed class OmrCandidateListItem
        {
            public OmrCandidateListItem(int rowNumber, OmrCandidateInfo source)
            {
                RowNumber = rowNumber;
                Source = source;
            }

            public int RowNumber { get; }
            public OmrCandidateInfo Source { get; }
            public string Engine => Source.Engine + (Source.PageIndex.HasValue ? $" (p{Source.PageIndex.Value})" : string.Empty);
            public string InputVariant => Source.InputVariant;
            public int PitchedNotes => Source.PitchedNotes;
            public int Measures => Source.Measures;
            public int QualityScore => Source.QualityScore;
            public string Status => Source.Status;
            public string MusicXmlPath => Source.MusicXmlPath;
        }

        private sealed class OmrPageStateCache
        {
            public string? SelectedSheetPath { get; set; }
            public string? RecognizedMusicXmlPath { get; set; }
            public OmrRecognitionResult? LastResult { get; set; }
            public string? SelectedCandidatePath { get; set; }
            public string? SheetSummary { get; set; }
            public string? BestReason { get; set; }
            public string? Preview { get; set; }
            public string? RuntimeStatus { get; set; }
            public bool IsOmrRunning { get; set; }
            public string? OmrProgress { get; set; }
        }

        private sealed class AudioPageStateCache
        {
            public string? SelectedAudioPath { get; set; }
            public List<DetectedAudioNote>? DetectedNotes { get; set; }
            public AudioRecognitionMode Mode { get; set; } = AudioRecognitionMode.MelodyFocus;
            public string? SelectedPathText { get; set; }
            public string? Summary { get; set; }
            public string? Preview { get; set; }
            public bool IsRunning { get; set; }
            public string? Progress { get; set; }
        }
    }
}

