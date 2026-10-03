using System.Diagnostics;
using System.Text.Json;
using FlowAuto.Core;
using FlowAuto.Models;
using System.Drawing;
using OpenCvSharp;
using Point = System.Drawing.Point;

namespace FlowAuto.Engine;

public class FlowExecutor
{
    private readonly FlowContext _context;
    private readonly Dictionary<string, HashSet<string>> _requiredGateInputs = new(StringComparer.Ordinal);

    /// <summary>
    /// Per-execution indexes for connection-based flows. Keeping these local to
    /// one execution avoids repeatedly scanning every node while traversing a
    /// connection (especially inside a loop body), without retaining flow data
    /// after the run finishes.
    /// </summary>
    private sealed class ExecutionGraphIndex
    {
        public Dictionary<string, FlowNode> NodesById { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, FlowNode> PairedLoopEndsByStartId { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<(string ToId, string FromPort, string ToPort)>> OutgoingMap { get; } = new();

        public ExecutionGraphIndex(IEnumerable<FlowNode> nodes, IEnumerable<FlowConnection> connections)
        {
            foreach (var node in nodes)
            {
                // TryAdd intentionally preserves the executor's historical
                // FirstOrDefault behavior for malformed flows with duplicate IDs.
                if (node.NodeId is not null)
                    NodesById.TryAdd(node.NodeId, node);

                if (node.NodeType == NodeType.LoopEnd &&
                    node.PairedLoopStartId is not null)
                {
                    PairedLoopEndsByStartId.TryAdd(node.PairedLoopStartId, node);
                }
            }

            // Preserve serialized connection order: Gate input delivery and
            // branch traversal deliberately follow that ordering.
            foreach (var connection in connections)
            {
                if (!OutgoingMap.TryGetValue(connection.FromId, out var successors))
                    OutgoingMap[connection.FromId] = successors = new();
                successors.Add((connection.ToId, connection.FromPort, connection.ToPort));
            }
        }
    }

    public FlowExecutor(FlowContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Execute the entire flow definition.
    /// </summary>
    public async Task ExecuteAsync(FlowDefinition flow)
    {
        _context.Logger.Info("SYSTEM", $"Starting flow: {flow.FlowName}");
        _context.CurrentNodeIndex = 0;
        InitializeGateState(flow);

        if (flow.Connections != null && flow.Connections.Count > 0)
        {
            await ExecuteNodesByConnectionsAsync(flow.Nodes, flow.Connections);
        }
        else
        {
            await ExecuteNodeListAsync(flow.Nodes);
        }

        _context.Logger.Success("SYSTEM", $"Flow completed: {flow.FlowName}");
    }

    private void InitializeGateState(FlowDefinition flow)
    {
        _requiredGateInputs.Clear();
        var gateIds = flow.Nodes
            .Where(node => node.NodeType == NodeType.Gate)
            .Select(node => node.NodeId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var gateId in gateIds)
        {
            _requiredGateInputs[gateId] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _context.Set($"{gateId}_input0", false);
            _context.Set($"{gateId}_input1", false);
            _context.Set($"{gateId}_input0_set", false);
            _context.Set($"{gateId}_input1_set", false);
        }

        foreach (var connection in flow.Connections ?? [])
        {
            if (!gateIds.Contains(connection.ToId)) continue;
            if (connection.ToPort.Equals("Input0", StringComparison.OrdinalIgnoreCase) ||
                connection.ToPort.Equals("Input1", StringComparison.OrdinalIgnoreCase))
            {
                _requiredGateInputs[connection.ToId].Add(connection.ToPort);
            }
        }
    }

    /// <summary>
    /// Execute nodes in connection-defined order (graph traversal).
    /// Falls back to list order for any unvisited orphan nodes.
    /// </summary>
    private async Task ExecuteNodesByConnectionsAsync(List<FlowNode> nodes, List<FlowConnection> connections)
    {
        var graph = new ExecutionGraphIndex(nodes, connections);

        // Find nodes that no other node points to (root nodes)
        var targetIds = new HashSet<string>(connections.Select(c => c.ToId));
        var roots = nodes.Where(n => !targetIds.Contains(n.NodeId)).ToList();
        var visited = new HashSet<string>();

        // Execute roots first (may be multiple start points)
        foreach (var root in roots)
        {
            await TraverseNodeChainAsync(root, graph, visited);
        }

        // Every true orphan is already included in roots. Any remaining node is
        // connected but unreachable (for example an inactive condition branch)
        // and must not be executed as a list fallback.
        var remaining = nodes.Where(n => !visited.Contains(n.NodeId)).ToList();
        if (remaining.Count > 0)
        {
            _context.Logger.Info("SYSTEM",
                $"Skipped {remaining.Count} unreachable/inactive node(s): " +
                string.Join(", ", remaining.Select(node => node.NodeName)));
        }
    }

    private async Task TraverseNodeChainAsync(
        FlowNode node, ExecutionGraphIndex graph,
        HashSet<string> visited)
    {
        if (!visited.Add(node.NodeId)) return;

        // ── LoopStart: hand off to the two-block loop executor ──
        if (node.NodeType == NodeType.Loop)
        {
            await ExecuteLoopStartAsync(node, graph, visited);
            return; // LoopStart handles its own body + LoopEnd output
        }

        // ── LoopEnd: skip when reached via normal traversal (it's handled inside the loop) ──
        if (node.NodeType == NodeType.LoopEnd)
        {
            _context.Logger.Info(node.NodeName, "LoopEnd reached (end of loop body)");
            return;
        }

        _context.CheckCancellation();
        await _context.WaitIfPausedAsync();

        if (!node.Enabled)
        {
            _context.Logger.Info(node.NodeName, "Skipped (disabled)");
        }
        else
        {
            await ExecuteNodeAsync(node);
        }
        _context.CurrentNodeIndex++;

        // A Gate's Result port is an execution guard: a false result must not
        // trigger downstream action nodes.
        if (node.NodeType == NodeType.Gate && !_context.Get<bool>($"{node.NodeId}_result"))
        {
            _context.Logger.Info(node.NodeName, "Gate result is false; downstream path skipped");
            return;
        }

        // Follow all outgoing connections, with port filtering for branch-capable nodes
        if (graph.OutgoingMap.TryGetValue(node.NodeId, out var successors))
        {
            // Determine the active port filter
            string? activePort = null;
            if (node.NodeType == NodeType.Condition)
            {
                activePort = _context.Get<string>($"{node.NodeId}_result") ?? "True";
            }
            else if (node.NodeType == NodeType.ColorCal)
            {
                var resultIdx = _context.Get<int>($"{node.NodeId}_result");
                activePort = resultIdx.ToString();
            }
            else if (node.NodeType == NodeType.ColorMotion)
            {
                // DirectionDetect mode uses direction-named ports
                var mode = node.GetParam<string>("MotionMode") ?? "MotionDetect";
                if (mode == "DirectionDetect")
                {
                    activePort = _context.Get<string>($"{node.NodeId}_direction");
                }
                else
                {
                    activePort = _context.Get<string>($"{node.NodeId}_result") ?? "True";
                }
            }

            foreach (var (toId, fromPort, toPort) in successors)
            {
                // Filter by active port for branch-capable nodes
                if (activePort != null && !string.Equals(fromPort, activePort, StringComparison.OrdinalIgnoreCase))
                    continue;

                // If this connection targets a Loop's BreakCond port, set flag instead of traversing
                if (toPort == "BreakCond")
                {
                    if (graph.NodesById.TryGetValue(toId, out var targetLoop) &&
                        targetLoop.NodeType == NodeType.Loop)
                    {
                        _context.Set($"{toId}_breakCond", true);
                        _context.Logger.Info(node.NodeName, $"Break condition signaled to Loop: {targetLoop.NodeName}");
                    }
                    continue;
                }

                if (graph.NodesById.TryGetValue(toId, out var nextNode))
                {
                    if (!RegisterGateInput(node, nextNode, toPort))
                        continue;
                    await TraverseNodeChainAsync(nextNode, graph, visited);
                }
            }
        }
    }

    private bool RegisterGateInput(FlowNode sourceNode, FlowNode targetNode, string toPort)
    {
        if (targetNode.NodeType != NodeType.Gate)
            return true;

        string? inputName = toPort.Equals("Input0", StringComparison.OrdinalIgnoreCase) ? "input0" :
            toPort.Equals("Input1", StringComparison.OrdinalIgnoreCase) ? "input1" : null;
        if (inputName == null)
            return true;

        bool value = ResolveBooleanResult(sourceNode);
        _context.Set($"{targetNode.NodeId}_{inputName}", value);
        _context.Set($"{targetNode.NodeId}_{inputName}_set", true);
        _context.Logger.Info(targetNode.NodeName,
            $"Received {toPort}={value} from {sourceNode.NodeName}");

        var logicType = targetNode.GetParam<string>("GateLogicType") ?? "AND";
        if (logicType.Equals("NOT", StringComparison.OrdinalIgnoreCase))
            return _context.Get<bool>($"{targetNode.NodeId}_input0_set");

        if (!_requiredGateInputs.TryGetValue(targetNode.NodeId, out var required) || required.Count == 0)
            return true;

        return required.All(port => _context.Get<bool>(
            $"{targetNode.NodeId}_{port.ToLowerInvariant()}_set"));
    }

    private bool ResolveBooleanResult(FlowNode sourceNode)
    {
        if (sourceNode.NodeType == NodeType.Gate)
            return _context.Get<bool>($"{sourceNode.NodeId}_result");

        var result = _context.Get<string>($"{sourceNode.NodeId}_result");
        return result == null || !result.Equals("False", StringComparison.OrdinalIgnoreCase);
    }

    private async Task ExecuteNodeListAsync(List<FlowNode> nodes)
    {
        foreach (var node in nodes)
        {
            _context.CheckCancellation();
            await _context.WaitIfPausedAsync();

            if (!node.Enabled)
            {
                _context.Logger.Info(node.NodeName, "Skipped (disabled)");
                continue;
            }

            await ExecuteNodeAsync(node);
            _context.CurrentNodeIndex++;
        }
    }

    public async Task ExecuteNodeAsync(FlowNode node)
    {
        _context.Logger.Info(node.NodeName, $"Executing [{node.NodeType}]");

        int retries = 0;
        while (retries <= node.RetryCount)
        {
            try
            {
                var flowCancellationToken = _context.FlowCancellationToken;
                using var nodeTimeout = CancellationTokenSource.CreateLinkedTokenSource(flowCancellationToken);
                nodeTimeout.CancelAfter(node.TimeoutMs);
                try
                {
                    using (_context.UseCancellationToken(nodeTimeout.Token))
                        await ExecuteNodeByTypeAsync(node);
                }
                catch (OperationCanceledException) when (
                    !flowCancellationToken.IsCancellationRequested && nodeTimeout.IsCancellationRequested)
                {
                    throw new TimeoutException($"Node timed out after {node.TimeoutMs}ms");
                }
                _context.Logger.Success(node.NodeName, "Completed");
                return;
            }
            catch (OperationCanceledException)
            {
                _context.Logger.Warning(node.NodeName, "Execution cancelled");
                throw;
            }
            catch (Exception ex) when (retries < node.RetryCount)
            {
                retries++;
                _context.Logger.Retry(node.NodeName, retries, node.RetryCount, ex.Message);
                await _context.DelayAsync(1000);
            }
            catch (Exception ex)
            {
                _context.Logger.Error(node.NodeName, $"Failed after {retries} retries: {ex.Message}");
                throw;
            }
        }
    }

    private async Task ExecuteNodeByTypeAsync(FlowNode node)
    {
        switch (node.NodeType)
        {
            case NodeType.StartProgram:
                await ExecuteStartProgramAsync(node);
                break;
            case NodeType.ClickElement:
                await ExecuteClickElementAsync(node);
                break;
            case NodeType.WaitCondition:
                await ExecuteWaitConditionAsync(node);
                break;
            case NodeType.KeyPress:
                await ExecuteKeyPressAsync(node);
                break;
            case NodeType.Loop:
                // LoopStart is handled by TraverseNodeChainAsync → ExecuteLoopStartAsync
                // If reached here (fallback / legacy), execute directly
                await ExecuteLoopStartLegacyAsync(node);
                break;
            case NodeType.LoopEnd:
                // LoopEnd is a no-op marker; actual loop logic lives in ExecuteLoopStartAsync
                _context.Logger.Info(node.NodeName, "LoopEnd marker");
                await Task.CompletedTask;
                break;
            case NodeType.Condition:
                await ExecuteConditionAsync(node);
                break;
            case NodeType.Gate:
                await ExecuteGateAsync(node);
                break;
            case NodeType.ColorMotion:
                await ExecuteColorMotionAsync(node);
                break;
            case NodeType.ColorCal:
                await ExecuteColorCalAsync(node);
                break;
            case NodeType.Break:
                await ExecuteBreakAsync(node);
                break;
            default:
                throw new NotSupportedException($"Unknown node type: {node.NodeType}");
        }
    }

    // ============ StartProgram ============

    private async Task ExecuteStartProgramAsync(FlowNode node)
    {
        var filePath = node.GetParam<string>("FilePath") ?? "";
        var workingDir = node.GetParam<string>("WorkingDirectory") ?? "";
        var arguments = node.GetParam<string>("Arguments") ?? "";
        var runAsAdmin = node.GetParam<bool?>("RunAsAdmin") ?? false;
        var waitForWindowMs = node.GetParam<int?>("WaitForWindowMs") ?? 5000;
        var windowKeyword = node.GetParam<string>("WindowTitleKeyword") ?? "";

        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            throw new FileNotFoundException($"Executable not found: {filePath}");

        var psi = new ProcessStartInfo
        {
            FileName = filePath,
            WorkingDirectory = string.IsNullOrEmpty(workingDir) ? Path.GetDirectoryName(filePath) ?? "" : workingDir,
            Arguments = arguments,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal
        };

        if (runAsAdmin)
            psi.Verb = "runas";

        _context.Logger.Info(node.NodeName, $"Starting: {filePath}");
        Process.Start(psi);

        if (!string.IsNullOrEmpty(windowKeyword) && waitForWindowMs > 0)
        {
            _context.Logger.Info(node.NodeName, $"Waiting for window: \"{windowKeyword}\" ({waitForWindowMs}ms)");
            var hWnd = await WindowHelper.WaitForWindowAsync(
                windowKeyword, waitForWindowMs, cancellationToken: _context.CancellationToken);
            if (hWnd != IntPtr.Zero)
            {
                _context.SetHwnd(hWnd);
                _context.Logger.Success(node.NodeName, $"Window found: 0x{hWnd:X}");
            }
            else
            {
                _context.Logger.Warning(node.NodeName, $"Window \"{windowKeyword}\" not found within timeout");
            }
        }

        await Task.CompletedTask;
    }

    // ============ ClickElement ============

    private async Task ExecuteClickElementAsync(FlowNode node)
    {
        var targetWindow = node.GetParam<string>("TargetWindow") ?? "";
        var locateMode = node.GetParam<string>("LocateMode") ?? "Coordinate";

        // Build region from parameters
        var region = node.GetParam<Region>("Region") ?? new Region { X = 0, Y = 0, Width = 100, Height = 100 };
        var templatePath = node.GetParam<string>("TemplateImagePath") ?? "";
        var threshold = node.GetParam<double?>("TemplateMatchThreshold") ?? 0.8;
        // Pre/Post delay: prefer node-specified, else global variables, else defaults
        var nodePre = node.GetParam<int?>("PreDelayMs");
        var nodePost = node.GetParam<int?>("PostDelayMs");
        var globalPre = _context.Get<int?>("GlobalPreDelayMs");
        var globalPost = _context.Get<int?>("GlobalPostDelayMs");
        var preDelayMs = nodePre ?? globalPre ?? 500; // default 500ms
        var postDelayMs = nodePost ?? globalPost ?? 500; // default 500ms

        // Scale range
        double minScale = 0.5, maxScale = 1.5, step = 0.1;
        var scaleRange = node.GetParam<TemplateScaleRange>("TemplateScaleRange");
        if (scaleRange != null)
        {
            minScale = scaleRange.Min;
            maxScale = scaleRange.Max;
            step = scaleRange.Step;
        }

        // Get window handle
        var hWnd = _context.CurrentHwnd;
        if (hWnd == IntPtr.Zero && !string.IsNullOrEmpty(targetWindow))
        {
            hWnd = WindowHelper.FindWindowByTitle(targetWindow);
            if (hWnd != IntPtr.Zero)
                _context.SetHwnd(hWnd);
        }

        if (hWnd == IntPtr.Zero)
            throw new InvalidOperationException("No target window found");

        // Activate window
        WindowHelper.ActivateWindow(hWnd);
        await _context.DelayAsync(200);

        var (clientLeft, clientTop, clientWidth, clientHeight) = WindowHelper.GetClientBounds(hWnd);
        // Full window only applies to TemplateMatch and OCR; Coordinate always uses explicit Region
        var useFullWindow = (locateMode != "Coordinate") && (node.GetParam<bool?>("UseFullScreen") ?? true);

        int absoluteX, absoluteY;

        // If full window, override region to span entire client area
        int captureX = useFullWindow ? 0 : region.X;
        int captureY = useFullWindow ? 0 : region.Y;
        int captureW = useFullWindow ? clientWidth : region.Width;
        int captureH = useFullWindow ? clientHeight : region.Height;

        switch (locateMode)
        {
            case "Coordinate":
                absoluteX = clientLeft + captureX + captureW / 2;
                absoluteY = clientTop + captureY + captureH / 2;
                _context.Logger.Info(node.NodeName, $"Coordinates: ({absoluteX}, {absoluteY})");
                break;

            case "TemplateMatch":
                if (string.IsNullOrEmpty(templatePath) || !File.Exists(templatePath))
                    throw new FileNotFoundException($"Template image not found: {templatePath}");

                // Capture region (or full screen)
                {
                    using var screenBmp = ScreenCapture.CaptureWindowRegion(hWnd, captureX, captureY, captureW, captureH);
                    if (screenBmp == null)
                        throw new InvalidOperationException("Failed to capture screen region");

                    // Share the pre-scaled pyramid across repeated nodes/runs while
                    // retaining it only for the duration of this match operation.
                    using var preparedTemplate = ImageRecognition.AcquirePreparedTemplate(
                        templatePath, minScale, maxScale, step, _context.CancellationToken);
                    var result = ImageRecognition.FindTemplate(
                        screenBmp, preparedTemplate.Template, threshold);

                    if (result == null)
                    {
                        var debugFile = DiagnosticArtifacts.CreatePngPath("debug_match", node.NodeName);
                        var debugQueued = DiagnosticArtifacts.QueuePng(screenBmp, debugFile);
                        var debugDetail = debugQueued
                            ? $"Diagnostic screenshot queued for saving: {debugFile}"
                            : "Diagnostic screenshot skipped because the write queue is busy.";

                        var msg = $"Template not matched (threshold: {threshold}). " +
                                  $"Template: {Path.GetFileName(templatePath)} " +
                                  $"({preparedTemplate.Template.Scales[0].Cols}×{preparedTemplate.Template.Scales[0].Rows}). " +
                                  $"Captured region: ({captureX},{captureY}) {captureW}×{captureH}. " +
                                  debugDetail;
                        throw new InvalidOperationException(msg);
                    }

                    absoluteX = clientLeft + captureX + result.Value.point.X;
                    absoluteY = clientTop + captureY + result.Value.point.Y;
                    _context.Logger.Info(node.NodeName, $"Template matched at ({absoluteX}, {absoluteY}), confidence: {result.Value.confidence:F3}");
                }
                break;

            case "OCR":
                {
                    var ocrText = node.GetParam<string>("OCRText") ?? "";
                    if (string.IsNullOrEmpty(ocrText))
                        throw new ArgumentException("OCRText is required for OCR locate mode");

                    using var screenBmp = ScreenCapture.CaptureWindowRegion(hWnd, captureX, captureY, captureW, captureH);
                    if (screenBmp == null)
                        throw new InvalidOperationException("Failed to capture screen region");

                    var debugFile = DiagnosticArtifacts.CreatePngPath("debug_ocr", node.NodeName);

                    var ocrSearch = await OcrHelper.FindTextAsync(
                        screenBmp, ocrText, debugFile, _context.CancellationToken);
                    var ocrResult = ocrSearch.Point;
                    var allOcrText = ocrSearch.AllRecognizedText;

                    _context.Logger.Info(node.NodeName,
                        $"OCR lang: {OcrHelper.ActiveLanguage}, recognized text: [{allOcrText}]");

                    if (ocrResult == null)
                    {
                        var debugDetail = ocrSearch.DebugScreenshotQueued == true
                            ? $"Diagnostic screenshot queued for saving: {debugFile}. "
                            : "Diagnostic screenshot skipped because the write queue is busy. ";
                        var msg = $"Text \"{ocrText}\" not found via OCR. " +
                                  $"Language: {OcrHelper.ActiveLanguage}. " +
                                  $"Recognized: [{allOcrText}]. " +
                                  debugDetail +
                                  $"Captured region: ({captureX},{captureY}) {captureW}×{captureH} " +
                                  $"from window client ({clientWidth}×{clientHeight}). " +
                                  $"Tip: For game UI, prefer TemplateMatch over OCR.";
                        throw new InvalidOperationException(msg);
                    }

                    absoluteX = clientLeft + captureX + ocrResult.Value.X;
                    absoluteY = clientTop + captureY + ocrResult.Value.Y;
                    _context.Logger.Info(node.NodeName, $"OCR found \"{ocrText}\" at ({absoluteX}, {absoluteY})");
                }
                break;

            case "HSVClick":
                {
                    using var screenBmp = ScreenCapture.CaptureWindowRegion(hWnd, captureX, captureY, captureW, captureH);
                    if (screenBmp == null)
                        throw new InvalidOperationException("Failed to capture screen region");

                    var targetColor = node.ResolveTargetRgb();
                    var hueTol = node.GetParam<int?>("HueTolerance") ?? 8;
                    var svTol = node.GetParam<int?>("SVTolerance") ?? 30;
                    var center = ImageRecognition.DetectColorCenter(screenBmp, targetColor, hueTol, svTol);

                    if (center == null)
                    {
                        var debugFile = DiagnosticArtifacts.CreatePngPath("debug_hsv", node.NodeName);
                        var filteredFile = Path.Combine(
                            Path.GetDirectoryName(debugFile)!,
                            $"{Path.GetFileNameWithoutExtension(debugFile)}_hsvfiltered.png");
                        var debugQueued = DiagnosticArtifacts.QueuePng(screenBmp, debugFile);
                        var filteredQueued = false;
                        try
                        {
                            using var filtered = ImageRecognition.ApplyHsvFilter(screenBmp, targetColor, hueTol, svTol);
                            filteredQueued = DiagnosticArtifacts.QueuePng(filtered, filteredFile);
                        }
                        catch (Exception exception)
                        {
                            DiagnosticArtifacts.ReportPreparationFailure(filteredFile, exception);
                        }
                        var queuedFiles = new List<string>(2);
                        if (debugQueued) queuedFiles.Add(debugFile);
                        if (filteredQueued) queuedFiles.Add(filteredFile);
                        var debugDetail = queuedFiles.Count > 0
                            ? $"Diagnostic files queued: {string.Join(", ", queuedFiles)}"
                            : "Diagnostic files were skipped because the write queue is busy.";
                        throw new InvalidOperationException(
                            $"Target color not found. RGB={targetColor.R},{targetColor.G},{targetColor.B}, " +
                            $"HueTol={hueTol}, SVTol={svTol}. {debugDetail}");
                    }

                    absoluteX = clientLeft + captureX + center.Value.X;
                    absoluteY = clientTop + captureY + center.Value.Y;
                    _context.Logger.Info(node.NodeName, $"HSV color center at ({absoluteX}, {absoluteY})");
                }
                break;

            case "HSVTemplateMatch":
                {
                    var refPath = node.GetParam<string>("ReferenceImagePath") ?? "";
                    if (string.IsNullOrEmpty(refPath) || !File.Exists(refPath))
                        throw new FileNotFoundException($"Reference image not found: {refPath}");

                    using var screenBmp = ScreenCapture.CaptureWindowRegion(hWnd, captureX, captureY, captureW, captureH);
                    if (screenBmp == null)
                        throw new InvalidOperationException("Failed to capture screen region");

                    using var preparedTemplate = ImageRecognition.AcquirePreparedTemplate(
                        refPath, 0.5, 1.5, 0.05, _context.CancellationToken);
                    var targetColor = node.ResolveTargetRgb();
                    var hueTol = node.GetParam<int?>("HueTolerance") ?? 8;
                    var svTol = node.GetParam<int?>("SVTolerance") ?? 30;
                    var tplThreshold = node.GetParam<double?>("TemplateMatchThreshold") ?? 0.8;

                    var result = ImageRecognition.FindTemplateWithColorFilter(
                        screenBmp, preparedTemplate.Template, targetColor, hueTol, svTol, tplThreshold);

                    if (result == null)
                    {
                        var debugFile = DiagnosticArtifacts.CreatePngPath("debug_hsv_tpl", node.NodeName);
                        var filteredFile = Path.Combine(
                            Path.GetDirectoryName(debugFile)!,
                            $"{Path.GetFileNameWithoutExtension(debugFile)}_hsvfiltered.png");
                        var debugQueued = DiagnosticArtifacts.QueuePng(screenBmp, debugFile);
                        var filteredQueued = false;
                        try
                        {
                            using var filtered = ImageRecognition.ApplyHsvFilter(screenBmp, targetColor, hueTol, svTol);
                            filteredQueued = DiagnosticArtifacts.QueuePng(filtered, filteredFile);
                        }
                        catch (Exception exception)
                        {
                            DiagnosticArtifacts.ReportPreparationFailure(filteredFile, exception);
                        }
                        var queuedFiles = new List<string>(2);
                        if (debugQueued) queuedFiles.Add(debugFile);
                        if (filteredQueued) queuedFiles.Add(filteredFile);
                        var debugDetail = queuedFiles.Count > 0
                            ? $"Diagnostic files queued: {string.Join(", ", queuedFiles)}"
                            : "Diagnostic files were skipped because the write queue is busy.";
                        throw new InvalidOperationException(
                            $"HSV+TemplateMatch failed. Ref: {Path.GetFileName(refPath)}. " +
                            $"Color RGB={targetColor.R},{targetColor.G},{targetColor.B}. {debugDetail}");
                    }

                    absoluteX = clientLeft + captureX + result.Value.point.X;
                    absoluteY = clientTop + captureY + result.Value.point.Y;
                    _context.Logger.Info(node.NodeName,
                        $"HSV+Template matched at ({absoluteX}, {absoluteY}), confidence: {result.Value.confidence:F3}");
                }
                break;

            default:
                throw new NotSupportedException($"Unknown locate mode: {locateMode}");
        }

        // Perform click
        await InputSimulator.MoveAndClickAsync(
            absoluteX, absoluteY, preDelayMs, postDelayMs, _context.CancellationToken);
    }

    private async Task ExecuteWaitConditionAsync(FlowNode node)
    {
        var targetWindow = node.GetParam<string>("TargetWindow") ?? "";
        var conditionType = node.GetParam<string>("ConditionType") ?? "ImageAppear";
        var checkIntervalMs = node.GetParam<int?>("CheckIntervalMs") ?? 500;
        var timeoutMs = node.TimeoutMs;

        var region = node.GetParam<Region>("Region") ?? new Region { X = 0, Y = 0, Width = 100, Height = 100 };
        var templatePath = node.GetParam<string>("TemplateImagePath") ?? "";
        var threshold = node.GetParam<double?>("TemplateMatchThreshold") ?? 0.8;

        // Get window handle
        var hWnd = _context.CurrentHwnd;
        if (hWnd == IntPtr.Zero && !string.IsNullOrEmpty(targetWindow))
        {
            hWnd = WindowHelper.FindWindowByTitle(targetWindow);
            if (hWnd != IntPtr.Zero)
                _context.SetHwnd(hWnd);
        }

        if (conditionType == "WindowExist" && !string.IsNullOrEmpty(targetWindow))
        {
            _context.Logger.Info(node.NodeName, $"Waiting for window: \"{targetWindow}\" ({timeoutMs}ms)");
            var found = await WindowHelper.WaitForWindowAsync(
                targetWindow, timeoutMs, checkIntervalMs, _context.CancellationToken);
            if (found != IntPtr.Zero)
            {
                _context.SetHwnd(found);
                _context.Logger.Success(node.NodeName, "Window found");
            }
            else
                throw new TimeoutException($"Window \"{targetWindow}\" not found within {timeoutMs}ms");
            return;
        }

        if (conditionType == "Timeout")
        {
            var waitMs = node.GetParam<int?>("WaitMs") ?? node.TimeoutMs;
            _context.Logger.Info(node.NodeName, $"Waiting {waitMs}ms...");
            await _context.DelayAsync(waitMs);
            return;
        }

        if (conditionType == "OCRContain")
        {
            var ocrText = node.GetParam<string>("OCRText") ?? "";
            if (string.IsNullOrEmpty(ocrText))
                throw new ArgumentException("OCRText is required for OCRContain condition");

            if (hWnd == IntPtr.Zero)
                throw new InvalidOperationException("No target window available for OCR");

            var useFullWindow = node.GetParam<bool?>("UseFullScreen") ?? true;
            var (cl, ct, cw, ch) = WindowHelper.GetClientBounds(hWnd);
            int ocrCapX = useFullWindow ? 0 : region.X;
            int ocrCapY = useFullWindow ? 0 : region.Y;
            int ocrCapW = useFullWindow ? cw : region.Width;
            int ocrCapH = useFullWindow ? ch : region.Height;

            _context.Logger.Info(node.NodeName, $"Waiting for OCR text \"{ocrText}\" ({timeoutMs}ms)... " +
                $"(OCR lang: {OcrHelper.ActiveLanguage})");

            var ocrSw = Stopwatch.StartNew();
            string? lastRecognized = null;
            while (ocrSw.ElapsedMilliseconds < timeoutMs)
            {
                _context.CheckCancellation();
                await _context.WaitIfPausedAsync();

                using var ocrBmp = ScreenCapture.CaptureWindowRegion(hWnd, ocrCapX, ocrCapY, ocrCapW, ocrCapH);
                if (ocrBmp != null)
                {
                    var ocrSearch = await OcrHelper.FindTextAsync(
                        ocrBmp, ocrText, cancellationToken: _context.CancellationToken);
                    var ocrResult = ocrSearch.Point;
                    lastRecognized = ocrSearch.AllRecognizedText;
                    if (ocrResult != null)
                    {
                        _context.Logger.Success(node.NodeName, $"OCR found \"{ocrText}\" after {ocrSw.ElapsedMilliseconds}ms");
                        return;
                    }
                }
                await _context.DelayAsync(checkIntervalMs);
            }
            throw new TimeoutException(
                $"OCR text \"{ocrText}\" not found within {timeoutMs}ms. " +
                $"OCR lang: {OcrHelper.ActiveLanguage}. " +
                $"Last recognized: [{lastRecognized}]. " +
                $"Tip: For game UI, prefer ImageAppear with TemplateMatch over OCRContain.");
        }

        if (string.IsNullOrEmpty(templatePath) || !File.Exists(templatePath))
            throw new FileNotFoundException($"Template image not found: {templatePath}");

        // Read scale range for multi-scale matching (same as ClickElement)
        double minScale = 0.5, maxScale = 1.5, scaleStep = 0.1;
        var scaleRange = node.GetParam<TemplateScaleRange>("TemplateScaleRange");
        if (scaleRange != null)
        {
            minScale = scaleRange.Min;
            maxScale = scaleRange.Max;
            scaleStep = scaleRange.Step;
        }
        using var preparedTemplate = ImageRecognition.AcquirePreparedTemplate(
            templatePath, minScale, maxScale, scaleStep, _context.CancellationToken);

        var sw = Stopwatch.StartNew();
        var useFullScreen = node.GetParam<bool?>("UseFullScreen") ?? true;
        var (clientLeft2, clientTop2, clientW2, clientH2) = WindowHelper.GetClientBounds(hWnd);
        int capX = useFullScreen ? 0 : region.X;
        int capY = useFullScreen ? 0 : region.Y;
        int capW = useFullScreen ? clientW2 : region.Width;
        int capH = useFullScreen ? clientH2 : region.Height;

        int pollCount = 0;
        int logInterval = Math.Max(1, 2000 / Math.Max(1, checkIntervalMs)); // log every ~2 seconds

        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            _context.CheckCancellation();
            await _context.WaitIfPausedAsync();

            if (hWnd == IntPtr.Zero) break;

            using var screenBmp = ScreenCapture.CaptureWindowRegion(hWnd, capX, capY, capW, capH);
            if (screenBmp == null)
            {
                await _context.DelayAsync(checkIntervalMs);
                continue;
            }

            var result = ImageRecognition.FindTemplate(screenBmp, preparedTemplate.Template, threshold);

            bool conditionMet = conditionType switch
            {
                "ImageAppear" => result != null,
                "ImageDisappear" => result == null,
                _ => false
            };

            if (conditionMet)
            {
                _context.Logger.Success(node.NodeName, $"Condition met after {sw.ElapsedMilliseconds}ms" +
                    (result != null ? $", confidence: {result.Value.confidence:F3}" : ""));
                return;
            }

            // Periodic feedback so user knows it's still polling
            pollCount++;
            if (pollCount % logInterval == 0)
            {
                _context.Logger.Info(node.NodeName,
                    $"Still waiting... ({sw.ElapsedMilliseconds}/{timeoutMs}ms, " +
                    $"best confidence: {(result?.confidence ?? 0):F3})");
            }

            await _context.DelayAsync(checkIntervalMs);
        }

        throw new TimeoutException($"Condition not met within {timeoutMs}ms");
    }

    // ============ KeyPress ============

    private async Task ExecuteKeyPressAsync(FlowNode node)
    {
        var targetWindow = node.GetParam<string>("TargetWindow") ?? "";
        var keyName = node.GetParam<string>("KeyName") ?? "";
        var pressMode = node.GetParam<string>("PressMode") ?? "Press";
        var holdDurationMs = node.GetParam<int?>("HoldDurationMs") ?? 500;

        byte scanCode;
        if (node.Parameters.TryGetValue("KeyScanCode", out var scObj) && scObj != null)
        {
            try
            {
                // JsonElement doesn't implement IConvertible — extract raw value
                if (scObj is System.Text.Json.JsonElement je)
                    scanCode = je.ValueKind == System.Text.Json.JsonValueKind.Number
                        ? je.Deserialize<byte>() : Convert.ToByte(je.GetRawText());
                else
                    scanCode = Convert.ToByte(scObj);
            }
            catch
            {
                _context.Logger.Warning(node.NodeName,
                    $"KeyScanCode corrupted ({scObj}), falling back to KeyName: {keyName}");
                if (!string.IsNullOrEmpty(keyName))
                    scanCode = InputSimulator.GetScanCode(keyName);
                else
                    throw new ArgumentException($"KeyScanCode corrupted and no KeyName: {scObj}");
            }
        }
        else if (!string.IsNullOrEmpty(keyName))
        {
            scanCode = InputSimulator.GetScanCode(keyName);
        }
        else
        {
            throw new ArgumentException("No scan code or key name provided");
        }

        // Activate target window if specified
        if (!string.IsNullOrEmpty(targetWindow))
        {
            var hWnd = WindowHelper.FindWindowByTitle(targetWindow);
            if (hWnd != IntPtr.Zero)
            {
                WindowHelper.ActivateWindow(hWnd);
                await _context.DelayAsync(200);
            }
        }
        else if (_context.CurrentHwnd != IntPtr.Zero)
        {
            WindowHelper.ActivateWindow(_context.CurrentHwnd);
            await _context.DelayAsync(200);
        }

        _context.Logger.Info(node.NodeName, $"Key: {keyName} (scan: 0x{scanCode:X2}), Mode: {pressMode}");

        switch (pressMode)
        {
            case "Press":
                await InputSimulator.PressKeyAsync(scanCode, _context.CancellationToken);
                break;
            case "Hold":
                await InputSimulator.HoldKeyAsync(scanCode, holdDurationMs, _context.CancellationToken);
                break;
            case "Release":
                InputSimulator.KeyUp(scanCode);
                break;
            default:
                throw new NotSupportedException($"Unknown press mode: {pressMode}");
        }
    }

    // ==================== LoopStart / LoopEnd (Two-Block Loop System) ====================

    /// <summary>
    /// Execute a LoopStart node using the two-block loop system.
    /// LoopStart marks the beginning, LoopEnd marks the end.
    /// Body nodes between them (via connections) are executed repeatedly.
    /// After the loop finishes, execution continues from LoopEnd's output.
    /// </summary>
    private async Task ExecuteLoopStartAsync(
        FlowNode loopStart, ExecutionGraphIndex graph,
        HashSet<string> visited)
    {
        var loopMode = loopStart.GetParam<string>("LoopMode") ?? "FixedCount";
        var loopCount = loopStart.GetParam<int?>("LoopCount") ?? 1;

        // Find the paired LoopEnd
        graph.PairedLoopEndsByStartId.TryGetValue(loopStart.NodeId, out var loopEnd);

        if (loopEnd == null)
        {
            // Fallback: try to locate LoopEnd by traversing connections from LoopStart's output
            loopEnd = FindLoopEndByTraversal(loopStart, graph);
        }

        if (loopEnd == null)
        {
            _context.Logger.Warning(loopStart.NodeName,
                "No paired LoopEnd found. Falling back to legacy single-Loop execution.");
            await ExecuteLoopStartLegacyAsync(loopStart);
            return;
        }

        _context.Logger.Info(loopStart.NodeName,
            $"Loop started ({loopMode}" +
            (loopMode == "FixedCount" ? $", count: {loopCount}" : ", BreakCondition") +
            $") — paired with LoopEnd [{loopEnd.NodeName}]");

        // Set loop state in context
        _context.Set($"{loopStart.NodeId}_loop_active", true);
        _context.Set($"{loopStart.NodeId}_breakCond", false);
        _context.Set($"{loopStart.NodeId}_break", false);

        int iteration = 0;
        bool breakRequested = false;

        while (!breakRequested)
        {
            _context.CheckCancellation();
            await _context.WaitIfPausedAsync();

            // ── FixedCount: check BEFORE executing to avoid off-by-one edge cases ──
            if (loopMode == "FixedCount" && loopCount > 0 && iteration >= loopCount)
            {
                _context.Logger.Success(loopStart.NodeName, $"Loop completed ({iteration}/{loopCount} iterations)");
                break;
            }

            // Reset break condition flag each iteration
            _context.Set($"{loopStart.NodeId}_breakCond", false);

            iteration++;
            _context.Logger.Info(loopStart.NodeName, $"Loop iteration {iteration}");

            try
            {
                // Execute one full pass of the loop body using connection traversal
                var iterVisited = new HashSet<string> { loopStart.NodeId };
                await TraverseLoopBodyAsync(loopStart, loopEnd, graph, iterVisited);
            }
            catch (Exception ex)
            {
                _context.Logger.Warning(loopStart.NodeName, $"Loop iteration {iteration} failed: {ex.Message}");
                breakRequested = true;
                break;
            }

            // Check break sources
            if (_context.Get<bool?>($"{loopStart.NodeId}_break") == true)
            {
                breakRequested = true;
                _context.Logger.Info(loopStart.NodeName, $"Break signal received, exiting loop after {iteration} iteration(s)");
                break;
            }

            if (_context.Get<bool?>($"{loopStart.NodeId}_breakCond") == true)
            {
                breakRequested = true;
                _context.Logger.Info(loopStart.NodeName, $"BreakCondition met, exiting loop after {iteration} iteration(s)");
                break;
            }
        }

        // Clean up loop state
        _context.Set($"{loopStart.NodeId}_loop_active", false);
        _context.Set($"{loopStart.NodeId}_break", false);
        _context.Set($"{loopStart.NodeId}_breakCond", false);

        // ── Mark ALL body nodes visited so they are NEVER re-executed after the loop ──
        CollectBodyNodeIds(loopStart, loopEnd, graph.OutgoingMap, visited);
        visited.Add(loopEnd.NodeId);

        // After loop, follow LoopEnd's output connections
        if (graph.OutgoingMap.TryGetValue(loopEnd.NodeId, out var endSucc))
        {
            foreach (var (toId, _, toPort) in endSucc)
            {
                if (toPort == "BreakCond") continue;
                if (graph.NodesById.TryGetValue(toId, out var nextNode))
                {
                    if (!RegisterGateInput(loopEnd, nextNode, toPort))
                        continue;
                    await TraverseNodeChainAsync(nextNode, graph, visited);
                }
            }
        }
    }

    /// <summary>
    /// Collect all node IDs in the loop body (between LoopStart and LoopEnd via connections)
    /// and add them to the visited set so they are never executed after the loop finishes.
    /// </summary>
    private static void CollectBodyNodeIds(
        FlowNode loopStart, FlowNode loopEnd,
        Dictionary<string, List<(string ToId, string FromPort, string ToPort)>> outgoingMap,
        HashSet<string> visited)
    {
        if (!outgoingMap.TryGetValue(loopStart.NodeId, out var succs)) return;
        var queue = new Queue<string>();
        foreach (var (toId, _, _) in succs)
            if (toId != loopEnd.NodeId) queue.Enqueue(toId);

        var seen = new HashSet<string> { loopStart.NodeId, loopEnd.NodeId };
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!seen.Add(id)) continue;
            visited.Add(id);

            if (outgoingMap.TryGetValue(id, out var next))
                foreach (var (nid, _, _) in next)
                    if (!seen.Contains(nid) && nid != loopEnd.NodeId)
                        queue.Enqueue(nid);
        }
    }

