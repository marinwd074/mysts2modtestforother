using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Nodes;

namespace CombatSolver;

internal static partial class SolverOverlay
{
    private static Button CreateHeaderButton(string text, float minimumWidth)
    {
        Button button = SolverUiTokens.CreateButton(text, SolverButtonStyle.Secondary);
        button.CustomMinimumSize = new Vector2(minimumWidth, SolverUiTokens.Size.ButtonHeight);
        button.ApplyLocaleFontSubstitution(FontType.Bold, "font");
        return button;
    }

    private static Control CreateNoveltyPortfolioHint()
    {
        _noveltyPortfolioHintButton = CreateDismissibleGuidanceHint(
            "NoveltyPortfolioHint",
            Accent,
            DismissNoveltyPortfolioHint);
        return _noveltyPortfolioHintButton;
    }

    private static Control CreateSpeedXWarning()
    {
        _speedXWarningButton = CreateDismissibleGuidanceHint(
            "SpeedXWarning",
            Warning,
            DismissSpeedXWarning);
        return _speedXWarningButton;
    }

    private static Button CreateDismissibleGuidanceHint(
        string name,
        Color tone,
        Action dismiss)
    {
        Button button = SolverUiTokens.CreateButton(string.Empty, SolverButtonStyle.Secondary);
        button.Name = name;
        button.Visible = false;
        button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        button.CustomMinimumSize = new Vector2(0, 44);
        button.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        button.AddThemeStyleboxOverride("normal", SolverUiTokens.CreateBox(
            SolverUiTokens.IsLightTheme ? tone.Lightened(0.82f) : tone.Darkened(0.78f),
            tone,
            SolverUiTokens.Radius.Large,
            SolverUiTokens.Spacing.Md,
            SolverUiTokens.Spacing.Xxs));
        button.AddThemeStyleboxOverride("hover", SolverUiTokens.CreateBox(
            SolverUiTokens.IsLightTheme ? tone.Lightened(0.72f) : tone.Darkened(0.68f),
            tone.Lightened(0.12f),
            SolverUiTokens.Radius.Large,
            SolverUiTokens.Spacing.Md,
            SolverUiTokens.Spacing.Xxs));
        button.Pressed += dismiss;
        return button;
    }

    private static Control CreatePerformanceHint()
    {
        _performanceHintButton = SolverUiTokens.CreateButton(
            SolverText.Get("本场战斗出现大战损，若对结果不满意可以前往 设置 > 性能，将性能预设调为高或极高后重试。点击本消息之后不再提示"),
            SolverButtonStyle.Secondary);
        _performanceHintButton.Name = "PerformanceHint";
        _performanceHintButton.Visible = false;
        _performanceHintButton.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _performanceHintButton.CustomMinimumSize = new Vector2(0, 44);
        _performanceHintButton.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _performanceHintButton.AddThemeStyleboxOverride("normal", SolverUiTokens.CreateBox(
            SolverUiTokens.IsLightTheme ? Warning.Lightened(0.82f) : Warning.Darkened(0.78f),
            Warning,
            SolverUiTokens.Radius.Large,
            SolverUiTokens.Spacing.Md,
            SolverUiTokens.Spacing.Xxs));
        _performanceHintButton.AddThemeStyleboxOverride("hover", SolverUiTokens.CreateBox(
            SolverUiTokens.IsLightTheme ? Warning.Lightened(0.72f) : Warning.Darkened(0.68f),
            Warning.Lightened(0.12f),
            SolverUiTokens.Radius.Large,
            SolverUiTokens.Spacing.Md,
            SolverUiTokens.Spacing.Xxs));
        _performanceHintButton.Pressed += DismissPerformanceHint;
        return _performanceHintButton;
    }

