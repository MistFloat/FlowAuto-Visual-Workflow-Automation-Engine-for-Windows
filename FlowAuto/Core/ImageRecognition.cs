using OpenCvSharp;
using OpenCvSharp.Extensions;
using Point = System.Drawing.Point;
using Size = OpenCvSharp.Size;

namespace FlowAuto.Core;

public static class ImageRecognition
{
    private const int MaxCachedTemplates = 32;
    private const int MaxCachedPreparedTemplates = 16;
    // Prepared pyramids can be significantly larger than their source images.
    // Keep their native OpenCV allocations capped separately from TemplateCache.
    private const long MaxPreparedTemplateCacheBytes = 64L * 1024 * 1024;
    private const int MaxPreparedScales = 256;

    // Keep the cache bounded and track file metadata so replacing a template on
    // disk takes effect without requiring an application restart.
    private static readonly Dictionary<string, TemplateCacheEntry> TemplateCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<PreparedTemplateCacheKey, PreparedTemplateCacheEntry>
        PreparedTemplateCache = new();
    private static readonly object CacheLock = new();
    private static long _cacheAccessCounter;
    private static long _preparedTemplateCacheBytes;
    private static readonly Lazy<Mat> MorphologyKernel = new(() =>
        Cv2.GetStructuringElement(MorphShapes.Rect, new Size(20, 15)));

    private sealed class TemplateCacheEntry(Mat template, long length, DateTime lastWriteTimeUtc, long lastAccess)
        : IDisposable
    {
        public Mat Template { get; } = template;
        public long Length { get; } = length;
        public DateTime LastWriteTimeUtc { get; } = lastWriteTimeUtc;
        public long LastAccess { get; set; } = lastAccess;

        public void Dispose() => Template.Dispose();
    }

    internal readonly record struct TemplateFileStamp(long Length, DateTime LastWriteTimeUtc);

    private readonly record struct PreparedTemplateCacheKey(
        string FullPath, long MinScaleBits, long MaxScaleBits, long StepBits)
    {
        public static PreparedTemplateCacheKey Create(
            string fullPath, double minScale, double maxScale, double step) => new(
            fullPath,
            BitConverter.DoubleToInt64Bits(minScale),
            BitConverter.DoubleToInt64Bits(maxScale),
            BitConverter.DoubleToInt64Bits(step));
    }

