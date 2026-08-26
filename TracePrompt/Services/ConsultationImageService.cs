using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TracePrompt.Models;
using Loc = TracePrompt.Localization.LocalizationManager;

namespace TracePrompt.Services;

/// <summary>
/// 定期キャプチャを時系列で並べ、各コマに紐づく操作を注釈する相談用画像を作ります。
/// 既定は縦一列レイアウトですが、2列レイアウト・画質プリセットにも対応します。
/// </summary>
public sealed class ConsultationImageService
{
    // JPEG圧縮に切り替えたことでファイルサイズは圧縮側で吸収できるため、
    // 解像度の上限は画質優先でPNG時代より緩めに設定しています。
    public const int DefaultMaxScreenshotWidth = 960;

    /// <summary>
    /// 1枚の結合画像が許容する高さの目安です。<see cref="ConsultationSplitService"/> が
    /// これを超えないよう事前にグループを分割するため、通常はここでの緊急縮小は発生しません。
    /// </summary>
    public const double SoftMaxTotalHeight = 14000;

    private const int JpegQuality = 90;

    private const double OuterPadding = 14;
    private const double HeaderBottomMargin = 6;
    private const double CardPadding = 10;
    private const double ColumnGutter = 16;

    private static readonly Typeface JapaneseTypeface = new(
        new FontFamily("Yu Gothic UI, Meiryo UI, MS UI Gothic, Segoe UI"),
        FontStyles.Normal,
        FontWeights.SemiBold,
        FontStretches.Normal);

    private static readonly Typeface JapaneseTypefaceRegular = new(
        new FontFamily("Yu Gothic UI, Meiryo UI, MS UI Gothic, Segoe UI"),
        FontStyles.Normal,
        FontWeights.Normal,
        FontStretches.Normal);

    /// <summary>
    /// 既存呼び出し互換の1列・標準画質オーバーロードです（自動結合フローはこちらを使い続けます）。
    /// timeline は古い順です。各エントリの Frame 画像に LinkedActions を描画します。
    /// outOfRegionActions（キャプチャ範囲外での操作）は、撮れる画像が無いためテキストのみのカードとして
    /// 時系列上の正しい位置に挟み込まれます。
    /// </summary>
    public bool TryBuildConsultationImage(
        IReadOnlyList<ConsultationTimelineEntry> timeline,
        IReadOnlyList<RecordedAction> outOfRegionActions,
        string outputPath,
        out string? errorMessage)
        => TryBuildConsultationImage(
            timeline,
            outOfRegionActions,
            outputPath,
            columns: 1,
            ConsultationQualityPreset.Standard,
            out errorMessage);