    /// <summary>
    /// Execute one iteration of the loop body. Traverses from LoopStart's output
    /// connections through the body until reaching LoopEnd (or all paths exhausted).
    /// Uses a per-iteration visited set so nodes can be re-executed each iteration.
    /// </summary>
    private async Task TraverseLoopBodyAsync(
        FlowNode loopStart, FlowNode loopEnd,
        ExecutionGraphIndex graph,
        HashSet<string> iterVisited)
    {
        if (!graph.OutgoingMap.TryGetValue(loopStart.NodeId, out var startSucc))
            return;

        foreach (var (toId, _, toPort) in startSucc)
        {
            if (toId == loopEnd.NodeId) continue;          // Skip direct LoopStart→LoopEnd
            if (toPort == "BreakCond") continue;           // BreakCond is handled in TraverseNodeChainAsync

            if (graph.NodesById.TryGetValue(toId, out var nextNode))
            {
                if (!RegisterGateInput(loopStart, nextNode, toPort))
                    continue;
                await TraverseLoopBodyNodeAsync(loopStart, nextNode, loopEnd, graph, iterVisited);
            }
        }
    }

    /// <summary>
    /// Traverse a single node within the loop body. Stops at LoopEnd.
    /// BreakCond connections signal the loop to break.
    /// Uses iterVisited to avoid infinite recursion within one iteration
    /// (but nodes are re-visited across iterations since iterVisited is fresh each time).
    /// </summary>
    private async Task TraverseLoopBodyNodeAsync(
        FlowNode loopStart, FlowNode node, FlowNode loopEnd,
        ExecutionGraphIndex graph,
        HashSet<string> iterVisited)
    {
        if (node.NodeId == loopEnd.NodeId) return;
        if (!iterVisited.Add(node.NodeId)) return; // Already visited in this iteration

        // Nested LoopStart? Delegate to the two-block executor
        if (node.NodeType == NodeType.Loop)
        {
            // Use a temporary outer visited-like set so the nested loop can track its own state
            var nestedVisited = new HashSet<string>();
            await ExecuteLoopStartAsync(node, graph, nestedVisited);
            return;
        }

        _context.CheckCancellation();
        await _context.WaitIfPausedAsync();

        if (node.Enabled)
            await ExecuteNodeAsync(node);
        else
            _context.Logger.Info(node.NodeName, "Skipped (disabled)");

        if (node.NodeType == NodeType.Gate && !_context.Get<bool>($"{node.NodeId}_result"))
        {
            _context.Logger.Info(node.NodeName, "Gate result is false; downstream path skipped");
            return;
        }

        // ── Port filtering for branch-capable nodes inside loop body ──
        // ColorCal / Condition / ColorMotion route to specific output ports based on result;
        // skip edges from non-matching ports to avoid executing all branch nodes.
        string? bodyActivePort = null;
        if (node.NodeType == NodeType.Condition)
        {
            bodyActivePort = _context.Get<string>($"{node.NodeId}_result") ?? "True";
        }
        else if (node.NodeType == NodeType.ColorCal)
        {
            var resultIdx = _context.Get<int>($"{node.NodeId}_result");
            bodyActivePort = resultIdx.ToString();
        }
        else if (node.NodeType == NodeType.ColorMotion)
        {
            var mm = node.GetParam<string>("MotionMode") ?? "MotionDetect";
            if (mm == "DirectionDetect")
                bodyActivePort = _context.Get<string>($"{node.NodeId}_direction");
            else
                bodyActivePort = _context.Get<string>($"{node.NodeId}_result") ?? "True";
        }

        if (!graph.OutgoingMap.TryGetValue(node.NodeId, out var succs)) return;

        foreach (var (toId, fromPort, toPort) in succs)
        {
            // Filter by active port for branch-capable nodes
            if (bodyActivePort != null && !string.Equals(fromPort, bodyActivePort, StringComparison.OrdinalIgnoreCase))
                continue;
            if (toId == loopEnd.NodeId) continue;     // Reached the end of the loop body

            // BreakCond — signal the enclosing LoopStart
            if (toPort == "BreakCond")
            {
                _context.Set($"{loopStart.NodeId}_breakCond", true);
                _context.Logger.Info(node.NodeName, $"Break condition signaled to Loop: {loopStart.NodeName}");
                continue;
            }

            if (graph.NodesById.TryGetValue(toId, out var nextNode))
            {
                if (!RegisterGateInput(node, nextNode, toPort))
                    continue;
                await TraverseLoopBodyNodeAsync(loopStart, nextNode, loopEnd, graph, iterVisited);
            }
        }
    }

