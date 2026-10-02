using Godot;
using MegaCrit.Sts2.Core.Localization.Fonts;
using System.Globalization;

namespace CombatSolver;

internal sealed partial class SolverMemoryUsageBar : PanelContainer
{
    private enum MemoryPressureTone
    {
        Idle,
        Normal,
        Warning,
        Danger,
    }

    private enum MemoryDisplayState
    {
        Search,
        SearchNearLimit,
        ForegroundReclaim,
        BackgroundCleanup,
        Idle,
        AutomaticManagement,
    }

    private const double RefreshIntervalSeconds = 0.25d;
    private const long BytesPerGigabyte = 1_000_000_000L;

    private readonly Label _label;
    private readonly PanelContainer _progress;
    private readonly ColorRect _systemSegment;
    private readonly ColorRect _processSegment;
    private readonly ColorRect _remainingSegment;
    private double _elapsedSinceRefresh = RefreshIntervalSeconds;
    private MemoryDisplayState? _lastLoggedState;
    private int _lastLoggedSearchLoadDecile = -1;

    public SolverMemoryUsageBar()
    {
        Name = "MemoryUsage";
        CustomMinimumSize = new Vector2(0f, SolverUiTokens.Size.ButtonHeight);
        MouseFilter = MouseFilterEnum.Pass;
        TooltipText =
            SolverText.Get("求解器内存与性能监视\n") +
            SolverText.Get("- 整条表示物理内存总量。灰色为其他占用估计，彩色为游戏工作集，空白为系统可用。\n");
        AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.SurfaceRaised,
            SolverUiTokens.Palette.Border,
            SolverUiTokens.Radius.Medium,
            SolverUiTokens.Spacing.Sm,
            SolverUiTokens.Spacing.Xs));

        VBoxContainer content = new()
        {
            MouseFilter = MouseFilterEnum.Ignore,
        };
        content.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Xxs);
        AddChild(content);

        _label = SolverUiTokens.CreateLabel(
            SolverText.Get("当前内存 --"),
            SolverUiTokens.Type.Caption,
            SolverUiTokens.Palette.TextSecondary,
            FontType.Bold);
        _label.HorizontalAlignment = HorizontalAlignment.Right;
        _label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        content.AddChild(_label);

        _progress = new PanelContainer
        {
            CustomMinimumSize = new Vector2(0f, 8f),
            MouseFilter = MouseFilterEnum.Ignore,
            ClipContents = true,
        };
        _progress.AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.ProgressBackground,
            SolverUiTokens.Palette.BorderSubtle,
            SolverUiTokens.Radius.Small,
            0,
            0));
        HBoxContainer segments = new()
        {
            MouseFilter = MouseFilterEnum.Ignore,
        };
        segments.AddThemeConstantOverride("separation", 0);
        _systemSegment = CreateSegment(SolverUiTokens.Palette.TextMuted.Darkened(0.3f));
        _processSegment = CreateSegment(SolverUiTokens.Palette.Accent);
        _remainingSegment = CreateSegment(Colors.Transparent);
        segments.AddChild(_systemSegment);
        segments.AddChild(_processSegment);
        segments.AddChild(_remainingSegment);
        _progress.AddChild(segments);
        content.AddChild(_progress);
        RefreshDisplay();
    }

    public override void _Process(double delta)
    {
        _elapsedSinceRefresh += delta;
        if (_elapsedSinceRefresh < RefreshIntervalSeconds || !IsVisibleInTree())
            return;
        _elapsedSinceRefresh = 0d;
        RefreshDisplay();
    }

    internal bool LayoutConfiguredForTesting
        => Math.Abs(CustomMinimumSize.X) < 0.01f
            && SizeFlagsHorizontal == SizeFlags.ExpandFill
            && Math.Abs(_progress.CustomMinimumSize.Y - 8f) < 0.01f;

    internal static bool ExerciseFormattingForTesting()
    {
        MemoryBarDisplay active = BuildDisplay(new SearchMemoryUsageSnapshot(
            6_400_000_000L,
            21_400_000_000L,
            16_000_000_000L,
            SearchActive: true,
            SearchAllocatedBytes: 9_000_000_000L,
            SearchAllocationLimitBytes: 10_000_000_000L,
            ProjectedSystemMemoryLoadBytes: 8_000_000_000L,
            SystemMemoryLimitBytes: 22_000_000_000L,
            Reclaiming: false,
            BackgroundReclaiming: false) { PhysicalMemoryTotalBytes = 32_000_000_000L });
        MemoryBarDisplay reclaiming = BuildDisplay(new SearchMemoryUsageSnapshot(
            6_100_000_000L,
            20_000_000_000L,
            16_000_000_000L,
            SearchActive: true,
            SearchAllocatedBytes: 10_000_000_000L,
            SearchAllocationLimitBytes: 10_000_000_000L,
            ProjectedSystemMemoryLoadBytes: 10_000_000_000L,
            SystemMemoryLimitBytes: 22_000_000_000L,
            Reclaiming: true,
            BackgroundReclaiming: false) { PhysicalMemoryTotalBytes = 32_000_000_000L });
        MemoryBarDisplay idle = BuildDisplay(new SearchMemoryUsageSnapshot(
            2_000_000_000L,
            8_000_000_000L,
            16_000_000_000L,
            SearchActive: false,
            SearchAllocatedBytes: 0L,
            SearchAllocationLimitBytes: long.MaxValue,
            ProjectedSystemMemoryLoadBytes: 0L,
            SystemMemoryLimitBytes: 12_000_000_000L,
            Reclaiming: false,
            BackgroundReclaiming: false) { PhysicalMemoryTotalBytes = 16_000_000_000L });
        MemoryBarDisplay background = BuildDisplay(new SearchMemoryUsageSnapshot(
            8_000_000_000L,
            18_000_000_000L,
            16_000_000_000L,
            SearchActive: false,
            SearchAllocatedBytes: 0L,
            SearchAllocationLimitBytes: long.MaxValue,
            ProjectedSystemMemoryLoadBytes: 0L,
            SystemMemoryLimitBytes: 22_000_000_000L,
            Reclaiming: false,
            BackgroundReclaiming: true) { PhysicalMemoryTotalBytes = 32_000_000_000L });
        MemoryBarDisplay systemLimited = BuildDisplay(new SearchMemoryUsageSnapshot(
            7_200_000_000L,
            21_200_000_000L,
            16_000_000_000L,
            SearchActive: true,
            SearchAllocatedBytes: 2_000_000_000L,
            SearchAllocationLimitBytes: 10_000_000_000L,
            ProjectedSystemMemoryLoadBytes: 21_200_000_000L,
            SystemMemoryLimitBytes: 22_000_000_000L,
            Reclaiming: false,
            BackgroundReclaiming: false) { PhysicalMemoryTotalBytes = 32_000_000_000L });
        SearchMemoryUsageSnapshot aboveThreshold = new(
            6_000_000_000L, 24_000_000_000L, 16_000_000_000L, true,
            0, long.MaxValue, 24_000_000_000L, 22_000_000_000L, false, false)
        { PhysicalMemoryTotalBytes = 32_000_000_000L };
        MemoryBarDisplay automatic = BuildDisplay(aboveThreshold);
        MemoryBarDisplay differentThreshold = BuildDisplay(aboveThreshold with { SystemMemoryLimitBytes = 16_000_000_000L });
        MemoryBarDisplay unavailable = BuildDisplay(aboveThreshold with { PhysicalMemoryTotalBytes = 0 });
        return active.Text == FormatSummary(6_400_000_000L, 10_600_000_000L) + SolverText.Get("  ·  即将整理")
            && Math.Abs(active.PressureRatio - 21.4d / 22d) < 0.001d
            && active.Tone == MemoryPressureTone.Danger
            && reclaiming.Text == FormatSummary(6_100_000_000L, 12_000_000_000L) + SolverText.Get("  ·  正在整理…")
            && reclaiming.State == MemoryDisplayState.ForegroundReclaim
            && idle.Text == FormatSummary(2_000_000_000L, 8_000_000_000L)
            && idle.State == MemoryDisplayState.Idle
            && background.Text == FormatSummary(8_000_000_000L, 14_000_000_000L) + SolverText.Get("  ·  后台清理中")
            && background.Tone == MemoryPressureTone.Warning
            && systemLimited.State == MemoryDisplayState.SearchNearLimit
            && Math.Abs(systemLimited.PressureRatio - 21.2d / 22d) < 0.001d
            && automatic.Text == FormatSummary(6_000_000_000L, 8_000_000_000L) + SolverText.Get("  ·  自动管理")
            && automatic.SystemRatio == 18d / 32d && automatic.ProcessRatio == 6d / 32d
            && automatic.SystemRatio + automatic.ProcessRatio == 24d / 32d
            && automatic.Text == differentThreshold.Text && automatic.SystemRatio == differentThreshold.SystemRatio
            && automatic.ProcessRatio == differentThreshold.ProcessRatio
            && unavailable.Text == FormatSummary(6_000_000_000L, null) + SolverText.Get("  ·  自动管理")
            && unavailable.SystemRatio == 0 && unavailable.ProcessRatio == 0
            && BuildTooltip(aboveThreshold).Contains("32.0 GB", StringComparison.Ordinal)
            && BuildTooltip(aboveThreshold).Contains("22.0 GB", StringComparison.Ordinal);
    }

    private void RefreshDisplay()
    {
        SearchMemoryUsageSnapshot snapshot = SolverController.CaptureSearchMemoryUsage();
        MemoryBarDisplay display = BuildDisplay(snapshot);
        TooltipText = BuildTooltip(snapshot);
        Color color = ToneColor(display.Tone);
        _label.Text = display.Text;
        _label.AddThemeColorOverride("font_color", color);
        _processSegment.Color = color;
        SetSegmentRatio(_systemSegment, display.SystemRatio);
        SetSegmentRatio(_processSegment, display.ProcessRatio);
        SetSegmentRatio(
            _remainingSegment,
            Math.Max(0d, 1d - display.SystemRatio - display.ProcessRatio));
        LogDisplayTransition(snapshot, display);
    }

    private void LogDisplayTransition(
        SearchMemoryUsageSnapshot snapshot,
        MemoryBarDisplay display)
    {
        int searchLoadDecile = display.State is MemoryDisplayState.Search
            or MemoryDisplayState.SearchNearLimit
                ? Math.Min(10, (int)Math.Floor(display.PressureRatio * 10d))
                : -1;
        if (_lastLoggedState == display.State
            && _lastLoggedSearchLoadDecile == searchLoadDecile)
        {
            return;
        }
        _lastLoggedState = display.State;
        _lastLoggedSearchLoadDecile = searchLoadDecile;
        SolverController.LogSearchMemoryDisplayState(
            snapshot,
            DisplayStateToken(display.State),
            display.PressureRatio);
    }

    private static MemoryBarDisplay BuildDisplay(SearchMemoryUsageSnapshot snapshot)
    {
        string summary = FormatSummary(snapshot.ProcessWorkingSetBytes, snapshot.PhysicalMemoryAvailableBytes);
        double pressureRatio = snapshot.CleanupPressureRatio;
        double systemRatio = snapshot.SystemSegmentRatio;
        double processRatio = snapshot.ProcessSegmentRatio;
        if (snapshot.Reclaiming)
        {
            return new MemoryBarDisplay(
                summary + SolverText.Get("  ·  正在整理…"),
                systemRatio,
                processRatio,
                pressureRatio,
                MemoryPressureTone.Warning,
                MemoryDisplayState.ForegroundReclaim);
        }
        if (snapshot.BackgroundReclaiming)
        {
            return new MemoryBarDisplay(
                summary + SolverText.Get("  ·  后台清理中"),
                systemRatio,
                processRatio,
                pressureRatio,
                MemoryPressureTone.Warning,
                MemoryDisplayState.BackgroundCleanup);
        }
        if (!snapshot.SearchActive)
        {
            return new MemoryBarDisplay(
                summary,
                systemRatio,
                processRatio,
                pressureRatio,
                ToneForRatio(pressureRatio),
                MemoryDisplayState.Idle);
        }
        if (!snapshot.HasGcWall)
        {
            return new MemoryBarDisplay(
                summary + SolverText.Get("  ·  自动管理"),
                systemRatio,
                processRatio,
                pressureRatio,
                ToneForRatio(pressureRatio),
                MemoryDisplayState.AutomaticManagement);
        }

        return new MemoryBarDisplay(
            summary + (pressureRatio >= 0.9d ? SolverText.Get("  ·  即将整理") : string.Empty),
            systemRatio,
            processRatio,
            pressureRatio,
            ToneForRatio(pressureRatio),
            pressureRatio >= 0.9d ? MemoryDisplayState.SearchNearLimit : MemoryDisplayState.Search);
    }

    private static string FormatSummary(long processBytes, long? availableBytes)
        => availableBytes is long available
            ? SolverText.Format($"游戏占用 {FormatGigabytes(processBytes)} GB · 系统可用 {FormatGigabytes(available)} GB")
            : SolverText.Format($"游戏占用 {FormatGigabytes(processBytes)} GB · 系统可用未知");

    private static string BuildTooltip(SearchMemoryUsageSnapshot snapshot)
    {
        string total = snapshot.HasPhysicalMemorySample
            ? FormatGigabytes(snapshot.PhysicalMemoryTotalBytes) : SolverText.Get("未知");
        string threshold = snapshot.SystemMemoryLimitBytes == long.MaxValue
            ? SolverText.Get("未知") : FormatGigabytes(snapshot.SystemMemoryLimitBytes);
        return SolverText.Get("求解器内存与性能监视\n")
            + SolverText.Get("- 游戏占用是进程工作集，包含求解器和其他 Mod；系统可用是操作系统当前可用的物理内存，两者不是用量与上限。\n")
            + SolverText.Get("- 整条表示物理内存总量。灰色为其他占用估计，彩色为游戏工作集，空白为系统可用。\n")
            + SolverText.Format($"物理内存总量：{total} GB；GC 压力阈值：{threshold} GB。\n")
            + SolverText.Get("GC 阈值用于回收压力判断，不是物理容量或游戏可用上限。GB = 1,000,000,000 字节。");
    }

    private static string FormatGigabytes(long bytes)
        => (bytes / (double)BytesPerGigabyte).ToString("F1", CultureInfo.InvariantCulture);

    private static ColorRect CreateSegment(Color color)
        => new()
        {
            Color = color,
            MouseFilter = MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };

    private static void SetSegmentRatio(Control segment, double ratio)
    {
        float stretchRatio = (float)Math.Clamp(ratio, 0d, 1d);
        segment.Visible = stretchRatio > 0f;
        segment.SizeFlagsStretchRatio = stretchRatio;
    }

    private static MemoryPressureTone ToneForRatio(double ratio)
        => ratio >= 0.9d
            ? MemoryPressureTone.Danger
            : ratio >= 0.7d
                ? MemoryPressureTone.Warning
                : MemoryPressureTone.Normal;

    private static Color ToneColor(MemoryPressureTone tone)
        => tone switch
        {
            MemoryPressureTone.Danger => SolverUiTokens.Palette.Danger,
            MemoryPressureTone.Warning => SolverUiTokens.Palette.Warning,
            MemoryPressureTone.Normal => SolverUiTokens.Palette.Accent,
            _ => SolverUiTokens.Palette.TextMuted,
        };

    private static string DisplayStateToken(MemoryDisplayState state) => state switch
    {
        MemoryDisplayState.Search => "search_load",
        MemoryDisplayState.SearchNearLimit => "search_near_cleanup",
        MemoryDisplayState.ForegroundReclaim => "foreground_cleanup",
        MemoryDisplayState.BackgroundCleanup => "idle_background_cleanup",
        MemoryDisplayState.Idle => "idle",
        MemoryDisplayState.AutomaticManagement => "automatic_management",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    private readonly record struct MemoryBarDisplay(
        string Text,
        double SystemRatio,
        double ProcessRatio,
        double PressureRatio,
        MemoryPressureTone Tone,
        MemoryDisplayState State);
}
