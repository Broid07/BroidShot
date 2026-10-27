using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace BroidShot.Modules.Convert;

/// <summary>
/// Supplies a password for a protected PDF. Arguments: file name, attempt number (0 for the first request;
/// greater than 0 means the previous password was wrong). Return null to skip the file.
/// Called on the thread doing the work, so a UI implementation must marshal to the dispatcher itself.
/// </summary>
public delegate string? PdfPasswordPrompt(string fileName, int attempt);

/// <summary>Rasterises PDF pages with the Windows.Data.Pdf runtime component that ships with Windows.</summary>
public static class PdfRendering
{
    private const int AccessDenied = unchecked((int)0x80070005);
    // Windows.Data.Pdf reports a missing or wrong password as ERROR_WRONG_PASSWORD (or ERROR_INVALID_PASSWORD on some builds).
    private const int WrongPassword = unchecked((int)0x8007052B);
    private const int InvalidPassword = unchecked((int)0x80070056);
    /// <summary>Wrong passwords accepted from a prompt before the file is skipped.</summary>
    public const int MaxPasswordAttempts = 10;
    public const long MaxRenderPixels = 100_000_000;
    public const int MaxRenderSide = 16384;

    /// <summary>Opens a PDF, asking <paramref name="passwords"/> when it is password-protected. <paramref name="usedPassword"/> is the password that worked (null if none was needed).</summary>
    public static async Task<(PdfDocument Document, string? UsedPassword)> OpenAsync(string path, string? knownPassword, PdfPasswordPrompt? passwords, CancellationToken cancellationToken)
    {
        StorageFile file;
        try
        {
            file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or COMException or UnauthorizedAccessException or ArgumentException)
        {
            throw new ConvertException("Dosya bulunamadı ya da okunamıyor.", ex);
        }

        string? password = knownPassword;
        int attempt = 0;
        string name = Path.GetFileName(path);
        while (true)
        {
            Exception? refused = null;
            foreach (string? candidate in PasswordForms(password))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    PdfDocument document = candidate is null
                        ? await PdfDocument.LoadFromFileAsync(file).AsTask(cancellationToken).ConfigureAwait(false)
                        : await PdfDocument.LoadFromFileAsync(file, candidate).AsTask(cancellationToken).ConfigureAwait(false);
                    return (document, password);
                }
                catch (Exception ex) when (IsPasswordProblem(ex)) { refused = ex; }
                catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or IOException)
                {
                    throw new ConvertException($"PDF açılamadı (0x{ex.HResult:X8}). Dosya bozuk ya da desteklenmeyen bir PDF olabilir.", ex);
                }
            }

            if (passwords is null) throw new ConvertException("Bu PDF parola korumalı; parola sorulamadığı için atlandı.", refused);
            // A prompt that keeps answering with a wrong password must not loop forever.
            if (attempt >= MaxPasswordAttempts) throw new ConvertException("Parola yanlış; çok fazla deneme yapıldı, dosya atlandı.", refused);
            // A stored password that fails counts as a wrong attempt too.
            string? supplied = passwords(name, password is null && attempt == 0 ? 0 : Math.Max(1, attempt));
            attempt++;
            if (supplied is null) throw new ConvertException("Parola girilmedi, dosya atlandı.", refused);
            password = supplied;
        }
    }

    private static bool IsPasswordProblem(Exception ex) => ex.HResult is AccessDenied or WrongPassword or InvalidPassword || ex is UnauthorizedAccessException;

    /// <summary>
    /// The ways a typed password may have been stored. Up to revision 4 a PDF password is a byte string, so letters
    /// outside Latin-1 (ş, ğ, ı…) depend on the producer: PDFsharp and many other tools keep only the low byte, while
    /// Windows.Data.Pdf encodes the text as given. Both forms are tried before the user is asked again.
    /// </summary>
    private static IEnumerable<string?> PasswordForms(string? password)
    {
        yield return password;
        if (password is null) yield break;
        string lowBytes = string.Create(password.Length, password, (span, text) => { for (int i = 0; i < text.Length; i++) span[i] = (char)(text[i] & 0xFF); });
        if (lowBytes != password) yield return lowBytes;
    }

    /// <summary>Pixel size a page will render at, or throws when it is unreasonably large.</summary>
    public static (int Width, int Height) GetRenderSize(PdfPage page, int dpi)
    {
        (int width, int height) = ImageMath.PagePixels(page.Size.Width, page.Size.Height, dpi);
        if (width > MaxRenderSide || height > MaxRenderSide || (long)width * height > MaxRenderPixels)
            throw new ConvertException($"Sayfa bu DPI'da çok büyük olur ({width}×{height} piksel). Daha düşük bir DPI seç.");
        return (width, height);
    }

    /// <summary>Renders a zero-based page to PNG bytes on a white background.</summary>
    public static async Task<(byte[] Png, int Width, int Height)> RenderPageAsync(PdfDocument document, int pageIndex, int dpi, CancellationToken cancellationToken)
    {
        if (dpi < 10 || dpi > 1200) throw new ConvertException("DPI 10 ile 1200 arasında olmalı.");
        using PdfPage page = document.GetPage((uint)pageIndex);
        (int width, int height) = GetRenderSize(page, dpi);
        var options = new PdfPageRenderOptions
        {
            DestinationWidth = (uint)width,
            DestinationHeight = (uint)height,
            BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255)
        };
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, options).AsTask(cancellationToken).ConfigureAwait(false);
            stream.Seek(0);
            byte[] bytes = new byte[stream.Size];
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size).AsTask(cancellationToken).ConfigureAwait(false);
            reader.ReadBytes(bytes);
            return (bytes, width, height);
        }
        catch (Exception ex) when (ex is COMException or OutOfMemoryException or InvalidOperationException)
        {
            throw new ConvertException($"Sayfa {pageIndex + 1} çizilemedi: {ex.Message}", ex);
        }
    }
}
