using TracePrompt.Models;

namespace TracePrompt.Services;

/// <summary>
/// 選択済みコマを「何枚の結合画像に、どの順で分割するか」だけを決める純粋ロジックです。
/// 画像の読み込み・描画は一切行いません（それは <see cref="ConsultationImageService"/> の仕事）。
/// </summary>
public sealed class ConsultationSplitService
{
    /// <summary>グループ内カード間の想定スペース（高さ見積り用のおおよその値）。</summary>
    private const double AssumedBlockSpacing = 12;

    /// <summary>
    /// 自動一覧化モード用の既定ルールです。
    /// 1〜4枚はすべて1枚に、5〜8枚は4枚ずつ、9枚以上は6枚ずつに分割します。
    /// </summary>
    public static int ResolveAutoFramesPerImage(int totalCount)
    {
        if (totalCount <= 4) return int.MaxValue;
        if (totalCount <= 8) return 4;
        return 6;
    }

    /// <summary>
    /// コマの縦横比の平均から、1列／2列のどちらが向くかを判定します。
    /// 横長寄り（幅/高さ が概ね1.2以上）なら1列、縦長寄りなら2列を提案します。
    /// </summary>
    public static int ResolveAutoColumns(
        IReadOnlyList<ConsultationTimelineEntry> entries,
        Func<ConsultationTimelineEntry, double?> estimateAspectRatio)
    {
        if (entries.Count == 0)
        {
            return 1;
        }

        double total = 0;
        int counted = 0;
        foreach (ConsultationTimelineEntry entry in entries)
        {
            double? ratio = estimateAspectRatio(entry);
            if (ratio is not double r || r <= 0)
            {
                continue;
            }

            total += r;
            counted++;
        }

        if (counted == 0)
        {
            return 1;
        }

        double average = total / counted;
        return average >= 1.2 ? 1 : 2;
    }

    /// <summary>
    /// 先頭から貪欲にグループへ詰めます。「件数が maxPerGroup に達した」か
    /// 「現在のグループの累積高さが heightBudget を超える」のどちらか早い方でグループを閉じ、
    /// 次の画像へ回します（＝キャンバス全体の再縮小ではなく、画像を増やして解像度を守ります）。
    /// </summary>
    public static List<List<ConsultationTimelineEntry>> SplitIntoGroups(
        IReadOnlyList<ConsultationTimelineEntry> entries,
        int maxPerGroup,
        Func<ConsultationTimelineEntry, double?> estimateHeight,
        double heightBudget)
    {
        var groups = new List<List<ConsultationTimelineEntry>>();
        if (entries.Count == 0)
        {
            return groups;
        }

        int effectiveMaxPerGroup = Math.Max(1, maxPerGroup);
        var current = new List<ConsultationTimelineEntry>();
        double currentHeight = 0;

        foreach (ConsultationTimelineEntry entry in entries)
        {
            double panelHeight = estimateHeight(entry) ?? 0;
            double addedHeight = current.Count == 0 ? panelHeight : panelHeight + AssumedBlockSpacing;

            bool wouldExceedCount = current.Count >= effectiveMaxPerGroup;
            bool wouldExceedHeight = current.Count > 0 && currentHeight + addedHeight > heightBudget;

            if (wouldExceedCount || wouldExceedHeight)
            {
                groups.Add(current);
                current = new List<ConsultationTimelineEntry>();
                currentHeight = 0;
                addedHeight = panelHeight;
            }

            current.Add(entry);
            currentHeight += addedHeight;
        }

        if (current.Count > 0)
        {
            groups.Add(current);
        }

        return groups;
    }
}