    /// <summary>
    /// Find the LoopEnd node paired with a LoopStart by traversing connections.
    /// The first LoopEnd encountered in the output chain is the paired one.
    /// </summary>
    private FlowNode? FindLoopEndByTraversal(
        FlowNode loopStart, ExecutionGraphIndex graph)
    {
        if (!graph.OutgoingMap.TryGetValue(loopStart.NodeId, out var startSucc))
            return null;

        var searchVisited = new HashSet<string> { loopStart.NodeId };
        var queue = new Queue<string>();

        foreach (var (toId, _, _) in startSucc)
            queue.Enqueue(toId);

        while (queue.Count > 0)
        {
            var nodeId = queue.Dequeue();
            if (!searchVisited.Add(nodeId)) continue;

            if (!graph.NodesById.TryGetValue(nodeId, out var node)) continue;

            if (node.NodeType == NodeType.LoopEnd)
                return node;

            if (graph.OutgoingMap.TryGetValue(nodeId, out var succs))
                foreach (var (nextId, _, _) in succs)
                    queue.Enqueue(nextId);
        }

        return null;
    }

    /// <summary>
    /// Legacy fallback: execute Loop node when no paired LoopEnd is found
    /// (e.g. old-format flow files, or standalone Loop blocks).
    /// </summary>
    private async Task ExecuteLoopStartLegacyAsync(FlowNode node)
    {
        var loopMode = node.GetParam<string>("LoopMode") ?? "FixedCount";
        var loopCount = node.GetParam<int?>("LoopCount") ?? 3;

        _context.Logger.Info(node.NodeName, $"Loop (legacy mode) — {loopMode}, count={loopCount}");

        _context.Set($"{node.NodeId}_loop_active", true);

        if (loopMode == "BreakCondition")
        {
            int iter = 0;
            while (_context.Get<bool?>($"{node.NodeId}_breakCond") != true &&
                   _context.Get<bool?>($"{node.NodeId}_break") != true)
            {
                _context.CheckCancellation();
                await _context.WaitIfPausedAsync();
                iter++;
                _context.Set($"{node.NodeId}_breakCond", false);
                if (node.Children != null && node.Children.Count > 0)
                    await ExecuteNodeListAsync(node.Children);
            }
            _context.Logger.Success(node.NodeName, $"Loop exited via break ({iter} iterations)");
        }
        else
        {
            int i;
            for (i = 0; (loopCount == 0 || i < loopCount); i++)
            {
                _context.CheckCancellation();
                await _context.WaitIfPausedAsync();
                if (_context.Get<bool?>($"{node.NodeId}_break") == true) break;
                if (node.Children != null && node.Children.Count > 0)
                    await ExecuteNodeListAsync(node.Children);
            }
            _context.Logger.Success(node.NodeName, $"Loop completed ({i} iterations)");
        }

        _context.Set($"{node.NodeId}_loop_active", false);
        _context.Set($"{node.NodeId}_break", false);
        _context.Set($"{node.NodeId}_breakCond", false);
    }

