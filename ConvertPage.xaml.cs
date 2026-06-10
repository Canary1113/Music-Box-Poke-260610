using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Printing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Printing;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;
using MusicBox.Models;
using MusicBox.Services;
using MusicBox.ViewModels;

namespace MusicBox
{
    public sealed partial class ConvertPage : Page
    {
        private static ConvertPageStateCache? s_cache;

        private MainViewModel? _viewModel;
        private readonly JianpuConverter _jianpuConverter = new();
        private readonly GuitarTabConverter _guitarTabConverter = new();
        private readonly MusicXmlExporter _musicXmlExporter = new();
        private readonly RasterPdfExportService _pdfExporter = new();
        private JianpuConverter.NativePreviewModel? _nativePreview;
        private TabPreviewModel? _guitarPreview;
        private string _latestGuitarTabText = string.Empty;
        private bool _previewLoaded;
        private ScoreProject? _sourceProject;
        private Border? _jianpuPreviewHost;
        private Border? _guitarTabPreviewHost;
        private ScrollViewer? _guitarPreviewScrollViewer;
        private CanvasControl? _guitarTabCanvas;
        private string _activeFormat = "jianpu";
        private bool _isPreparingPrintPreview;
        private PrintManager? _printManager;
        private PrintDocument? _printDocument;
        private IPrintDocumentSource? _printDocumentSource;
        private readonly List<UIElement> _printPages = new();
        private readonly List<RenderedPage> _renderedPages = new();
        private CanvasFontSet? _musicFontSet;
        private CanvasFontFace? _musicFontFace;
        private bool _musicBraceFontAttempted;

        private const string MusicFontRelativeFolder = "Assets\\Fonts";
        private const string MusicFontFamily = "Bravura";
        private const string MusicFontFile = "Bravura.otf";
        private const int SmuflBrace = 0xE000;

        private readonly CanvasTextFormat _titleFormat = new()
        {
            FontFamily = "Times New Roman",
            FontSize = 29f,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Top
        };

        private readonly CanvasTextFormat _metaFormat = new()
        {
            FontFamily = "Times New Roman",
            FontSize = 16f,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = CanvasHorizontalAlignment.Left,
            VerticalAlignment = CanvasVerticalAlignment.Top
        };

        private readonly CanvasTextFormat _tokenFormat = new()
        {
            FontFamily = "Times New Roman",
            FontSize = 25f,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = CanvasHorizontalAlignment.Left,
            VerticalAlignment = CanvasVerticalAlignment.Top
        };

        private readonly CanvasTextFormat _accidentalFormat = new()
        {
            FontFamily = "Times New Roman",
            FontSize = 14f,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = CanvasHorizontalAlignment.Left,
            VerticalAlignment = CanvasVerticalAlignment.Top
        };

        private readonly CanvasTextFormat _chordDegreeFormat = new()
        {
            FontFamily = "Times New Roman",
            FontSize = 21f,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = CanvasHorizontalAlignment.Left,
            VerticalAlignment = CanvasVerticalAlignment.Top
        };

        private readonly CanvasTextFormat _chordAccidentalFormat = new()
        {
            FontFamily = "Times New Roman",
            FontSize = 12f,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = CanvasHorizontalAlignment.Left,
            VerticalAlignment = CanvasVerticalAlignment.Top
        };

        private readonly CanvasTextFormat _barFormat = new()
        {
            FontFamily = "Times New Roman",
            FontSize = 28f,
            FontWeight = Microsoft.UI.Text.FontWeights.Normal,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Top
        };

        private readonly CanvasTextFormat _measureNumberFormat = new()
        {
            FontFamily = "Times New Roman",
            FontSize = 15f,
            FontWeight = Microsoft.UI.Text.FontWeights.Light,
            HorizontalAlignment = CanvasHorizontalAlignment.Left,
            VerticalAlignment = CanvasVerticalAlignment.Top
        };

        private readonly CanvasTextFormat _statusFormat = new()
        {
            FontFamily = "Microsoft YaHei UI",
            FontSize = 18f,
            FontWeight = Microsoft.UI.Text.FontWeights.Normal,
            HorizontalAlignment = CanvasHorizontalAlignment.Left,
            VerticalAlignment = CanvasVerticalAlignment.Top
        };

        private const float ChordRowStep = 19.5f;
        private const float ChordDotSpacing = 4.2f;
        private readonly CanvasTextFormat _guitarStringFormat = new()
        {
            FontFamily = "Consolas",
            FontSize = 18f,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = CanvasHorizontalAlignment.Right,
            VerticalAlignment = CanvasVerticalAlignment.Center
        };

        private readonly CanvasTextFormat _guitarFretFormat = new()
        {
            FontFamily = "Consolas",
            FontSize = 18f,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center
        };

        private sealed record RenderedPage(byte[] JpegBytes, int PixelWidth, int PixelHeight);

        public ConvertPage()
        {
            InitializeComponent();
            BuildDynamicPreviewSurface();
            NavigationCacheMode = NavigationCacheMode.Required;
            Loaded += ConvertPage_Loaded;
            Unloaded += ConvertPage_Unloaded;
            ActualThemeChanged += ConvertPage_ActualThemeChanged;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
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

            RestorePageStateFromCache();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            SavePageStateToCache();
        }

        private async void ConvertPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_sourceProject != null && _nativePreview != null)
            {
                UpdateCanvasSize();
                UpdateGuitarCanvasSize();
                JianpuCanvas?.Invalidate();
                _guitarTabCanvas?.Invalidate();
                ApplyPreviewMode();
                return;
            }

