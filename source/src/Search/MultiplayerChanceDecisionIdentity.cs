using System.Text;

namespace CombatSolver;

/// <summary>
/// Shared identity for the deployable local decision that precedes the first current-turn
/// Joint Shadow outcome. Forecast metadata and post-EndTurn turn-start choices are deliberately
/// excluded: they are observations after the local decision, not actions the local player chose.
/// </summary>
internal static class MultiplayerChanceDecisionIdentity
{
    internal static bool TryGetCurrentTurnShadowOutcome(
        SearchNode candidate,
        int rootTurn,
        out SearchNode outcomeNode,
        out ShadowForecastPlan forecast)
    {
        SearchNode? current = candidate;
        while (current?.Parent != null)
        {
            PlanAction? action = current.Action;
            if (action is
                {
                    Kind: PlanActionKind.EndTurn,
                    ShadowForecast: not null
                }
                && action.Turn == rootTurn)
            {
                outcomeNode = current;
                forecast = action.ShadowForecast!;
                return true;
            }
            current = current.Parent;
        }

        outcomeNode = null!;
        forecast = null!;
        return false;
    }

    internal static string CurrentTurnDecisionKey(
        SearchNode candidate,
        int rootTurn)
        => CurrentTurnDecisionKey(candidate.Actions, rootTurn);

    internal static string CurrentTurnDecisionKey(
        IReadOnlyList<PlanAction> actions,
        int rootTurn)
    {
        StringBuilder key = new();
        foreach (PlanAction action in actions)
        {
            if (action.Turn != rootTurn)
                continue;

            // U5 teammate forecasts are observations, not deployable choices. Everything after
            // the first forecast boundary is contingent on a state that must be observed and
            // replanned before another local action may be authorized.
            if (action.Kind == PlanActionKind.TeammateForecast)
                break;

            AppendDecisionAction(key, action);
            if (action.Kind == PlanActionKind.EndTurn)
                break;
        }
        return key.ToString();
    }

    private static void AppendDecisionAction(
        StringBuilder key,
        PlanAction action)
    {
        key.Append((int)action.Kind).Append(':')
            .Append(action.CardId).Append(':')
            .Append(action.CardOccurrence).Append(':')
            .Append(action.CardStateKey).Append(':')
            .Append(action.CardStateOccurrence).Append(':')
            .Append(action.CardUpgradeLevel).Append(':')
            .Append(action.CardEnchantmentId).Append(':')
            .Append(action.TargetCombatId?.ToString() ?? "-").Append(':')
            .Append(action.PotionSlot).Append(':')
            .Append(action.PotionId).Append(':')
            .Append(action.EndsPlayerTurn ? '1' : '0').Append('|');
        AppendDecisionChoice(key, action.Choice);
        if (action.NestedChoices != null)
        {
            key.Append("N").Append(action.NestedChoicesBeforePrimary).Append('[');
            foreach (PlanCardChoice choice in action.NestedChoices)
                AppendDecisionChoice(key, choice);
            key.Append(']');
        }

        // TurnStartChoices belong to the state reached after EndTurn. ShadowForecast is also
        // forecast-only metadata. Neither can distinguish the deployable current-turn choice.
        key.Append(';');
    }

    private static void AppendDecisionChoice(
        StringBuilder key,
        PlanCardChoice? choice)
    {
        if (choice == null)
        {
            key.Append('-');
            return;
        }

        key.Append((int)choice.Effect).Append(':')
            .Append((int)choice.SourcePile).Append(':')
            .Append(choice.SourceId).Append(':')
            .Append(choice.ContextId).Append(':')
            .Append((int)choice.Timing).Append('[');
        foreach (PlanCardToken card in choice.Cards)
        {
            key.Append(card.CardId).Append(':')
                .Append(card.UpgradeLevel).Append(':')
                .Append(card.StateKey).Append(':')
                .Append(card.SourceOccurrence).Append(':')
                .Append(card.OptionOccurrence).Append(',');
        }
        key.Append(']');
    }
}