    // ============ Condition (ImageAppear + OCRContain only) ============

    private async Task ExecuteConditionAsync(FlowNode node)
    {
        var conditionType = node.GetParam<string>("ConditionType") ?? "ImageAppear";
        var targetWindow = node.GetParam<string>("TargetWindow") ?? "";
        var region = node.GetParam<Region>("Region") ?? new Region { X = 0, Y = 0, Width = 100, Height = 100 };

        var hWnd = _context.CurrentHwnd;
        if (hWnd == IntPtr.Zero && !string.IsNullOrEmpty(targetWindow))
            hWnd = WindowHelper.FindWindowByTitle(targetWindow);

        var useFullScreen = node.GetParam<bool?>("UseFullScreen") ?? true;
        var (clientWb, clientHb) = (0, 0);
        int condX = region.X, condY = region.Y, condW = region.Width, condH = region.Height;
        if (hWnd != IntPtr.Zero)
        {
            var bounds = WindowHelper.GetClientBounds(hWnd);
            clientWb = bounds.Width;
            clientHb = bounds.Height;
            if (useFullScreen)
            {
                condX = 0;
                condY = 0;
                condW = clientWb;
                condH = clientHb;
            }
        }

        bool conditionResult = false;

        if (conditionType == "ImageAppear" && hWnd != IntPtr.Zero)
        {
            var templatePath = node.GetParam<string>("TemplateImagePath") ?? "";
            var threshold = node.GetParam<double?>("TemplateMatchThreshold") ?? 0.8;

            if (string.IsNullOrEmpty(templatePath) || !File.Exists(templatePath))
                throw new FileNotFoundException($"Template image not found: {templatePath}");

            _context.Logger.Info(node.NodeName, $"Checking ImageAppear: {Path.GetFileName(templatePath)}");

            using var screenBmp = ScreenCapture.CaptureWindowRegion(hWnd, condX, condY, condW, condH);
            if (screenBmp != null)
            {
                // Multi-scale template matching
                double minScale = 0.5, maxScale = 1.5, scaleStep = 0.1;
                var scaleRange = node.GetParam<TemplateScaleRange>("TemplateScaleRange");
                if (scaleRange != null)
                {
                    minScale = scaleRange.Min;
                    maxScale = scaleRange.Max;
                    scaleStep = scaleRange.Step;
                }

                using var preparedTemplate = ImageRecognition.AcquirePreparedTemplate(
                    templatePath, minScale, maxScale, scaleStep, _context.CancellationToken);
                var result = ImageRecognition.FindTemplate(screenBmp, preparedTemplate.Template, threshold);
                conditionResult = result != null;

                if (result != null)
                    _context.Logger.Success(node.NodeName, $"Image found, confidence: {result.Value.confidence:F3}");
                else
                    _context.Logger.Info(node.NodeName, $"Image not found (threshold: {threshold})");
            }
        }
        else if (conditionType == "OCRContain" && hWnd != IntPtr.Zero)
        {
            var ocrText = node.GetParam<string>("OCRText") ?? "";
            if (string.IsNullOrEmpty(ocrText))
                throw new ArgumentException("OCRText is required for OCRContain condition");

            _context.Logger.Info(node.NodeName, $"Checking OCR for: \"{ocrText}\"");
            using var ocrBmp = ScreenCapture.CaptureWindowRegion(hWnd, condX, condY, condW, condH);
            if (ocrBmp != null)
            {
                var ocrSearch = await OcrHelper.FindTextAsync(
                    ocrBmp, ocrText, cancellationToken: _context.CancellationToken);
                conditionResult = ocrSearch.Point != null;
                _context.Logger.Info(node.NodeName,
                    $"OCR result: found={conditionResult}, text=[{ocrSearch.AllRecognizedText}]");
            }
        }

        var branchName = conditionResult ? "True" : "False";
        _context.Logger.Info(node.NodeName, $"Condition: {conditionResult}, taking {branchName} branch");
        _context.Set($"{node.NodeId}_result", branchName);
        // Branch routing now handled by TraverseNodeChainAsync via port filtering
    }