    private static Control CreateSearchLimitHint()
    {
        _searchLimitHint = new PanelContainer
        {
            Name = "SearchLimitHint",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 44),
        };
        _searchLimitHint.AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            SolverUiTokens.IsLightTheme ? Warning.Lightened(0.82f) : Warning.Darkened(0.78f),
            Warning,
            SolverUiTokens.Radius.Large,
            SolverUiTokens.Spacing.Md,
            SolverUiTokens.Spacing.Xxs));
        _searchLimitHintLabel = CreateTextLabel(
            string.Empty,
            SolverUiTokens.Type.Body,
            Warning,
            FontType.Bold);
        _searchLimitHintLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _searchLimitHintLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _searchLimitHint.AddChild(_searchLimitHintLabel);
        return _searchLimitHint;
    }

    private static Control CreateBossHpStrategyHint()
    {
        _bossHpStrategyHintButton = SolverUiTokens.CreateButton(
            string.Empty,
            SolverButtonStyle.Secondary);
        _bossHpStrategyHintButton.Name = "BossHpStrategyHint";
        _bossHpStrategyHintButton.Visible = false;
        _bossHpStrategyHintButton.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _bossHpStrategyHintButton.CustomMinimumSize = new Vector2(0, 44);
        _bossHpStrategyHintButton.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _bossHpStrategyHintButton.AddThemeStyleboxOverride("normal", SolverUiTokens.CreateBox(
            SolverUiTokens.IsLightTheme ? Accent.Lightened(0.84f) : Accent.Darkened(0.78f),
            Accent,
            SolverUiTokens.Radius.Large,
            SolverUiTokens.Spacing.Md,
            SolverUiTokens.Spacing.Xxs));
        _bossHpStrategyHintButton.AddThemeStyleboxOverride("hover", SolverUiTokens.CreateBox(
            SolverUiTokens.IsLightTheme ? Accent.Lightened(0.74f) : Accent.Darkened(0.68f),
            Accent.Lightened(0.12f),
            SolverUiTokens.Radius.Large,
            SolverUiTokens.Spacing.Md,
            SolverUiTokens.Spacing.Xxs));
        _bossHpStrategyHintButton.Pressed += DismissBossHpStrategyHint;
        return _bossHpStrategyHintButton;
    }

    private static Control CreateSummarySection()
    {
        const int summaryFontSize = 16;
        _summaryPanel = CreateSectionPanel("SummaryPanel");
        _summaryPanel.MouseFilter = Control.MouseFilterEnum.Pass;
        _summaryPanel.CustomMinimumSize = Vector2.Zero;
        VBoxContainer layout = new() { MouseFilter = Control.MouseFilterEnum.Pass };
        layout.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        HBoxContainer statusRow = new()
        {
            MouseFilter = Control.MouseFilterEnum.Pass,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        statusRow.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Md);
        HFlowContainer statisticsRow = new()
        {
            MouseFilter = Control.MouseFilterEnum.Pass,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        statisticsRow.AddThemeConstantOverride("h_separation", SolverUiTokens.Spacing.Md);
        statisticsRow.AddThemeConstantOverride("v_separation", SolverUiTokens.Spacing.Xs);
        _summaryStatusBadge = new PanelContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(0, 26),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
        };
        _summaryStateLabel = CreateTextLabel(
            SolverText.Get("等待战斗状态"),
            summaryFontSize,
            TextMuted,
            FontType.Bold);
        _summaryStatusBadge.AddChild(_summaryStateLabel);
        statusRow.AddChild(_summaryStatusBadge);
        _summaryContextLabel = CreateTextLabel(
            string.Empty,
            summaryFontSize,
            SolverUiTokens.Palette.TextSecondary,
            FontType.Bold);
        _summaryContextLabel.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        _summaryContextLabel.CustomMinimumSize = new Vector2(0, 24);
        _summaryContextLabel.AutowrapMode = TextServer.AutowrapMode.Off;
        _summaryContextLabel.Visible = false;
        statisticsRow.AddChild(_summaryContextLabel);
        _summaryText = CreateRichText(summaryFontSize);
        _summaryText.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _summaryText.FitContent = true;
        _summaryText.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _summaryText.CustomMinimumSize = new Vector2(0, 24);
        _summaryText.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        _summaryText.ApplyLocaleFontSubstitution(FontType.Bold, "normal_font");
        _progressText = CreateTextLabel(string.Empty, summaryFontSize, TextPrimary, FontType.Bold);
        _progressText.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        _progressText.CustomMinimumSize = new Vector2(0, 24);
        _progressText.AutowrapMode = TextServer.AutowrapMode.Off;
        _progressText.Visible = false;
        statisticsRow.AddChild(_progressText);
        _reviewText = CreateTextLabel(
            string.Empty,
            summaryFontSize,
            SolverUiTokens.Palette.TextSecondary,
            FontType.Bold);
        _reviewText.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _reviewText.CustomMinimumSize = new Vector2(0, 24);
        _reviewText.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _reviewText.Visible = false;
        _detailsButton = new SolverDetailsButton
        {
            Visible = false,
        };
        _detailsButton.Pressed += ToggleDetails;
        MarginContainer detailsSlot = new()
        {
            CustomMinimumSize = new Vector2(0, 24),
            MouseFilter = Control.MouseFilterEnum.Pass,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
        };
        detailsSlot.AddChild(_detailsButton);
        detailsSlot.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        statusRow.AddChild(_summaryText);
        statusRow.AddChild(detailsSlot);
        layout.AddChild(statusRow);
        statisticsRow.AddChild(_reviewText);
        layout.AddChild(statisticsRow);
        _searchProgressBar = new ProgressBar
        {
            Name = "SearchProgress",
            MinValue = 0,
            MaxValue = SolverSearchProfile.Default.MaxExpandedNodes,
            Value = 0,
            ShowPercentage = false,
            CustomMinimumSize = new Vector2(0, 4),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _searchProgressBar.AddThemeStyleboxOverride("background",
            SolverUiTokens.CreateBox(
                SolverUiTokens.Palette.ProgressBackground,
                SolverUiTokens.IsLightTheme ? Colors.Transparent : SolverUiTokens.Palette.BorderSubtle,
                SolverUiTokens.Radius.Small,
                0,
                0,
                borderWidth: SolverUiTokens.IsLightTheme ? 0 : 1));
        _searchProgressBar.AddThemeStyleboxOverride("fill",
            SolverUiTokens.CreateBox(
                SolverUiTokens.Palette.ProgressFill,
                Accent,
                SolverUiTokens.Radius.Small,
                0,
                0));
        layout.AddChild(_searchProgressBar);
        _summaryPanel.AddChild(layout);
        return _summaryPanel;
    }

    private static Control CreateFooter()
    {
        _theftPolicyControls = new HBoxContainer
        {
            Name = "TheftPolicy",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        _theftPolicyControls.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Xs);
        _preserveResourcesButton = CreateButton(SolverText.Get("保牌/保钱"), false);
        _preserveResourcesButton.CustomMinimumSize = new Vector2(112, SolverUiTokens.Size.ButtonHeight);
        _preserveResourcesButton.Pressed += () => OnTheftPolicyPressed(SolverTheftPolicy.PreserveResources);
        _theftPolicyControls.AddChild(_preserveResourcesButton);
        _letEscapeButton = CreateButton(SolverText.Get("放走"), false);
        _letEscapeButton.CustomMinimumSize = new Vector2(72, SolverUiTokens.Size.ButtonHeight);
        _letEscapeButton.Pressed += () => OnTheftPolicyPressed(SolverTheftPolicy.LetEscape);
        _theftPolicyControls.AddChild(_letEscapeButton);
        _body!.AddChild(_theftPolicyControls);
        _body.MoveChild(_theftPolicyControls, 2);

        _recalculateButton = CreateButton(SolverText.Get("重新计算"), false);
        _recalculateButton.CustomMinimumSize = new Vector2(112, SolverUiTokens.Size.ButtonHeight);
        _recalculateButton.Pressed += OnRecalculatePressed;

        _stopSearchButton = CreateButton(SolverText.Get("停止计算"), false);
        SolverUiTokens.ApplyButtonStyle(_stopSearchButton, SolverButtonStyle.Danger);
        _stopSearchButton.CustomMinimumSize = new Vector2(112, SolverUiTokens.Size.ButtonHeight);
        _stopSearchButton.Pressed += OnStopSearchPressed;

        _adoptRouteButton = CreateButton(SolverText.Get("采用当前路线"), false);
        _adoptRouteButton.CustomMinimumSize = new Vector2(132, SolverUiTokens.Size.ButtonHeight);
        _adoptRouteButton.Pressed += OnAdoptRoutePressed;
        _adoptRouteButton.TooltipText = SolverText.Get("结束搜索并采用屏幕当前路线；已开启的全自动或排队执行仍按原设置继续。");

        _executeButton = CreateButton(SolverText.Get("执行本回合"), false);
        _renderedExecuteButtonStyle = SolverButtonStyle.Secondary;
        _executeButton.CustomMinimumSize = new Vector2(132, SolverUiTokens.Size.ButtonHeight);
        _executeButton.Pressed += OnExecutePressed;

        _fullAutoButton = SolverUiTokens.CreateButton(SolverText.Get("全自动：关"), SolverButtonStyle.Secondary);
        _fullAutoButton.CustomMinimumSize = new Vector2(144, SolverUiTokens.Size.ButtonHeight);
        _fullAutoButton.TooltipText = SolverText.Get("控制本场自动续打。关闭后，正在执行的动作按原流程完成。");
        _fullAutoButton.Pressed += OnFullAutoPressed;

        HBoxContainer autoStart = new()
        {
            MouseFilter = Control.MouseFilterEnum.Pass,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            TooltipText = SolverText.Get("每场战斗开始时自动开启全自动。本场手动停止后保持停止，下场战斗再次开启。"),
        };
        autoStart.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Xs);
        autoStart.AddChild(CreateTextLabel(SolverText.Get("自动开启全自动"), SolverUiTokens.Type.Caption, TextPrimary));
        _autoEnableFullAutoSwitch = SolverSettingsPanel.CreateToggle();
        _autoEnableFullAutoSwitch.CustomMinimumSize = new Vector2(40, 24);
        _autoEnableFullAutoSwitch.TooltipText = autoStart.TooltipText;
        _autoEnableFullAutoSwitch.ButtonPressed = SolverSettings.Current.AutoEnableFullAuto;
        _autoEnableFullAutoSwitch.Toggled += enabled =>
        {
            if (SolverSettings.Current.AutoEnableFullAuto != enabled)
                SolverSettings.Update(SolverSettings.Current with { AutoEnableFullAuto = enabled });
        };
        autoStart.AddChild(_autoEnableFullAutoSwitch);

        _memoryUsageBar = new SolverMemoryUsageBar
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };

        _systemMemoryReleaseButton = SolverUiTokens.CreateButton(SolverText.Get("强制释放内存"), SolverButtonStyle.Secondary);
        _systemMemoryReleaseButton.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        _systemMemoryReleaseButton.TooltipText = SolverText.Get("等待搜索退出并回收求解器内存后，请求 Windows 管理员权限，")
            + SolverText.Get("清空系统工作集与待机列表。其他程序之后重新载入页面时可能短暂卡顿。");
        _systemMemoryReleaseButton.Pressed += OnSystemMemoryReleasePressed;

        _actionBar = new SolverActionBar(_executeButton, _recalculateButton, _stopSearchButton,
            _adoptRouteButton, _fullAutoButton, autoStart, _memoryUsageBar, _systemMemoryReleaseButton);
        return _actionBar;
    }

}

