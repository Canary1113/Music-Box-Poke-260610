using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MusicBox.Models;
using MusicBox.Services;
using MusicBox.ViewModels;

namespace MusicBox
{
    public sealed class ComposePage : Page
    {
        private TextBlock _summaryText = null!;
        private TextBlock _seedText = null!;
        private TextBox _titleBox = null!;
        private TextBox _tempoBox = null!;
        private TextBox _measuresBox = null!;
        private TextBox _chordTextBox = null!;
        private TextBox _projectPreviewTextBox = null!;
        private ComboBox _styleComboBox = null!;
        private ComboBox _moodComboBox = null!;
        private ComboBox _keyComboBox = null!;
        private ComboBox _modeComboBox = null!;
        private ComboBox _timeSignatureComboBox = null!;
        private ToggleSwitch _bassToggleSwitch = null!;
        private Button _applyButton = null!;
        private Button _sendToConvertButton = null!;

        private MainViewModel? _viewModel;
        private SmartComposeResult? _generatedResult;
        private SmartComposeService? _composer;
        private static bool s_showComposeProbeDialog = true;

        public ComposePage()
        {
            NavigationCacheMode = NavigationCacheMode.Required;
            DebugTrace.Write("ComposePage.ctor begin");
            try
            {
                BuildContent();
                DebugTrace.Write("ComposePage.BuildContent completed");
                Loaded += ComposePage_Loaded;
                DebugTrace.Write("ComposePage.Loaded handler attached");
            }
            catch (Exception ex)
            {
                DebugTrace.Write($"ComposePage.ctor catch: {ex}");
                Content = BuildStartupErrorContent(ex.Message);
            }
        }

        private void BuildContent()
        {
            _summaryText = new TextBlock { Text = "还没有生成内容。", TextWrapping = TextWrapping.Wrap };
            _seedText = new TextBlock { Text = "种子: -", Opacity = 0.68 };
            _titleBox = new TextBox { Text = "智能创作" };
            _tempoBox = new TextBox { Text = "112" };
            _measuresBox = new TextBox { Text = "8" };
            _chordTextBox = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 72 };
            _projectPreviewTextBox = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120 };
            _styleComboBox = CreateComboBox(("流行 Pop", "pop"), ("民谣 Folk", "folk"), ("氛围 Ambient", "ambient"), ("舞曲 Dance", "dance"));
            _moodComboBox = CreateComboBox(("明亮 Bright", "bright"), ("沉静 Moody", "moody"));
            _keyComboBox = CreateComboBox(("C / a", "0"), ("G / e", "1"), ("D / b", "2"), ("A / f#", "3"), ("F / d", "-1"), ("Bb / g", "-2"), ("Eb / c", "-3"));
            _modeComboBox = CreateComboBox(("大调 Major", "major"), ("小调 Minor", "minor"));
            _timeSignatureComboBox = CreateComboBox(("4/4", "4/4"), ("3/4", "3/4"), ("6/8", "6/8"));
            _bassToggleSwitch = new ToggleSwitch { Header = "加入低音根音", IsOn = true };
            _applyButton = new Button { Content = "写入编辑页", IsEnabled = false };
            _sendToConvertButton = new Button { Content = "发送到转换页", IsEnabled = false };

            var generateButton = new Button { Content = "生成片段" };
            generateButton.Click += GenerateButton_Click;
            _applyButton.Click += ApplyButton_Click;
            _sendToConvertButton.Click += SendToConvertButton_Click;

            var root = new StackPanel
            {
                Padding = new Thickness(24),
                Spacing = 12
            };

