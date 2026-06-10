using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MusicBox.Models;
using MusicBox.Services;
using MusicBox.ViewModels;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace MusicBox
{
    public sealed partial class ComposeWorkbenchPage : Page
    {
        private const int CandidateCount = 3;
        private const int CandidatePoolMultiplier = 7;
        private const int MinimumCandidatePool = 10;
        private const double DiversityPenaltyWeight = 18d;
        private const string ChineseLanguage = "zh-Hans";
        private const string EnglishLanguage = "en-US";
        private static readonly string[] SharpNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
        private static readonly string[] FlatNames = { "C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B" };

        private readonly SmartComposeService _service = new();
        private readonly ComposePreferenceService _preferenceService = new();
        private readonly PreviewPlaybackService _playback = new();
        private readonly AudioExportService _audioExporter = new();
        private readonly List<SmartComposeResult> _candidates = new();
        private readonly double?[] _candidatePreferenceScores = new double?[CandidateCount];
        private readonly ComposePrediction?[] _candidatePredictions = new ComposePrediction?[CandidateCount];
        private readonly ComposeCategoryRating?[] _candidateSavedRatings = new ComposeCategoryRating?[CandidateCount];
        private readonly bool[] _keptCandidates = new bool[CandidateCount];
        private readonly AppSettingsService _settings = AppSettingsService.Instance;
        private MainViewModel? _viewModel;
        private SmartComposeRequest? _lastRequest;
        private int _seedBase;
        private int _generationSerial;

        public ComposeWorkbenchPage()
        {
            DebugTrace.Write("ComposeWorkbenchPage.ctor begin");
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Required;
            Loaded += ComposeWorkbenchPage_Loaded;
            Unloaded += ComposeWorkbenchPage_Unloaded;
            LocalizationService.LanguageChanged += LocalizationService_LanguageChanged;
            _playback.PlaybackStateChanged += Playback_PlaybackStateChanged;
            MoodBox.SelectedIndex = 0;
            LengthBox.SelectedIndex = 1;
            ApplyLocalizedText();
            ApplyStaticButtonVisuals();
            HideStatusText();
            ResetCandidateSurface();
            DebugTrace.Write("ComposeWorkbenchPage.ctor end");
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is MainViewModel vm)
            {
                _viewModel = vm;
                DataContext = vm;
            }

            RefreshPlayButtons();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            _playback.Pause();
            RefreshPlayButtons();
        }

        private void ComposeWorkbenchPage_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyLocalizedText();
            _viewModel?.SetStatus(T("compose.status.ready"));
        }

        private void ComposeWorkbenchPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _playback.Pause();
        }

        private void Playback_PlaybackStateChanged(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(RefreshPlayButtons);
        }

        private void LocalizationService_LanguageChanged(object? sender, EventArgs e)
        {
            ApplyLocalizedText();
            RenderCandidates();
        }

        private void GenerateButton_Click(object sender, RoutedEventArgs e)
        {
            _seedBase = Environment.TickCount;
            _generationSerial++;
            Array.Fill(_keptCandidates, false);
            HideStatusText();
            GenerateCandidates(preserveKept: false);
        }

        private void RetryButton_Click(object sender, RoutedEventArgs e)
        {
            _seedBase = Environment.TickCount;
            _generationSerial++;
            HideStatusText();
            GenerateCandidates(preserveKept: true);
        }

        private async void PlayCandidateButton_Click(object sender, RoutedEventArgs e)
        {
            int index = ResolveCandidateIndex(sender);
            if (!HasCandidate(index))
            {
                return;
            }

            SmartComposeResult result = _candidates[index];
            RefreshPlayButtons();
            await _playback.TogglePlayAsync(index, result.Project);
            RefreshPlayButtons();
        }

        private void ApplyCandidateButton_Click(object sender, RoutedEventArgs e)
        {
            int index = ResolveCandidateIndex(sender);
            if (!HasCandidate(index) || _viewModel == null)
            {
                return;
            }

            SmartComposeResult result = _candidates[index];
            _viewModel.LoadProjectSnapshot(CloneProject(result.Project));
            _viewModel.SetStatus(TF("compose.status.applied", (char)('A' + index), result.Project.Title));

            if (App.MainWindow is MainWindow window)
            {
                window.NavigateToPage("editor");
            }
        }

        private async void SaveCandidateAudioButton_Click(object sender, RoutedEventArgs e)
        {
            int index = ResolveCandidateIndex(sender);
            if (!HasCandidate(index))
            {
                return;
            }

            try
            {
                SmartComposeResult result = _candidates[index];
                string? path = await PickSavePathAsync(".wav", "WAV Audio", GetSuggestedAudioName(result.Project.Title, index));
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                bool isEnglish = IsEnglishUi();
                ShowStatusText(isEnglish ? "Exporting audio..." : "正在导出音频...");
                await RunAudioExportWithProgressAsync(
                    isEnglish ? "Exporting Audio" : "正在导出音频",
                    isEnglish ? "Please wait while the WAV file is generated." : "正在生成 WAV 文件，请稍候。",
                    () => System.Threading.Tasks.Task.Run(() => _audioExporter.ExportWav(result.Project, path)));

                string message = TF("compose.status.audio_exported", (char)('A' + index), Path.GetFileName(path));
                ShowStatusText(message);
                _viewModel?.SetStatus(message);
            }
            catch (Exception ex)
            {
                string message = TF("compose.status.audio_export_failed", (char)('A' + index), ex.Message);
                ShowStatusText(message);
                _viewModel?.SetStatus(message);
            }
        }

        private void ToggleKeepButton_Click(object sender, RoutedEventArgs e)
        {
            int index = ResolveCandidateIndex(sender);
            if (!HasCandidate(index))
            {
                return;
            }

            _keptCandidates[index] = !_keptCandidates[index];
            RefreshKeepButtons();
        }

        private async void SaveRatingButton_Click(object sender, RoutedEventArgs e)
        {
            int index = ResolveCandidateIndex(sender);
            if (!HasCandidate(index))
            {
                return;
            }

            SmartComposeResult result = _candidates[index];
            ComposeCategoryRating seedRating = BuildDialogSeedRating(index);
            var dialogResult = await ShowCategoryRatingDialogAsync(index, seedRating);
            if (dialogResult.Action == ComposeRatingDialogAction.Cancel)
            {
                return;
            }

            if (dialogResult.Action == ComposeRatingDialogAction.Remove)
            {
                if (_preferenceService.RemoveRating(result.Seed))
                {
                    _candidateSavedRatings[index] = null;
                    RefreshCandidatePredictions();
                    RenderCandidates();
                    string cleared = TF("compose.status.rating_cleared", (char)('A' + index), _preferenceService.GetRatingCount());
                    ShowStatusText(cleared);
                    _viewModel?.SetStatus(cleared);
                }

                return;
            }

            SmartComposeRequest request = _lastRequest ?? BuildRequest();
            _preferenceService.UpsertRating(request, result, dialogResult.Rating);
            _candidateSavedRatings[index] = dialogResult.Rating;
            RefreshCandidatePredictions();
            RenderCandidates();
            string message = TF("compose.status.rating_saved", (char)('A' + index), dialogResult.Rating.Overall, _preferenceService.GetRatingCount());
            ShowStatusText(message);
            _viewModel?.SetStatus(message);
        }

        private void RatingSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (sender is not Slider slider)
            {
                return;
            }

            int index = ResolveCandidateIndex(slider);
            if (index < 0)
            {
                return;
            }

            GetRatingValueText(index).Text = Math.Round(slider.Value).ToString("0");
            GetScoreProgress(index).Value = Math.Round(slider.Value);
        }

        private void GenerateCandidates(bool preserveKept)
        {
            try
            {
                _playback.Reset();
                SmartComposeRequest request = BuildRequest();
                request.Seed = _seedBase ^ (_generationSerial * 104729);
                _lastRequest = request;

                int slotsToFill = preserveKept
                    ? Enumerable.Range(0, CandidateCount).Count(index => !_keptCandidates[index] || !HasCandidate(index))
                    : CandidateCount;
                int relevantRatings = _preferenceService.GetRelevantRatingCount(request.MoodId);
                int preferencePoolBoost = relevantRatings >= 4
                    ? Math.Min(12, 2 + relevantRatings / 2)
                    : 0;
                int candidatePoolSize = Math.Max(MinimumCandidatePool, slotsToFill * CandidatePoolMultiplier + preferencePoolBoost);
                IReadOnlyList<SmartComposeResult> generated = _service.GenerateCandidates(request, candidatePoolSize);
                IReadOnlyList<ComposeCandidateRanking> ranked = _preferenceService.RankCandidates(request, generated);
                List<ComposeCandidateRanking> selectedGenerated = SelectDiverseCandidates(ranked, slotsToFill);
                var nextCandidates = new List<SmartComposeResult>(CandidateCount);
                var nextPreferenceScores = new double?[CandidateCount];
                var nextPredictions = new ComposePrediction?[CandidateCount];
                var nextSavedRatings = new ComposeCategoryRating?[CandidateCount];
                int generatedCursor = 0;

                for (int index = 0; index < CandidateCount; index++)
                {
                    if (preserveKept && _keptCandidates[index] && HasCandidate(index))
                    {
                        nextCandidates.Add(_candidates[index]);
                        nextPreferenceScores[index] = _candidatePreferenceScores[index];
                        nextPredictions[index] = _candidatePredictions[index];
                        nextSavedRatings[index] = _candidateSavedRatings[index];
                    }
                    else
                    {
                        ComposeCandidateRanking candidate = selectedGenerated[Math.Min(generatedCursor, selectedGenerated.Count - 1)];
                        nextCandidates.Add(candidate.Result);
                        nextPreferenceScores[index] = candidate.Prediction.FinalScore;
                        nextPredictions[index] = candidate.Prediction;
                        nextSavedRatings[index] = candidate.SavedRating;
                        generatedCursor++;
                    }
                }

                _candidates.Clear();
                _candidates.AddRange(nextCandidates);
                Array.Copy(nextPreferenceScores, _candidatePreferenceScores, CandidateCount);
                Array.Copy(nextPredictions, _candidatePredictions, CandidateCount);
                Array.Copy(nextSavedRatings, _candidateSavedRatings, CandidateCount);
                RenderCandidates();
                HideStatusText();
                string status = relevantRatings >= 8
                    ? TF("compose.status.reranked", CandidateCount, relevantRatings)
                    : TF("compose.status.generated", CandidateCount);
                _viewModel?.SetStatus(status);
            }
            catch (Exception ex)
            {
                string message = TF("compose.status.generation_failed", ex.Message);
                ShowStatusText(message);
                _viewModel?.SetStatus(message);
                ResetCandidateSurface(T("compose.status.failed_short"));
            }
        }

        private static List<ComposeCandidateRanking> SelectDiverseCandidates(IReadOnlyList<ComposeCandidateRanking> ranked, int count)
        {
            if (count <= 0 || ranked.Count == 0)
            {
                return new List<ComposeCandidateRanking>();
            }

            var remaining = ranked.ToList();
            var selected = new List<ComposeCandidateRanking>(Math.Min(count, remaining.Count));

            while (selected.Count < count && remaining.Count > 0)
            {
                ComposeCandidateRanking? bestCandidate = null;
                double bestAdjustedScore = double.NegativeInfinity;

                foreach (ComposeCandidateRanking candidate in remaining)
                {
                    double similarityPenalty = selected.Count == 0
                        ? 0d
                        : selected.Max(existing => ComputeCandidateSimilarity(existing.Features, candidate.Features)) * DiversityPenaltyWeight;
                    bool sameProgression = selected.Any(existing =>
                        string.Equals(existing.Result.ChordProgression, candidate.Result.ChordProgression, StringComparison.OrdinalIgnoreCase));
                    double adjustedScore = candidate.Prediction.FinalScore - similarityPenalty - (sameProgression ? 8d : 0d);
                    if (adjustedScore > bestAdjustedScore)
                    {
                        bestAdjustedScore = adjustedScore;
                        bestCandidate = candidate;
                    }
                }

                if (bestCandidate == null)
                {
                    break;
                }

                selected.Add(bestCandidate);
                remaining.Remove(bestCandidate);
            }

            return selected;
        }

        private static double ComputeCandidateSimilarity(ComposeFeatureVector left, ComposeFeatureVector right)
        {
            double score =
                SimilarityByDistance(left.PitchRange, right.PitchRange, 10d) +
                SimilarityByDistance(left.NoteDensity, right.NoteDensity, 2.6d) +
                SimilarityByDistance(left.ChordDensity, right.ChordDensity, 0.85d) +
                SimilarityByDistance(left.LargeLeapRatio, right.LargeLeapRatio, 0.14d) +
                SimilarityByDistance(left.BassShare, right.BassShare, 0.18d) +
                SimilarityByDistance(left.RepetitionRatio, right.RepetitionRatio, 0.16d) +
                SimilarityByDistance(left.DurationMismatch, right.DurationMismatch, 0.12d) +
                SimilarityByDistance(left.RegisterCenter, right.RegisterCenter, 6d) +
                SimilarityByDistance(left.RhythmVariance, right.RhythmVariance, 0.45d);

            return score / 9d;
        }

        private static double SimilarityByDistance(double left, double right, double tolerance)
        {
            if (tolerance <= 0d)
            {
                return 0d;
            }

            return Math.Clamp(1d - Math.Abs(left - right) / tolerance, 0d, 1d);
        }

        private SmartComposeRequest BuildRequest()
        {
            string moodId = SelectedTag(MoodBox);
            string lengthId = SelectedTag(LengthBox);
            int autoBpm = ResolveAutoBpm(moodId);
            (int numerator, int denominator) = ResolveAutoMeter(moodId);

            return new SmartComposeRequest
            {
                Title = string.IsNullOrWhiteSpace(TitleBox.Text) ? T("compose.default_title") : TitleBox.Text.Trim(),
                Bpm = autoBpm,
                Measures = ResolveMeasureCount(lengthId),
                KeyFifths = 0,
                Mode = KeyMode.Major,
                TimeSignature = new TimeSignature(numerator, denominator),
                MoodId = moodId,
                LengthId = lengthId,
                IncludeBass = true,
                AutoTonality = true,
                UseSustainPedal = false
            };
        }

        private void RenderCandidates()
        {
            RenderCandidate(0, Option1SummaryText, Option1ApplyButton, Option1RateButton);
            RenderCandidate(1, Option2SummaryText, Option2ApplyButton, Option2RateButton);
            RenderCandidate(2, Option3SummaryText, Option3ApplyButton, Option3RateButton);
            UpdateGeneratedBadge();
            RefreshPlayButtons();
            RefreshExportAudioButtons();
            RefreshKeepButtons();
        }

        private void RenderCandidate(int index, TextBlock summaryText, Button applyButton, Button rateButton)
        {
            summaryText.FontSize = IsEnglishUi() ? 13 : 14;
            summaryText.LineHeight = IsEnglishUi() ? 20 : 22;

            if (!HasCandidate(index))
            {
                summaryText.Text = T("compose.action.waiting");
                applyButton.IsEnabled = false;
                rateButton.IsEnabled = false;
                GetRatingSlider(index).IsEnabled = false;
                GetRatingValueText(index).Text = "-";
                GetScoreProgress(index).Value = 0;
                UpdateCandidateMeta(index, null, null);
                return;
            }

            SmartComposeResult result = _candidates[index];
            ComposePrediction? prediction = _candidatePredictions[index];
            ComposeCategoryRating? savedRating = _candidateSavedRatings[index];
            summaryText.Text = BuildCandidateNarrative(result, prediction, savedRating);
            applyButton.IsEnabled = true;
            rateButton.IsEnabled = true;
            Slider slider = GetRatingSlider(index);
            slider.IsEnabled = true;
            slider.Value = savedRating?.Overall ?? prediction?.FinalScore ?? 50d;
            double score = Math.Round(slider.Value);
            GetRatingValueText(index).Text = score.ToString("0");
            GetScoreProgress(index).Value = score;
            UpdateCandidateMeta(index, result, prediction);
        }

        private void ResetCandidateSurface(string? placeholder = null)
        {
            string text = string.IsNullOrWhiteSpace(placeholder)
                ? T("compose.action.waiting")
                : placeholder;

            _candidates.Clear();
            _lastRequest = null;
            Array.Clear(_candidatePreferenceScores, 0, _candidatePreferenceScores.Length);
            Array.Clear(_candidatePredictions, 0, _candidatePredictions.Length);
            Array.Clear(_candidateSavedRatings, 0, _candidateSavedRatings.Length);
            Option1SummaryText.Text = text;
            Option2SummaryText.Text = text;
            Option3SummaryText.Text = text;
            Option1ApplyButton.IsEnabled = false;
            Option2ApplyButton.IsEnabled = false;
            Option3ApplyButton.IsEnabled = false;
            ResetRatingControls();
            UpdateCandidateMeta(0, null, null);
            UpdateCandidateMeta(1, null, null);
            UpdateCandidateMeta(2, null, null);
            GetRatingValueText(0).Text = "-";
            GetRatingValueText(1).Text = "-";
            GetRatingValueText(2).Text = "-";
            GetScoreProgress(0).Value = 0;
            GetScoreProgress(1).Value = 0;
            GetScoreProgress(2).Value = 0;
            UpdateGeneratedBadge();
            RefreshPlayButtons();
            RefreshExportAudioButtons();
            RefreshKeepButtons();
        }

        private void RefreshPlayButtons()
        {
            UpdatePlayButton(Option1PlayButton, 0);
            UpdatePlayButton(Option2PlayButton, 1);
            UpdatePlayButton(Option3PlayButton, 2);
        }

        private void RefreshExportAudioButtons()
        {
            UpdateExportAudioButton(Option1ExportAudioButton, 0);
            UpdateExportAudioButton(Option2ExportAudioButton, 1);
            UpdateExportAudioButton(Option3ExportAudioButton, 2);
        }

        private void UpdatePlayButton(Button button, int index)
        {
            bool enabled = HasCandidate(index);
            button.IsEnabled = enabled;
            bool isActive = enabled && _playback.IsPlaying && _playback.ActiveIndex == index;
            SetButtonContent(
                button,
                isActive ? Symbol.Pause : Symbol.Play,
                T(isActive ? "compose.action.pause" : "compose.action.play"),
                14,
                IsEnglishUi() ? 13 : 14);
        }

        private void UpdateExportAudioButton(Button button, int index)
        {
            bool enabled = HasCandidate(index);
            button.IsEnabled = enabled;
            ToolTipService.SetToolTip(button, T("compose.action.save_audio"));
            SetButtonContent(
                button,
                Symbol.Save,
                null,
                12);
        }

        private void RefreshKeepButtons()
        {
            UpdateKeepButton(Option1KeepButton, 0);
            UpdateKeepButton(Option2KeepButton, 1);
            UpdateKeepButton(Option3KeepButton, 2);
        }

        private void UpdateKeepButton(Button button, int index)
        {
            bool enabled = HasCandidate(index);
            button.IsEnabled = enabled;
            ToolTipService.SetToolTip(button, T(enabled && _keptCandidates[index] ? "compose.action.unkeep" : "compose.action.keep"));
            SetButtonContent(button, Symbol.Accept, null, 12);
            TryApplyAccentStyle(button, enabled && _keptCandidates[index]);
        }

        private void UpdateGeneratedBadge()
        {
            if (GeneratedBadgeText == null)
            {
                return;
            }

            bool isEnglish = IsEnglishUi();
            GeneratedBadgeText.Text = isEnglish
                ? $"Generated {_candidates.Count} candidate plans"
                : $"已生成 {_candidates.Count} 个候选方案";
        }

        private void UpdateCandidateMeta(int index, SmartComposeResult? result, ComposePrediction? prediction)
        {
            TextBlock keyText = GetCandidateMetaText(index, "key");
            TextBlock meterText = GetCandidateMetaText(index, "meter");
            TextBlock measuresText = GetCandidateMetaText(index, "measures");
            TextBlock tempoText = GetCandidateMetaText(index, "tempo");
            TextBlock durationText = GetCandidateMetaText(index, "duration");
            TextBlock preferenceText = GetCandidateMetaText(index, "preference");

            if (result == null)
            {
                keyText.Text = "-";
                meterText.Text = "-";
                measuresText.Text = "-";
                tempoText.Text = "-";
                durationText.Text = "-";
                preferenceText.Text = "-";
                return;
            }

            ScoreProject project = result.Project;
            int safePpq = Math.Max(1, project.Ppq);
            int ticksPerMeasure = Math.Max(1, project.TimeSignature.TicksPerMeasure(safePpq));
            int totalTicks = project.Notes.Count == 0
                ? ticksPerMeasure
                : Math.Max(ticksPerMeasure, project.Notes.Max(note => note.StartTick + Math.Max(1, note.DurationTicks)));
            int measureCount = Math.Max(1, (int)Math.Ceiling(totalTicks / (double)ticksPerMeasure));

            keyText.Text = BuildLocalizedKeyLabel(project.KeySignature.Fifths, project.KeySignature.Mode);
            meterText.Text = $"{project.TimeSignature.Numerator}/{project.TimeSignature.Denominator}";
            measuresText.Text = measureCount.ToString();
            tempoText.Text = $"{project.Bpm} BPM";
            durationText.Text = FormatDuration(totalTicks, safePpq, project.Bpm);
            preferenceText.Text = prediction == null ? "-" : Math.Round(prediction.FinalScore).ToString("0");
        }

        private TextBlock GetCandidateMetaText(int index, string key)
        {
            return (index, key) switch
            {
                (0, "key") => Option1KeyText,
                (0, "meter") => Option1MeterText,
                (0, "measures") => Option1MeasuresText,
                (0, "tempo") => Option1TempoText,
                (0, "duration") => Option1DurationText,
                (0, "preference") => Option1PreferenceText,
                (1, "key") => Option2KeyText,
                (1, "meter") => Option2MeterText,
                (1, "measures") => Option2MeasuresText,
                (1, "tempo") => Option2TempoText,
                (1, "duration") => Option2DurationText,
                (1, "preference") => Option2PreferenceText,
                (2, "key") => Option3KeyText,
                (2, "meter") => Option3MeterText,
                (2, "measures") => Option3MeasuresText,
                (2, "tempo") => Option3TempoText,
                (2, "duration") => Option3DurationText,
                _ => Option3PreferenceText
            };
        }

        private void ApplyLocalizedText()
        {
            bool isEnglish = IsEnglishUi();
            string localizedDefaultTitle = T("compose.default_title");
            string chineseDefaultTitle = LocalizationService.TranslateForLanguage(ChineseLanguage, "compose.default_title");
            string englishDefaultTitle = LocalizationService.TranslateForLanguage(EnglishLanguage, "compose.default_title");

            PageTitleText.Text = isEnglish ? "Smart Compose" : "智能创作";
            PageTitleText.FontSize = 30;

            PageSubtitleText.Text = isEnglish
                ? "Generate multiple melody plans from title, mood, and length, then quickly preview or write them to staff notation."
                : "根据标题、情绪与长度生成多个旋律方案，并快速试听与写入五线谱。";
            PageSubtitleText.Visibility = Visibility.Visible;
            PageSubtitleText.FontSize = 14;

            TitleLabelText.Text = T("compose.label.title");
            MoodLabelText.Text = T("compose.label.mood");
            LengthLabelText.Text = T("compose.label.length");

            if (string.IsNullOrWhiteSpace(TitleBox.Text)
                || string.Equals(TitleBox.Text.Trim(), chineseDefaultTitle, StringComparison.Ordinal)
                || string.Equals(TitleBox.Text.Trim(), englishDefaultTitle, StringComparison.Ordinal))
            {
                TitleBox.Text = localizedDefaultTitle;
            }

            SetComboBoxItemText(MoodCalmItem, T("compose.mood.calm"));
            SetComboBoxItemText(MoodPositiveItem, T("compose.mood.positive"));
            SetComboBoxItemText(MoodSadItem, T("compose.mood.sad"));
            SetComboBoxItemText(MoodSleepItem, T("compose.mood.sleep"));
            SetComboBoxItemText(MoodHopefulItem, T("compose.mood.hopeful"));
            SetComboBoxItemText(MoodNostalgicItem, T("compose.mood.nostalgic"));
            SetComboBoxItemText(MoodDreamyItem, T("compose.mood.dreamy"));
            SetComboBoxItemText(MoodTenseItem, T("compose.mood.tense"));

            SetComboBoxItemText(LengthShortItem, T("compose.length.short"));
            SetComboBoxItemText(LengthMediumItem, T("compose.length.medium"));
            SetComboBoxItemText(LengthLongItem, T("compose.length.long"));

            Option1TitleText.Text = T("compose.option.a");
            Option2TitleText.Text = T("compose.option.b");
            Option3TitleText.Text = T("compose.option.c");

            double compactTextSize = isEnglish ? 13 : 14;
            SetButtonContent(GenerateButton, Symbol.OutlineStar, T("compose.action.generate"), 14, isEnglish ? 13 : 14);
            SetButtonContent(RetryButton, Symbol.Refresh, T("compose.action.retry"), 12, isEnglish ? 13 : 14);
            SetPlainButtonContent(Option1RateButton, T("compose.action.rate_details"), compactTextSize);
            SetPlainButtonContent(Option2RateButton, T("compose.action.rate_details"), compactTextSize);
            SetPlainButtonContent(Option3RateButton, T("compose.action.rate_details"), compactTextSize);
            SetButtonContent(Option1ExportAudioButton, Symbol.Save, null, 12);
            SetButtonContent(Option2ExportAudioButton, Symbol.Save, null, 12);
            SetButtonContent(Option3ExportAudioButton, Symbol.Save, null, 12);
            ToolTipService.SetToolTip(Option1ExportAudioButton, T("compose.action.save_audio"));
            ToolTipService.SetToolTip(Option2ExportAudioButton, T("compose.action.save_audio"));
            ToolTipService.SetToolTip(Option3ExportAudioButton, T("compose.action.save_audio"));

            Option1ApplyButton.Content = T("compose.action.apply_to_editor");
            Option2ApplyButton.Content = T("compose.action.apply_to_editor");
            Option3ApplyButton.Content = T("compose.action.apply_to_editor");
            TitleLabelText.FontSize = compactTextSize;
            MoodLabelText.FontSize = compactTextSize;
            LengthLabelText.FontSize = compactTextSize;
            Option1ApplyButton.FontSize = compactTextSize;
            Option2ApplyButton.FontSize = compactTextSize;
            Option3ApplyButton.FontSize = compactTextSize;
            UpdateGeneratedBadge();
            LocalizeStaticComposeText();

            RenderCandidates();
            ApplyStaticButtonVisuals();
        }

        private void LocalizeStaticComposeText()
        {
            bool isEnglish = IsEnglishUi();
            var replacements = new Dictionary<string, string>
            {
                ["调号"] = isEnglish ? "Key" : "调号",
                ["Key"] = isEnglish ? "Key" : "调号",
                ["拍号"] = isEnglish ? "Meter" : "拍号",
                ["Meter"] = isEnglish ? "Meter" : "拍号",
                ["小节数"] = isEnglish ? "Measures" : "小节数",
                ["Measures"] = isEnglish ? "Measures" : "小节数",
                ["速度"] = isEnglish ? "Tempo" : "速度",
                ["Tempo"] = isEnglish ? "Tempo" : "速度",
                ["总时长"] = isEnglish ? "Duration" : "总时长",
                ["Duration"] = isEnglish ? "Duration" : "总时长",
                ["偏好预测"] = isEnglish ? "Fit" : "偏好预测",
                ["Fit"] = isEnglish ? "Fit" : "偏好预测",
                ["分类评分"] = isEnglish ? "Category score" : "分类评分",
                ["Category score"] = isEnglish ? "Category score" : "分类评分"
            };

            ApplyTextReplacements(this, replacements);
        }

        private static void ApplyTextReplacements(DependencyObject root, IReadOnlyDictionary<string, string> replacements)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject? child = VisualTreeHelper.GetChild(root, i);
                if (child == null)
                {
                    continue;
                }

                if (child is TextBlock textBlock
                    && textBlock.Text != null
                    && replacements.TryGetValue(textBlock.Text, out string? replacement)
                    && replacement != null)
                {
                    textBlock.Text = replacement;
                }

                ApplyTextReplacements(child, replacements);
            }
        }

        private void ApplyStaticButtonVisuals()
        {
            TryApplyAccentStyle(GenerateButton, true);
            TryApplyAccentStyle(Option1PlayButton, true);
            TryApplyAccentStyle(Option2PlayButton, true);
            TryApplyAccentStyle(Option3PlayButton, true);
        }

        private static void SetComboBoxItemText(ComboBoxItem item, string text)
        {
            if (item.Content is StackPanel panel)
            {
                foreach (object child in panel.Children)
                {
                    if (child is TextBlock textBlock)
                    {
                        textBlock.Text = text;
                        return;
                    }
                }
            }

            item.Content = text;
        }

        private void ShowStatusText(string message)
        {
            StatusText.Text = message;
            StatusText.FontSize = IsEnglishUi() ? 13 : 14;
            StatusText.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void HideStatusText()
        {
            StatusText.Text = string.Empty;
            StatusText.Visibility = Visibility.Collapsed;
        }

        private bool HasCandidate(int index)
        {
            return index >= 0 && index < _candidates.Count;
        }

        private bool IsEnglishUi()
        {
            return _settings.ResolveLanguageTag().StartsWith("en", StringComparison.OrdinalIgnoreCase);
        }

        private static string T(string key)
        {
            return LocalizationService.Translate(key);
        }

        private static string TF(string key, params object?[] args)
        {
            return LocalizationService.Format(key, args);
        }

        private static void TryApplyAccentStyle(Button button, bool applyAccent)
        {
            if (applyAccent)
            {
                Color accent = ResolveAccentColor();
                button.Background = new SolidColorBrush(accent);
                button.BorderBrush = new SolidColorBrush(accent);
                button.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
                return;
            }

            button.ClearValue(Control.BackgroundProperty);
            button.ClearValue(Control.BorderBrushProperty);
            button.ClearValue(Control.ForegroundProperty);
        }

        private static Color ResolveAccentColor()
        {
            if (Application.Current.Resources.TryGetValue("SystemAccentColor", out object value)
                && value is Color accentColor)
            {
                return accentColor;
            }

            return Color.FromArgb(255, 54, 103, 153);
        }

        private static void SetPlainButtonContent(Button button, string text, double textSize)
        {
            button.Content = new TextBlock
            {
                Text = text,
                FontSize = textSize,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
        }

        private static void SetButtonContent(Button button, Symbol symbol, string? text, double iconSize = 16, double textSize = 14)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = text == null ? 0 : 6,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            panel.Children.Add(new Viewbox
            {
                Width = iconSize,
                Height = iconSize,
                Child = new SymbolIcon(symbol)
            });

            if (!string.IsNullOrWhiteSpace(text))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = text,
                    FontSize = textSize,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            button.Content = panel;
        }

        private string BuildCandidateDetails(SmartComposeResult result)
        {
            return BuildCandidateDetails(result, null, null);
        }

        private string BuildCandidateDetails(SmartComposeResult result, ComposePrediction? prediction, ComposeCategoryRating? savedRating)
        {
            ScoreProject project = result.Project;
            int safePpq = Math.Max(1, project.Ppq);
            int ticksPerMeasure = Math.Max(1, project.TimeSignature.TicksPerMeasure(safePpq));
            int totalTicks = project.Notes.Count == 0
                ? ticksPerMeasure
                : Math.Max(ticksPerMeasure, project.Notes.Max(note => note.StartTick + Math.Max(1, note.DurationTicks)));
            int measureCount = Math.Max(1, (int)Math.Ceiling(totalTicks / (double)ticksPerMeasure));
            var lines = new List<string>
            {
                $"{T("compose.meta.key")}: {BuildLocalizedKeyLabel(project.KeySignature.Fifths, project.KeySignature.Mode)}",
                $"{T("compose.meta.meter")}: {project.TimeSignature.Numerator}/{project.TimeSignature.Denominator}",
                $"{T("compose.meta.measures")}: {measureCount}",
                $"{T("compose.meta.tempo")}: {project.Bpm} BPM",
                $"{T("compose.meta.duration")}: {FormatDuration(totalTicks, safePpq, project.Bpm)}"
            };

            if (prediction != null)
            {
                lines.Add($"{T("compose.meta.preference_fit")}: {Math.Round(prediction.FinalScore):0}");
                lines.Add($"{T("compose.meta.predicted_breakdown")}: {BuildCategoryScoreText(prediction.MelodyScore, prediction.RhythmScore, prediction.HarmonyScore, prediction.MoodFitScore, prediction.OverallScore)}");
                lines.Add($"{T("compose.meta.model_kind")}: {prediction.ModelKind}");
            }

            if (savedRating != null)
            {
                ComposeCategoryRating rating = savedRating;
                lines.Add($"{T("compose.meta.user_rating")}: {BuildCategoryScoreText(rating.Melody, rating.Rhythm, rating.Harmony, rating.MoodFit, rating.Overall)}");
            }

            if (!string.IsNullOrWhiteSpace(prediction?.CreationReason))
            {
                lines.Add($"{T("compose.meta.creation_reason")}: {prediction.CreationReason}");
            }

            if (!string.IsNullOrWhiteSpace(prediction?.RankingReason))
            {
                lines.Add($"{T("compose.meta.ranking_reason")}: {prediction.RankingReason}");
            }

            return string.Join(Environment.NewLine, lines);
        }

        private string BuildCandidateNarrative(SmartComposeResult result, ComposePrediction? prediction, ComposeCategoryRating? savedRating)
        {
            var lines = new List<string>();

            if (prediction != null)
            {
                lines.Add($"{T("compose.meta.predicted_breakdown")}: {BuildCategoryScoreText(prediction.MelodyScore, prediction.RhythmScore, prediction.HarmonyScore, prediction.MoodFitScore, prediction.OverallScore)}");
                lines.Add($"{T("compose.meta.model_kind")}: {prediction.ModelKind}");
            }
            else
            {
                lines.Add(result.Summary);
            }

            if (!string.IsNullOrWhiteSpace(prediction?.CreationReason))
            {
                lines.Add($"{T("compose.meta.creation_reason")}: {prediction.CreationReason}");
            }

            if (!string.IsNullOrWhiteSpace(prediction?.RankingReason))
            {
                lines.Add($"{T("compose.meta.ranking_reason")}: {prediction.RankingReason}");
            }

            if (savedRating != null)
            {
                ComposeCategoryRating rating = savedRating;
                lines.Add($"{T("compose.meta.user_rating")}: {BuildCategoryScoreText(rating.Melody, rating.Rhythm, rating.Harmony, rating.MoodFit, rating.Overall)}");
            }

            return string.Join(Environment.NewLine + Environment.NewLine, lines);
        }

        private string BuildCategoryScoreText(double melody, double rhythm, double harmony, double moodFit, double overall)
        {
            return $"{T("compose.rate.melody")} {Math.Round(melody):0} / {T("compose.rate.rhythm")} {Math.Round(rhythm):0} / {T("compose.rate.harmony")} {Math.Round(harmony):0} / {T("compose.rate.mood_fit")} {Math.Round(moodFit):0} / {T("compose.rate.overall")} {Math.Round(overall):0}";
        }

        private string BuildLocalizedKeyLabel(int fifths, KeyMode mode)
        {
            int pitchClass = Mod(fifths * 7 + (mode == KeyMode.Minor ? 9 : 0), 12);
            string tonic = fifths >= 0 ? SharpNames[pitchClass] : FlatNames[pitchClass];
            return $"{tonic} {T(mode == KeyMode.Minor ? "compose.mode.minor" : "compose.mode.major")}";
        }

        private static string FormatDuration(int totalTicks, int ppq, int bpm)
        {
            double seconds = totalTicks * 60d / (Math.Max(1, ppq) * Math.Max(1, bpm));
            TimeSpan span = TimeSpan.FromSeconds(Math.Max(0, seconds));
            int totalMinutes = (int)Math.Floor(span.TotalMinutes);
            return $"{totalMinutes}:{span.Seconds:00}";
        }

        private static int ResolveMeasureCount(string lengthId)
        {
            return lengthId switch
            {
                "long" => 64,
                "medium" => 32,
                _ => 16
            };
        }

        private static int ResolveAutoBpm(string moodId)
        {
            return moodId switch
            {
                "sleep" => 52,
                "sad" => 76,
                "positive" => 118,
                "hopeful" => 104,
                "tense" => 132,
                _ => 96
            };
        }

        private static (int Numerator, int Denominator) ResolveAutoMeter(string moodId)
        {
            return moodId switch
            {
                "sleep" => (6, 8),
                "sad" => (3, 4),
                _ => (4, 4)
            };
        }

        private static int ResolveCandidateIndex(object sender)
        {
            return sender is FrameworkElement element
                && int.TryParse(element.Tag?.ToString(), out int index)
                ? index
                : -1;
        }

        private static string SelectedTag(ComboBox box)
        {
            return (box.SelectedItem as ComboBoxItem)?.Tag?.ToString()?.Trim().ToLowerInvariant() ?? string.Empty;
        }

        private static int Mod(int value, int modulus)
        {
            int result = value % modulus;
            return result < 0 ? result + modulus : result;
        }

        private static string GetSuggestedAudioName(string title, int index)
        {
            string name = string.IsNullOrWhiteSpace(title)
                ? $"Compose-{(char)('A' + index)}"
                : title.Trim();

            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalid, '_');
            }

            return $"{name}-Option{(char)('A' + index)}.wav";
        }

        private async System.Threading.Tasks.Task RunAudioExportWithProgressAsync(string title, string message, Func<System.Threading.Tasks.Task> exportAction)
        {
            if (exportAction == null)
            {
                return;
            }

            if (XamlRoot == null)
            {
                await exportAction();
                return;
            }

            var ring = new ProgressRing
            {
                Width = 28,
                Height = 28,
                IsActive = true
            };

            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12
            };
            content.Children.Add(ring);
            content.Children.Add(new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 320
            });

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = title,
                Content = content
            };

            var showTask = dialog.ShowAsync().AsTask();
            await System.Threading.Tasks.Task.Yield();
            try
            {
                await exportAction();
            }
            finally
            {
                dialog.Hide();
                await showTask;
            }
        }

        private static async System.Threading.Tasks.Task<string?> PickSavePathAsync(string extension, string fileTypeDescription, string suggestedFileName)
        {
            if (App.MainWindow == null)
            {
                return null;
            }

            string normalizedExtension = extension.StartsWith('.') ? extension : $".{extension}";
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedFileName)
            };
            picker.FileTypeChoices.Add(fileTypeDescription, new List<string> { normalizedExtension });

            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            StorageFile? file = await picker.PickSaveFileAsync();
            return file?.Path;
        }

        private static ScoreProject CloneProject(ScoreProject source)
        {
            var project = new ScoreProject
            {
                Title = source.Title,
                Bpm = source.Bpm,
                TimeSignature = new TimeSignature(source.TimeSignature.Numerator, source.TimeSignature.Denominator),
                KeySignature = new KeySignature(source.KeySignature.Fifths, source.KeySignature.Mode),
                Ppq = source.Ppq,
                UpdatedAt = source.UpdatedAt,
                Notes = source.Notes.Select(note => new NoteEvent
                {
                    Midi = note.Midi,
                    StartTick = note.StartTick,
                    DurationTicks = note.DurationTicks,
                    BaseDurationTicks = note.BaseDurationTicks,
                    AugmentationDots = note.AugmentationDots,
                    IsRest = note.IsRest,
                    Voice = note.Voice,
                    Accidental = note.Accidental,
                    IsStaccato = note.IsStaccato,
                    IsStaccatissimo = note.IsStaccatissimo,
                    IsAccent = note.IsAccent,
                    Ornament = note.Ornament,
                    OrnamentOffsetX = note.OrnamentOffsetX,
                    OrnamentOffsetY = note.OrnamentOffsetY,
                    GraceOrnamentOffsetX = note.GraceOrnamentOffsetX,
                    GraceOrnamentOffsetY = note.GraceOrnamentOffsetY,
                    TieStart = note.TieStart,
                    TieEnd = note.TieEnd,
                    BeamGroupId = note.BeamGroupId,
                    StemUpOverride = note.StemUpOverride,
                    PreferTrebleStaff = note.PreferTrebleStaff
                }).ToList(),
                ExpressionMarks = source.ExpressionMarks.Select(mark => new ExpressionMark
                {
                    Code = mark.Code,
                    StartTick = mark.StartTick,
                    StaffStepOffset = mark.StaffStepOffset,
                    SpanBeats = mark.SpanBeats,
                    ShapeHeightSteps = mark.ShapeHeightSteps,
                    SlopeSteps = mark.SlopeSteps
                }).ToList(),
                TimeSignatureChanges = source.TimeSignatureChanges.Select(change => new TimeSignatureChange
                {
                    Tick = change.Tick,
                    Numerator = change.Numerator,
                    Denominator = change.Denominator
                }).ToList(),
                KeySignatureChanges = source.KeySignatureChanges.Select(change => new KeySignatureChange
                {
                    Tick = change.Tick,
                    Fifths = change.Fifths,
                    Mode = change.Mode
                }).ToList(),
                StaffClefs = source.StaffClefs.ToDictionary(entry => entry.Key, entry => entry.Value),
                LayoutSystemMeasureCounts = new List<int>(),
                LayoutBarlineOffsets = new Dictionary<int, float>(),
                LayoutMeasuresPerSystemOverride = 0,
                LayoutAutoMeasuresPerSystem = 0
            };

            NormalizeComposeVoicesForStaff(project);
            NormalizeComposeExpressionMarksForStaff(project);
            return project;
        }

        private static void NormalizeComposeVoicesForStaff(ScoreProject project)
        {
            foreach (NoteEvent note in project.Notes)
            {
                bool preferTreble = note.PreferTrebleStaff
                    ?? note.Voice is 1 or 3
                    || note.Midi >= 60;
                note.PreferTrebleStaff = preferTreble;
                note.Voice = preferTreble ? 1 : 2;
            }
        }

        private static void NormalizeComposeExpressionMarksForStaff(ScoreProject project)
        {
            if (project.ExpressionMarks.Count == 0)
            {
                return;
            }

            project.ExpressionMarks = project.ExpressionMarks
                .Select((mark, index) => new { Mark = mark, Index = index })
                .OrderBy(item => item.Mark.StartTick)
                .ThenBy(item => GetComposeExpressionSortPriority(item.Mark))
                .ThenBy(item => item.Index)
                .Select(item => item.Mark)
                .ToList();
        }

        private static int GetComposeExpressionSortPriority(ExpressionMark mark)
        {
            return NormalizeComposeExpressionCode(mark.Code) switch
            {
                "ped_release" => 0,
                "ped_line" => 1,
                "ped" => 2,
                _ => 3
            };
        }

        private static string NormalizeComposeExpressionCode(string? code)
        {
            return string.IsNullOrWhiteSpace(code)
                ? string.Empty
                : code.Trim().ToLowerInvariant();
        }

        private ComposeCategoryRating BuildDialogSeedRating(int index)
        {
            if (_candidateSavedRatings[index] != null)
            {
                return new ComposeCategoryRating
                {
                    Melody = _candidateSavedRatings[index]!.Melody,
                    Rhythm = _candidateSavedRatings[index]!.Rhythm,
                    Harmony = _candidateSavedRatings[index]!.Harmony,
                    MoodFit = _candidateSavedRatings[index]!.MoodFit,
                    Overall = _candidateSavedRatings[index]!.Overall
                };
            }

            double overall = GetRatingSlider(index).Value;
            ComposePrediction? prediction = _candidatePredictions[index];
            return new ComposeCategoryRating
            {
                Melody = (int)Math.Round(prediction?.MelodyScore ?? overall),
                Rhythm = (int)Math.Round(prediction?.RhythmScore ?? overall),
                Harmony = (int)Math.Round(prediction?.HarmonyScore ?? overall),
                MoodFit = (int)Math.Round(prediction?.MoodFitScore ?? overall),
                Overall = (int)Math.Round(overall)
            };
        }

        private async System.Threading.Tasks.Task<ComposeRatingDialogResult> ShowCategoryRatingDialogAsync(int index, ComposeCategoryRating current)
        {
            string optionLabel = ((char)('A' + index)).ToString();

            Slider melodySlider = CreateDialogSlider(current.Melody);
            TextBlock melodyValue = CreateDialogValueText(current.Melody);
            Slider rhythmSlider = CreateDialogSlider(current.Rhythm);
            TextBlock rhythmValue = CreateDialogValueText(current.Rhythm);
            Slider harmonySlider = CreateDialogSlider(current.Harmony);
            TextBlock harmonyValue = CreateDialogValueText(current.Harmony);
            Slider moodFitSlider = CreateDialogSlider(current.MoodFit);
            TextBlock moodFitValue = CreateDialogValueText(current.MoodFit);
            Slider overallSlider = CreateDialogSlider(current.Overall);
            TextBlock overallValue = CreateDialogValueText(current.Overall);

            var content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = T("compose.rate.dialog_hint"),
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.8
                    },
                    CreateDialogRatingRow(T("compose.rate.melody"), melodySlider, melodyValue),
                    CreateDialogRatingRow(T("compose.rate.rhythm"), rhythmSlider, rhythmValue),
                    CreateDialogRatingRow(T("compose.rate.harmony"), harmonySlider, harmonyValue),
                    CreateDialogRatingRow(T("compose.rate.mood_fit"), moodFitSlider, moodFitValue),
                    CreateDialogRatingRow(T("compose.rate.overall"), overallSlider, overallValue)
                }
            };

            AttachDialogSliderValue(melodySlider, melodyValue);
            AttachDialogSliderValue(rhythmSlider, rhythmValue);
            AttachDialogSliderValue(harmonySlider, harmonyValue);
            AttachDialogSliderValue(moodFitSlider, moodFitValue);
            AttachDialogSliderValue(overallSlider, overallValue);

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = TF("compose.rate.dialog_title", optionLabel),
                Content = content,
                PrimaryButtonText = T("compose.rate.dialog_save"),
                SecondaryButtonText = T("compose.rate.dialog_clear"),
                CloseButtonText = T("compose.rate.dialog_cancel"),
                DefaultButton = ContentDialogButton.Primary
            };

            ContentDialogResult result = await dialog.ShowAsync();
            return result switch
            {
                ContentDialogResult.Primary => new ComposeRatingDialogResult(
                    ComposeRatingDialogAction.Save,
                    new ComposeCategoryRating
                    {
                        Melody = (int)Math.Round(melodySlider.Value),
                        Rhythm = (int)Math.Round(rhythmSlider.Value),
                        Harmony = (int)Math.Round(harmonySlider.Value),
                        MoodFit = (int)Math.Round(moodFitSlider.Value),
                        Overall = (int)Math.Round(overallSlider.Value)
                    }),
                ContentDialogResult.Secondary => new ComposeRatingDialogResult(
                    ComposeRatingDialogAction.Remove,
                    current),
                _ => new ComposeRatingDialogResult(ComposeRatingDialogAction.Cancel, current)
            };
        }

        private static Slider CreateDialogSlider(int value)
        {
            return new Slider
            {
                Minimum = 0,
                Maximum = 100,
                StepFrequency = 5,
                TickFrequency = 10,
                SmallChange = 5,
                LargeChange = 10,
                Width = 240,
                Value = Math.Clamp(value, 0, 100)
            };
        }

        private static TextBlock CreateDialogValueText(int value)
        {
            return new TextBlock
            {
                Width = 36,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right,
                Text = Math.Clamp(value, 0, 100).ToString()
            };
        }

        private static Grid CreateDialogRatingRow(string label, Slider slider, TextBlock valueText)
        {
            var grid = new Grid
            {
                ColumnSpacing = 10
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(108) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var labelText = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(labelText, 0);
            Grid.SetColumn(slider, 1);
            Grid.SetColumn(valueText, 2);
            grid.Children.Add(labelText);
            grid.Children.Add(slider);
            grid.Children.Add(valueText);
            return grid;
        }

        private static void AttachDialogSliderValue(Slider slider, TextBlock valueText)
        {
            slider.ValueChanged += (_, args) =>
            {
                valueText.Text = Math.Round(args.NewValue).ToString("0");
            };
        }

        private void RefreshCandidatePredictions()
        {
            if (_candidates.Count == 0)
            {
                return;
            }

            SmartComposeRequest request = _lastRequest ?? BuildRequest();
            IReadOnlyList<ComposeCandidateRanking> reranked = _preferenceService.RankCandidates(request, _candidates);
            var rankingsBySeed = reranked.ToDictionary(item => item.Result.Seed);
            for (int index = 0; index < _candidates.Count; index++)
            {
                if (!rankingsBySeed.TryGetValue(_candidates[index].Seed, out ComposeCandidateRanking? ranking))
                {
                    continue;
                }

                _candidatePredictions[index] = ranking.Prediction;
                _candidatePreferenceScores[index] = ranking.Prediction.FinalScore;
                _candidateSavedRatings[index] = ranking.SavedRating;
            }
        }

        private void ResetRatingControls()
        {
            ResetRatingControl(Option1RatingSlider, Option1RatingValueText, Option1RateButton);
            ResetRatingControl(Option2RatingSlider, Option2RatingValueText, Option2RateButton);
            ResetRatingControl(Option3RatingSlider, Option3RatingValueText, Option3RateButton);
        }

        private static void ResetRatingControl(Slider slider, TextBlock valueText, Button button)
        {
            slider.Value = 50;
            slider.IsEnabled = false;
            valueText.Text = "50";
            button.IsEnabled = false;
        }

        private Slider GetRatingSlider(int index)
        {
            return index switch
            {
                0 => Option1RatingSlider,
                1 => Option2RatingSlider,
                _ => Option3RatingSlider
            };
        }

        private ProgressBar GetScoreProgress(int index)
        {
            return index switch
            {
                0 => Option1ScoreProgress,
                1 => Option2ScoreProgress,
                _ => Option3ScoreProgress
            };
        }

        private TextBlock GetRatingValueText(int index)
        {
            return index switch
            {
                0 => Option1RatingValueText,
                1 => Option2RatingValueText,
                _ => Option3RatingValueText
            };
        }

        private enum ComposeRatingDialogAction
        {
            Cancel,
            Save,
            Remove
        }

        private readonly record struct ComposeRatingDialogResult(ComposeRatingDialogAction Action, ComposeCategoryRating Rating);
    }
}
