using System.Runtime.InteropServices;
using System.Text.Json;
using FlowAuto.Core;
using FlowAuto.Engine;
using FlowAuto.Models;

namespace FlowAuto;

public partial class MainForm : Form
{
    private FlowCanvas _canvas = null!;
    private ToolboxPanel _toolbox = null!;
    private PropertyPanel _propertyPanel = null!;
    private RichTextBox _logBox = null!;
    private FlowLogger _logger = null!;
    private FlowContext? _execContext;
    private FlowExecutor? _executor;
    private string _currentFilePath = "";

    // Toolbar buttons
    private Button _btnNew = null!;
    private Button _btnLoad = null!;
    private Button _btnSave = null!;
    private Button _btnRun = null!;
    private Button _btnPause = null!;
    private Button _btnStop = null!;
    private Button _btnScreenshot = null!;
    private Button _btnWindowPicker = null!;
    private Button _btnRegionPicker = null!;
    private Button _btnColorPicker = null!;
    private Button _btnKeyPicker = null!;
    private Button _btnSettings = null!;
    private Button _btnArrange = null!;
    private Button _btnExample = null!;
    private readonly ToolTip _toolTip = new();
    private bool _isDirty;

    // Global settings
    private int _globalPreDelayMs = 500;
    private int _globalPostDelayMs = 500;

    private Label _statusLabel = null!;
    private SplitContainer _mainSplit = null!;
    private SplitContainer _rightSplit = null!;

    public MainForm()
    {
        Text = "FlowAuto - Visual Workflow Automation Engine";
        Size = new Size(1400, 900);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1050, 680);
        BackColor = AppTheme.Window;
        Font = new Font("Segoe UI", 9);
        KeyPreview = true;

