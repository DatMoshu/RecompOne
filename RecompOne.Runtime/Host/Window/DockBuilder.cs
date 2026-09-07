using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;

namespace RecompOne.Runtime.Host.Window;

internal static unsafe class DockBuilder
{
    const string Lib = "cimgui";

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    static extern void igDockBuilderRemoveNode(uint nodeId);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    static extern void igDockBuilderAddNode(uint nodeId, int flags);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    static extern void igDockBuilderSetNodeSize(uint nodeId, Vector2 size);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    static extern void igDockBuilderDockWindow(byte* windowName, uint nodeId);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    static extern void igDockBuilderFinish(uint nodeId);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    static extern uint igDockBuilderSplitNode(uint nodeId, int splitDir, float sizeRatioForNodeAtDir, uint* outIdAtDir, uint* outIdAtOppositeDir);

    public static void SetupCenterLayout(uint dockId, Vector2 size, string windowName)
    {
        igDockBuilderRemoveNode(dockId);
        igDockBuilderAddNode(dockId, 0);
        igDockBuilderSetNodeSize(dockId, size);

        Dock(windowName, dockId);

        igDockBuilderFinish(dockId);
    }

    // The shipped default: a left column (top/bottom split), the game viewport centered
    // and largest, then one or two right-hand columns of individually-stacked debug
    // panels (each gets its own row, not tabbed together, so all are visible at once).
    //
    // farRightColumn is the "what is the game doing" column -- Game Info and Savestates
    // -- kept separate from the "what is the machine doing" column so that a run shows
    // both at a glance without anyone arranging windows first. Every panel in the
    // default layout is also force-opened by the caller; docking a closed window
    // positions it but leaves it invisible, which reads as the layout not applying.
    public static void SetupColumnsLayout(uint dockId, Vector2 size, string centerWindow,
        (string top, string bottom) leftColumn, string[] rightColumn,
        string[]? farRightColumn = null, string? centerBottom = null,
        float leftRatio = 0.19f, float rightRatio = 0.22f, float farRightRatio = 0.22f,
        float centerBottomRatio = 0.26f)
    {
        igDockBuilderRemoveNode(dockId);
        igDockBuilderAddNode(dockId, 0);
        igDockBuilderSetNodeSize(dockId, size);

        uint leftId, afterLeft;
        igDockBuilderSplitNode(dockId, (int)ImGuiDir.Left, leftRatio, &leftId, &afterLeft);

        // Each ratio is of the WHOLE node, so renormalize against what is actually left
        // after the previous splits -- otherwise the far-right column eats the centre.
        float remainingFrac = 1f - leftRatio;
        uint afterFarRight = afterLeft;
        uint farRightId = 0;
        if (farRightColumn is { Length: > 0 })
        {
            igDockBuilderSplitNode(afterLeft, (int)ImGuiDir.Right,
                farRightRatio / remainingFrac, &farRightId, &afterFarRight);
            remainingFrac -= farRightRatio;
        }

        uint rightId, centerId;
        igDockBuilderSplitNode(afterFarRight, (int)ImGuiDir.Right,
            rightRatio / remainingFrac, &rightId, &centerId);

        // The log goes UNDER the viewport rather than in a side column: it is the one panel
        // whose content is wide lines, and a screenshot of a misbehaving run is only useful
        // if those lines are readable rather than clipped at 20 characters.
        if (centerBottom is not null)
        {
            uint logId, viewportId;
            igDockBuilderSplitNode(centerId, (int)ImGuiDir.Down, centerBottomRatio, &logId, &viewportId);
            Dock(centerWindow, viewportId);
            Dock(centerBottom, logId);
        }
        else
        {
            Dock(centerWindow, centerId);
        }

        uint leftTop, leftBottom;
        igDockBuilderSplitNode(leftId, (int)ImGuiDir.Up, 0.5f, &leftTop, &leftBottom);
        Dock(leftColumn.top, leftTop);
        Dock(leftColumn.bottom, leftBottom);

        StackColumn(rightId, rightColumn);
        if (farRightColumn is { Length: > 0 }) StackColumn(farRightId, farRightColumn);

        igDockBuilderFinish(dockId);
    }

    // Give each window its own row in the column rather than tabbing them together.
    static void StackColumn(uint columnId, string[] windows)
    {
        uint remaining = columnId;
        for (int i = 0; i < windows.Length; i++)
        {
            uint rowId;
            if (i < windows.Length - 1)
            {
                float ratio = 1f / (windows.Length - i);
                uint below;
                igDockBuilderSplitNode(remaining, (int)ImGuiDir.Up, ratio, &rowId, &below);
                remaining = below;
            }
            else
            {
                rowId = remaining;
            }
            Dock(windows[i], rowId);
        }
    }

    static void Dock(string windowName, uint nodeId)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(windowName + "\0");
        fixed (byte* p = bytes) igDockBuilderDockWindow(p, nodeId);
    }
}