            await RefreshPreviewAsync();
        }

        private void ConvertPage_Unloaded(object sender, RoutedEventArgs e)
        {
            UnregisterPrintManager();
            DisposeMusicBraceFont();
        }

        private async void ConvertPage_ActualThemeChanged(FrameworkElement sender, object args)
        {
            await RefreshPreviewAsync();
        }

        private void BuildDynamicPreviewSurface()
        {
            if (RootGrid == null || RootGrid.Children.Count == 0 || _jianpuPreviewHost != null)
            {
                return;
            }

            _jianpuPreviewHost = RootGrid.Children[0] as Border;
            if (_jianpuPreviewHost == null)
            {
                return;
            }

            _guitarTabCanvas = new CanvasControl
            {
                Width = 1400,
                Height = 900,
                VerticalAlignment = VerticalAlignment.Top,
                ClearColor = Color.FromArgb(0, 0, 0, 0)
            };
            _guitarTabCanvas.Draw += GuitarTabCanvas_Draw;

            _guitarPreviewScrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = null,
                Padding = new Thickness(0)
            };
            _guitarPreviewScrollViewer.SizeChanged += PreviewScrollViewer_SizeChanged;
            _guitarPreviewScrollViewer.Content = new Grid
            {
                Margin = new Thickness(-2, 0, -2, -2),
                Background = null,
                Children =
                {
                    _guitarTabCanvas
                }
            };

            _guitarTabPreviewHost = new Border
            {
                Margin = new Thickness(0),
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(5),
                Background = null,
                BorderBrush = null,
                BorderThickness = new Thickness(0),
                Translation = new System.Numerics.Vector3(0, 0, 16),
                VerticalAlignment = VerticalAlignment.Stretch,
                Visibility = Visibility.Collapsed,
                Child = _guitarPreviewScrollViewer
            };

            RootGrid.Children.Insert(1, _guitarTabPreviewHost);
            ApplyPreviewMode();
        }

        private void ApplyPreviewMode()
        {
            bool showGuitar = string.Equals(_activeFormat, "guitar", StringComparison.OrdinalIgnoreCase);

            if (_jianpuPreviewHost != null)
            {
                _jianpuPreviewHost.Visibility = showGuitar ? Visibility.Collapsed : Visibility.Visible;
            }

            if (_guitarTabPreviewHost != null)
            {
                _guitarTabPreviewHost.Visibility = showGuitar ? Visibility.Visible : Visibility.Collapsed;
            }

            if (showGuitar)
            {
                UpdateGuitarCanvasSize();
                _guitarTabCanvas?.Invalidate();
            }

            if (App.MainWindow is MainWindow window)
            {
                window.SyncConvertFormatSelection(_activeFormat);
            }
        }

        public void HandleTitleBarImportCommand(string command)
        {
            string normalized = command?.Trim().ToLowerInvariant() ?? string.Empty;
            if (normalized == "import_file")
            {
                _ = ImportFromPickerAsync();
                return;
            }

            if (normalized == "import_editor")
            {
                _ = ImportFromEditorAsync();
            }
        }

        public void HandleTitleBarFormatCommand(string command)
        {
            string normalized = command?.Trim().ToLowerInvariant() ?? string.Empty;
            if (normalized == "staff_to_guitar_tab")
            {
                _activeFormat = "guitar";
                ApplyPreviewMode();
                _ = RefreshPreviewAsync();
                return;
            }

            _activeFormat = "jianpu";
            ApplyPreviewMode();
            _ = RefreshPreviewAsync();
        }

        public void HandleTitleBarExportCommand(string command)
        {
            string normalized = command?.Trim().ToLowerInvariant() ?? string.Empty;
            if (normalized == "print")
            {
                _ = PrintPreviewAsync();
                return;
            }

            if (normalized == "export_pdf")
            {
                _ = ExportPreviewToPdfAsync();
                return;
            }

            if (normalized == "export_musicxml")
            {
                _ = ExportSourceToMusicXmlAsync();
                return;
            }

            if (normalized == "export_guitar_tab_txt")
            {
                _ = ExportGuitarTabAsync();
            }
        }

        private async Task ImportFromPickerAsync()
        {
            if (_viewModel == null)
            {
                SetStatus(Loc("\u672a\u627e\u5230\u5de5\u7a0b\u6570\u636e\u3002", "Project data not found."));
                return;
            }

            string? path = await PickOpenPathAsync(".json", ".musicxml", ".xml");
            if (string.IsNullOrWhiteSpace(path))
            {
                SetStatus(Loc("\u5df2\u53d6\u6d88\u5bfc\u5165\u3002", "Import canceled."));
                return;
            }

            try
            {
                string ext = Path.GetExtension(path)?.Trim().ToLowerInvariant() ?? string.Empty;
                if (ext == ".json")
                {
                    _viewModel.LoadProjectFromPath(path);
                }
                else if (ext is ".musicxml" or ".xml")
                {
                    _viewModel.ImportMusicXmlFromPath(path);
                }
                else
                {
                    SetStatus(Loc("\u4e0d\u652f\u6301\u7684\u6587\u4ef6\u7c7b\u578b\u3002", "Unsupported file type."));
                    return;
                }

                _sourceProject = CloneProject(_viewModel.Project);
                await RefreshPreviewAsync();
            }
            catch (Exception ex)
            {
                SetStatus($"{Loc("导入失败", "Import failed")}: {ex.Message}");
            }
        }

        private async Task ImportFromEditorAsync()
        {
            if (_viewModel == null)
            {
                SetStatus(Loc("\u672a\u627e\u5230\u5de5\u7a0b\u6570\u636e\u3002", "Project data not found."));
                return;
            }

            _sourceProject = CloneProject(_viewModel.Project);
            ApplyPreviewMode();
            await RefreshPreviewAsync();
            string title = string.IsNullOrWhiteSpace(_sourceProject.Title) ? Loc("\u672a\u547d\u540d", "Untitled") : _sourceProject.Title.Trim();
            SetStatus($"{Loc("\u5df2\u4ece\u4e94\u7ebf\u8c31\u9875\u5bfc\u5165", "Imported from staff")}: {title}");
        }

        private async Task RefreshPreviewAsync()
        {
            if (_sourceProject == null)
            {
                _previewLoaded = false;
                _nativePreview = null;
                _guitarPreview = null;
                _latestGuitarTabText = string.Empty;
                JianpuCanvas?.Invalidate();
                _guitarTabCanvas?.Invalidate();
                SavePageStateToCache();
                SetStatus(Loc("请先在“导入”菜单里选择“五线谱页导入”或“从文件导入”。", "Choose Import -> From Staff or From File first."));
                return;
            }

            if (_viewModel == null)
            {
                SetStatus(Loc("未找到工程数据。", "Project data not found."));
                return;
            }

            try
            {
                SetStatus(Loc("正在转换简谱与吉他谱...", "Converting to jianpu and guitar tab..."));
                float viewportWidth = (float)Math.Max(760d, (PreviewScrollViewer?.ActualWidth ?? RootGrid?.ActualWidth ?? 1200d) - 92d);

                _latestGuitarTabText = _guitarTabConverter.BuildAsciiTab(_sourceProject);
                _nativePreview = _jianpuConverter.BuildNativePreviewModel(_sourceProject, viewportWidth);
                _guitarPreview = _guitarTabConverter.BuildPreviewModel(_sourceProject, viewportWidth);
                _previewLoaded = true;

                UpdateCanvasSize();
                UpdateGuitarCanvasSize();
                JianpuCanvas?.Invalidate();
                _guitarTabCanvas?.Invalidate();
                SavePageStateToCache();
                SetStatus(Loc("简谱与吉他谱预览已更新。", "Jianpu and guitar tab previews updated."));
            }
            catch (Exception ex)
            {
                _previewLoaded = false;
                _nativePreview = null;
                _guitarPreview = null;
                _latestGuitarTabText = string.Empty;
                JianpuCanvas?.Invalidate();
                _guitarTabCanvas?.Invalidate();
                SavePageStateToCache();
                SetStatus($"{Loc("转换失败", "Conversion failed")}: {ex.Message}");
            }

            await Task.CompletedTask;
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
                UpdatedAt = source.UpdatedAt
            };

            project.Notes = source.Notes.Select(n => new NoteEvent
            {
                Midi = n.Midi,
                StartTick = n.StartTick,
                DurationTicks = n.DurationTicks,
                BaseDurationTicks = n.BaseDurationTicks,
                AugmentationDots = n.AugmentationDots,
                IsRest = n.IsRest,
                Voice = n.Voice,
                Accidental = n.Accidental,
                IsStaccato = n.IsStaccato,
                IsStaccatissimo = n.IsStaccatissimo,
                IsAccent = n.IsAccent,
                Ornament = n.Ornament,
                OrnamentOffsetX = n.OrnamentOffsetX,
                OrnamentOffsetY = n.OrnamentOffsetY,
                GraceOrnamentOffsetX = n.GraceOrnamentOffsetX,
                GraceOrnamentOffsetY = n.GraceOrnamentOffsetY,
                TieStart = n.TieStart,
                TieEnd = n.TieEnd,
                BeamGroupId = n.BeamGroupId,
                StemUpOverride = n.StemUpOverride,
                PreferTrebleStaff = n.PreferTrebleStaff
            }).ToList();

            project.ExpressionMarks = source.ExpressionMarks.Select(m => new ExpressionMark
            {
                Code = m.Code,
                StartTick = m.StartTick,
                StaffStepOffset = m.StaffStepOffset,
                SpanBeats = m.SpanBeats,
                ShapeHeightSteps = m.ShapeHeightSteps,
                SlopeSteps = m.SlopeSteps
            }).ToList();

            project.TimeSignatureChanges = source.TimeSignatureChanges.Select(c => new TimeSignatureChange
            {
                Tick = c.Tick,
                Numerator = c.Numerator,
                Denominator = c.Denominator
            }).ToList();
            project.KeySignatureChanges = source.KeySignatureChanges.Select(c => new KeySignatureChange
            {
                Tick = c.Tick,
                Fifths = c.Fifths,
                Mode = c.Mode
            }).ToList();
            project.StaffClefs = source.StaffClefs.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            project.LayoutSystemMeasureCounts = source.LayoutSystemMeasureCounts.ToList();
            project.LayoutBarlineOffsets = source.LayoutBarlineOffsets.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            project.LayoutMeasuresPerSystemOverride = source.LayoutMeasuresPerSystemOverride;
            project.LayoutAutoMeasuresPerSystem = source.LayoutAutoMeasuresPerSystem;
            return project;
        }

        private void PreviewScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateCanvasSize();
            UpdateGuitarCanvasSize();
            JianpuCanvas?.Invalidate();
            _guitarTabCanvas?.Invalidate();
        }

        private void UpdateCanvasSize()
        {
            if (JianpuCanvas == null)
            {
                return;
            }

            float viewportWidth = (float)Math.Max(760d, PreviewScrollViewer?.ActualWidth ?? RootGrid?.ActualWidth ?? 1200d);
            float viewportHeight = (float)Math.Max(560d, PreviewScrollViewer?.ActualHeight ?? RootGrid?.ActualHeight ?? 720d);
            float targetWidth = Math.Max(viewportWidth - 4f, _nativePreview?.ContentWidth ?? 1200f);
            float targetHeight = Math.Max(viewportHeight - 4f, EstimateNativeContentHeight(_nativePreview));

            JianpuCanvas.Width = targetWidth;
            JianpuCanvas.Height = targetHeight;
        }


        private void UpdateGuitarCanvasSize()
        {
            if (_guitarTabCanvas == null)
            {
                return;
            }

            float viewportWidth = (float)Math.Max(760d, _guitarPreviewScrollViewer?.ActualWidth ?? RootGrid?.ActualWidth ?? 1200d);
            float viewportHeight = (float)Math.Max(560d, _guitarPreviewScrollViewer?.ActualHeight ?? RootGrid?.ActualHeight ?? 720d);
            float targetWidth = Math.Max(viewportWidth - 4f, _guitarPreview?.ContentWidth ?? 960f);
            float targetHeight = Math.Max(viewportHeight - 4f, EstimateGuitarContentHeight(_guitarPreview));

            _guitarTabCanvas.Width = targetWidth;
            _guitarTabCanvas.Height = targetHeight;
        }

        private void GuitarTabCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            DrawGuitarPreview(args.DrawingSession, (float)Math.Max(200d, _guitarTabCanvas?.ActualWidth ?? 0d), printMode: false, _guitarPreview);
        }

        private static float EstimateGuitarContentHeight(TabPreviewModel? preview)
        {
            if (preview == null)
            {
                return 720f;
            }

            return Math.Max(720f, 214f + preview.Systems.Count * 214f + 40f);
        }
        private void JianpuCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            DrawJianpuPreview(args.DrawingSession, (float)Math.Max(200d, JianpuCanvas.ActualWidth), printMode: false, _nativePreview);
        }

        private static float EstimateNativeContentHeight(JianpuConverter.NativePreviewModel? preview)
        {
            if (preview == null)
            {
                return 720f;
            }

            float systemTop = 226f;
            foreach (var system in preview.Systems)
            {
                float upperExtraRise = EstimateUpperChordRise(system);
                float extraSystemPadding = Math.Max(0f, upperExtraRise - 10f);
                systemTop += 188f + extraSystemPadding;
            }

            return Math.Max(720f, systemTop + 40f);
        }

        private async Task<IReadOnlyList<RenderedPage>> BuildRenderedPagesAsync()
        {
            await RefreshPreviewAsync();

            bool showGuitar = string.Equals(_activeFormat, "guitar", StringComparison.OrdinalIgnoreCase);
            if (showGuitar && (_guitarPreview == null || _guitarPreview.Systems.Count == 0))
            {
                throw new InvalidOperationException(Loc("吉他谱预览尚未就绪。", "Guitar tab preview is not ready."));
            }

            if (!showGuitar && _nativePreview == null)
            {
                throw new InvalidOperationException(Loc("简谱预览尚未就绪。", "Jianpu preview is not ready."));
            }

            JianpuConverter.NativePreviewModel? renderNativePreview = _nativePreview;
            TabPreviewModel? renderGuitarPreview = _guitarPreview;
            if (_sourceProject != null)
            {
                const float printPreviewWidth = 1080f;
                if (showGuitar)
                {
                    renderGuitarPreview = _guitarTabConverter.BuildPreviewModel(_sourceProject, printPreviewWidth);
                }
                else
                {
                    renderNativePreview = _jianpuConverter.BuildNativePreviewModel(_sourceProject, printPreviewWidth);
                }
            }

            if (showGuitar && (renderGuitarPreview == null || renderGuitarPreview.Systems.Count == 0))
            {
                throw new InvalidOperationException(Loc("吉他谱打印预览尚未就绪。", "Guitar tab print preview is not ready."));
            }

            if (!showGuitar && (renderNativePreview == null || renderNativePreview.Systems.Count == 0))
            {
                throw new InvalidOperationException(Loc("简谱打印预览尚未就绪。", "Jianpu print preview is not ready."));
            }

            const int pagePixelWidth = 2480;
            const int pagePixelHeight = 3508;
            const float printDpi = 300f;
            const float sidePadding = 86f;
            const float topPadding = 88f;
            const float bottomPadding = 124f;

            float contentWidth = showGuitar
                ? Math.Max(860f, renderGuitarPreview?.ContentWidth ?? 860f)
                : Math.Max(860f, renderNativePreview?.ContentWidth ?? 860f);
            float contentHeight = showGuitar
                ? EstimateGuitarContentHeight(renderGuitarPreview)
                : EstimateNativeContentHeight(renderNativePreview);
            float scale = Math.Clamp((pagePixelWidth - sidePadding * 2f) / Math.Max(1f, contentWidth), 0.72f, 2.35f);
            float logicalPageHeight = (pagePixelHeight - topPadding - bottomPadding) / Math.Max(0.01f, scale);
            IReadOnlyList<(float Top, float Bottom)> systems = showGuitar
                ? BuildGuitarSystemSpans(renderGuitarPreview)
                : BuildJianpuSystemSpans(renderNativePreview);

            if (systems.Count == 0)
            {
                systems = new[] { (0f, Math.Max(400f, contentHeight)) };
            }

            var pages = new List<RenderedPage>();
            for (int systemIndex = 0; systemIndex < systems.Count;)
            {
                float pageStartY = pages.Count == 0
                    ? 0f
                    : Math.Max(0f, systems[systemIndex].Top - 24f);
                float pageEndY = systems[systemIndex].Bottom;
                int lastSystem = systemIndex;

                while (lastSystem + 1 < systems.Count && systems[lastSystem + 1].Bottom - pageStartY <= logicalPageHeight)
                {
                    lastSystem++;
                    pageEndY = systems[lastSystem].Bottom;
                }

                using var renderTarget = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), pagePixelWidth, pagePixelHeight, printDpi);
                using (CanvasDrawingSession ds = renderTarget.CreateDrawingSession())
                {
                    ds.Clear(Colors.White);
                    ds.Transform = System.Numerics.Matrix3x2.CreateScale(scale)
                        * System.Numerics.Matrix3x2.CreateTranslation(sidePadding, topPadding - pageStartY * scale);

                    if (showGuitar)
                    {
                        DrawGuitarPreview(ds, contentWidth, printMode: true, renderGuitarPreview);
                    }
                    else
                    {
                        DrawJianpuPreview(ds, contentWidth, printMode: true, renderNativePreview);
                    }

                    ds.Transform = System.Numerics.Matrix3x2.Identity;
                    string footerText = (pages.Count + 1).ToString();
                    ds.DrawText(
                        footerText,
                        0f,
                        pagePixelHeight - 74f,
                        pagePixelWidth,
                        28f,
                        Colors.Black,
                        new CanvasTextFormat
                        {
                            FontFamily = "Times New Roman",
                            FontSize = 24f,
                            HorizontalAlignment = CanvasHorizontalAlignment.Center,
                            VerticalAlignment = CanvasVerticalAlignment.Center
                        });
                }

                pages.Add(await EncodeRenderTargetAsync(renderTarget));
                systemIndex = lastSystem + 1;
            }

            return pages;
        }

        private async Task PreparePrintPagesAsync()
        {
            _renderedPages.Clear();
            _printPages.Clear();

            IReadOnlyList<RenderedPage> pages = await BuildRenderedPagesAsync();
            foreach (RenderedPage page in pages)
            {
                _renderedPages.Add(page);
                _printPages.Add(await CreatePrintPageElementAsync(page));
            }
        }

        private void EnsurePrintManager()
        {
            if (_printDocument != null && _printManager != null)
            {
                return;
            }

            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            _printDocument = new PrintDocument();
            _printDocument.Paginate += PrintDocument_Paginate;
            _printDocument.GetPreviewPage += PrintDocument_GetPreviewPage;
            _printDocument.AddPages += PrintDocument_AddPages;
            _printDocumentSource = _printDocument.DocumentSource;

            _printManager = PrintManagerInterop.GetForWindow(hwnd);
            _printManager.PrintTaskRequested += PrintManager_PrintTaskRequested;
        }

        private void UnregisterPrintManager()
        {
            if (_printManager != null)
            {
                _printManager.PrintTaskRequested -= PrintManager_PrintTaskRequested;
                _printManager = null;
            }

            if (_printDocument != null)
            {
                _printDocument.Paginate -= PrintDocument_Paginate;
                _printDocument.GetPreviewPage -= PrintDocument_GetPreviewPage;
                _printDocument.AddPages -= PrintDocument_AddPages;
                _printDocument = null;
            }

            _printDocumentSource = null;
            _printPages.Clear();
            _renderedPages.Clear();
        }

        private async Task<RenderedPage> EncodeRenderTargetAsync(CanvasRenderTarget renderTarget)
        {
            using var stream = new InMemoryRandomAccessStream();
            await renderTarget.SaveAsync(stream, CanvasBitmapFileFormat.Jpeg);
            stream.Seek(0);

            byte[] bytes = new byte[stream.Size];
            using (Stream managed = stream.AsStreamForRead())
            {
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = await managed.ReadAsync(bytes, offset, bytes.Length - offset).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        break;
                    }

                    offset += read;
                }
            }

            return new RenderedPage(bytes, (int)renderTarget.SizeInPixels.Width, (int)renderTarget.SizeInPixels.Height);
        }

        private async Task<UIElement> CreatePrintPageElementAsync(RenderedPage page)
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(page.JpegBytes.AsBuffer());
            stream.Seek(0);

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);

            return new Grid
            {
                Background = new SolidColorBrush(Colors.White),
                Children =
                {
                    new Image
                    {
                        Source = bitmap,
                        Stretch = Stretch.Uniform,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        VerticalAlignment = VerticalAlignment.Stretch
                    }
                }
            };
        }

        private void PrintManager_PrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
        {
            args.Request.CreatePrintTask(" ", requestArgs =>
            {
                if (_printDocumentSource != null)
                {
                    requestArgs.SetSource(_printDocumentSource);
                }
            });
        }

        private void PrintDocument_Paginate(object sender, PaginateEventArgs e)
        {
            if (_printDocument == null)
            {
                return;
            }

            _printDocument.SetPreviewPageCount(Math.Max(1, _printPages.Count), PreviewPageCountType.Final);
        }

        private void PrintDocument_GetPreviewPage(object sender, GetPreviewPageEventArgs e)
        {
            if (_printDocument == null || _printPages.Count == 0)
            {
                return;
            }

            int index = Math.Clamp((int)e.PageNumber - 1, 0, _printPages.Count - 1);
            _printDocument.SetPreviewPage((int)e.PageNumber, _printPages[index]);
        }

        private void PrintDocument_AddPages(object sender, AddPagesEventArgs e)
        {
            if (_printDocument == null)
            {
                return;
            }

            foreach (UIElement page in _printPages)
            {
                _printDocument.AddPage(page);
            }

            _printDocument.AddPagesComplete();
        }

        private static IReadOnlyList<(float Top, float Bottom)> BuildGuitarSystemSpans(TabPreviewModel? preview)
        {
            if (preview == null || preview.Systems.Count == 0)
            {
                return Array.Empty<(float Top, float Bottom)>();
            }

            var spans = new List<(float Top, float Bottom)>(preview.Systems.Count);
            float systemTop = 214f;
            foreach (TabSystem _ in preview.Systems)
            {
                spans.Add((Math.Max(0f, systemTop - 26f), systemTop + 178f));
                systemTop += 214f;
            }

            return spans;
        }

        private static IReadOnlyList<(float Top, float Bottom)> BuildJianpuSystemSpans(JianpuConverter.NativePreviewModel? preview)
        {
            if (preview == null || preview.Systems.Count == 0)
            {
                return Array.Empty<(float Top, float Bottom)>();
            }

            var spans = new List<(float Top, float Bottom)>(preview.Systems.Count);
            float systemTop = 226f;
            foreach (JianpuConverter.NativeSystem system in preview.Systems)
            {
                float upperExtraRise = EstimateUpperChordRise(system);
                float extraSystemPadding = Math.Max(0f, upperExtraRise - 10f);
                float upperRowTop = systemTop + 26f + extraSystemPadding;
                float lowerRowTop = upperRowTop + 70f;
                spans.Add((Math.Max(0f, systemTop - 24f), lowerRowTop + 62f));
                systemTop += 188f + extraSystemPadding;
            }

            return spans;
        }

        private void DrawGuitarPreview(CanvasDrawingSession ds, float canvasWidth, bool printMode, TabPreviewModel? preview)
        {
            Color ink = printMode ? Colors.Black : GetInkColor();
            Color subInk = printMode
                ? Color.FromArgb(255, 72, 72, 72)
                : Color.FromArgb((byte)(ink.A == 255 ? 190 : ink.A), ink.R, ink.G, ink.B);
            Color stringLine = printMode
                ? Color.FromArgb(255, 70, 70, 70)
                : (ActualTheme == ElementTheme.Dark ? Color.FromArgb(255, 190, 190, 190) : Color.FromArgb(255, 70, 70, 70));
            Color fretFill = printMode
                ? Color.FromArgb(255, 250, 250, 250)
                : (ActualTheme == ElementTheme.Dark ? Color.FromArgb(255, 28, 28, 28) : Color.FromArgb(255, 250, 250, 250));

            if (preview == null || preview.Systems.Count == 0)
            {
                float emptyCanvasWidth = Math.Max(200f, canvasWidth);
                float emptyCanvasHeight = 160f;
                ds.DrawText(Loc("当前工程没有可转换的吉他谱音符。", "No guitar tab notes in current project."), 0f, 0f, emptyCanvasWidth, emptyCanvasHeight, subInk, CreateCenteredStatusFormat());
                return;
            }

            float left = 30f;
            float stringsLeft = 86f;
            float systemTop = 214f;
            const float lineGap = 24f;
            const float measureGap = 18f;
            string[] labels = { "e", "B", "G", "D", "A", "E" };

            ds.DrawText(preview.Title, 0f, 62f, canvasWidth, 60f, ink, _titleFormat);
            ds.DrawText($"TAB   {preview.MeterText}   {preview.Bpm} BPM", stringsLeft, 154f, subInk, _metaFormat);

            foreach (TabSystem system in preview.Systems)
            {
                float stringsTop = systemTop + 20f;
                float stringsBottom = stringsTop + lineGap * (labels.Length - 1);

                ds.DrawText(system.StartMeasureNumber.ToString(), left, systemTop - 6f, ink, _measureNumberFormat);
                for (int stringIndex = 0; stringIndex < labels.Length; stringIndex++)
                {
                    float y = stringsTop + stringIndex * lineGap;
                    ds.DrawText(labels[stringIndex], left, y - 12f, 40f, 24f, subInk, _guitarStringFormat);
                }

                float measureX = stringsLeft;
                for (int measureIndex = 0; measureIndex < system.Measures.Count; measureIndex++)
                {
                    TabMeasure measure = system.Measures[measureIndex];
                    float innerLeft = measureX + 8f;
                    float innerRight = measureX + measure.Width - 8f;

                    for (int stringIndex = 0; stringIndex < labels.Length; stringIndex++)
                    {
                        float y = stringsTop + stringIndex * lineGap;
                        ds.DrawLine(measureX, y, measureX + measure.Width, y, stringLine, 1f);
                    }

                    if (measureIndex == 0)
                    {
                        DrawTabBarline(ds, measure.LeftBarText, measureX, stringsTop, stringsBottom, stringLine);
                    }
                    DrawTabBarline(ds, measure.RightBarText, measureX + measure.Width, stringsTop, stringsBottom, stringLine);

                    foreach (TabPlacement placement in measure.Placements)
                    {
                        float x = innerLeft + (innerRight - innerLeft) * (placement.TickInMeasure / (float)Math.Max(1, measure.MeasureTicks));
                        foreach (TabPosition position in placement.Positions)
                        {
                            string fretText = position.Fret.ToString();
                            float y = stringsTop + position.StringIndex * lineGap;
                            float textWidth = MeasureGlyphWidth(ds, fretText, _guitarFretFormat);
                            float maskWidth = Math.Max(16f, textWidth + 10f);
                            float maskHeight = 18f;
                            float maskLeft = x - maskWidth * 0.5f;
                            float maskTop = y - maskHeight * 0.5f;
                            ds.FillRectangle(maskLeft, maskTop, maskWidth, maskHeight, fretFill);
                            ds.DrawText(fretText, maskLeft, maskTop - 1f, maskWidth, maskHeight, ink, _guitarFretFormat);
                        }
                    }

                    measureX += measure.Width + measureGap;
                }

                systemTop += 214f;
            }

            if (!_previewLoaded)
            {
                ds.DrawText(Loc("正在准备吉他谱预览...", "Preparing guitar tab preview..."), left, systemTop + 8f, subInk, _statusFormat);
            }
        }

        private void DrawJianpuPreview(CanvasDrawingSession ds, float canvasWidth, bool printMode, JianpuConverter.NativePreviewModel? preview)
        {
            Color ink = printMode ? Colors.Black : GetInkColor();
            Color subInk = printMode
                ? Color.FromArgb(255, 72, 72, 72)
                : Color.FromArgb((byte)(ink.A == 255 ? 190 : ink.A), ink.R, ink.G, ink.B);
            Color barInk = printMode
                ? Colors.Black
                : (ActualTheme == ElementTheme.Dark ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(255, 0, 0, 0));
            Color measureInk = barInk;

            if (preview == null)
            {
                float emptyCanvasWidth = Math.Max(200f, canvasWidth);
                float emptyCanvasHeight = 160f;
                ds.DrawText(Loc("当前工程没有音符。", "No notes in current project."), 0f, 0f, emptyCanvasWidth, emptyCanvasHeight, subInk, CreateCenteredStatusFormat());
                return;
            }

            float left = 34f;
            float braceX = left + 14f;
            float rowStartX = left + 44f;

            ds.DrawText(preview.Title, 0f, 62f, canvasWidth, 60f, ink, _titleFormat);
            string meta = $"1={preview.KeyText}   {preview.MeterText}   {preview.Bpm} BPM";
            ds.DrawText(meta, rowStartX, 154f, subInk, _metaFormat);

            float systemTop = 226f;
            foreach (JianpuConverter.NativeSystem system in preview.Systems)
            {
                int systemStartMeasureIndex = Math.Max(0, system.StartMeasureNumber - 1);
                float upperExtraRise = EstimateUpperChordRise(system);
                float extraSystemPadding = Math.Max(0f, upperExtraRise - 10f);
                float upperRowTop = systemTop + 26f + extraSystemPadding;
                float lowerRowTop = upperRowTop + 70f;
                float braceTop = upperRowTop - 3f;
                float braceBottom = lowerRowTop + 31f;

                ds.DrawText(system.StartMeasureNumber.ToString(), braceX - 7f, upperRowTop - 11f, measureInk, _measureNumberFormat);
                DrawJianpuSystemBrace(ds, braceX + 9.4f, braceTop + 8.0f, braceBottom + 8.0f, measureInk);

                DrawStaffRow(ds, system, upperRowTop, isUpper: true, ink, subInk, barInk, rowStartX, canvasWidth);
                DrawStaffRow(ds, system, lowerRowTop, isUpper: false, ink, subInk, barInk, rowStartX, canvasWidth);
                DrawJianpuExpressionMarks(ds, system, systemStartMeasureIndex, upperRowTop, lowerRowTop, rowStartX, canvasWidth, barInk, subInk);

                systemTop += 188f + extraSystemPadding;
            }

            if (!_previewLoaded)
            {
                ds.DrawText(Loc("正在准备预览...", "Preparing preview..."), left, systemTop + 8f, subInk, _statusFormat);
            }
        }

        private CanvasTextFormat CreateCenteredStatusFormat()
        {
            return new CanvasTextFormat
            {
                FontFamily = _statusFormat.FontFamily,
                FontSize = _statusFormat.FontSize,
                FontWeight = _statusFormat.FontWeight,
                HorizontalAlignment = CanvasHorizontalAlignment.Center,
                VerticalAlignment = CanvasVerticalAlignment.Center
            };
        }

        private void DrawJianpuSystemBrace(CanvasDrawingSession ds, float x, float top, float bottom, Color color)
        {
            EnsureMusicBraceFont();

            if (_musicFontFace != null && _musicFontFace.HasCharacter((uint)SmuflBrace))
            {
                float height = Math.Max(40f, bottom - top);
                float centerY = (top + bottom) * 0.5f;
                float size = Math.Max(42f, height * 0.97f);
                float opticalUnit = size / 7.6f;
                float glyphX = x + opticalUnit * 0.42f;
                float baselineY = centerY + opticalUnit * 1.42f;
                DrawGlyphWithFace(ds, _musicFontFace, SmuflBrace, glyphX, baselineY, size, color);
                return;
            }

            DrawLegacyJianpuSystemBrace(ds, x, top, bottom, color);
        }

        private static void DrawLegacyJianpuSystemBrace(CanvasDrawingSession ds, float x, float top, float bottom, Color color)
        {
            float height = Math.Max(40f, bottom - top);
            float width = Math.Clamp(height * 0.095f, 7.2f, 10.2f);
            float mid = (top + bottom) * 0.5f;
            float lobe = height * 0.235f;
            float neck = Math.Max(5f, height * 0.058f);
            float thickness = Math.Clamp(width * 0.12f, 0.95f, 1.35f);
            float outerX = x + width;
            float innerX = x + width * 0.16f;
            float shoulderX = x + width * 0.58f;
            float cuspX = x + width * 0.04f;
            float neckInset = Math.Max(2.2f, width * 0.22f);
            float upperShoulderY = top + lobe;
            float lowerShoulderY = bottom - lobe;

            using var pathBuilder = new CanvasPathBuilder(ds.Device);
            pathBuilder.BeginFigure(outerX, top);
            pathBuilder.AddCubicBezier(
                new System.Numerics.Vector2(innerX, top + height * 0.015f),
                new System.Numerics.Vector2(innerX, upperShoulderY - neck * 1.2f),
                new System.Numerics.Vector2(shoulderX, mid - neckInset));
            pathBuilder.AddCubicBezier(
                new System.Numerics.Vector2(x + width * 0.84f, mid - neck * 0.92f),
                new System.Numerics.Vector2(x + width * 0.78f, mid - neck * 0.18f),
                new System.Numerics.Vector2(cuspX, mid));
            pathBuilder.AddCubicBezier(
                new System.Numerics.Vector2(x + width * 0.78f, mid + neck * 0.18f),
                new System.Numerics.Vector2(x + width * 0.84f, mid + neck * 0.92f),
                new System.Numerics.Vector2(shoulderX, mid + neckInset));
            pathBuilder.AddCubicBezier(
                new System.Numerics.Vector2(innerX, lowerShoulderY + neck * 1.2f),
                new System.Numerics.Vector2(innerX, bottom - height * 0.015f),
                new System.Numerics.Vector2(outerX, bottom));
            pathBuilder.EndFigure(CanvasFigureLoop.Open);

            using var geometry = CanvasGeometry.CreatePath(pathBuilder);
            ds.DrawGeometry(geometry, color, thickness);
        }

        private void EnsureMusicBraceFont()
        {
            if (_musicBraceFontAttempted)
            {
                return;
            }

            _musicBraceFontAttempted = true;
            if (TryCreateFontFaceFromUri(MusicFontFile, out var uriSet, out var uriFace))
            {
                _musicFontSet = uriSet;
                _musicFontFace = uriFace;
                return;
            }

            if (TryCreateFontFaceFromSystem(MusicFontFamily, out var systemSet, out var systemFace))
            {
                _musicFontSet = systemSet;
                _musicFontFace = systemFace;
            }
        }

        private void DisposeMusicBraceFont()
        {
            if (_musicFontSet is IDisposable disposable)
            {
                disposable.Dispose();
            }

            _musicFontSet = null;
            _musicFontFace = null;
            _musicBraceFontAttempted = false;
        }

        private static float GetGlyphAdvanceRaw(CanvasFontFace fontFace, int codePoint, float fontSize)
        {
            var indices = fontFace.GetGlyphIndices(new uint[] { (uint)codePoint });
            if (indices.Length == 0)
            {
                return fontSize * 0.6f;
            }

            int glyphIndex = indices[0];
            var metrics = fontFace.GetGlyphMetrics(new int[] { glyphIndex }, false);
            return metrics.Length > 0 ? metrics[0].AdvanceWidth : fontSize * 0.6f;
        }

        private static float DrawGlyphWithFace(
            CanvasDrawingSession ds,
            CanvasFontFace fontFace,
            int codePoint,
            float x,
            float baselineY,
            float fontSize,
            Color color)
        {
            var indices = fontFace.GetGlyphIndices(new uint[] { (uint)codePoint });
            if (indices.Length == 0)
            {
                return 0f;
            }

            int glyphIndex = indices[0];
            float advance = GetGlyphAdvanceRaw(fontFace, codePoint, fontSize);
            if (advance <= 0f)
            {
                advance = fontSize * 0.6f;
            }

            var glyphs = new CanvasGlyph[]
            {
                new CanvasGlyph
                {
                    Index = glyphIndex,
                    Advance = advance,
                    AdvanceOffset = 0f,
                    AscenderOffset = 0f
                }
            };

            using var brush = new Microsoft.Graphics.Canvas.Brushes.CanvasSolidColorBrush(ds, color);
            ds.DrawGlyphRun(new System.Numerics.Vector2(x, baselineY), fontFace, fontSize, glyphs, false, 0, brush);
            return advance;
        }

        private static bool TryCreateFontFaceFromUri(string fileName, out CanvasFontSet? fontSet, out CanvasFontFace? fontFace)
        {
            fontSet = null;
            fontFace = null;
            try
            {
                string relativePath = MusicFontRelativeFolder.Replace('\\', '/');
                var uri = new Uri($"ms-appx:///{relativePath}/{fileName}");
                fontSet = new CanvasFontSet(uri);
                fontFace = fontSet.Fonts.FirstOrDefault();
                return fontFace != null;
            }
            catch
            {
                fontSet = null;
                fontFace = null;
                return false;
            }
        }

        private static bool TryCreateFontFaceFromSystem(string familyName, out CanvasFontSet? fontSet, out CanvasFontFace? fontFace)
        {
            fontSet = null;
            fontFace = null;
            try
            {
                fontSet = CanvasFontSet.GetSystemFontSet();
                fontFace = FindSystemFontFace(fontSet, familyName);
                if (fontFace != null)
                {
                    return true;
                }

                fontSet = null;
                return false;
            }
            catch
            {
                fontSet = null;
                fontFace = null;
                return false;
            }
        }

        private static CanvasFontFace? FindSystemFontFace(CanvasFontSet systemSet, string familyName)
        {
            foreach (var face in systemSet.Fonts)
            {
                if (FamilyMatches(face, familyName))
                {
                    return face;
                }
            }

            return null;
        }

        private static bool FamilyMatches(CanvasFontFace face, string familyName)
        {
            foreach (var entry in face.FamilyNames)
            {
                if (string.Equals(entry.Value, familyName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private void DrawTabBarline(CanvasDrawingSession ds, string text, float x, float top, float bottom, Color color)
        {
            string safe = string.IsNullOrWhiteSpace(text) ? "|" : text;
            float thin = 1.15f;
            float thick = 2.2f;

            void DrawVertical(float cx, float thickness)
            {
                ds.DrawLine(cx, top, cx, bottom, color, thickness);
            }

            void DrawRepeatDots(float cx)
            {
                ds.FillCircle(cx, top + 34f, 1.45f, color);
                ds.FillCircle(cx, top + 58f, 1.45f, color);
            }

            switch (safe)
            {
                case "||":
                    DrawVertical(x - 2.1f, thin);
                    DrawVertical(x + 1.3f, thick);
                    break;
                case ":|":
                    DrawRepeatDots(x - 4.4f);
                    DrawVertical(x + 1.2f, thick);
                    break;
                case "|:":
                    DrawVertical(x - 1.2f, thick);
                    DrawRepeatDots(x + 4.4f);
                    break;
                default:
                    DrawVertical(x, thin);
                    break;
            }
        }

        private void DrawJianpuExpressionMarks(
            CanvasDrawingSession ds,
            JianpuConverter.NativeSystem system,
            int systemStartMeasureIndex,
            float upperRowTop,
            float lowerRowTop,
            float rowStartX,
            float canvasWidth,
            Color ink,
            Color subInk)
        {
            if (_sourceProject?.ExpressionMarks == null || system.Measures.Count == 0)
            {
                return;
            }

            int measureTicks = Math.Max(1, system.Measures[0].MeasureTicks);
            int beatTicks = Math.Max(1, system.Measures[0].BeatTicks);
            int systemStartTick = systemStartMeasureIndex * measureTicks;
            int systemEndTick = systemStartTick + system.Measures.Count * measureTicks;

            foreach (ExpressionMark mark in _sourceProject.ExpressionMarks.OrderBy(mark => mark.StartTick))
            {
                string code = ScorePreviewLayoutHelper.NormalizeExpressionCode(mark.Code);
                if (!code.StartsWith("score_", StringComparison.Ordinal))
                {
                    continue;
                }

                if (code is "score_repeat_barline" or "score_final_barline")
                {
                    continue;
                }

                int spanTicks = Math.Max(1, (int)Math.Round(Math.Max(0.2f, mark.SpanBeats) * beatTicks));
                if (mark.StartTick >= systemEndTick || mark.StartTick + spanTicks <= systemStartTick)
                {
                    continue;
                }

                if (code is "score_ending_1" or "score_ending_2")
                {
                    if (!TryResolveJianpuTickX(system, systemStartMeasureIndex, rowStartX, mark.StartTick, out float startX))
                    {
                        continue;
                    }

                    int endTick = mark.StartTick + spanTicks;
                    float endX = TryResolveJianpuTickX(system, systemStartMeasureIndex, rowStartX, endTick, out float resolvedEndX)
                        ? resolvedEndX
                        : GetJianpuSystemEndX(system, rowStartX);
                    endX = Math.Max(startX + 24f, endX);

                    float y = upperRowTop - 18f;
                    float hookY = y + 18f;
                    ds.DrawLine(startX, y, endX, y, ink, 1.05f);
                    ds.DrawLine(startX, y, startX, hookY, ink, 1.05f);
                    if (endTick <= systemEndTick)
                    {
                        ds.DrawLine(endX, y, endX, hookY, ink, 1.05f);
                    }

                    string label = code.EndsWith("_2", StringComparison.Ordinal) ? "2" : "1";
                    ds.DrawText(label, startX + 4f, y - 1f, ink, new CanvasTextFormat
                    {
                        FontFamily = "Times New Roman",
                        FontSize = 18f,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                    });
                    continue;
                }

                if (!TryResolveJianpuTickX(system, systemStartMeasureIndex, rowStartX, mark.StartTick, out float x))
                {
                    continue;
                }

                float drawY = mark.StaffStepOffset > 12f
                    ? lowerRowTop + 36f
                    : upperRowTop + 38f;

                if (code == "cresc")
                {
                    float width = Math.Max(24f, Math.Min(92f, spanTicks / (float)beatTicks * 26f));
                    ds.DrawLine(x, drawY + 10f, x + width, drawY + 4f, subInk, 1.05f);
                    ds.DrawLine(x, drawY + 10f, x + width, drawY + 16f, subInk, 1.05f);
                    continue;
                }

                string? textLabel = code switch
                {
                    "score_segno" => "segno",
                    _ => null
                };

                if (string.IsNullOrWhiteSpace(textLabel))
                {
                    continue;
                }

                ds.DrawText(textLabel, x - 2f, Math.Min(drawY, lowerRowTop + 52f), subInk, new CanvasTextFormat
                {
                    FontFamily = "Times New Roman",
                    FontSize = code is "rit" or "cresc_text" or "dim_text" ? 15.5f : 17f,
                    FontStyle = code is "rit" or "cresc_text" or "dim_text"
                        ? Windows.UI.Text.FontStyle.Italic
                        : Windows.UI.Text.FontStyle.Normal,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                });
            }
        }

        private bool TryResolveJianpuTickX(
            JianpuConverter.NativeSystem system,
            int systemStartMeasureIndex,
            float rowStartX,
            int tick,
            out float x)
        {
            x = 0f;
            if (system.Measures.Count == 0)
            {
                return false;
            }

            int measureTicks = Math.Max(1, system.Measures[0].MeasureTicks);
            int absoluteMeasureIndex = Math.Max(0, tick / measureTicks);
            int localMeasureIndex = absoluteMeasureIndex - systemStartMeasureIndex;
            if (localMeasureIndex < 0)
            {
                return false;
            }

            float cursor = rowStartX;
            if (!string.IsNullOrWhiteSpace(system.LeftKeyText))
            {
                cursor += 56f;
            }

            cursor += 22f;
            for (int i = 0; i < system.Measures.Count; i++)
            {
                JianpuConverter.NativeMeasure measure = system.Measures[i];
                if (localMeasureIndex == i)
                {
                    bool hasLeftBoundaryKey = i == 0
                        ? !string.IsNullOrWhiteSpace(system.LeftKeyText)
                        : !string.IsNullOrWhiteSpace(system.Measures[i - 1].RightKeyText);
                    bool hasRightBoundaryKey = !string.IsNullOrWhiteSpace(measure.RightKeyText);
                    float leftInset = hasLeftBoundaryKey ? 8f : 5f;
                    float rightInset = hasRightBoundaryKey ? 11f : 7f;
                    float innerX = cursor + leftInset;
                    float available = Math.Max(24f, measure.Width - leftInset - rightInset);
                    int safeBeatTicks = Math.Max(1, measure.BeatTicks);
                    int beatCount = Math.Max(1, (int)Math.Ceiling(measure.MeasureTicks / (double)safeBeatTicks));
                    float beatGap = 7.2f;
                    float timelineWidth = Math.Max(18f, available - Math.Max(0, beatCount - 1) * beatGap);
                    int localTick = Math.Clamp(tick - absoluteMeasureIndex * measureTicks, 0, measure.MeasureTicks);
                    int beatIndex = Math.Min(Math.Max(0, beatCount - 1), localTick / safeBeatTicks);
                    x = innerX + timelineWidth * (localTick / (float)Math.Max(1, measure.MeasureTicks)) + beatIndex * beatGap;
                    return true;
                }

                cursor += measure.Width;
                if (!string.IsNullOrWhiteSpace(measure.RightKeyText))
                {
                    cursor += 56f;
                }

                cursor += 22f;
            }

            if (localMeasureIndex == system.Measures.Count)
            {
                x = GetJianpuSystemEndX(system, rowStartX);
                return true;
            }

            return false;
        }

        private float GetJianpuSystemEndX(JianpuConverter.NativeSystem system, float rowStartX)
        {
            float cursor = rowStartX;
            if (!string.IsNullOrWhiteSpace(system.LeftKeyText))
            {
                cursor += 56f;
            }

            cursor += 22f;
            foreach (JianpuConverter.NativeMeasure measure in system.Measures)
            {
                cursor += measure.Width;
                if (!string.IsNullOrWhiteSpace(measure.RightKeyText))
                {
                    cursor += 56f;
                }

                cursor += 22f;
            }

            return cursor;
        }

        private static float EstimateUpperChordRise(JianpuConverter.NativeSystem system)
        {
            if (system?.Measures == null || system.Measures.Count == 0)
            {
                return 0f;
            }

            float maxRise = 0f;
            foreach (var measure in system.Measures)
            {
                var tokens = measure?.UpperTokens;
                if (tokens == null || tokens.Count == 0)
                {
                    continue;
                }

                foreach (var token in tokens)
                {
                    if (token?.ChordPitches == null || token.ChordPitches.Count <= 1)
                    {
                        continue;
                    }

                    int rowCount = token.ChordPitches.Count;
                    int maxTopDots = token.ChordPitches.Max(p => Math.Max(0, p.TopDots));
                    float rise = GetChordVerticalRise(rowCount, maxTopDots);
                    maxRise = Math.Max(maxRise, rise);
                }
            }

            return maxRise;
        }

        private void DrawStaffRow(
            Microsoft.Graphics.Canvas.CanvasDrawingSession ds,
            JianpuConverter.NativeSystem system,
            float rowTop,
            bool isUpper,
            Color ink,
            Color subInk,
            Color barInk,
            float rowStartX,
            float canvasWidth)
        {
            float x = rowStartX;
            const float keyWidth = 56f;
            const float barWidth = 22f;
            float rightEdge = Math.Max(rowStartX + 160f, canvasWidth - 24f);

            if (!string.IsNullOrWhiteSpace(system.LeftKeyText))
            {
                if (isUpper)
                {
                    ds.DrawText(system.LeftKeyText, x, rowTop + 2f, subInk, _metaFormat);
                }

                x += keyWidth;
            }

            DrawBarText(ds, system.LeftBarText, x, rowTop + 11f, barInk);
            x += barWidth;

            for (int measureIndex = 0; measureIndex < system.Measures.Count; measureIndex++)
            {
                var measure = system.Measures[measureIndex];
                var tokens = isUpper ? measure.UpperTokens : measure.LowerTokens;
                bool hasLeftBoundaryKey = measureIndex == 0
                    ? !string.IsNullOrWhiteSpace(system.LeftKeyText)
                    : !string.IsNullOrWhiteSpace(system.Measures[measureIndex - 1].RightKeyText);
                bool hasRightBoundaryKey = !string.IsNullOrWhiteSpace(measure.RightKeyText);

                DrawMeasureTokens(
                    ds,
                    tokens,
                    x,
                    rowTop,
                    measure.Width,
                    measure.MeasureTicks,
                    measure.BeatTicks,
                    ink,
                    hasLeftBoundaryKey,
                    hasRightBoundaryKey);
                x += measure.Width;

                if (!string.IsNullOrWhiteSpace(measure.RightKeyText))
                {
                    if (x + keyWidth + barWidth > rightEdge)
                    {
                        break;
                    }

                    if (isUpper)
                    {
                        ds.DrawText(measure.RightKeyText, x, rowTop + 2f, subInk, _metaFormat);
                    }

                    x += keyWidth;
                }
                else if (measureIndex < system.Measures.Count - 1 && x + barWidth > rightEdge)
                {
                    // Avoid rendering a clipped trailing barline at system end.
                    break;
                }

                DrawBarText(ds, measure.RightBarText, x, rowTop + 11f, barInk);
                x += barWidth;
            }
        }

        private static float AlignPixel(float value, float thickness)
        {
            float rounded = (float)Math.Round(value);
            return ((int)Math.Round(thickness)) % 2 == 1 ? rounded + 0.5f : rounded;
        }

        private void DrawBarText(Microsoft.Graphics.Canvas.CanvasDrawingSession ds, string text, float x, float y, Color color)
        {
            string safe = string.IsNullOrWhiteSpace(text) ? "|" : text;
            const float barWidth = 22f;
            float left = x;
            float right = x + barWidth;
            float center = (left + right) * 0.5f;
            float top = y + 1f;
            float bottom = y + 34f;
            float thin = 1.3f;
            float thick = 2.2f;

            void DrawVertical(float cx, float thickness)
            {
                float px = AlignPixel(cx, thickness);
                ds.DrawLine(px, top, px, bottom, color, thickness);
            }

            void DrawRepeatDots(float cx)
            {
                float dotX = AlignPixel(cx, 1f);
                ds.FillCircle(dotX, y + 12f, 1.4f, color);
                ds.FillCircle(dotX, y + 24f, 1.4f, color);
            }

            switch (safe)
            {
                case "||":
                    DrawVertical(center - 2.2f, thin);
                    DrawVertical(center + 2.2f, thick);
                    break;
                case "|:":
                    DrawVertical(center - 3.4f, thick);
                    DrawVertical(center + 0.2f, thin);
                    DrawRepeatDots(center + 5.2f);
                    break;
                case ":|":
                    DrawRepeatDots(center - 5.2f);
                    DrawVertical(center - 0.2f, thin);
                    DrawVertical(center + 3.4f, thick);
                    break;
                case ":|:":
                    DrawRepeatDots(center - 6.0f);
                    DrawVertical(center - 2.6f, thin);
                    DrawVertical(center + 0.2f, thick);
                    DrawVertical(center + 3.6f, thin);
                    DrawRepeatDots(center + 7.0f);
                    break;
                default:
                    DrawVertical(center, thin);
                    break;
            }
        }

        private void DrawMeasureTokens(
            Microsoft.Graphics.Canvas.CanvasDrawingSession ds,
            IReadOnlyList<JianpuConverter.NativeToken> tokens,
            float x,
            float rowTop,
            float measureWidth,
            int measureTicks,
            int beatTicks,
            Color ink,
            bool hasLeftBoundaryKey,
            bool hasRightBoundaryKey)
        {
            if (tokens == null || tokens.Count == 0)
            {
                return;
            }

            float leftInset = hasLeftBoundaryKey ? 8f : 5f;
            float rightInset = hasRightBoundaryKey ? 11f : 7f;
            float innerX = x + leftInset;
            float available = Math.Max(24f, measureWidth - leftInset - rightInset);
            float noteY = rowTop + 13.6f;
            float measureClipStart = innerX + 1f;
            float measureClipEnd = Math.Max(measureClipStart + 2f, x + measureWidth - rightInset - 1f);
            int safeMeasureTicks = Math.Max(1, measureTicks);
            int safeBeatTicks = Math.Max(1, beatTicks);
            int beatCount = Math.Max(1, (int)Math.Ceiling(safeMeasureTicks / (double)safeBeatTicks));
            float beatGap = 7.2f;
            float timelineWidth = Math.Max(18f, available - Math.Max(0, beatCount - 1) * beatGap);

            float ResolveTimelineX(int tickInMeasure)
            {
                int clamped = Math.Clamp(tickInMeasure, 0, safeMeasureTicks);
                int beatIndex = Math.Min(Math.Max(0, beatCount - 1), clamped / safeBeatTicks);
                return innerX
                    + timelineWidth * (clamped / (float)safeMeasureTicks)
                    + beatIndex * beatGap;
            }

            var orderedTokens = tokens
                .OrderBy(t => t.TickInMeasure)
                .ThenBy(t => t.DurationTicks)
                .ToList();

            var placements = new List<TokenPlacement>(orderedTokens.Count);
            float previousBodyRight = measureClipStart - 2f;
            float previousAccidentalRight = measureClipStart - 2f;
            float minNoteGap = 1.6f;
            for (int i = 0; i < orderedTokens.Count; i++)
            {
                var token = orderedTokens[i];
                float anchorX = ResolveTimelineX(token.TickInMeasure);
                float tokenWidth = ResolveTokenRenderWidth(token, safeBeatTicks);
                float xLeft = anchorX - tokenWidth * 0.5f;
                xLeft = Math.Clamp(xLeft, measureClipStart, measureClipEnd - 6f);
                if (xLeft < previousBodyRight + minNoteGap)
                {
                    xLeft = Math.Min(measureClipEnd - 6f, previousBodyRight + minNoteGap);
                }

                TokenDrawMetrics metrics = DrawTokenText(
                    ds,
                    token,
                    xLeft,
                    noteY,
                    tokenWidth,
                    ink,
                    measureClipStart,
                    measureClipEnd,
                    previousAccidentalRight);

                previousBodyRight = Math.Max(previousBodyRight, metrics.BodyRight);
                previousAccidentalRight = Math.Max(previousAccidentalRight, metrics.AccidentalRight);
                placements.Add(new TokenPlacement(token, xLeft, tokenWidth, metrics.CenterX));
            }

            const float dotSpacing = 3.15f;
            const float dotRadius = 1.2f;
            foreach (var p in placements)
            {
                float topBase = noteY - 0.15f;
                for (int d = 0; d < p.Token.TopDots; d++)
                {
                    ds.FillCircle(p.CenterX, topBase - d * dotSpacing, dotRadius, ink);
                }
            }

            int maxUnderline = placements.Count == 0 ? 0 : placements.Max(p => p.Token.UnderlineCount);
            Color lineColor = Color.FromArgb(255, 0, 0, 0);
            float lineBase = noteY + 22.4f;
            float lineStep = 2.1f;
            for (int level = 0; level < maxUnderline; level++)
            {
                int i = 0;
                while (i < placements.Count)
                {
                    if (placements[i].Token.UnderlineCount <= level)
                    {
                        i++;
                        continue;
                    }

                    float startX = placements[i].CenterX - 4.2f;
                    float endX = placements[i].CenterX + 4.2f;
                    int runBeat = placements[i].Token.TickInMeasure / safeBeatTicks;
                    i++;
                    while (i < placements.Count && placements[i].Token.UnderlineCount > level)
                    {
                        int nextBeat = placements[i].Token.TickInMeasure / safeBeatTicks;
                        if (nextBeat != runBeat)
                        {
                            break;
                        }

                        endX = placements[i].CenterX + 4.2f;
                        i++;
                    }

                    startX = Math.Clamp(startX, measureClipStart, measureClipEnd);
                    endX = Math.Clamp(endX, measureClipStart, measureClipEnd);
                    if (endX <= startX + 0.8f)
                    {
                        continue;
                    }

                    float y = lineBase + level * lineStep;
                    float py = AlignPixel(y, 1f);
                    ds.DrawLine(startX, py, endX, py, lineColor, 0.65f);
                }
            }

            // Draw lower octave dots after underlines so mixed cases stay clear.
            foreach (var p in placements)
            {
                float downBase = lineBase + p.Token.UnderlineCount * lineStep + 3.25f;
                for (int d = 0; d < p.Token.BottomDots; d++)
                {
                    ds.FillCircle(p.CenterX, downBase + d * dotSpacing, dotRadius, ink);
                }
            }
        }

        private TokenDrawMetrics DrawTokenText(
            Microsoft.Graphics.Canvas.CanvasDrawingSession ds,
            JianpuConverter.NativeToken token,
            float x,
            float y,
            float width,
            Color color,
            float clipStart,
            float clipEnd,
            float previousAccidentalRight)
        {
            if (token.IsChord)
            {
                return DrawChordTokenText(ds, token, x, y, width, color, clipStart, clipEnd);
            }

            string text = string.IsNullOrWhiteSpace(token.Text) ? "0" : token.Text;
            int splitIndex = 0;
            while (splitIndex < text.Length && IsAccidentalChar(text[splitIndex]))
            {
                splitIndex++;
            }

            string accidentalPrefix = splitIndex > 0 ? text[..splitIndex] : string.Empty;
            string bodyAndExtend = splitIndex < text.Length ? text[splitIndex..] : "0";
            if (string.IsNullOrWhiteSpace(bodyAndExtend))
            {
                bodyAndExtend = "0";
            }

            int extendCount = 0;
            while (extendCount < bodyAndExtend.Length && bodyAndExtend[^(extendCount + 1)] == '-')
            {
                extendCount++;
            }

            string bodyText = extendCount > 0 ? bodyAndExtend[..^extendCount] : bodyAndExtend;
            if (string.IsNullOrWhiteSpace(bodyText))
            {
                bodyText = "0";
            }

            float accidentalWidth = 0f;
            for (int i = 0; i < accidentalPrefix.Length; i++)
            {
                accidentalWidth += MeasureGlyphWidth(ds, accidentalPrefix[i].ToString(), _accidentalFormat);
            }

            float bodyWidth = MeasureGlyphWidth(ds, bodyText, _tokenFormat);
            float extendWidth = extendCount > 0 ? Math.Max(MeasureExtendDrawWidth(ds, _tokenFormat, extendCount), Math.Max(0f, width - bodyWidth - 6f)) : 0f;
            float totalWidth = bodyWidth + (extendWidth > 0f ? 2.2f + extendWidth : 0f);
            float bodyStart = x + Math.Max(0f, (width - totalWidth) * 0.5f);
            bodyStart = Math.Clamp(bodyStart, clipStart + 1f, Math.Max(clipStart + 1f, clipEnd - totalWidth - 1f));

            float accidentalRight = float.MinValue;
            if (accidentalWidth > 0f)
            {
                float accidentalGap = 0.55f;
                float minGapToBody = 0.25f;
                float accidentalStart = bodyStart - accidentalGap - accidentalWidth;
                float maxAllowedStart = bodyStart - minGapToBody - accidentalWidth;
                accidentalStart = Math.Min(accidentalStart, maxAllowedStart);
                if (previousAccidentalRight > clipStart)
                {
                    float minStartAfterPrevious = previousAccidentalRight + 0.55f;
                    accidentalStart = Math.Max(accidentalStart, minStartAfterPrevious);
                    accidentalStart = Math.Min(accidentalStart, maxAllowedStart);
                }

                accidentalStart = Math.Max(clipStart, accidentalStart);
                float cursor = accidentalStart;
                for (int i = 0; i < accidentalPrefix.Length; i++)
                {
                    string glyph = accidentalPrefix[i].ToString();
                    float w = MeasureGlyphWidth(ds, glyph, _accidentalFormat);
                    ds.DrawText(glyph, cursor, y - 4.9f, color, _accidentalFormat);
                    cursor += w;
                }

                accidentalRight = cursor;
            }

            ds.DrawText(bodyText, bodyStart, y, color, _tokenFormat);
            float centerX = bodyStart + bodyWidth * 0.5f;
            float bodyRight = bodyStart + bodyWidth;
            if (extendCount > 0)
            {
                DrawExtendSegments(ds, bodyRight + 2.2f, y, extendCount, extendWidth, color, _tokenFormat);
            }

            if (accidentalWidth <= 0f)
            {
                accidentalRight = float.MinValue;
            }

            return new TokenDrawMetrics(bodyStart, bodyStart + totalWidth, accidentalRight, centerX);
        }

        private static float MeasureExtendDrawWidth(Microsoft.Graphics.Canvas.CanvasDrawingSession ds, CanvasTextFormat format, int extendCount)
        {
            if (extendCount <= 0)
            {
                return 0f;
            }

            float dashWidth = MeasureGlyphWidth(ds, "-", format);
            float gap = Math.Max(6f, dashWidth * 1.25f);
            return extendCount * dashWidth + Math.Max(0, extendCount - 1) * gap;
        }

        private static void DrawExtendSegments(
            Microsoft.Graphics.Canvas.CanvasDrawingSession ds,
            float startX,
            float y,
            int extendCount,
            float regionWidth,
            Color color,
            CanvasTextFormat format)
        {
            if (extendCount <= 0)
            {
                return;
            }

            float dashWidth = MeasureGlyphWidth(ds, "-", format);
            float slotWidth = extendCount > 0
                ? Math.Max(dashWidth, regionWidth / extendCount)
                : dashWidth;
            float dashOffset = Math.Max(0f, (slotWidth - dashWidth) * 0.5f);
            for (int i = 0; i < extendCount; i++)
            {
                float cursor = startX + i * slotWidth + dashOffset;
                ds.DrawText("-", cursor, y, color, format);
            }
        }

        private static float GetChordFontScale(int rowCount)
        {
            return rowCount switch
            {
                <= 3 => 1f,
                4 => 0.92f,
                5 => 0.84f,
                _ => 0.76f
            };
        }

        private static float GetChordRowStep(int rowCount)
        {
            float scale = GetChordFontScale(rowCount);
            return Math.Max(13.8f, ChordRowStep * (0.84f + scale * 0.16f));
        }

        private static float GetChordVerticalRise(int rowCount, int maxTopDots)
        {
            float scale = GetChordFontScale(rowCount);
            float rowStep = GetChordRowStep(rowCount);
            float dotSpacing = Math.Max(3.1f, ChordDotSpacing * (0.82f + scale * 0.18f));
            return Math.Max(0, rowCount - 1) * rowStep
                + Math.Max(0, maxTopDots) * dotSpacing
                + 6.5f * (0.88f + scale * 0.12f);
        }

        private static CanvasTextFormat CreateScaledTextFormat(CanvasTextFormat source, float scale)
        {
            return new CanvasTextFormat
            {
                FontFamily = source.FontFamily,
                FontSize = Math.Max(8f, source.FontSize * scale),
                FontWeight = source.FontWeight,
                HorizontalAlignment = source.HorizontalAlignment,
                VerticalAlignment = source.VerticalAlignment
            };
        }

        private TokenDrawMetrics DrawChordTokenText(
            Microsoft.Graphics.Canvas.CanvasDrawingSession ds,
            JianpuConverter.NativeToken token,
            float x,
            float y,
            float width,
            Color color,
            float clipStart,
            float clipEnd)
        {
            if (token.ChordPitches == null || token.ChordPitches.Count == 0)
            {
                return new TokenDrawMetrics(x, x + width, float.MinValue, x + width * 0.5f);
            }

            var rows = token.ChordPitches;
            int rowCount = rows.Count;
            float fontScale = GetChordFontScale(rowCount);
            float rowStep = GetChordRowStep(rowCount);
            float dotSpacing = Math.Max(3.1f, ChordDotSpacing * (0.82f + fontScale * 0.18f));
            float dotRadius = Math.Max(0.94f, 1.12f * (0.84f + fontScale * 0.16f));
            float accidentalYOffset = -4.3f * (0.82f + fontScale * 0.18f);
            float bottomDotBaseOffset = Math.Max(15.6f, 22.6f * (0.76f + fontScale * 0.24f));
            var degreeFormat = CreateScaledTextFormat(_chordDegreeFormat, fontScale);
            var accidentalFormat = CreateScaledTextFormat(_chordAccidentalFormat, Math.Min(1f, fontScale + 0.02f));

            var accidentalWidths = new float[rowCount];
            var degreeWidths = new float[rowCount];
            float maxAccidentalWidth = 0f;
            float maxDegreeWidth = 0f;
            for (int i = 0; i < rowCount; i++)
            {
                float accW = 0f;
                if (!string.IsNullOrWhiteSpace(rows[i].Accidental))
                {
                    foreach (char ch in rows[i].Accidental)
                    {
                        accW += MeasureGlyphWidth(ds, ch.ToString(), accidentalFormat);
                    }
                }

                float degW = MeasureGlyphWidth(ds, string.IsNullOrWhiteSpace(rows[i].Degree) ? "0" : rows[i].Degree, degreeFormat);
                accidentalWidths[i] = accW;
                degreeWidths[i] = degW;
                maxAccidentalWidth = Math.Max(maxAccidentalWidth, accW);
                maxDegreeWidth = Math.Max(maxDegreeWidth, degW);
            }

            float stackWidth = maxAccidentalWidth + maxDegreeWidth;
            float extendWidth = token.ExtendCount > 0 ? Math.Max(MeasureExtendDrawWidth(ds, degreeFormat, token.ExtendCount), Math.Max(0f, width - stackWidth - 6f)) : 0f;
            float totalWidth = stackWidth + (extendWidth > 0f ? extendWidth + 2.2f : 0f);
            float bodyStart = x + Math.Max(0f, (width - totalWidth) * 0.5f);
            bodyStart = Math.Clamp(bodyStart, clipStart + 1f, Math.Max(clipStart + 1f, clipEnd - totalWidth - 1f));

            float degreeStart = bodyStart + maxAccidentalWidth;
            float topRowY = y - Math.Max(0, rowCount - 1) * rowStep + 8.6f;
            float maxAccidentalRight = float.MinValue;
            for (int i = 0; i < rowCount; i++)
            {
                var row = rows[i];
                float rowY = topRowY + i * rowStep;
                float cursor = degreeStart - accidentalWidths[i];

                if (!string.IsNullOrWhiteSpace(row.Accidental))
                {
                    foreach (char ch in row.Accidental)
                    {
                        string glyph = ch.ToString();
                        float w = MeasureGlyphWidth(ds, glyph, accidentalFormat);
                        ds.DrawText(glyph, cursor, rowY + accidentalYOffset, color, accidentalFormat);
                        cursor += w;
                    }

                    maxAccidentalRight = Math.Max(maxAccidentalRight, degreeStart);
                }

                string degreeText = string.IsNullOrWhiteSpace(row.Degree) ? "0" : row.Degree;
                float degreeX = degreeStart;
                ds.DrawText(degreeText, degreeX, rowY, color, degreeFormat);
                float degreeCenterX = degreeX + degreeWidths[i] * 0.5f;
                for (int d = 0; d < row.TopDots; d++)
                {
                    ds.FillCircle(degreeCenterX, rowY - 0.55f - d * dotSpacing, dotRadius, color);
                }

                for (int d = 0; d < row.BottomDots; d++)
                {
                    ds.FillCircle(degreeCenterX, rowY + bottomDotBaseOffset + d * dotSpacing, dotRadius, color);
                }
            }

            if (token.ExtendCount > 0)
            {
                DrawExtendSegments(ds, bodyStart + stackWidth + 2.2f, y, token.ExtendCount, extendWidth, color, degreeFormat);
            }

            if (maxAccidentalRight < -1e20f)
            {
                maxAccidentalRight = float.MinValue;
            }

            float bodyLeft = bodyStart;
            float bodyRight = bodyStart + totalWidth;
            float centerX = degreeStart + maxDegreeWidth * 0.5f;
            return new TokenDrawMetrics(bodyLeft, bodyRight, maxAccidentalRight, centerX);
        }

        private static float ResolveTokenRenderWidth(JianpuConverter.NativeToken token, int beatTicks)
        {
            int safeBeatTicks = Math.Max(1, beatTicks);
            float ratio = token.DurationTicks / (float)safeBeatTicks;
            float durationScale = ratio switch
            {
                <= 0.18f => 0.40f,
                <= 0.30f => 0.50f,
                <= 0.55f => 0.65f,
                <= 0.90f => 0.80f,
                <= 1.20f => 0.96f,
                <= 2.40f => 1.10f,
                _ => 1.24f
            };

            if (token.IsChord)
            {
                int rowCount = Math.Max(2, token.ChordPitches?.Count ?? 0);
                int maxGlyphCount = 1;
                if (token.ChordPitches != null && token.ChordPitches.Count > 0)
                {
                    maxGlyphCount = token.ChordPitches.Max(p =>
                        Math.Max(1, p.Degree?.Length ?? 0)
                        + (string.IsNullOrWhiteSpace(p.Accidental) ? 0 : p.Accidental.Length));
                }

                float chordWidth = 22f
                    + Math.Max(0, maxGlyphCount - 1) * 5.2f
                    + Math.Min(6, Math.Max(0, token.ExtendCount)) * 3.8f
                    + (rowCount >= 3 ? 1.4f : 0f);
                float rowScale = 0.88f + GetChordFontScale(rowCount) * 0.12f;
                float scaledChordWidth = chordWidth * MathF.Sqrt(Math.Max(0.35f, durationScale)) * rowScale;
                return Math.Clamp(scaledChordWidth, 14f, 56f);
            }

            float textWeight = MathF.Max(0f, (token.Text?.Length ?? 1) - 1) * 1.6f;
            float width = 32f * durationScale + textWeight;
            if (HasLeadingAccidental(token.Text))
            {
                width += 2.2f;
            }

            return Math.Clamp(width, 11f, 46f);
        }

        private readonly struct TokenDrawMetrics
        {
            public TokenDrawMetrics(float bodyLeft, float bodyRight, float accidentalRight, float centerX)
            {
                BodyLeft = bodyLeft;
                BodyRight = bodyRight;
                AccidentalRight = accidentalRight;
                CenterX = centerX;
            }

            public float BodyLeft { get; }
            public float BodyRight { get; }
            public float AccidentalRight { get; }
            public float CenterX { get; }
        }

        private readonly struct TokenPlacement
        {
            public TokenPlacement(JianpuConverter.NativeToken token, float x, float width, float centerX)
            {
                Token = token;
                X = x;
                Width = width;
                CenterX = centerX;
            }

            public JianpuConverter.NativeToken Token { get; }
            public float X { get; }
            public float Width { get; }
            public float CenterX { get; }
        }

        private static bool IsAccidentalChar(char ch)
        {
            return ch == '#' || ch == '\u266F' || ch == 'b' || ch == '\u266D' || ch == '\u266E';
        }

        private static bool HasLeadingAccidental(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            return IsAccidentalChar(text[0]);
        }

        private static float MeasureGlyphWidth(
            Microsoft.Graphics.Canvas.CanvasDrawingSession ds,
            string text,
            CanvasTextFormat format)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 1f;
            }

            using var layout = new CanvasTextLayout(ds.Device, text, format, 0f, 0f);
            float w = (float)Math.Max(layout.LayoutBounds.Width, layout.DrawBounds.Width);
            return Math.Max(1f, w);
        }

        private Color GetInkColor()
        {
            return ActualTheme == ElementTheme.Dark
                ? Color.FromArgb(255, 238, 238, 238)
                : Color.FromArgb(255, 20, 20, 20);
        }

        private async Task ExportSourceToMusicXmlAsync()
        {
            if (_sourceProject == null)
            {
                SetStatus(Loc("\u8bf7\u5148\u5bfc\u5165\u5de5\u7a0b\uff0c\u518d\u5bfc\u51fa MusicXML\u3002", "Import a score before exporting MusicXML."));
                return;
            }

            try
            {
                string suggested = GetSuggestedExportName("musicxml", "MusicXML");
                string? path = await PickSavePathAsync(".musicxml", "MusicXML \u4e50\u8c31", suggested);
                if (string.IsNullOrWhiteSpace(path))
                {
                    SetStatus(Loc("\u5df2\u53d6\u6d88\u5bfc\u51fa\u3002", "Export canceled."));
                    return;
                }

                _musicXmlExporter.Export(CloneProject(_sourceProject), path);
                SetStatus($"{Loc("\u5df2\u5bfc\u51fa MusicXML", "MusicXML exported")}: {path}");
            }
            catch (Exception ex)
            {
                SetStatus($"{Loc("\u5bfc\u51fa\u5931\u8d25", "Export failed")}: {ex.Message}");
            }
        }

        private async Task ExportGuitarTabAsync()
        {
            if (_sourceProject == null)
            {
                SetStatus(Loc("\u8bf7\u5148\u5bfc\u5165\u5de5\u7a0b\uff0c\u518d\u5bfc\u51fa\u5409\u4ed6\u8c31\u3002", "Import a score before exporting guitar tab."));
                return;
            }

            try
            {
                await RefreshPreviewAsync();
                if (string.IsNullOrWhiteSpace(_latestGuitarTabText))
                {
                    SetStatus(Loc("\u5409\u4ed6\u8c31\u9884\u89c8\u5c1a\u672a\u5c31\u7eea\u3002", "Guitar tab preview is not ready."));
                    return;
                }

                string suggested = GetSuggestedExportName("txt", "\u5409\u4ed6\u8c31");
                string? path = await PickSavePathAsync(".txt", "Guitar Tab Text", suggested);
                if (string.IsNullOrWhiteSpace(path))
                {
                    SetStatus(Loc("\u5df2\u53d6\u6d88\u5bfc\u51fa\u3002", "Export canceled."));
                    return;
                }

                await File.WriteAllTextAsync(path, _latestGuitarTabText);
                SetStatus($"{Loc("\u5df2\u5bfc\u51fa\u5409\u4ed6\u8c31", "Guitar tab exported")}: {path}");
            }
            catch (Exception ex)
            {
                SetStatus($"{Loc("\u5bfc\u51fa\u5931\u8d25", "Export failed")}: {ex.Message}");
            }
        }

        private async Task ExportPreviewToPdfAsync()
        {
            if (_viewModel == null)
            {
                SetStatus(Loc("\u672a\u627e\u5230\u5de5\u7a0b\u6570\u636e\u3002", "Project data not found."));
                return;
            }

            try
            {
                IReadOnlyList<RenderedPage> pages = await BuildRenderedPagesAsync();
                string suggested = GetSuggestedExportName("pdf", string.Equals(_activeFormat, "guitar", StringComparison.OrdinalIgnoreCase) ? "\u5409\u4ed6\u8c31" : "\u7b80\u8c31");
                string? path = await PickSavePathAsync(".pdf", "PDF \u6587\u6863", suggested);
                if (string.IsNullOrWhiteSpace(path))
                {
                    SetStatus(Loc("\u5df2\u53d6\u6d88\u5bfc\u51fa\u3002", "Export canceled."));
                    return;
                }

                _pdfExporter.ExportJpegPages(path, pages.Select(page => new RasterPdfPage(page.JpegBytes, page.PixelWidth, page.PixelHeight)).ToList());
                _viewModel.SetStatus($"{Loc("\u5df2\u5bfc\u51fa PDF", "PDF exported")}: {Path.GetFileName(path)}");
                SetStatus($"{Loc("\u5df2\u5bfc\u51fa", "Exported")}: {path}");
            }
            catch (Exception ex)
            {
                SetStatus($"{Loc("导出失败", "Export failed")}: {ex.Message}");
            }
        }

        private async Task PrintPreviewAsync()
        {
            if (_isPreparingPrintPreview)
            {
                return;
            }

            try
            {
                _isPreparingPrintPreview = true;
                EnsurePrintManager();
                await PreparePrintPagesAsync();
                IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                await PrintManagerInterop.ShowPrintUIForWindowAsync(hwnd);
            }
            catch (Exception ex)
            {
                SetStatus($"{Loc("\u6253\u5370\u5931\u8d25", "Print failed")}: {ex.Message}");
            }
            finally
            {
                _isPreparingPrintPreview = false;
            }
        }

        private async Task<string?> PickOpenPathAsync(params string[] extensions)
        {
            if (App.MainWindow == null)
            {
                return null;
            }

            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                ViewMode = PickerViewMode.List
            };

            foreach (string ext in extensions)
            {
                if (string.IsNullOrWhiteSpace(ext)) continue;
                picker.FileTypeFilter.Add(ext.StartsWith('.') ? ext : $".{ext}");
            }

            if (picker.FileTypeFilter.Count == 0)
            {
                picker.FileTypeFilter.Add("*");
            }

            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            StorageFile? file = await picker.PickSingleFileAsync();
            return file?.Path;
        }

        private async Task<string?> PickSavePathAsync(string extension, string fileTypeDescription, string suggestedFileName)
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

        private string GetSuggestedExportName(string extension, string suffix)
        {
            string title = _viewModel?.Title ?? string.Empty;
            if (string.IsNullOrWhiteSpace(title))
            {
                title = "Untitled";
            }

            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                title = title.Replace(invalid, '_');
            }

            string safeSuffix = string.IsNullOrWhiteSpace(suffix) ? "export" : suffix;
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                safeSuffix = safeSuffix.Replace(invalid, '_');
            }

            string normalizedExtension = extension.StartsWith('.') ? extension : $".{extension}";
            return $"{title}-{safeSuffix}{normalizedExtension}";
        }

        private void RestorePageStateFromCache()
        {
            if (_sourceProject != null || s_cache == null)
            {
                return;
            }

            _sourceProject = s_cache.SourceProject != null ? CloneProject(s_cache.SourceProject) : null;
            _latestGuitarTabText = s_cache.LatestGuitarTabText ?? string.Empty;
            _activeFormat = string.Equals(s_cache.ActiveFormat, "guitar", StringComparison.OrdinalIgnoreCase) ? "guitar" : "jianpu";
            _previewLoaded = false;
            _nativePreview = null;
            _guitarPreview = null;
            ApplyPreviewMode();
        }

        private void SavePageStateToCache()
        {
            s_cache = new ConvertPageStateCache
            {
                SourceProject = _sourceProject != null ? CloneProject(_sourceProject) : null,
                LatestGuitarTabText = ... [truncated]