    internal sealed class PreparedTemplateCacheEntry(
        PreparedTemplate template, TemplateFileStamp fileStamp, long estimatedBytes, long lastAccess)
        : IDisposable
    {
        public PreparedTemplate Template { get; } = template;
        public TemplateFileStamp FileStamp { get; } = fileStamp;
        public long EstimatedBytes { get; } = estimatedBytes;
        public long LastAccess { get; set; } = lastAccess;
        public int LeaseCount { get; set; }
        public bool Retired { get; set; }
        private bool IsDisposed { get; set; }

        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            Template.Dispose();
        }
    }

    /// <summary>
    /// A pre-scaled template pyramid. Reuse one instance across polling frames
    /// to avoid resizing the same template for every captured image.
    /// </summary>
    public sealed class PreparedTemplate : IDisposable
    {
        internal IReadOnlyList<Mat> Scales { get; }
        public int ScaleCount => Scales.Count;
        internal int MinimumWidth { get; }
        internal int MinimumHeight { get; }

        internal PreparedTemplate(IReadOnlyList<Mat> scales)
        {
            if (scales.Count == 0)
                throw new ArgumentException("At least one template scale is required.", nameof(scales));

            Scales = scales;
            MinimumWidth = scales.Min(scale => scale.Cols);
            MinimumHeight = scales.Min(scale => scale.Rows);
        }

        public void Dispose()
        {
            foreach (var scale in Scales)
                scale.Dispose();
        }
    }

    /// <summary>
    /// A temporary, read-only lease over a cached pre-scaled template pyramid.
    /// Dispose the lease when matching is finished; do not dispose <see cref="Template"/>
    /// directly, because its Mats are shared by other active leases.
    /// </summary>
    public sealed class PreparedTemplateLease : IDisposable
    {
        private PreparedTemplateCacheEntry? _entry;

        public PreparedTemplate Template => _entry?.Template
            ?? throw new ObjectDisposedException(nameof(PreparedTemplateLease));

        internal PreparedTemplateLease(PreparedTemplateCacheEntry entry) => _entry = entry;

        public void Dispose()
        {
            var entry = Interlocked.Exchange(ref _entry, null);
            if (entry != null)
                ReleasePreparedTemplate(entry);
        }
    }

    /// <summary>
    /// Load a template Mat from file path, with caching.
    /// </summary>
    public static Mat LoadTemplate(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
            throw new ArgumentException("Template image path is required.", nameof(imagePath));

        var fullPath = Path.GetFullPath(imagePath);
        var file = new FileInfo(fullPath);
        if (!file.Exists)
        {
            lock (CacheLock)
                RemoveCachedTemplateFileLocked(fullPath);
            throw new FileNotFoundException($"Template image not found: {fullPath}", fullPath);
        }

        lock (CacheLock)
        {
            if (TemplateCache.TryGetValue(fullPath, out var cached))
            {
                if (cached.Length == file.Length && cached.LastWriteTimeUtc == file.LastWriteTimeUtc)
                {
                    cached.LastAccess = ++_cacheAccessCounter;
                    return cached.Template.Clone();
                }

                cached.Dispose();
                TemplateCache.Remove(fullPath);
            }

            var mat = Cv2.ImRead(fullPath, ImreadModes.Color);
            if (mat.Empty())
            {
                mat.Dispose();
                throw new InvalidDataException($"Template image failed to load: {fullPath}");
            }

            if (TemplateCache.Count >= MaxCachedTemplates)
            {
                var oldest = TemplateCache.MinBy(item => item.Value.LastAccess);
                oldest.Value.Dispose();
                TemplateCache.Remove(oldest.Key);
            }

            TemplateCache[fullPath] = new TemplateCacheEntry(
                mat, file.Length, file.LastWriteTimeUtc, ++_cacheAccessCounter);
            return mat.Clone();
        }
    }

    /// <summary>
    /// Clear the template cache.
    /// </summary>
    public static void ClearCache()
    {
        lock (CacheLock)
        {
            foreach (var item in PreparedTemplateCache.ToArray())
                RetirePreparedTemplateEntryLocked(item.Key, item.Value);

            foreach (var entry in TemplateCache.Values)
                entry.Dispose();
            TemplateCache.Clear();
        }
    }

    /// <summary>
    /// Acquire a shared pre-scaled template pyramid for a file-backed template.
    /// Entries are invalidated when the file length or UTC write time changes,
    /// bounded by count and estimated native-memory size, and remain alive until
    /// every active lease is released.
    /// </summary>
    public static PreparedTemplateLease AcquirePreparedTemplate(
        string imagePath,
        double minScale = 0.5,
        double maxScale = 1.5,
        double step = 0.1,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
            throw new ArgumentException("Template image path is required.", nameof(imagePath));
        ValidateScaleRange(minScale, maxScale, step);
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(imagePath);
        // File paths are case-insensitive on the supported Windows target. Normalize
        // the key so differently-cased references share one pyramid as well.
        var key = PreparedTemplateCacheKey.Create(
            fullPath.ToUpperInvariant(), minScale, maxScale, step);
        // A file may be replaced while another thread is building its pyramid. Retry
        // once in that uncommon case, but always do decoding/resizing outside the lock.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var fileStamp = GetTemplateFileStampOrEvict(fullPath);
            lock (CacheLock)
            {
                InvalidatePreparedTemplatesForFileLocked(fullPath, fileStamp);
                if (PreparedTemplateCache.TryGetValue(key, out var cached))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return CreatePreparedTemplateLeaseLocked(cached);
                }
            }

            // A concurrent caller may prepare the same key at the same time; the
            // loser safely drops its local pyramid below and leases the winner instead.
            var (preparedTemplate, stableStamp) = BuildPreparedTemplateFromStableFile(
                fullPath, minScale, maxScale, step, cancellationToken);
            var cacheOwnsPreparedTemplate = false;
            try
            {
                lock (CacheLock)
                {
                    var currentStamp = GetTemplateFileStampOrEvict(fullPath);
                    if (currentStamp != stableStamp)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        continue;
                    }

                    InvalidatePreparedTemplatesForFileLocked(fullPath, currentStamp);
                    if (PreparedTemplateCache.TryGetValue(key, out var cached))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return CreatePreparedTemplateLeaseLocked(cached);
                    }

                    var estimatedBytes = EstimatePreparedTemplateBytes(preparedTemplate);
                    var entry = new PreparedTemplateCacheEntry(
                        preparedTemplate, currentStamp, estimatedBytes, ++_cacheAccessCounter);

                    if (estimatedBytes > MaxPreparedTemplateCacheBytes)
                    {
                        // Never retain one unusually large source image for the whole app
                        // lifetime. The lease still owns it until this matching operation ends.
                        entry.Retired = true;
                        cacheOwnsPreparedTemplate = true;
                        return CreatePreparedTemplateLeaseLocked(entry);
                    }

                    MakePreparedTemplateCapacityLocked(estimatedBytes);
                    PreparedTemplateCache[key] = entry;
                    _preparedTemplateCacheBytes += estimatedBytes;
                    cacheOwnsPreparedTemplate = true;
                    return CreatePreparedTemplateLeaseLocked(entry);
                }
            }
            finally
            {
                if (!cacheOwnsPreparedTemplate)
                    preparedTemplate.Dispose();
            }
        }

        throw new IOException($"Template changed while its scaled variants were being prepared: {fullPath}");
    }

    private static (PreparedTemplate Template, TemplateFileStamp FileStamp)
        BuildPreparedTemplateFromStableFile(
            string fullPath, double minScale, double maxScale, double step,
            CancellationToken cancellationToken)
    {
        // Last-write time has finite filesystem precision. Reading it before and after
        // decoding still prevents caching the usual save/replace race without holding
        // a global lock during native image processing.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = GetTemplateFileStampOrEvict(fullPath);
            using var sourceTemplate = LoadTemplate(fullPath);
            cancellationToken.ThrowIfCancellationRequested();

            var preparedTemplate = PrepareTemplate(sourceTemplate, minScale, maxScale, step);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var after = GetTemplateFileStampOrEvict(fullPath);
                if (before == after)
                    return (preparedTemplate, after);
            }
            catch
            {
                preparedTemplate.Dispose();
                throw;
            }

            preparedTemplate.Dispose();
        }

        throw new IOException($"Template changed while it was being loaded: {fullPath}");
    }

    private static TemplateFileStamp GetTemplateFileStamp(string fullPath)
    {
        var file = new FileInfo(fullPath);
        file.Refresh();
        if (!file.Exists)
            throw new FileNotFoundException($"Template image not found: {fullPath}", fullPath);
        return new TemplateFileStamp(file.Length, file.LastWriteTimeUtc);
    }

    private static TemplateFileStamp GetTemplateFileStampOrEvict(string fullPath)
    {
        try
        {
            return GetTemplateFileStamp(fullPath);
        }
        catch (FileNotFoundException)
        {
            lock (CacheLock)
                RemoveCachedTemplateFileLocked(fullPath);
            throw;
        }
    }

    private static void RemoveCachedTemplateFileLocked(string fullPath)
    {
        if (TemplateCache.Remove(fullPath, out var cachedTemplate))
            cachedTemplate.Dispose();

        foreach (var item in PreparedTemplateCache
                     .Where(item => string.Equals(item.Key.FullPath, fullPath,
                         StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            RetirePreparedTemplateEntryLocked(item.Key, item.Value);
        }
    }

    private static long EstimatePreparedTemplateBytes(PreparedTemplate preparedTemplate)
    {
        // Step includes OpenCV row alignment, so this accounts for each scaled Mat's
        // actual pixel-buffer footprint rather than only the number of cache entries.
        try
        {
            long total = 0;
            foreach (var scale in preparedTemplate.Scales)
            {
                total = checked(total + checked((long)scale.Rows * (long)scale.Step()));
            }

            return total;
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    private static PreparedTemplateLease CreatePreparedTemplateLeaseLocked(
        PreparedTemplateCacheEntry entry)
    {
        entry.LastAccess = ++_cacheAccessCounter;
        entry.LeaseCount++;
        return new PreparedTemplateLease(entry);
    }

    private static void ReleasePreparedTemplate(PreparedTemplateCacheEntry entry)
    {
        lock (CacheLock)
        {
            if (entry.LeaseCount <= 0)
                return;

            entry.LeaseCount--;
            if (entry.Retired && entry.LeaseCount == 0)
                entry.Dispose();
        }
    }

    private static void InvalidatePreparedTemplatesForFileLocked(
        string fullPath, TemplateFileStamp currentStamp)
    {
        foreach (var item in PreparedTemplateCache
                     .Where(item => string.Equals(item.Key.FullPath, fullPath,
                         StringComparison.OrdinalIgnoreCase) && item.Value.FileStamp != currentStamp)
                     .ToArray())
        {
            RetirePreparedTemplateEntryLocked(item.Key, item.Value);
        }
    }

    private static void MakePreparedTemplateCapacityLocked(long additionalBytes)
    {
        while (PreparedTemplateCache.Count >= MaxCachedPreparedTemplates ||
               _preparedTemplateCacheBytes > MaxPreparedTemplateCacheBytes - additionalBytes)
        {
            if (PreparedTemplateCache.Count == 0)
                return;

            var oldest = PreparedTemplateCache.MinBy(item => item.Value.LastAccess);
            RetirePreparedTemplateEntryLocked(oldest.Key, oldest.Value);
        }
    }

    private static void RetirePreparedTemplateEntryLocked(
        PreparedTemplateCacheKey key, PreparedTemplateCacheEntry entry)
    {
        if (!PreparedTemplateCache.Remove(key))
            return;

        _preparedTemplateCacheBytes -= entry.EstimatedBytes;
        entry.Retired = true;
        if (entry.LeaseCount == 0)
            entry.Dispose();
    }

    public static PreparedTemplate PrepareTemplate(
        Mat templateMat, double minScale = 0.5, double maxScale = 1.5, double step = 0.1)
    {
        ArgumentNullException.ThrowIfNull(templateMat);
        if (templateMat.Empty())
            throw new ArgumentException("Template image is empty.", nameof(templateMat));
        ValidateScaleRange(minScale, maxScale, step);

        var estimatedScaleCountValue = Math.Floor((maxScale - minScale) / step) + 2;
        if (!double.IsFinite(estimatedScaleCountValue) || estimatedScaleCountValue > MaxPreparedScales)
            throw new ArgumentOutOfRangeException(nameof(step),
                $"Scale range produces more than {MaxPreparedScales} templates; increase the step size.");
        var estimatedScaleCount = (int)estimatedScaleCountValue;

        var scales = new List<Mat>(estimatedScaleCount);
        var dimensions = new HashSet<(int Width, int Height)>();

        void AddScale(double scale)
        {
            int width = Math.Max(1, (int)Math.Round(templateMat.Cols * scale));
            int height = Math.Max(1, (int)Math.Round(templateMat.Rows * scale));
            if (!dimensions.Add((width, height))) return;

            if (width == templateMat.Cols && height == templateMat.Rows)
            {
                scales.Add(templateMat.Clone());
                return;
            }

            var resized = new Mat();
            try
            {
                Cv2.Resize(templateMat, resized, new Size(width, height), interpolation:
                    scale < 1 ? InterpolationFlags.Area : InterpolationFlags.Linear);
                scales.Add(resized);
            }
            catch
            {
                resized.Dispose();
                throw;
            }
        }

        try
        {
            // Preserve the previous fast path: exact-size matching is attempted first.
            AddScale(1.0);
            for (var scale = minScale; scale <= maxScale + step * 1e-6; scale += step)
                AddScale(scale);

            return new PreparedTemplate(scales);
        }
        catch
        {
            foreach (var scale in scales)
                scale.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Multi-scale template matching.
    /// Returns the center point (relative to the source image) if match found, or null.
    /// </summary>
    public static (Point point, double confidence)? FindTemplate(
        Bitmap screenSource, Mat templateMat,
        double minScale = 0.5, double maxScale = 1.5, double step = 0.1,
        double threshold = 0.8)
    {
        using var prepared = PrepareTemplate(templateMat, minScale, maxScale, step);
        return FindTemplate(screenSource, prepared, threshold);
    }

    public static (Point point, double confidence)? FindTemplate(
        Bitmap screenSource, PreparedTemplate preparedTemplate, double threshold = 0.8)
    {
        ArgumentNullException.ThrowIfNull(screenSource);
        ArgumentNullException.ThrowIfNull(preparedTemplate);
        ValidateThreshold(threshold);

        using var refMat = BitmapConverter.ToMat(screenSource);
        foreach (var scaledTemplate in preparedTemplate.Scales)
        {
            if (!TryMatch(refMat, scaledTemplate, out var confidence, out var location))
                continue;
            if (confidence < threshold)
                continue;

            return (new Point(
                location.X + scaledTemplate.Cols / 2,
                location.Y + scaledTemplate.Rows / 2), confidence);
        }

        return null;
    }

    private static bool TryMatch(Mat refMat, Mat tplMat, out double maxVal, out OpenCvSharp.Point maxLoc)
    {
        maxVal = 0;
        maxLoc = default;

        if (tplMat.Width > refMat.Width || tplMat.Height > refMat.Height)
            return false;

        using var resultMat = new Mat();
        Cv2.MatchTemplate(refMat, tplMat, resultMat, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(resultMat, out _, out maxVal, out _, out maxLoc);
        return true;
    }

    /// <summary>
    /// Multi-scale template matching within a single ROI.
    /// Returns the best match across all scales (even below threshold).
    /// </summary>
    private static bool TryMatchMultiScale(Mat roi, PreparedTemplate preparedTemplate,
        out double bestMaxVal, out OpenCvSharp.Point bestLoc,
        out int bestTplW, out int bestTplH)
    {
        bestMaxVal = 0;
        bestLoc = default;
        bestTplW = 0;
        bestTplH = 0;

        foreach (var scaledTemplate in preparedTemplate.Scales)
        {
            if (!TryMatch(roi, scaledTemplate, out var maxVal, out var maxLoc) || maxVal <= bestMaxVal)
                continue;

            bestMaxVal = maxVal;
            bestLoc = maxLoc;
            bestTplW = scaledTemplate.Cols;
            bestTplH = scaledTemplate.Rows;
        }

        return bestMaxVal > 0;
    }

    /// <summary>
    /// Detect a color center using HSV color space thresholding.
    /// Returns the center point (relative to the source image) or null.
    /// </summary>
    public static Point? DetectColorCenter(Bitmap screenFrame, Color targetRgb, int hueTolerance = 5, int svTolerance = 10)
    {
        using var frame = BitmapConverter.ToMat(screenFrame);
        using var hsvMat = new Mat();
        Cv2.CvtColor(frame, hsvMat, ColorConversionCodes.BGR2HSV);

        using var mask = CreateHsvMask(hsvMat, targetRgb, hueTolerance, svTolerance);

        // Morphological close to fill gaps
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, MorphologyKernel.Value);

        Cv2.FindContours(mask, out OpenCvSharp.Point[][] contours, out HierarchyIndex[] hierarchy,
            RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        if (contours.Length == 0) return null;

        OpenCvSharp.Point[]? largestContour = null;
        double largestArea = double.MinValue;
        foreach (var contour in contours)
        {
            var area = Cv2.ContourArea(contour);
            if (area <= largestArea) continue;
            largestArea = area;
            largestContour = contour;
        }

        if (largestContour == null) return null;
        var rect = Cv2.BoundingRect(largestContour);

        return new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
    }

    /// <summary>
    /// Apply HSV color filter to isolate regions of a target color, then perform template matching
    /// within those isolated regions. Returns the best match center point and confidence, or null.
    /// This combines Steps 3-5 (HSV filtering + morphology + contour detection) with template matching.
    /// </summary>
    public static (Point point, double confidence)? FindTemplateWithColorFilter(
        Bitmap screenFrame, Mat templateMat,
        Color targetRgb, int hueTolerance = 5, int svTolerance = 10,
        double templateThreshold = 0.8)
    {
        using var prepared = PrepareTemplate(templateMat, 0.5, 1.5, 0.05);
        return FindTemplateWithColorFilter(
            screenFrame, prepared, targetRgb, hueTolerance, svTolerance, templateThreshold);
    }

    public static (Point point, double confidence)? FindTemplateWithColorFilter(
        Bitmap screenFrame, PreparedTemplate preparedTemplate,
        Color targetRgb, int hueTolerance = 5, int svTolerance = 10,
        double templateThreshold = 0.8)
    {
        ArgumentNullException.ThrowIfNull(preparedTemplate);
        ValidateThreshold(templateThreshold);

        using var frame = BitmapConverter.ToMat(screenFrame);
        using var hsvMat = new Mat();
        Cv2.CvtColor(frame, hsvMat, ColorConversionCodes.BGR2HSV);

        using var mask = CreateHsvMask(hsvMat, targetRgb, hueTolerance, svTolerance);

        // Morphological close to connect broken regions
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, MorphologyKernel.Value);

        // Build a filtered frame where only matching-color pixels are kept (rest black)
        using var filteredFrame = new Mat();
        frame.CopyTo(filteredFrame, mask);

        // Find contours, and for each bounding rect, run template matching on filtered frame
        Cv2.FindContours(mask, out OpenCvSharp.Point[][] contours, out HierarchyIndex[] hierarchy,
            RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        if (contours.Length == 0) return null;

        double bestMaxVal = 0;
        OpenCvSharp.Point bestLoc = default;
        int bestRectW = 0, bestRectH = 0;
        int bestRectX = 0, bestRectY = 0;
        int bestMatchedTplW = 0;
        int bestMatchedTplH = 0;
        int minTplW = preparedTemplate.MinimumWidth;
        int minTplH = preparedTemplate.MinimumHeight;

        // Try matching in each color-filtered contour's bounding rect (multi-scale)
        foreach (var contour in contours)
        {
            var rect = Cv2.BoundingRect(contour);
            if (rect.Width < minTplW || rect.Height < minTplH) continue;

            using var roi = new Mat(filteredFrame, rect);
            if (roi.Empty()) continue;

            if (!TryMatchMultiScale(roi, preparedTemplate,
                out double maxVal, out OpenCvSharp.Point maxLoc, out int matchedTplW, out int matchedTplH)) continue;

            if (maxVal > bestMaxVal)
            {
                bestMaxVal = maxVal;
                bestLoc = maxLoc;
                bestRectX = rect.X;
                bestRectY = rect.Y;
                bestRectW = rect.Width;
                bestRectH = rect.Height;
                bestMatchedTplW = matchedTplW;
                bestMatchedTplH = matchedTplH;
            }

            if (maxVal > templateThreshold)
                break; // good enough
        }

        if (bestMaxVal > templateThreshold)
        {
            int centerX = bestRectX + bestLoc.X + bestMatchedTplW / 2;
            int centerY = bestRectY + bestLoc.Y + bestMatchedTplH / 2;
            return (new Point(centerX, centerY), bestMaxVal);
        }

        return null;
    }

    /// <summary>
    /// Detect multiple color centers using HSV filtering.
    /// Returns up to maxTargets center points, sorted by contour area (largest first).
    /// </summary>
    public static List<Point> DetectMultipleColorCenters(Bitmap screenFrame, Color targetRgb, int hueTolerance = 5, int svTolerance = 10, int maxTargets = 5)
    {
        var results = new List<Point>();
        if (maxTargets <= 0) return results;

        using var frame = BitmapConverter.ToMat(screenFrame);
        using var hsvMat = new Mat();
        Cv2.CvtColor(frame, hsvMat, ColorConversionCodes.BGR2HSV);

        using var mask = CreateHsvMask(hsvMat, targetRgb, hueTolerance, svTolerance);
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, MorphologyKernel.Value);

        Cv2.FindContours(mask, out OpenCvSharp.Point[][] contours, out HierarchyIndex[] hierarchy,
            RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        if (contours.Length == 0) return results;

        // Sort by contour area descending and take up to maxTargets
        var candidates = new List<(OpenCvSharp.Point[] Contour, double Area)>();
        foreach (var contour in contours)
        {
            var area = Cv2.ContourArea(contour);
            if (area > 50) // Filter out noise
                candidates.Add((contour, area));
        }

        candidates.Sort((left, right) => right.Area.CompareTo(left.Area));
        var resultCount = Math.Min(maxTargets, candidates.Count);
        for (var index = 0; index < resultCount; index++)
        {
            var item = candidates[index];
            var rect = Cv2.BoundingRect(item.Contour);
            results.Add(new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2));
        }

        return results;
    }

    /// <summary>
    /// Detect multiple targets using HSV color filter + template matching.
    /// Returns up to maxTargets center points.
    /// </summary>
    public static List<Point> DetectMultipleTargets(Bitmap screenFrame, Mat templateMat, Color targetRgb, int hueTolerance = 5, int svTolerance = 10, double templateThreshold = 0.8, int maxTargets = 5)
    {
        using var prepared = PrepareTemplate(templateMat, 0.5, 1.5, 0.05);
        return DetectMultipleTargets(
            screenFrame, prepared, targetRgb, hueTolerance, svTolerance, templateThreshold, maxTargets);
    }

    public static List<Point> DetectMultipleTargets(Bitmap screenFrame, PreparedTemplate preparedTemplate, Color targetRgb, int hueTolerance = 5, int svTolerance = 10, double templateThreshold = 0.8, int maxTargets = 5)
    {
        var results = new List<Point>();
        if (maxTargets <= 0) return results;
        ArgumentNullException.ThrowIfNull(preparedTemplate);
        ValidateThreshold(templateThreshold);

        using var frame = BitmapConverter.ToMat(screenFrame);
        using var hsvMat = new Mat();
        Cv2.CvtColor(frame, hsvMat, ColorConversionCodes.BGR2HSV);

        using var mask = CreateHsvMask(hsvMat, targetRgb, hueTolerance, svTolerance);
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, MorphologyKernel.Value);

        // Build a filtered frame where only matching-color pixels are kept (rest black)
        using var filteredFrame = new Mat();
        frame.CopyTo(filteredFrame, mask);

        Cv2.FindContours(mask, out OpenCvSharp.Point[][] contours, out HierarchyIndex[] hierarchy,
            RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        if (contours.Length == 0) return results;

        var matches = new List<(Point point, double confidence, double area)>();
        int minTplW = preparedTemplate.MinimumWidth;
        int minTplH = preparedTemplate.MinimumHeight;

        foreach (var contour in contours)
        {
            var rect = Cv2.BoundingRect(contour);
            if (rect.Width < minTplW || rect.Height < minTplH) continue;

            using var roi = new Mat(filteredFrame, rect);
            if (roi.Empty()) continue;

            if (!TryMatchMultiScale(roi, preparedTemplate,
                out double maxVal, out OpenCvSharp.Point maxLoc, out int matchedTplW, out int matchedTplH)) continue;

            if (maxVal > templateThreshold)
            {
                int centerX = rect.X + maxLoc.X + matchedTplW / 2;
                int centerY = rect.Y + maxLoc.Y + matchedTplH / 2;
                matches.Add((new Point(centerX, centerY), maxVal, Cv2.ContourArea(contour)));
            }
        }

        // Sort by confidence descending, then by area, take up to maxTargets
        return matches
            .OrderByDescending(m => m.confidence)
            .ThenByDescending(m => m.area)
            .Take(maxTargets)
            .Select(m => m.point)
            .ToList();
    }

    /// <summary>
    /// Calculate the ratio of pixels matching the target color within the frame.
    /// Returns 0.0 to 1.0.
    /// </summary>
    public static double CalculateColorFillRatio(Bitmap screenFrame, Color targetRgb, int hueTolerance = 5, int svTolerance = 10)
    {
        using var frame = BitmapConverter.ToMat(screenFrame);
        using var hsvMat = new Mat();
        Cv2.CvtColor(frame, hsvMat, ColorConversionCodes.BGR2HSV);

        using var mask = CreateHsvMask(hsvMat, targetRgb, hueTolerance, svTolerance);

        double matchingPixels = Cv2.CountNonZero(mask);
        double totalPixels = mask.Rows * mask.Cols;

        return totalPixels > 0 ? matchingPixels / totalPixels : 0.0;
    }

    /// <summary>
    /// Apply HSV color filter to a Bitmap, keeping only pixels matching the target color.
    /// Non-matching pixels become black. Useful for creating clean reference images
    /// for ColorMotion snip.
    /// </summary>
    public static Bitmap ApplyHsvFilter(Bitmap source, Color targetRgb, int hueTolerance = 5, int svTolerance = 10)
    {
        using var frame = BitmapConverter.ToMat(source);
        using var hsvMat = new Mat();
        Cv2.CvtColor(frame, hsvMat, ColorConversionCodes.BGR2HSV);

        using var mask = CreateHsvMask(hsvMat, targetRgb, hueTolerance, svTolerance);
        using var result = new Mat(frame.Size(), MatType.CV_8UC3, Scalar.Black);
        frame.CopyTo(result, mask);
        return BitmapConverter.ToBitmap(result);
    }

    private static Mat CreateHsvMask(Mat hsvMat, Color targetRgb, int hueTolerance, int svTolerance)
    {
        hueTolerance = Math.Clamp(hueTolerance, 0, 179);
        svTolerance = Math.Clamp(svTolerance, 0, 255);

        var (rawHue, rawSaturation, rawValue) = RgbToHsv(targetRgb);
        int hue = Math.Clamp((int)Math.Round(rawHue), 0, 179);
        int saturation = Math.Clamp((int)Math.Round(rawSaturation), 0, 255);
        int value = Math.Clamp((int)Math.Round(rawValue), 0, 255);
        int minSaturation = Math.Max(0, saturation - svTolerance);
        int maxSaturation = Math.Min(255, saturation + svTolerance);
        int minValue = Math.Max(0, value - svTolerance);
        int maxValue = Math.Min(255, value + svTolerance);

        var mask = new Mat();
        if (hueTolerance >= 179)
        {
            Cv2.InRange(hsvMat,
                new Scalar(0, minSaturation, minValue),
                new Scalar(179, maxSaturation, maxValue), mask);
            return mask;
        }

        int minHue = hue - hueTolerance;
        int maxHue = hue + hueTolerance;
        if (minHue >= 0 && maxHue <= 179)
        {
            Cv2.InRange(hsvMat,
                new Scalar(minHue, minSaturation, minValue),
                new Scalar(maxHue, maxSaturation, maxValue), mask);
            return mask;
        }

        // Hue is circular in OpenCV (179 is adjacent to 0). Build the two
        // edge ranges and combine them so reds near that boundary are retained.
        using var wrappedMask = new Mat();
        if (minHue < 0)
        {
            Cv2.InRange(hsvMat,
                new Scalar(0, minSaturation, minValue),
                new Scalar(maxHue, maxSaturation, maxValue), mask);
            Cv2.InRange(hsvMat,
                new Scalar(180 + minHue, minSaturation, minValue),
                new Scalar(179, maxSaturation, maxValue), wrappedMask);
        }
        else
        {
            Cv2.InRange(hsvMat,
                new Scalar(minHue, minSaturation, minValue),
                new Scalar(179, maxSaturation, maxValue), mask);
            Cv2.InRange(hsvMat,
                new Scalar(0, minSaturation, minValue),
                new Scalar(maxHue - 180, maxSaturation, maxValue), wrappedMask);
        }

        Cv2.BitwiseOr(mask, wrappedMask, mask);
        return mask;
    }

    private static void ValidateScaleRange(double minScale, double maxScale, double step)
    {
        if (!double.IsFinite(minScale) || minScale <= 0)
            throw new ArgumentOutOfRangeException(nameof(minScale), "Minimum scale must be finite and greater than zero.");
        if (!double.IsFinite(maxScale) || maxScale < minScale)
            throw new ArgumentOutOfRangeException(nameof(maxScale), "Maximum scale must be finite and at least the minimum scale.");
        if (!double.IsFinite(step) || step <= 0)
            throw new ArgumentOutOfRangeException(nameof(step), "Scale step must be finite and greater than zero.");
    }

    private static void ValidateThreshold(double threshold)
    {
        if (!double.IsFinite(threshold) || threshold < 0 || threshold > 1)
            throw new ArgumentOutOfRangeException(nameof(threshold), "Match threshold must be between 0 and 1.");
    }

    private static (double h, double s, double v) RgbToHsv(Color c)
    {
        double r = c.R / 255.0;
        double g = c.G / 255.0;
        double b = c.B / 255.0;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        double h = 0;
        if (delta > 1e-6)
        {
            if (Math.Abs(max - r) < 1e-6)
                h = 60 * (((g - b) / delta) % 6);
            else if (Math.Abs(max - g) < 1e-6)
                h = 60 * ((b - r) / delta + 2);
            else
                h = 60 * ((r - g) / delta + 4);
        }
        if (h < 0) h += 360;

        double s = max > 1e-6 ? delta / max : 0;
        double v = max;

        // OpenCV HSV: H(0-179), S(0-255), V(0-255)
        return (h / 2.0, s * 255, v * 255);
    }
}