    /// <summary>
    /// 手動選択・分割向けの拡張版です。columns は 1 以上を指定してください
    /// （Auto 判定は呼び出し側であらかじめ列数へ解決してから渡してください）。
    /// </summary>
    public bool TryBuildConsultationImage(
        IReadOnlyList<ConsultationTimelineEntry> timeline,
        IReadOnlyList<RecordedAction> outOfRegionActions,
        string outputPath,
        int columns,
        ConsultationQualityPreset quality,
        out string? errorMessage)
    {
        errorMessage = null;

        outOfRegionActions ??= Array.Empty<RecordedAction>();
        bool hasOutOfRegion = outOfRegionActions.Count > 0;

        if ((timeline is null || timeline.Count == 0) && !hasOutOfRegion)
        {
            errorMessage = Loc.Instance.Get("Consultation_Error_NoCaptures");
            return false;
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            errorMessage = Loc.Instance.Get("Error_NoOutputPath");
            return false;
        }

        try
        {
            var usable = (timeline ?? Array.Empty<ConsultationTimelineEntry>())
                .Where(t => !string.IsNullOrWhiteSpace(t.Frame.ScreenshotPath)
                            && File.Exists(t.Frame.ScreenshotPath))
                .ToList();

            if (usable.Count == 0 && !hasOutOfRegion)
            {
                errorMessage = Loc.Instance.Get("Consultation_Error_NoUsableFrames");
                return false;
            }

            (double widthMultiplier, int jpegQuality) = ResolveQualitySettings(quality);
            int maxWidth = ResolveMaxWidth(usable.Count, widthMultiplier);

            List<PanelLayout>? panels = MeasurePanels(usable, outOfRegionActions, maxWidth, out errorMessage);
            if (panels is null)
            {
                return false;
            }

            int effectiveColumns = Math.Clamp(columns, 1, Math.Max(1, panels.Count));
            return RenderColumns(panels, effectiveColumns, jpegQuality, outputPath, out errorMessage);
        }
        catch (Exception ex)
        {
            errorMessage = Loc.Instance.Format("Consultation_Error_GenerationFailed_Format", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 画像の縦横比（幅÷高さ）です。読み込みに失敗した場合は null を返します。
    /// 自動レイアウト判定（<see cref="ConsultationSplitService.ResolveAutoColumns"/>）から使います。
    /// </summary>
    public double? TryGetAspectRatio(string screenshotPath)
    {
        BitmapSource? source = LoadBitmapWithoutLock(screenshotPath);
        if (source is null || source.PixelHeight <= 0)
        {
            return null;
        }

        return (double)source.PixelWidth / source.PixelHeight;
    }

    /// <summary>
    /// 分割単位を決めるための概算高さ（見出し＋画像＋余白、カード間スペースは含まず）です。
    /// 読み込みに失敗した場合は null を返します。<see cref="ConsultationSplitService"/> から使います。
    /// </summary>
    public double? EstimatePanelHeight(ConsultationTimelineEntry entry, int maxWidth)
    {
        BitmapSource? source = LoadBitmapWithoutLock(entry.Frame.ScreenshotPath);
        if (source is null)
        {
            return null;
        }

        double scale = source.PixelWidth > maxWidth ? (double)maxWidth / source.PixelWidth : 1.0;
        int drawWidth = Math.Max(1, (int)Math.Round(source.PixelWidth * scale));
        int drawHeight = Math.Max(1, (int)Math.Round(source.PixelHeight * scale));

        string title = Loc.Instance.Format("Consultation_FrameTitle_Single_Format", entry.Frame.RecordedAt);
        string detail = entry.HasActions
            ? string.Join(" ／ ", entry.LinkedActions.Select(a => a.ToConsultationDetail()))
            : Loc.Instance.Get("Consultation_ScreenDisplay");

        FormattedText titleText = CreateFormattedText(title, 16, Brushes.Black, bold: true);
        FormattedText detailText = CreateFormattedText(detail, 12, Brushes.Black, bold: false);
        detailText.MaxTextWidth = drawWidth;
        double headerHeight = titleText.Height + 2 + detailText.Height;

        return CardPadding + headerHeight + HeaderBottomMargin + drawHeight + CardPadding;
    }

    /// <summary>
    /// 各エントリの画像を読み込み、描画サイズ・見出しを測定します。
    /// キャプチャ範囲外での操作（画像なし）は、テキストのみのカードとして時刻順に混ぜ込みます。
    /// </summary>
    private List<PanelLayout>? MeasurePanels(
        List<ConsultationTimelineEntry> usable,
        IReadOnlyList<RecordedAction> outOfRegionActions,
        int maxWidth,
        out string? errorMessage)
    {
        errorMessage = null;
        var timed = new List<(DateTime At, PanelLayout Panel)>(usable.Count + outOfRegionActions.Count);

        for (int i = 0; i < usable.Count; i++)
        {
            ConsultationTimelineEntry entry = usable[i];
            BitmapSource? source = LoadBitmapWithoutLock(entry.Frame.ScreenshotPath);
            if (source is null)
            {
                errorMessage = Loc.Instance.Format("Consultation_Error_FrameLoadFailed_Format", i + 1);
                return null;
            }

            double scale = source.PixelWidth > maxWidth
                ? (double)maxWidth / source.PixelWidth
                : 1.0;
            int drawWidth = Math.Max(1, (int)Math.Round(source.PixelWidth * scale));
            int drawHeight = Math.Max(1, (int)Math.Round(source.PixelHeight * scale));

            string title = Loc.Instance.Format("Consultation_FrameTitle_Multi_Format", i + 1, usable.Count, entry.Frame.RecordedAt);
            string detail = entry.HasActions
                ? string.Join(" ／ ", entry.LinkedActions.Select(a => a.ToConsultationDetail()))
                : (i == 0 ? Loc.Instance.Get("Consultation_ScreenDisplay") : Loc.Instance.Get("Consultation_ScreenChange"));

            FormattedText titleText = CreateFormattedText(title, 16, Brushes.Black, bold: true);
            FormattedText detailText = CreateFormattedText(
                detail,
                12,
                new SolidColorBrush(Color.FromRgb(50, 60, 70)),
                bold: false);
            // 操作記録が長い（連続クリック等を結合した）場合でも、キャプチャ画像の幅を超えて
            // カードごと横に広がらないよう、画像の描画幅で折り返します。
            detailText.MaxTextWidth = drawWidth;
            double headerHeight = titleText.Height + 2 + detailText.Height;

            timed.Add((entry.Frame.RecordedAt, new PanelLayout(entry, source, drawWidth, drawHeight, titleText, detailText, headerHeight)));
        }

        foreach (RecordedAction action in outOfRegionActions)
        {
            string title = $"{action.NoImageReasonLabel}　{action.RecordedAt:HH:mm:ss.fff}";
            // ここに来る action は必ず MainViewModel.CollapseOutOfRegionActionsToSingleCard を通った
            // 結合済みの1件で、項目ごとの種類ラベルは Summary 側に既に埋め込まれているため、
            // ここで TypeLabel を重ねて付けません。
            string detail = action.Summary;

            FormattedText titleText = CreateFormattedText(title, 16, Brushes.Black, bold: true);
            FormattedText detailText = CreateFormattedText(
                detail,
                12,
                new SolidColorBrush(Color.FromRgb(50, 60, 70)),
                bold: false);
            // 画像を伴わないカードにも、他のカードと横幅がそろうよう同じ上限幅で折り返します。
            detailText.MaxTextWidth = maxWidth;
            double headerHeight = titleText.Height + 2 + detailText.Height;

            timed.Add((action.RecordedAt, new PanelLayout(titleText, detailText, headerHeight)));
        }

        return timed.OrderBy(t => t.At).Select(t => t.Panel).ToList();
    }

    /// <summary>
    /// columnCount 列のレイアウトで描画します（1列なら縦一列、2列以上なら各パネルをその時点で
    /// いちばん低い列へ貪欲に割り当て、列間の高さを揃えます）。
    /// </summary>
    private bool RenderColumns(List<PanelLayout> panels, int columnCount, int jpegQuality, string outputPath, out string? errorMessage)
    {
        errorMessage = null;
        columnCount = Math.Max(1, columnCount);
        double blockSpacing = panels.Count >= 6 ? 10 : 14;

        double colWidth = 0;
        foreach (PanelLayout panel in panels)
        {
            double cardWidth = Math.Max(panel.DrawWidth, Math.Max(panel.TitleText.Width, panel.DetailText.Width)) + CardPadding * 2;
            colWidth = Math.Max(colWidth, cardWidth);
        }

        // 時系列順（panels の並び順）を先頭から columnCount 個の「連続したかたまり」に分割し、
        // 左の列から順に詰める（新聞の段組みと同じ考え方）。列の高さバランスより、
        // 左列を上から下まで読み切ってから右列に進む、という自然な読み順を優先する。
        // （1行ごとに列を切り替える方式だと、右列の先頭カードが時系列としては
        // 左列2枚目より前に来てしまい、読み順と時系列がズレて見える）
        int itemsPerColumn = (int)Math.Ceiling(panels.Count / (double)columnCount);
        var columns = new List<PanelLayout>[columnCount];
        var columnHeights = new double[columnCount];
        for (int i = 0; i < columnCount; i++)
        {
            columns[i] = new List<PanelLayout>();
            columnHeights[i] = OuterPadding;
        }

        for (int i = 0; i < panels.Count; i++)
        {
            PanelLayout panel = panels[i];
            double cardHeight = CardPadding + panel.HeaderHeight + HeaderBottomMargin + panel.DrawHeight + CardPadding;
            int target = Math.Min(i / itemsPerColumn, columnCount - 1);

            if (columns[target].Count > 0)
            {
                columnHeights[target] += blockSpacing;
            }

            columns[target].Add(panel);
            columnHeights[target] += cardHeight;
        }

        for (int i = 0; i < columnCount; i++)
        {
            columnHeights[i] += OuterPadding;
        }

        double totalHeight = columnHeights.Max();
        double totalWidth = colWidth * columnCount + ColumnGutter * Math.Max(0, columnCount - 1) + OuterPadding * 2;

        double shrink = totalHeight > SoftMaxTotalHeight ? SoftMaxTotalHeight / totalHeight : 1.0;
        int canvasWidth = (int)Math.Ceiling(totalWidth * shrink);
        int canvasHeight = (int)Math.Ceiling(totalHeight * shrink);

        if (canvasWidth <= 0 || canvasHeight <= 0 || canvasHeight > 30000)
        {
            errorMessage = Loc.Instance.Get("Consultation_Error_InvalidImageSize");
            return false;
        }

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(
                new SolidColorBrush(Color.FromRgb(245, 247, 250)),
                null,
                new Rect(0, 0, canvasWidth, canvasHeight));

            if (Math.Abs(shrink - 1.0) > 0.0001)
            {
                dc.PushTransform(new ScaleTransform(shrink, shrink));
            }

            var cardPen = new Pen(new SolidColorBrush(Color.FromRgb(200, 210, 220)), 1);
            cardPen.Freeze();
            var accentBrush = new SolidColorBrush(Color.FromRgb(30, 100, 200));
            accentBrush.Freeze();
            var outOfRegionAccentBrush = new SolidColorBrush(Color.FromRgb(200, 140, 20));
            outOfRegionAccentBrush.Freeze();

            for (int col = 0; col < columnCount; col++)
            {
                double x = OuterPadding + col * (colWidth + ColumnGutter);
                double y = OuterPadding;
                foreach (PanelLayout panel in columns[col])
                {
                    double cardHeight = CardPadding + panel.HeaderHeight + HeaderBottomMargin + panel.DrawHeight + CardPadding;
                    DrawPanelCard(dc, panel, x, y, colWidth, cardHeight, cardPen, accentBrush, outOfRegionAccentBrush);
                    y += cardHeight + blockSpacing;
                }
            }

            if (Math.Abs(shrink - 1.0) > 0.0001)
            {
                dc.Pop();
            }
        }

        return SaveVisual(visual, canvasWidth, canvasHeight, jpegQuality, outputPath, out errorMessage);
    }

    private static void DrawPanelCard(
        DrawingContext dc,
        PanelLayout panel,
        double x,
        double y,
        double cardWidth,
        double cardHeight,
        Pen cardPen,
        Brush accentBrush,
        Brush outOfRegionAccentBrush)
    {
        dc.DrawRoundedRectangle(Brushes.White, cardPen, new Rect(x, y, cardWidth, cardHeight), 6, 6);
        dc.DrawRectangle(panel.IsTextOnly ? outOfRegionAccentBrush : accentBrush, null, new Rect(x, y, 5, cardHeight));

        double contentX = x + CardPadding;
        double contentY = y + CardPadding;
        dc.DrawText(panel.TitleText, new Point(contentX, contentY));
        contentY += panel.TitleText.Height + 2;
        dc.DrawText(panel.DetailText, new Point(contentX, contentY));
        contentY += panel.DetailText.Height + HeaderBottomMargin;

        // 画面外での操作はキャプチャ画像が無いため、見出しだけのカードで終わります。
        if (panel.IsTextOnly)
        {
            return;
        }

        dc.DrawImage(panel.Source!, new Rect(contentX, contentY, panel.DrawWidth, panel.DrawHeight));

        // キーボードは画像上部、その他は座標注釈
        double keyBannerOffset = 0;
        foreach (RecordedAction action in panel.Entry!.LinkedActions)
        {
            if (action.ActionType == RecordedActionType.Keyboard)
            {
                DrawKeyboardBanner(
                    dc,
                    contentX,
                    contentY + keyBannerOffset,
                    panel.DrawWidth,
                    action.KeyboardDisplay ?? action.Summary);
                keyBannerOffset += 28;
            }
            else
            {
                DrawActionAnnotation(dc, action, contentX, contentY, panel.DrawWidth, panel.DrawHeight);
            }
        }
    }

    private static bool SaveVisual(
        DrawingVisual visual,
        int canvasWidth,
        int canvasHeight,
        int jpegQuality,
        string outputPath,
        out string? errorMessage)
    {
        errorMessage = null;
        try
        {
            var bitmap = new RenderTargetBitmap(canvasWidth, canvasHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();

            string? directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var encoder = new JpegBitmapEncoder { QualityLevel = jpegQuality };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                encoder.Save(stream);
            }

            return true;
        }
        catch (Exception ex)
        {
            errorMessage = Loc.Instance.Format("Consultation_Error_GenerationFailed_Format", ex.Message);
            return false;
        }
    }

    private static void DrawActionAnnotation(
        DrawingContext dc,
        RecordedAction action,
        double imageX,
        double imageY,
        double imageWidth,
        double imageHeight)
    {
        switch (action.ActionType)
        {
            case RecordedActionType.LeftClick:
                if (action.RelativeX is double lx && action.RelativeY is double ly)
                {
                    DrawClickMarker(dc, imageX + lx * imageWidth, imageY + ly * imageHeight,
                        Color.FromArgb(230, 230, 40, 40), doubleCircle: false);
                }

                break;
            case RecordedActionType.RightClick:
                if (action.RelativeX is double rx && action.RelativeY is double ry)
                {
                    DrawClickMarker(dc, imageX + rx * imageWidth, imageY + ry * imageHeight,
                        Color.FromArgb(230, 40, 100, 230), doubleCircle: false);
                }

                break;
            case RecordedActionType.LeftDoubleClick:
                if (action.RelativeX is double ldx && action.RelativeY is double ldy)
                {
                    DrawClickMarker(dc, imageX + ldx * imageWidth, imageY + ldy * imageHeight,
                        Color.FromArgb(230, 230, 40, 40), doubleCircle: true);
                }

                break;
            case RecordedActionType.RightDoubleClick:
                if (action.RelativeX is double rdx && action.RelativeY is double rdy)
                {
                    DrawClickMarker(dc, imageX + rdx * imageWidth, imageY + rdy * imageHeight,
                        Color.FromArgb(230, 40, 100, 230), doubleCircle: true);
                }

                break;
            case RecordedActionType.Drag:
                if (action.RelativeX is double sx && action.RelativeY is double sy
                    && action.EndRelativeX is double ex && action.EndRelativeY is double ey)
                {
                    DrawDragArrow(
                        dc,
                        imageX + sx * imageWidth,
                        imageY + sy * imageHeight,
                        imageX + ex * imageWidth,
                        imageY + ey * imageHeight);
                }

                break;
            case RecordedActionType.MouseWheel:
                if (action.RelativeX is double wx && action.RelativeY is double wy)
                {
                    DrawWheelMarker(dc, imageX + wx * imageWidth, imageY + wy * imageHeight, action.WheelUp == true);
                }

                break;
        }
    }

    private static void DrawClickMarker(DrawingContext dc, double centerX, double centerY, Color fillColor, bool doubleCircle)
    {
        const double radius = 10;
        var outline = new Pen(Brushes.White, 2.5);
        outline.Freeze();
        var fill = new SolidColorBrush(fillColor);
        fill.Freeze();
        var crossPen = new Pen(Brushes.White, 1.6);
        crossPen.Freeze();
        var outerRing = new Pen(new SolidColorBrush(Color.FromArgb(180, fillColor.R, fillColor.G, fillColor.B)), 1.6);
        outerRing.Freeze();
        dc.DrawEllipse(null, outerRing, new Point(centerX, centerY), radius + 3, radius + 3);
        if (doubleCircle)
        {
            var ring2 = new Pen(fill, 2);
            ring2.Freeze();
            dc.DrawEllipse(null, ring2, new Point(centerX, centerY), radius + 6, radius + 6);
        }

        dc.DrawEllipse(fill, outline, new Point(centerX, centerY), radius, radius);
        double arm = radius - 2;
        dc.DrawLine(crossPen, new Point(centerX - arm, centerY), new Point(centerX + arm, centerY));
        dc.DrawLine(crossPen, new Point(centerX, centerY - arm), new Point(centerX, centerY + arm));
        dc.DrawEllipse(Brushes.White, null, new Point(centerX, centerY), 2, 2);
    }

    private static void DrawDragArrow(DrawingContext dc, double x1, double y1, double x2, double y2)
    {
        var linePen = new Pen(new SolidColorBrush(Color.FromArgb(230, 20, 140, 80)), 3.5);
        linePen.Freeze();
        var whitePen = new Pen(Brushes.White, 5);
        whitePen.Freeze();
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(220, 20, 140, 80)), whitePen, new Point(x1, y1), 7, 7);
        dc.DrawLine(whitePen, new Point(x1, y1), new Point(x2, y2));
        dc.DrawLine(linePen, new Point(x1, y1), new Point(x2, y2));
        double angle = Math.Atan2(y2 - y1, x2 - x1);
        const double headLen = 14;
        const double headAngle = Math.PI / 7;
        var tip = new Point(x2, y2);
        var p1 = new Point(x2 - headLen * Math.Cos(angle - headAngle), y2 - headLen * Math.Sin(angle - headAngle));
        var p2 = new Point(x2 - headLen * Math.Cos(angle + headAngle), y2 - headLen * Math.Sin(angle + headAngle));
        var geo = new StreamGeometry();
        using (StreamGeometryContext ctx = geo.Open())
        {
            ctx.BeginFigure(tip, true, true);
            ctx.LineTo(p1, true, false);
            ctx.LineTo(p2, true, false);
        }

        geo.Freeze();
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(230, 20, 140, 80)), whitePen, geo);
    }

    private static void DrawWheelMarker(DrawingContext dc, double centerX, double centerY, bool up)
    {
        var fill = new SolidColorBrush(Color.FromArgb(220, 120, 40, 180));
        fill.Freeze();
        var outline = new Pen(Brushes.White, 2);
        outline.Freeze();
        dc.DrawEllipse(fill, outline, new Point(centerX, centerY), 16, 16);
        var arrowPen = new Pen(Brushes.White, 2.5);
        arrowPen.Freeze();
        double dir = up ? -1 : 1;
        dc.DrawLine(arrowPen, new Point(centerX, centerY + 8 * dir), new Point(centerX, centerY - 8 * dir));
        dc.DrawLine(arrowPen, new Point(centerX, centerY - 8 * dir), new Point(centerX - 5, centerY - 2 * dir));
        dc.DrawLine(arrowPen, new Point(centerX, centerY - 8 * dir), new Point(centerX + 5, centerY - 2 * dir));
    }

    private static void DrawKeyboardBanner(DrawingContext dc, double imageX, double imageY, double imageWidth, string keyText)
    {
        string label = string.IsNullOrWhiteSpace(keyText) ? Loc.Instance.Get("CapturedInput_Keyboard") : keyText;
        FormattedText text = CreateFormattedText(label, 14, Brushes.White, bold: true);
        double padX = 10;
        double padY = 5;
        double boxW = Math.Min(imageWidth - 8, text.Width + padX * 2);
        double boxH = text.Height + padY * 2;
        var bg = new SolidColorBrush(Color.FromArgb(210, 40, 50, 70));
        bg.Freeze();
        dc.DrawRoundedRectangle(bg, null, new Rect(imageX + 4, imageY + 4, boxW, boxH), 4, 4);
        dc.DrawText(text, new Point(imageX + 4 + padX, imageY + 4 + padY));
    }

    private static (double widthMultiplier, int jpegQuality) ResolveQualitySettings(ConsultationQualityPreset preset) => preset switch
    {
        ConsultationQualityPreset.TextPriority => (1.15, 95),
        ConsultationQualityPreset.SizePriority => (0.75, 78),
        _ => (1.0, JpegQuality),
    };

    private static int ResolveMaxWidth(int count, double widthMultiplier = 1.0)
    {
        int baseWidth = count >= 8 ? 760 : count >= 5 ? 840 : count >= 3 ? 900 : DefaultMaxScreenshotWidth;
        return Math.Max(240, (int)Math.Round(baseWidth * widthMultiplier));
    }

    private static BitmapSource? LoadBitmapWithoutLock(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0)
            {
                return null;
            }

            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static FormattedText CreateFormattedText(string text, double fontSize, Brush brush, bool bold)
    {
        return new FormattedText(
            text,
            CultureInfo.GetCultureInfo("ja-JP"),
            FlowDirection.LeftToRight,
            bold ? JapaneseTypeface : JapaneseTypefaceRegular,
            fontSize,
            brush,
            1.0);
    }

    /// <summary>
    /// 描画パネル1枚分です。通常はキャプチャ画像付き（<see cref="Entry"/>/<see cref="Source"/> あり）ですが、
    /// キャプチャ範囲外での操作は画像が無いため、見出しだけの「テキストのみ」パネルとして扱います
    /// （<see cref="IsTextOnly"/> が true のとき <see cref="Entry"/>/<see cref="Source"/> は null）。
    /// </summary>
    private sealed class PanelLayout
    {
        public PanelLayout(
            ConsultationTimelineEntry entry,
            BitmapSource source,
            int drawWidth,
            int drawHeight,
            FormattedText titleText,
            FormattedText detailText,
            double headerHeight)
        {
            Entry = entry;
            Source = source;
            DrawWidth = drawWidth;
            DrawHeight = drawHeight;
            TitleText = titleText;
            DetailText = detailText;
            HeaderHeight = headerHeight;
            IsTextOnly = false;
        }

        public PanelLayout(FormattedText titleText, FormattedText detailText, double headerHeight)
        {
            TitleText = titleText;
            DetailText = detailText;
            HeaderHeight = headerHeight;
            DrawWidth = 0;
            DrawHeight = 0;
            IsTextOnly = true;
        }

        public ConsultationTimelineEntry? Entry { get; }
        public BitmapSource? Source { get; }
        public int DrawWidth { get; }
        public int DrawHeight { get; }
        public FormattedText TitleText { get; }
        public FormattedText DetailText { get; }
        public double HeaderHeight { get; }
        public bool IsTextOnly { get; }
    }
}
