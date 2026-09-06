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
    // and largest, and a right column of individually-stacked debug panels (each gets
    // its own row, not tabbed together, so all of them are visible at once).
    public static void SetupColumnsLayout(uint dockId, Vector2 size, string centerWindow,
        (string top, string bottom) leftColumn, string[] rightColumn,
        float leftRatio = 0.22f, float rightRatio = 0.30f)
    {
        igDockBuilderRemoveNode(dockId);
        igDockBuilderAddNode(dockId, 0);
        igDockBuilderSetNodeSize(dockId, size);

        uint leftId, afterLeft;
        igDockBuilderSplitNode(dockId, (int)ImGuiDir.Left, leftRatio, &leftId, &afterLeft);

        uint rightId, centerId;
        // rightRatio is of the whole node; renormalize against the remainder after the left split.
        float rightOfRemainder = rightRatio / (1f - leftRatio);
        igDockBuilderSplitNode(afterLeft, (int)ImGuiDir.Right, rightOfRemainder, &rightId, &centerId);

        Dock(centerWindow, centerId);

        uint leftTop, leftBottom;
        igDockBuilderSplitNode(leftId, (int)ImGuiDir.Up, 0.5f, &leftTop, &leftBottom);
        Dock(leftColumn.top, leftTop);
        Dock(leftColumn.bottom, leftBottom);

        uint remaining = rightId;
        for (int i = 0; i < rightColumn.Length; i++)
        {
            uint rowId;
            if (i < rightColumn.Length - 1)
            {
                float ratio = 1f / (rightColumn.Length - i);
                uint below;
                igDockBuilderSplitNode(remaining, (int)ImGuiDir.Up, ratio, &rowId, &below);
                remaining = below;
            }
            else
            {
                rowId = remaining;
            }
            Dock(rightColumn[i], rowId);
        }

        igDockBuilderFinish(dockId);
    }

    static void Dock(string windowName, uint nodeId)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(windowName + "\0");
        fixed (byte* p = bytes) igDockBuilderDockWindow(p, nodeId);
    }
}
