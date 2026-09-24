namespace CombatSolver;

/// <summary>
/// Pure trust boundary for the quality-first multiplayer refresh slice. The old plan may be
/// replayed only when the new live root is identical or when every delta is durability-only on
/// an enemy that remained alive. Everything else is a strong refresh signal.
/// </summary>
internal static class MultiplayerPlanRefreshContracts
{
    internal static bool IsReplayCompatible(
        string previousStateText,
        string currentStateText,
        out string reason)
    {
        if (string.Equals(previousStateText, currentStateText, StringComparison.Ordinal))
        {
            reason = "exact_local_root";
            return true;
        }

        string[] previousFields = previousStateText.Split(';');
        string[] currentFields = currentStateText.Split(';');
        if (previousFields.Length != currentFields.Length)
        {
            reason = "field_count_changed";
            return false;
        }

        bool sawSoftEnemyDelta = false;
        for (int index = 0; index < previousFields.Length; index++)
        {
            string expectedField = previousFields[index];
            string actualField = currentFields[index];
            if (string.Equals(expectedField, actualField, StringComparison.Ordinal))
                continue;

            int expectedSeparator = expectedField.IndexOf('=');
            int actualSeparator = actualField.IndexOf('=');
            if (expectedSeparator <= 0
                || actualSeparator <= 0
                || !string.Equals(
                    expectedField[..expectedSeparator],
                    actualField[..actualSeparator],
                    StringComparison.Ordinal))
            {
                reason = "field_identity_changed";
                return false;
            }

            string fieldName = expectedField[..expectedSeparator];
            if (!fieldName.StartsWith('E')
                || !IsSoftLivingEnemyDurabilityDelta(
                    expectedField[(expectedSeparator + 1)..],
                    actualField[(actualSeparator + 1)..]))
            {
                reason = $"strong_field_change:{fieldName}";
                return false;
            }
            sawSoftEnemyDelta = true;
        }

        reason = sawSoftEnemyDelta
            ? "living_enemy_hp_or_block_only"
            : "exact_local_root";
        return true;
    }

    private static bool IsSoftLivingEnemyDurabilityDelta(
        string previousValue,
        string currentValue)
    {
        string[] previousParts = previousValue.Split('/');
        string[] currentParts = currentValue.Split('/');
        if (previousParts.Length != 7 || currentParts.Length != 7)
            return false;

        // combat-id, monster id, slot, max HP and move must stay identical.
        foreach (int fixedIndex in new[] { 0, 1, 2, 4, 6 })
        {
            if (!string.Equals(
                    previousParts[fixedIndex],
                    currentParts[fixedIndex],
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (!int.TryParse(previousParts[3], out int previousHp)
            || !int.TryParse(currentParts[3], out int currentHp)
            || !int.TryParse(previousParts[5], out _)
            || !int.TryParse(currentParts[5], out _))
        {
            return false;
        }

        // Crossing the alive/dead boundary can invalidate targets and lethal ordering.
        return previousHp > 0 && currentHp > 0;
    }
}
