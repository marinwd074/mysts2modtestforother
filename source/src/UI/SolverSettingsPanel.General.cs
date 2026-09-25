using System.Globalization;
using Godot;
using MegaCrit.Sts2.Core.Localization.Fonts;

namespace CombatSolver;

internal sealed partial class SolverSettingsPanel
{
    private CheckButton _solverEnabled = null!;
    private CheckButton _automaticCalculation = null!;
    private CheckButton _stopOnCombatEnd = null!;
    private CheckButton _stopOnDeathTurn = null!;
    private CheckButton _stopOnWorseRecalculation = null!;
    private OptionButton _actTransitionBossHpStrategy = null!;
    private OptionButton _finalBossHpStrategy = null!;
    private CheckButton _multiplayerPrediction = null!;
    private CheckButton _multiplayerLethalHpRecalculation = null!;
    private OptionButton _multiplayerCombatObjective = null!;
    private LineEdit _acceptableBattleHpLoss = null!;
    private OptionButton _searchCompletionNotificationPolicy = null!;
    private OptionButton _overlayTheme = null!;
    private HSlider _overlayOpacity = null!;
    private Label _overlayOpacityValue = null!;

    internal bool SearchCompletionNotificationSettingsConfiguredForTesting
        => _searchCompletionNotificationPolicy.GetItemId(
               _searchCompletionNotificationPolicy.Selected)
           == (int)ResolveSearchCompletionNotificationPolicy(SolverSettings.Current);

    internal bool VisualSettingsConfiguredForTesting
        => _overlayTheme.GetItemId(_overlayTheme.Selected) == (int)SolverSettings.Current.OverlayTheme
           && Math.Abs(_overlayOpacity.Value - SolverSettings.Current.OverlayOpacity) < 0.001d;

    internal bool BossHpStrategySettingsConfiguredForTesting
        => _actTransitionBossHpStrategy.GetItemId(_actTransitionBossHpStrategy.Selected)
               == (int)SolverSettings.Current.ActTransitionBossHpStrategy
           && _finalBossHpStrategy.GetItemId(_finalBossHpStrategy.Selected)
               == (int)SolverSettings.Current.FinalBossHpStrategy;

    internal bool ExerciseBossHpStrategySettingsForTesting()
    {
        SolverSettingsData original = SolverSettings.Current;
        try
        {
            SolverSettings.ApplyForTesting(SolverSettings.RoundTripForTesting(original with
            {
                ActTransitionBossHpStrategy = BossHpStrategy.MinimizeHpLoss,
                FinalBossHpStrategy = BossHpStrategy.ProgressionFirst,
            }));
            Reload();
            bool actTransitionIndependent = BossHpStrategySettingsConfiguredForTesting;

            SolverSettings.ApplyForTesting(SolverSettings.RoundTripForTesting(original with
            {
                ActTransitionBossHpStrategy = BossHpStrategy.ProgressionFirst,
                FinalBossHpStrategy = BossHpStrategy.MinimizeHpLoss,
            }));
            Reload();
            return actTransitionIndependent && BossHpStrategySettingsConfiguredForTesting;
        }
        finally
        {
            SolverSettings.ApplyForTesting(original);
            Reload();
        }
    }

    internal bool AcceptableBattleHpLossSettingsConfiguredForTesting
        => _acceptableBattleHpLoss.Text == SolverSettings.Current.AcceptableBattleHpLoss.ToString(CultureInfo.InvariantCulture);

    internal bool ExerciseThresholdOutsideClickForTesting()
    {
        SettingsPage previousPage = _activePage;
        if (!TrySelectPage(SettingsPage.Performance))
            throw new InvalidOperationException("Cannot open performance settings for threshold input.");
        try
        {
            _acceptableBattleHpLoss.GrabFocus();
            _acceptableBattleHpLoss.Text = "19";
            using InputEventMouseButton click = new() { Pressed = true, ButtonIndex = MouseButton.Left, Position = new Vector2(-1, -1) };
            _Input(click);
            return _performancePage.IsAncestorOf(_acceptableBattleHpLoss)
                && !_acceptableBattleHpLoss.HasFocus() && SolverSettings.Current.AcceptableBattleHpLoss == 19;
        }
        finally { TrySelectPage(previousPage); }
    }

