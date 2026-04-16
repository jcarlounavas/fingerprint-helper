using Microsoft.AspNetCore.Mvc;
using SourceAFIS;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FingerjetHelper.Controllers
{
    /// <summary>
    /// Image-based fingerprint matching for the Laravel event-ms app.
    /// Verify: POST /Match/verify — one probe vs one reference; returns { match, score }.
    /// Identify: POST /Match/identify — one probe vs many references; returns { match, bestMatchId, score }.
    /// </summary>
    [ApiController]
    [Route("[controller]")]
    public class MatchController : ControllerBase
    {
        [HttpGet("status")]
        public IActionResult Status() => Ok(new { status = "ok", service = "FingerjetHelper" });

        public class VerifyRequest
        {
            public string ProbeImageBase64 { get; set; } = string.Empty;
            public string ReferenceImageBase64 { get; set; } = string.Empty;
            public double? Threshold { get; set; }
            public bool? AutoInvert { get; set; }
        }

        public class IdentifyRequest
        {
            public string ProbeImageBase64 { get; set; } = string.Empty;
            public List<IdentifyReference> References { get; set; } = new();
            public double? Threshold { get; set; }
        }

        public class IdentifyReference
        {
            public object? Id { get; set; }
            public string ImageBase64 { get; set; } = string.Empty;
        }

        private static Bitmap DecodeBase64ToBitmap(string base64)
        {
            if (string.IsNullOrWhiteSpace(base64))
                throw new ArgumentException("Empty base64");

            base64 = base64.Trim();
            if (base64.StartsWith("\"") && base64.EndsWith("\"") && base64.Length > 2)
                base64 = base64.Substring(1, base64.Length - 2);

            base64 = Regex.Replace(base64, @"\s+", "");
            if (base64.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = base64.Split(',', 2);
                base64 = parts.Length == 2 ? parts[1] : base64;
            }

            base64 = base64.Replace('-', '+').Replace('_', '/');

            int mod = base64.Length % 4;
            if (mod == 2) base64 += "==";
            else if (mod == 3) base64 += "=";

            try
            {
                byte[] bytes = Convert.FromBase64String(base64);
                using var ms = new MemoryStream(bytes);
                using var tmp = new Bitmap(ms);
                return new Bitmap(tmp);
            }
            catch (Exception ex)
            {
                throw new ArgumentException("Invalid Base64 image data: " + ex.Message);
            }
        }

        private static byte[] ToGrayscaleBytes(Bitmap bmp)
        {
            int width = bmp.Width;
            int height = bmp.Height;
            var pixels = new byte[width * height];

            if (bmp.PixelFormat == PixelFormat.Format8bppIndexed)
            {
                var rect = new Rectangle(0, 0, width, height);
                var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format8bppIndexed);
                try
                {
                    int stride = Math.Abs(data.Stride);
                    var raw = new byte[stride * height];
                    Marshal.Copy(data.Scan0, raw, 0, raw.Length);

                    for (int y = 0; y < height; y++)
                    {
                        int srcRow = y * stride;
                        int dstRow = y * width;
                        Buffer.BlockCopy(raw, srcRow, pixels, dstRow, width);
                    }
                    return pixels;
                }
                finally { bmp.UnlockBits(data); }
            }

            using var clone = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(clone)) g.DrawImage(bmp, 0, 0, width, height);

            var rect24 = new Rectangle(0, 0, width, height);
            var data24 = clone.LockBits(rect24, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                int stride = Math.Abs(data24.Stride);
                var rgbValues = new byte[stride * height];
                Marshal.Copy(data24.Scan0, rgbValues, 0, rgbValues.Length);

                for (int y = 0; y < height; y++)
                {
                    int row = y * stride;
                    int outRow = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        int idx = row + x * 3;
                        byte b = rgbValues[idx];
                        byte g = rgbValues[idx + 1];
                        byte r = rgbValues[idx + 2];
                        pixels[outRow + x] = (byte)(0.299 * r + 0.587 * g + 0.114 * b);
                    }
                }
                return pixels;
            }
            finally { clone.UnlockBits(data24); }
        }

        private static bool ShouldInvert(byte[] pixels)
        {
            if (pixels == null || pixels.Length == 0) return false;
            int dark = pixels.Count(p => p < 128);
            return ((double)dark / pixels.Length) < 0.45;
        }

        private static void InvertPixels(byte[] pixels)
        {
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = (byte)(255 - pixels[i]);
        }

        private static FingerprintTemplate BuildFingerprintTemplate(string base64, bool? autoInvert = null)
        {
            using var bmp = DecodeBase64ToBitmap(base64);
            var pixels = ToGrayscaleBytes(bmp);
            if (autoInvert ?? ShouldInvert(pixels)) InvertPixels(pixels);
            var options = new FingerprintImageOptions { Dpi = 500 };
            var image = new FingerprintImage(bmp.Width, bmp.Height, pixels, options);
            return new FingerprintTemplate(image);
        }

        [HttpPost("verify")]
        public IActionResult Verify([FromBody] VerifyRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.ProbeImageBase64) ||
                string.IsNullOrWhiteSpace(request.ReferenceImageBase64))
                return Ok(new { match = false, score = 0.0, message = "Missing images" });

            try
            {
                var probeTemplate = BuildFingerprintTemplate(request.ProbeImageBase64, request.AutoInvert);
                var refTemplate = BuildFingerprintTemplate(request.ReferenceImageBase64, request.AutoInvert);
                var matcher = new FingerprintMatcher(probeTemplate);
                double score = matcher.Match(refTemplate);
                double threshold = request.Threshold ?? 10.0;
                bool match = score >= threshold;

                return Ok(new { match, score });
            }
            catch (ArgumentException ex)
            {
                return Ok(new { match = false, score = 0.0, message = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { match = false, score = 0, error = ex.Message });
            }
        }

        [HttpPost("identify")]
        public IActionResult Identify([FromBody] IdentifyRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.ProbeImageBase64))
                return Ok(new { match = false, bestMatchId = (object?)null, score = 0.0, message = "Missing probe image" });

            if (request.References == null || request.References.Count == 0)
                return Ok(new { match = false, bestMatchId = (object?)null, score = 0.0, message = "No references" });

            const int MaxReferences = 10_000;
            if (request.References.Count > MaxReferences)
                return Ok(new { match = false, bestMatchId = (object?)null, score = 0.0, message = $"Too many references (max {MaxReferences})" });

            FingerprintTemplate probeTemplate;
            try { probeTemplate = BuildFingerprintTemplate(request.ProbeImageBase64); }
            catch (ArgumentException ex) { return Ok(new { match = false, bestMatchId = (object?)null, score = 0.0, message = ex.Message }); }

            double threshold = request.Threshold ?? 10.0;
            double bestScore = -1;
            object? bestId = null;
            var bestLock = new object();

            // Conservative parallelism for laptop CPUs/RAM. Configure via env var.
            // FINGERJET_IDENTIFY_MAX_DEGREE: default 4, hard cap 6.
            int maxDegree = 6;
            var env = Environment.GetEnvironmentVariable("FINGERJET_IDENTIFY_MAX_DEGREE");
            if (!string.IsNullOrWhiteSpace(env) && int.TryParse(env, out var parsed))
                maxDegree = parsed;
            if (maxDegree < 1) maxDegree = 1;
            if (maxDegree > 6) maxDegree = 6;

            Parallel.ForEach(
                request.References,
                new ParallelOptions { MaxDegreeOfParallelism = maxDegree },
                refItem =>
                {
                    if (string.IsNullOrWhiteSpace(refItem.ImageBase64))
                        return;

                    try
                    {
                        var refTemplate = BuildFingerprintTemplate(refItem.ImageBase64);
                        // Create matcher per iteration to avoid shared-state/thread-safety issues.
                        var matcher = new FingerprintMatcher(probeTemplate);
                        double score = matcher.Match(refTemplate);

                        lock (bestLock)
                        {
                            if (score > bestScore)
                            {
                                bestScore = score;
                                bestId = refItem.Id;
                            }
                        }
                    }
                    catch
                    {
                        // skip invalid references
                    }
                }
            );

            bool match = bestScore >= threshold && bestId != null;
            double resultScore = bestScore >= 0 ? bestScore : 0.0;
            return Ok(new { match, bestMatchId = bestId, score = resultScore });
        }
    }
}