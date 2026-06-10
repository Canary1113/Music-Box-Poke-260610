using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Printing;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Windows.Devices.Midi;
using Windows.Foundation;
using Windows.Graphics.Printing;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;
using Windows.UI.Text;
using MusicBox.Models;
using MusicBox.Services;
using MusicBox.ViewModels;

namespace MusicBox
{
    public sealed partial class EditorPage : Page
    {
        private sealed class NoteDrawInfo
        {
            public NoteEvent Note { get; set; } = null!;
            public float X { get; init; }
            public float Y { get; init; }
            public bool PreferTrebleStaff { get; init; }
            public int OttavaShiftOctaves { get; init; }
            public float HeadWidth { get; init; }
            public int VisualDurationTicks { get; init; }
            public int Beams { get; init; }
            public int DotCount { get; init; }
            public bool IsWhole { get; init; }
            public bool IsHalf { get; init; }
            public bool FillHead { get; init; }
            public bool StemUp { get; set; }
            public int MeasureIndex { get; init; }
            public bool IsBeamed { get; set; }
        }

        private sealed class BeamGroup
        {
            public List<NoteDrawInfo> Notes { get; set; } = new();
            public int Beams { get; init; }
            public bool StemUp { get; init; }
            public float BeamSlope { get; init; }
            public float BeamIntercept { get; init; }
        }

        private sealed class EditorHistoryState
        {
            public string ProjectJson { get; set; } = string.Empty;
            public int ManualAdditionalSystems { get; init; }
            public int ManualMeasureCount { get; init; }
            public Dictionary<int, float> BarlineOffsets { get; init; } = new();
            public List<int> SystemMeasureCounts { get; init; } = new();
        }

        private sealed class ClipboardNoteItem
        {
            public NoteEvent Note { get; init; } = new();
            public int StartOffset { get; init; }
        }

        private sealed class ClipboardExpressionItem
        {
            public ExpressionMark Mark { get; init; } = new();
            public int StartOffset { get; init; }
        }

        private sealed class EditorClipboardState
        {
            public List<ClipboardNoteItem> Notes { get; init; } = new();
            public List<ClipboardExpressionItem> Marks { get; init; } = new();
        }

        private readonly struct OrnamentHitTarget
        {
            public OrnamentHitTarget(Rect bounds, bool isGrace, float baseY)
            {
                Bounds = bounds;
                IsGrace = isGrace;
                BaseY = baseY;
            }

            public Rect Bounds { get; }
            public bool IsGrace { get; }
            public float BaseY { get; }
        }

        private readonly struct ClefHitTarget
        {
            public ClefHitTarget(int systemIndex, bool topStaff, Rect bounds, float anchorX, float anchorY)
            {
                SystemIndex = systemIndex;
                TopStaff = topStaff;
                Bounds = bounds;
                AnchorX = anchorX;
                AnchorY = anchorY;
            }

            public int SystemIndex { get; }
            public bool TopStaff { get; }
            public Rect Bounds { get; }
            public float AnchorX { get; }
            public float AnchorY { get; }
        }

        private MainViewModel? _viewModel;
        private readonly AppSettingsService _settings = AppSettingsService.Instance;

        private const float HitRadius = 14f;
        private const float BaseNoteHeadWidth = 9f;
        private const float BaseNoteHeadHeight = 6f;
        private const float NoteHeadScale = 1.9f;
        private const float TrebleClefYOffset = 0.0f;
        private const float BassClefYOffset = 0.0f;
        private const float KeySignatureYOffset = 0.75f;
        private const float KeySignatureSharpYOffsetAdjust = -0.75f;
        private const float KeySignatureFlatYOffsetAdjust = -0.75f;
        private const float KeySignatureBassFlatYOffsetAdjust = 0.0f;
        private const float KeySignatureAdvance = 0.82f;
        private const float ClefSpaceFactor = 2.6f;
        private const float KeyGapAfterClef = 1.368f;
        private const float MusicGapAfterKey = 0.45f;
        private const float SymbolSizeGap = 10.5f;
        private const float ClefVerticalStretch = 1.0f;
        private const float TimeSigGapAfterKey = 1.085f;
        private const float TimeSigVerticalOffset = 0.0f;
        private const int FallbackAutoMeasuresPerSystem = 6;
        private const float StaffMiddleGapFactor = 5.16f;
        private const float SystemSpacingFactor = 10.8f;
        private const float PrintCompactMeasureWidthScale = 0.84f;
        private const float PrintVerticalLayoutScale = 0.90f;
        private const float PrintSideMarginScale = 0.95f;
        private const double PrintPageSidePaddingScale = 0.44d;
        private const float MaxInteractiveCanvasExtent = 16000f;
        private const float MaxBeamSlopeAbs = 0.36f;
        private const float DefaultExpressionStaffStepOffset = 18f;
        private const float ExpressionHitPadding = 8f;
        private const float ExpressionScale = 1.5f;
        private const float ExpressionGlyphUniformSpacingFactor = 0.06f;
        private const float OrnamentLeftShiftByAdvanceFactor = 0.8f;
        private const float GraceOrnamentLeftShiftByAdvanceFactor = 0.35f;
        private const float OrnamentSharedOffsetXSpaces = -0.18f;
        private const float OrnamentDistanceFromNoteSpaces = 0.72f;
        private const float TrillExtraDistanceSpaces = 0.34f;
        private const float GraceOrnamentOffsetXSpaces = 0f;
        private const float GraceOrnamentOffsetYSpaces = 0.22f;
        private const float GraceOrnamentStemUpYOffsetSpaces = 0.28f;
        private const float GraceOrnamentStemDownYOffsetSpaces = 0.36f;
        private const float BravuraStaffLineThicknessSpaces = 0.13f;
        private const float BravuraThinBarlineThicknessSpaces = 0.16f;
        private const float BravuraThickBarlineThicknessSpaces = 0.5f;
        private const float BravuraLedgerLineThicknessSpaces = 0.13f;
        private const float BravuraLedgerLineExtensionSpaces = 0.4f;
        private readonly bool _hideCustomNotationFallback = true;
        private const float StaffInteriorPaddingFactor = 0.08f;
        private const int TrebleLowerSwitchDiatonic = 22; // D3: treble down to 4 ledger lines
        private const int BassUpperSwitchDiatonic = 34; // B4: bass up to 4 ledger lines
        private const int TrebleBottomDiatonic = 30; // E4
        private const int BassBottomDiatonic = 18; // G2
        private const float MinCanvasHeight = 3600f;
        private const string MusicFontRelativeFolder = "Assets\\Fonts";
        private const string ForcedMusicFontFamily = "Bravura";
        private const string ForcedMusicFontFile = "Bravura.otf";
        private const string MusicTextFontFile = "BravuraText.otf";
        private const string MusicTextFontFamily = "Bravura Text";

        private const int SmuflGClef = 0xE050;
        private const int SmuflFClef = 0xE062;
        private const int SmuflAccidentalFlat = 0xE260;
        private const int SmuflAccidentalSharp = 0xE262;
        private const int SmuflAccidentalDoubleSharp = 0xE263;
        private const int SmuflAccidentalDoubleFlat = 0xE264;
        private const int SmuflTimeSig0 = 0xE080;
        private const int SmuflFlag8thUp = 0xE240;
        private const int SmuflFlag8thDown = 0xE241;
        private const int SmuflFlag16thUp = 0xE242;
        private const int SmuflFlag16thDown = 0xE243;
        private const int SmuflFlag32thUp = 0xE244;
        private const int SmuflFlag32thDown = 0xE245;
        private const int SmuflNoteheadWhole = 0xE0A2;
        private const int SmuflNoteheadHalf = 0xE0A3;
        private const int SmuflNoteheadBlack = 0xE0A4;
        private const int SmuflAccidentalNatural = 0xE261;
        private const int SmuflDynamicP = 0xE520;
        private const int SmuflDynamicM = 0xE521;
        private const int SmuflDynamicF = 0xE522;
        private const int SmuflDynamicS = 0xE524;
        private const int SmuflDynamicPP = 0xE52B;
        private const int SmuflDynamicPPP = 0xE52A;
        private const int SmuflDynamicMP = 0xE52C;
        private const int SmuflDynamicMF = 0xE52D;
        private const int SmuflDynamicFF = 0xE52F;
        private const int SmuflDynamicFFF = 0xE530;
        private const int SmuflArticStaccato = 0xE4A2;
        private const int SmuflArticAccentAbove = 0xE4A0;
        private const int SmuflArticAccentBelow = 0xE4A1;
        private const int SmuflArticStaccatissimoAbove = 0xE4A6;
        private const int SmuflArticStaccatissimoBelow = 0xE4A7;
        private const int SmuflAugmentationDot = 0xE1E7;
        private const int SmuflOrnamentTrill = 0xE566;
        private const int SmuflOrnamentShortTrill = 0xE56C;
        private const int SmuflOrnamentMordent = 0xE56D;
        private const int SmuflOrnamentTurn = 0xE567;
        private const int SmuflOrnamentTurnInverted = 0xE568;
        private const int SmuflGraceNoteAcciaccaturaStemUp = 0xE560;
        private const int SmuflGraceNoteAcciaccaturaStemDown = 0xE561;
        private const int SmuflGraceNoteAppoggiaturaStemUp = 0xE562;
        private const int SmuflGraceNoteAppoggiaturaStemDown = 0xE563;
        private const int SmuflTremolo3 = 0xE222;
        private const int SmuflUnmeasuredTremoloSimple = 0xE22D;
        private const int SmuflBrace = 0xE000;
        private const int SmuflPedalMark = 0xE650;
        private const int SmuflStaff5LinesWide = 0xE01A;
        private const int SmuflLegerLine = 0xE022;
        private const int SmuflBarlineSingle = 0xE030;
        private const int SmuflBarlineFinal = 0xE032;
        private const int SmuflBarlineRepeatRight = 0xE041;
        private const int SmuflSegno = 0xE047;
        private const int SmuflNoteWhole = 0xE1D2;
        private const int SmuflNoteHalfUp = 0xE1D3;
        private const int SmuflNoteHalfDown = 0xE1D4;
        private const int SmuflNoteQuarterUp = 0xE1D5;
        private const int SmuflNoteQuarterDown = 0xE1D6;
        private const int SmuflNote8thUp = 0xE1D7;
        private const int SmuflNote8thDown = 0xE1D8;
        private const int SmuflNote16thUp = 0xE1D9;
        private const int SmuflNote16thDown = 0xE1DA;
        private const int SmuflNote32thUp = 0xE1DB;
        private const int SmuflNote32thDown = 0xE1DC;
        private const int SmuflRestWhole = 0xE4E3;
        private const int SmuflRestHalf = 0xE4E4;
        private const int SmuflRestQuarter = 0xE4E5;
        private const int SmuflRest8th = 0xE4E6;
        private const int SmuflRest16th = 0xE4E7;
        private const int SmuflRest32th = 0xE4E8;
        private const int SmuflPedalUpMark = 0xE655;
        private const int SmuflMetNoteWhole = 0xECA2;
        private const int SmuflMetNoteHalfUp = 0xECA3;
        private const int SmuflMetNoteQuarterUp = 0xECA5;
        private const int SmuflMetNote8thUp = 0xECA7;
        private const int SmuflMetNote16thUp = 0xECA9;
        private const int SmuflMetNote32thUp = 0xECAB;
        private const float InlineSignatureStartOffsetFactor = 0.56f;
        private const string ScoreMarkGClef = "score_gclef";
        private const string ScoreMarkFClef = "score_fclef";
        private const string ScoreMarkFinalBarline = "score_final_barline";
        private const string ScoreMarkRepeatBarline = "score_repeat_barline";
        private const string ScoreMarkSegno = "score_segno";
        private const string ScoreMarkEnding1 = "score_ending_1";
        private const string ScoreMarkEnding2 = "score_ending_2";
        private const float SlurMaxSlopeSteps = 20f;
        private const float SlurAutoSlopeClampSteps = 18f;
        private static readonly TimeSpan InsertionAnchorMaxAge = TimeSpan.FromSeconds(5);
        private static readonly NoteLength[] NoteLengthCycleOrder =
        {
            NoteLength.Whole,
            NoteLength.Half,
            NoteLength.Quarter,
            NoteLength.Eighth,
            NoteLength.Sixteenth,
            NoteLength.ThirtySecond
        };

