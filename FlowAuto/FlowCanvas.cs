using FlowAuto.Engine;
using FlowAuto.Models;

namespace FlowAuto;

public class FlowCanvas : Panel
{
    public List<FlowNode> Nodes { get; } = new();
    public List<FlowConnection> Connections { get; } = new();
    public FlowNode? SelectedNode { get; private set; }
    public int SelectedNodeIndex { get; private set; } = -1;

    // Multi-selection
    public HashSet<int> SelectedNodeIndices { get; } = new();

    // Events
    public event Action? NodesChanged;
    public event Action<int>? NodeSelected;
    public event Action? SelectionCleared;
    public event Action? ConnectionsChanged;

    // Drawing constants
    private const int CardWidth = 200;
    private const int CardHeight = 80;
    private const int ConnectorRadius = 12;
    private const int GridSize = 20;

    // Reused drawing resources avoid GDI allocation churn on canvases with many nodes.
    private readonly Bitmap _gridTile = CreateGridTile();
    private readonly TextureBrush _gridBrush;
    private readonly Font _nodeNameFont = new("Segoe UI", 10, FontStyle.Bold);
    private readonly Font _nodeTypeFont = new("Segoe UI", 8);
    private readonly Font _parameterFont = new("Consolas", 7);
    private readonly Font _connectorLabelFont = new("Segoe UI", 7, FontStyle.Bold);
    private readonly Font _smallConnectorLabelFont = new("Segoe UI", 6, FontStyle.Bold);
    private readonly Font _smallLabelFont = new("Segoe UI", 6);

    // Drag state
    private bool _isDragging;
    private bool _nodesMovedDuringDrag;
    private Point _dragStart;
    private int _dragNodeIndex = -1;

    // Multi-drag offset tracking for batch move
    private Dictionary<int, PointF> _dragInitialPositions = new();

    // Rubber band selection
    private bool _isRubberBanding;
    private Point _rubberBandStart;
    private Point _rubberBandCurrent;

    // Connection drag state
    private bool _isConnecting;
    private int _connectFromIndex = -1;
    private string _connectFromPort = "Output";
    private Point _connectCurrentPos;

    // Connection reconnection state (drag existing connection to new target)
    private bool _isReconnecting;
    private FlowConnection? _reconnectConnection;
    private bool _reconnectFromInput; // true = dragging input end, false = dragging output end

    // Hover state
    private FlowConnection? _hoveredConnection;

    // Manual scroll
    private int _scrollX;
    private int _scrollY;
    private int _virtualWidth = 2000;
    private int _virtualHeight = 2000;

    // Context menu
    private ContextMenuStrip? _contextMenu;

    public FlowCanvas()
    {
        _gridBrush = new TextureBrush(
            _gridTile,
            System.Drawing.Drawing2D.WrapMode.Tile);

        DoubleBuffered = true;
        BackColor = AppTheme.Canvas;
        AllowDrop = true;
        TabStop = true;
        SetStyle(ControlStyles.Selectable |
                 ControlStyles.UserPaint |
                 ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.ResizeRedraw, true);

        SetupContextMenu();
        SetupScrollBars();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _gridBrush.Dispose();
            _gridTile.Dispose();
            _nodeNameFont.Dispose();
            _nodeTypeFont.Dispose();
            _parameterFont.Dispose();
            _connectorLabelFont.Dispose();
            _smallConnectorLabelFont.Dispose();
            _smallLabelFont.Dispose();
        }

