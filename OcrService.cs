using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Lootlens;

public sealed class OcrService {
    private readonly OcrEngine? _engine;

    public OcrService() {
        try {
            _engine = OcrEngine.TryCreateFromUserProfileLanguages();
        } catch (Exception ex) {
            Debug.WriteLine($"[OCR] Failed to initialize OcrEngine: {ex.Message}");
            _engine = null;
        }
    }

    public async Task<string?> RecognizeAroundCursorAsync(int regionWidth, int regionHeight, CancellationToken cancellationToken = default) {
        try {
            var result = await RecognizeCoreAsync(regionWidth, regionHeight, cancellationToken);
            if (result is null)
                return null;

            var text = NormalizeText(result.Text);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        } catch (OperationCanceledException) {
            return null;
        } catch (Exception ex) {
            Debug.WriteLine($"[OCR] Recognition failed: {ex.Message}");
            return null;
        }
    }

    public async Task<IReadOnlyList<string>> RecognizeCandidateLinesAroundCursorAsync(int regionWidth, int regionHeight, CancellationToken cancellationToken = default) {
        try {
            if (!GetCursorPos(out var cursor))
                return Array.Empty<string>();

            var searchRect = GetDynamicSearchRegionAroundCursor(cursor, regionWidth, regionHeight);
            var ocrResult = await RecognizeRegionAsync(searchRect, cancellationToken);
            if (ocrResult is null)
                return Array.Empty<string>();

            var ranked = ExtractScoredLines(ocrResult, searchRect, cursor.X, cursor.Y);

            var focused = ranked
                .Where(entry => entry.distanceToCursor <= 210)
                .Where(entry => entry.deltaYFromCursor <= 70)
                .Where(entry => entry.deltaYFromCursor >= -240)
                .ToList();

            if (focused.Count == 0) {
                focused = ranked
                    .Where(entry => entry.distanceToCursor <= 320)
                    .Where(entry => entry.deltaYFromCursor <= 110)
                    .Where(entry => entry.deltaYFromCursor >= -300)
                    .ToList();
            }

            var strictCandidates = focused
                .Where(entry => IsLikelyItemNameLine(entry.text))
                .Select(entry => entry.text)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToList();

            if (strictCandidates.Count > 0)
                return strictCandidates;

            return focused
                .Select(entry => entry.text)
                .Where(IsLikelyFallbackItemLine)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .ToList();

        } catch (OperationCanceledException) {
            return Array.Empty<string>();
        } catch (Exception ex) {
            Debug.WriteLine($"[OCR] Candidate extraction failed: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    private async Task<OcrResult?> RecognizeCoreAsync(int regionWidth, int regionHeight, CancellationToken cancellationToken) {
        if (_engine is null)
            return null;

        if (regionWidth <= 0 || regionHeight <= 0)
            return null;

        var rect = GetCaptureRegion(regionWidth, regionHeight);
        return await RecognizeRegionAsync(rect, cancellationToken);
    }

    private async Task<OcrResult?> RecognizeRegionAsync(Rectangle rect, CancellationToken cancellationToken) {
        if (_engine is null)
            return null;

        if (rect.Width <= 0 || rect.Height <= 0)
            return null;

        using var bitmap = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap)) {
            graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, rect.Size, CopyPixelOperation.SourceCopy);
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var softwareBitmap = ConvertToSoftwareBitmap(bitmap);
        return await _engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken);
    }

    private sealed class ScoredLine {
        public required string text { get; init; }
        public required double score { get; init; }
        public required double distanceToCursor { get; init; }
        public required double deltaYFromCursor { get; init; }
    }

    private static IReadOnlyList<ScoredLine> ExtractScoredLines(OcrResult result, Rectangle captureRect, int cursorX, int cursorY) {
        var ranked = new List<ScoredLine>();

        var localCursorX = cursorX - captureRect.Left;
        var localCursorY = cursorY - captureRect.Top;

        foreach (var line in result.Lines) {
            var normalized = NormalizeLine(line.Text);
            if (normalized.Length < 3)
                continue;

            var words = line.Words;
            if (words.Count == 0)
                continue;

            var minX = double.MaxValue;
            var minY = double.MaxValue;
            var maxX = double.MinValue;
            var maxY = double.MinValue;

            foreach (var word in words) {
                var rect = word.BoundingRect;
                minX = Math.Min(minX, rect.X);
                minY = Math.Min(minY, rect.Y);
                maxX = Math.Max(maxX, rect.X + rect.Width);
                maxY = Math.Max(maxY, rect.Y + rect.Height);
            }

            var lineCenterX = (minX + maxX) / 2.0;
            var lineCenterY = (minY + maxY) / 2.0;
            var dx = lineCenterX - localCursorX;
            var dy = lineCenterY - localCursorY;
            var distance = Math.Sqrt((dx * dx) + (dy * dy));

            var width = Math.Max(1.0, maxX - minX);
            var height = Math.Max(1.0, maxY - minY);

            var score = ScoreTooltipLine(normalized);

            // Prefer lines close to cursor, but not tiny labels.
            score += Math.Max(0.0, 260.0 - distance);
            if (width >= 150)
                score += 80;
            else if (width < 70)
                score -= 120;

            if (height >= 18)
                score += 25;

            // Tooltip title is typically above or around the cursor.
            if (lineCenterY <= localCursorY + 8)
                score += 60;
            else
                score -= 40;

            // Prefer lines with 2-8 words and realistic name lengths.
            var wordCount = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
            if (wordCount is >= 2 and <= 8)
                score += 60;
            else if (wordCount == 1)
                score -= 70;

            ranked.Add(new ScoredLine {
                text = normalized,
                score = score,
                distanceToCursor = distance,
                deltaYFromCursor = lineCenterY - localCursorY
            });
        }

        if (ranked.Count == 0) {
            var fallback = NormalizeText(result.Text);
            if (string.IsNullOrWhiteSpace(fallback))
                return Array.Empty<ScoredLine>();

            return fallback
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(NormalizeLine)
                .Where(line => line.Length >= 3)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(line => new ScoredLine {
                    text = line,
                    score = ScoreTooltipLine(line),
                    distanceToCursor = double.MaxValue,
                    deltaYFromCursor = double.MaxValue
                })
                .ToList();
        }

        return ranked
            .OrderByDescending(entry => entry.score)
            .Take(8)
            .ToList();
    }

    private static Rectangle GetCaptureRegion(int width, int height) {
        if (!GetCursorPos(out var cursor))
            return new Rectangle(0, 0, width, height);

        var left = cursor.X - (width / 2);
        var top = cursor.Y - (height / 2);

        return new Rectangle(left, top, width, height);
    }

    private static Rectangle GetDynamicSearchRegionAroundCursor(POINT cursor, int regionWidth, int regionHeight) {
        var width = Math.Clamp((int)(regionWidth * 1.8), 520, 980);
        var height = Math.Clamp((int)(regionHeight * 1.4), 260, 620);

        // Bias region upward because tooltip names are usually above the cursor.
        var left = cursor.X - (width / 2);
        var top = cursor.Y - (int)(height * 0.68);
        var rect = new Rectangle(left, top, width, height);

        var bounds = GetScreenBounds(cursor.X, cursor.Y);
        return ClampRectToBounds(rect, bounds);
    }

    private static Rectangle GetScreenBounds(int x, int y) {
        var left = (int)Math.Floor(System.Windows.SystemParameters.VirtualScreenLeft);
        var top = (int)Math.Floor(System.Windows.SystemParameters.VirtualScreenTop);
        var width = (int)Math.Ceiling(System.Windows.SystemParameters.VirtualScreenWidth);
        var height = (int)Math.Ceiling(System.Windows.SystemParameters.VirtualScreenHeight);

        if (width <= 0 || height <= 0)
            return new Rectangle(0, 0, 1920, 1080);

        return new Rectangle(left, top, width, height);
    }

    private static Rectangle ClampRectToBounds(Rectangle rect, Rectangle bounds) {
        var x = Math.Max(bounds.Left, rect.Left);
        var y = Math.Max(bounds.Top, rect.Top);
        var right = Math.Min(bounds.Right, rect.Right);
        var bottom = Math.Min(bounds.Bottom, rect.Bottom);

        if (right <= x || bottom <= y)
            return Rectangle.Empty;

        return new Rectangle(x, y, right - x, bottom - y);
    }

    private static SoftwareBitmap ConvertToSoftwareBitmap(Bitmap bitmap) {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);

        try {
            var sourceStride = data.Stride;
            var width = bitmap.Width;
            var height = bitmap.Height;
            var targetStride = width * 4;
            var buffer = new byte[targetStride * height];

            if (sourceStride == targetStride) {
                Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            } else {
                var row = new byte[Math.Abs(sourceStride)];
                for (var y = 0; y < height; y++) {
                    var sourcePtr = IntPtr.Add(data.Scan0, y * sourceStride);
                    Marshal.Copy(sourcePtr, row, 0, row.Length);
                    System.Buffer.BlockCopy(row, 0, buffer, y * targetStride, targetStride);
                }
            }

            using var writer = new DataWriter();
            writer.WriteBytes(buffer);
            var ibuffer = writer.DetachBuffer();

            return SoftwareBitmap.CreateCopyFromBuffer(
                ibuffer,
                BitmapPixelFormat.Bgra8,
                width,
                height,
                BitmapAlphaMode.Premultiplied);
        } finally {
            bitmap.UnlockBits(data);
        }
    }

    private static string NormalizeText(string? text) {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var builder = new StringBuilder(text.Length);
        foreach (var c in text) {
            if (char.IsControl(c) && c != '\n' && c != '\r' && c != '\t')
                continue;

            builder.Append(c);
        }

        return builder
            .ToString()
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeLine)
            .Where(line => line.Length > 0)
            .Aggregate(new StringBuilder(), (sb, line) => {
                if (sb.Length > 0)
                    sb.Append('\n');

                sb.Append(line);
                return sb;
            })
            .ToString();
    }

    private static string NormalizeLine(string? text) {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var trimmed = text.Trim();
        trimmed = Regex.Replace(trimmed, "\\s+", " ");
        return trimmed;
    }

    private static bool IsLikelyItemNameLine(string line) {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var normalized = line.Trim();
        if (normalized.Length < 3 || normalized.Length > 64)
            return false;

        if (normalized.Contains('₽'))
            return false;

        if (normalized.EndsWith(':'))
            return false;

        var letters = normalized.Count(char.IsLetter);
        if (letters < 2)
            return false;

        var digits = normalized.Count(char.IsDigit);
        if (digits > letters)
            return false;

        var tokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Single-word OCR labels inside the stash grid are often noisy and wrong for tooltip matching.
        if (tokens.Length == 1) {
            var token = tokens[0];
            if (token.Length < 8)
                return false;

            // Accept long single words only when they resemble a real item name token.
            if (!token.Any(char.IsDigit) && !token.Contains('-'))
                return false;
        }

        if (tokens.Length >= 2) {
            var longWordCount = tokens.Count(t => t.Length >= 3);
            if (longWordCount == 0)
                return false;
        }

        return true;
    }

    private static bool IsLikelyFallbackItemLine(string line) {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var normalized = line.Trim();
        if (normalized.Length < 5 || normalized.Length > 72)
            return false;

        var tokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokens.Length >= 2)
            return true;

        // Allow a single token only if it carries discriminative structure.
        var token = tokens[0];
        return token.Length >= 8 && (token.Any(char.IsDigit) || token.Contains('-') || token.Contains('x'));
    }

    private static double ScoreTooltipLine(string line) {
        if (string.IsNullOrWhiteSpace(line))
            return double.MinValue;

        var score = 0d;
        var trimmed = line.Trim();
        var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var wordCount = words.Length;

        if (wordCount >= 2 && wordCount <= 7)
            score += 180;
        else if (wordCount == 1)
            score -= 110;

        if (trimmed.Length >= 10 && trimmed.Length <= 46)
            score += 90;
        else if (trimmed.Length > 56)
            score -= 40;

        if (trimmed.Contains('-'))
            score += 30;

        if (trimmed.Any(char.IsDigit) && trimmed.Any(char.IsLetter))
            score += 25;

        var upperCount = trimmed.Count(char.IsUpper);
        if (upperCount >= 2)
            score += 20;

        return score;
    }

    private static void AddUnique(List<string> list, string candidate) {
        if (string.IsNullOrWhiteSpace(candidate))
            return;

        if (!list.Contains(candidate, StringComparer.OrdinalIgnoreCase)) {
            list.Add(candidate);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT {
        public int X;
        public int Y;
    }
}
