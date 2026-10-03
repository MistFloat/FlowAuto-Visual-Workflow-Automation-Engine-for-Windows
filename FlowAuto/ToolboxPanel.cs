using FlowAuto.Models;

namespace FlowAuto;

public class ToolboxPanel : Panel
{
    private readonly FlowLayoutPanel _itemsPanel;
    private readonly TextBox _searchBox;
    private readonly ToolTip _toolTip = new();

    private static readonly (string Name, string Description, NodeType Type, Color Color)[] Items =
    [
        ("Start Program", "Launch an application and find its window", NodeType.StartProgram, Color.FromArgb(80, 145, 255)),
        ("Click Element", "Click by coordinate, image, text or color", NodeType.ClickElement, Color.FromArgb(42, 190, 130)),
        ("Wait Condition", "Wait until a visual condition is met", NodeType.WaitCondition, Color.FromArgb(245, 174, 65)),
        ("Key Press", "Send a keyboard action", NodeType.KeyPress, Color.FromArgb(170, 105, 255)),
        ("Loop Start", "Repeat a connected block", NodeType.Loop, Color.FromArgb(255, 137, 76)),
        ("Loop End", "Mark the end of a loop", NodeType.LoopEnd, Color.FromArgb(255, 137, 76)),
        ("Condition", "Route execution using a condition", NodeType.Condition, Color.FromArgb(239, 83, 105)),
        ("Gate", "Merge multiple execution paths", NodeType.Gate, Color.FromArgb(38, 190, 210)),
        ("Break", "Exit the currently active loop", NodeType.Break, Color.FromArgb(239, 105, 83)),
        ("ColorCal", "Calculate a branch from color targets", NodeType.ColorCal, Color.FromArgb(184, 104, 220)),
        ("ColorMotion", "Detect color, movement or direction", NodeType.ColorMotion, Color.FromArgb(38, 180, 165))
    ];

    public event Action<NodeType>? ToolActivated;

    public ToolboxPanel()
    {
        Dock = DockStyle.Fill;
        BackColor = AppTheme.Surface;
        Padding = new Padding(12, 12, 8, 8);

        var title = new Label
        {
            Text = "NODES",
            Font = new Font("Segoe UI Semibold", 10),
            ForeColor = AppTheme.Text,
            Dock = DockStyle.Top,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft
        };

        _searchBox = new TextBox
        {
            PlaceholderText = "Search nodes...",
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = AppTheme.SurfaceRaised,
            ForeColor = AppTheme.Text,
            Font = new Font("Segoe UI", 9),
            Dock = DockStyle.Top,
            Height = 30,
            Margin = new Padding(0, 0, 0, 10)
        };
        _searchBox.TextChanged += (_, _) => PopulateItems(_searchBox.Text);

        _itemsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = AppTheme.Surface,
            Padding = new Padding(0, 10, 0, 0)
        };
        _itemsPanel.SizeChanged += (_, _) =>
        {
            var width = Math.Max(150, _itemsPanel.ClientSize.Width - 8);
            foreach (Control control in _itemsPanel.Controls) control.Width = width;
        };

        Controls.Add(_itemsPanel);
        Controls.Add(_searchBox);
        Controls.Add(title);
        PopulateItems("");
    }

    private void PopulateItems(string filter)
    {
        _itemsPanel.SuspendLayout();
        _itemsPanel.Controls.Clear();
        foreach (var item in Items.Where(item =>
                     item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                     item.Description.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            _itemsPanel.Controls.Add(CreateItem(item));
        }
        _itemsPanel.ResumeLayout();
    }

    private Control CreateItem((string Name, string Description, NodeType Type, Color Color) item)
    {
        var card = new Panel
        {
            Width = Math.Max(150, _itemsPanel.ClientSize.Width - 8),
            Height = 52,
            Margin = new Padding(0, 0, 0, 7),
            BackColor = AppTheme.SurfaceRaised,
            Cursor = Cursors.Hand,
            Tag = item.Type
        };
        var accent = new Panel { Dock = DockStyle.Left, Width = 4, BackColor = item.Color };
        var name = new Label
        {
            Text = item.Name,
            ForeColor = AppTheme.Text,
            Font = new Font("Segoe UI Semibold", 9),
            Location = new Point(14, 7),
            AutoSize = true,
            BackColor = Color.Transparent
        };
        var description = new Label
        {
            Text = item.Description,
            ForeColor = AppTheme.TextMuted,
            Font = new Font("Segoe UI", 7.5f),
            Location = new Point(14, 28),
            AutoEllipsis = true,
            Size = new Size(166, 17),
            BackColor = Color.Transparent
        };
        var addButton = new Button
        {
            Text = "+",
            Dock = DockStyle.Right,
            Width = 34,
            TabStop = false,
            AccessibleName = $"Add {item.Name}"
        };
        AppTheme.StyleButton(addButton);
        addButton.FlatAppearance.BorderSize = 0;
        addButton.Font = new Font("Segoe UI", 12, FontStyle.Bold);
        addButton.Click += (_, _) => ToolActivated?.Invoke(item.Type);
        card.Controls.Add(addButton);
        card.Controls.Add(description);
        card.Controls.Add(name);
        card.Controls.Add(accent);

        void BeginDrag(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) card.DoDragDrop(item.Type.ToString(), DragDropEffects.Copy);
        }
        void Activate(object? _, EventArgs __) => ToolActivated?.Invoke(item.Type);
        foreach (Control control in new Control[] { card, name, description })
        {
            control.MouseDown += (_, e) => BeginDrag(e);
            control.DoubleClick += Activate;
            _toolTip.SetToolTip(control, $"{item.Description}\nDouble-click to add");
        }
        card.MouseEnter += (_, _) => card.BackColor = Color.FromArgb(39, 46, 61);
        card.MouseLeave += (_, _) => card.BackColor = AppTheme.SurfaceRaised;
        _toolTip.SetToolTip(addButton, $"Add {item.Name}");
        return card;
    }
}
