using System.Drawing.Drawing2D;
using System.Reflection;
using System.Text.Json;

namespace IPv6Lens;

internal static class TrafficShareTests
{
    internal static void Run(string path)
    {
        var checks = new List<string>();
        try
        {
            using var share = new TrafficShare();
            var paint = typeof(TrafficShare).GetMethod("PaintContent",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            long[][] samples = {
                new long[] { 18000000, 1000, 17999000 },
                new long[] { 0, 0, 0 },
                new long[] { 36000000, 0, 0 },
                new long[] { 1, 36000000, 1 },
                new long[] { long.MaxValue, 1, 1 },
                new long[] { long.MaxValue, long.MaxValue, long.MaxValue }
            };
            foreach (float scale in new[] { 1f, 1.25f, 1.5f })
            {
                using var image = new Bitmap((int)(600 * scale), (int)(180 * scale));
                using var graphics = Graphics.FromImage(image);
                graphics.ScaleTransform(scale, scale);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                for (int i = 0; i < samples.Length; i++)
                {
                    var values = samples[i];
                    share.Update(values[0], values[1], values[2]);
                    paint.Invoke(share, new object[] { graphics, 600f, 180f });
                    double total = values.Sum(n => (double)n);
                    double expected = total > 0 ? 100d * values[0] / total : 0;
                    if (Math.Abs(share.Percent - expected) > 1e-9 ||
                        share.V6 != values[0] || share.V4 != values[1] || share.Unknown != values[2])
                        throw new IOException("Drawing changed the traffic totals or percentage");
                    checks.Add($"Actual ring rendering at scale {scale}, sample {i}, preserves counters");
                }
            }
            using (var image = new Bitmap(600, 180))
            using (var graphics = Graphics.FromImage(image))
            {
                foreach (float height in new[] { 36f, 20f })
                {
                    paint.Invoke(share, new object[] { graphics, 600f, height });
                    checks.Add($"Transient height {height} does not draw an invalid ring");
                }
            }
            File.WriteAllText(path, JsonSerializer.Serialize(new { state = "passed", checks }));
        }
        catch (Exception ex)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new {
                state = "failed", checks, error = (ex.InnerException ?? ex).ToString()
            }));
            Environment.ExitCode = 1;
        }
    }
}
