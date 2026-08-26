using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using TracePrompt.Models;
using Loc = TracePrompt.Localization.LocalizationManager;

namespace TracePrompt.Services;

/// <summary>
/// 画面上の指定範囲のスクリーンショットを取得するサービスです。
/// BitBlt でピクセルをそのままコピーするため、ツールチップやコンテキストメニューなど
/// 別ウィンドウとして表示されるものも含めて、見えているとおりに撮れます。
/// </summary>
public sealed class ScreenshotService
{
    private const int SrcCopy = 0x00CC0020;

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(
        nint hdcDest, int xDest, int yDest, int width, int height,
        nint hdcSrc, int xSrc, int ySrc, int rop);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hDC);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint hgdiobj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint hObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint hdc);

    /// <summary>
    /// 画面上の指定範囲を PNG として保存します（BitBlt によるピクセルコピー）。
    /// 失敗しても例外は投げず、false とエラーメッセージを返します。
    /// </summary>
    public bool TryCaptureRegionToPng(WindowBounds region, string outputFilePath, out string? errorMessage)
    {
        errorMessage = null;

        if (region.Width <= 0 || region.Height <= 0)
        {
            errorMessage = Loc.Instance.Get("Screenshot_Error_InvalidRegionSize");
            return false;
        }

        if (string.IsNullOrWhiteSpace(outputFilePath))
        {
            errorMessage = Loc.Instance.Get("Error_NoOutputPath");
            return false;
        }

        nint screenDc = nint.Zero;
        nint memDc = nint.Zero;
        nint hBitmap = nint.Zero;
        nint oldBitmap = nint.Zero;

        try
        {
            // 画面 DC を元に、互換メモリ DC とビットマップを用意します。
            screenDc = GetDC(nint.Zero);
            if (screenDc == nint.Zero)
            {
                errorMessage = Loc.Instance.Get("Screenshot_Error_ScreenDcFailed");
                return false;
            }

            memDc = CreateCompatibleDC(screenDc);
            if (memDc == nint.Zero)
            {
                errorMessage = Loc.Instance.Get("Screenshot_Error_MemoryDcFailed");
                return false;
            }

            hBitmap = CreateCompatibleBitmap(screenDc, region.Width, region.Height);
            if (hBitmap == nint.Zero)
            {
                errorMessage = Loc.Instance.Get("Screenshot_Error_BitmapCreateFailed");
                return false;
            }

            oldBitmap = SelectObject(memDc, hBitmap);

            bool copied = BitBlt(
                memDc, 0, 0, region.Width, region.Height,
                screenDc, region.Left, region.Top, SrcCopy);
            if (!copied)
            {
                errorMessage = Loc.Instance.Get("Screenshot_Error_BitBltFailed");
                return false;
            }

            // GDI ビットマップを WPF の BitmapSource に変換して PNG 保存します。
            // （外部 NuGet の System.Drawing を使わないための実装です）
            BitmapSource bitmapSource = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                nint.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            bitmapSource.Freeze();

            string? directory = Path.GetDirectoryName(outputFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmapSource));

            using (var stream = File.Create(outputFilePath))
            {
                encoder.Save(stream);
            }

            return true;
        }
        catch (Exception ex)
        {
            errorMessage = Loc.Instance.Format("Screenshot_Error_SaveFailed_Format", ex.Message);
            return false;
        }
        finally
        {
            // GDI リソースは必ず解放します。
            if (memDc != nint.Zero && oldBitmap != nint.Zero)
            {
                SelectObject(memDc, oldBitmap);
            }

            if (hBitmap != nint.Zero)
            {
                DeleteObject(hBitmap);
            }

            if (memDc != nint.Zero)
            {
                DeleteDC(memDc);
            }

            if (screenDc != nint.Zero)
            {
                ReleaseDC(nint.Zero, screenDc);
            }
        }
    }
}