    // ============ Gate (AND/OR/NOT, 2 inputs only) ============

    private async Task ExecuteGateAsync(FlowNode node)
    {
        var logicType = node.GetParam<string>("GateLogicType") ?? "AND";

        // Gate receives two input signals (Input0, Input1)
        // The context stores predecessor results keyed by {nodeId}_result
        // For simplicity, we look up the two predecessor node IDs from connections

        bool input0 = _context.Get<bool>($"{node.NodeId}_input0");
        bool input1 = _context.Get<bool>($"{node.NodeId}_input1");

        bool gateResult = logicType switch
        {
            "AND" => input0 && input1,
            "OR" => input0 || input1,
            "NOT" => !input0,  // NOT only uses Input0
            _ => false
        };

        // Store result in context for downstream nodes
        _context.Set($"{node.NodeId}_result", gateResult);
        _context.Logger.Info(node.NodeName, $"Gate {logicType}: in0={input0}, in1={input1} => {gateResult}");

        await Task.CompletedTask;
    }

    // ============ ColorMotion ============

    private async Task ExecuteColorMotionAsync(FlowNode node)
    {
        var motionMode = node.GetParam<string>("MotionMode") ?? "MotionDetect";
        var targetWindow = node.GetParam<string>("TargetWindow") ?? "";

        var hWnd = _context.CurrentHwnd;
        if (hWnd == IntPtr.Zero && !string.IsNullOrEmpty(targetWindow))
            hWnd = WindowHelper.FindWindowByTitle(targetWindow);
        if (hWnd == IntPtr.Zero)
            throw new InvalidOperationException("No target window found for ColorMotion");

        var region = node.GetParam<Region>("Region") ?? new Region { X = 0, Y = 0, Width = 200, Height = 200 };
        var useFullScreen = node.GetParam<bool?>("UseFullScreen") ?? true;
        var (clientW, clientH) = (0, 0);
        int capX = region.X, capY = region.Y, capW = region.Width, capH = region.Height;
        if (hWnd != IntPtr.Zero)
        {
            var bounds = WindowHelper.GetClientBounds(hWnd);
            clientW = bounds.Width;
            clientH = bounds.Height;
            if (useFullScreen) { capX = 0; capY = 0; capW = clientW; capH = clientH; }
        }

        // HSV params — use ResolveTargetRgb for robust string-based colour reading
        var targetRgb = node.ResolveTargetRgb();
        var hueTol = node.GetParam<int?>("HueTolerance") ?? 8;
        var svTol = node.GetParam<int?>("SVTolerance") ?? 30;

        if (motionMode == "MotionDetect")
        {
            // Monitor color motion: detect if target color is moving
            var checkInterval = node.GetParam<int?>("MoveCheckIntervalMs") ?? 30;
            var durationMs = node.GetParam<int?>("MoveDurationMs") ?? 10000;
            var moveThresholdPx = node.GetParam<int?>("MoveThresholdPx") ?? 5;

            _context.Logger.Info(node.NodeName, $"ColorMotion MotionDetect: {durationMs}ms, threshold={moveThresholdPx}px");

            var stopAt = DateTime.UtcNow.AddMilliseconds(durationMs);
            Point? lastCenter = null;
            bool motionDetected = false;
            using var capture = new WindowRegionCaptureSession(hWnd, capX, capY, capW, capH);

            while (DateTime.UtcNow < stopAt && !motionDetected)
            {
                _context.CheckCancellation();
                await _context.WaitIfPausedAsync();

                // The session owns this borrowed frame. It is reused after this
                // iteration, so do not dispose it here or retain it asynchronously.
                var frame = capture.Capture();
                if (frame == null) break;

                var currentCenter = ImageRecognition.DetectColorCenter(frame, targetRgb, hueTol, svTol);

                if (currentCenter != null && lastCenter != null)
                {
                    int dx = currentCenter.Value.X - lastCenter.Value.X;
                    int dy = currentCenter.Value.Y - lastCenter.Value.Y;
                    if (Math.Abs(dx) > moveThresholdPx || Math.Abs(dy) > moveThresholdPx)
                    {
                        motionDetected = true;
                        _context.Logger.Success(node.NodeName, $"Motion detected: dx={dx}, dy={dy}");
                    }
                }

                if (currentCenter != null) lastCenter = currentCenter;
                await _context.DelayAsync(checkInterval);
            }

            _context.Set($"{node.NodeId}_result", motionDetected);
            _context.Set($"{node.NodeId}_motionDetected", motionDetected);
            _context.Set($"{node.NodeId}_result", motionDetected ? "True" : "False");

            _context.Logger.Info(node.NodeName, $"MotionDetect result: {motionDetected}");
            // Branch routing now handled by TraverseNodeChainAsync via port filtering
        }
        else if (motionMode == "StateChange")
        {
            // Monitor color state change at fixed position
            var checkInterval = node.GetParam<int?>("StateCheckIntervalMs") ?? 100;
            var durationMs = node.GetParam<int?>("StateDurationMs") ?? 30000;
            var changeThreshold = node.GetParam<double?>("ColorChangeThreshold") ?? 0.15;

            _context.Logger.Info(node.NodeName, $"ColorMotion StateChange: {durationMs}ms");

            var stopAt = DateTime.UtcNow.AddMilliseconds(durationMs);
            bool stateChanged = false;
            double? baselineRatio = null;
            using var capture = new WindowRegionCaptureSession(hWnd, capX, capY, capW, capH);

            while (DateTime.UtcNow < stopAt && !stateChanged)
            {
                _context.CheckCancellation();
                await _context.WaitIfPausedAsync();

                var frame = capture.Capture();
                if (frame == null) break;

                double currentRatio = ImageRecognition.CalculateColorFillRatio(frame, targetRgb, hueTol, svTol);

                if (baselineRatio == null)
                {
                    baselineRatio = currentRatio;
                    _context.Logger.Info(node.NodeName, $"Baseline color ratio: {baselineRatio:F4}");
                }
                else if (Math.Abs(currentRatio - baselineRatio.Value) > changeThreshold)
                {
                    stateChanged = true;
                    _context.Logger.Success(node.NodeName, $"State changed: {baselineRatio:F4} → {currentRatio:F4}");
                }

                await _context.DelayAsync(checkInterval);
            }

            _context.Set($"{node.NodeId}_result", stateChanged);
            _context.Set($"{node.NodeId}_stateChanged", stateChanged);

            _context.Set($"{node.NodeId}_result", stateChanged ? "True" : "False");
            _context.Logger.Info(node.NodeName, $"StateChange result: {stateChanged}");
            // Branch routing now handled by TraverseNodeChainAsync via port filtering
        }
        else if (motionMode == "DirectionDetect")
        {
            // Two sub-modes:
            //   "TemplateMatch" — HSV filter + template matching on the filtered shape
            //   "ColorTrack"   — pure HSV center-of-mass tracking (no template needed)
            var trackMode = node.GetParam<string>("TrackMode") ?? "TemplateMatch";
            var threshold = node.GetParam<double?>("TemplateMatchThreshold") ?? 0.8;
            var checkInterval = node.GetParam<int?>("MoveCheckIntervalMs") ?? 30;
            var durationMs = node.GetParam<int?>("MoveDurationMs") ?? 10000;

            _context.Logger.Info(node.NodeName,
                $"ColorMotion DirectionDetect ({trackMode}): {durationMs}ms");

            var refImagePath = node.GetParam<string>("ReferenceImagePath") ?? "";
            if (trackMode == "TemplateMatch")
            {
                if (string.IsNullOrEmpty(refImagePath) || !File.Exists(refImagePath))
                    throw new FileNotFoundException($"Reference image not found: {refImagePath}");
            }

            using var preparedTemplate = trackMode == "TemplateMatch"
                ? ImageRecognition.AcquirePreparedTemplate(
                    refImagePath, 0.5, 1.5, 0.05, _context.CancellationToken)
                : null;

            var stopAt = DateTime.UtcNow.AddMilliseconds(durationMs);
            Point? lastCenter = null;
            string detectedDirection = "Stationary";
            bool found = false;
            using var capture = new WindowRegionCaptureSession(hWnd, capX, capY, capW, capH);

            while (DateTime.UtcNow < stopAt && !found)
            {
                _context.CheckCancellation();
                await _context.WaitIfPausedAsync();

                var frame = capture.Capture();
                if (frame == null) break;

                Point? currentCenter;

                if (trackMode == "ColorTrack")
                {
                    // Pure HSV color center detection — no template matching
                    currentCenter = ImageRecognition.DetectColorCenter(frame, targetRgb, hueTol, svTol);
                }
                else
                {
                    // Template matching within HSV-filtered regions
                    var result = ImageRecognition.FindTemplateWithColorFilter(
                        frame, preparedTemplate!.Template, targetRgb, hueTol, svTol, threshold);
                    currentCenter = result?.point;
                }

                if (currentCenter != null)
                {
                    if (lastCenter != null)
                    {
                        int dx = currentCenter.Value.X - lastCenter.Value.X;
                        int dy = currentCenter.Value.Y - lastCenter.Value.Y;
                        detectedDirection = ClassifyDirection(dx, dy);
                        if (detectedDirection != "Stationary")
                        {
                            found = true;
                            _context.Logger.Success(node.NodeName,
                                $"Direction: {detectedDirection} (dx={dx}, dy={dy})");
                        }
                    }
                    lastCenter = currentCenter;
                }

                await _context.DelayAsync(checkInterval);
            }

            _context.Set($"{node.NodeId}_result", detectedDirection);
            _context.Set($"{node.NodeId}_direction", detectedDirection);

            _context.Logger.Info(node.NodeName, $"DirectionDetect result: {detectedDirection}");
            // Direction branch routing is now handled by connection traversal (activePort filtering)
            // — no internal ExecuteNodeListAsync to avoid double-execution with connected nodes.
        }
        else if (motionMode == "ColorDetect")
        {
            // Simple color presence detection (no shape/motion consideration)
            var checkInterval = node.GetParam<int?>("MoveCheckIntervalMs") ?? 100;
            var durationMs = node.GetParam<int?>("MoveDurationMs") ?? 10000;

            _context.Logger.Info(node.NodeName, $"ColorMotion ColorDetect: {durationMs}ms");

            var stopAt = DateTime.UtcNow.AddMilliseconds(durationMs);
            bool colorFound = false;
            using var capture = new WindowRegionCaptureSession(hWnd, capX, capY, capW, capH);

            while (DateTime.UtcNow < stopAt && !colorFound)
            {
                _context.CheckCancellation();
                await _context.WaitIfPausedAsync();

                var frame = capture.Capture();
                if (frame == null) break;

                var center = ImageRecognition.DetectColorCenter(frame, targetRgb, hueTol, svTol);
                if (center != null)
                {
                    colorFound = true;
                    _context.Logger.Success(node.NodeName, $"Color detected at ({center.Value.X}, {center.Value.Y})");
                }

                await _context.DelayAsync(checkInterval);
            }

            _context.Set($"{node.NodeId}_result", colorFound);
            _context.Set($"{node.NodeId}_colorFound", colorFound);
            _context.Set($"{node.NodeId}_result", colorFound ? "True" : "False");

            _context.Logger.Info(node.NodeName, $"ColorDetect result: {colorFound}");
            // Branch routing now handled by TraverseNodeChainAsync via port filtering
        }
    }