        InitializeComponents();
        UpdateWindowTitle();
        FormClosing += OnMainFormClosing;
    }

    private void InitializeComponents()
    {
        // Top toolbar
        var toolbar = CreateToolbar();

        // Status bar
        _statusLabel = new Label
        {
            Text = "Ready",
            ForeColor = AppTheme.TextMuted,
            BackColor = AppTheme.Surface,
            Dock = DockStyle.Bottom,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 0, 0)
        };

        // Main split container
        _mainSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterWidth = 5,
            BackColor = AppTheme.Border
        };

        // Left: Toolbox
        var toolboxContainer = new Panel { Dock = DockStyle.Fill };
        _toolbox = new ToolboxPanel();
        _toolbox.ToolActivated += type => _canvas.AddNode(FlowCanvas.CreateDefaultNode(type));
        toolboxContainer.Controls.Add(_toolbox);

        // Center: Canvas
        _canvas = new FlowCanvas { Dock = DockStyle.Fill };
        _canvas.NodesChanged += OnCanvasChanged;
        _canvas.ConnectionsChanged += OnCanvasChanged;
        _canvas.NodeSelected += OnNodeSelected;
        _canvas.SelectionCleared += () => _propertyPanel.ShowNode(null);

        // Right split: Property panel + Log
        _rightSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 5,
            BackColor = AppTheme.Border
        };

        // Right-top: Property panel
        _propertyPanel = new PropertyPanel();
        _propertyPanel.ValueChanged += () =>
        {
            _isDirty = true;
            UpdateWindowTitle();
            _canvas.Invalidate();
        };
        var propContainer = new Panel { Dock = DockStyle.Fill };
        var propHeader = CreateSectionHeader("PROPERTIES");
        propContainer.Controls.Add(_propertyPanel);
        propContainer.Controls.Add(propHeader);

        // Right-bottom: Log
        _logBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            BackColor = AppTheme.Canvas,
            ForeColor = Color.FromArgb(205, 214, 230),
            Font = new Font("Consolas", 9),
            ReadOnly = true,
            WordWrap = false,
            BorderStyle = BorderStyle.None,
            Padding = new Padding(8)
        };
        var logContainer = new Panel { Dock = DockStyle.Fill };
        var logHeader = CreateSectionHeader("EXECUTION LOG",
            ("Copy", () => { if (_logBox.TextLength > 0) Clipboard.SetText(_logBox.Text); }),
            ("Clear", () => _logBox.Clear()));
        logContainer.Controls.Add(_logBox);
        logContainer.Controls.Add(logHeader);

        _rightSplit.Panel1.Controls.Add(propContainer);
        _rightSplit.Panel2.Controls.Add(logContainer);

        // Layout splits
        _mainSplit.Panel1.Controls.Add(toolboxContainer);
        _mainSplit.Panel2.Controls.Add(_canvas);

        // Main layout
        var mainPanel = new Panel { Dock = DockStyle.Fill };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));

        layout.Controls.Add(_mainSplit, 0, 0);
        layout.Controls.Add(_rightSplit, 1, 0);

        mainPanel.Controls.Add(layout);
        mainPanel.Controls.Add(toolbar);
        mainPanel.Controls.Add(_statusLabel);

        toolbar.Dock = DockStyle.Top;
        _statusLabel.Dock = DockStyle.Bottom;

        Controls.Add(mainPanel);

        _logger = new FlowLogger(richTextBox: _logBox);

        // Set split container properties after layout is complete
        Load += (s, e) =>
        {
            _mainSplit.Panel1MinSize = 150;
            _mainSplit.Panel2MinSize = 250;
            _mainSplit.SplitterDistance = Math.Min(250, _mainSplit.Width - _mainSplit.Panel2MinSize - 10);

            _rightSplit.Panel1MinSize = 100;
            _rightSplit.Panel2MinSize = 100;
            _rightSplit.SplitterDistance = Math.Min(430, _rightSplit.Height - _rightSplit.Panel2MinSize - 10);
        };

        // Start with empty canvas
    }

    // ============ Global hotkeys ============

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.N)) { NewFlow(); return true; }
        if (keyData == (Keys.Control | Keys.O)) { LoadFlow(); return true; }
        if (keyData == (Keys.Control | Keys.Shift | Keys.S)) { SaveFlow(saveAs: true); return true; }
        if (keyData == (Keys.Control | Keys.S))
        {
            SaveFlow();
            return true;
        }
        if (keyData == Keys.F5 && _btnRun.Enabled) { RunFlow(); return true; }
        if (keyData == (Keys.Shift | Keys.F5) && _btnStop.Enabled) { StopFlow(); return true; }
        if (keyData == (Keys.Control | Keys.L)) { _logBox.Clear(); return true; }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private Control CreateToolbar()
    {
        var toolbar = new FlowLayoutPanel
        {
            Height = 58,
            BackColor = AppTheme.Surface,
            Padding = new Padding(12, 10, 8, 8),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoScroll = true
        };

        _btnNew = CreateToolButton("New", () => NewFlow(), "New flow (Ctrl+N)");
        _btnLoad = CreateToolButton("Open", () => LoadFlow(), "Open flow (Ctrl+O)");
        _btnSave = CreateToolButton("Save", () => SaveFlow(), "Save flow (Ctrl+S)");
        _btnRun = CreateToolButton("▶ Run", () => RunFlow(), "Run flow (F5)", AppTheme.Success, 72);
        _btnPause = CreateToolButton("Ⅱ Pause", () => PauseFlow(), "Pause or resume", AppTheme.Warning, 78);
        _btnStop = CreateToolButton("■ Stop", () => StopFlow(), "Stop flow (Shift+F5)", AppTheme.Danger, 72);
        _btnArrange = CreateToolButton("Arrange", ArrangeCanvas, "Automatically arrange nodes", AppTheme.Accent, 74);
        _btnExample = CreateToolButton("Example", CreateExampleFlow, "Create a starter flow", width: 72);
        _btnScreenshot = CreateToolButton("Snip", OpenScreenshotTool, "Capture a screen region");
        _btnWindowPicker = CreateToolButton("Window", OpenWindowPicker, "Pick a target window", width: 68);
        _btnRegionPicker = CreateToolButton("Region", OpenRegionPicker, "Pick a window region", width: 66);
        _btnColorPicker = CreateToolButton("Color", OpenColorPicker, "Pick a target color", Color.FromArgb(166, 95, 215), 62);
        _btnKeyPicker = CreateToolButton("Key", OpenKeyPicker, "Capture a key", Color.FromArgb(54, 130, 220), 54);
        _btnSettings = CreateToolButton("Settings", OpenGlobalSettings, "Global delays", width: 72);

        toolbar.Controls.AddRange([
            _btnNew, _btnLoad, _btnSave, CreateSeparator(),
            _btnRun, _btnPause, _btnStop, CreateSeparator(),
            _btnArrange, _btnExample, CreateSeparator(),
            _btnScreenshot, _btnWindowPicker, _btnRegionPicker, _btnColorPicker, _btnKeyPicker, _btnSettings
        ]);

        return toolbar;
    }

    private Button CreateToolButton(string text, Action action, string tooltip, Color? color = null, int width = 58)
    {
        var btn = new Button
        {
            Text = text,
            Size = new Size(width, 34),
            Margin = new Padding(0, 0, 6, 0)
        };
        AppTheme.StyleButton(btn, color);
        btn.Click += (s, e) => action();
        _toolTip.SetToolTip(btn, tooltip);
        return btn;
    }

    private static Control CreateSeparator() => new Panel
    {
        Size = new Size(1, 26),
        Margin = new Padding(5, 4, 11, 0),
        BackColor = AppTheme.Border
    };

    private static Panel CreateSectionHeader(string title, params (string Text, Action Action)[] actions)
    {
        var panel = new Panel { Dock = DockStyle.Top, Height = 38, BackColor = AppTheme.SurfaceRaised };
        var label = new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 0, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = AppTheme.TextMuted,
            Font = new Font("Segoe UI Semibold", 9)
        };
        panel.Controls.Add(label);
        foreach (var action in actions.Reverse())
        {
            var button = new Button { Text = action.Text, Dock = DockStyle.Right, Width = 52 };
            AppTheme.StyleButton(button);
            button.FlatAppearance.BorderSize = 0;
            button.Click += (_, _) => action.Action();
            panel.Controls.Add(button);
        }
        return panel;
    }

    // ============ Canvas events ============

    private void OnCanvasChanged()
    {
        _isDirty = true;
        UpdateWindowTitle();
        _statusLabel.Text = $"  {_canvas.Nodes.Count} nodes   •   {_canvas.Connections.Count} connections";
    }

    private void UpdateWindowTitle()
    {
        var name = string.IsNullOrEmpty(_currentFilePath) ? "Untitled" : Path.GetFileName(_currentFilePath);
        Text = $"{(_isDirty ? "● " : "")}FlowAuto  —  {name}";
    }

    private void ArrangeCanvas()
    {
        _canvas.AutoLayout();
        _statusLabel.Text = "Canvas arranged automatically";
    }

    private void OnMainFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_isDirty)
        {
            var result = MessageBox.Show("Save changes before closing?", "Unsaved Flow",
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (result == DialogResult.Cancel)
            {
                e.Cancel = true;
                return;
            }
            if (result == DialogResult.Yes)
            {
                SaveFlow();
                e.Cancel = _isDirty; // Save dialog was cancelled or saving failed.
            }
        }

        if (e.Cancel) return;

        // A background flow and a global keyboard hook can otherwise outlive
        // the UI controls they call into during shutdown.
        _execContext?.Cts?.Cancel();
        if (_execContext?.IsPaused == true)
        {
            _execContext.IsPaused = false;
            _execContext.PauseTcs?.TrySetResult(false);
        }
        RemoveKeyHook();
    }

    private void OnNodeSelected(int index)
    {
        if (index >= 0 && index < _canvas.Nodes.Count)
        {
            _propertyPanel.ShowNode(_canvas.Nodes[index]);
        }
    }

    // ============ File operations ============

    private void NewFlow()
    {
        if (!ConfirmDiscardChanges("create a new flow")) return;

        _canvas.ClearNodes();
        _currentFilePath = "";
        _isDirty = false;
        UpdateWindowTitle();
        _statusLabel.Text = "New flow";
    }

    private void LoadFlow()
    {
        if (!ConfirmDiscardChanges("load another flow")) return;

        using var dlg = new OpenFileDialog
        {
            Filter = "Flow Files|*.flow.json|All Files|*.*",
            Title = "Load Flow"
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        try
        {
            var json = File.ReadAllText(dlg.FileName);
            var flow = JsonSerializer.Deserialize<FlowDefinition>(json);
            var validationErrors = FlowValidator.Validate(flow);
            if (validationErrors.Count > 0)
            {
                MessageBox.Show(string.Join(Environment.NewLine, validationErrors), "Invalid Flow",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (flow == null) return; // Guaranteed by validation; keeps nullable analysis explicit.

            _canvas.ClearNodes();
            foreach (var node in flow.Nodes)
            {
                _canvas.Nodes.Add(node);
            }
            // Load connections if present in JSON
            if (flow.Connections != null && flow.Connections.Count > 0)
            {
                _canvas.LoadConnections(flow.Connections);
            }
            else
            {
                // Fallback: auto-connect for backward compatibility
                AutoConnectNodes();
            }
            _canvas.Invalidate();
            _currentFilePath = dlg.FileName;
            _isDirty = false;
            UpdateWindowTitle();
            _statusLabel.Text = $"Loaded: {Path.GetFileName(dlg.FileName)}";
            _logger.Info("SYSTEM", $"Loaded flow: {flow.FlowName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private bool ConfirmDiscardChanges(string action)
    {
        if (!_isDirty) return true;

        var result = MessageBox.Show(
            $"Save changes before you {action}?", "Unsaved Flow",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (result == DialogResult.Cancel) return false;
        if (result != DialogResult.Yes) return true;

        SaveFlow();
        return !_isDirty;
    }

    private void AutoConnectNodes()
    {
        _canvas.Connections.Clear();
        for (int i = 0; i < _canvas.Nodes.Count - 1; i++)
        {
            var fromNode = _canvas.Nodes[i];
            var toNode = _canvas.Nodes[i + 1];
            // Determine appropriate ports based on node types
            string fromPort = fromNode.NodeType switch
            {
                NodeType.Condition or NodeType.Loop => "True",
                NodeType.ColorCal => "0",
                _ => "Output"
            };
            _canvas.AddConnection(fromNode.NodeId, toNode.NodeId, fromPort);
        }
    }

    private void SaveFlow(bool saveAs = false)
    {
        var targetPath = _currentFilePath;
        if (saveAs || string.IsNullOrEmpty(targetPath))
        {
            using var dlg = new SaveFileDialog
            {
                Filter = "Flow Files|*.flow.json|All Files|*.*",
                Title = "Save Flow",
                DefaultExt = ".flow.json",
                FileName = string.IsNullOrEmpty(_currentFilePath) ? "unnamed.flow.json" : Path.GetFileName(_currentFilePath)
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            targetPath = dlg.FileName;
        }

        try
        {
            var flow = GetFlowDefinition();
            var json = JsonSerializer.Serialize(flow, new JsonSerializerOptions { WriteIndented = true });
            var tempPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, targetPath, true);
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            _currentFilePath = targetPath;
            _isDirty = false;
            UpdateWindowTitle();
            _statusLabel.Text = $"Saved: {Path.GetFileName(targetPath)}";
            _logger.Info("SYSTEM", $"Saved flow: {flow.FlowName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public FlowDefinition GetFlowDefinition()
    {
        var flowName = string.IsNullOrEmpty(_currentFilePath)
            ? "Untitled Flow"
            : Path.GetFileNameWithoutExtension(_currentFilePath);
        return new FlowDefinition
        {
            FlowName = flowName,
            Nodes = _canvas.Nodes.ToList(),
            Connections = _canvas.Connections.ToList()
        };
    }

    // ============ Execution ============

    private async void RunFlow()
    {
        var flow = GetFlowDefinition();
        var validationErrors = FlowValidator.Validate(flow);
        if (validationErrors.Count > 0)
        {
            MessageBox.Show(string.Join(Environment.NewLine, validationErrors), "Invalid Flow",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (flow.Nodes.Count == 0)
        {
            MessageBox.Show("No nodes to execute.", "Info");
            return;
        }

        _logBox.Clear();
        _execContext = new FlowContext(_logger);
        _executor = new FlowExecutor(_execContext);
        _execContext.Cts = new CancellationTokenSource();

        // Inject global delay settings into context
        _execContext.Set("GlobalPreDelayMs", _globalPreDelayMs);
        _execContext.Set("GlobalPostDelayMs", _globalPostDelayMs);

        SetExecutionButtons(running: true);

        try
        {
            await Task.Run(async () =>
            {
                try
                {
                    await _executor.ExecuteAsync(flow);
                }
                catch (OperationCanceledException)
                {
                    _logger.Warning("SYSTEM", "Execution stopped by user");
                }
                catch (Exception ex)
                {
                    _logger.Error("SYSTEM", $"Execution failed: {ex.Message}");
                }
            });
        }
        finally
        {
            _execContext?.Cts?.Dispose();
            if (_execContext != null) _execContext.Cts = null;
            if (!IsDisposed && !Disposing)
                SetExecutionButtons(running: false);
        }
    }

    private void PauseFlow()
    {
        if (_execContext == null) return;

        if (!_execContext.IsPaused)
        {
            _execContext.IsPaused = true;
            _execContext.PauseTcs = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _btnPause.Text = "Resume";
            _btnPause.BackColor = AppTheme.Success;
            _statusLabel.Text = "PAUSED";
        }
        else
        {
            _execContext.IsPaused = false;
            _execContext.PauseTcs?.TrySetResult(true);
            _btnPause.Text = "Pause";
            _btnPause.BackColor = AppTheme.Warning;
            _statusLabel.Text = "Running...";
        }
    }

    private void StopFlow()
    {
        _execContext?.Cts?.Cancel();
        if (_execContext?.IsPaused == true)
        {
            _execContext.IsPaused = false;
            _execContext.PauseTcs?.TrySetResult(false);
        }
        // Keep Run disabled until the executor has actually unwound. Starting a
        // second flow here could otherwise overlap with an action still stopping.
        _statusLabel.Text = "Stopping...";
        _logger.Info("SYSTEM", "Stopping...");
    }

    private void SetExecutionButtons(bool running)
    {
        _btnRun.Enabled = !running;
        _btnPause.Enabled = running;
        _btnStop.Enabled = running;
        _btnPause.Text = "Pause";
        _btnPause.BackColor = AppTheme.Warning;

        if (running)
            _statusLabel.Text = "Running...";
        else
            _statusLabel.Text = "Ready";
    }

    // ============ Screenshot Tool ============

    private void OpenScreenshotTool()
    {
        // ── If current node is ColorMotion or ClickElement, pass HSV params so the snip is filtered ──
        System.Drawing.Color? hsvColor = null;
        int hueTol = 8, svTol = 30;
        if (_propertyPanel.CurrentNode != null &&
            (_propertyPanel.CurrentNode.NodeType == Models.NodeType.ColorMotion ||
             _propertyPanel.CurrentNode.NodeType == Models.NodeType.ClickElement))
        {
            var node = _propertyPanel.CurrentNode;
            hsvColor = node.ResolveTargetRgb();
            hueTol = node.GetParam<int?>("HueTolerance") ?? 8;
            svTol = node.GetParam<int?>("SVTolerance") ?? 30;
        }

        new ScreenshotOverlay(screenshotPath =>
        {
            foreach (var idx in _canvas.SelectedNodeIndices)
            {
                var node = _canvas.Nodes[idx];
                node.SetParam("TemplateImagePath", screenshotPath);
                // For ColorMotion/ClickElement HSVTemplateMatch, also set as ReferenceImagePath
                if (node.NodeType == Models.NodeType.ColorMotion || node.NodeType == Models.NodeType.ClickElement)
                    node.SetParam("ReferenceImagePath", screenshotPath);
            }
            if (_propertyPanel.CurrentNode != null)
                _propertyPanel.ShowNode(_propertyPanel.CurrentNode);
            _logger.Info("SYSTEM", $"Screenshot saved: {screenshotPath} ({_canvas.SelectedNodeIndices.Count} node(s))");
        }, hsvColor, hueTol, svTol).ShowDialog(this);
    }

    // ============ Window Picker ============

    private void OpenWindowPicker()
    {
        _statusLabel.Text = "Move mouse over target window and press Ctrl...";
        MessageBox.Show("Move your mouse over the target window and press Ctrl to capture its title.",
            "Window Picker", MessageBoxButtons.OK, MessageBoxIcon.Information);

        // Start monitoring
        StartWindowPicker();
    }

    private void StartWindowPicker()
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                // Check if Ctrl is held
                if ((Control.ModifierKeys & Keys.Control) == Keys.Control)
                {
                    IntPtr hWnd = WindowHelper.GetForegroundWindow();
                    if (hWnd != IntPtr.Zero)
                    {
                        var sb = new System.Text.StringBuilder(256);
                        WindowHelper.GetWindowText(hWnd, sb, sb.Capacity);
                        string title = sb.ToString();

                        this.Invoke(() =>
                    {
                        foreach (var idx in _canvas.SelectedNodeIndices)
                        {
                            var node = _canvas.Nodes[idx];
                            node.SetParam("TargetWindow", title);
                        }
                        if (_propertyPanel.CurrentNode != null)
                            _propertyPanel.ShowNode(_propertyPanel.CurrentNode);
                        _statusLabel.Text = $"Window captured: {title}";
                        _logger.Info("SYSTEM", $"Window picked: {title} ({_canvas.SelectedNodeIndices.Count} node(s))");
                    });
                        break;
                    }
                }
                Thread.Sleep(100);
            }
        });
        thread.IsBackground = true;
        thread.Start();
        _statusLabel.Text = "Waiting for Ctrl press...";
    }

    // ============ Region Picker ============

    private void OpenRegionPicker()
    {
        if (_propertyPanel.CurrentNode == null)
        {
            MessageBox.Show("Please select a node first.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var node = _propertyPanel.CurrentNode;
        var targetWindow = node.GetParam<string>("TargetWindow") ?? "";

        // ColorCal: try per-target TargetWindow from DetectionTargets
        if (string.IsNullOrEmpty(targetWindow) && node.NodeType == NodeType.ColorCal)
        {
            var targets = node.GetParam<List<ColorCalTarget>>("DetectionTargets");
            if (targets != null && targets.Count > 0)
                targetWindow = targets[0].TargetWindow ?? "";
        }

        if (string.IsNullOrEmpty(targetWindow))
        {
            if (_execContext != null && _execContext.CurrentHwnd != IntPtr.Zero)
            {
                OpenWindowRegionPicker(_execContext.CurrentHwnd);
                return;
            }
            MessageBox.Show("Please set a Target Window first (use Pick Win, or manually type a window title).",
                "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var hWnd = WindowHelper.FindWindowByTitle(targetWindow);
        if (hWnd == IntPtr.Zero)
        {
            MessageBox.Show($"Window \"{targetWindow}\" not found. Make sure the target window is open.",
                "Window Not Found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        OpenWindowRegionPicker(hWnd);
    }

    private void OpenWindowRegionPicker(IntPtr hWnd)
    {
        this.WindowState = FormWindowState.Minimized;

        var picker = new WindowRegionPicker(hWnd, region =>
        {
            this.Invoke(() =>
            {
                foreach (var idx in _canvas.SelectedNodeIndices)
                {
                    var selNode = _canvas.Nodes[idx];
                    // ColorCal: fill Region for ALL targets
                    if (selNode.NodeType == NodeType.ColorCal)
                    {
                        var targets = selNode.GetParam<List<ColorCalTarget>>("DetectionTargets");
                        if (targets != null)
                        {
                            foreach (var t in targets)
                            {
                                t.Region = new Engine.Region { X = region.X, Y = region.Y, Width = region.Width, Height = region.Height };
                            }
                            selNode.SetParam("DetectionTargets", targets);
                        }
                    }
                    else
                    {
                        selNode.SetParam("Region", region);
                    }
                }
                if (_propertyPanel.CurrentNode != null)
                    _propertyPanel.ShowNode(_propertyPanel.CurrentNode);
                _statusLabel.Text = $"Region: ({region.X}, {region.Y}) {region.Width}x{region.Height}";
                _logger.Info("SYSTEM", $"Region picked: ({region.X},{region.Y}) {region.Width}x{region.Height} ({_canvas.SelectedNodeIndices.Count} node(s))");
            });
        });

        picker.FormClosed += (s, e) =>
        {
            this.Invoke(() =>
            {
                this.WindowState = FormWindowState.Normal;
                this.Activate();
            });
        };

        picker.Show();
    }

    // ============ Color Picker ============

    private void OpenColorPicker()
    {
        using var picker = new ColorPickerForm((color, hueTol, svTol) =>
        {
            int filledCount = 0;
            foreach (var idx in _canvas.SelectedNodeIndices)
            {
                var node = _canvas.Nodes[idx];
                if (node.NodeType == NodeType.ColorMotion ||
                    node.NodeType == NodeType.ColorCal ||
                    node.NodeType == NodeType.ClickElement)
                {
                    node.SetParam("TargetRgb", $"{color.R},{color.G},{color.B}");
                    node.SetParam("HueTolerance", hueTol);
                    node.SetParam("SVTolerance", svTol);
                    filledCount++;
                }
            }
            if (_propertyPanel.CurrentNode != null)
                _propertyPanel.ShowNode(_propertyPanel.CurrentNode);
            _statusLabel.Text = $"Color picked: RGB({color.R},{color.G},{color.B}) H±{hueTol} SV±{svTol}";
            _logger.Info("SYSTEM", $"Color picked: RGB({color.R},{color.G},{color.B}) HueTol={hueTol} SVTol={svTol} ({filledCount} node(s))");
        });

        if (picker.ShowDialog(this) == DialogResult.OK)
        {
            // Color already applied via callback
        }
    }

    // ============ Global Settings ============

    private void OpenGlobalSettings()
    {
        var form = new Form
        {
            Text = "Global Settings",
            Size = new Size(360, 200),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Color.FromArgb(37, 37, 42)
        };

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(16),
            BackColor = Color.FromArgb(37, 37, 42)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // Pre-delay
        var lblPre = new Label { Text = "Pre-click Delay (ms):", ForeColor = Color.White, AutoSize = true, TextAlign = ContentAlignment.MiddleRight };
        var nudPre = new NumericUpDown { Minimum = 0, Maximum = 99999, Value = _globalPreDelayMs, BackColor = Color.FromArgb(50, 50, 55), ForeColor = Color.White, Dock = DockStyle.Fill };
        table.Controls.Add(lblPre, 0, 0);
        table.Controls.Add(nudPre, 1, 0);

        // Post-delay
        var lblPost = new Label { Text = "Post-click Delay (ms):", ForeColor = Color.White, AutoSize = true, TextAlign = ContentAlignment.MiddleRight };
        var nudPost = new NumericUpDown { Minimum = 0, Maximum = 99999, Value = _globalPostDelayMs, BackColor = Color.FromArgb(50, 50, 55), ForeColor = Color.White, Dock = DockStyle.Fill };
        table.Controls.Add(lblPost, 0, 1);
        table.Controls.Add(nudPost, 1, 1);

        // Buttons
        var btnPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Padding = new Padding(0, 12, 0, 0) };
        var btnCancel = new Button { Text = "Cancel", FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(60, 60, 65), ForeColor = Color.White, Size = new Size(80, 28) };
        var btnOk = new Button { Text = "OK", FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(52, 168, 83), ForeColor = Color.White, Size = new Size(80, 28) };
        btnCancel.Click += (s, e) => form.DialogResult = DialogResult.Cancel;
        btnOk.Click += (s, e) => form.DialogResult = DialogResult.OK;
        btnPanel.Controls.Add(btnCancel);
        btnPanel.Controls.Add(btnOk);
        table.Controls.Add(btnPanel, 1, 2);

        form.Controls.Add(table);

        if (form.ShowDialog(this) == DialogResult.OK)
        {
            _globalPreDelayMs = (int)nudPre.Value;
            _globalPostDelayMs = (int)nudPost.Value;
            _statusLabel.Text = $"Global delays: Pre={_globalPreDelayMs}ms, Post={_globalPostDelayMs}ms";
            _logger.Info("SYSTEM", $"Global settings updated: Pre={_globalPreDelayMs}ms, Post={_globalPostDelayMs}ms");
        }
    }

    // ============ Example ============

    private void CreateExampleFlow()
    {
        if (_canvas.Nodes.Count > 0 && MessageBox.Show("Replace the current flow with an example?", "Example Flow",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        _canvas.ClearNodes();
        var flow = new FlowDefinition
        {
            FlowName = "Example: Launch & Click",
            Nodes = new List<FlowNode>
            {
                FlowCanvas.CreateDefaultNode(NodeType.StartProgram),
                FlowCanvas.CreateDefaultNode(NodeType.ClickElement),
                FlowCanvas.CreateDefaultNode(NodeType.WaitCondition),
                FlowCanvas.CreateDefaultNode(NodeType.KeyPress)
            }
        };

        foreach (var node in flow.Nodes)
        {
            _canvas.Nodes.Add(node);
        }
        AutoConnectNodes();
        _canvas.AutoLayout();
        OnCanvasChanged();
    }

    // ============ Pick Key (Low-level Keyboard Hook) ============

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hHook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hHook, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;

    private IntPtr _keyHookId = IntPtr.Zero;
    private LowLevelKeyboardProc? _keyHookProc;

    private void OpenKeyPicker()
    {
        RemoveKeyHook();

        _keyHookProc = KeyHookCallback;
        _keyHookId = SetWindowsHookEx(WH_KEYBOARD_LL, _keyHookProc, GetModuleHandle(null), 0);

        if (_keyHookId == IntPtr.Zero)
        {
            MessageBox.Show(this, "Failed to install keyboard hook.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _statusLabel.Text = "Press any key to capture its scan code...";
        _logger.Info("SYSTEM", "Key picker active — press any key...");
    }

    private void RemoveKeyHook()
    {
        if (_keyHookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyHookId);
            _keyHookId = IntPtr.Zero;
        }
        _keyHookProc = null;
    }

    private IntPtr KeyHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN)
        {
            var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            byte scanCode = (byte)kb.scanCode;

            // Unhook immediately to capture only the first key press
            if (_keyHookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyHookId);
                _keyHookId = IntPtr.Zero;
            }

            this.Invoke(() =>
            {
                bool known = InputSimulator.IsKnownScanCode(scanCode);
                string? keyName = InputSimulator.GetKeyName(scanCode);
                string displayName = keyName ?? $"VK 0x{kb.vkCode:X}";

                // Fill all selected KeyPress nodes
                int filledCount = 0;
                foreach (var idx in _canvas.SelectedNodeIndices)
                {
                    var node = _canvas.Nodes[idx];
                    if (node.NodeType == NodeType.KeyPress)
                    {
                        node.SetParam("KeyScanCode", scanCode);
                        if (!string.IsNullOrEmpty(keyName))
                            node.SetParam("KeyName", keyName);
                        filledCount++;
                    }
                }

                if (_propertyPanel.CurrentNode != null)
                    _propertyPanel.ShowNode(_propertyPanel.CurrentNode);

                string msg = $"Key captured: {displayName} (ScanCode: 0x{scanCode:X2})";
                if (!known)
                    msg += " ⚠️ Unknown scan code — may cause corrupted warnings";

                _statusLabel.Text = msg;
                _logger.Info("SYSTEM", $"{msg}{(filledCount > 0 ? $" — filled {filledCount} KeyPress node(s)" : "")}");

                MessageBox.Show(this, msg, "Pick Key", MessageBoxButtons.OK,
                    known ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            });

            return (IntPtr)1; // Block the key from further propagation
        }
        return CallNextHookEx(_keyHookId, nCode, wParam, lParam);
    }
}
