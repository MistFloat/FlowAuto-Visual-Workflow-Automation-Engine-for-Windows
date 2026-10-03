using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Reflection;
using System.Text.Json;
using FlowAuto.Core;
using FlowAuto.Engine;
using FlowAuto.Models;
using OpenCvSharp;
using OpenCvSharp.Extensions;

namespace FlowAuto.RegressionTests;

internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("template matching locates an exact pattern", () => RunSync(TestTemplateMatching)),
            ("invalid scale ranges fail fast", () => RunSync(TestInvalidScaleRange)),
            ("HSV matching wraps across hue 179/0", () => RunSync(TestHueWrapAround)),
            ("capture regions clamp safely for reusable polling buffers", () => RunSync(TestCaptureRegionClamping)),
            ("ColorCal reuses matching capture requests", () => RunSync(TestColorCalCaptureGrouping)),
            ("ColorCal evaluates nested ternaries and arithmetic conditions", () => RunSync(TestColorCalExpressionEvaluation)),
            ("template cache refreshes changed files", () => RunSync(TestTemplateCacheRefresh)),
            ("prepared template cache reuses leases and refreshes files", () => RunSync(TestPreparedTemplateCache)),
            ("diagnostic PNG writes are queued and retained within a cap", TestDiagnosticArtifactsAsync),
            ("flow validation rejects unsafe parameters and cycles", () => RunSync(TestFlowValidation)),
            ("bundled browser test flows remain valid", () => RunSync(TestBundledFlows)),
            ("adding a Loop creates one paired LoopEnd", () => RunSync(TestLoopPairCreation)),
            ("gate waits for both AND inputs", TestGateAndTraversalAsync),
            ("false gate result blocks downstream execution", TestGateFalseStopsTraversalAsync),
            ("indexed traversal visits large chains and ignores missing targets", TestIndexedGraphTraversalAsync),
            ("indexed LoopEnd pairing preserves repeated loop execution", TestIndexedLoopTraversalAsync),
            ("node timeout cancels the active operation", TestNodeTimeoutAsync),
            ("flow stop interrupts long waits", TestFlowCancellationAsync),
            ("prepared templates remove repeated resize work", () => RunSync(BenchmarkPreparedTemplates))
        };

        Console.WriteLine($"FlowAuto regression suite ({tests.Length} tests)");
        foreach (var test in tests)
        {
            try
            {
                await test.Run();
                _passed++;
                Console.WriteLine($"PASS  {test.Name}");
            }
            catch (Exception exception)
            {
                _failed++;
                Console.WriteLine($"FAIL  {test.Name}");
                Console.WriteLine($"      {exception.GetType().Name}: {exception.Message}");
            }
        }

        ImageRecognition.ClearCache();
        Console.WriteLine($"Result: {_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static Task RunSync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    private static void TestTemplateMatching()
    {
        using var template = CreatePatternBitmap(20, 16);
        using var source = new Bitmap(120, 90, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(source))
        {
            graphics.Clear(Color.FromArgb(16, 24, 32));
            graphics.DrawImageUnscaled(template, 37, 26);
        }

        using var templateMat = BitmapConverter.ToMat(template);
        var result = ImageRecognition.FindTemplate(source, templateMat, 0.8, 1.2, 0.1, 0.98);

        Assert(result != null, "Expected the template to be found.");
        var match = result!.Value;
        Assert(Math.Abs(match.point.X - 47) <= 1, $"Unexpected X center: {match.point.X}.");
        Assert(Math.Abs(match.point.Y - 34) <= 1, $"Unexpected Y center: {match.point.Y}.");
    }

    private static void TestInvalidScaleRange()
    {
        using var template = new Mat(10, 10, MatType.CV_8UC3, Scalar.White);
        AssertThrows<ArgumentOutOfRangeException>(() =>
            ImageRecognition.PrepareTemplate(template, 0.5, 1.5, 0));
        AssertThrows<ArgumentOutOfRangeException>(() =>
            ImageRecognition.PrepareTemplate(template, 1.5, 0.5, 0.1));
        AssertThrows<ArgumentOutOfRangeException>(() =>
            ImageRecognition.PrepareTemplate(template, 0.5, 1.5, 0.0001));
    }

    private static void TestHueWrapAround()
    {
        using var bitmap = new Bitmap(100, 60, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Black);
            using var nearHue179 = new SolidBrush(Color.FromArgb(255, 0, 8));
            graphics.FillRectangle(nearHue179, 20, 15, 40, 30);
        }

        var center = ImageRecognition.DetectColorCenter(bitmap, Color.Red, 5, 20);
        var ratio = ImageRecognition.CalculateColorFillRatio(bitmap, Color.Red, 5, 20);

        Assert(center != null, "A near-red hue on the 179 side of the HSV boundary was missed.");
        var detectedCenter = center!.Value;
        Assert(Math.Abs(detectedCenter.X - 40) <= 2 && Math.Abs(detectedCenter.Y - 30) <= 2,
            $"Unexpected color center: {detectedCenter}.");
        Assert(ratio > 0.19 && ratio < 0.21, $"Unexpected matching fill ratio: {ratio:F4}.");
    }

    private static void TestCaptureRegionClamping()
    {
        Assert(ScreenCapture.TryClampRegionToClientBounds(
                800, 600, 100, 200, int.MaxValue, int.MaxValue, out var clipped),
            "A partially visible capture region should be clamped, not rejected.");
        Assert(clipped == new Rectangle(100, 200, 700, 400),
            $"Unexpected clipped capture region: {clipped}.");

        Assert(!ScreenCapture.TryClampRegionToClientBounds(
                800, 600, 800, 0, 1, 1, out _),
            "A region starting outside the client area must not allocate a frame.");
        Assert(!ScreenCapture.TryClampRegionToClientBounds(
                800, 600, -1, 0, 20, 20, out _),
            "A negative capture origin must be rejected.");

        var session = new WindowRegionCaptureSession(IntPtr.Zero, 0, 0, 20, 20);
        try
        {
            Assert(session.Capture() == null,
                "A capture session without a valid window must not create a bitmap.");
        }
        finally
        {
            session.Dispose();
        }
        AssertThrows<ObjectDisposedException>(() => session.Capture());
    }

    private static void TestColorCalCaptureGrouping()
    {
        var executorType = typeof(FlowExecutor);
        var requestType = executorType.GetNestedType(
            "ColorCalCaptureRequest", BindingFlags.NonPublic);
        var countMethod = executorType.GetMethod(
            "CountColorCalCaptureUses", BindingFlags.Static | BindingFlags.NonPublic);

        Assert(requestType != null, "ColorCal capture request type was not found.");
        Assert(countMethod != null, "ColorCal capture grouping method was not found.");

        var constructor = requestType!
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(candidate => candidate.GetParameters().Length == 5);
        object CreateRequest(long windowHandle, int x, int y, int width, int height) =>
            constructor.Invoke([new IntPtr(windowHandle), x, y, width, height]);

        var requests = Array.CreateInstance(requestType, 4);
        requests.SetValue(CreateRequest(101, 0, 0, 640, 480), 0);
        requests.SetValue(CreateRequest(101, 0, 0, 640, 480), 1);
        requests.SetValue(CreateRequest(101, 10, 0, 640, 480), 2);
        requests.SetValue(CreateRequest(202, 0, 0, 640, 480), 3);

        var useCounts = countMethod!.Invoke(null, [requests]) as System.Collections.IDictionary;
        Assert(useCounts != null, "ColorCal capture grouping returned an unexpected result.");
        Assert(useCounts!.Count == 3,
            $"Expected three distinct capture requests, got {useCounts.Count}.");

        var counts = useCounts.Values.Cast<object>().Select(Convert.ToInt32).OrderBy(value => value).ToArray();
        Assert(counts.SequenceEqual([1, 1, 2]),
            $"Expected capture use counts [1, 1, 2], got [{string.Join(", ", counts)}].");
    }

    private static void TestColorCalExpressionEvaluation()
    {
        var method = typeof(FlowExecutor).GetMethod(
            "EvaluateColorCalExpressionV2", BindingFlags.Static | BindingFlags.NonPublic);
        Assert(method != null, "ColorCal expression evaluator was not found.");

        var results = new List<ColorCalTargetResult>
        {
            new() { Name = "A", X = 12, Y = 5, Found = true },
            new() { Name = "B", X = 31, Y = 20, Found = true }
        };
        var targets = new List<ColorCalTarget>
        {
            new() { Name = "A" },
            new() { Name = "B" }
        };

        int Evaluate(string expression)
        {
            try
            {
                return Convert.ToInt32(method!.Invoke(null,
                    [expression, results, targets, CreateContext(), "test-node"]));
            }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            {
                throw exception.InnerException;
            }
        }

        Assert(Evaluate("0 ? 2 : 1 ? 3 : 4") == 3,
            "A nested ternary in the false branch was evaluated incorrectly.");
        Assert(Evaluate("1 ? 2 : 0 ? 3 : 4") == 2,
            "A nested ternary selected the wrong matching colon.");
        Assert(Evaluate("Math.Abs(A.X - B.X) < 20 ? (A.X + B.X) / 10 : 9") == 4,
            "Arithmetic operands and Math.Abs were not evaluated in a ColorCal condition.");
        Assert(Evaluate("A.Found && B.X > A.X ? Math.Max(6, 5) : 0") == 6,
            "Logical conditions or Math.Max were not evaluated correctly.");
    }

    private static void TestTemplateCacheRefresh()
    {
        var tempDirectory = Path.Combine(
            Path.GetTempPath(), "FlowAuto.RegressionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var imagePath = Path.Combine(tempDirectory, "cache-refresh-test.png");
        try
        {
            SaveSolidBitmap(imagePath, Color.Red);
            using var first = ImageRecognition.LoadTemplate(imagePath);
            var firstPixel = first.Get<Vec3b>(0, 0);
            Assert(firstPixel.Item2 > 240 && firstPixel.Item0 < 15, "First cached image was not red.");

            SaveSolidBitmap(imagePath, Color.Blue);
            File.SetLastWriteTimeUtc(imagePath, DateTime.UtcNow.AddMinutes(1));
            using var second = ImageRecognition.LoadTemplate(imagePath);
            var secondPixel = second.Get<Vec3b>(0, 0);
            Assert(secondPixel.Item0 > 240 && secondPixel.Item2 < 15,
                "The cache returned stale data after the template file changed.");
        }
        finally
        {
            ImageRecognition.ClearCache();
            if (Directory.Exists(tempDirectory)) Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static void TestPreparedTemplateCache()
    {
        var tempDirectory = Path.Combine(
            Path.GetTempPath(), "FlowAuto.RegressionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var imagePath = Path.Combine(tempDirectory, "prepared-cache-test.png");
        try
        {
            using (var initialTemplate = CreatePatternBitmap(20, 16))
                initialTemplate.Save(imagePath, ImageFormat.Png);

            using var source = new Bitmap(100, 70, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(source))
            using (var initialTemplate = CreatePatternBitmap(20, 16))
            {
                graphics.Clear(Color.FromArgb(16, 24, 32));
                graphics.DrawImageUnscaled(initialTemplate, 31, 22);
            }

            using var first = ImageRecognition.AcquirePreparedTemplate(
                imagePath, 0.5, 1.5, 0.05);
            using var second = ImageRecognition.AcquirePreparedTemplate(
                imagePath, 0.5, 1.5, 0.05);
            Assert(ReferenceEquals(first.Template, second.Template),
                "Matching template requests did not reuse the same prepared pyramid.");
            Assert(ImageRecognition.FindTemplate(source, first.Template, 0.98) != null,
                "The leased prepared template did not match its source image.");

            // Clearing an app cache must not invalidate an in-flight match lease.
            ImageRecognition.ClearCache();
            Assert(ImageRecognition.FindTemplate(source, first.Template, 0.98) != null,
                "Clearing the cache disposed a prepared template while it was leased.");

            using (var replacementTemplate = CreatePatternBitmap(23, 16))
                replacementTemplate.Save(imagePath, ImageFormat.Png);
            File.SetLastWriteTimeUtc(imagePath, DateTime.UtcNow.AddMinutes(1));

            using var refreshed = ImageRecognition.AcquirePreparedTemplate(
                imagePath, 0.5, 1.5, 0.05);
            Assert(!ReferenceEquals(first.Template, refreshed.Template),
                "A changed template file reused a stale prepared pyramid.");

            // Force parallel cold-cache acquisition. Only one completed pyramid should
            // become resident; other callers must receive a lease over that same entry.
            ImageRecognition.ClearCache();
            var concurrentTemplates = new ConcurrentBag<ImageRecognition.PreparedTemplate>();
            Parallel.For(0, 8, _ =>
            {
                using var lease = ImageRecognition.AcquirePreparedTemplate(
                    imagePath, 0.5, 1.5, 0.05);
                concurrentTemplates.Add(lease.Template);
            });
            Assert(concurrentTemplates.Count == 8 &&
                   concurrentTemplates.All(template =>
                       ReferenceEquals(template, concurrentTemplates.First())),
                "Concurrent requests did not safely converge on the cached prepared pyramid.");

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            AssertThrows<OperationCanceledException>(() =>
                ImageRecognition.AcquirePreparedTemplate(
                    imagePath, 0.5, 1.5, 0.05, cancellation.Token));
        }
        finally
        {
            ImageRecognition.ClearCache();
            if (Directory.Exists(tempDirectory)) Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static async Task TestDiagnosticArtifactsAsync()
    {
        var tempDirectory = Path.Combine(
            Path.GetTempPath(), "FlowAuto.RegressionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var queuedPath = Path.Combine(tempDirectory, "queued.png");
        try
        {
            using (var bitmap = new Bitmap(32, 24, PixelFormat.Format24bppRgb))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.MediumPurple);
                Assert(DiagnosticArtifacts.QueuePng(bitmap, queuedPath),
                    "The diagnostic PNG queue rejected an idle write.");
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await DiagnosticArtifacts.WaitForIdleAsync(timeout.Token);
            Assert(File.Exists(queuedPath), "The queued diagnostic PNG was not written.");
            Assert(new FileInfo(queuedPath).Length > 0, "The queued diagnostic PNG was empty.");

            var now = DateTime.UtcNow;
            File.SetLastWriteTimeUtc(queuedPath, now.AddMinutes(-3));
            var oldPath = Path.Combine(tempDirectory, "old.png");
            var recentPath = Path.Combine(tempDirectory, "recent.png");
            var newestPath = Path.Combine(tempDirectory, "newest.png");
            SaveSolidBitmap(oldPath, Color.Red);
            SaveSolidBitmap(recentPath, Color.Green);
            SaveSolidBitmap(newestPath, Color.Blue);
            File.SetLastWriteTimeUtc(oldPath, now.AddMinutes(-2));
            File.SetLastWriteTimeUtc(recentPath, now.AddMinutes(-1));
            File.SetLastWriteTimeUtc(newestPath, now);

            DiagnosticArtifacts.PruneDirectory(tempDirectory, maxFiles: 2, maxBytes: 1024 * 1024);
            var retained = Directory.GetFiles(tempDirectory, "*.png")
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Assert(retained.SequenceEqual(["newest.png", "recent.png"]),
                $"Unexpected retained diagnostics: {string.Join(", ", retained)}.");
        }
        finally
        {
            if (Directory.Exists(tempDirectory)) Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static void TestLoopPairCreation()
    {
        Exception? failure = null;
        using var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                using var canvas = new FlowCanvas();
                var loop = FlowCanvas.CreateDefaultNode(NodeType.Loop);
                canvas.AddNode(loop);

                Assert(canvas.Nodes.Count == 2,
                    $"Adding a Loop should add its paired LoopEnd; found {canvas.Nodes.Count} nodes.");
                var loopEnd = canvas.Nodes.Single(node => node.NodeType == NodeType.LoopEnd);
                Assert(loopEnd.PairedLoopStartId == loop.NodeId,
                    "The generated LoopEnd is not paired to its LoopStart.");

                canvas.AddNode(FlowCanvas.CreateDefaultNode(NodeType.LoopEnd));
                Assert(canvas.Nodes.Count == 3,
                    "Manually adding a LoopEnd should not generate an additional LoopEnd.");
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                completed.Set();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!completed.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("The isolated FlowCanvas test did not complete.");
        thread.Join();
        if (failure != null) throw failure;
    }

    private static void TestFlowValidation()
    {
        var first = TimeoutNode("first");
        first.TimeoutMs = 0;
        first.SetParam("CheckIntervalMs", 0);
        first.SetParam("TemplateScaleRange", new TemplateScaleRange { Min = 0.5, Max = 1.5, Step = 0 });
        first.SetParam("UseFullScreen", false);
        first.SetParam("Region", new FlowAuto.Engine.Region { X = -1, Y = 0, Width = 0, Height = 10 });
        var second = TimeoutNode("second");

        var flow = new FlowDefinition
        {
            Nodes = [first, second],
            Connections =
            [
                Connect(first, second),
                Connect(second, first)
            ]
        };

        var errors = FlowValidator.Validate(flow);
        Assert(errors.Any(error => error.Contains("timeout", StringComparison.OrdinalIgnoreCase)),
            "Invalid timeout was not reported.");
        Assert(errors.Any(error => error.Contains("scale range", StringComparison.OrdinalIgnoreCase)),
            "Invalid scale range was not reported.");
        Assert(errors.Any(error => error.Contains("region", StringComparison.OrdinalIgnoreCase)),
            "Invalid capture region was not reported.");
        Assert(errors.Any(error => error.Contains("cycle", StringComparison.OrdinalIgnoreCase)),
            "Connection cycle was not reported.");

        var validFlow = new FlowDefinition { Nodes = [TimeoutNode("valid")] };
        Assert(FlowValidator.Validate(validFlow).Count == 0, "A minimal valid flow was rejected.");
    }

    private static void TestBundledFlows()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null &&
               !Directory.Exists(Path.Combine(directory.FullName, "TestScripts", "TestFlows")))
            directory = directory.Parent;

        Assert(directory != null, "Could not locate TestScripts/TestFlows from the test output directory.");
        var flowDirectory = Path.Combine(directory!.FullName, "TestScripts", "TestFlows");
        var files = Directory.GetFiles(flowDirectory, "*.flow.json");
        Assert(files.Length > 0, "No bundled flow files were found.");

        foreach (var file in files)
        {
            var flow = JsonSerializer.Deserialize<FlowDefinition>(File.ReadAllText(file));
            var errors = FlowValidator.Validate(flow);
            Assert(errors.Count == 0,
                $"{Path.GetFileName(file)} failed validation: {string.Join(" | ", errors)}");
        }
    }

    private static async Task TestGateAndTraversalAsync()
    {
        var first = TimeoutNode("first");
        var second = TimeoutNode("second");
        var gate = GateNode("gate", "AND");
        var downstream = TimeoutNode("downstream");
        var flow = new FlowDefinition
        {
            Nodes = [first, second, gate, downstream],
            Connections =
            [
                Connect(first, gate, toPort: "Input0"),
                Connect(second, gate, toPort: "Input1"),
                Connect(gate, downstream)
            ]
        };

        var context = CreateContext();
        await new FlowExecutor(context).ExecuteAsync(flow);

        Assert(context.Get<bool>($"{gate.NodeId}_result"), "AND gate did not receive both input signals.");
        Assert(context.CurrentNodeIndex == 4,
            $"Expected all four nodes to execute once; actual count was {context.CurrentNodeIndex}.");
    }

    private static async Task TestGateFalseStopsTraversalAsync()
    {
        var source = TimeoutNode("source");
        var gate = GateNode("not", "NOT");
        var downstream = TimeoutNode("must-not-run");
        var flow = new FlowDefinition
        {
            Nodes = [source, gate, downstream],
            Connections =
            [
                Connect(source, gate, toPort: "Input0"),
                Connect(gate, downstream)
            ]
        };

        var context = CreateContext();
        await new FlowExecutor(context).ExecuteAsync(flow);

        Assert(!context.Get<bool>($"{gate.NodeId}_result"), "NOT gate should invert the true execution signal.");
        Assert(context.CurrentNodeIndex == 2,
            $"Downstream node ran after a false Gate result; execution count was {context.CurrentNodeIndex}.");
    }

    private static async Task TestIndexedGraphTraversalAsync()
    {
        const int nodeCount = 512;
        var nodes = new List<FlowNode>(nodeCount);
        for (var index = 0; index < nodeCount; index++)
        {
            var node = TimeoutNode($"node-{index}");
            node.Enabled = false;
            nodes.Add(node);
        }

        var connections = new List<FlowConnection>(nodeCount);
        for (var index = 0; index < nodes.Count - 1; index++)
            connections.Add(Connect(nodes[index], nodes[index + 1]));

        // Legacy flows can contain a dangling connection. Traversal has always
        // ignored it; the indexed fast path must retain that compatibility.
        connections.Add(new FlowConnection
        {
            FromId = nodes[^1].NodeId,
            ToId = "missing-target-node",
            FromPort = "Output",
            ToPort = "Input"
        });

        var context = CreateContext();
        await new FlowExecutor(context).ExecuteAsync(new FlowDefinition
        {
            Nodes = nodes,
            Connections = connections
        });

        Assert(context.CurrentNodeIndex == nodeCount,
            $"Indexed chain traversal visited {context.CurrentNodeIndex} of {nodeCount} nodes.");
    }

    private static async Task TestIndexedLoopTraversalAsync()
    {
        var loop = new FlowNode
        {
            NodeId = Guid.NewGuid().ToString("N"),
            NodeName = "loop-start",
            NodeType = NodeType.Loop,
            TimeoutMs = 1000,
            RetryCount = 0
        };
        loop.SetParam("LoopMode", "FixedCount");
        loop.SetParam("LoopCount", 2);

        var body = TimeoutNode("loop-body");
        body.Enabled = false;
        var loopEnd = new FlowNode
        {
            NodeId = Guid.NewGuid().ToString("N"),
            NodeName = "loop-end",
            NodeType = NodeType.LoopEnd,
            PairedLoopStartId = loop.NodeId,
            TimeoutMs = 1000,
            RetryCount = 0
        };
        var afterLoop = TimeoutNode("after-loop");
        afterLoop.Enabled = false;

        var logs = new List<string>();
        var context = new FlowContext(new FlowLogger(onLog: logs.Add));
        await new FlowExecutor(context).ExecuteAsync(new FlowDefinition
        {
            Nodes = [loop, body, loopEnd, afterLoop],
            Connections =
            [
                Connect(loop, body),
                Connect(body, loopEnd),
                Connect(loopEnd, afterLoop)
            ]
        });

        Assert(logs.Count(line => line.Contains("Loop iteration", StringComparison.Ordinal)) == 2,
            "The paired LoopEnd index did not preserve the requested two loop iterations.");
        Assert(logs.Count(line => line.Contains("loop-body", StringComparison.Ordinal) &&
                                  line.Contains("Skipped", StringComparison.Ordinal)) == 2,
            "The loop body did not execute exactly once per iteration.");
        Assert(logs.Count(line => line.Contains("after-loop", StringComparison.Ordinal) &&
                                  line.Contains("Skipped", StringComparison.Ordinal)) == 1,
            "The LoopEnd successor did not execute exactly once after the loop.");
    }

    private static async Task TestNodeTimeoutAsync()
    {
        var node = TimeoutNode("slow-node");
        node.TimeoutMs = 40;
        node.SetParam("WaitMs", 500);
        var context = CreateContext();
        var stopwatch = Stopwatch.StartNew();

        await AssertThrowsAsync<TimeoutException>(() =>
            new FlowExecutor(context).ExecuteAsync(new FlowDefinition { Nodes = [node] }));

        stopwatch.Stop();
        Assert(stopwatch.ElapsedMilliseconds < 300,
            $"Timed-out operation did not stop promptly ({stopwatch.ElapsedMilliseconds} ms).");
        Assert(context.CurrentNodeIndex == 0, "A timed-out node was marked as completed.");
    }

    private static async Task TestFlowCancellationAsync()
    {
        var node = TimeoutNode("cancel-node");
        node.TimeoutMs = 5000;
        node.SetParam("WaitMs", 3000);
        using var cancellation = new CancellationTokenSource();
        var context = CreateContext();
        context.Cts = cancellation;
        var execution = new FlowExecutor(context).ExecuteAsync(new FlowDefinition { Nodes = [node] });

        await Task.Delay(30);
        cancellation.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(() => execution);
    }

    private static void BenchmarkPreparedTemplates()
    {
        using var template = CreatePatternBitmap(20, 16);
        using var source = new Bitmap(80, 60, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(source))
        {
            graphics.Clear(Color.FromArgb(16, 24, 32));
            graphics.DrawImageUnscaled(template, 42, 28);
        }
        using var templateMat = BitmapConverter.ToMat(template);

        const int iterations = 100;
        var repeatedPrepare = Stopwatch.StartNew();
        for (var index = 0; index < iterations; index++)
            Assert(ImageRecognition.FindTemplate(source, templateMat, 0.5, 1.5, 0.05, 0.95) != null,
                "Unprepared match failed.");
        repeatedPrepare.Stop();

        using var prepared = ImageRecognition.PrepareTemplate(templateMat, 0.5, 1.5, 0.05);
        var reusedPrepare = Stopwatch.StartNew();
        for (var index = 0; index < iterations; index++)
            Assert(ImageRecognition.FindTemplate(source, prepared, 0.95) != null, "Prepared match failed.");
        reusedPrepare.Stop();

        var speedup = repeatedPrepare.Elapsed.TotalMilliseconds /
                      Math.Max(0.001, reusedPrepare.Elapsed.TotalMilliseconds);
        Console.WriteLine($"      benchmark: {repeatedPrepare.Elapsed.TotalMilliseconds:F1} ms -> " +
                          $"{reusedPrepare.Elapsed.TotalMilliseconds:F1} ms ({speedup:F2}x)");
    }

    private static Bitmap CreatePatternBitmap(int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(25, 35, 45));
        using var accent = new SolidBrush(Color.FromArgb(15, 220, 135));
        graphics.FillRectangle(accent, 2, 2, width / 2, height - 4);
        using var marker = new Pen(Color.White, 2);
        graphics.DrawLine(marker, width / 2, 1, width - 2, height - 2);
        graphics.DrawRectangle(Pens.OrangeRed, 1, 1, width - 3, height - 3);
        return bitmap;
    }

    private static void SaveSolidBitmap(string path, Color color)
    {
        using var bitmap = new Bitmap(8, 8, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(color);
        bitmap.Save(path, ImageFormat.Png);
    }

    private static FlowNode TimeoutNode(string name)
    {
        var node = new FlowNode
        {
            NodeId = Guid.NewGuid().ToString("N"),
            NodeName = name,
            NodeType = NodeType.WaitCondition,
            TimeoutMs = 1000,
            RetryCount = 0
        };
        node.SetParam("ConditionType", "Timeout");
        node.SetParam("WaitMs", 1);
        return node;
    }

    private static FlowNode GateNode(string name, string logicType)
    {
        var node = new FlowNode
        {
            NodeId = Guid.NewGuid().ToString("N"),
            NodeName = name,
            NodeType = NodeType.Gate,
            TimeoutMs = 1000,
            RetryCount = 0
        };
        node.SetParam("GateLogicType", logicType);
        return node;
    }

    private static FlowConnection Connect(
        FlowNode from, FlowNode to, string fromPort = "Output", string toPort = "Input") => new()
    {
        FromId = from.NodeId,
        ToId = to.NodeId,
        FromPort = fromPort,
        ToPort = toPort
    };

    private static FlowContext CreateContext() => new(new FlowLogger(onLog: _ => { }));

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertThrows<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name} to be thrown.");
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name} to be thrown.");
    }
}