    /// <summary>
    /// Classify movement direction into 5 cardinal outputs using angle-based sectors (±35°).
    /// Horizontal-dominant movements within ±35° of the X axis map to Left/Right.
    /// Vertical-dominant movements within ±35° of the Y axis map to Up/Down.
    /// </summary>
    private static string ClassifyDirection(int dx, int dy)
    {
        int threshold = 3;
        int absDx = Math.Abs(dx);
        int absDy = Math.Abs(dy);

        if (absDx < threshold && absDy < threshold)
            return "Stationary";

        // angle in degrees: 0=Right, 90=Down, ±180=Left, -90=Up
        double angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;

        if (angle > -35 && angle <= 35)
            return "Right";
        if (angle > 35 && angle <= 125)
            return "Down";
        if (angle > 125 || angle <= -125)
            return "Left";
        // angle ∈ (-125, -35]
        return "Up";
    }

    // ============ ColorCal (v2.2) ============

    private Task ExecuteColorCalAsync(FlowNode node)
    {
        // 1. Load detection targets
        var targetsConfig = node.GetParam<List<ColorCalTarget>>("DetectionTargets") ?? new List<ColorCalTarget>();
        if (targetsConfig.Count == 0)
        {
            _context.Logger.Warning(node.NodeName, "No detection targets configured");
            _context.Set($"{node.NodeId}_result", 0);
            return Task.CompletedTask;
        }

        // 2. Resolve capture requests before detecting. Targets that point to the
        // same client-area rectangle share one bitmap, so their measurements are
        // based on the same screen frame instead of sequential screenshots.
        var capturePlans = new List<ColorCalTargetCapturePlan>(targetsConfig.Count);
        foreach (var target in targetsConfig)
        {
            var hWnd = _context.CurrentHwnd;
            if (hWnd == IntPtr.Zero && !string.IsNullOrEmpty(target.TargetWindow))
                hWnd = WindowHelper.FindWindowByTitle(target.TargetWindow);

            if (hWnd == IntPtr.Zero)
            {
                capturePlans.Add(new ColorCalTargetCapturePlan(target, null));
                continue;
            }

            var bounds = WindowHelper.GetClientBounds(hWnd);
            int capX = target.Region.X, capY = target.Region.Y;
            int capW = target.Region.Width, capH = target.Region.Height;
            if (target.UseFullScreen)
            {
                capX = 0;
                capY = 0;
                capW = bounds.Width;
                capH = bounds.Height;
            }

            capturePlans.Add(new ColorCalTargetCapturePlan(
                target, new ColorCalCaptureRequest(hWnd, capX, capY, capW, capH)));
        }

        var remainingCaptureUses = CountColorCalCaptureUses(
            capturePlans
                .Where(plan => plan.CaptureRequest.HasValue)
                .Select(plan => plan.CaptureRequest!.Value));
        var capturedFrames = new Dictionary<ColorCalCaptureRequest, Bitmap?>();
        var detectionResults = new List<ColorCalTargetResult>(targetsConfig.Count);

        try
        {
            // Preserve target/result ordering even when capture requests are shared.
            foreach (var plan in capturePlans)
            {
                var target = plan.Target;
                var result = new ColorCalTargetResult { Name = target.Name };

                if (plan.CaptureRequest is not { } captureRequest)
                {
                    // Keep the existing per-target diagnostic when no window resolves.
                    _context.Logger.Warning(node.NodeName, $"Target '{target.Name}': No target window found");
                    detectionResults.Add(result);
                    continue;
                }

                if (!capturedFrames.TryGetValue(captureRequest, out var frame))
                {
                    frame = ScreenCapture.CaptureWindowRegion(
                        captureRequest.WindowHandle,
                        captureRequest.X,
                        captureRequest.Y,
                        captureRequest.Width,
                        captureRequest.Height);
                    capturedFrames.Add(captureRequest, frame);
                }

                try
                {
                    if (frame == null)
                    {
                        // A shared capture failure still reports once for each target,
                        // matching the previous diagnostic behavior.
                        _context.Logger.Warning(node.NodeName, $"Target '{target.Name}': Failed to capture screen");
                    }
                    else
                    {
                        result = DetectSingleTargetInFrame(node, target, frame);
                    }
                }
                finally
                {
                    // Drop a shared frame immediately after its last consumer. The
                    // outer finally covers exceptional exits before that point.
                    remainingCaptureUses[captureRequest]--;
                    if (remainingCaptureUses[captureRequest] == 0)
                    {
                        capturedFrames.Remove(captureRequest);
                        frame?.Dispose();
                    }
                }

                detectionResults.Add(result);
            }
        }
        finally
        {
            foreach (var frame in capturedFrames.Values)
                frame?.Dispose();
        }

        // 3. Store results in context for expression evaluation
        foreach (var r in detectionResults)
        {
            _context.Set($"{node.NodeId}_{r.Name}_X", r.X);
            _context.Set($"{node.NodeId}_{r.Name}_Y", r.Y);
            _context.Set($"{node.NodeId}_{r.Name}_Found", r.Found);
            _context.Logger.Info(node.NodeName, $"Target '{r.Name}': Found={r.Found}, X={r.X}, Y={r.Y}");
        }

        // 4. Evaluate expression
        var expression = node.GetParam<string>("Expression") ?? "0";
        int resultIndex;
        try
        {
            resultIndex = EvaluateColorCalExpressionV2(expression, detectionResults, targetsConfig, _context, node.NodeId);
        }
        catch (Exception ex)
        {
            _context.Logger.Warning(node.NodeName, $"Expression eval failed: {ex.Message}, using default (0)");
            resultIndex = 0;
        }

        _context.Set($"{node.NodeId}_result", resultIndex);
        _context.Logger.Info(node.NodeName, $"ColorCal expression result: {resultIndex}");
        return Task.CompletedTask;
    }