        private float _staffLeft = 36f;
        private float _staffGap = 12f;
        private float _staffMiddleGapFactor = StaffMiddleGapFactor;
        private float _beatWidth = 60f;
        private float _measureWidth = 240f;
        private float _staffWidth = 600f;
        private float _staffContentWidth = 600f;
        private float _primaryBottomLineY = 0f;
        private float _primaryTrebleTop = 0f;
        private float _primaryTrebleBottom = 0f;
        private float _primaryBassTop = 0f;
        private float _primaryBassBottom = 0f;
        private float _musicStartX = 36f;
        private int _beatsPerBar = 4;
        private int _ticksPerBeat = 480;
        private int _measuresPerSystem = 1;
        private int _autoMeasuresPerSystem = FallbackAutoMeasuresPerSystem;
        private int _displayMeasuresPerSystemOverride;
        private int _systemCount = 1;
        private float _systemStride = 0f;
        private float _systemTopMargin = 0f;
        private float _clefSpace = 0f;
        private float _keySpace = 0f;
        private float _timeSignatureBlockWidth = 0f;
        private int _manualAdditionalSystems;
        private int _manualMeasureCount;
        private int _totalMeasureCount = 1;
        private readonly List<int> _systemMeasureCounts = new();
        private readonly List<int> _measureTickBoundaries = new();
        private bool _allowAutoMeasureRatioAdjust;
        private bool _musicFontAvailable;
        private bool _musicFontInitialized;
        private string _musicFontStatus = string.Empty;
        private bool _musicFontInstallPromptShown;
        private string _musicFontFamily = string.Empty;
        private string _musicFontUri = string.Empty;
        private CanvasFontSet? _musicFontSet;
        private CanvasFontFace? _musicFontFace;
        private CanvasFontSet? _musicTextFontSet;
        private CanvasFontFace? _musicTextFontFace;

        private bool _dragging;
        private bool _isSelectingRect;
        private Point _selectionStart;
        private Point _selectionEnd;
        private bool _pendingCanvasClickAction;
        private Point _pendingCanvasPressPoint;
        private bool _pendingCanvasPressShift;
        private bool _isDraggingSelectionGroup;
        private readonly Dictionary<NoteEvent, (int StartTick, int Midi, bool? PreferTrebleStaff)> _selectionGroupNoteBaseline = new();
        private readonly Dictionary<ExpressionMark, (int StartTick, float StaffStepOffset)> _selectionGroupMarkBaseline = new();
        private int _selectionGroupAnchorStartTick;
        private int _selectionGroupAnchorMidi;
        private bool _isTitleEditing;
        private Rect _titleHitRect;
        private NoteEvent? _activeNote;
        private NoteEvent? _beamLinkAnchorNote;
        private NoteEvent? _beamLinkDraggingNote;
        private float _beamLinkAnchorPointerY = float.NaN;
        private int _measurePanelMeasureIndex = -1;
        private int _measurePanelSystemIndex = -1;
        private int _measurePanelBoundaryLocalIndex = -1;
        private float _measurePanelBarlineX;
        private float _measurePanelTrebleTop;
        private readonly Dictionary<int, float> _barlineOffsets = new();
        private readonly Dictionary<int, float> _measureDemandFactorCache = new();
        private bool _isDraggingBarline;
        private int _dragBarlineSystemIndex = -1;
        private int _dragBarlineLocalIndex = -1;
        private int _dragBarlineMeasureIndex = -1;
        private float _dragBarlineTrebleTop;
        private float _dragBarlinePointerStartX;
        private bool _dragBarlineMoved;
        private int _highlightBarlineSystemIndex = -1;
        private int _highlightBarlineLocalIndex = -1;
        private ExpressionMark? _activeExpressionMark;
        private string? _pendingExpressionCode;
        private NoteAccidental _pendingAccidental = NoteAccidental.None;
        private bool _pendingStaccatissimo;
        private bool _pendingStaccato;
        private bool _pendingAccent;
        private bool _pendingAugmentationDot;
        private NoteOrnament _pendingOrnament = NoteOrnament.None;
        private bool _syncingNoteTypeControls;
        private bool _syncingDurationModeControls;
        private bool _isRestInputMode;
        private ExpressionDragMode _expressionDragMode = ExpressionDragMode.Move;
        private float _expressionDragOffsetX;
        private float _expressionDragOffsetY;
        private MenuFlyout? _contextMenu;
        private MenuFlyoutItem? _contextCopy;
        private MenuFlyoutItem? _contextCut;
        private MenuFlyoutItem? _contextPaste;
        private MenuFlyoutItem? _contextDelete;
        private float _pendingSlurSlopeSteps;
        private readonly MidiExporter _midiExporter = new();
        private MidiSynthesizer? _midiSynth;
        private DispatcherTimer? _playbackTimer;
        private readonly List<PlaybackEvent> _playbackEvents = new();
        private readonly List<PlaybackNoteSpan> _playbackNoteSpans = new();
        private readonly List<PlaybackCursorPoint> _playbackCursorPoints = new();
        private readonly Dictionary<int, int> _activePlaybackNotes = new();
        private int _playbackEventIndex;
        private int _playbackCurrentTick;
        private int _playbackTotalTicks;
        private double _playbackTicksPerSecond;
        private double _playbackVolume = 0.80d;
        private bool _isPlaybackRunning;
        private bool _isPlaybackPaused;
        private DateTimeOffset _playbackStartTimeUtc;
        private readonly string _playbackMidiPath = Path.Combine(Path.GetTempPath(), "musicbox-preview.mid");
        private bool _isDraggingPlaybackSlider;
        private bool _resumePlaybackAfterSliderDrag;
        private bool _syncingPlaybackSlider;
        private bool _playbackSliderPointerHandlersRegistered;
        private bool _isPlaybackOverlayPointerOver;
        private bool _isPlaybackOverlayExpanded;
        private bool _playbackOverlayScaleInitialized;
        private Storyboard? _playbackOverlayScaleStoryboard;
        private readonly DispatcherTimer _playbackOverlayCollapseTimer;
        private static bool _hasPersistedEditorViewState;
        private static double _persistedHorizontalOffset;
        private static double _persistedVerticalOffset;
        private static int _persistedPlaybackTick;
        private static bool _persistedPlaybackCanResume;
        private bool _isPreparingPrintPreview;
        private bool _forcePrintInkOnWhite;
        private readonly RasterPdfExportService _pdfExporter = new();
        private readonly AudioExportService _audioExporter = new();
        private PrintManager? _printManager;
        private PrintDocument? _printDocument;
        private IPrintDocumentSource? _printDocumentSource;
        private readonly List<UIElement> _printPages = new();
        private readonly List<UIElement> _pendingPrintPages = new();
        private readonly List<RasterPdfPage> _pendingPdfPages = new();
        private readonly List<EditorHistoryState> _historyStates = new();
        private int _historyIndex = -1;
        private bool _isApplyingHistory;
        private bool _pendingHistoryCommitFromDrag;
        private EditorClipboardState? _clipboard;
        private Point _lastPointerCanvasPoint = new(-1, -1);
        private bool _hasPendingInsertionAnchor;
        private Point _pendingInsertionAnchorPoint = new(-1, -1);
        private DateTimeOffset _pendingInsertionAnchorTimestampUtc = DateTimeOffset.MinValue;
        private bool _pendingSlurGesture;
        private bool _pendingSlurGestureRectMode;
        private Point _pendingSlurGestureStart;
        private readonly Dictionary<NoteEvent, OrnamentHitTarget> _ornamentHitTargets = new();
        private NoteEvent? _activeOrnamentNote;
        private bool _activeOrnamentIsGrace;
        private float _activeOrnamentBaseY;
        private float _ornamentDragStartPointerX;
        private float _ornamentDragStartPointerY;
        private float _ornamentDragStartOffsetX;
        private float _ornamentDragStartOffsetY;
        private readonly List<ClefHitTarget> _clefHitTargets = new();
        private int _clefPanelSystemIndex = -1;
        private bool _clefPanelTopStaff;
        private float _clefPanelAnchorX;
        private float _clefPanelAnchorY;