            root.Children.Add(new TextBlock
            {
                Text = "智能创作",
                FontSize = 28,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            root.Children.Add(new TextBlock
            {
                Text = "根据风格、情绪和调性自动生成一段可继续编辑的音乐草稿。",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.72
            });

            var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            actionRow.Children.Add(generateButton);
            actionRow.Children.Add(_applyButton);
            actionRow.Children.Add(_sendToConvertButton);
            root.Children.Add(actionRow);

            root.Children.Add(CreateLabel("标题"));
            root.Children.Add(_titleBox);
            root.Children.Add(CreateLabel("风格"));
            root.Children.Add(_styleComboBox);
            root.Children.Add(CreateLabel("情绪"));
            root.Children.Add(_moodComboBox);
            root.Children.Add(CreateLabel("调号"));
            root.Children.Add(_keyComboBox);
            root.Children.Add(CreateLabel("调式"));
            root.Children.Add(_modeComboBox);
            root.Children.Add(CreateLabel("拍号"));
            root.Children.Add(_timeSignatureComboBox);
            root.Children.Add(CreateLabel("速度 BPM"));
            root.Children.Add(_tempoBox);
            root.Children.Add(CreateLabel("小节数"));
            root.Children.Add(_measuresBox);
            root.Children.Add(_bassToggleSwitch);

            root.Children.Add(new TextBlock
            {
                Text = "生成结果",
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 12, 0, 0)
            });
            root.Children.Add(_summaryText);
            root.Children.Add(_seedText);
            root.Children.Add(CreateLabel("和弦走向"));
            root.Children.Add(_chordTextBox);
            root.Children.Add(CreateLabel("生成说明"));
            root.Children.Add(_projectPreviewTextBox);

            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = root
            };
        }

        private static UIElement BuildStartupErrorContent(string message)
        {
            var panel = new StackPanel
            {
                Padding = new Thickness(24),
                Spacing = 12
            };

            panel.Children.Add(new TextBlock
            {
                Text = "智能创作暂时不可用",
                FontSize = 28,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            panel.Children.Add(new TextBlock
            {
                Text = $"页面初始化失败: {message}",
                TextWrapping = TextWrapping.Wrap
            });

            return new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = panel
            };
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            DebugTrace.Write("ComposePage.OnNavigatedTo begin");
            base.OnNavigatedTo(e);
            if (e.Parameter is MainViewModel vm)
            {
                _viewModel = vm;
                DataContext = vm;
            }
            else if (_viewModel == null && DataContext is MainViewModel existingVm)
            {
                _viewModel = existingVm;
            }
            DebugTrace.Write("ComposePage.OnNavigatedTo end");
        }

        private async void ComposePage_Loaded(object sender, RoutedEventArgs e)
        {
            DebugTrace.Write("ComposePage.Loaded begin");
            if (s_showComposeProbeDialog)
            {
                s_showComposeProbeDialog = false;
                await ShowDebugDialogAsync("创作页测试", "已进入创作页 Loaded。说明导航和页面构造已经完成。");
                DebugTrace.Write("ComposePage.Loaded probe dialog closed");
            }

            if (_generatedResult == null)
            {
                DebugTrace.Write("ComposePage.Loaded calling TryGenerateMusic");
                TryGenerateMusic();
            }
            DebugTrace.Write("ComposePage.Loaded end");
        }

        private void GenerateButton_Click(object sender, RoutedEventArgs e)
        {
            TryGenerateMusic();
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_generatedResult == null || _viewModel == null)
            {
                return;
            }

            _viewModel.LoadProjectSnapshot(CloneProject(_generatedResult.Project));
            _viewModel.SetStatus($"已将智能创作写入编辑页: {_generatedResult.Project.Title}");
            if (App.MainWindow is MainWindow window)
            {
                window.NavigateToPage("editor");
            }
        }

        private void SendToConvertButton_Click(object sender, RoutedEventArgs e)
        {
            if (_generatedResult == null || _viewModel == null)
            {
                return;
            }

            _viewModel.LoadProjectSnapshot(CloneProject(_generatedResult.Project));
            _viewModel.SetStatus($"已将智能创作发送到转换页: {_generatedResult.Project.Title}");
            if (App.MainWindow is MainWindow window)
            {
                window.NavigateToConvertAndImportEditor();
            }
        }

        private void TryGenerateMusic()
        {
            DebugTrace.Write("ComposePage.TryGenerateMusic begin");
            try
            {
                GenerateMusic();
                DebugTrace.Write("ComposePage.TryGenerateMusic success");
            }
            catch (Exception ex)
            {
                DebugTrace.Write($"ComposePage.TryGenerateMusic catch: {ex}");
                _generatedResult = null;
                _summaryText.Text = $"生成失败: {ex.Message}";
                _seedText.Text = "种子: -";
                _chordTextBox.Text = string.Empty;
                _projectPreviewTextBox.Text = "请调整参数后重试，或稍后再点一次“生成片段”。";
                _applyButton.IsEnabled = false;
                _sendToConvertButton.IsEnabled = false;
                _viewModel?.SetStatus($"智能创作失败: {ex.Message}");
                _ = ShowDebugDialogAsync("创作页生成异常", ex.ToString());
            }
        }

        private void GenerateMusic()
        {
            DebugTrace.Write("ComposePage.GenerateMusic begin");
            _composer ??= new SmartComposeService();
            SmartComposeRequest request = BuildRequest();
            DebugTrace.Write($"ComposePage.GenerateMusic request bpm={request.Bpm} measures={request.Measures} style={request.StyleId} mood={request.MoodId}");
            _generatedResult = _composer.Generate(request);
            DebugTrace.Write("ComposePage.GenerateMusic service returned");

            _summaryText.Text = _generatedResult.Summary;
            _seedText.Text = $"种子: {_generatedResult.Seed}";
            _chordTextBox.Text = _generatedResult.ChordProgression;
            _projectPreviewTextBox.Text = BuildProjectPreviewText(_generatedResult.Project);
            _applyButton.IsEnabled = true;
            _sendToConvertButton.IsEnabled = true;
            _viewModel?.SetStatus($"已生成智能创作片段: {_generatedResult.Project.Title}");
            DebugTrace.Write("ComposePage.GenerateMusic end");
        }

        private SmartComposeRequest BuildRequest()
        {
            return new SmartComposeRequest
            {
                Title = string.IsNullOrWhiteSpace(_titleBox.Text) ? "智能创作" : _titleBox.Text.Trim(),
                Bpm = ParseBoundedInt(_tempoBox.Text, 112, 60, 220),
                Measures = ParseBoundedInt(_measuresBox.Text, 8, 4, 32),
                KeyFifths = ParseIntTag(_keyComboBox, 0),
                Mode = GetSelectedTag(_modeComboBox) == "minor" ? KeyMode.Minor : KeyMode.Major,
                TimeSignature = ParseTimeSignature(GetSelectedTag(_timeSignatureComboBox)),
                StyleId = GetSelectedTag(_styleComboBox),
                MoodId = GetSelectedTag(_moodComboBox),
                IncludeBass = _bassToggleSwitch.IsOn
            };
        }

        private static TextBlock CreateLabel(string text)
        {
            return new TextBlock { Text = text };
        }

        private static ComboBox CreateComboBox(params (string Text, string Tag)[] items)
        {
            var comboBox = new ComboBox { SelectedIndex = 0 };
            foreach ((string text, string tag) in items)
            {
                comboBox.Items.Add(new ComboBoxItem { Content = text, Tag = tag });
            }

            return comboBox;
        }

        private static string BuildProjectPreviewText(ScoreProject project)
        {
            int noteCount = project.Notes.Count(note => !note.IsRest && note.Voice == 1);
            int bassCount = project.Notes.Count(note => !note.IsRest && note.Voice != 1);
            int totalMeasures = Math.Max(1, project.Notes.Count == 0
                ? 1
                : (int)Math.Ceiling(project.Notes.Max(note => note.StartTick + note.DurationTicks) / (double)project.TimeSignature.TicksPerMeasure(project.Ppq)));

            return string.Join(Environment.NewLine, new[]
            {
                $"标题: {project.Title}",
                $"速度: {project.Bpm} BPM",
                $"拍号: {project.TimeSignature.Numerator}/{project.TimeSignature.Denominator}",
                $"主旋律音符: {noteCount}",
                $"低音音符: {bassCount}",
                $"估算长度: {totalMeasures} 小节",
                "生成完成后可直接写入编辑页，继续微调音高、时值和表情。"
            });
        }

        private static string GetSelectedTag(ComboBox comboBox)
        {
            return (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString()?.Trim().ToLowerInvariant() ?? string.Empty;
        }

        private static int ParseIntTag(ComboBox comboBox, int fallback)
        {
            return int.TryParse((comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out int value)
                ? value
                : fallback;
        }

        private static int ParseBoundedInt(string? text, int fallback, int min, int max)
        {
            return int.TryParse(text, out int value)
                ? Math.Clamp(value, min, max)
                : fallback;
        }

        private static TimeSignature ParseTimeSignature(string value)
        {
            string[] parts = value.Split('/');
            if (parts.Length == 2
                && int.TryParse(parts[0], out int numerator)
                && int.TryParse(parts[1], out int denominator))
            {
                return new TimeSignature(numerator, denominator);
            }

            return new TimeSignature(4, 4);
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

        private async Task ShowDebugDialogAsync(string title, string message)
        {
            if (XamlRoot == null)
            {
                return;
            }

            try
            {
                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = new ScrollViewer
                    {
                        MaxHeight = 420,
                        Content = new TextBlock
                        {
                            Text = message,
                            TextWrapping = TextWrapping.Wrap
                        }
                    },
                    CloseButtonText = "关闭",
                    XamlRoot = XamlRoot
                };

                await dialog.ShowAsync();
            }
            catch
            {
            }
        }
    }
}

