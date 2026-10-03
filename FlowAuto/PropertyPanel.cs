using System.Text.Json;
using FlowAuto.Engine;
using FlowAuto.Models;

namespace FlowAuto;

public class PropertyPanel : Panel
{
    private FlowNode? _currentNode;
    private readonly TableLayoutPanel _table;
    private readonly Dictionary<string, Control> _controls = new();
    private bool _rebuilding;
    private int _currentRow;

    public FlowNode? CurrentNode => _currentNode;
    public event Action? ValueChanged;

    public PropertyPanel()
    {
        Dock = DockStyle.Fill;
        BackColor = AppTheme.Surface;

        _table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoScroll = true,
            Padding = new Padding(12),
            BackColor = AppTheme.Surface
        };
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(_table);
    }

    public void ShowNode(FlowNode? node)
    {
        _currentNode = node;
        _controls.Clear();
        _table.Controls.Clear();
        _table.RowCount = 0;
        _table.RowStyles.Clear();
        _currentRow = 0;

        if (node == null)
        {
            ShowEmptyMessage();
            return;
        }

        ShowHeader(node);
        AddSeparator();

        // Common properties
        AddTextField("NodeName", "Name", node.NodeName, v => node.NodeName = v);
        AddCheckBox("Enabled", "Enabled", node.Enabled, v => node.Enabled = v);
        AddNumericField("TimeoutMs", "Timeout (ms)", node.TimeoutMs, v => node.TimeoutMs = v);
        AddNumericField("RetryCount", "Retry Count", node.RetryCount, v => node.RetryCount = v);
        AddSeparator();

        // Type-specific properties
        switch (node.NodeType)
        {
            case NodeType.StartProgram:
                ShowStartProgramParams(node);
                break;
            case NodeType.ClickElement:
                ShowClickElementParams(node);
                break;
            case NodeType.WaitCondition:
                ShowWaitConditionParams(node);
                break;
            case NodeType.KeyPress:
                ShowKeyPressParams(node);
                break;
            case NodeType.Loop:
                ShowLoopParams(node);
                break;
            case NodeType.LoopEnd:
                ShowLoopEndParams(node);
                break;
            case NodeType.Condition:
                ShowConditionParams(node);
                break;
            case NodeType.Gate:
                ShowGateParams(node);
                break;
            case NodeType.ColorMotion:
                ShowColorMotionParams(node);
                break;
            case NodeType.ColorCal:
                ShowColorCalParams(node);
                break;
            case NodeType.Break:
                ShowBreakParams(node);
                break;
        }

        // Help button at the bottom
        AddSeparator();
        var helpBtn = new Button
        {
            Text = "?  Help",
            Height = 26,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(60, 80, 100),
            ForeColor = Color.FromArgb(180, 210, 240),
            Font = new Font("Segoe UI", 8, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Dock = DockStyle.Fill
        };
        AppTheme.StyleButton(helpBtn, AppTheme.Accent);
        helpBtn.FlatAppearance.BorderSize = 0;
        helpBtn.Click += (s, e) =>
        {
            var helpForm = new HelpForm(node.NodeType);
            helpForm.ShowDialog();
        };
        _table.Controls.Add(helpBtn, 1, _currentRow);
        _table.SetColumnSpan(helpBtn, 2);
        _table.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        _currentRow++;
    }

    private void ShowHeader(FlowNode node)
    {
        var titleLabel = new Label
        {
            Text = $"Properties: {node.NodeType}",
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = AppTheme.Text,
            AutoSize = true
        };
        _table.Controls.Add(titleLabel, 0, _currentRow);
        _table.SetColumnSpan(titleLabel, 2);
        _table.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        _currentRow++;
    }

    private void ShowEmptyMessage()
    {
        var label = new Label
        {
            Text = "Select a node to view properties",
            ForeColor = AppTheme.TextMuted,
            AutoSize = true
        };
        _table.Controls.Add(label, 0, 0);
        _table.SetColumnSpan(label, 2);
        _table.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
    }

    // ============ Common field helpers ============

    private void AddTextField(string key, string label, string value, Action<string> setter)
    {
        AddLabel(label);
        var tb = new TextBox
        {
            Text = value,
            Dock = DockStyle.Fill,
            BackColor = AppTheme.SurfaceRaised,
            ForeColor = AppTheme.Text,
            BorderStyle = BorderStyle.FixedSingle
        };
        tb.TextChanged += (s, e) => { if (!_rebuilding) { setter(tb.Text); ValueChanged?.Invoke(); } };
        _table.Controls.Add(tb, 1, _currentRow);
        _controls[key] = tb;
        _table.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        _currentRow++;
    }

    private void AddNumericField(string key, string label, int value, Action<int> setter)
    {
        AddLabel(label);
        var nud = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 999999,
            Value = value,
            Dock = DockStyle.Fill,
            BackColor = AppTheme.SurfaceRaised,
            ForeColor = AppTheme.Text,
            BorderStyle = BorderStyle.FixedSingle
        };
        // Register handler AFTER initial Value is set to avoid recursive ShowNode during build
        nud.ValueChanged += (s, e) => { if (!_rebuilding) { setter((int)nud.Value); ValueChanged?.Invoke(); } };
        _table.Controls.Add(nud, 1, _currentRow);
        _controls[key] = nud;
        _table.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        _currentRow++;
    }

    private void AddDoubleField(string key, string label, double value, Action<double> setter)
    {
        AddLabel(label);
        var nud = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 999999,
            DecimalPlaces = 2,
            Increment = 0.1m,
            Value = (decimal)value,
            Dock = DockStyle.Fill,
            BackColor = AppTheme.SurfaceRaised,
            ForeColor = AppTheme.Text,
            BorderStyle = BorderStyle.FixedSingle
        };
        // Register handler AFTER initial Value is set to avoid recursive ShowNode during build
        nud.ValueChanged += (s, e) => { if (!_rebuilding) { setter((double)nud.Value); ValueChanged?.Invoke(); } };
        _table.Controls.Add(nud, 1, _currentRow);
        _controls[key] = nud;
        _table.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        _currentRow++;
    }

    private void AddCheckBox(string key, string label, bool value, Action<bool> setter)
    {
        AddLabel(label);
        var cb = new CheckBox
        {
            Checked = value,
            ForeColor = AppTheme.Text,
            BackColor = Color.Transparent
        };
        cb.CheckedChanged += (s, e) => { if (!_rebuilding) { setter(cb.Checked); ValueChanged?.Invoke(); } };
        _table.Controls.Add(cb, 1, _currentRow);
        _controls[key] = cb;
        _table.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        _currentRow++;
    }

    private void AddComboBox(string key, string label, string value, string[] items, Action<string> setter)
    {
        AddLabel(label);
        var cb = new ComboBox
        {
            Dock = DockStyle.Fill,
            BackColor = AppTheme.SurfaceRaised,
            ForeColor = AppTheme.Text,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat
        };
        cb.Items.AddRange(items);
        cb.SelectedItem = value;
        cb.SelectedIndexChanged += (s, e) => { if (!_rebuilding) { setter(cb.SelectedItem?.ToString() ?? ""); ValueChanged?.Invoke(); } };
        _table.Controls.Add(cb, 1, _currentRow);
        _controls[key] = cb;
        _table.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        _currentRow++;
    }

    private void AddBrowseButton(string key, string label, string value, Action<string> setter, string filter, bool isImage = true)
    {
        AddLabel(label);
        var panel = new Panel { Dock = DockStyle.Fill, Height = 26 };

        var btn = new Button
        {
            Text = "...",
            Width = 26,
            Height = 22,
            Dock = DockStyle.Right,
            FlatStyle = FlatStyle.Flat,
            BackColor = AppTheme.SurfaceRaised,
            ForeColor = AppTheme.Text
        };

        var tb = new TextBox
        {
            Text = value,
            Dock = DockStyle.Fill,
            BackColor = AppTheme.SurfaceRaised,
            ForeColor = AppTheme.Text,
            BorderStyle = BorderStyle.FixedSingle
        };

        btn.Click += (s, e) =>
        {
            using var dlg = new OpenFileDialog { Filter = filter };
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                tb.Text = dlg.FileName;
            }
        };

        tb.TextChanged += (s, e) => { if (!_rebuilding) { setter(tb.Text); ValueChanged?.Invoke(); } };

        // Button must be added first so Dock.Right takes priority over Dock.Fill
        panel.Controls.Add(btn);
        panel.Controls.Add(tb);

        _table.Controls.Add(panel, 1, _currentRow);
        _controls[key] = tb;
        _table.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        _currentRow++;
    }

    private void AddRegionEditor(string key, string label, Engine.Region value)
    {
        AddLabel(label);
        var panel = new Panel { Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right, Height = 54 };
        var nudX = new NumericUpDown { Minimum = 0, Maximum = 99999, Value = value.X, Width = 55, Location = new Point(0, 2), BackColor = Color.FromArgb(50, 50, 55), ForeColor = Color.White };
        var nudY = new NumericUpDown { Minimum = 0, Maximum = 99999, Value = value.Y, Width = 55, Location = new Point(60, 2), BackColor = Color.FromArgb(50, 50, 55), ForeColor = Color.White };
        var nudW = new NumericUpDown { Minimum = 0, Maximum = 99999, Value = value.Width, Width = 55, Location = new Point(0, 28), BackColor = Color.FromArgb(50, 50, 55), ForeColor = Color.White };
        var nudH = new NumericUpDown { Minimum = 0, Maximum = 99999, Value = value.Height, Width = 55, Location = new Point(60, 28), BackColor = Color.FromArgb(50, 50, 55), ForeColor = Color.White };
        panel.Controls.AddRange([nudX, nudY, nudW, nudH]);
        nudX.ValueChanged += (s, e) => { value.X = (int)nudX.Value; ValueChanged?.Invoke(); };
        nudY.ValueChanged += (s, e) => { value.Y = (int)nudY.Value; ValueChanged?.Invoke(); };
        nudW.ValueChanged += (s, e) => { value.Width = (int)nudW.Value; ValueChanged?.Invoke(); };
        nudH.ValueChanged += (s, e) => { value.Height = (int)nudH.Value; ValueChanged?.Invoke(); };
        _table.Controls.Add(panel, 1, _currentRow);
        _controls[key] = panel;
        _table.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        _currentRow++;
    }

    private void AddLabel(string text)
    {
        var label = new Label
        {
            Text = text,
            ForeColor = AppTheme.TextMuted,
            AutoSize = false,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 4, 4, 0),
            MaximumSize = new Size(110, 22)
        };
        _table.Controls.Add(label, 0, _currentRow);
    }

    /// <summary>
    /// A hint / instruction label that spans both columns (no paired input control).
    /// </summary>
    private void AddHintLabel(string text)
    {
        var label = new Label
        {
            Text = text,
            Font = new Font("Segoe UI", 8, FontStyle.Italic),
            ForeColor = AppTheme.TextMuted,
            AutoSize = false,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
            Height = 20
        };
        _table.Controls.Add(label, 0, _currentRow);
        _table.SetColumnSpan(label, 2);
        _table.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        _currentRow++;
    }

    private void AddLabelHeader(string text)
    {
        var label = new Label
        {
            Text = text,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            ForeColor = AppTheme.Accent,
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 0)
        };
        _table.Controls.Add(label, 0, _currentRow);
        _table.SetColumnSpan(label, 2);
        _table.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        _currentRow++;
    }

    private void AddRgbField(string key, string label, FlowNode node, System.Drawing.Color defaultColor)
    {
        // Read from the plain string that survives JSON round-trip
        var stored = node.GetParam<string>(key);
        System.Drawing.Color color;
        if (!string.IsNullOrEmpty(stored))
        {
            var parts = stored.Split(',');
            color = parts.Length == 3 && int.TryParse(parts[0], out int r) && int.TryParse(parts[1], out int g) && int.TryParse(parts[2], out int b)
                ? System.Drawing.Color.FromArgb(r, g, b)
                : defaultColor;
        }
        else
        {
            color = defaultColor;
        }

        AddLabel(label);
        var panel = new Panel { Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right, Height = 54 };

        var nudR = new NumericUpDown { Minimum = 0, Maximum = 255, Value = color.R, Width = 52, Location = new Point(0, 2), BackColor = Color.FromArgb(50, 50, 55), ForeColor = Color.White };
        var nudG = new NumericUpDown { Minimum = 0, Maximum = 255, Value = color.G, Width = 52, Location = new Point(58, 2), BackColor = Color.FromArgb(50, 50, 55), ForeColor = Color.White };
        var nudB = new NumericUpDown { Minimum = 0, Maximum = 255, Value = color.B, Width = 52, Location = new Point(116, 2), BackColor = Color.FromArgb(50, 50, 55), ForeColor = Color.White };

        var preview = new Panel
        {
            Width = 24, Height = 24,
            Location = new Point(176, 2),
            BackColor = color,
            BorderStyle = BorderStyle.FixedSingle
        };

        Action updateColor = () =>
        {
            var c = System.Drawing.Color.FromArgb((int)nudR.Value, (int)nudG.Value, (int)nudB.Value);
            preview.BackColor = c;
            // Store as simple string (Color objects don't survive JSON round-trip)
            node.SetParam(key, $"{c.R},{c.G},{c.B}");
            ValueChanged?.Invoke();
        };

        nudR.ValueChanged += (s, e) => updateColor();
        nudG.ValueChanged += (s, e) => updateColor();
        nudB.ValueChanged += (s, e) => updateColor();

        panel.Controls.AddRange([nudR, nudG, nudB, preview]);

        _table.Controls.Add(panel, 1, _currentRow);
        _controls[key] = panel;
        _table.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        _currentRow++;
    }

    private void AddSeparator()
    {
        var sep = new Label
        {
            Text = "",
            Height = 1,
            BackColor = AppTheme.Border,
            Dock = DockStyle.Top
        };
        _table.Controls.Add(sep, 0, _currentRow);
        _table.SetColumnSpan(sep, 2);
        _table.RowStyles.Add(new RowStyle(SizeType.Absolute, 8));
        _currentRow++;
    }

    // ============ Node-specific parameter panels ============

    private void ShowStartProgramParams(FlowNode node)
    {
        AddBrowseButton("FilePath", "File Path", node.GetParam<string>("FilePath") ?? "", v => node.SetParam("FilePath", v), "Executables|*.exe|All Files|*.*");
        AddBrowseButton("WorkingDirectory", "Work Dir", node.GetParam<string>("WorkingDirectory") ?? "", v => node.SetParam("WorkingDirectory", v), "All Files|*.*");
        AddTextField("Arguments", "Arguments", node.GetParam<string>("Arguments") ?? "", v => node.SetParam("Arguments", v));
        AddCheckBox("RunAsAdmin", "Run as Admin", node.GetParam<bool?>("RunAsAdmin") ?? false, v => node.SetParam("RunAsAdmin", v));
        AddNumericField("WaitForWindowMs", "Wait Window (ms)", node.GetParam<int?>("WaitForWindowMs") ?? 5000, v => node.SetParam("WaitForWindowMs", v));
        AddTextField("WindowTitleKeyword", "Window Keyword", node.GetParam<string>("WindowTitleKeyword") ?? "", v => node.SetParam("WindowTitleKeyword", v));
    }

    private void ShowClickElementParams(FlowNode node)
    {
        var region = node.GetParam<Engine.Region>("Region") ?? new Engine.Region();
        node.SetParam("Region", region);

        AddTextField("TargetWindow", "Target Window", node.GetParam<string>("TargetWindow") ?? "", v => node.SetParam("TargetWindow", v));

        var locateMode = node.GetParam<string>("LocateMode") ?? "Coordinate";
        AddComboBox("LocateMode", "Locate Mode", locateMode,
            ["Coordinate", "TemplateMatch", "OCR", "HSVClick", "HSVTemplateMatch"], v =>
            {
                if (_rebuilding) return;
                node.SetParam("LocateMode", v);
                _rebuilding = true;
                ShowNode(node);
                _rebuilding = false;
            });

        // Full Window toggle — relevant for TemplateMatch, OCR, and HSV modes
        var mode = node.GetParam<string>("LocateMode") ?? "Coordinate";
        bool isHsvMode = mode == "HSVClick" || mode == "HSVTemplateMatch";
        if (mode == "TemplateMatch" || mode == "OCR" || isHsvMode)
        {
            var useFullWindow = node.GetParam<bool?>("UseFullScreen") ?? true;
            AddCheckBox("UseFullScreen", "Full Window", useFullWindow, v =>
            {
                node.SetParam("UseFullScreen", v);
                _rebuilding = true;
                ShowNode(node);
                _rebuilding = false;
            });

            if (!useFullWindow)
            {
                AddRegionEditor("Region", "Region (X,Y,W,H)\n(relative to window)", region);
            }
        }
        else
        {
            // Coordinate mode always shows Region editor
            AddRegionEditor("Region", "Region (X,Y,W,H)\n(relative to window)", region);
        }

        if (mode == "TemplateMatch")
        {
            AddBrowseButton("TemplateImagePath", "Template Image", node.GetParam<string>("TemplateImagePath") ?? "", v => node.SetParam("TemplateImagePath", v), "PNG Files|*.png|All Files|*.*");
            AddDoubleField("TemplateMatchThreshold", "Threshold", node.GetParam<double?>("TemplateMatchThreshold") ?? 0.8, v => node.SetParam("TemplateMatchThreshold", v));
        }
        else if (mode == "OCR")
        {
            AddTextField("OCRText", "OCR Text", node.GetParam<string>("OCRText") ?? "", v => node.SetParam("OCRText", v));
        }
        else if (isHsvMode)
        {
            AddSeparator();
            AddLabelHeader("HSV Color Filter");
            AddRgbField("TargetRgb", "Target Color (R,G,B)", node, System.Drawing.Color.FromArgb(49, 218, 183));
            AddNumericField("HueTolerance", "Hue Tolerance", node.GetParam<int?>("HueTolerance") ?? 8, v => node.SetParam("HueTolerance", v));
            AddNumericField("SVTolerance", "SV Tolerance", node.GetParam<int?>("SVTolerance") ?? 30, v => node.SetParam("SVTolerance", v));

            if (mode == "HSVTemplateMatch")
            {
                AddBrowseButton("ReferenceImagePath", "Reference Image", node.GetParam<string>("ReferenceImagePath") ?? "", v => node.SetParam("ReferenceImagePath", v), "PNG Files|*.png|All Files|*.*");
                AddDoubleField("TemplateMatchThreshold", "Tpl. Threshold", node.GetParam<double?>("TemplateMatchThreshold") ?? 0.8, v => node.SetParam("TemplateMatchThreshold", v));
            }
        }
        // Coordinate: no extra fields

        AddNumericField("PreDelayMs", "Pre-delay (ms)", node.GetParam<int?>("PreDelayMs") ?? 500, v => node.SetParam("PreDelayMs", v));
        AddNumericField("PostDelayMs", "Post-delay (ms)", node.GetParam<int?>("PostDelayMs") ?? 500, v => node.SetParam("PostDelayMs", v));
    }

    private void ShowWaitConditionParams(FlowNode node)
    {
        var region = node.GetParam<Engine.Region>("Region") ?? new Engine.Region();
        node.SetParam("Region", region);
        var conditionType = node.GetParam<string>("ConditionType") ?? "ImageAppear";

        AddTextField("TargetWindow", "Target Window", node.GetParam<string>("TargetWindow") ?? "", v => node.SetParam("TargetWindow", v));
        AddComboBox("ConditionType", "Condition", conditionType,
            ["ImageAppear", "ImageDisappear", "OCRContain", "WindowExist", "Timeout"], v =>
            {
                if (_rebuilding) return;
                node.SetParam("ConditionType", v);
                _rebuilding = true;
                ShowNode(node);
                _rebuilding = false;
            });

        // Full Window toggle — only for image/OCR-based conditions
        if (conditionType == "ImageAppear" || conditionType == "ImageDisappear" || conditionType == "OCRContain")
        {
            var useFullWindow = node.GetParam<bool?>("UseFullScreen") ?? true;
            AddCheckBox("UseFullScreen", "Full Window", useFullWindow, v =>
            {
                node.SetParam("UseFullScreen", v);
                _rebuilding = true;
                ShowNode(node);
                _rebuilding = false;
            });

            if (!useFullWindow)
            {
                AddRegionEditor("Region", "Region (X,Y,W,H)", region);
            }
        }

        if (conditionType == "ImageAppear" || conditionType == "ImageDisappear")
        {
            AddBrowseButton("TemplateImagePath", "Template Image", node.GetParam<string>("TemplateImagePath") ?? "", v => node.SetParam("TemplateImagePath", v), "PNG Files|*.png|All Files|*.*");
            AddDoubleField("TemplateMatchThreshold", "Threshold", node.GetParam<double?>("TemplateMatchThreshold") ?? 0.8, v => node.SetParam("TemplateMatchThreshold", v));
        }
        else if (conditionType == "OCRContain")
        {
            AddTextField("OCRText", "OCR Text", node.GetParam<string>("OCRText") ?? "", v => node.SetParam("OCRText", v));
        }
        else if (conditionType == "Timeout")
        {
            var waitMs = node.GetParam<int?>("WaitMs") ?? node.TimeoutMs;
            AddNumericField("WaitMs", "Wait Duration (ms)", waitMs, v => node.SetParam("WaitMs", v));
        }
        // WindowExist needs no extra fields

        AddNumericField("CheckIntervalMs", "Check Interval (ms)", node.GetParam<int?>("CheckIntervalMs") ?? 500, v => node.SetParam("CheckIntervalMs", v));
    }

    private void ShowKeyPressParams(FlowNode node)
    {
        AddTextField("TargetWindow", "Target Window", node.GetParam<string>("TargetWindow") ?? "", v => node.SetParam("TargetWindow", v));
        AddTextField("KeyName", "Key Name", node.GetParam<string>("KeyName") ?? "", v => node.SetParam("KeyName", v));
        AddNumericField("KeyScanCode", "Scan Code", (int)(node.GetParam<byte?>("KeyScanCode") ?? 0), v => node.SetParam("KeyScanCode", (byte)v));
        AddComboBox("PressMode", "Press Mode", node.GetParam<string>("PressMode") ?? "Press",
            ["Press", "Hold", "Release"], v => node.SetParam("PressMode", v));
        AddNumericField("HoldDurationMs", "Hold Duration (ms)", node.GetParam<int?>("HoldDurationMs") ?? 500, v => node.SetParam("HoldDurationMs", v));
    }

    private void ShowLoopParams(FlowNode node)
    {
        var loopMode = node.GetParam<string>("LoopMode") ?? "FixedCount";
        AddComboBox("LoopMode", "Loop Mode", loopMode,
            ["FixedCount", "BreakCondition"], v =>
            {
                if (_rebuilding) return;
                node.SetParam("LoopMode", v);
                _rebuilding = true;
                ShowNode(node);
                _rebuilding = false;
            });

        if (loopMode == "FixedCount")
        {
            AddNumericField("LoopCount", "Loop Count (0=infinite)", node.GetParam<int?>("LoopCount") ?? 3, v => node.SetParam("LoopCount", v));
        }

        var completeCount = node.TrueBranch?.Count ?? 0;
        var breakCount = node.FalseBranch?.Count ?? 0;
        AddSeparator();
        AddHintLabel($"Complete: {completeCount} node(s) | Break: {breakCount} node(s)");
        if (loopMode == "BreakCondition")
            AddHintLabel("Left white port receives break signal.");
        else
            AddHintLabel("Body nodes connect in a cycle.");
    }

    private void ShowLoopEndParams(FlowNode node)
    {
        AddHintLabel("⟐ Loop End — Marks the end of the loop body.");
        AddHintLabel("Connect the last body node's output here.");

        if (!string.IsNullOrEmpty(node.PairedLoopStartId))
            AddHintLabel($"Paired with LoopStart: {node.PairedLoopStartId[..Math.Min(8, node.PairedLoopStartId.Length)]}...");
        else
            AddHintLabel("Paired LoopStart: (auto-detected at runtime)");

        AddHintLabel("After loop exits, execution continues from this node's output.");
    }

    private void ShowConditionParams(FlowNode node)
    {
        var region = node.GetParam<Engine.Region>("Region") ?? new Engine.Region();
        node.SetParam("Region", region);

        var conditionType = node.GetParam<string>("ConditionType") ?? "ImageAppear";
        AddComboBox("ConditionType", "Condition", conditionType,
            ["ImageAppear", "OCRContain"], v =>
            {
                if (_rebuilding) return;
                node.SetParam("ConditionType", v);
                _rebuilding = true;
                ShowNode(node);
                _rebuilding = false;
            });

        // Full Window toggle
        var useFullWindow = node.GetParam<bool?>("UseFullScreen") ?? true;
        AddCheckBox("UseFullScreen", "Full Window", useFullWindow, v =>
        {
            node.SetParam("UseFullScreen", v);
            _rebuilding = true;
            ShowNode(node);
            _rebuilding = false;
        });

        if (!useFullWindow)
        {
            AddRegionEditor("Region", "Region (X,Y,W,H)\n(relative to window)", region);
        }

        if (conditionType == "ImageAppear")
        {
            AddBrowseButton("TemplateImagePath", "Template Image", node.GetParam<string>("TemplateImagePath") ?? "", v => node.SetParam("TemplateImagePath", v), "PNG Files|*.png|All Files|*.*");
            AddDoubleField("TemplateMatchThreshold", "Threshold", node.GetParam<double?>("TemplateMatchThreshold") ?? 0.8, v => node.SetParam("TemplateMatchThreshold", v));
        }
        else if (conditionType == "OCRContain")
        {
            AddTextField("OCRText", "OCR Text", node.GetParam<string>("OCRText") ?? "", v => node.SetParam("OCRText", v));
        }

        // True/False branch info
        var trueCount = node.TrueBranch?.Count ?? 0;
        var falseCount = node.FalseBranch?.Count ?? 0;
        AddSeparator();
        AddHintLabel($"True branch: {trueCount} node(s)");
        AddHintLabel($"False branch: {falseCount} node(s)");
    }

    private void ShowGateParams(FlowNode node)
    {
        var logicType = node.GetParam<string>("GateLogicType") ?? "AND";
        AddComboBox("GateLogicType", "Logic Type", logicType,
            ["AND", "OR", "NOT"], v =>
        {
            if (_rebuilding) return;
            node.SetParam("GateLogicType", v);
            _rebuilding = true;
            ShowNode(node);
            _rebuilding = false;
        });

        AddSeparator();
        AddHintLabel("Two input ports (0 and 1), single Result output.");
        AddHintLabel("AND: both true → true");
        AddHintLabel("OR: any true → true");
        AddHintLabel("NOT: invert Input0");
    }

    // ============ ColorMotion Node ============

    private void ShowColorMotionParams(FlowNode node)
    {
        var region = node.GetParam<Engine.Region>("Region") ?? new Engine.Region();
        node.SetParam("Region", region);

        var motionMode = node.GetParam<string>("MotionMode") ?? "MotionDetect";
        AddComboBox("MotionMode", "Mode", motionMode,
            ["MotionDetect", "StateChange", "DirectionDetect", "ColorDetect"], v =>
            {
                if (_rebuilding) return;
                node.SetParam("MotionMode", v);
                _rebuilding = true;
                ShowNode(node);
                _rebuilding = false;
            });

        // Common: Window/Region
        AddTextField("TargetWindow", "Target Window", node.GetParam<string>("TargetWindow") ?? "", v => node.SetParam("TargetWindow", v));
        var useFullWindow = node.GetParam<bool?>("UseFullScreen") ?? true;
        AddCheckBox("UseFullScreen", "Full Window", useFullWindow, v =>
        {
            node.SetParam("UseFullScreen", v);
            _rebuilding = true;
            ShowNode(node);
            _rebuilding = false;
        });
        if (!useFullWindow)
        {
            AddRegionEditor("Region", "Region (X,Y,W,H)", region);
        }

        // HSV Color Filter
        AddSeparator();
        AddLabelHeader("HSV Color Filter");
        AddRgbField("TargetRgb", "Target Color (R,G,B)", node, System.Drawing.Color.FromArgb(49, 218, 183));
        AddNumericField("HueTolerance", "Hue Tolerance", node.GetParam<int?>("HueTolerance") ?? 8, v => node.SetParam("HueTolerance", v));
        AddNumericField("SVTolerance", "SV Tolerance", node.GetParam<int?>("SVTolerance") ?? 30, v => node.SetParam("SVTolerance", v));

        if (motionMode == "MotionDetect")
        {
            AddLabelHeader("Motion Detection");
            AddNumericField("MoveCheckIntervalMs", "Check Interval (ms)", node.GetParam<int?>("MoveCheckIntervalMs") ?? 30, v => node.SetParam("MoveCheckIntervalMs", v));
            AddNumericField("MoveDurationMs", "Duration (ms)", node.GetParam<int?>("MoveDurationMs") ?? 10000, v => node.SetParam("MoveDurationMs", v));
            AddNumericField("MoveThresholdPx", "Move Threshold (px)", node.GetParam<int?>("MoveThresholdPx") ?? 5, v => node.SetParam("MoveThresholdPx", v));
        }
        else if (motionMode == "StateChange")
        {
            AddLabelHeader("State Change Detection");
            AddNumericField("StateCheckIntervalMs", "Check Interval (ms)", node.GetParam<int?>("StateCheckIntervalMs") ?? 100, v => node.SetParam("StateCheckIntervalMs", v));
            AddNumericField("StateDurationMs", "Duration (ms)", node.GetParam<int?>("StateDurationMs") ?? 30000, v => node.SetParam("StateDurationMs", v));
            AddDoubleField("ColorChangeThreshold", "Change Threshold", node.GetParam<double?>("ColorChangeThreshold") ?? 0.15, v => node.SetParam("ColorChangeThreshold", v));
        }
        else if (motionMode == "DirectionDetect")
        {
            AddLabelHeader("Direction Detection");

            var trackMode = node.GetParam<string>("TrackMode") ?? "TemplateMatch";
            AddComboBox("TrackMode", "Track Mode", trackMode,
                ["TemplateMatch", "ColorTrack"], v =>
                {
                    if (_rebuilding) return;
                    node.SetParam("TrackMode", v);
                    _rebuilding = true;
                    ShowNode(node);
                    _rebuilding = false;
                });

            if (trackMode == "TemplateMatch")
            {
                AddHintLabel("📷 Snip: captured image is HSV-filtered — only the target color shape remains.");
                AddBrowseButton("ReferenceImagePath", "Reference Image", node.GetParam<string>("ReferenceImagePath") ?? "", v => node.SetParam("ReferenceImagePath", v), "PNG Files|*.png|All Files|*.*");
                AddDoubleField("TemplateMatchThreshold", "Tpl. Threshold", node.GetParam<double?>("TemplateMatchThreshold") ?? 0.8, v => node.SetParam("TemplateMatchThreshold", v));
            }
            else
            {
                AddHintLabel("🎯 ColorTrack: pure HSV center tracking — no reference image needed.");
            }

            AddNumericField("MoveCheckIntervalMs", "Check Interval (ms)", node.GetParam<int?>("MoveCheckIntervalMs") ?? 30, v => node.SetParam("MoveCheckIntervalMs", v));
            AddNumericField("MoveDurationMs", "Duration (ms)", node.GetParam<int?>("MoveDurationMs") ?? 10000, v => node.SetParam("MoveDurationMs", v));
        }
        else if (motionMode == "ColorDetect")
        {
            AddLabelHeader("Color Detection");
            AddHintLabel("🎯 Detect if target color is present in region.");
            AddNumericField("MoveCheckIntervalMs", "Check Interval (ms)", node.GetParam<int?>("MoveCheckIntervalMs") ?? 100, v => node.SetParam("MoveCheckIntervalMs", v));
            AddNumericField("MoveDurationMs", "Duration (ms)", node.GetParam<int?>("MoveDurationMs") ?? 10000, v => node.SetParam("MoveDurationMs", v));
        }
    }

    // ============ ColorCal Node ============

    private void ShowColorCalParams(FlowNode node)
    {
        // Load or init detection targets
        var targets = node.GetParam<List<ColorCalTarget>>("DetectionTargets") ?? new List<ColorCalTarget>();
        if (targets.Count == 0)
        {
            targets.Add(new ColorCalTarget { Name = "Target1" });
            node.SetParam("DetectionTargets", targets);
        }

        // === Add/Remove target buttons at TOP ===
        var addBtn = new Button
        {
            Text = "+ Add Target",
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(60, 60, 65),
            ForeColor = Color.White,
            Tag = node.NodeId
        };
        addBtn.Click += OnColorCalAddTarget;
        var removeBtn = new Button
        {
            Text = "- Remove Last",
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(60, 60, 65),
            ForeColor = Color.White,
            Tag = node.NodeId
        };
        removeBtn.Click += OnColorCalRemoveTarget;

        _table.Controls.Add(addBtn, 0, _currentRow);
        _table.Controls.Add(removeBtn, 1, _currentRow);
        _table.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        _currentRow++;

        AddSeparator();
        AddLabelHeader("Detection Targets");
        AddHintLabel($"Count: {targets.Count}");

        for (int i = 0; i < targets.Count; i++)
        {
            AddTargetEditor(node, i);
        }

        AddSeparator();

        // === Expression ===
        AddLabelHeader("Expression (must return int)");
        var expr = node.GetParam<string>("Expression") ?? "0";
        AddTextField("Expression", "Expression", expr, v => node.SetParam("Expression", v));
        AddHintLabel("Use {Name}.X, .Y, .Found — see Help for details.");

        AddSeparator();

        // === Successor Count ===
        AddLabelHeader("Branch Routing");
        var successorCount = node.GetParam<int?>("SuccessorCount") ?? 1;
        AddNumericField("SuccessorCount", "Output Count", successorCount, v =>
        {
            node.SetParam("SuccessorCount", v);
            if (!_rebuilding)
            {
                _rebuilding = true;
                ShowNode(node);
                _rebuilding = false;
            }
        });
        AddHintLabel("Result 0 → Port 0, 1 → Port 1, etc.");
    }

    private void OnColorCalAddTarget(object? sender, EventArgs e)
    {
        if (_currentNode == null) return;
        var targets = _currentNode.GetParam<List<ColorCalTarget>>("DetectionTargets") ?? new List<ColorCalTarget>();
        targets.Add(new ColorCalTarget { Name = $"Target{targets.Count + 1}" });
        _currentNode.SetParam("DetectionTargets", targets);
        _rebuilding = true;
        ShowNode(_currentNode);
        _rebuilding = false;
    }

    private void OnColorCalRemoveTarget(object? sender, EventArgs e)
    {
        if (_currentNode == null) return;
        var targets = _currentNode.GetParam<List<ColorCalTarget>>("DetectionTargets") ?? new List<ColorCalTarget>();
        if (targets.Count > 1)
        {
            targets.RemoveAt(targets.Count - 1);
            _currentNode.SetParam("DetectionTargets", targets);
            _rebuilding = true;
            ShowNode(_currentNode);
            _rebuilding = false;
        }
    }

    private void AddTargetEditor(FlowNode node, int index)
    {
        var targets = node.GetParam<List<ColorCalTarget>>("DetectionTargets") ?? new List<ColorCalTarget>();
        if (index >= targets.Count) return;
        var t = targets[index];

        AddSeparator();
        var header = new Label
        {
            Text = $"--- Target: {t.Name} ---",
            Font = new Font("Segoe UI", 8, FontStyle.Bold),
            ForeColor = Color.FromArgb(200, 180, 100),
            AutoSize = true
        };
        _table.Controls.Add(header, 0, _currentRow);
        _table.SetColumnSpan(header, 2);
        _table.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        _currentRow++;

        // Name
        AddTextField($"TargetName_{index}", "Name", t.Name, v =>
        {
            t.Name = v;
            node.SetParam("DetectionTargets", targets);
        });

        // Window
        AddTextField($"TargetWindow_{index}", "Window", t.TargetWindow, v =>
        {
            t.TargetWindow = v;
            node.SetParam("DetectionTargets", targets);
        });

        // Fullscreen toggle
        AddCheckBox($"UseFullScreen_{index}", "Full Window", t.UseFullScreen, v =>
        {
            t.UseFullScreen = v;
            node.SetParam("DetectionTargets", node.GetParam<List<ColorCalTarget>>("DetectionTargets"));
            if (!_rebuilding)
            {
                _rebuilding = true;
                ShowNode(node);
                _rebuilding = false;
            }
        });

        if (!t.UseFullScreen)
        {
            AddRegionEditorInline($"Region_{index}", "Region (X,Y,W,H)", t.Region);
        }

        // Track Mode
        AddComboBoxInline($"TrackMode_{index}", "Track Mode", t.TrackMode,
            ["TemplateMatch", "ColorTrack"], index, v =>
        {
            t.TrackMode = v;
            node.SetParam("DetectionTargets", targets);
            if (!_rebuilding)
            {
                _rebuilding = true;
                ShowNode(node);
                _rebuilding = false;
            }
        });

        // HSV Color - use Tag to avoid closure capture
        AddRgbFieldInline($"TargetRgb_{index}", "Color (R,G,B)", index, t.GetRgbColor());

        // Tolerance
        AddNumericField($"HueTolerance_{index}", "Hue Tol", t.HueTolerance, v =>
        {
            t.HueTolerance = v;
            node.SetParam("DetectionTargets", targets);
        });
        AddNumericField($"SVTolerance_{index}", "SV Tol", t.SVTolerance, v =>
        {
            t.SVTolerance = v;
            node.SetParam("DetectionTargets", targets);
        });

        if (t.TrackMode == "TemplateMatch")
        {
            // Template
            AddBrowseButton($"TemplateImagePath_{index}", "Template", t.TemplateImagePath, v =>
            {
                t.TemplateImagePath = v;
                node.SetParam("DetectionTargets", targets);
            }, "PNG Files|*.png|All Files|*.*");
            AddDoubleField($"TemplateMatchThreshold_{index}", "Tpl Threshold", t.TemplateMatchThreshold, v =>
            {
                t.TemplateMatchThreshold = v;
                node.SetParam("DetectionTargets", targets);
            });
        }
        else
        {
            AddHintLabel("🎯 ColorTrack: pure HSV center detection — no template needed.");
        }
    }

    private void AddRegionEditorInline(string key, string label, Engine.Region region)
    {
        AddLabel(label);
        var panel = new Panel { Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right, Height = 54 };
        var nudX = new NumericUpDown { Minimum = 0, Maximum = 99999, Value = region.X, Width = 55, Location = new Point(0, 2), BackColor = Color.FromArgb(50, 50, 55), ForeColor = Color.White };
        var nudY = new NumericUpDown { Minimum = 0, Maximum = 99999, Value = region.Y, Width = 55, Location = new Point(60, 2), BackColor = Color.FromArgb(50, 50, 55), ForeColor = Color.White };
        var nudW = new NumericUpDown { Minimum = 0, Maximum = 99999, Value = region.Width, Width = 55, Location = new Point(0, 28), BackColor = Color.FromArgb(50, 50, 55), ForeColor = Color.White };
        var nudH = new NumericUpDown { Minimum = 0, Maximum = 99999, Value = region.Height, Width = 55, Location = new Point(60, 28), BackColor = Color.FromArgb(50, 50, 55), ForeColor = Color.White };
        panel.Controls.AddRange([nudX, nudY, nudW, nudH]);
        nudX.ValueChanged += (s, e) => { region.X = (int)nudX.Value; };
        nudY.ValueChanged += (s, e) => { region.Y = (int)nudY.Value; };
        nudW.ValueChanged += (s, e) => { region.Width = (int)nudW.Value; };
        nudH.ValueChanged += (s, e) => { region.Height = (int)nudH.Value; };
        _table.Controls.Add(panel, 1, _currentRow);
        _controls[key] = panel;
        _table.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        _currentRow++;
    }

    private void AddRgbFieldInline(string key, string label, int targetIndex, System.Drawing.Color defaultColor)
    {
        AddLabel(label);
        var panel = new Panel { Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right, Height = 54 };

        var nudR = new NumericUpDown { Minimum = 0, Maximum = 255, Value = defaultColor.R, Width = 52, Location = new Point(0, 2), BackColor = Color.FromArgb(50, 50, 55), ForeColor = Color.White };
        var nudG = new NumericUpDown { Minimum = 0, Maximum = 255, Value = defaultColor.G, Width = 52, Location = new Point(58, 2), BackColor = Color.FromArgb(50, 50, 55), ForeColor = Color.White };
        var nudB = new NumericUpDown { Minimum = 0, Maximum = 255, Value = defaultColor.B, Width = 52, Location = new Point(116, 2), BackColor = Color.FromArgb(50, 50, 55), ForeColor = Color.White };

        var preview = new Panel
        {
            Width = 24, Height = 24,
            Location = new Point(176, 2),
            BackColor = defaultColor,
            BorderStyle = BorderStyle.FixedSingle
        };

        // Store references in Tag to avoid closure capture of panel/control
        panel.Tag = (nudR, nudG, nudB, preview, targetIndex);
        nudR.ValueChanged += OnRgbChanged;
        nudG.ValueChanged += OnRgbChanged;
        nudB.ValueChanged += OnRgbChanged;

        panel.Controls.AddRange([nudR, nudG, nudB, preview]);

        _table.Controls.Add(panel, 1, _currentRow);
        _controls[key] = panel;
        _table.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        _currentRow++;
    }

    private void OnRgbChanged(object? sender, EventArgs e)
    {
        if (sender is not NumericUpDown nud || nud.Parent?.Tag is not (NumericUpDown r, NumericUpDown g, NumericUpDown b, Panel preview, int targetIndex)) return;
        if (_currentNode == null) return;
        var targets = _currentNode.GetParam<List<ColorCalTarget>>("DetectionTargets") ?? new List<ColorCalTarget>();
        if (targetIndex >= targets.Count) return;
        var c = System.Drawing.Color.FromArgb((int)r.Value, (int)g.Value, (int)b.Value);
        preview.BackColor = c;
        targets[targetIndex].SetRgbColor(c);
        _currentNode.SetParam("DetectionTargets", targets);
    }

    private void AddComboBoxInline(string key, string label, string value, string[] items, int targetIndex, Action<string> setter)
    {
        AddLabel(label);
        var cb = new ComboBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(50, 50, 55),
            ForeColor = Color.White,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            Tag = targetIndex
        };
        cb.Items.AddRange(items);
        cb.SelectedItem = value;
        cb.SelectedIndexChanged += (s, e) =>
        {
            if (_rebuilding) return;
            setter(cb.SelectedItem?.ToString() ?? "");
        };
        _table.Controls.Add(cb, 1, _currentRow);
        _controls[key] = cb;
        _table.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        _currentRow++;
    }

    // ============ Break Node ============

    private void ShowBreakParams(FlowNode node)
    {
        AddLabelHeader("Break Node");
        AddLabel("Auto-generated with Loop node.");
        AddLabel("Interrupts enclosing loop when triggered.");
    }
}