    private LineEdit CreateAcceptableBattleHpLossInput()
    {
        LineEdit input = CreateInput("0");
        _reloadInputs.Add(data => input.Text = data.AcceptableBattleHpLoss.ToString(CultureInfo.InvariantCulture));
        bool Commit()
        {
            if (!int.TryParse(input.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                || value < 0 || value > SolverSettings.MaximumAcceptableBattleHpLoss)
            {
                ShowInvalid(input, SolverText.Format($"请输入 0–{SolverSettings.MaximumAcceptableBattleHpLoss} 的整数"));
                return false;
            }
            if (SolverSettings.Current.AcceptableBattleHpLoss == value)
                return KeepUnchanged(input);
            return SaveSetting(input, SolverSettings.Current with { AcceptableBattleHpLoss = value }, SolverText.Get("已保存，下次搜索生效"));
        }
        input.FocusExited += () => Commit();
        input.TextSubmitted += _ => Commit();
        _commitInputs.Add(Commit);
        return input;
    }

    internal bool ExerciseVisualSettingsForTesting()
    {
        SolverSettingsData original = SolverSettings.Current;
        try
        {
            SolverSettings.ApplyForTesting(original with
            {
                OverlayTheme = SolverOverlayTheme.Light,
                OverlayOpacity = 0.55f,
            });
            Reload();
            SolverOverlay.ApplyOverlayOpacity();
            return VisualSettingsConfiguredForTesting
                   && _overlayTheme.GetItemId(_overlayTheme.Selected) == (int)SolverOverlayTheme.Light
                   && _overlayOpacityValue.Text == "55%"
                   && Math.Abs(SolverOverlay.OverlayOpacityForTesting - 0.55f) < 0.001f;
        }
        finally
        {
            SolverSettings.ApplyForTesting(original);
            Reload();
            SolverOverlay.ApplyOverlayOpacity();
        }
    }

    internal bool ExerciseSearchCompletionNotificationPolicyForTesting()
    {
        SolverSettingsData original = SolverSettings.Current;
        try
        {
            SolverSettings.ApplyForTesting(original with
            {
                SearchCompletionNotificationsEnabled = false,
                SearchCompletionNotificationMode = SolverSearchCompletionNotificationMode.Always,
            });
            Reload();
            bool disabledLoaded = SelectedSearchCompletionNotificationPolicy()
                                  == SearchCompletionNotificationPolicy.Disabled;

            SolverSettings.ApplyForTesting(original with
            {
                SearchCompletionNotificationsEnabled = true,
                SearchCompletionNotificationMode =
                    SolverSearchCompletionNotificationMode.OnlyWhenGameInBackground,
            });
            Reload();
            bool backgroundLoaded = SelectedSearchCompletionNotificationPolicy()
                                    == SearchCompletionNotificationPolicy.BackgroundOnly;

            SolverSettings.ApplyForTesting(original with
            {
                SearchCompletionNotificationsEnabled = true,
                SearchCompletionNotificationMode = SolverSearchCompletionNotificationMode.Always,
            });
            Reload();
            bool alwaysLoaded = SelectedSearchCompletionNotificationPolicy()
                                == SearchCompletionNotificationPolicy.Always;
            return disabledLoaded && backgroundLoaded && alwaysLoaded;
        }
        finally
        {
            SolverSettings.ApplyForTesting(original);
            Reload();
        }
    }

    private Control CreateGeneralPage()
    {
        VBoxContainer content = CreatePageContent("GeneralSettingsPage");
        GridContainer solverGrid = CreateSettingsGrid();
        _solverEnabled = CreateToggle();
        _solverEnabled.Toggled += OnSolverEnabledToggled;
        AddBasicRow(solverGrid, SolverText.Get("启用求解器"), _solverEnabled);
        _automaticCalculation = CreateToggle();
        _automaticCalculation.Toggled += OnAutomaticCalculationToggled;
        AddBasicRow(
            solverGrid,
            SolverText.Get("自动计算"),
            _automaticCalculation,
            SolverText.Get("开启后会在进入战斗局面和每个玩家回合自动开始后台计算；关闭后由主面板手动开始计算。"));
        AddSettingsSection(content, SolverText.Get("开始计算"),
            SolverText.Get("控制求解器启停与自动计算。自动开启全自动可在主界面右侧设置。"), solverGrid);

        GridContainer bossStrategyGrid = CreateSettingsGrid();
        _actTransitionBossHpStrategy = CreateBossHpStrategyInput(
            data => data.ActTransitionBossHpStrategy,
            (data, strategy) => data with { ActTransitionBossHpStrategy = strategy });
        AddBasicRow(
            bossStrategyGrid,
            SolverText.Get("第一、二幕血量取舍"),
            _actTransitionBossHpStrategy,
            SolverText.Get("通关优先会按战后回复 80% 折算血量价值并尽量保留药水；最低战损会按普通战斗完整比较掉血。重新计算后生效。"));
        _finalBossHpStrategy = CreateBossHpStrategyInput(
            data => data.FinalBossHpStrategy,
            (data, strategy) => data with { FinalBossHpStrategy = strategy });
        AddBasicRow(
            bossStrategyGrid,
            SolverText.Get("最终 Boss 血量取舍"),
            _finalBossHpStrategy,
            SolverText.Get("通关优先只要求路线存活并优先保留资源；最低战损会继续比较剩余血量。重新计算后生效。"));

        GridContainer executionGrid = CreateSettingsGrid();
        _stopOnCombatEnd = CreateToggle();
        _stopOnCombatEnd.Toggled += OnStopOnCombatEndToggled;
        AddBasicRow(executionGrid, SolverText.Get("预计结束战斗时暂停"), _stopOnCombatEnd);
        _stopOnDeathTurn = CreateToggle();
        _stopOnDeathTurn.Toggled += OnStopOnDeathTurnToggled;
        AddBasicRow(executionGrid, SolverText.Get("死亡回合时暂停"), _stopOnDeathTurn);
        _stopOnWorseRecalculation = CreateToggle();
        _stopOnWorseRecalculation.Toggled += OnStopOnWorseRecalculationToggled;
        AddBasicRow(executionGrid, SolverText.Get("重算后战损增加时暂停"), _stopOnWorseRecalculation);
        GridContainer speedGrid = CreateSettingsGrid();
        AddBasicRow(speedGrid, SolverText.Get("自动出牌速度"), CreateDeploymentFastModeInput());
        AddBasicRow(speedGrid, SolverText.Get("牌间额外停顿（秒）"), CreateOptionalDoubleInput(
            0d,
            data => data.DeploymentInterActionDelaySeconds,
            (data, value) => data with { DeploymentInterActionDelaySeconds = value },
            0d,
            3d));
        AddSettingsSection(content, SolverText.Get("出牌速度"), SolverText.Get("调整自动执行的节奏，下次执行生效。"), speedGrid);
        AddSettingsSection(content, SolverText.Get("自动执行的暂停条件"),
            SolverText.Get("选择哪些情况下暂停自动执行并交还操作权。"), executionGrid);
        AddSettingsSection(content, SolverText.Get("幕末 Boss"),
            SolverText.Get("分别设置幕末战斗的血量取舍，重新计算后生效。"), bossStrategyGrid);

        GridContainer multiplayerObjectiveGrid = CreateSettingsGrid();
        _multiplayerPrediction = CreateToggle();
        _multiplayerPrediction.Toggled += OnMultiplayerPredictionToggled;
        AddBasicRow(
            multiplayerObjectiveGrid,
            SolverText.Get("启用多人专用搜索算法（实验）"),
            _multiplayerPrediction,
            SolverText.Get("默认关闭：多人使用单人完整路线搜索、排序与剪枝，只保留多人运行和执行边界。开启后才启用多人路线语义、团队目标、队友预测、Scenario/Robust 复评和 Carry 排序。"));
        _multiplayerLethalHpRecalculation = CreateToggle();
        _multiplayerLethalHpRecalculation.Toggled += OnMultiplayerLethalHpRecalculationToggled;
        AddBasicRow(
            multiplayerObjectiveGrid,
            SolverText.Get("斩杀线敌人血量变化时重算（实验）"),
            _multiplayerLethalHpRecalculation,
            SolverText.Get("默认关闭。前期敌人掉血不打断现有单人最优路线；进入项目现有斩杀窗口后，敌人 HP 再发生变化才触发路线重算。该开关不启用团队目标、队友预测或 Robust。"));
        _multiplayerCombatObjective = CreateMultiplayerCombatObjectiveInput();
        AddBasicRow(
            multiplayerObjectiveGrid,
            SolverText.Get("多人路线目标"),
            _multiplayerCombatObjective,
            SolverText.Get("仅在启用多人预测算法后生效。最低团队战损始终优先保血；动态斩杀会综合提前结束回合数与团队战损选择路线。"));
        AddSettingsSection(
            content,
            SolverText.Get("多人模式"),
            SolverText.Get("默认多人算法与单人搜索核心一致；所有多人专用搜索算法都必须通过上方总开关显式开启。单人搜索不读取这些设置。"),
            multiplayerObjectiveGrid);

        GridContainer interfaceGrid = CreateSettingsGrid();
        _overlayTheme = CreateOverlayThemeInput();
        AddBasicRow(
            interfaceGrid,
            SolverText.Get("界面主题"),
            _overlayTheme,
            SolverText.Get("深色为默认主题；切换后会重建当前覆盖层，并保留最近的路线与设置页面。"));
        AddBasicRow(
            interfaceGrid,
            SolverText.Get("覆盖层透明度"),
            CreateOverlayOpacityInput(),
            SolverText.Get("调整整个求解器覆盖层的透明度，范围为 25%–100%，立即生效。"));
        _searchCompletionNotificationPolicy = CreateSearchCompletionNotificationPolicyInput();
        AddBasicRow(interfaceGrid, SolverText.Get("搜索结束通知"), _searchCompletionNotificationPolicy,
            SolverText.Get("搜索成功、失败、停止或结果过期时发送 Windows 系统通知和提示音。可关闭、仅在游戏不处于前台时通知，或始终通知；其他平台不会调用 Windows 接口。"));
        AddSettingsSection(content, SolverText.Get("显示与通知"),
            SolverText.Get("设置主题、透明度与系统通知。"), interfaceGrid);
        return CreatePageScroll(content);
    }

    private void ReloadGeneralPage(SolverSettingsData data)
    {
        _solverEnabled.ButtonPressed = !data.SolverDisabled;
        _automaticCalculation.ButtonPressed = data.AutomaticCalculationEnabled;
        _stopOnCombatEnd.ButtonPressed = data.StopFullAutoOnCombatEnd;
        _stopOnDeathTurn.ButtonPressed = data.StopFullAutoOnDeathTurn;
        _stopOnWorseRecalculation.ButtonPressed = data.StopFullAutoOnWorseRecalculation;
        _multiplayerPrediction.ButtonPressed = data.UseMultiplayerPrediction;
        _multiplayerLethalHpRecalculation.ButtonPressed = data.UseMultiplayerLethalHpRecalculation;
        _multiplayerCombatObjective.Disabled = !data.UseMultiplayerPrediction;
    }

    private OptionButton CreateSearchCompletionNotificationPolicyInput()
    {
        OptionButton input = CreateOptionInput(260);
        input.AddItem(SolverText.Get("关闭"), (int)SearchCompletionNotificationPolicy.Disabled);
        input.AddItem(SolverText.Get("仅游戏不在前台（默认）"), (int)SearchCompletionNotificationPolicy.BackgroundOnly);
        input.AddItem(SolverText.Get("始终通知"), (int)SearchCompletionNotificationPolicy.Always);
        _reloadInputs.Add(data => input.Selected = input.GetItemIndex(
            (int)ResolveSearchCompletionNotificationPolicy(data)));
        input.ItemSelected += index =>
        {
            if (_loading)
                return;
            SearchCompletionNotificationPolicy policy =
                (SearchCompletionNotificationPolicy)input.GetItemId((int)index);
            SolverSettings.Update(SolverSettings.Current with
            {
                SearchCompletionNotificationsEnabled = policy != SearchCompletionNotificationPolicy.Disabled,
                SearchCompletionNotificationMode = policy == SearchCompletionNotificationPolicy.Always
                    ? SolverSearchCompletionNotificationMode.Always
                    : SolverSearchCompletionNotificationMode.OnlyWhenGameInBackground,
            });
            SetStatus(SolverText.Get("已保存并立即生效"), SolverUiTokens.Palette.Success);
        };
        return input;
    }

    private OptionButton CreateBossHpStrategyInput(
        Func<SolverSettingsData, BossHpStrategy> read,
        Func<SolverSettingsData, BossHpStrategy, SolverSettingsData> write)
    {
        OptionButton input = CreateOptionInput(260);
        input.AddItem(SolverText.Get("通关优先（默认）"), (int)BossHpStrategy.ProgressionFirst);
        input.AddItem(SolverText.Get("最低战损"), (int)BossHpStrategy.MinimizeHpLoss);
        _reloadInputs.Add(data => input.Selected = input.GetItemIndex((int)read(data)));
        input.ItemSelected += index =>
        {
            if (_loading)
                return;
            BossHpStrategy strategy = (BossHpStrategy)input.GetItemId((int)index);
            SolverSettings.Update(write(SolverSettings.Current, strategy));
            SolverOverlay.RefreshBossHpStrategyHint();
            SetStatus(SolverText.Get("已保存，重新计算后生效"), SolverUiTokens.Palette.Success);
        };
        return input;
    }

    private OptionButton CreateMultiplayerCombatObjectiveInput()
    {
        OptionButton input = CreateOptionInput(260);
        input.AddItem(
            SolverText.Get("最低团队战损"),
            (int)MultiplayerCombatObjectiveStrategy.MinimizeTeamLoss);
        input.AddItem(
            SolverText.Get("动态斩杀（推荐）"),
            (int)MultiplayerCombatObjectiveStrategy.AdaptiveLethalTempo);
        _reloadInputs.Add(data => input.Selected = input.GetItemIndex(
            (int)data.MultiplayerCombatObjectiveStrategy));
        input.ItemSelected += index =>
        {
            if (_loading)
                return;
            MultiplayerCombatObjectiveStrategy strategy =
                (MultiplayerCombatObjectiveStrategy)input.GetItemId((int)index);
            SolverSettings.Update(SolverSettings.Current with
            {
                MultiplayerCombatObjectiveStrategy = strategy,
            });
            SetStatus(SolverText.Get("多人路线目标已保存，重新计算后生效"), SolverUiTokens.Palette.Success);
        };
        return input;
    }

    private OptionButton CreateDeploymentFastModeInput()
    {
        OptionButton input = CreateOptionInput();
        input.AddItem(SolverText.Get("跟随游戏（默认）"), (int)SolverDeploymentFastMode.FollowGame);
        input.AddItem(SolverText.Get("正常"), (int)SolverDeploymentFastMode.Normal);
        input.AddItem(SolverText.Get("快速"), (int)SolverDeploymentFastMode.Fast);
        input.AddItem(SolverText.Get("瞬间"), (int)SolverDeploymentFastMode.Instant);
        _reloadInputs.Add(data => input.Selected = input.GetItemIndex((int)data.DeploymentFastMode));
        input.ItemSelected += index =>
        {
            if (_loading)
                return;
            SolverDeploymentFastMode mode = (SolverDeploymentFastMode)input.GetItemId((int)index);
            SolverSettings.Update(SolverSettings.Current with { DeploymentFastMode = mode });
            SetStatus(SolverText.Get("已保存，下次执行生效"), SolverUiTokens.Palette.Success);
        };
        return input;
    }

    private OptionButton CreateOverlayThemeInput()
    {
        OptionButton input = CreateOptionInput();
        input.AddItem(SolverText.Get("深色（默认）"), (int)SolverOverlayTheme.Dark);
        input.AddItem(SolverText.Get("浅色"), (int)SolverOverlayTheme.Light);
        _reloadInputs.Add(data => input.Selected = input.GetItemIndex((int)data.OverlayTheme));
        input.ItemSelected += index =>
        {
            if (_loading)
                return;
            SolverOverlayTheme theme = (SolverOverlayTheme)input.GetItemId((int)index);
            SolverSettings.Update(SolverSettings.Current with { OverlayTheme = theme });
            SetStatus(SolverText.Get("界面主题已保存并应用"), SolverUiTokens.Palette.Success);
            SolverOverlay.ApplyConfiguredTheme();
        };
        return input;
    }

    private Control CreateOverlayOpacityInput()
    {
        HBoxContainer row = new()
        {
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        row.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        _overlayOpacity = new HSlider
        {
            MinValue = 0.25,
            MaxValue = 1d,
            Step = 0.05,
            FocusMode = FocusModeEnum.None,
            MouseDefaultCursorShape = CursorShape.PointingHand,
            CustomMinimumSize = new Vector2(220, 24),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        StyleSlider(_overlayOpacity);
        _overlayOpacityValue = SolverUiTokens.CreateLabel(
            "100%",
            SolverUiTokens.Type.Body,
            SolverUiTokens.Palette.TextPrimary,
            FontType.Bold);
        _overlayOpacityValue.HorizontalAlignment = HorizontalAlignment.Right;
        _overlayOpacityValue.CustomMinimumSize = new Vector2(48, 24);
        _reloadInputs.Add(data =>
        {
            _overlayOpacity.SetValueNoSignal(data.OverlayOpacity);
            _overlayOpacityValue.Text = $"{Math.Round(data.OverlayOpacity * 100d)}%";
        });
        _overlayOpacity.ValueChanged += value =>
        {
            _overlayOpacityValue.Text = $"{Math.Round(value * 100d)}%";
            if (_loading)
                return;
            SolverSettings.Update(SolverSettings.Current with { OverlayOpacity = (float)value });
            SolverOverlay.ApplyOverlayOpacity();
            SetStatus(SolverText.Get("透明度已保存并立即生效"), SolverUiTokens.Palette.Success);
        };
        row.AddChild(_overlayOpacity);
        row.AddChild(_overlayOpacityValue);
        return row;
    }

    private void OnSolverEnabledToggled(bool enabled)
    {
        if (_loading)
            return;
        SolverController.SetSolverDisabled(!enabled);
        SetStatus(enabled ? SolverText.Get("求解器已启用") : SolverText.Get("求解器已暂停"), SolverUiTokens.Palette.Success);
    }

    private void OnAutomaticCalculationToggled(bool enabled)
    {
        if (_loading)
            return;
        SolverController.SetAutomaticCalculationEnabled(enabled);
        SetStatus(
            enabled ? SolverText.Get("自动计算已开启") : SolverText.Get("自动计算已关闭"),
            SolverUiTokens.Palette.Success);
    }

    private void OnStopOnCombatEndToggled(bool enabled)
    {
        if (_loading)
            return;
        SolverController.SetStopFullAutoOnCombatEnd(enabled);
        SetStatus(SolverText.Get("已保存并立即生效"), SolverUiTokens.Palette.Success);
    }

    private void OnStopOnDeathTurnToggled(bool enabled)
    {
        if (_loading)
            return;
        SolverController.SetStopFullAutoOnDeathTurn(enabled);
        SetStatus(SolverText.Get("已保存并立即生效"), SolverUiTokens.Palette.Success);
    }

    private void OnStopOnWorseRecalculationToggled(bool enabled)
    {
        if (_loading)
            return;
        SolverController.SetStopFullAutoOnWorseRecalculation(enabled);
        SetStatus(SolverText.Get("已保存并立即生效"), SolverUiTokens.Palette.Success);
    }

    private void OnMultiplayerPredictionToggled(bool enabled)
    {
        if (_loading)
            return;
        SolverSettings.Update(SolverSettings.Current with { UseMultiplayerPrediction = enabled });
        _multiplayerCombatObjective.Disabled = !enabled;
        SetStatus(
            SolverText.Get(enabled
                ? "多人专用搜索算法已开启，重新计算后生效"
                : "多人专用搜索算法已关闭，重新计算后使用单人搜索核心"),
            SolverUiTokens.Palette.Success);
    }

    private void OnMultiplayerLethalHpRecalculationToggled(bool enabled)
    {
        if (_loading)
            return;
        SolverSettings.Update(SolverSettings.Current with
        {
            UseMultiplayerLethalHpRecalculation = enabled,
        });
        SetStatus(
            SolverText.Get(enabled
                ? "斩杀线血量重算已开启"
                : "斩杀线血量重算已关闭，敌人掉血不会主动打断路线"),
            SolverUiTokens.Palette.Success);
    }

    private static SearchCompletionNotificationPolicy ResolveSearchCompletionNotificationPolicy(
        SolverSettingsData data)
    {
        if (!data.SearchCompletionNotificationsEnabled)
            return SearchCompletionNotificationPolicy.Disabled;
        return data.SearchCompletionNotificationMode == SolverSearchCompletionNotificationMode.Always
            ? SearchCompletionNotificationPolicy.Always
            : SearchCompletionNotificationPolicy.BackgroundOnly;
    }

    private SearchCompletionNotificationPolicy SelectedSearchCompletionNotificationPolicy()
        => (SearchCompletionNotificationPolicy)_searchCompletionNotificationPolicy.GetItemId(
            _searchCompletionNotificationPolicy.Selected);

    private enum SearchCompletionNotificationPolicy
    {
        Disabled,
        BackgroundOnly,
        Always,
    }
}