        private readonly struct PlaybackEvent
        {
            public PlaybackEvent(int tick, int order, int midi, bool isOn, int velocity = 100)
            {
                Tick = tick;
                Order = order;
                Midi = midi;
                IsOn = isOn;
                Velocity = velocity;
            }

            public int Tick { get; }
            public int Order { get; }
            public int Midi { get; }
            public bool IsOn { get; }
            public int Velocity { get; }
        }

        private readonly struct PlaybackNoteSpan
        {
            public PlaybackNoteSpan(int startTick, int endTick, int midi, int velocity = 100)
            {
                StartTick = startTick;
                EndTick = endTick;
                Midi = midi;
                Velocity = velocity;
            }

            public int StartTick { get; }
            public int EndTick { get; }
            public int Midi { get; }
            public int Velocity { get; }
        }

        private readonly struct PlaybackCursorPoint
        {
            public PlaybackCursorPoint(int playbackTick, int sourceTick)
            {
                PlaybackTick = playbackTick;
                SourceTick = sourceTick;
            }

            public int PlaybackTick { get; }
            public int SourceTick { get; }
        }

        private readonly struct PlaybackRepeatSegment
        {
            public PlaybackRepeatSegment(int startTick, int endTick)
            {
                StartTick = Math.Max(0, startTick);
                EndTick = Math.Max(StartTick + 1, endTick);
            }

            public int StartTick { get; }
            public int EndTick { get; }
            public int DurationTicks => Math.Max(1, EndTick - StartTick);
        }

        private readonly struct PlaybackPedalRange
        {
            public PlaybackPedalRange(int startTick, int endTick)
            {
                StartTick = Math.Max(0, startTick);
                EndTick = Math.Max(StartTick + 1, endTick);
            }

            public int StartTick { get; }
            public int EndTick { get; }
        }

        private enum ExpressionDragMode
        {
            Move,
            ResizeSpan,
            ResizeHeight,
            ResizeSlope
        }

        private enum StaffClefType
        {
            Treble,
            Bass
        }

        private readonly CanvasTextFormat _clefFormat = new()
        {
            FontSize = 48
        };

        private readonly CanvasTextFormat _keyFormat = new()
        {
            FontSize = 22
        };

        private readonly CanvasTextFormat _timeSigFormat = new()
        {
            FontSize = 22
        };

        private readonly CanvasTextFormat _expressionTextFormat = new()
        {
            FontFamily = "Times New Roman",
            FontSize = 20
        };

        private readonly CanvasTextFormat _titleFormat = new()
        {
            FontFamily = "Times New Roman",
            FontSize = 26,
            HorizontalAlignment = CanvasHorizontalAlignment.Center
        };

        private readonly CanvasTextFormat _tempoFormat = new()
        {
            FontFamily = "Times New Roman",
            FontSize = 16
        };

        private readonly CanvasTextFormat _measureNumberFormat = new()
        {
            FontFamily = "Times New Roman",
            FontSize = 14
        };

        private static readonly JsonSerializerOptions HistoryJsonOptions = new()
        {
            WriteIndented = false
        };

        private float GetNoteHeadWidth() => BaseNoteHeadWidth * NoteHeadScale;
        private float GetNoteHeadHeight() => BaseNoteHeadHeight * NoteHeadScale;

        public EditorPage()
        {
            NavigationCacheMode = NavigationCacheMode.Required;
            InitializeComponent();
            Loaded += EditorPage_Loaded;
            Unloaded += EditorPage_Unloaded;
            ActualThemeChanged += EditorPage_ActualThemeChanged;
            if (Resources.TryGetValue("ToolbarShadow", out var shadowResource) &&
                shadowResource is ThemeShadow themeShadow)
            {
                themeShadow.Receivers.Add(ToolbarShadowReceiver);
            }
            _playbackTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _playbackTimer.Tick += PlaybackTimer_Tick;
            _playbackOverlayCollapseTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _playbackOverlayCollapseTimer.Tick += PlaybackOverlayCollapseTimer_Tick;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is MainViewModel vm)
            {
                AttachViewModel(vm);
            }

            if (_viewModel != null && _viewModel.SnapDivision <= 0)
            {
                _viewModel.SnapDivision = 8;
            }
            _manualAdditionalSystems = 0;
            _manualMeasureCount = 0;
            _autoMeasuresPerSystem = GetDefaultMeasuresPerSystemForTimeSignature(_viewModel?.TimeSigNumerator ?? 4, _viewModel?.TimeSigDenominator ?? 4);
            _barlineOffsets.Clear();
            _systemMeasureCounts.Clear();
            if (AccidentalSharpItem != null) AccidentalSharpItem.IsChecked = false;
            if (AccidentalDoubleSharpItem != null) AccidentalDoubleSharpItem.IsChecked = false;
            if (AccidentalFlatItem != null) AccidentalFlatItem.IsChecked = false;
            if (AccidentalDoubleFlatItem != null) AccidentalDoubleFlatItem.IsChecked = false;
            if (AccidentalNaturalItem != null) AccidentalNaturalItem.IsChecked = false;
            if (StaccatissimoMenuItem != null) StaccatissimoMenuItem.IsChecked = false;
            if (StaccatoMenuItem != null) StaccatoMenuItem.IsChecked = false;
            if (AccentMenuItem != null) AccentMenuItem.IsChecked = false;
            if (AugmentationDotMenuItem != null) AugmentationDotMenuItem.IsChecked = false;
            if (OrnamentNoneMenuItem != null) OrnamentNoneMenuItem.IsChecked = true;
            HideTitleInlineEditor(commitChanges: false);
            HideClefEditPanel();
            SyncPendingNoteTypeFromControls();
            SetTimeSignatureSelection(_viewModel?.TimeSigNumerator ?? 4, _viewModel?.TimeSigDenominator ?? 4);
            SetKeySignatureSelection(_viewModel?.KeySignatureFifths ?? 0);
            SetTempoSelection(_viewModel?.Bpm ?? 0);
            SetSnapSelection(_viewModel?.SnapDivision ?? 8);
            if (_viewModel != null)
            {
                _viewModel.SelectedNoteLength = NoteLength.None;
            }
            EnsureDefaultNoteLengthSelection();
            SetDurationInputModeSelection(_isRestInputMode);
            UpdateNoteTypeMenuEnabledState();
            ApplyLocalizedEditorText();
            ResetHistoryState();
            UpdatePlaybackProgressBar();
            try
            {
                EnsureMusicFontSelectionInitialized();
            }
            catch (Exception ex)
            {
                _musicFontAvailable = false;
                _musicFontStatus = $"Music font init failed: {ex.Message}";
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            HideTitleInlineEditor(commitChanges: false);
            HideClefEditPanel();
            if (ScoreScrollViewer != null)
            {
                _persistedHorizontalOffset = ScoreScrollViewer.HorizontalOffset;
                _persistedVerticalOffset = ScoreScrollViewer.VerticalOffset;
                _hasPersistedEditorViewState = true;
            }
            _persistedPlaybackTick = GetCurrentPlaybackTickSnapshot();
            _persistedPlaybackCanResume = _isPlaybackRunning || _isPlaybackPaused;
            PausePlaybackInternal();
            UnregisterPrintManager();
            DetachViewModel();
        }