    private readonly record struct ColorCalCaptureRequest(
        IntPtr WindowHandle, int X, int Y, int Width, int Height);

    private readonly record struct ColorCalTargetCapturePlan(
        ColorCalTarget Target, ColorCalCaptureRequest? CaptureRequest);

    private static Dictionary<ColorCalCaptureRequest, int> CountColorCalCaptureUses(
        IEnumerable<ColorCalCaptureRequest> captureRequests)
    {
        var uses = new Dictionary<ColorCalCaptureRequest, int>();
        foreach (var captureRequest in captureRequests)
        {
            uses.TryGetValue(captureRequest, out var count);
            uses[captureRequest] = count + 1;
        }

        return uses;
    }

    private ColorCalTargetResult DetectSingleTargetInFrame(
        FlowNode node, ColorCalTarget target, Bitmap frame)
    {
        var result = new ColorCalTargetResult { Name = target.Name };

        var targetRgb = target.GetRgbColor();

        if (target.TrackMode == "TemplateMatch")
        {
            // HSV filter + template matching (shape + color)
            if (!string.IsNullOrEmpty(target.TemplateImagePath) && File.Exists(target.TemplateImagePath))
            {
                try
                {
                    using var preparedTemplate = ImageRecognition.AcquirePreparedTemplate(
                        target.TemplateImagePath, 0.5, 1.5, 0.05, _context.CancellationToken);
                    var matches = ImageRecognition.DetectMultipleTargets(
                        frame, preparedTemplate.Template, targetRgb, target.HueTolerance,
                        target.SVTolerance, target.TemplateMatchThreshold, 1);
                    if (matches.Count > 0)
                    {
                        result.Found = true;
                        result.X = matches[0].X;
                        result.Y = matches[0].Y;
                        result.Confidence = target.TemplateMatchThreshold;
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    _context.Logger.Warning(node.NodeName, $"Target '{target.Name}': Template match failed: {ex.Message}");
                }
            }

            // Fallback to pure HSV detection if template match fails or no template
            var centers = ImageRecognition.DetectMultipleColorCenters(frame, targetRgb, target.HueTolerance, target.SVTolerance, 1);
            if (centers.Count > 0)
            {
                result.Found = true;
                result.X = centers[0].X;
                result.Y = centers[0].Y;
                result.Confidence = 1.0;
            }
        }
        else // ColorTrack
        {
            // Pure HSV color center detection — no template needed
            var centers = ImageRecognition.DetectMultipleColorCenters(frame, targetRgb, target.HueTolerance, target.SVTolerance, 1);
            if (centers.Count > 0)
            {
                result.Found = true;
                result.X = centers[0].X;
                result.Y = centers[0].Y;
                result.Confidence = 1.0;
            }
        }

        return result;
    }

    /// <summary>
    /// Evaluate a C# expression for ColorCal v2.
    /// Variables available: {TargetName}.X, {TargetName}.Y, {TargetName}.Found
    /// Supports: arithmetic, comparison, ternary, Math functions.
    /// Result must be a single integer (branch index).
    /// </summary>
    private static int EvaluateColorCalExpressionV2(string expression, List<ColorCalTargetResult> results, List<ColorCalTarget> targets, FlowContext context, string nodeId)
    {
        expression = expression.Trim();

        // Direct integer
        if (int.TryParse(expression, out var directInt)) return directInt;

        // Pre-process: replace {Name}.X / {Name}.Y / {Name}.Found with actual values
        // Also support index-based aliases: A=first target, B=second, etc.
        var processedExpr = expression;
        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            // Name-based: Target1.X, Target2.X, etc.
            processedExpr = processedExpr.Replace($"{r.Name}.X", r.X.ToString());
            processedExpr = processedExpr.Replace($"{r.Name}.Y", r.Y.ToString());
            processedExpr = processedExpr.Replace($"{r.Name}.Found", r.Found ? "1" : "0");
            // Index-based alias: A.X, B.X, C.X, ... (A=0, B=1, C=2, ...)
            char alias = (char)('A' + i);
            processedExpr = processedExpr.Replace($"{alias}.X", r.X.ToString());
            processedExpr = processedExpr.Replace($"{alias}.Y", r.Y.ToString());
            processedExpr = processedExpr.Replace($"{alias}.Found", r.Found ? "1" : "0");
        }

        // Handle nested ternary: recursively parse the outermost ? : pair.
        var rootTernary = ParseRootTernary(processedExpr);
        if (rootTernary.HasValue)
        {
            bool conditionResult = EvaluateColorCalCondition(rootTernary.Value.Condition);
            string branchToEval = conditionResult ? rootTernary.Value.TrueExpr : rootTernary.Value.FalseExpr;
            return EvaluateColorCalExpressionV2(branchToEval, results, targets, context, nodeId);
        }

        var value = EvaluateSimpleExpression(processedExpr);
        if (!double.IsFinite(value))
            throw new InvalidOperationException("ColorCal expression produced a non-finite result.");
        return checked((int)value);
    }

    /// <summary>
    /// Parse the root-level ternary operator, correctly handling nesting and parentheses.
    /// Returns null if no root-level ternary is found.
    /// </summary>
    private static (string Condition, string TrueExpr, string FalseExpr)? ParseRootTernary(string expr)
    {
        expr = StripOuterParentheses(expr);

        // Find the first outermost question mark, then its matching colon. A
        // nested ternary in either branch consumes one colon of its own; using
        // the last colon (the old approach) incorrectly parsed false branches.
        int questionIndex = -1;
        int depth = 0;
        for (int i = 0; i < expr.Length; i++)
        {
            char c = expr[i];
            if (c == '(') depth++;
            else if (c == ')')
            {
                if (--depth < 0)
                    throw new FormatException("ColorCal expression has an unmatched closing parenthesis.");
            }
            else if (c == '?' && depth == 0)
            {
                questionIndex = i;
                break;
            }
        }

        if (depth != 0 && questionIndex == -1)
            throw new FormatException("ColorCal expression has an unmatched opening parenthesis.");
        if (questionIndex == -1) return null;

        int colonIndex = -1;
        depth = 0;
        int nestedTernaries = 0;
        for (int i = questionIndex + 1; i < expr.Length; i++)
        {
            char c = expr[i];
            if (c == '(') depth++;
            else if (c == ')')
            {
                if (--depth < 0)
                    throw new FormatException("ColorCal expression has an unmatched closing parenthesis.");
            }
            else if (depth == 0 && c == '?')
            {
                nestedTernaries++;
            }
            else if (depth == 0 && c == ':')
            {
                if (nestedTernaries == 0)
                {
                    colonIndex = i;
                    break;
                }
                nestedTernaries--;
            }
        }

        if (colonIndex == -1)
            throw new FormatException("ColorCal ternary expression is missing a matching ':'.");

        string condition = expr.Substring(0, questionIndex).Trim();
        string trueExpr = expr.Substring(questionIndex + 1, colonIndex - questionIndex - 1).Trim();
        string falseExpr = expr.Substring(colonIndex + 1).Trim();

        if (condition.Length == 0 || trueExpr.Length == 0 || falseExpr.Length == 0)
            throw new FormatException("ColorCal ternary expression has an empty branch.");

        return (condition, trueExpr, falseExpr);
    }

    private static bool EvaluateColorCalCondition(string condition)
    {
        condition = StripOuterParentheses(condition);
        if (condition.Length == 0)
            throw new FormatException("ColorCal condition is empty.");

        if (TrySplitTopLevel(condition, "||", out var leftOr, out var rightOr))
            return EvaluateColorCalCondition(leftOr) || EvaluateColorCalCondition(rightOr);
        if (TrySplitTopLevel(condition, "&&", out var leftAnd, out var rightAnd))
            return EvaluateColorCalCondition(leftAnd) && EvaluateColorCalCondition(rightAnd);
        if (condition[0] == '!' && !condition.StartsWith("!=", StringComparison.Ordinal))
            return !EvaluateColorCalCondition(condition[1..]);

        if (!TryFindTopLevelComparison(condition, out var comparisonIndex, out var op))
            return Math.Abs(EvaluateSimpleExpression(condition)) > 0.001;

        double left = EvaluateSimpleExpression(condition[..comparisonIndex]);
        double right = EvaluateSimpleExpression(condition[(comparisonIndex + op.Length)..]);

        return op switch
        {
            ">" => left > right,
            ">=" => left >= right,
            "<" => left < right,
            "<=" => left <= right,
            "==" => Math.Abs(left - right) < 0.001,
            "!=" => Math.Abs(left - right) >= 0.001,
            _ => false
        };
    }

