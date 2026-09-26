using System.Text;
using MegaCrit.Sts2.Core.Combat;

namespace CombatSolver;

/// <summary>
/// 搜索开始时的完整可见状态文本，用于拒绝已经过期的后台结果；不做摘要或哈希。
/// </summary>
internal sealed record LiveCombatStamp(string StateText)
{
    public static LiveCombatStamp Capture(CombatState state)
        => new(ContinuationStamp.CaptureLive(state).StateText);

    public static LiveCombatStamp CaptureLocalCoreSearchValidity(CombatState state)
        => new(NormalizeLocalCoreSearchValidity(
            ContinuationStamp.CaptureLive(state).StateText));

    private static string NormalizeLocalCoreSearchValidity(string stateText)
    {
        string[] fields = stateText.Split(';');
        StringBuilder normalized = new(stateText.Length);
        bool first = true;
        for (int index = 0; index < fields.Length; index++)
        {
            string field = fields[index];
            int separator = field.IndexOf('=');
            string name = separator < 0 ? field : field[..separator];
            if (IsSharedMutableSearchField(name))
                continue;
            if (!first)
                normalized.Append(';');
            normalized.Append(field);
            first = false;
        }
        return normalized.ToString();
    }

    private static bool IsSharedMutableSearchField(string name)
        => name is "P" or "R"
            || IsIndexedField(name, "E")
            || IsIndexedField(name, "AI")
            || IsIndexedField(name, "MS");

    private static bool IsIndexedField(string name, string prefix)
    {
        if (!name.StartsWith(prefix, StringComparison.Ordinal)
            || name.Length == prefix.Length)
        {
            return false;
        }
        for (int index = prefix.Length; index < name.Length; index++)
        {
            if (!char.IsAsciiDigit(name[index]))
                return false;
        }
        return true;
    }

    public static LiveCombatStamp FromContinuation(ContinuationStamp continuation)
        => new(continuation.StateText);
}