        private void EditorPage_Loaded(object sender, RoutedEventArgs e)
        {
            LocalizationService.LanguageChanged += LocalizationService_LanguageChanged;
            if (!_playbackSliderPointerHandlersRegistered && PlaybackProgressSlider != null)
            {
                PlaybackProgressSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(PlaybackProgressSlider_PointerPressed), true);
                PlaybackProgressSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(PlaybackProgressSlider_PointerReleased), true);
                PlaybackProgressSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(PlaybackProgressSlider_PointerCaptureLost), true);
                _playbackSliderPointerHandlersRegistered = true;
            }
            ApplyLocalizedEditorText();
            UpdatePlaybackOverlayTheme();
            UpdateTopToolbarLayout();
            _isPlaybackOverlayExpanded = true;
            UpdatePlaybackOverlayLayout();
            UpdatePlaybackOverlayScale(expanded: true, animate: false);
            if (_hasPersistedEditorViewState && ScoreScrollViewer != null)
            {
                _ = DispatcherQueue.TryEnqueue(() =>
                {
                    ScoreScrollViewer.ChangeView(_persistedHorizontalOffset, _persistedVerticalOffset, null, true);
                });
            }
            if (_persistedPlaybackCanResume || _persistedPlaybackTick > 0)
            {
                if (_playbackTotalTicks <= 0 && _viewModel?.Project != null)
                {
                    BuildPlaybackEvents();
                }

                _playbackCurrentTick = Math.Clamp(_persistedPlaybackTick, 0, Math.Max(0, _playbackTotalTicks));
                _isPlaybackPaused = _persistedPlaybackCanResume && _playbackTotalTicks > 0;
                UpdatePlaybackProgressBar();
            }
            StartPlaybackOverlayCollapseDelay();
        }

        private void EditorPage_Unloaded(object sender, RoutedEventArgs e)
        {
            LocalizationService.LanguageChanged -= LocalizationService_LanguageChanged;
            if (_playbackSliderPointerHandlersRegistered && PlaybackProgressSlider != null)
            {
                PlaybackProgressSlider.RemoveHandler(UIElement.PointerPressedEvent, new PointerEventHandler(PlaybackProgressSlider_PointerPressed));
                PlaybackProgressSlider.RemoveHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(PlaybackProgressSlider_PointerReleased));
                PlaybackProgressSlider.RemoveHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(PlaybackProgressSlider_PointerCaptureLost));
                _playbackSliderPointerHandlersRegistered = false;
            }
            _playbackOverlayCollapseTimer.Stop();
            DisablePlaybackRefractionEffect();
        }

        private void EditorPage_ActualThemeChanged(FrameworkElement sender, object args)
        {
            UpdatePlaybackOverlayTheme();
            StaffCanvas?.Invalidate();
        }

        private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateTopToolbarLayout();
            UpdatePlaybackOverlayLayout();
        }

        private void ScoreScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateTopToolbarLayout();
            UpdatePlaybackOverlayLayout();
            StaffCanvas?.Invalidate();
        }

        private void UpdateTopToolbarLayout()
        {
            if (TopToolbarOverlay == null)
            {
                return;
            }

            double viewportWidth = ScoreScrollViewer?.ActualWidth > 1d
                ? ScoreScrollViewer.ActualWidth
                : (RootGrid?.ActualWidth ?? 0d);
            if (viewportWidth <= 1d)
            {
                return;
            }

            double targetMaxWidth = Math.Clamp(viewportWidth * 0.90d, 520d, 2200d);
            targetMaxWidth = Math.Min(targetMaxWidth, Math.Max(420d, viewportWidth - 24d));
            TopToolbarOverlay.Width = double.NaN;
            TopToolbarOverlay.MaxWidth = targetMaxWidth;
        }

        private void LocalizationService_LanguageChanged(object? sender, EventArgs e)
        {
            ApplyLocalizedEditorText();
            StaffCanvas?.Invalidate();
        }

        private void AttachViewModel(MainViewModel vm)
        {
            if (_viewModel != null)
            {
                _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            }

            _viewModel = vm;
            DataContext = vm;
            _viewModel.Project.Notes ??= new List<NoteEvent>();
            _viewModel.Project.ExpressionMarks ??= new List<ExpressionMark>();
            _viewModel.Project.TimeSignatureChanges ??= new List<TimeSignatureChange>();
            _viewModel.Project.KeySignatureChanges ??= new List<KeySignatureChange>();
            _viewModel.Project.StaffClefs ??= new Dictionary<string, string>();
            _viewModel.Project.LayoutSystemMeasureCounts ??= new List<int>();
            _viewModel.Project.LayoutBarlineOffsets ??= new Dictionary<int, float>();
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            RestoreLayoutFromProject();
        }

        private void DetachViewModel()
        {
            if (_viewModel == null) return;
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        }

        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.KeySignatureFifths))
            {
                SetKeySignatureSelection(_viewModel?.KeySignatureFifths ?? 0);
            }
            else if (e.PropertyName == nameof(MainViewModel.TimeSigNumerator)
                || e.PropertyName == nameof(MainViewModel.TimeSigDenominator))
            {
                SetTimeSignatureSelection(_viewModel?.TimeSigNumerator ?? 4, _viewModel?.TimeSigDenominator ?? 4);
            }
            else if (e.PropertyName == nameof(MainViewModel.Bpm))
            {
                SetTempoSelection(_viewModel?.Bpm ?? 0);
            }
            else if (e.PropertyName == nameof(MainViewModel.SnapDivision))
            {
                SetSnapSelection(_viewModel?.SnapDivision ?? 8);
            }
            else if (e.PropertyName == nameof(MainViewModel.SelectedNoteLength))
            {
                EnsureDefaultNoteLengthSelection();
            }

            if (!_isApplyingHistory
                && (e.PropertyName == nameof(MainViewModel.Title)
                    || e.PropertyName == nameof(MainViewModel.Bpm)
                    || e.PropertyName == nameof(MainViewModel.KeySignatureFifths)
                    || e.PropertyName == nameof(MainViewModel.TimeSigNumerator)
                    || e.PropertyName == nameof(MainViewModel.TimeSigDenominator)))
            {
                PushHistorySnapshot();
            }

            StaffCanvas.Invalidate();
        }

        private void StaffCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            if (_viewModel == null) return;

            var ds = args.DrawingSession;
            Color paperColor = GetScorePaperColor();
            ds.Clear(paperColor);
            try
            {

            var width = (float)sender.ActualWidth;
            var height = (float)sender.ActualHeight;
            DrawScoreToSession(ds, width, height, _isSelectingRect);
            }
            catch (Exception ex)
            {
                _musicFontAvailable = false;
                _musicFontStatus = $"Render error: {ex.Message}";
                DrawMissingFontNotice(ds, (float)sender.ActualWidth);
            }

            UpdateNoteStepPanel();
        }

        private void DrawScoreToSession(
            Microsoft.Graphics.Canvas.CanvasDrawingSession ds,
            float width,
            float height,
            bool drawSelectionOverlay,
            float? layoutWidthOverride = null,
            bool? suppressSelectionVisualsOverride = null,
            bool? suppressBeatGridOverride = null,
            bool compactForPrintLayout = false)
        {
            if (_viewModel == null) return;

            _staffGap = 12f;
            float sizeGap = SymbolSizeGap;
            _beatWidth = GetBeatWidth();
            float layoutWidth = layoutWidthOverride.HasValue && layoutWidthOverride.Value > 1f
                ? layoutWidthOverride.Value
                : (float)(ScoreScrollViewer?.ActualWidth > 1d ? ScoreScrollViewer.ActualWidth : width);
            bool suppressSelectionVisuals = suppressSelectionVisualsOverride ?? false;
            bool suppressBeatGrid = suppressBeatGridOverride ?? false;
            _measureDemandFactorCache.Clear();
            float minSidePadding = compactForPrintLayout ? 12f : 25f;
            float maxSidePadding = compactForPrintLayout ? 20f : 40f;
            const float paddingWidthMin = 980f;
            const float paddingWidthMax = 2100f;
            float widthRatio = Math.Clamp((layoutWidth - paddingWidthMin) / Math.Max(1f, paddingWidthMax - paddingWidthMin), 0f, 1f);
            float sidePadding = minSidePadding + (maxSidePadding - minSidePadding) * widthRatio;
            float rawWidth = Math.Max(0, layoutWidth - sidePadding * 2f);

            int beatsPerBar = Math.Max(1, _viewModel.TimeSigNumerator);
            _beatsPerBar = beatsPerBar;
            _ticksPerBeat = GetTicksPerBeat();
            int fifths = _viewModel.Project.KeySignature.Fifths;
            int maxKeyAbs = Math.Abs(fifths);
            if (_viewModel.Project.KeySignatureChanges != null && _viewModel.Project.KeySignatureChanges.Count > 0)
            {
                int changeMax = _viewModel.Project.KeySignatureChanges
                    .Where(c => c != null)
                    .Select(c => Math.Abs(Math.Clamp(c.Fifths, -7, 7)))
                    .DefaultIfEmpty(0)
                    .Max();
                maxKeyAbs = Math.Max(maxKeyAbs, changeMax);
            }

            int keyCount = Math.Min(maxKeyAbs, 7);
            float keyAdvance = sizeGap * KeySignatureAdvance;
            _clefSpace = sizeGap * ClefSpaceFactor;
            _keySpace = keyCount > 0 ? keyCount * keyAdvance + sizeGap * 0.6f : 0f;
            _clefFormat.FontSize = sizeGap * 4.2f;
            _keyFormat.FontSize = sizeGap * 2.2f * 1.25f;
            _timeSigFormat.FontSize = sizeGap * 3.6f;
            int timeDigits = Math.Max(_viewModel.TimeSigNumerator.ToString().Length, _viewModel.TimeSigDenominator.ToString().Length);
            float timeSpace = Math.Max(sizeGap * (1.1f * timeDigits + 0.4f), _timeSigFormat.FontSize * 0.9f);
            _timeSignatureBlockWidth = sizeGap * TimeSigGapAfterKey + timeSpace;
            float reservedLeft = _clefSpace + _keySpace + sizeGap * TimeSigGapAfterKey + timeSpace + sizeGap * MusicGapAfterKey;

            float baseMeasureWidth = beatsPerBar * _beatWidth;
            float musicWidth = Math.Max(0, rawWidth - reservedLeft);
            int autoMeasuresPerSystem = GetDefaultMeasuresPerSystemForTimeSignature(_viewModel.TimeSigNumerator, _viewModel.TimeSigDenominator);
            int previousAutoMeasuresPerSystem = _autoMeasuresPerSystem;
            _autoMeasuresPerSystem = autoMeasuresPerSystem;
            if (_displayMeasuresPerSystemOverride <= 0 && previousAutoMeasuresPerSystem != autoMeasuresPerSystem)
            {
                _systemMeasureCounts.Clear();
                _barlineOffsets.Clear();
            }

            int targetMeasuresPerSystem = _displayMeasuresPerSystemOverride > 0
                ? Math.Max(1, _displayMeasuresPerSystemOverride)
                : Math.Max(1, autoMeasuresPerSystem);

            float widthScale = compactForPrintLayout ? PrintCompactMeasureWidthScale : 0.965f;
            float targetMusicWidth = Math.Max(1f, musicWidth * widthScale);
            float targetMeasureWidthByWindow = targetMusicWidth / Math.Max(1, targetMeasuresPerSystem);
            float minMeasureWidth = Math.Max(_staffGap * 1.7f, baseMeasureWidth * 0.35f);
            float measureWidth = Math.Max(minMeasureWidth, targetMeasureWidthByWindow);
            _measureWidth = measureWidth;
            _measuresPerSystem = Math.Max(1, targetMeasuresPerSystem);
            _staffContentWidth = _measuresPerSystem * measureWidth;
            float contentWidth = reservedLeft + _staffContentWidth;
            _staffWidth = Math.Min(rawWidth, contentWidth);
            _staffLeft = Math.Max(6f, (layoutWidth - _staffWidth) / 2f);
            _musicStartX = _staffLeft + reservedLeft;
            if (_musicStartX > _staffLeft + _staffWidth)
            {
                _musicStartX = _staffLeft + _staffWidth;
            }

            float middleGapFactor = compactForPrintLayout ? StaffMiddleGapFactor * PrintVerticalLayoutScale : StaffMiddleGapFactor;
            _staffMiddleGapFactor = middleGapFactor;
            float systemHeight = (4f + middleGapFactor + 4f) * _staffGap;
            float systemSpacingFactor = compactForPrintLayout ? SystemSpacingFactor * PrintVerticalLayoutScale : SystemSpacingFactor;
            float systemSpacing = _staffGap * systemSpacingFactor;
            float systemStride = systemHeight + systemSpacing;
            float headerReserveFactor = compactForPrintLayout ? 21.5f * PrintVerticalLayoutScale : 21.5f;
            float headerReserve = _staffGap * headerReserveFactor;
            int measureTicks = Math.Max(1, _ticksPerBeat * beatsPerBar);
            int desiredTotalMeasureCount = _displayMeasuresPerSystemOverride > 0
                ? GetFixedModeTotalMeasureCount(measureTicks, _measuresPerSystem)
                : GetTotalMeasureCount(measureTicks, _measuresPerSystem);
            if (_displayMeasuresPerSystemOverride > 0)
            {
                EnsureFixedSystemMeasureCounts(_measuresPerSystem, desiredTotalMeasureCount);
            }
            else
            {
                int autoSystemCount = GetRequiredSystemCount(_ticksPerBeat, beatsPerBar, _measuresPerSystem);
                int baselineSystemCount = _systemMeasureCounts.Count > 0
                    ? _systemMeasureCounts.Count
                    : autoSystemCount;
                int minSystemCount = Math.Max(baselineSystemCount, 1 + Math.Max(0, _manualAdditionalSystems));
                EnsureSystemMeasureCounts(_measuresPerSystem, minSystemCount, desiredTotalMeasureCount);
                if (ReflowDenseMeasuresAcrossSystems())
                {
                    _barlineOffsets.Clear();
                }
            }
            _totalMeasureCount = Math.Max(1, _systemMeasureCounts.Sum());
            int systemCount = Math.Max(1, _systemMeasureCounts.Count);
            RebuildMeasureTickBoundaries(_totalMeasureCount);
            float topMarginFloorFactor = compactForPrintLayout ? 21.7f * PrintVerticalLayoutScale : 21.7f;
            float topMargin = Math.Max(18f + headerReserve, _staffGap * topMarginFloorFactor);
            float staffLineThickness = GetStaffLineThickness();
            topMargin = AlignAxisForStroke(topMargin, staffLineThickness, ds.Dpi);
            systemStride = AlignDistanceToPixelGrid(systemStride, ds.Dpi);
            float contentHeight = topMargin + Math.Max(0, systemCount - 1) * systemStride + systemHeight + _staffGap * 3.2f;
            float maxMusicWidth = Math.Max(1f, _staffContentWidth);
            float desiredCanvasWidth = Math.Max(layoutWidth, reservedLeft + maxMusicWidth + sidePadding * 2f + _staffGap * 0.6f);
            EnsureStaffCanvasExtent(desiredCanvasWidth, contentHeight);
            _systemCount = systemCount;
            _systemStride = systemStride;
            _systemTopMargin = topMargin;
            _ornamentHitTargets.Clear();
            _clefHitTargets.Clear();

            DrawScoreHeader(ds, topMargin);

            for (int systemIndex = 0; systemIndex < systemCount; systemIndex++)
            {
                float systemTop = topMargin + systemIndex * systemStride;
                float trebleTop = systemTop;
                float trebleBottom = trebleTop + 4f * _staffGap;
                float bassTop = trebleBottom + middleGapFactor * _staffGap;
                float staffBottom = bassTop + 4f * _staffGap;
                int measuresInSystem = GetMeasuresInSystem(systemIndex);

                DrawStaff(ds, systemIndex, trebleTop, bassTop);
                DrawGrid(ds, systemIndex, trebleTop, staffBottom, beatsPerBar, measuresInSystem, systemIndex == systemCount - 1, suppressBeatGrid);
                DrawSystemMeasureNumber(ds, systemIndex, trebleTop);

                if (systemIndex == 0)
                {
                    _primaryTrebleTop = trebleTop;
                    _primaryTrebleBottom = trebleTop + 4f * _staffGap;
                    _primaryBassTop = bassTop;
                    _primaryBassBottom = bassTop + 4f * _staffGap;
                    _primaryBottomLineY = _primaryTrebleBottom;
                }

                DrawNotes(ds, systemIndex, measuresInSystem, trebleTop, bassTop, suppressSelectionVisuals);
                DrawExpressionMarks(ds, systemIndex, measuresInSystem, trebleTop, suppressSelectionVisuals);
                DrawPlaybackCursor(ds, systemIndex, measuresInSystem, trebleTop, staffBottom);
            }

            if (drawSelectionOverlay && _isSelectingRect)
            {
                DrawSelectionRectangle(ds);
            }

            if (!_musicFontAvailable)
            {
                DrawMissingFontNotice(ds, width);
            }
        }

        private int GetRequiredSystemCount(int ticksPerBeat, int beatsPerBar, int measuresPerSystem)
        {
            if (_viewModel == null) return 1;

            int safeTicksPerBeat = Math.Max(1, ticksPerBeat);
            int safeBeats = Math.Max(1, beatsPerBar);
            int safeMeasuresPerSystem = Math.Max(1, measuresPerSystem);
            int measureTicks = Math.Max(1, safeTicksPerBeat * safeBeats);
            int measureCount = GetTotalMeasureCount(measureTicks, safeMeasuresPerSystem);
            int autoSystems = Math.Max(1, (int)Math.Ceiling(measureCount / (double)safeMeasuresPerSystem));
            return Math.Max(1, autoSystems);
        }

        private int GetTotalMeasureCount(int measureTicks, int measuresPerSystem)
        {
            int perSystem = Math.Max(1, measuresPerSystem);
            int contentMeasureCount = GetContentMeasureCount(measureTicks);
            int manualMeasureCount = Math.Max(1, _manualMeasureCount);
            int total = Math.Max(contentMeasureCount, manualMeasureCount);
            int minimumRows = Math.Max(1, 1 + Math.Max(0, _manualAdditionalSystems));
            int minimumMeasureCount = Math.Max(1, minimumRows * perSystem);
            if (_systemMeasureCounts.Count > 0)
            {
                int preserved = _systemMeasureCounts.Sum();
                if (_systemMeasureCounts.Count < minimumRows)
                {
                    preserved += (minimumRows - _systemMeasureCounts.Count) * perSystem;
                }

                minimumMeasureCount = Math.Max(minimumMeasureCount, Math.Max(1, preserved));
            }

            return Math.Max(total, minimumMeasureCount);
        }

        private int GetFixedModeTotalMeasureCount(int measureTicks, int measuresPerSystem)
        {
            int perSystem = Math.Max(1, measuresPerSystem);
            int contentMeasureCount = GetContentMeasureCount(measureTicks);
            int manualMeasureCount = Math.Max(1, _manualMeasureCount);
            int minimumRows = Math.Max(1, 1 + Math.Max(0, _manualAdditionalSystems));
            int minimumMeasureCount = Math.Max(1, minimumRows * perSystem);
            return Math.Max(Math.Max(contentMeasureCount, manualMeasureCount), minimumMeasureCount);
        }

        private static int GetDefaultMeasuresPerSystemForTimeSignature(int numerator, int denominator)
        {
            int safeNumerator = Math.Clamp(numerator, 1, 12);
            int safeDenominator = denominator is 1 or 2 or 4 or 8 or 16 ? denominator : 4;

            if (safeNumerator == 4 && safeDenominator == 4) return 4;
            if (safeNumerator == 3 && safeDenominator == 4) return 5;
            if (safeNumerator == 6 && safeDenominator == 8) return 5;

            double quarterPerMeasure = safeNumerator * (4d / safeDenominator);
            if (quarterPerMeasure <= 0.0)
            {
                return FallbackAutoMeasuresPerSystem;
            }

            int estimated = (int)Math.Floor(20d / quarterPerMeasure);
            return Math.Clamp(estimated, 3, 10);
        }

        private int GetMeasuresInSystem(int systemIndex)
        {
            int safeSystemIndex = Math.Max(0, systemIndex);
            if (_systemMeasureCounts.Count > safeSystemIndex)
            {
                return Math.Max(1, _systemMeasureCounts[safeSystemIndex]);
            }

            return Math.Max(1, _measuresPerSystem);
        }

        private int GetSystemStartMeasureIndex(int systemIndex)
        {
            int safeSystemIndex = Math.Max(0, systemIndex);
            int start = 0;
            for (int i = 0; i < safeSystemIndex && i < _systemMeasureCounts.Count; i++)
            {
                start += Math.Max(1, _systemMeasureCounts[i]);
            }

            if (_systemMeasureCounts.Count == 0)
            {
                start = safeSystemIndex * Math.Max(1, _measuresPerSystem);
            }

            return Math.Max(0, start);
        }

        private int GetSystemIndexForMeasureIndex(int measureIndex)
        {
            int safeMeasureIndex = Math.Max(0, measureIndex);
            if (_systemMeasureCounts.Count == 0)
            {
                return Math.Max(0, safeMeasureIndex / Math.Max(1, _measuresPerSystem));
            }

            int cursor = 0;
            for (int system = 0; system < _systemMeasureCounts.Count; system++)
            {
                int count = Math.Max(1, _systemMeasureCounts[system]);
                if (safeMeasureIndex < cursor + count)
                {
                    return system;
                }

                cursor += count;
            }

            return Math.Max(0, _systemMeasureCounts.Count - 1);
        }

        private void EnsureSystemMeasureCounts(int defaultPerSystem, int minimumSystems, int minimumTotalMeasures)
        {
            int safeDefault = Math.Max(1, defaultPerSystem);
            int safeMinSystems = Math.Max(1, minimumSystems);
            int safeTotal = Math.Max(1, minimumTotalMeasures);

            if (_systemMeasureCounts.Count == 0)
            {
                for (int i = 0; i < safeMinSystems; i++)
                {
                    _systemMeasureCounts.Add(safeDefault);
                }
            }

            while (_systemMeasureCounts.Count < safeMinSystems)
            {
                _systemMeasureCounts.Add(safeDefault);
            }

            for (int i = 0; i < _systemMeasureCounts.Count; i++)
            {
                _systemMeasureCounts[i] = Math.Max(1, _systemMeasureCounts[i]);
            }

            int total = _systemMeasureCounts.Sum();
            while (total < safeTotal)
            {
                int last = Math.Max(0, _systemMeasureCounts.Count - 1);
                _systemMeasureCounts[last]++;
                total++;
            }
        }

        private void EnsureFixedSystemMeasureCounts(int fixedPerSystem, int minimumTotalMeasures)
        {
            int safePerSystem = Math.Max(1, fixedPerSystem);
            int safeTotal = Math.Max(1, minimumTotalMeasures);
            int minSystemsByContent = Math.Max(1, (int)Math.Ceiling(safeTotal / (double)safePerSystem));
            int minSystemsByManual = Math.Max(1, 1 + Math.Max(0, _manualAdditionalSystems));
            int requiredSystems = Math.Max(minSystemsByContent, minSystemsByManual);

            _systemMeasureCounts.Clear();
            for (int i = 0; i < requiredSystems; i++)
            {
                _systemMeasureCounts.Add(safePerSystem);
            }
        }

        private bool ReflowDenseMeasuresAcrossSystems()
        {
            if (_viewModel?.Project == null || _systemMeasureCounts.Count == 0)
            {
                return false;
            }

            bool changed = false;
            int safety = 0;
            while (safety++ < 256)
            {
                bool movedAny = false;
                int systemStartMeasure = 0;
                for (int systemIndex = 0; systemIndex < _systemMeasureCounts.Count; systemIndex++)
                {
                    int measuresInSystem = Math.Max(1, _systemMeasureCounts[systemIndex]);
                    while (measuresInSystem > 1
                           && !CanSystemSatisfyMeasureDemand(systemIndex, systemStartMeasure, measuresInSystem))
                    {
                        measuresInSystem--;
                        _systemMeasureCounts[systemIndex] = measuresInSystem;
                        if (systemIndex + 1 >= _systemMeasureCounts.Count)
                        {
                            _systemMeasureCounts.Add(1);
                        }
                        else
                        {
                            _systemMeasureCounts[systemIndex + 1] = Math.Max(1, _systemMeasureCounts[systemIndex + 1] + 1);
                        }

                        changed = true;
                        movedAny = true;
                    }

                    systemStartMeasure += _systemMeasureCounts[systemIndex];
                }

                if (!movedAny)
                {
                    break;
                }
            }

            return changed;
        }

        private bool CanSystemSatisfyMeasureDemand(int systemIndex, int systemStartMeasure, int measuresInSystem)
        {
            int count = Math.Max(1, measuresInSystem);
            float startX = GetSystemMusicStartX(systemIndex);
            float endX = GetSystemContentRightX();
            float availableWidth = Math.Max(1f, endX - startX);
            float preferredSum = 0f;
            float minimumSum = 0f;

            for (int localIndex = 0; localIndex < count; localIndex++)
            {
                int globalMeasure = Math.Max(0, systemStartMeasure + localIndex);
                float demand = GetMeasureVisualDemandFactor(globalMeasure);
                float preferred = Math.Max(_measureWidth * 0.42f, _measureWidth * (0.54f + demand * 0.62f));
                if (HasMeasureBarlineScoreMarks(globalMeasure))
                {
                    preferred += Math.Max(_staffGap * 1.6f, _measureWidth * 0.10f);
                }

                preferredSum += preferred;
                minimumSum += Math.Min(availableWidth, GetDynamicMeasureMinWidth(systemIndex, localIndex, count));
            }

            if (preferredSum <= availableWidth)
            {
                return true;
            }

            return minimumSum <= availableWidth;
        }

        private int GetBarlineOffsetKey(int systemIndex, int localBoundaryIndex)
        {
            return systemIndex * 1000 + localBoundaryIndex;
        }

        private float GetSystemRightX(int systemIndex)
        {
            int measuresInSystem = GetMeasuresInSystem(systemIndex);
            float[] boundaries = GetSystemBarlinePositions(systemIndex, measuresInSystem);
            return boundaries[Math.Max(1, measuresInSystem)];
        }

        private float GetSystemContentRightX()
        {
            return _musicStartX + Math.Max(1f, _staffContentWidth);
        }

        private bool ShouldDrawTimeSignatureAtSystemStart(int systemIndex)
        {
            if (_viewModel == null)
            {
                return systemIndex <= 0;
            }

            int safeSystem = Math.Max(0, systemIndex);
            int systemStartMeasure = GetSystemStartMeasureIndex(safeSystem);
            int systemStartTick = GetMeasureBoundaryTick(systemStartMeasure);
            if (safeSystem == 0)
            {
                return true;
            }

            return _viewModel.Project.TimeSignatureChanges.Any(c => c != null && Math.Max(0, c.Tick) == systemStartTick);
        }

        private float GetSystemMusicStartX(int systemIndex)
        {
            float startX = _musicStartX;
            if (systemIndex <= 0 || _timeSignatureBlockWidth <= 0f)
            {
                return startX;
            }

            if (!ShouldDrawTimeSignatureAtSystemStart(systemIndex))
            {
                float minStart = _staffLeft + _clefSpace + _keySpace + SymbolSizeGap * MusicGapAfterKey;
                startX = Math.Max(minStart, startX - _timeSignatureBlockWidth);
            }

            return startX;
        }

        private float GetBaseBarlineX(int systemIndex, int localBoundaryIndex, int measuresInSystem)
        {
            int safeMeasures = Math.Max(1, measuresInSystem);
            int clampedBoundary = Math.Clamp(localBoundaryIndex, 0, safeMeasures);
            float startX = GetSystemMusicStartX(systemIndex);
            float endX = GetSystemContentRightX();
            float[] baseBoundaries = GetSystemBaseBarlinePositions(systemIndex, safeMeasures, startX, endX);
            return baseBoundaries[clampedBoundary];
        }

        private float[] GetSystemBarlinePositions(int systemIndex, int measuresInSystem)
        {
            int count = Math.Max(1, measuresInSystem);
            var boundaries = new float[count + 1];
            float startX = GetSystemMusicStartX(systemIndex);
            // Keep each system's terminal barline at a stable X position.
            float endX = GetSystemContentRightX();
            float defaultMeasureWidth = _allowAutoMeasureRatioAdjust
                ? Math.Max(1f, (endX - startX) / count)
                : Math.Max(1f, _measureWidth);
            float availableWidth = Math.Max(1f, endX - startX);
            float[] baseBoundaries = GetSystemBaseBarlinePositions(systemIndex, count, startX, endX);
            int systemStartMeasure = GetSystemStartMeasureIndex(systemIndex);
            bool tailMeasureEmpty = IsMeasureEmpty(systemStartMeasure + count - 1);
            var minMeasureWidths = new float[count];
            for (int i = 0; i < count; i++)
            {
                int globalMeasure = systemStartMeasure + i;
                float demand = GetMeasureVisualDemandFactor(globalMeasure);
                float baseSegmentWidth = Math.Max(1f, baseBoundaries[i + 1] - baseBoundaries[i]);
                float minWidth = Math.Max(_staffGap * 3f, Math.Max(defaultMeasureWidth * 0.30f, baseSegmentWidth * 0.30f));
                if (demand > 1.45f)
                {
                    minWidth = Math.Max(minWidth, Math.Min(defaultMeasureWidth * 0.52f, baseSegmentWidth * 0.58f));
                }

                if (HasMeasureBarlineScoreMarks(globalMeasure))
                {
                    minWidth = Math.Max(minWidth, Math.Max(_staffGap * 3.5f, defaultMeasureWidth * 0.40f));
                }

                minMeasureWidths[i] = Math.Min(minWidth, availableWidth);
            }

            float minSum = minMeasureWidths.Sum();
            if (minSum > availableWidth && minSum > 1f)
            {
                float downScale = availableWidth / minSum;
                for (int i = 0; i < minMeasureWidths.Length; i++)
                {
                    minMeasureWidths[i] = Math.Max(1f, minMeasureWidths[i] * downScale);
                }
            }

            var suffixMinWidths = new float[count + 1];
            suffixMinWidths[count] = 0f;
            for (int i = count - 1; i >= 0; i--)
            {
                suffixMinWidths[i] = suffixMinWidths[i + 1] + minMeasureWidths[i];
            }

            boundaries[0] = startX;
            boundaries[count] = endX;

            for (int i = 1; i < count; i++)
            {
                float baseX = baseBoundaries[i];
                int key = GetBarlineOffsetKey(systemIndex, i);
                if (_barlineOffsets.TryGetValue(key, out float offset))
                {
                    baseX += offset;
                }

                float minX = boundaries[i - 1] + minMeasureWidths[i - 1];
                bool allowTailCollapse = i == count - 1 && tailMeasureEmpty;
                float rightReserve = allowTailCollapse ? 0f : suffixMinWidths[i];
                float maxX = endX - rightReserve;
                if (maxX < minX)
                {
                    maxX = minX;
                }

                boundaries[i] = Math.Clamp(baseX, minX, maxX);
            }

            return boundaries;
        }

        private float[] GetSystemBaseBarlinePositions(int systemIndex, int measuresInSystem, float startX, float endX)
        {
            int count = Math.Max(1, measuresInSystem);
            var boundaries = new float[count + 1];
            boundaries[0] = startX;
            boundaries[count] = endX;
            if (count == 1)
            {
                return boundaries;
            }

            float availableWidth = Math.Max(1f, endX - startX);
            var demandFactors = new float[count];
            int systemStartMeasure = GetSystemStartMeasureIndex(systemIndex);
            float sumDemand = 0f;
            for (int i = 0; i < count; i++)
            {
                float demand = GetMeasureVisualDemandFactor(systemStartMeasure + i);
                demandFactors[i] = demand;
                sumDemand += demand;
            }

            if (sumDemand <= 0.0001f)
            {
                sumDemand = count;
                for (int i = 0; i < count; i++)
                {
                    demandFactors[i] = 1f;
                }
            }

            float unit = availableWidth / sumDemand;
            float cursor = startX;
            for (int i = 1; i < count; i++)
            {
                cursor += demandFactors[i - 1] * unit;
                boundaries[i] = cursor;
            }

            boundaries[count] = endX;
            return boundaries;
        }

        private float GetMeasureVisualDemandFactor(int measureIndex)
        {
            int safeMeasure = Math.Max(0, measureIndex);
            if (_measureDemandFactorCache.TryGetValue(safeMeasure, out float cached))
            {
                return cached;
            }

            if (_viewModel?.Project == null)
            {
                return 1f;
            }

            int measureStartTick = GetMeasureBoundaryTick(safeMeasure);
            int measureEndTick = GetMeasureBoundaryTick(safeMeasure + 1);
            if (measureEndTick <= measureStartTick)
            {
                return 1f;
            }

            int ppq = Math.Max(1, _viewModel.Project.Ppq);
            int shortThreshold = Math.Max(1, ppq / 2);
            int tinyThreshold = Math.Max(1, ppq / 8);
            int soundingCount = 0;
            int shortCount = 0;
            int tinyCount = 0;
            int accidentalCount = 0;
            int chordExtra = 0;
            int closeOnsetWeight = 0;
            int chordClusterWeight = 0;
            var onsets = new Dictionary<int, int>();

            foreach (var note in _viewModel.Project.Notes)
            {
                if (note == null || note.IsRest)
                {
                    continue;
                }

                int tick = Math.Max(0, note.StartTick);
                if (tick < measureStartTick || tick >= measureEndTick)
                {
                    continue;
                }

                soundingCount++;
                int baseTicks = note.BaseDurationTicks > 0 ? note.BaseDurationTicks : note.DurationTicks;
                baseTicks = Math.Max(1, baseTicks);
                if (baseTicks <= shortThreshold)
                {
                    shortCount++;
                }

                if (baseTicks <= tinyThreshold)
                {
                    tinyCount++;
                }

                if (note.Accidental != NoteAccidental.None)
                {
                    accidentalCount++;
                }

                if (onsets.TryGetValue(tick, out int existing))
                {
                    onsets[tick] = existing + 1;
                }
                else
                {
                    onsets[tick] = 1;
                }
            }

            foreach (int count in onsets.Values)
            {
                chordExtra += Math.Max(0, count - 1);
                if (count >= 2)
                {
                    chordClusterWeight += count;
                }
            }

            int[] orderedOnsets = onsets.Keys.OrderBy(tick => tick).ToArray();
            for (int index = 1; index < orderedOnsets.Length; index++)
            {
                int gap = orderedOnsets[index] - orderedOnsets[index - 1];
                if (gap <= Math.Max(1, ppq / 8))
                {
                    closeOnsetWeight += 2;
                }
                else if (gap <= Math.Max(1, ppq / 4))
                {
                    closeOnsetWeight += 1;
                }
            }

            int markWeight = 0;
            foreach (var mark in _viewModel.Project.ExpressionMarks)
            {
                if (mark == null) continue;
                string code = NormalizeExpressionCode(mark.Code);
                int tick = Math.Max(0, mark.StartTick);
                bool inMeasure = tick >= measureStartTick && tick < measureEndTick;
                bool onMeasureEndBoundary = tick == measureEndTick && IsBarlineAnchoredScoreMark(code);
                if (!inMeasure && !onMeasureEndBoundary)
                {
                    continue;
                }

                markWeight += code switch
                {
                    ScoreMarkFinalBarline => 3,
                    ScoreMarkRepeatBarline => 3,
                    ScoreMarkEnding1 or ScoreMarkEnding2 => 2,
                    ScoreMarkSegno => 2,
                    _ => 1
                };
            }

            float factor = 1f
                + soundingCount * 0.028f
                + shortCount * 0.032f
                + tinyCount * 0.07f
                + accidentalCount * 0.024f
                + chordExtra * 0.062f
                + chordClusterWeight * 0.026f
                + closeOnsetWeight * 0.085f
                + markWeight * 0.14f;
            factor = Math.Clamp(factor, 1f, 3.2f);
            _measureDemandFactorCache[safeMeasure] = factor;
            return factor;
        }

        private bool HasMeasureBarlineScoreMarks(int measureIndex)
        {
            if (_viewModel?.Project == null)
            {
                return false;
            }

            int safeMeasure = Math.Max(0, measureIndex);
            int measureStartTick = GetMeasureBoundaryTick(safeMeasure);
            int measureEndTick = GetMeasureBoundaryTick(safeMeasure + 1);
            foreach (var mark in _viewModel.Project.ExpressionMarks)
            {
                if (mark == null)
                {
                    continue;
                }

                string code = NormalizeExpressionCode(mark.Code);
                int tick = Math.Max(0, mark.StartTick);
                bool inMeasure = tick >= measureStartTick && tick < measureEndTick;
                bool onMeasureEndBoundary = tick == measureEndTick && IsBarlineAnchoredScoreMark(code);
                if (!inMeasure && !onMeasureEndBoundary)
                {
                    continue;
                }

                if (code is ScoreMarkRepeatBarline or ScoreMarkFinalBarline or ScoreMarkEnding1 or ScoreMarkEnding2 or ScoreMarkSegno)
                {
                    return true;
                }
            }

            return false;
        }

        private float GetEndBoundarySplitMinWidth(float previousBoundaryX, float finalBoundaryX)
        {
            float tailWidth = Math.Max(1f, finalBoundaryX - previousBoundaryX);
            float preferredMin = Math.Max(_measureWidth * 0.35f, _staffGap * 3f);
            float maxAllowedByTail = Math.Max(1f, tailWidth * 0.45f);
            return Math.Max(1f, Math.Min(preferredMin, maxAllowedByTail));
        }

        private float GetDynamicMeasureMinWidth(int systemIndex, int localMeasureIndex, int measuresInSystem)
        {
            int safeMeasures = Math.Max(1, measuresInSystem);
            int clampedLocal = Math.Clamp(localMeasureIndex, 0, Math.Max(0, safeMeasures - 1));
            int globalMeasure = GetSystemStartMeasureIndex(systemIndex) + clampedLocal;
            float demand = GetMeasureVisualDemandFactor(globalMeasure);
            float minWidth = Math.Max(_measureWidth * 0.35f, _staffGap * 3f);
            if (demand > 1.45f)
            {
                minWidth = Math.Max(minWidth, _measureWidth * 0.45f);
            }

            if (HasMeasureBarlineScoreMarks(globalMeasure))
            {
                minWidth = Math.Max(minWidth, Math.Max(_staffGap * 3.8f, _measureWidth * 0.40f));
            }

            return minWidth;
        }

        private void SetSystemBoundaryAbsolutePosition(int systemIndex, int localBoundaryIndex, int measuresInSystem, float targetX)
        {
            if (localBoundaryIndex <= 0 || localBoundaryIndex >= Math.Max(1, measuresInSystem))
            {
                return;
            }

            float baseX = GetBaseBarlineX(systemIndex, localBoundaryIndex, measuresInSystem);
            int key = GetBarlineOffsetKey(systemIndex, localBoundaryIndex);
            float offset = targetX - baseX;
            if (Math.Abs(offset) < 0.5f)
            {
                _barlineOffsets.Remove(key);
            }
            else
            {
                _barlineOffsets[key] = offset;
            }
        }

        private void RemoveStaleBarlineOffsetsForSystem(int systemIndex, int measuresInSystem)
        {
            int safeSystem = Math.Max(0, systemIndex);
            int safeCount = Math.Max(1, measuresInSystem);
            var staleKeys = _barlineOffsets.Keys
                .Where(k => k / 1000 == safeSystem && (k % 1000) >= safeCount)
                .ToList();
            foreach (int key in staleKeys)
            {
                _barlineOffsets.Remove(key);
            }
        }

        private void EnsureStaffCanvasExtent(float targetWidth, float targetHeight)
        {
            if (StaffCanvas == null) return;

            double desiredWidth = Math.Max(1d, Math.Ceiling(targetWidth));
            double desired = Math.Min(MaxInteractiveCanvasExtent, Math.Max(MinCanvasHeight, Math.Ceiling(targetHeight)));
            if (double.IsNaN(StaffCanvas.Width) || Math.Abs(StaffCanvas.Width - desiredWidth) > 0.5d)
            {
                StaffCanvas.Width = desiredWidth;
            }

            if (double.IsNaN(StaffCanvas.Height) || Math.Abs(StaffCanvas.Height - desired) > 0.5d)
            {
                StaffCanvas.Height = desired;
            }

            if (NoteStepOverlayCanvas != null)
            {
                if (double.IsNaN(NoteStepOverlayCanvas.Width) || Math.Abs(NoteStepOverlayCanvas.Width - desiredWidth) > 0.5d)
                {
                    NoteStepOverlayCanvas.Width = desiredWidth;
                }

                if (double.IsNaN(NoteStepOverlayCanvas.Height) || Math.Abs(NoteStepOverlayCanvas.Height - desired) > 0.5d)
                {
                    NoteStepOverlayCanvas.Height = desired;
                }
            }
        }

        private float GetBeatWidth()
        {
            if (_viewModel == null) return 60f;

            int denominator = _viewModel.TimeSigDenominator;
            float baseWidth = 54f;
            float scale = 4f / Math.Max(1, denominator);
            return Math.Clamp(baseWidth * scale, 24f, 120f);
        }

        private float GetStaffLineThickness()
        {
            float legacyThin = SymbolSizeGap * BravuraStaffLineThicknessSpaces * 0.8f;
            float barlineThin = SymbolSizeGap * BravuraThinBarlineThicknessSpaces;
            return Math.Max(0.8f, (legacyThin + barlineThin) * 0.5f);
        }

        private float GetThinBarlineThickness()
        {
            return Math.Max(GetStaffLineThickness(), SymbolSizeGap * BravuraThinBarlineThicknessSpaces);
        }

        private float GetFinalBarlineThickThickness(float thinThickness)
        {
            // Keep a clear visual contrast, but avoid the previous overly heavy ending barline.
            return Math.Max(thinThickness * 2.2f, SymbolSizeGap * BravuraThickBarlineThicknessSpaces * 0.8f);
        }

        private static float AlignAxisForStroke(float positionDip, float strokeThicknessDip, float dpi)
        {
            float scale = Math.Max(0.01f, dpi / 96f);
            float positionPx = positionDip * scale;
            float strokePx = Math.Max(0.1f, strokeThicknessDip * scale);
            bool oddStroke = ((int)Math.Round(strokePx)) % 2 != 0;
            float alignedPx = oddStroke
                ? (float)(Math.Round(positionPx - 0.5f) + 0.5f)
                : (float)Math.Round(positionPx);
            return alignedPx / scale;
        }

        private static float AlignDistanceToPixelGrid(float distanceDip, float dpi)
        {
            float scale = Math.Max(0.01f, dpi / 96f);
            float distancePx = Math.Max(0.1f, distanceDip * scale);
            return Math.Max(0.01f, (float)Math.Round(distancePx) / scale);
        }

        private void DrawStaff(Microsoft.Graphics.Canvas.CanvasDrawingSession ds, int systemIndex, float trebleTop, float bassTop)
        {
            if (_viewModel == null) return;
            Color ink = GetNotationInkColor();
            int measuresInSystem = GetMeasuresInSystem(systemIndex);
            float rightX = GetSystemRightX(systemIndex);
            float staffLineThickness = GetStaffLineThickness();
            for (int i = 0; i < 5; i++)
            {
                float y = AlignAxisForStroke(trebleTop + i * _staffGap, staffLineThickness, ds.Dpi);
                ds.DrawLine(_staffLeft, y, rightX, y, ink, staffLineThickness);
            }

            for (int i = 0; i < 5; i++)
            {
                float y = AlignAxisForStroke(bassTop + i * _staffGap, staffLineThickness, ds.Dpi);
                ds.DrawLine(_staffLeft, y, rightX, y, ink, staffLineThickness);
            }

            StaffClefType topClef = GetSystemStaffClefType(systemIndex, topStaff: true);
            StaffClefType bottomClef = GetSystemStaffClefType(systemIndex, topStaff: false);
            float topClefLineY = GetStaffClefAnchorLineY(trebleTop, topClef);
            float bottomClefLineY = GetStaffClefAnchorLineY(bassTop, bottomClef);

            float clefX = _staffLeft + SymbolSizeGap * 0.6f;
            if (_musicFontAvailable && _musicFontFace != null)
            {
                DrawClefGlyph(ds, GetClefGlyphCode(topClef), clefX, topClefLineY, GetStaffClefYOffset(topClef), _clefFormat.FontSize);
                DrawClefGlyph(ds, GetClefGlyphCode(bottomClef), clefX, bottomClefLineY, GetStaffClefYOffset(bottomClef), _clefFormat.FontSize);
            }
            RegisterClefHitTarget(systemIndex, topStaff: true, clefX, topClefLineY, _clefFormat.FontSize);
            RegisterClefHitTarget(systemIndex, topStaff: false, clefX, bottomClefLineY, _clefFormat.FontSize);

            int systemMeasureStart = GetSystemStartMeasureIndex(systemIndex);
            int systemStartTick = GetMeasureBoundaryTick(systemMeasureStart);
            int systemEndTick = GetMeasureBoundaryTick(systemMeasureStart + measuresInSystem);
            TimeSignature startTimeSig = GetEffectiveTimeSignatureAtTick(systemStartTick);
            int startKeyFifths = GetEffectiveKeySignatureFifthsAtTick(systemStartTick);
            DrawKeySignature(ds, systemIndex, trebleTop, bassTop, startKeyFifths);
            bool drawTimeSigAtSystemStart = ShouldDrawTimeSignatureAtSystemStart(systemIndex);
            if (drawTimeSigAtSystemStart)
            {
                DrawTimeSignatureAtSystemStart(ds, trebleTop, bassTop, startTimeSig.Numerator, startTimeSig.Denominator, startKeyFifths);
            }
            DrawInlineSignatureChanges(ds, systemIndex, trebleTop, bassTop, systemStartTick, systemEndTick);
            // Keep brace slightly lower for optical balance between staves.
            float braceShift = SymbolSizeGap * 5.5f + 6f;
            DrawGrandStaffBrace(ds, trebleTop - SymbolSizeGap * 0.35f + braceShift, bassTop + 4.35f * SymbolSizeGap + braceShift);
        }

        private bool TryDrawStaffLinesGlyph(Microsoft.Graphics.Canvas.CanvasDrawingSession ds, float leftX, float topY, float width)
        {
            if (!_musicFontAvailable || _musicFontFace == null) return false;
            if (!_musicFontFace.HasCharacter((uint)SmuflStaff5LinesWide)) return false;

            float size = Math.Max(SymbolSizeGap * 4.2f, 24f);
            float advance = GetGlyphAdvanceRaw(SmuflStaff5LinesWide, size);
            if (advance < 1f) return false;

            float baselineY = topY + _staffGap * 4f;
            float scaleX = Math.Max(0.01f, width / advance);
            var old = ds.Transform;
            var origin = new System.Numerics.Vector2(leftX, baselineY);
            ds.Transform = System.Numerics.Matrix3x2.CreateScale(scaleX, 1f, origin) * old;
            DrawGlyph(ds, SmuflStaff5LinesWide, leftX, baselineY, size);
            ds.Transform = old;
            return true;
        }

        private void DrawSystemMeasureNumber(Microsoft.Graphics.Canvas.CanvasDrawingSession ds, int systemIndex, float trebleTop)
        {
            if (_viewModel == null) return;
            if (systemIndex <= 0) return;

            int firstMeasureNumber = GetSystemStartMeasureIndex(systemIndex) + 1;
            string text = firstMeasureNumber.ToString();
            float x = _staffLeft - _staffGap * 0.2f;
            float y = trebleTop - _staffGap * 2.74f;
            _measureNumberFormat.FontSize = Math.Max(14f, SymbolSizeGap * 1.34f);
            _measureNumberFormat.FontFamily = _expressionTextFormat.FontFamily;
            bool isActiveDragSystem = _isDraggingBarline && _dragBarlineSystemIndex == systemIndex;
            _measureNumberFormat.FontWeight = isActiveDragSystem ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            ds.DrawText(text, x, y, isActiveDragSystem ? GetAccentColor() : GetNotationInkColor(), _measureNumberFormat);
        }

        private void DrawScoreHeader(Microsoft.Graphics.Canvas.CanvasDrawingSession ds, float firstSystemTop)
        {
            if (_viewModel == null) return;
            Color ink = GetNotationInkColor();

            _titleFormat.FontSize = Math.Max(29f, SymbolSizeGap * 3.0f);
            _tempoFormat.FontSize = Math.Max(15f, SymbolSizeGap * 1.32f);
            _titleFormat.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
            _tempoFormat.FontWeight = Microsoft.UI.Text.FontWeights.Normal;

            string title = string.IsNullOrWhiteSpace(_viewModel.Title) ? "Untitled" : _viewModel.Title.Trim();
            bool hasChinese = title.Any(c => c >= '\u4E00' && c <= '\u9FFF');
            string headerFont = hasChinese ? "Microsoft YaHei UI" : "Times New Roman";
            _titleFormat.FontFamily = headerFont;
            _tempoFormat.FontFamily = "Times New Roman";
            float titleRectX = _staffLeft;
            float titleRectY = firstSystemTop - _staffGap * 17.9f;
            float titleRectWidth = _staffWidth;
            float titleRectHeight = _staffGap * 3.2f;
            _titleHitRect = new Rect(titleRectX, titleRectY - _staffGap * 0.35f, titleRectWidth, titleRectHeight + _staffGap * 0.7f);
            ds.DrawText(title, titleRectX, titleRectY, titleRectWidth, titleRectHeight, ink, _titleFormat);

            int bpm = (int)Math.Round(Math.Max(0d, _viewModel.Bpm));
            if (bpm > 0)
            {
                float tempoX = _staffLeft + _staffGap * 0.4f;
                float tempoY = firstSystemTop - _staffGap * 4.65f;
                DrawTempoHeader(ds, tempoX, tempoY, bpm, _viewModel.TimeSigDenominator, ink);
            }
        }

        private void DrawTempoHeader(Microsoft.Graphics.Canvas.CanvasDrawingSession ds, float x, float y, int bpm, int denominator, Color ink)
        {
            string prefix = $"{GetTempoItalianName(bpm)} (";
            ds.DrawText(prefix, x, y, ink, _tempoFormat);

            float prefixWidth = GetTextWidth(prefix, _tempoFormat);
            float cursor = x + prefixWidth;
            int beatGlyphCode = GetTempoBeatGlyphCode(denominator);
            if (_musicFontAvailable && _musicFontFace != null && beatGlyphCode != 0 && _musicFontFace.HasCharacter((uint)beatGlyphCode))
            {
                float glyphSize = Math.Max(_tempoFormat.FontSize * 1.08f, SymbolSizeGap * 1.42f);
                float beatBaseline = y + _tempoFormat.FontSize * 0.88f;
                float advance = DrawGlyph(ds, beatGlyphCode, cursor, beatBaseline, glyphSize, ink);
                cursor += Math.Max(advance, glyphSize * 0.42f);
            }
            else
            {
                string beatSymbol = GetTempoBeatFallback(denominator);
                var fallbackFormat = new CanvasTextFormat
                {
                    FontFamily = "Segoe UI Symbol",
                    FontSize = _tempoFormat.FontSize,
                    FontStyle = FontStyle.Normal,
                    FontWeight = _tempoFormat.FontWeight
                };
                ds.DrawText(beatSymbol, cursor, y, ink, fallbackFormat);
                cursor += GetTextWidth(beatSymbol, fallbackFormat);
            }

            string suffix = $" = {bpm})";
            ds.DrawText(suffix, cursor + SymbolSizeGap * 0.16f, y, ink, _tempoFormat);
        }

        private float GetTextWidth(string text, CanvasTextFormat format)
        {
            if (string.IsNullOrEmpty(text)) return 0f;
            using var layout = new CanvasTextLayout(Ca... [truncated]