    private static double EvaluateSimpleExpression(string expression)
    {
        return new NumericExpressionParser(expression).Parse();
    }

    private static string StripOuterParentheses(string expression)
    {
        var result = expression.Trim();
        while (result.Length >= 2 && result[0] == '(' && result[^1] == ')')
        {
            int depth = 0;
            bool wrapsWholeExpression = true;
            for (int i = 0; i < result.Length; i++)
            {
                if (result[i] == '(') depth++;
                else if (result[i] == ')')
                {
                    if (--depth < 0)
                        throw new FormatException("ColorCal expression has an unmatched closing parenthesis.");
                }

                if (depth == 0 && i < result.Length - 1)
                {
                    wrapsWholeExpression = false;
                    break;
                }
            }

            if (depth != 0)
                throw new FormatException("ColorCal expression has an unmatched opening parenthesis.");
            if (!wrapsWholeExpression) break;
            result = result[1..^1].Trim();
        }

        return result;
    }

    private static bool TrySplitTopLevel(
        string expression, string separator, out string left, out string right)
    {
        int depth = 0;
        for (int i = 0; i <= expression.Length - separator.Length; i++)
        {
            char c = expression[i];
            if (c == '(')
            {
                depth++;
                continue;
            }
            if (c == ')')
            {
                if (--depth < 0)
                    throw new FormatException("ColorCal expression has an unmatched closing parenthesis.");
                continue;
            }

            if (depth == 0 && string.CompareOrdinal(expression, i, separator, 0, separator.Length) == 0)
            {
                left = expression[..i].Trim();
                right = expression[(i + separator.Length)..].Trim();
                if (left.Length == 0 || right.Length == 0)
                    throw new FormatException("ColorCal logical expression has an empty operand.");
                return true;
            }
        }

        if (depth != 0)
            throw new FormatException("ColorCal expression has an unmatched opening parenthesis.");
        left = right = string.Empty;
        return false;
    }

    private static bool TryFindTopLevelComparison(
        string expression, out int index, out string op)
    {
        int depth = 0;
        for (int i = 0; i < expression.Length; i++)
        {
            char c = expression[i];
            if (c == '(')
            {
                depth++;
                continue;
            }
            if (c == ')')
            {
                if (--depth < 0)
                    throw new FormatException("ColorCal expression has an unmatched closing parenthesis.");
                continue;
            }
            if (depth != 0) continue;

            if (c is '>' or '<' or '=' or '!')
            {
                string candidate = i + 1 < expression.Length && expression[i + 1] == '='
                    ? expression.Substring(i, 2)
                    : expression.Substring(i, 1);
                if (candidate is ">" or ">=" or "<" or "<=" or "==" or "!=")
                {
                    index = i;
                    op = candidate;
                    return true;
                }
            }
        }

        if (depth != 0)
            throw new FormatException("ColorCal expression has an unmatched opening parenthesis.");
        index = -1;
        op = string.Empty;
        return false;
    }

    /// <summary>
    /// Small, closed arithmetic parser for ColorCal expressions after target
    /// aliases have been replaced with numbers. This avoids executing arbitrary
    /// DataTable expressions while supporting the documented arithmetic and
    /// common Math helpers.
    /// </summary>
    private sealed class NumericExpressionParser(string expression)
    {
        private readonly string _expression = expression ?? throw new ArgumentNullException(nameof(expression));
        private int _position;

        public double Parse()
        {
            var result = ParseAdditive();
            SkipWhitespace();
            if (_position != _expression.Length)
                throw Error($"Unexpected token '{_expression[_position]}'.");
            return EnsureFinite(result);
        }

        private double ParseAdditive()
        {
            var value = ParseMultiplicative();
            while (true)
            {
                if (TryConsume('+')) value = EnsureFinite(value + ParseMultiplicative());
                else if (TryConsume('-')) value = EnsureFinite(value - ParseMultiplicative());
                else return value;
            }
        }

        private double ParseMultiplicative()
        {
            var value = ParsePower();
            while (true)
            {
                if (TryConsume('*')) value = EnsureFinite(value * ParsePower());
                else if (TryConsume('/'))
                {
                    var divisor = ParsePower();
                    if (Math.Abs(divisor) < double.Epsilon)
                        throw new DivideByZeroException("ColorCal expression divides by zero.");
                    value = EnsureFinite(value / divisor);
                }
                else if (TryConsume('%'))
                {
                    var divisor = ParsePower();
                    if (Math.Abs(divisor) < double.Epsilon)
                        throw new DivideByZeroException("ColorCal expression takes a remainder by zero.");
                    value = EnsureFinite(value % divisor);
                }
                else return value;
            }
        }

        private double ParsePower()
        {
            var value = ParseUnary();
            return TryConsume('^') ? EnsureFinite(Math.Pow(value, ParsePower())) : value;
        }

        private double ParseUnary()
        {
            if (TryConsume('+')) return ParseUnary();
            if (TryConsume('-')) return EnsureFinite(-ParseUnary());
            return ParsePrimary();
        }

        private double ParsePrimary()
        {
            if (TryConsume('('))
            {
                var value = ParseAdditive();
                Expect(')');
                return value;
            }

            SkipWhitespace();
            if (_position >= _expression.Length)
                throw Error("Expected a number, function, or parenthesized expression.");
            if (char.IsDigit(_expression[_position]) || _expression[_position] == '.')
                return ParseNumber();
            if (char.IsLetter(_expression[_position]) || _expression[_position] == '_')
                return ParseIdentifierOrFunction();
            throw Error($"Unexpected token '{_expression[_position]}'.");
        }

        private double ParseIdentifierOrFunction()
        {
            int start = _position;
            while (_position < _expression.Length &&
                   (char.IsLetterOrDigit(_expression[_position]) || _expression[_position] is '_' or '.'))
                _position++;
            var name = _expression[start.._position];
            if (name.Equals("true", StringComparison.OrdinalIgnoreCase)) return 1;
            if (name.Equals("false", StringComparison.OrdinalIgnoreCase)) return 0;

            Expect('(');
            var arguments = new List<double>();
            if (!Peek(')'))
            {
                do
                {
                    arguments.Add(ParseAdditive());
                } while (TryConsume(','));
            }
            Expect(')');

            return name.ToLowerInvariant() switch
            {
                "abs" or "math.abs" => ApplyUnary(name, arguments, Math.Abs),
                "floor" or "math.floor" => ApplyUnary(name, arguments, Math.Floor),
                "ceiling" or "math.ceiling" => ApplyUnary(name, arguments, Math.Ceiling),
                "sqrt" or "math.sqrt" => ApplyUnary(name, arguments, Math.Sqrt),
                "round" or "math.round" => ApplyRound(name, arguments),
                "min" or "math.min" => ApplyBinary(name, arguments, Math.Min),
                "max" or "math.max" => ApplyBinary(name, arguments, Math.Max),
                _ => throw Error($"Unsupported function '{name}'.")
            };
        }

        private double ParseNumber()
        {
            int start = _position;
            while (_position < _expression.Length && char.IsDigit(_expression[_position])) _position++;
            if (_position < _expression.Length && _expression[_position] == '.')
            {
                _position++;
                while (_position < _expression.Length && char.IsDigit(_expression[_position])) _position++;
            }
            if (_position < _expression.Length && _expression[_position] is 'e' or 'E')
            {
                _position++;
                if (_position < _expression.Length && _expression[_position] is '+' or '-') _position++;
                int exponentStart = _position;
                while (_position < _expression.Length && char.IsDigit(_expression[_position])) _position++;
                if (exponentStart == _position)
                    throw Error("A scientific-notation exponent requires digits.");
            }

            var token = _expression[start.._position];
            if (!double.TryParse(token, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
                throw Error($"Invalid number '{token}'.");
            return EnsureFinite(value);
        }

        private bool TryConsume(char expected)
        {
            SkipWhitespace();
            if (_position >= _expression.Length || _expression[_position] != expected) return false;
            _position++;
            return true;
        }

        private bool Peek(char expected)
        {
            SkipWhitespace();
            return _position < _expression.Length && _expression[_position] == expected;
        }

        private void Expect(char expected)
        {
            if (!TryConsume(expected))
                throw Error($"Expected '{expected}'.");
        }

        private void SkipWhitespace()
        {
            while (_position < _expression.Length && char.IsWhiteSpace(_expression[_position]))
                _position++;
        }

        private FormatException Error(string message) =>
            new($"{message} Position {_position} in '{_expression}'.");

        private static double EnsureFinite(double value)
        {
            if (!double.IsFinite(value))
                throw new OverflowException("ColorCal expression produced a non-finite number.");
            return value;
        }

        private static double ApplyUnary(string name, IReadOnlyList<double> arguments, Func<double, double> function)
        {
            if (arguments.Count != 1)
                throw new FormatException($"Function '{name}' requires one argument.");
            return EnsureFinite(function(arguments[0]));
        }

        private static double ApplyBinary(string name, IReadOnlyList<double> arguments, Func<double, double, double> function)
        {
            if (arguments.Count != 2)
                throw new FormatException($"Function '{name}' requires two arguments.");
            return EnsureFinite(function(arguments[0], arguments[1]));
        }

        private static double ApplyRound(string name, IReadOnlyList<double> arguments)
        {
            return arguments.Count switch
            {
                1 => EnsureFinite(Math.Round(arguments[0])),
                2 => EnsureFinite(Math.Round(arguments[0], checked((int)arguments[1]))),
                _ => throw new FormatException($"Function '{name}' requires one or two arguments.")
            };
        }
    }

    // ============ Break ============

    private async Task ExecuteBreakAsync(FlowNode node)
    {
        _context.Logger.Info(node.NodeName, "Break node triggered");

        // Find the active Loop node and signal break
        // The Break node receives a signal from a Condition/ColorMotion node
        // and sets the break flag on the associated Loop node

        // Signal break - search context for active loop
        bool signaled = false;
        foreach (var key in _context.Variables.Keys)
        {
            if (key.EndsWith("_loop_active") && _context.Get<bool?>(key) == true)
            {
                var loopId = key.Replace("_loop_active", "");
                _context.Set($"{loopId}_break", true);
                _context.Logger.Info(node.NodeName, $"Break signaled to Loop: {loopId}");
                signaled = true;
                break;
            }
        }

        if (!signaled)
            _context.Logger.Warning(node.NodeName, "No active loop found to break");

        // Execute True branch (Break) if connected
        if (node.TrueBranch != null && node.TrueBranch.Count > 0)
            await ExecuteNodeListAsync(node.TrueBranch);

        await Task.CompletedTask;
    }

    // ============ Helpers ============
}

// Support classes
public class Region
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

public class TemplateScaleRange
{
    public double Min { get; set; } = 0.5;
    public double Max { get; set; } = 1.5;
    public double Step { get; set; } = 0.1;
}
