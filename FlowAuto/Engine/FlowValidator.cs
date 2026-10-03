using FlowAuto.Models;

namespace FlowAuto.Engine;

public static class FlowValidator
{
    public static IReadOnlyList<string> Validate(FlowDefinition? flow)
    {
        var errors = new List<string>();
        if (flow == null)
        {
            errors.Add("Flow data is empty or invalid.");
            return errors;
        }

        if (flow.Nodes == null)
        {
            errors.Add("Nodes collection is missing.");
            return errors;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var nodesById = new Dictionary<string, FlowNode>(StringComparer.Ordinal);
        foreach (var node in flow.Nodes)
        {
            var label = string.IsNullOrWhiteSpace(node.NodeName) ? node.NodeId : node.NodeName;
            if (string.IsNullOrWhiteSpace(node.NodeId))
                errors.Add($"Node '{label}' has no ID.");
            else if (!ids.Add(node.NodeId))
                errors.Add($"Duplicate node ID: {node.NodeId}.");
            else
                nodesById[node.NodeId] = node;

            if (node.TimeoutMs <= 0)
                errors.Add($"Node '{label}' must have a timeout greater than 0 ms.");
            if (node.RetryCount < 0)
                errors.Add($"Node '{label}' cannot have a negative retry count.");

            ValidateNodeParameters(node, label, errors);
        }

        var connections = flow.Connections ?? [];
        var connectionKeys = new HashSet<(string FromId, string FromPort, string ToId, string ToPort)>();
        foreach (var connection in connections)
        {
            if (!ids.Contains(connection.FromId))
                errors.Add($"Connection source does not exist: {connection.FromId}.");
            if (!ids.Contains(connection.ToId))
                errors.Add($"Connection target does not exist: {connection.ToId}.");
            if (connection.FromId == connection.ToId)
                errors.Add($"Node '{connection.FromId}' cannot connect directly to itself.");
            if (string.IsNullOrWhiteSpace(connection.FromPort) || string.IsNullOrWhiteSpace(connection.ToPort))
                errors.Add($"Connection {connection.FromId} -> {connection.ToId} has an empty port name.");
            if (!connectionKeys.Add((connection.FromId, connection.FromPort, connection.ToId, connection.ToPort)))
                errors.Add($"Duplicate connection: {connection.FromId}[{connection.FromPort}] -> {connection.ToId}[{connection.ToPort}].");
        }

        ValidateGateInputs(nodesById, connections, errors);
        ValidateAcyclicGraph(ids, connections, errors);

        return errors;
    }

    private static void ValidateNodeParameters(FlowNode node, string label, List<string> errors)
    {
        var scaleRange = node.GetParam<TemplateScaleRange>("TemplateScaleRange");
        if (scaleRange != null &&
            (!double.IsFinite(scaleRange.Min) || !double.IsFinite(scaleRange.Max) ||
             !double.IsFinite(scaleRange.Step) || scaleRange.Min <= 0 ||
             scaleRange.Max < scaleRange.Min || scaleRange.Step <= 0))
        {
            errors.Add($"Node '{label}' has an invalid template scale range (Min > 0, Max >= Min, Step > 0 required).");
        }
        else if (scaleRange != null && (scaleRange.Max - scaleRange.Min) / scaleRange.Step > 255)
        {
            errors.Add($"Node '{label}' template scale range creates too many scales; increase Step.");
        }

        var threshold = node.GetParam<double?>("TemplateMatchThreshold");
        if (threshold is { } thresholdValue &&
            (!double.IsFinite(thresholdValue) || thresholdValue < 0 || thresholdValue > 1))
            errors.Add($"Node '{label}' template match threshold must be between 0 and 1.");

        ValidatePositiveParameter(node, label, "CheckIntervalMs", errors);
        ValidatePositiveParameter(node, label, "MoveCheckIntervalMs", errors);
        ValidatePositiveParameter(node, label, "StateCheckIntervalMs", errors);
        ValidatePositiveParameter(node, label, "MoveDurationMs", errors);
        ValidatePositiveParameter(node, label, "StateDurationMs", errors);

        var waitMs = node.GetParam<int?>("WaitMs");
        if (waitMs is < 0)
            errors.Add($"Node '{label}' WaitMs cannot be negative.");
        ValidateNonNegativeParameter(node, label, "PreDelayMs", errors);
        ValidateNonNegativeParameter(node, label, "PostDelayMs", errors);
        ValidateNonNegativeParameter(node, label, "HoldDurationMs", errors);

        var hueTolerance = node.GetParam<int?>("HueTolerance");
        if (hueTolerance is < 0 or > 179)
            errors.Add($"Node '{label}' hue tolerance must be between 0 and 179.");
        var svTolerance = node.GetParam<int?>("SVTolerance");
        if (svTolerance is < 0 or > 255)
            errors.Add($"Node '{label}' saturation/value tolerance must be between 0 and 255.");

        var region = node.GetParam<Region>("Region");
        var locateMode = node.GetParam<string>("LocateMode") ?? "";
        var requiresRegion =
            (node.NodeType == NodeType.ClickElement &&
             locateMode.Equals("Coordinate", StringComparison.OrdinalIgnoreCase)) ||
            node.GetParam<bool?>("UseFullScreen") == false;
        if (requiresRegion && region != null &&
            (region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0))
            errors.Add($"Node '{label}' region requires X/Y >= 0 and Width/Height > 0.");

        var loopCount = node.GetParam<int?>("LoopCount");
        if (loopCount is < 0)
            errors.Add($"Node '{label}' loop count cannot be negative.");
    }

    private static void ValidatePositiveParameter(
        FlowNode node, string label, string parameterName, List<string> errors)
    {
        var value = node.GetParam<int?>(parameterName);
        if (value is <= 0)
            errors.Add($"Node '{label}' {parameterName} must be greater than zero.");
    }

    private static void ValidateNonNegativeParameter(
        FlowNode node, string label, string parameterName, List<string> errors)
    {
        var value = node.GetParam<int?>(parameterName);
        if (value is < 0)
            errors.Add($"Node '{label}' {parameterName} cannot be negative.");
    }

    private static void ValidateGateInputs(
        IReadOnlyDictionary<string, FlowNode> nodesById,
        IReadOnlyList<FlowConnection> connections,
        List<string> errors)
    {
        foreach (var gate in nodesById.Values.Where(node => node.NodeType == NodeType.Gate))
        {
            var inputPorts = connections
                .Where(connection => connection.ToId == gate.NodeId)
                .Select(connection => connection.ToPort)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var logicType = gate.GetParam<string>("GateLogicType") ?? "AND";

            if (!inputPorts.Contains("Input0"))
                errors.Add($"Gate '{gate.NodeName}' requires a connection to Input0.");
            if (!logicType.Equals("NOT", StringComparison.OrdinalIgnoreCase) && !inputPorts.Contains("Input1"))
                errors.Add($"Gate '{gate.NodeName}' requires a connection to Input1 for {logicType}.");
        }
    }

    private static void ValidateAcyclicGraph(
        IReadOnlySet<string> nodeIds,
        IReadOnlyList<FlowConnection> connections,
        List<string> errors)
    {
        var outgoing = connections
            .Where(connection => nodeIds.Contains(connection.FromId) && nodeIds.Contains(connection.ToId))
            .GroupBy(connection => connection.FromId)
            .ToDictionary(group => group.Key, group => group.Select(connection => connection.ToId).Distinct().ToList());
        var state = new Dictionary<string, byte>(StringComparer.Ordinal);

        bool Visit(string nodeId)
        {
            state[nodeId] = 1;
            if (outgoing.TryGetValue(nodeId, out var successors))
            {
                foreach (var successor in successors)
                {
                    if (state.GetValueOrDefault(successor) == 1) return true;
                    if (state.GetValueOrDefault(successor) == 0 && Visit(successor)) return true;
                }
            }
            state[nodeId] = 2;
            return false;
        }

        if (nodeIds.Any(nodeId => state.GetValueOrDefault(nodeId) == 0 && Visit(nodeId)))
            errors.Add("Flow connections contain a cycle. Use Loop/LoopEnd nodes instead of a direct graph cycle.");
    }
}
