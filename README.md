# FlowAuto

**Visual Workflow Automation Engine for Windows** — automate desktop programs, games, and tools using a visual node editor. No coding required.

![Version](https://img.shields.io/badge/version-5.0-blue)
![Platform](https://img.shields.io/badge/platform-Windows_10%2F11-lightgrey)
![.NET](https://img.shields.io/badge/.NET-8.0_(bundled)-purple)
![License](https://img.shields.io/badge/license-MIT-green)

---

## What is FlowAuto?

FlowAuto is a **visual node-based automation engine** that lets you build complex desktop automation workflows by dragging and connecting logic nodes. It combines image recognition, OCR, color tracking, window control, and keyboard/mouse simulation into a single GUI tool.

### How It Works

1. **Design** your automation flow on a visual canvas — drag nodes from the toolbox and connect them
2. **Configure** each node's parameters in the property panel (windows, coordinates, images, colors, keys)
3. **Run** the flow — the engine executes nodes sequentially, following the connections
4. **Monitor** execution through a real-time log panel

Flows are saved as JSON files and can be edited or shared.

---

## Use Cases

| Scenario                         | How FlowAuto Helps                                                                       |
| -------------------------------- | ---------------------------------------------------------------------------------------- |
| **Game automation**        | Auto-launch → wait for loading → image-click UI → press keys in a loop                |
| **Desktop app automation** | Launch app → wait for window → click buttons → fill forms                             |
| **UI monitoring**          | Detect color/state changes on screen, trigger actions when conditions change             |
| **Visual tracking**        | Track colored objects on screen, detect movement direction, auto-control via key presses |
| **Multi-condition logic**  | Combine multiple visual conditions with AND/OR logic for complex decision making         |

---

## Node Types

FlowAuto provides **9 logic nodes**, each serving a distinct purpose in the automation pipeline.

### `StartProgram` — Launch External Programs

- **What it does**: Starts an executable, waits for its window to appear, stores the window handle for downstream nodes.
- **Use when**: You need to launch a game, tool, or any program as the first step of your flow.
- **Key features**: Supports admin mode, custom arguments, window keyword matching.

### `ClickElement` — Locate & Click UI Elements

- **What it does**: Finds and clicks a target position in a window using three modes: **Coordinate**, **TemplateMatch**, or **OCR**.
- **Use when**: You need to click buttons, menus, or any UI element.
- **Key features**: Multi-scale template matching (0.5x–1.5x), configurable pre/post-click delays.

### `WaitCondition` — Wait for a Condition

- **What it does**: Blocks execution until a condition is met. Supports 5 types: **ImageAppear**, **ImageDisappear**, **OCRContain**, **WindowExist**, **Timeout**.
- **Use when**: You need to wait for a loading screen to finish, a dialog to appear, or a fixed delay before the next step.
- **Key features**: Configurable check interval, timeout, and retry logic.

### `KeyPress` — Send Keyboard Input

- **What it does**: Sends hardware scan code keystrokes to the target window. Supports Press, Hold, and Release modes.
- **Use when**: You need to press keys (letters, function keys, ESC, SPACE, etc.) in a target window.
- **Key features**: Built-in scan code table, hold duration control, window activation.

### `Loop` — Repeat Child Nodes

- **What it does**: Executes connected child nodes in a loop. Two modes: **FixedCount** (repeat N times) or **BreakCondition** (loop until a break signal is received).
- **Use when**: You need to repeat actions, monitor continuously, or run until a condition is met.
- **Key features**: BreakCondition mode has a built-in white input port on the left — connect a Condition's True output to exit the loop. No separate Break node needed.

### `Condition` — Conditional Branching

- **What it does**: Checks a condition once and routes execution to **True** or **False** branch. Supports **ImageAppear** (template match) and **OCRContain** (text detection).
- **Use when**: You need to make a decision based on what appears on screen.
- **Key features**: Instant snapshot check (not polling), full-screen or region-based.

### `Gate` — Logic Gate (2-Input, 1-Output)

- **What it does**: Receives two upstream signals, performs a logical operation (**AND** / **OR** / **NOT**), and produces a single result for downstream nodes.
- **Use when**: You need to combine multiple conditions. Example: two ColorMotion nodes detecting different directions → Gate(AND) = diagonal movement detected.
- **Key features**: Two input ports (0, 1), one result output port.

### `ColorMotion` — Color Visual Monitoring

- **What it does**: Independent color tracking and motion analysis node with three modes:
  - **MotionDetect**: Is a target color moving? → True/False
  - **StateChange**: Has the color at a fixed position changed? → True/False
  - **DirectionDetect**: Which direction is the target moving? → 5 output ports (Up, Down, Left, Right, Stationary)
- **Use when**: You need to track colored objects on screen, detect UI state changes, or drive directional control.
- **Key features**: HSV color filtering, template matching reference, configurable frame intervals. Diagonal detection via two ColorMotion nodes + Gate(AND).

### `ColorCal` — Multi-Target Color Computation

- **What it does**: Defines multiple independent detection targets (each with its own HSV/template config), batch-identifies them, then evaluates a **C# expression** based on the targets' pixel coordinates. The integer result routes to the matching output port.
- **Use when**: You need complex positional logic. Example: `Target1.Found ? (Target1.X > Target2.X ? 0 : 1) : 2` — route to different branches based on relative positions of two targets.
- **Key features**: Expression variables (`{Name}.X`, `{Name}.Y`, `{Name}.Found`), configurable successor count, dynamic output ports.

---

## Project Structure

```
FlowAuto/
├── FlowAuto.sln              # Visual Studio solution
├── FlowAuto/                 # Main project
│   ├── Program.cs            # Entry point
│   ├── MainForm.cs           # Main window, toolbar, execution
│   ├── FlowCanvas.cs         # Visual node editor (rendering, connections)
│   ├── PropertyPanel.cs      # Node property editor
│   ├── ToolboxPanel.cs       # Node type list (drag source)
│   ├── HelpForm.cs           # Per-node English help documentation
│   ├── ScreenshotOverlay.cs  # Screenshot tool
│   ├── WindowRegionPicker.cs # Window/region picker tool
│   ├── ColorPickerForm.cs    # HSV color picker
│   ├── Engine/
│   │   ├── FlowExecutor.cs   # Flow execution engine
│   │   ├── FlowContext.cs    # Execution context (state, window handles)
│   │   └── FlowLogger.cs     # Logging
│   ├── Core/
│   │   ├── ImageRecognition.cs   # Template matching, color tracking
│   │   ├── OcrHelper.cs          # OCR text detection
│   │   ├── InputSimulator.cs     # Keyboard/mouse simulation
│   │   ├── ScreenCapture.cs      # Screen capture
│   │   └── WindowHelper.cs       # Windows API (FindWindow, etc.)
│   └── Models/
│       ├── FlowNode.cs           # Node data model
│       ├── FlowConnection.cs     # Connection data model
│       ├── ColorCalTarget.cs     # ColorCal target definition
│       ── Enums.cs              # All enums (NodeType, modes, etc.)
├── FlowAuto_用户手册.md        # Chinese user manual
├── FlowAuto_用户手册_CN.md     # Chinese user manual (full)
└── README.md                   # This file
```

---

## Quick Start

### For End Users (Download & Run)

1. Download the latest `FlowAuto` release package from the [Releases]() page
2. Extract the archive to any folder
3. Run `FlowAuto.exe`

> **No .NET runtime or OpenCV installation needed** — everything is bundled in the self-contained package.

### Prerequisites (for End Users)

- **Windows 10/11 (x64)**
- **Visual C++ Redistributable (x64)** — [Download](https://aka.ms/vs/17/release/vc_redist.x64.exe) *(usually pre-installed on most systems)*

### Build from Source (for Developers)

```powershell
# Clone the repository
cd FlowAuto

# Publish self-contained (no .NET SDK needed on target machine)
dotnet publish FlowAuto\FlowAuto.csproj --configuration Release -o "..\publish" --self-contained true

# The output is in the publish folder — copy the entire folder to any Windows x64 machine and run:
.\FlowAuto.exe
```

> **Administrator privileges required** — the application uses a manifest to request elevation. Keyboard/mouse simulation and window control require admin rights.

### Regression Tests

The repository includes a dependency-free regression executable covering flow routing, Gate behavior, cancellation/timeouts, validation, HSV boundary matching, template cache refresh, and prepared-template performance.

```powershell
dotnet run --project FlowAuto.RegressionTests\FlowAuto.RegressionTests.csproj --configuration Release
```

---

## Screenshots

*(Add screenshots here)*

- Visual node editor with connections
- Property panel with per-node configuration
- Screenshot tool for template capture
- HSV color picker for visual monitoring

---

## Documentation

- **[Chinese User Manual (Full)](FlowAuto_用户手册_CN.md)** — Comprehensive guide with every node, tool, and FAQ
- **[English User Manual](FlowAuto_用户手册.md)** — English version of the user manual
- **In-app Help** — Each node's property panel has a **? Help** button with English documentation

---

## Flow File Format

Flows are saved as JSON. Example structure:

```json
{
  "FlowName": "Auto Launch & Click",
  "Version": "1.0",
  "Nodes": [
    {
      "NodeId": "guid-1",
      "NodeType": "StartProgram",
      "NodeName": "Launch App",
      "Parameters": {
        "FilePath": "D:\\Game\\launcher.exe",
        "WindowTitleKeyword": "Game"
      }
    },
    {
      "NodeId": "guid-2",
      "NodeType": "ClickElement",
      "NodeName": "Click Start",
      "Parameters": {
        "TargetWindow": "Game",
        "LocateMode": "TemplateMatch",
        "TemplateImagePath": "templates/start_btn.png"
      }
    }
  ]
}
```

---

## Dependencies

All dependencies are **bundled inside the self-contained release** — end users do not need to install anything except the VC++ Redistributable.

| Package                  | Version | Purpose                                             |
| ------------------------ | ------- | --------------------------------------------------- |
| OpenCvSharp4             | 4.13.0  | Image processing, template matching, color tracking |
| OpenCvSharp4.Extensions  | 4.13.0  | Bitmap interoperability                             |
| OpenCvSharp4.runtime.win | 4.13.0  | Native OpenCV binaries (bundled)                    |
| .NET 8.0 Runtime         | 8.0     | Application runtime (bundled)                       |

---

## License

MIT License

---

## Acknowledgments

- [OpenCvSharp](https://github.com/shimat/opencvsharp) — OpenCV wrapper for .NET
- [Microsoft .NET 8](https://dotnet.microsoft.com/) — Desktop runtime and WinForms