        base.Dispose(disposing);
    }

    private static Bitmap CreateGridTile()
    {
        var tile = new Bitmap(GridSize, GridSize);
        using var graphics = Graphics.FromImage(tile);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(Color.FromArgb(70, 64, 73, 92));
        graphics.FillEllipse(brush, 0, 0, 1.5f, 1.5f);
        return tile;
    }

    private void SetupScrollBars()
    {
        // Horizontal scrollbar at bottom
        _hScroll = new HScrollBar
        {
            Dock = DockStyle.Bottom,
            Height = 17,
            Minimum = 0,
            Maximum = _virtualWidth,
            LargeChange = 100,
            SmallChange = 10
        };
        _hScroll.ValueChanged += (s, e) => { _scrollX = _hScroll.Value; Invalidate(); };
        Controls.Add(_hScroll);

        // Vertical scrollbar at right
        _vScroll = new VScrollBar
        {
            Dock = DockStyle.Right,
            Width = 17,
            Minimum = 0,
            Maximum = _virtualHeight,
            LargeChange = 100,
            SmallChange = 10
        };
        _vScroll.ValueChanged += (s, e) => { _scrollY = _vScroll.Value; Invalidate(); };
        Controls.Add(_vScroll);

        // Bring canvas to front so scrollbars don't block it
        SendToBack();
    }

    private HScrollBar _hScroll = null!;
    private VScrollBar _vScroll = null!;

    private void UpdateScrollBars()
    {
        int maxX = _virtualWidth;
        int maxY = _virtualHeight;
        foreach (var node in Nodes)
        {
            if ((int)(node.CanvasX + CardWidth + 200) > maxX) maxX = (int)(node.CanvasX + CardWidth + 200);
            if ((int)(node.CanvasY + CardHeight + 200) > maxY) maxY = (int)(node.CanvasY + CardHeight + 200);
        }
        _virtualWidth = maxX;
        _virtualHeight = maxY;

        _hScroll.Maximum = Math.Max(0, _virtualWidth - ClientSize.Width + _hScroll.LargeChange);
        _vScroll.Maximum = Math.Max(0, _virtualHeight - ClientSize.Height + _vScroll.LargeChange);
    }

    private void SetupContextMenu()
    {
        _contextMenu = new ContextMenuStrip();
        _contextMenu.Items.Add("Delete", null, (s, e) =>
        {
            if (SelectedNodeIndices.Count > 0)
                DeleteSelectedNodes();
            else if (SelectedNodeIndex >= 0)
                DeleteNode(SelectedNodeIndex);
        });
        _contextMenu.Items.Add("Duplicate", null, (s, e) =>
        {
            if (SelectedNodeIndex >= 0) DuplicateNode(SelectedNodeIndex);
        });
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add("Edit", null, (s, e) =>
        {
            if (SelectedNodeIndex >= 0) NodeSelected?.Invoke(SelectedNodeIndex);
        });
    }

    // ============ Connection Hit Testing ============

    /// <summary>
    /// Hit test for connections. Returns the connection under the point, or null.
    /// </summary>
    private FlowConnection? HitTestConnection(Point pt)
    {
        if (Connections.Count == 0) return null;

        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        using var pen = new Pen(Color.Black, 8); // Wide hit-test area
        var nodesById = CreateNodeLookup();

        foreach (var conn in Connections)
        {
            if (!nodesById.TryGetValue(conn.FromId, out var fromNode) ||
                !nodesById.TryGetValue(conn.ToId, out var toNode))
                continue;

            var start = GetConnectorPoint(fromNode, conn.FromPort);
            var end = GetTargetConnectorPoint(toNode, conn.ToPort);
            var controlOffset = Math.Max(40, Math.Abs(end.Y - start.Y) / 2);
            var cp1 = new Point(start.X, start.Y + controlOffset);
            var cp2 = new Point(end.X, end.Y - controlOffset);

            path.Reset();
            path.AddBezier(start, cp1, cp2, end);
            if (path.IsOutlineVisible(pt, pen))
                return conn;
        }
        return null;
    }

    /// <summary>
    /// Builds a node-id lookup for connection rendering and hit testing.
    /// Preserve the first node for duplicate IDs to retain the previous lookup behavior.
    /// </summary>
    private Dictionary<string, FlowNode> CreateNodeLookup()
    {
        var nodesById = new Dictionary<string, FlowNode>(Nodes.Count, StringComparer.Ordinal);
        foreach (var node in Nodes)
        {
            nodesById.TryAdd(node.NodeId, node);
        }

        return nodesById;
    }

    // ============ Node Management ============

    public void AddNode(FlowNode node)
    {
        if (Nodes.Count > 0)
        {
            var last = Nodes[^1];
            node.CanvasX = last.CanvasX;
            node.CanvasY = last.CanvasY + CardHeight + 60;
        }
        else
        {
            node.CanvasX = 50;
            node.CanvasY = 50;
        }

        AddNodeCore(node);
    }

    /// <summary>
    /// Adds a node that has already been positioned and creates the matching LoopEnd
    /// marker when the node is a LoopStart. Keeping this in one path ensures toolbox
    /// and drag-and-drop additions preserve the same loop-pair invariant.
    /// </summary>
    private void AddNodeCore(FlowNode node)
    {
        Nodes.Add(node);

        if (node.NodeType == NodeType.Loop)
        {
            var loopEnd = CreateDefaultNode(NodeType.LoopEnd);
            loopEnd.PairedLoopStartId = node.NodeId;
            loopEnd.CanvasX = node.CanvasX;
            loopEnd.CanvasY = node.CanvasY + CardHeight + 120;
            Nodes.Add(loopEnd);
        }

        UpdateScrollBars();
        NodesChanged?.Invoke();
        Invalidate();
    }

    public void DeleteNode(int index)
    {
        if (index < 0 || index >= Nodes.Count) return;
        var nodeId = Nodes[index].NodeId;
        Nodes.RemoveAt(index);
        Connections.RemoveAll(c => c.FromId == nodeId || c.ToId == nodeId);

        if (SelectedNodeIndex == index)
        {
            SelectedNode = null;
            SelectedNodeIndex = -1;
            SelectionCleared?.Invoke();
        }

        // Also remove from multi-selection and adjust indices
        if (SelectedNodeIndices.Contains(index))
            SelectedNodeIndices.Remove(index);
        // Rebuild indices since we removed a node
        var newSet = new HashSet<int>();
        foreach (var idx in SelectedNodeIndices)
        {
            if (idx < index) newSet.Add(idx);
            else if (idx > index) newSet.Add(idx - 1);
        }
        SelectedNodeIndices.Clear();
        foreach (var idx in newSet) SelectedNodeIndices.Add(idx);

        UpdateScrollBars();
        NodesChanged?.Invoke();
        ConnectionsChanged?.Invoke();
        Invalidate();
    }

    public void DeleteSelectedNodes()
    {
        if (SelectedNodeIndices.Count == 0) return;

        // Sort descending to remove from end to start
        var indicesToDelete = SelectedNodeIndices.OrderByDescending(i => i).ToList();
        foreach (var idx in indicesToDelete)
        {
            if (idx < 0 || idx >= Nodes.Count) continue;
            var nodeId = Nodes[idx].NodeId;
            Nodes.RemoveAt(idx);
            Connections.RemoveAll(c => c.FromId == nodeId || c.ToId == nodeId);
        }

        SelectedNodeIndices.Clear();
        SelectedNode = null;
        SelectedNodeIndex = -1;
        SelectionCleared?.Invoke();

        UpdateScrollBars();
        NodesChanged?.Invoke();
        ConnectionsChanged?.Invoke();
        Invalidate();
    }

    public void DuplicateNode(int index)
    {
        if (index < 0 || index >= Nodes.Count) return;
        var original = Nodes[index];
        var node = new FlowNode
        {
            NodeId = Guid.NewGuid().ToString(),
            NodeType = original.NodeType,
            NodeName = original.NodeName + " (Copy)",
            Enabled = original.Enabled,
            TimeoutMs = original.TimeoutMs,
            RetryCount = original.RetryCount,
            Parameters = new Dictionary<string, object?>(original.Parameters),
            CanvasX = original.CanvasX + 30,
            CanvasY = original.CanvasY + 30
        };
        Nodes.Add(node);
        NodesChanged?.Invoke();
        Invalidate();
    }

    public void ClearNodes()
    {
        Nodes.Clear();
        Connections.Clear();
        SelectedNode = null;
        SelectedNodeIndex = -1;
        SelectedNodeIndices.Clear();
        SelectionCleared?.Invoke();
        NodesChanged?.Invoke();
        ConnectionsChanged?.Invoke();
        Invalidate();
    }

    // ============ Connection Management ============

    public void AddConnection(string fromId, string toId, string fromPort = "Output", string toPort = "Input")
    {
        // Prevent duplicate connections
        if (Connections.Any(c => c.FromId == fromId && c.ToId == toId && c.FromPort == fromPort && c.ToPort == toPort))
            return;

        // Prevent self-connection
        if (fromId == toId) return;

        Connections.Add(new FlowConnection
        {
            FromId = fromId,
            ToId = toId,
            FromPort = fromPort,
            ToPort = toPort
        });
        ConnectionsChanged?.Invoke();
        Invalidate();
    }

    public void RemoveConnection(string fromId, string toId, string fromPort)
    {
        Connections.RemoveAll(c => c.FromId == fromId && c.ToId == toId && c.FromPort == fromPort);
        ConnectionsChanged?.Invoke();
        Invalidate();
    }

    public void RemoveConnectionsFrom(string fromId, string fromPort)
    {
        Connections.RemoveAll(c => c.FromId == fromId && c.FromPort == fromPort);
        ConnectionsChanged?.Invoke();
        Invalidate();
    }

    public void RemoveConnectionsTo(string toId)
    {
        Connections.RemoveAll(c => c.ToId == toId);
        ConnectionsChanged?.Invoke();
        Invalidate();
    }

    public void RemoveConnectionsTo(string toId, string toPort)
    {
        Connections.RemoveAll(c => c.ToId == toId && c.ToPort == toPort);
        ConnectionsChanged?.Invoke();
        Invalidate();
    }

    public void LoadConnections(List<FlowConnection> connections)
    {
        Connections.Clear();
        Connections.AddRange(connections);
        ConnectionsChanged?.Invoke();
        Invalidate();
    }

    /// <summary>
    /// Arrange the graph into readable top-to-bottom layers. Disconnected and
    /// cyclic nodes are retained in deterministic trailing layers.
    /// </summary>
    public void AutoLayout()
    {
        if (Nodes.Count == 0) return;

        var nodeById = Nodes.ToDictionary(n => n.NodeId, StringComparer.Ordinal);
        var incoming = Nodes.ToDictionary(n => n.NodeId, _ => 0, StringComparer.Ordinal);
        var outgoing = Nodes.ToDictionary(n => n.NodeId, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var connection in Connections)
        {
            if (!nodeById.ContainsKey(connection.FromId) || !nodeById.ContainsKey(connection.ToId)) continue;
            incoming[connection.ToId]++;
            outgoing[connection.FromId].Add(connection.ToId);
        }

        var queue = new Queue<string>(Nodes.Where(n => incoming[n.NodeId] == 0).Select(n => n.NodeId));
        var level = Nodes.ToDictionary(n => n.NodeId, _ => 0, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!visited.Add(id)) continue;
            foreach (var target in outgoing[id])
            {
                level[target] = Math.Max(level[target], level[id] + 1);
                if (--incoming[target] == 0) queue.Enqueue(target);
            }
        }

        var trailingLevel = level.Values.DefaultIfEmpty(0).Max() + 1;
        foreach (var node in Nodes.Where(n => !visited.Contains(n.NodeId))) level[node.NodeId] = trailingLevel++;

        foreach (var group in Nodes.GroupBy(n => level[n.NodeId]).OrderBy(g => g.Key))
        {
            var row = group.ToList();
            for (var i = 0; i < row.Count; i++)
            {
                row[i].CanvasX = 60 + i * (CardWidth + 60);
                row[i].CanvasY = 60 + group.Key * (CardHeight + 70);
            }
        }

        _scrollX = _scrollY = 0;
        _hScroll.Value = _vScroll.Value = 0;
        UpdateScrollBars();
        NodesChanged?.Invoke();
        Invalidate();
    }

    // ============ Hit Testing ============

    private int HitTestNode(Point pt)
    {
        for (int i = Nodes.Count - 1; i >= 0; i--)
        {
            var n = Nodes[i];
            var rect = GetNodeRect(n);
            if (rect.Contains(pt)) return i;
        }
        return -1;
    }

    /// <summary>
    /// Hit test for output connectors. Returns (nodeIndex, portName) or (-1, "") if none.
    /// </summary>
    private (int index, string port) HitTestOutputConnector(Point pt)
    {
        for (int i = Nodes.Count - 1; i >= 0; i--)
        {
            var n = Nodes[i];
            var rect = GetNodeRect(n);
            if (!rect.Contains(pt)) continue;

            if (n.NodeType == NodeType.Condition || n.NodeType == NodeType.Loop || n.NodeType == NodeType.Break)
            {
                // True/Complete connector is left third of bottom edge
                var truePt = GetTrueConnector(n);
                if (Distance(pt, truePt) <= ConnectorRadius + 2)
                    return (i, "True");

                // False/Break connector is right third
                var falsePt = GetFalseConnector(n);
                if (Distance(pt, falsePt) <= ConnectorRadius + 2)
                    return (i, "False");
            }
            else if (n.NodeType == NodeType.ColorMotion)
            {
                var mode = n.GetParam<string>("MotionMode") ?? "MotionDetect";
                if (mode == "DirectionDetect")
                {
                    // Multiple direction output ports distributed along bottom edge
                    var ports = GetDirectionPorts(n);
                    foreach (var (portName, portPt) in ports)
                    {
                        if (Distance(pt, portPt) <= ConnectorRadius + 2)
                            return (i, portName);
                    }
                }
                else
                {
                    // MotionDetect / StateChange: True/False outputs
                    var truePt = GetTrueConnector(n);
                    if (Distance(pt, truePt) <= ConnectorRadius + 2)
                        return (i, "True");
                    var falsePt = GetFalseConnector(n);
                    if (Distance(pt, falsePt) <= ConnectorRadius + 2)
                        return (i, "False");
                }
            }
            else if (n.NodeType == NodeType.ColorCal)
            {
                // ColorCal uses result-based output ports
                var ports = GetResultPorts(n);
                foreach (var (portName, portPt) in ports)
                {
                    if (Distance(pt, portPt) <= ConnectorRadius + 2)
                        return (i, portName);
                }
                // Also has a standard output
                var outputPt = GetOutputConnector(n);
                if (Distance(pt, outputPt) <= ConnectorRadius + 2)
                    return (i, "Output");
            }
            else if (n.NodeType == NodeType.Gate)
            {
                // Gate has single output at center bottom
                var outputPt = GetOutputConnector(n);
                if (Distance(pt, outputPt) <= ConnectorRadius + 2)
                    return (i, "Output");
            }
            else
            {
                var outputPt = GetOutputConnector(n);
                if (Distance(pt, outputPt) <= ConnectorRadius + 2)
                    return (i, "Output");
            }
        }
        return (-1, "");
    }

    /// <summary>
    /// Hit test for input connector. Gate nodes have two input slots (left/right of top edge).
    /// Loop nodes in BreakCondition mode have a white break-condition port on the left edge.
    /// </summary>
    private (int index, string port) HitTestInputConnector(Point pt)
    {
        for (int i = Nodes.Count - 1; i >= 0; i--)
        {
            var n = Nodes[i];

            // Gate nodes support two inputs
            if (n.NodeType == NodeType.Gate)
            {
                var leftInput = new Point((int)n.CanvasX + CardWidth / 3 - _scrollX, (int)n.CanvasY - _scrollY);
                var rightInput = new Point((int)n.CanvasX + CardWidth * 2 / 3 - _scrollX, (int)n.CanvasY - _scrollY);

                if (Distance(pt, leftInput) <= ConnectorRadius + 2)
                    return (i, "Input0");
                if (Distance(pt, rightInput) <= ConnectorRadius + 2)
                    return (i, "Input1");
            }
            // Loop BreakCondition: white break-condition port on left edge
            else if (n.NodeType == NodeType.Loop)
            {
                var loopMode = n.GetParam<string>("LoopMode") ?? "FixedCount";
                if (loopMode == "BreakCondition")
                {
                    var breakCondPt = new Point((int)n.CanvasX - _scrollX, (int)n.CanvasY + CardHeight / 2 - _scrollY);
                    if (Distance(pt, breakCondPt) <= ConnectorRadius + 2)
                        return (i, "BreakCond");
                }
                // Standard input on top
                var inputPt = GetInputConnector(n);
                if (Distance(pt, inputPt) <= ConnectorRadius + 2)
                    return (i, "Input");
            }
            else
            {
                var inputPt = GetInputConnector(n);
                if (Distance(pt, inputPt) <= ConnectorRadius + 2)
                    return (i, "Input");
            }
        }
        return (-1, "");
    }

    private static int Distance(Point a, Point b)
    {
        int dx = a.X - b.X;
        int dy = a.Y - b.Y;
        return (int)Math.Sqrt(dx * dx + dy * dy);
    }

    private Rectangle GetNodeRect(FlowNode node)
    {
        return new Rectangle((int)node.CanvasX - _scrollX, (int)node.CanvasY - _scrollY, CardWidth, CardHeight);
    }

    private Point GetOutputConnector(FlowNode node)
    {
        return new Point((int)node.CanvasX + CardWidth / 2 - _scrollX, (int)node.CanvasY + CardHeight - _scrollY);
    }

    private Point GetTrueConnector(FlowNode node)
    {
        return new Point((int)node.CanvasX + CardWidth / 3 - _scrollX, (int)node.CanvasY + CardHeight - _scrollY);
    }

    private Point GetFalseConnector(FlowNode node)
    {
        return new Point((int)node.CanvasX + CardWidth * 2 / 3 - _scrollX, (int)node.CanvasY + CardHeight - _scrollY);
    }

    private Point GetCenterConnector(FlowNode node)
    {
        return new Point((int)node.CanvasX + CardWidth / 2 - _scrollX, (int)node.CanvasY + CardHeight - _scrollY);
    }

    private Point GetInputConnector(FlowNode node)
    {
        return new Point((int)node.CanvasX + CardWidth / 2 - _scrollX, (int)node.CanvasY - _scrollY);
    }

    private Point GetConnectorPoint(FlowNode node, string port)
    {
        // Handle standard ports
        if (port == "True") return GetTrueConnector(node);
        if (port == "False") return GetFalseConnector(node);
        if (port == "Output") return GetOutputConnector(node);

        // Check direction ports (ColorMotion DirectionDetect)
        var dirPorts = GetDirectionPorts(node);
        var dirMatch = dirPorts.FirstOrDefault(d => d.portName == port);
        if (dirMatch.portName != null) return dirMatch.point;

        // Check result ports (ColorCal)
        var resPorts = GetResultPorts(node);
        var resMatch = resPorts.FirstOrDefault(r => r.portName == port);
        if (resMatch.portName != null) return resMatch.point;

        return GetOutputConnector(node);
    }

    /// <summary>
    /// Get the position for a direction-named output port (ColorMotion DirectionDetect).
    /// </summary>
    private Point GetDirectionConnectorPoint(FlowNode node, string direction, int index, int total)
    {
        float segmentWidth = CardWidth / (float)(total + 1);
        float x = node.CanvasX + segmentWidth * (index + 1) - _scrollX;
        float y = node.CanvasY + CardHeight - _scrollY;
        return new Point((int)x, (int)y);
    }

    /// <summary>
    /// Get all direction output ports for a ColorMotion node in DirectionDetect mode.
    /// Returns list of (portName, point).
    /// </summary>
    private List<(string portName, Point point)> GetDirectionPorts(FlowNode node)
    {
        var ports = new List<(string, Point)>();
        var directions = node.GetParam<List<string>>("Directions");
        if (directions == null || directions.Count == 0)
        {
            // Default directions
            directions = new List<string> { "Up", "Down", "Left", "Right", "Stationary" };
        }
        for (int i = 0; i < directions.Count; i++)
        {
            ports.Add((directions[i], GetDirectionConnectorPoint(node, directions[i], i, directions.Count)));
        }
        return ports;
    }

    /// <summary>
    /// Get all result output ports for a ColorCal node.
    /// Ports are numbered 0, 1, 2... based on SuccessorCount.
    /// </summary>
    private List<(string portName, Point point)> GetResultPorts(FlowNode node)
    {
        var ports = new List<(string, Point)>();
        var successorCount = node.GetParam<int?>("SuccessorCount") ?? 1;
        for (int i = 0; i < successorCount; i++)
        {
            ports.Add((i.ToString(), GetDirectionConnectorPoint(node, i.ToString(), i, successorCount)));
        }
        return ports;
    }

    private Point GetTargetConnectorPoint(FlowNode node, string port)
    {
        return port switch
        {
            "Input0" => new Point((int)node.CanvasX + CardWidth / 3 - _scrollX, (int)node.CanvasY - _scrollY),
            "Input1" => new Point((int)node.CanvasX + CardWidth * 2 / 3 - _scrollX, (int)node.CanvasY - _scrollY),
            "BreakCond" => new Point((int)node.CanvasX - _scrollX, (int)node.CanvasY + CardHeight / 2 - _scrollY),
            _ => GetInputConnector(node)
        };
    }

    // ============ Drawing ============

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        // Full clear of the visible area
        g.Clear(BackColor);

        // Draw grid aligned to scroll offset
        DrawGrid(g);

        // Draw connections. Build the ID lookup once for this paint, instead of
        // linearly searching nodes again for every connection.
        Dictionary<string, FlowNode>? nodesById = null;
        if (Connections.Count > 0 || _isReconnecting)
        {
            nodesById = CreateNodeLookup();
        }

        if (Connections.Count > 0)
        {
            DrawConnections(g, nodesById!);
        }

        // Draw active connection line while dragging
        if (_isConnecting)
        {
            DrawActiveConnection(g);
        }

        // Draw reconnecting line while re-routing an existing connection
        if (_isReconnecting)
        {
            DrawReconnectingLine(g, nodesById!);
        }

        // Draw nodes
        for (int i = 0; i < Nodes.Count; i++)
        {
            DrawNodeCard(g, Nodes[i], SelectedNodeIndices.Contains(i) || i == SelectedNodeIndex);
        }

        // Draw rubber band selection rectangle
        if (_isRubberBanding)
        {
            DrawRubberBand(g);
        }
    }

    private void DrawGrid(Graphics g)
    {
        // A tiled brush draws the same scroll-aligned dot grid with one paint
        // operation rather than issuing one FillEllipse call for every point.
        _gridBrush.ResetTransform();
        _gridBrush.TranslateTransform(-(_scrollX % GridSize), -(_scrollY % GridSize));
        g.FillRectangle(_gridBrush, ClientRectangle);
    }

    private void DrawConnections(Graphics g, IReadOnlyDictionary<string, FlowNode> nodesById)
    {
        using var pen = new Pen(Color.FromArgb(100, 180, 180, 180), 2);
        pen.EndCap = System.Drawing.Drawing2D.LineCap.ArrowAnchor;

        foreach (var conn in Connections)
        {
            if (!nodesById.TryGetValue(conn.FromId, out var fromNode) ||
                !nodesById.TryGetValue(conn.ToId, out var toNode))
                continue;

            var start = GetConnectorPoint(fromNode, conn.FromPort);
            var end = GetTargetConnectorPoint(toNode, conn.ToPort);

            // Determine if this connection is hovered or being reconnected
            bool isHovered = conn == _hoveredConnection;
            bool isReconnecting = conn == _reconnectConnection;

            // Color code by port, with hover highlight
            pen.Color = conn.FromPort switch
            {
                "True" => isHovered || isReconnecting ? Color.FromArgb(255, 52, 168, 83) : Color.FromArgb(180, 52, 168, 83),
                "False" => isHovered || isReconnecting ? Color.FromArgb(255, 233, 30, 99) : Color.FromArgb(180, 233, 30, 99),
                _ => isHovered || isReconnecting ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(180, 180, 180, 180)
            };
            pen.Width = isHovered || isReconnecting ? 3 : 2;

            var controlOffset = Math.Max(40, Math.Abs(end.Y - start.Y) / 2);
            var cp1 = new Point(start.X, start.Y + controlOffset);
            var cp2 = new Point(end.X, end.Y - controlOffset);

            g.DrawBezier(pen, start, cp1, cp2, end);
        }
    }

    private void DrawReconnectingLine(Graphics g, IReadOnlyDictionary<string, FlowNode> nodesById)
    {
        if (_reconnectConnection == null) return;

        if (!nodesById.TryGetValue(_reconnectConnection.FromId, out var fromNode) ||
            !nodesById.TryGetValue(_reconnectConnection.ToId, out var toNode))
            return;

        Point start, end;
        if (_reconnectFromInput)
        {
            // Dragging the input end: start from output connector, end at mouse
            start = GetConnectorPoint(fromNode, _reconnectConnection.FromPort);
            end = _connectCurrentPos;
        }
        else
        {
            // Dragging the output end: start from mouse, end at input connector
            start = _connectCurrentPos;
            end = GetTargetConnectorPoint(toNode, _reconnectConnection.ToPort);
        }

        using var pen = new Pen(Color.FromArgb(220, 255, 255, 255), 2);
        pen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
        pen.EndCap = System.Drawing.Drawing2D.LineCap.ArrowAnchor;

        var controlOffset = Math.Max(40, Math.Abs(end.Y - start.Y) / 2);
        var cp1 = new Point(start.X, start.Y + controlOffset);
        var cp2 = new Point(end.X, end.Y - controlOffset);

        g.DrawBezier(pen, start, cp1, cp2, end);
    }

    private void DrawActiveConnection(Graphics g)
    {
        if (_connectFromIndex < 0 || _connectFromIndex >= Nodes.Count) return;

        var fromNode = Nodes[_connectFromIndex];
        var start = GetConnectorPoint(fromNode, _connectFromPort);
        var end = _connectCurrentPos;

        using var pen = new Pen(Color.FromArgb(180, 255, 255, 255), 2);
        pen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
        pen.EndCap = System.Drawing.Drawing2D.LineCap.ArrowAnchor;

        var controlOffset = Math.Max(40, Math.Abs(end.Y - start.Y) / 2);
        var cp1 = new Point(start.X, start.Y + controlOffset);
        var cp2 = new Point(end.X, end.Y - controlOffset);

        g.DrawBezier(pen, start, cp1, cp2, end);
    }

    private void DrawRubberBand(Graphics g)
    {
        var rect = GetRubberBandRect();
        using var pen = new Pen(Color.FromArgb(200, 66, 133, 244), 1);
        pen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
        using var brush = new SolidBrush(Color.FromArgb(40, 66, 133, 244));
        g.FillRectangle(brush, rect);
        g.DrawRectangle(pen, rect);
    }

    private Rectangle GetRubberBandRect()
    {
        int x = Math.Min(_rubberBandStart.X, _rubberBandCurrent.X);
        int y = Math.Min(_rubberBandStart.Y, _rubberBandCurrent.Y);
        int w = Math.Abs(_rubberBandCurrent.X - _rubberBandStart.X);
        int h = Math.Abs(_rubberBandCurrent.Y - _rubberBandStart.Y);
        return new Rectangle(x, y, w, h);
    }

    private void DrawNodeCard(Graphics g, FlowNode node, bool isSelected)
    {
        var rect = GetNodeRect(node);
        Color baseColor = GetNodeColor(node.NodeType);

        // Shadow
        using (var shadowBrush = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
        {
            var shadowRect = new Rectangle(rect.X + 3, rect.Y + 3, rect.Width, rect.Height);
            g.FillRoundedRectangle(shadowBrush, shadowRect, 8);
        }

        // Card background
        using var cardBrush = new SolidBrush(AppTheme.SurfaceRaised);
        g.FillRoundedRectangle(cardBrush, rect, 8);

        // Selection border
        if (isSelected)
        {
            using var selPen = new Pen(AppTheme.Accent, 2.5f);
            g.DrawRoundedRectangle(selPen, rect, 8);
        }
        else
        {
            using var borderPen = new Pen(AppTheme.Border);
            g.DrawRoundedRectangle(borderPen, rect, 8);
        }

        // Color bar (left edge)
        using var colorBrush = new SolidBrush(baseColor);
        var barRect = new Rectangle(rect.X + 3, rect.Y + 8, 5, rect.Height - 16);
        g.FillRectangle(colorBrush, barRect);

        // Status indicator (little circle for disabled)
        if (!node.Enabled)
        {
            using var grayBrush = new SolidBrush(Color.Gray);
            g.FillEllipse(grayBrush, rect.Right - 20, rect.Top + 8, 10, 10);
        }

        // Node name
        using var nameBrush = new SolidBrush(AppTheme.Text);
        var nameRect = new Rectangle(rect.X + 14, rect.Y + 6, rect.Width - 30, 22);
        g.DrawString(node.NodeName, _nodeNameFont, nameBrush, nameRect);

        // Node type
        using var typeBrush = new SolidBrush(baseColor);
        var typeRect = new Rectangle(rect.X + 14, rect.Y + 28, rect.Width - 20, 16);
        var displayType = node.NodeType switch
        {
            NodeType.Loop => "Loop Start",
            NodeType.LoopEnd => "Loop End",
            _ => node.NodeType.ToString()
        };
        g.DrawString(displayType, _nodeTypeFont, typeBrush, typeRect);

        // Parameter summary
        using var paramBrush = new SolidBrush(AppTheme.TextMuted);
        var paramRect = new Rectangle(rect.X + 14, rect.Y + 44, rect.Width - 20, 32);
        var summary = GetParameterSummary(node);
        g.DrawString(summary, _parameterFont, paramBrush, paramRect);

        // Input connector (standard nodes only; Gate and LoopStart have their own multi-input drawn below)
        if (node.NodeType != NodeType.Gate && node.NodeType != NodeType.Loop)
        {
            var inputPt = GetInputConnector(node);
            g.FillEllipse(Brushes.White, inputPt.X - ConnectorRadius / 2, inputPt.Y - ConnectorRadius / 2, ConnectorRadius, ConnectorRadius);
            using (var connPen = new Pen(Color.FromArgb(100, 180, 180, 180)))
            {
                g.DrawEllipse(connPen, inputPt.X - ConnectorRadius / 2, inputPt.Y - ConnectorRadius / 2, ConnectorRadius, ConnectorRadius);
            }
        }

        // Output connector(s)
        if (node.NodeType == NodeType.Condition || node.NodeType == NodeType.Loop || node.NodeType == NodeType.Break)
        {
            // True/Complete connector (green)
            var truePt = GetTrueConnector(node);
            using var trueBrush = new SolidBrush(Color.FromArgb(52, 168, 83));
            g.FillEllipse(trueBrush, truePt.X - ConnectorRadius / 2, truePt.Y - ConnectorRadius / 2, ConnectorRadius, ConnectorRadius);
            using var connPen = new Pen(Color.FromArgb(100, 180, 180, 180));
            g.DrawEllipse(connPen, truePt.X - ConnectorRadius / 2, truePt.Y - ConnectorRadius / 2, ConnectorRadius, ConnectorRadius);
            using var labelBrush = new SolidBrush(Color.FromArgb(52, 168, 83));
            var trueLabel = node.NodeType == NodeType.Loop ? "Complete" :
                            node.NodeType == NodeType.Break ? "Break" : "True";
            g.DrawString(trueLabel, _connectorLabelFont, labelBrush, truePt.X - 16, truePt.Y + ConnectorRadius + 2);

            // False/Break connector (red)
            var falsePt = GetFalseConnector(node);
            using var falseBrush = new SolidBrush(Color.FromArgb(233, 30, 99));
            g.FillEllipse(falseBrush, falsePt.X - ConnectorRadius / 2, falsePt.Y - ConnectorRadius / 2, ConnectorRadius, ConnectorRadius);
            using var fconnPen = new Pen(Color.FromArgb(100, 180, 180, 180));
            g.DrawEllipse(fconnPen, falsePt.X - ConnectorRadius / 2, falsePt.Y - ConnectorRadius / 2, ConnectorRadius, ConnectorRadius);
            using var flabelBrush = new SolidBrush(Color.FromArgb(233, 30, 99));
            var falseLabel = node.NodeType == NodeType.Loop ? "Break" :
                             node.NodeType == NodeType.Break ? "Continue" : "False";
            g.DrawString(falseLabel, _connectorLabelFont, flabelBrush, falsePt.X - 12, falsePt.Y + ConnectorRadius + 2);
        }
        else if (node.NodeType == NodeType.ColorMotion)
        {
            var mode = node.GetParam<string>("MotionMode") ?? "MotionDetect";
            if (mode == "DirectionDetect")
            {
                // Multi-direction output ports
                var ports = GetDirectionPorts(node);
                foreach (var (portName, portPt) in ports)
                {
                    using var dirBrush = new SolidBrush(GetDirectionColor(portName));
                    g.FillEllipse(dirBrush, portPt.X - ConnectorRadius / 2, portPt.Y - ConnectorRadius / 2, ConnectorRadius, ConnectorRadius);
                    using var connPen = new Pen(Color.FromArgb(100, 180, 180, 180));
                    g.DrawEllipse(connPen, portPt.X - ConnectorRadius / 2, portPt.Y - ConnectorRadius / 2, ConnectorRadius, ConnectorRadius);
                    using var lBrush = new SolidBrush(GetDirectionColor(portName));
                    g.DrawString(portName, _smallConnectorLabelFont, lBrush, portPt.X - 10, portPt.Y + ConnectorRadius + 2);
                }
            }
            else
            {
                // True/False for MotionDetect and StateChange
                DrawTrueFalsePorts(g, node);
            }
        }
        else if (node.NodeType == NodeType.ColorCal)
        {
            // Result-based output ports
            var ports = GetResultPorts(node);
            var defaultColors = new[] {
                Color.FromArgb(52, 168, 83), Color.FromArgb(233, 30, 99),
                Color.FromArgb(66, 133, 244), Color.FromArgb(251, 188, 4),
                Color.FromArgb(156, 39, 176)
            };
            for (int i = 0; i < ports.Count; i++)
            {
                var (portName, portPt) = ports[i];
                var col = i < defaultColors.Length ? defaultColors[i] : Color.Gray;
                using var resBrush = new SolidBrush(col);
                g.FillEllipse(resBrush, portPt.X - ConnectorRadius / 2, portPt.Y - ConnectorRadius / 2, ConnectorRadius, ConnectorRadius);
                using var connPen = new Pen(Color.FromArgb(100, 180, 180, 180));
                g.DrawEllipse(connPen, portPt.X - ConnectorRadius / 2, portPt.Y - ConnectorRadius / 2, ConnectorRadius, ConnectorRadius);
                using var lBrush = new SolidBrush(col);
                g.DrawString(portName, _smallConnectorLabelFont, lBrush, portPt.X - 10, portPt.Y + ConnectorRadius + 2);
            }
        }
        else if (node.NodeType == NodeType.Gate)
        {
            // Gate has a single output port (result)
            var outputPt = GetOutputConnector(node);
            using var gateBrush = new SolidBrush(Color.FromArgb(0, 188, 212));
            g.FillEllipse(gateBrush, outputPt.X - ConnectorRadius / 2, outputPt.Y - ConnectorRadius / 2, ConnectorRadius, ConnectorRadius);
            using var connPen = new Pen(Color.FromArgb(100, 180, 180, 180));
            g.DrawEllipse(connPen, outputPt.X - ConnectorRadius / 2, outputPt.Y - ConnectorRadius / 2, ConnectorRadius, ConnectorRadius);
            using var labelBrush = new SolidBrush(Color.FromArgb(0, 188, 212));
            g.DrawString("Result", _connectorLabelFont, labelBrush, outputPt.X - 14, outputPt.Y + ConnectorRadius + 2);
        }
        else
        {
            // Standard output connector
            var outputPt = GetOutputConnector(node);
            g.FillEllipse(Brushes.White, outputPt.X - ConnectorRadius / 2, outputPt.Y - ConnectorRadius / 2, ConnectorRadius, ConnectorRadius);
            using var connPen = new Pen(Color.FromArgb(100, 180, 180, 180));
            g.DrawEllipse(connPen, outputPt.X - ConnectorRadius / 2, outputPt.Y - ConnectorRadius / 2, ConnectorRadius, ConnectorRadius);
        }

        // Gate node: draw TWO input connectors (was 3, now standardized to 2)
        if (node.NodeType == NodeType.Gate)
        {
            var leftInput = new Point((int)node.CanvasX + CardWidth / 3 - _scrollX, (int)node.CanvasY - _scrollY);
            var rightInput = new Point((int)node.CanvasX + CardWidth * 2 / 3 - _scrollX, (int)node.CanvasY - _scrollY);

            using var inputBrush = new SolidBrush(Color.FromArgb(0, 188, 212));
            using var inputPen = new Pen(Color.FromArgb(100, 180, 180, 180));
            using var inputLabelBrush = new SolidBrush(Color.FromArgb(150, 150, 150));

            g.FillEllipse(inputBrush, leftInput.X - ConnectorRadius / 2, leftInput.Y - ConnectorRadius / 2, ConnectorRadius, ConnectorRadius);
            g.DrawEllipse(inputPen, leftInput.X - ConnectorRadius / 2, leftInput.Y - ConnectorRadius / 2, ConnectorRadius, ConnectorRadius);
            g.DrawString("0", _smallLabelFont, inputLabelBrush, leftInput.X - 3, leftInput.Y - ConnectorRadius - 8);

            g.FillEllipse(inputBrush, rightInput.X - ConnectorRadius / 2, rightInput.Y - ConnectorRadius / 2, ConnectorRadius, ConnectorRadius);
            g.DrawEllipse(inputPen, rightInput.X - ConnectorRadius / 2, rightInput.Y - ConnectorRadius / 2, ConnectorRadius, ConnectorRadius);
            g.DrawString("1", _smallLabelFont, inputLabelBrush, rightInput.X - 3, rightInput.Y - ConnectorRadius - 8);
        }

        // Loop node in BreakCondition mode: white break-condition input port on LEFT side
        if (node.NodeType == NodeType.Loop)
        {
            var loopMode = node.GetParam<string>("LoopMode") ?? "FixedCount";

            // Standard input port on top
            var inputPt = GetInputConnector(node);
            g.FillEllipse(Brushes.White, inputPt.X - ConnectorRadius / 2, inputPt.Y - ConnectorRadius / 2, ConnectorRadius, ConnectorRadius);
            using (var connPen = new Pen(Color.FromArgb(100, 180, 180, 180)))
            {
                g.DrawEllipse(connPen, inputPt.X - ConnectorRadius / 2, inputPt.Y - ConnectorRadius / 2, ConnectorRadius, ConnectorRadius);
            }

            if (loopMode == "BreakCondition")
            {
                // White break-condition port on the left edge, vertically centered
                var breakCondPt = new Point((int)node.CanvasX - _scrollX, (int)node.CanvasY + CardHeight / 2 - _scrollY);
                using var whiteBrush = new SolidBrush(Color.White);
                g.FillEllipse(whiteBrush, breakCondPt.X - ConnectorRadius / 2, breakCondPt.Y - ConnectorRadius / 2, ConnectorRadius, ConnectorRadius);
                using var whitePen = new Pen(Color.FromArgb(180, 255, 255, 255), 2);
                g.DrawEllipse(whitePen, breakCondPt.X - ConnectorRadius / 2, breakCondPt.Y - ConnectorRadius / 2, ConnectorRadius, ConnectorRadius);
                using var bcBrush = new SolidBrush(Color.White);
                g.DrawString("Break", _smallConnectorLabelFont, bcBrush, breakCondPt.X - ConnectorRadius - 16, breakCondPt.Y - 6);
            }
        }
    }

    /// <summary>
    /// Draw True/False output ports for ColorMotion (MotionDetect/StateChange modes).
    /// </summary>
    private void DrawTrueFalsePorts(Graphics g, FlowNode node)
    {
        var truePt = GetTrueConnector(node);
        using var trueBrush = new SolidBrush(Color.FromArgb(52, 168, 83));
        g.FillEllipse(trueBrush, truePt.X - ConnectorRadius / 2, truePt.Y - ConnectorRadius / 2, ConnectorRadius, ConnectorRadius);
        using var connPen = new Pen(Color.FromArgb(100, 180, 180, 180));
        g.DrawEllipse(connPen, truePt.X - ConnectorRadius / 2, truePt.Y - ConnectorRadius / 2, ConnectorRadius, ConnectorRadius);
        using var trueLB = new SolidBrush(Color.FromArgb(52, 168, 83));
        g.DrawString("True", _connectorLabelFont, trueLB, truePt.X - 12, truePt.Y + ConnectorRadius + 2);

        var falsePt = GetFalseConnector(node);
        using var falseBrush = new SolidBrush(Color.FromArgb(233, 30, 99));
        g.FillEllipse(falseBrush, falsePt.X - ConnectorRadius / 2, falsePt.Y - ConnectorRadius / 2, ConnectorRadius, ConnectorRadius);
        g.DrawEllipse(connPen, falsePt.X - ConnectorRadius / 2, falsePt.Y - ConnectorRadius / 2, ConnectorRadius, ConnectorRadius);
        using var falseLB = new SolidBrush(Color.FromArgb(233, 30, 99));
        g.DrawString("False", _connectorLabelFont, falseLB, falsePt.X - 14, falsePt.Y + ConnectorRadius + 2);
    }

    private static Color GetDirectionColor(string direction) => direction switch
    {
        "Up" => Color.FromArgb(66, 133, 244),      // Blue
        "Down" => Color.FromArgb(52, 168, 83),     // Green
        "Left" => Color.FromArgb(251, 188, 4),     // Yellow
        "Right" => Color.FromArgb(233, 30, 99),    // Red
        "Stationary" => Color.FromArgb(158, 158, 158), // Gray
        _ => Color.Gray
    };

    private static string GetConditionSummary(FlowNode node)
    {
        var ct = node.GetParam<string>("ConditionType") ?? "?";
        return ct switch
        {
            "ImageAppear" => $"If 图片存在: {Path.GetFileName(node.GetParam<string>("TemplateImagePath") ?? "?")}",
            "OCRContain" => $"If 文字包含: \"{node.GetParam<string>("OCRText") ?? "?"}\"",
            _ => ct
        };
    }

    private Color GetNodeColor(NodeType type) => type switch
    {
        NodeType.StartProgram => Color.FromArgb(66, 133, 244),   // Blue
        NodeType.ClickElement => Color.FromArgb(52, 168, 83),    // Green
        NodeType.WaitCondition => Color.FromArgb(251, 188, 4),   // Yellow
        NodeType.KeyPress => Color.FromArgb(154, 71, 220),      // Purple
        NodeType.Loop => Color.FromArgb(255, 152, 0),           // Orange
        NodeType.LoopEnd => Color.FromArgb(255, 152, 0),        // Orange (same as Loop)
        NodeType.Condition => Color.FromArgb(233, 30, 99),      // Pink
        NodeType.Gate => Color.FromArgb(0, 188, 212),           // Cyan
        NodeType.ColorMotion => Color.FromArgb(0, 200, 83),     // Green-Teal
        NodeType.ColorCal => Color.FromArgb(156, 39, 176),      // Deep Purple
        NodeType.Break => Color.FromArgb(244, 67, 54),          // Red
        _ => Color.Gray
    };

    private string GetParameterSummary(FlowNode node)
    {
        return node.NodeType switch
        {
            NodeType.StartProgram => node.GetParam<string>("FilePath")?.Split('\\').LastOrDefault() ?? "",
            NodeType.ClickElement => node.GetParam<string>("LocateMode") ?? "Coordinate",
            NodeType.WaitCondition => node.GetParam<string>("ConditionType") ?? "",
            NodeType.KeyPress => node.GetParam<string>("KeyName") ?? "",
            NodeType.Loop => node.GetParam<string>("LoopMode") == "BreakCondition"
                ? "Loop ∞ (BreakCond)"
                : $"Loop x{node.GetParam<int?>("LoopCount") ?? 0}",
            NodeType.LoopEnd => "⟐ End",
            NodeType.Condition => GetConditionSummary(node),
            NodeType.Gate => node.GetParam<string>("GateLogicType") ?? "AND",
            NodeType.ColorMotion => node.GetParam<string>("MotionMode") == "DirectionDetect"
                ? $"DirDetect ({node.GetParam<string>("TrackMode") ?? "TemplateMatch"})"
                : node.GetParam<string>("MotionMode") ?? "MotionDetect",
            NodeType.ColorCal => $"Expr: {Truncate(node.GetParam<string>("Expression") ?? "", 40)}",
            NodeType.Break => "Break Loop",
            _ => ""
        };
    }

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..(maxLen - 3)] + "...";

    // ============ Selection Helpers ============

    private void SelectSingleNode(int index)
    {
        SelectedNodeIndices.Clear();
        if (index >= 0)
            SelectedNodeIndices.Add(index);

        SelectedNodeIndex = index;
        SelectedNode = index >= 0 ? Nodes[index] : null;

        if (index >= 0)
            NodeSelected?.Invoke(index);
        else
            SelectionCleared?.Invoke();

        Invalidate();
    }

    private void ToggleNodeSelection(int index)
    {
        if (index < 0 || index >= Nodes.Count) return;

        if (SelectedNodeIndices.Contains(index))
        {
            SelectedNodeIndices.Remove(index);
            if (SelectedNodeIndex == index)
            {
                // Pick another as primary if available
                SelectedNodeIndex = SelectedNodeIndices.FirstOrDefault(-1);
                SelectedNode = SelectedNodeIndex >= 0 ? Nodes[SelectedNodeIndex] : null;
            }
        }
        else
        {
            SelectedNodeIndices.Add(index);
            SelectedNodeIndex = index;
            SelectedNode = Nodes[index];
            NodeSelected?.Invoke(index);
        }

        if (SelectedNodeIndices.Count == 0)
            SelectionCleared?.Invoke();

        Invalidate();
    }

    private void SelectNodesInRect(Rectangle rect)
    {
        bool ctrlHeld = (ModifierKeys & Keys.Control) == Keys.Control;
        if (!ctrlHeld)
            SelectedNodeIndices.Clear();

        for (int i = 0; i < Nodes.Count; i++)
        {
            var nodeRect = GetNodeRect(Nodes[i]);
            if (rect.IntersectsWith(nodeRect))
            {
                SelectedNodeIndices.Add(i);
            }
        }

        SelectedNodeIndex = SelectedNodeIndices.FirstOrDefault(-1);
        SelectedNode = SelectedNodeIndex >= 0 ? Nodes[SelectedNodeIndex] : null;

        if (SelectedNodeIndex >= 0)
            NodeSelected?.Invoke(SelectedNodeIndex);
        else
            SelectionCleared?.Invoke();

        Invalidate();
    }

    // ============ Mouse Events ============

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        var testPt = new Point(e.X, e.Y);
        var virtualPt = new Point(e.X + _scrollX, e.Y + _scrollY);

        // Check if clicking on an output connector (start new connection or reconnect existing output)
        var (outIdx, outPort) = HitTestOutputConnector(testPt);
        if (outIdx >= 0 && e.Button == MouseButtons.Left)
        {
            var outNodeId = Nodes[outIdx].NodeId;
            // Check if there's an existing connection from this output port to start reconnecting
            var existingConn = Connections.FirstOrDefault(c => c.FromId == outNodeId && c.FromPort == outPort);
            if (existingConn != null)
            {
                // Start reconnecting from output end
                _isReconnecting = true;
                _reconnectConnection = existingConn;
                _reconnectFromInput = false;
                _connectCurrentPos = testPt;
                Connections.Remove(existingConn); // Remove temporarily, will add back on drop
                Invalidate();
                return;
            }

            _isConnecting = true;
            _connectFromIndex = outIdx;
            _connectFromPort = outPort;
            _connectCurrentPos = testPt;
            Invalidate();
            return;
        }

        // Check if clicking on an input connector (reconnect existing input or delete)
        var (inIdx, inPort) = HitTestInputConnector(testPt);
        if (inIdx >= 0)
        {
            var inNodeId = Nodes[inIdx].NodeId;
            var existing = Connections.FirstOrDefault(c => c.ToId == inNodeId && c.ToPort == inPort);
            if (existing != null)
            {
                if (e.Button == MouseButtons.Left)
                {
                    // Start reconnecting from input end
                    _isReconnecting = true;
                    _reconnectConnection = existing;
                    _reconnectFromInput = true;
                    _connectCurrentPos = testPt;
                    Connections.Remove(existing); // Remove temporarily, will add back on drop
                    Invalidate();
                    return;
                }
                else if (e.Button == MouseButtons.Right)
                {
                    Connections.Remove(existing);
                    ConnectionsChanged?.Invoke();
                    Invalidate();
                    return;
                }
            }
        }

        // Check if right-clicking on a connection line to delete it
        if (e.Button == MouseButtons.Right)
        {
            var hitConn = HitTestConnection(testPt);
            if (hitConn != null)
            {
                Connections.Remove(hitConn);
                ConnectionsChanged?.Invoke();
                Invalidate();
                return;
            }
        }

        if (e.Button == MouseButtons.Left)
        {
            var idx = HitTestNode(testPt);
            if (idx >= 0)
            {
                bool ctrlHeld = (ModifierKeys & Keys.Control) == Keys.Control;
                bool shiftHeld = (ModifierKeys & Keys.Shift) == Keys.Shift;

                if (ctrlHeld)
                {
                    ToggleNodeSelection(idx);
                }
                else if (shiftHeld && SelectedNodeIndex >= 0)
                {
                    // Range selection
                    int start = Math.Min(SelectedNodeIndex, idx);
                    int end = Math.Max(SelectedNodeIndex, idx);
                    SelectedNodeIndices.Clear();
                    for (int i = start; i <= end; i++)
                        SelectedNodeIndices.Add(i);
                    SelectedNodeIndex = idx;
                    SelectedNode = Nodes[idx];
                    NodeSelected?.Invoke(idx);
                    Invalidate();
                }
                else
                {
                    // Normal click: if clicking an already-selected node in multi-selection, prepare to drag all
                    if (SelectedNodeIndices.Count > 1 && SelectedNodeIndices.Contains(idx))
                    {
                        // Keep selection, start multi-drag
                        _isDragging = true;
                        _nodesMovedDuringDrag = false;
                        _dragStart = virtualPt;
                        _dragNodeIndex = idx;
                        _dragInitialPositions.Clear();
                        foreach (var selIdx in SelectedNodeIndices)
                        {
                            _dragInitialPositions[selIdx] = new PointF(Nodes[selIdx].CanvasX, Nodes[selIdx].CanvasY);
                        }
                    }
                    else
                    {
                        SelectSingleNode(idx);
                        _isDragging = true;
                        _nodesMovedDuringDrag = false;
                        _dragStart = virtualPt;
                        _dragNodeIndex = idx;
                        _dragInitialPositions.Clear();
                        _dragInitialPositions[idx] = new PointF(Nodes[idx].CanvasX, Nodes[idx].CanvasY);
                    }
                }
            }
            else
            {
                // Clicked on empty space: start rubber band selection
                if ((ModifierKeys & Keys.Control) != Keys.Control)
                {
                    SelectedNodeIndices.Clear();
                    SelectedNode = null;
                    SelectedNodeIndex = -1;
                    SelectionCleared?.Invoke();
                }
                _isRubberBanding = true;
                _rubberBandStart = testPt;
                _rubberBandCurrent = testPt;
                Invalidate();
            }
        }
        else if (e.Button == MouseButtons.Right)
        {
            var idx = HitTestNode(testPt);
            if (idx >= 0)
            {
                if (!SelectedNodeIndices.Contains(idx))
                    SelectSingleNode(idx);
                _contextMenu?.Show(this, e.Location);
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var testPt = new Point(e.X, e.Y);

        if (_isConnecting || _isReconnecting)
        {
            _connectCurrentPos = testPt;
            Invalidate();
            return;
        }

        if (_isRubberBanding)
        {
            _rubberBandCurrent = testPt;
            Invalidate();
            return;
        }

        if (_isDragging && _dragNodeIndex >= 0 && _dragNodeIndex < Nodes.Count)
        {
            // Moving a node repaints the canvas continuously. Connection hit
            // testing is expensive and cannot affect drag behavior, so skip it.
            bool clearedHover = _hoveredConnection != null;
            if (clearedHover)
            {
                _hoveredConnection = null;
                Cursor = Cursors.Default;
            }

            var virtualPt = new Point(e.X + _scrollX, e.Y + _scrollY);
            var dx = virtualPt.X - _dragStart.X;
            var dy = virtualPt.Y - _dragStart.Y;

            if (dx == 0 && dy == 0)
            {
                if (clearedHover) Invalidate();
                return;
            }

            if (SelectedNodeIndices.Count > 1 && SelectedNodeIndices.Contains(_dragNodeIndex))
            {
                // Batch move all selected nodes
                foreach (var selIdx in SelectedNodeIndices)
                {
                    if (selIdx < 0 || selIdx >= Nodes.Count) continue;
                    if (_dragInitialPositions.TryGetValue(selIdx, out var initialPos))
                    {
                        Nodes[selIdx].CanvasX = initialPos.X + dx;
                        Nodes[selIdx].CanvasY = initialPos.Y + dy;
                    }
                }
            }
            else
            {
                // Single node move
                Nodes[_dragNodeIndex].CanvasX += dx;
                Nodes[_dragNodeIndex].CanvasY += dy;
                _dragStart = virtualPt;
            }

            _nodesMovedDuringDrag = true;
            UpdateScrollBars();
            Invalidate();
            return;
        }

        // Hover detection for connections
        var prevHover = _hoveredConnection;
        _hoveredConnection = HitTestConnection(testPt);
        if (_hoveredConnection != prevHover)
        {
            Invalidate();
            // Change cursor to indicate interactivity
            Cursor = _hoveredConnection != null ? Cursors.Hand : Cursors.Default;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (_isRubberBanding)
        {
            _isRubberBanding = false;
            var rect = GetRubberBandRect();
            if (rect.Width > 5 && rect.Height > 5)
            {
                SelectNodesInRect(rect);
            }
            else
            {
                // Small drag = click on empty space, clear selection if no ctrl
                if ((ModifierKeys & Keys.Control) != Keys.Control)
                {
                    SelectedNodeIndices.Clear();
                    SelectedNode = null;
                    SelectedNodeIndex = -1;
                    SelectionCleared?.Invoke();
                    Invalidate();
                }
            }
            return;
        }

        if (_isConnecting)
        {
            var testPt = new Point(e.X, e.Y);
            var (inIdx, inPort) = HitTestInputConnector(testPt);

            if (inIdx >= 0 && _connectFromIndex >= 0 && _connectFromIndex < Nodes.Count)
            {
                var fromNode = Nodes[_connectFromIndex];
                var toNode = Nodes[inIdx];

                // Don't connect to self
                if (fromNode.NodeId != toNode.NodeId)
                {
                    AddConnection(fromNode.NodeId, toNode.NodeId, _connectFromPort, inPort);
                }
            }

            _isConnecting = false;
            _connectFromIndex = -1;
            _connectFromPort = "Output";
            Invalidate();
            return;
        }

        if (_isReconnecting && _reconnectConnection != null)
        {
            var testPt = new Point(e.X, e.Y);

            if (_reconnectFromInput)
            {
                // Reconnecting the input end: need to drop on an input connector
                var (inIdx, inPort) = HitTestInputConnector(testPt);
                if (inIdx >= 0)
                {
                    var toNode = Nodes[inIdx];
                    // Don't connect to self
                    if (toNode.NodeId != _reconnectConnection.FromId)
                    {
                        AddConnection(_reconnectConnection.FromId, toNode.NodeId, _reconnectConnection.FromPort, inPort);
                    }
                }
                else
                {
                    // Dropped in empty space: restore original connection
                    Connections.Add(_reconnectConnection);
                    ConnectionsChanged?.Invoke();
                }
            }
            else
            {
                // Reconnecting the output end: need to drop on an output connector
                var (outIdx, outPort) = HitTestOutputConnector(testPt);
                if (outIdx >= 0)
                {
                    var fromNode = Nodes[outIdx];
                    // Don't connect to self
                    if (fromNode.NodeId != _reconnectConnection.ToId)
                    {
                        AddConnection(fromNode.NodeId, _reconnectConnection.ToId, outPort);
                    }
                }
                else
                {
                    // Dropped in empty space: restore original connection
                    Connections.Add(_reconnectConnection);
                    ConnectionsChanged?.Invoke();
                }
            }

            _isReconnecting = false;
            _reconnectConnection = null;
            Invalidate();
            return;
        }

        bool nodesMoved = _isDragging && _nodesMovedDuringDrag;
        _isDragging = false;
        _nodesMovedDuringDrag = false;
        _dragNodeIndex = -1;
        _dragInitialPositions.Clear();

        if (nodesMoved)
        {
            NodesChanged?.Invoke();
        }
    }

    public void SelectNode(int index)
    {
        SelectSingleNode(index);
    }

    // ============ Keyboard ============

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.KeyCode == Keys.Delete)
        {
            if (SelectedNodeIndices.Count > 1)
            {
                DeleteSelectedNodes();
            }
            else if (SelectedNodeIndex >= 0)
            {
                DeleteNode(SelectedNodeIndex);
            }
        }
        else if (e.Control && e.KeyCode == Keys.X)
        {
            if (SelectedNodeIndices.Count > 1)
            {
                DeleteSelectedNodes();
            }
            else if (SelectedNodeIndex >= 0)
            {
                DeleteNode(SelectedNodeIndex);
            }
        }
        else if (e.Control && e.KeyCode == Keys.D)
        {
            if (SelectedNodeIndex >= 0)
            {
                DuplicateNode(SelectedNodeIndex);
            }
        }
        else if (e.Control && e.KeyCode == Keys.A)
        {
            // Select all nodes
            SelectedNodeIndices.Clear();
            for (int i = 0; i < Nodes.Count; i++)
                SelectedNodeIndices.Add(i);
            SelectedNodeIndex = Nodes.Count > 0 ? 0 : -1;
            SelectedNode = SelectedNodeIndex >= 0 ? Nodes[SelectedNodeIndex] : null;
            if (SelectedNodeIndex >= 0)
                NodeSelected?.Invoke(SelectedNodeIndex);
            Invalidate();
        }
    }

    protected override bool IsInputKey(Keys keyData)
    {
        return keyData is Keys.Up or Keys.Down or Keys.Left or Keys.Right;
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(typeof(string)) == true)
        {
            e.Effect = DragDropEffects.Copy;
        }
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(typeof(string)) == true)
        {
            var nodeTypeName = (string)e.Data.GetData(typeof(string))!;
            if (Enum.TryParse<NodeType>(nodeTypeName, out var nodeType))
            {
                var node = CreateDefaultNode(nodeType);
                var clientPos = PointToClient(new Point(e.X, e.Y));
                node.CanvasX = clientPos.X + _scrollX - CardWidth / 2;
                node.CanvasY = clientPos.Y + _scrollY - CardHeight / 2;
                AddNodeCore(node);
            }
        }
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        UpdateScrollBars();
        Invalidate();
    }

    public static FlowNode CreateDefaultNode(NodeType type)
    {
        var node = new FlowNode
        {
            NodeType = type,
            NodeName = type switch
            {
                NodeType.StartProgram => "New StartProgram",
                NodeType.ClickElement => "New Click",
                NodeType.WaitCondition => "New Wait",
                NodeType.KeyPress => "New KeyPress",
                NodeType.Loop => "New Loop",
                NodeType.LoopEnd => "Loop End",
                NodeType.Condition => "New Condition",
                NodeType.Gate => "New Gate",
                NodeType.ColorMotion => "New ColorMotion",
                NodeType.ColorCal => "New ColorCal",
                NodeType.Break => "Break",
                _ => "New Node"
            }
        };

        // Set default parameters
        switch (type)
        {
            case NodeType.StartProgram:
                node.SetParam("FilePath", "");
                node.SetParam("WorkingDirectory", "");
                node.SetParam("Arguments", "");
                node.SetParam("RunAsAdmin", false);
                node.SetParam("WaitForWindowMs", 5000);
                node.SetParam("WindowTitleKeyword", "");
                break;
            case NodeType.ClickElement:
                node.SetParam("TargetWindow", "");
                node.SetParam("LocateMode", "Coordinate");
                node.SetParam("Region", new Engine.Region { X = 0, Y = 0, Width = 200, Height = 200 });
                node.SetParam("UseFullScreen", true);
                node.SetParam("TemplateImagePath", "");
                node.SetParam("TemplateMatchThreshold", 0.8);
                node.SetParam("TemplateScaleRange", new TemplateScaleRange());
                node.SetParam("PreDelayMs", 100);
                node.SetParam("PostDelayMs", 500);
                break;
            case NodeType.WaitCondition:
                node.SetParam("TargetWindow", "");
                node.SetParam("ConditionType", "ImageAppear");
                node.SetParam("Region", new Engine.Region { X = 0, Y = 0, Width = 200, Height = 200 });
                node.SetParam("UseFullScreen", true);
                node.SetParam("TemplateImagePath", "");
                node.SetParam("TemplateMatchThreshold", 0.8);
                node.SetParam("CheckIntervalMs", 500);
                break;
            case NodeType.KeyPress:
                node.SetParam("KeyScanCode", (byte)0);
                node.SetParam("KeyName", "F");
                node.SetParam("PressMode", "Press");
                node.SetParam("HoldDurationMs", 500);
                node.SetParam("TargetWindow", "");
                break;
            case NodeType.Loop:
                node.SetParam("LoopCount", 3);
                node.SetParam("LoopMode", "FixedCount");
                break;
            case NodeType.Condition:
                // Condition now only supports ImageAppear and OCRContain
                node.SetParam("ConditionType", "ImageAppear");
                node.SetParam("TargetWindow", "");
                node.SetParam("Region", new Engine.Region { X = 0, Y = 0, Width = 200, Height = 200 });
                node.SetParam("UseFullScreen", true);
                node.SetParam("TemplateImagePath", "");
                node.SetParam("TemplateMatchThreshold", 0.8);
                break;
            case NodeType.Gate:
                node.SetParam("GateLogicType", "AND");
                break;
            case NodeType.ColorMotion:
                node.SetParam("MotionMode", "MotionDetect");
                node.SetParam("TargetWindow", "");
                node.SetParam("Region", new Engine.Region { X = 0, Y = 0, Width = 200, Height = 200 });
                node.SetParam("UseFullScreen", true);
                // HSV
                node.SetParam("TargetRgb", "49,218,183");
                node.SetParam("HueTolerance", 8);
                node.SetParam("SVTolerance", 30);
                // Motion params
                node.SetParam("MoveCheckIntervalMs", 30);
                node.SetParam("MoveDurationMs", 10000);
                node.SetParam("MoveThresholdPx", 5);
                // StateChange params
                node.SetParam("StateCheckIntervalMs", 100);
                node.SetParam("StateDurationMs", 30000);
                node.SetParam("ColorChangeThreshold", 0.15);
                // DirectionDetect params
                node.SetParam("Directions", new List<string> { "Up", "Down", "Left", "Right", "Stationary" });
                node.SetParam("ReferenceImagePath", "");
                node.SetParam("TemplateMatchThreshold", 0.8);
                break;
            case NodeType.ColorCal:
                node.SetParam("DetectionTargets", new List<ColorCalTarget>
                {
                    new ColorCalTarget { Name = "Target1" }
                });
                node.SetParam("Expression", "Target1.Found ? 0 : 1");
                node.SetParam("SuccessorCount", 2);
                break;
            case NodeType.Break:
                // Break node is auto-generated with Loop, minimal params
                node.Enabled = true;
                node.TimeoutMs = 5000;
                break;
        }

        return node;
    }
}

// Helper extension for rounded rectangle
internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics g, Brush brush, Rectangle rect, int radius)
    {
        using var path = GetRoundedRectPath(rect, radius);
        g.FillPath(brush, path);
    }

    public static void DrawRoundedRectangle(this Graphics g, Pen pen, Rectangle rect, int radius)
    {
        using var path = GetRoundedRectPath(rect, radius);
        g.DrawPath(pen, path);
    }

    private static System.Drawing.Drawing2D.GraphicsPath GetRoundedRectPath(Rectangle rect, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        int d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
