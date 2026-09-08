using System.Numerics;
using System.Text;
using ImGuiNET;
using RecompOne.Runtime.Assets;

namespace RecompOne.Runtime.Host.Window;

/// <summary>Browse whatever the running port registered in <see cref="AssetCatalog"/>:
/// a lazy tree on the left; on the right the selected node's details, an image preview
/// when it has one, a hex view of its bytes, and an Export button that writes those bytes
/// to <c>assets_export/</c> beside the exe. Game-agnostic: it renders nodes, it does not
/// parse anything itself.</summary>
internal sealed class AssetViewerPanel : IPanel
{
    public string Name => "Asset Viewer";
    public bool IsOpen { get; set; }

    AssetNode? _selected;
    byte[]? _bytes;
    (int W, int H, byte[] Rgba)? _image;
    uint _tex;
    int _texW, _texH;
    bool _imageUploaded;
    int _hexRows = 64;
    string _status = "";
    readonly TextureView _view = new();
    string _filter = "";

    public void Draw()
    {
        ImGui.SetNextWindowSize(new Vector2(900, 560), ImGuiCond.FirstUseEver);
        bool open = IsOpen;
        if (!ImGui.Begin(Name, ref open)) { IsOpen = open; ImGui.End(); return; }

        if (AssetCatalog.Roots.Count == 0)
        {
            ImGui.TextDisabled("This port hasn't registered any assets.");
            ImGui.TextWrapped("A game host registers roots at startup with AssetCatalog.Register(node); " +
                              "see games/ParasiteEve/ParasiteEve.PC/AssetSources.cs.");
            IsOpen = open; ImGui.End();
            return;
        }

        float leftW = Math.Max(220f, ImGui.GetContentRegionAvail().X * 0.36f);
        ImGui.BeginChild("tree", new Vector2(leftW, 0), ImGuiChildFlags.Border);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##filter", "filter (this level)", ref _filter, 128);
        foreach (var root in AssetCatalog.Roots) DrawNode(root, 0);
        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.BeginChild("detail", Vector2.Zero, ImGuiChildFlags.Border);
        DrawDetail();
        ImGui.EndChild();

        IsOpen = open;
        ImGui.End();
    }

    void DrawNode(AssetNode n, int depth)
    {
        if (depth > 0 && _filter.Length > 0 && !n.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)
            && !(n.Summary?.Contains(_filter, StringComparison.OrdinalIgnoreCase) ?? false))
            return;
        var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth;
        if (!n.HasChildren) flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;
        if (ReferenceEquals(n, _selected)) flags |= ImGuiTreeNodeFlags.Selected;
        string label = n.Summary == null ? n.Name : $"{n.Name}  ";
        bool opened = ImGui.TreeNodeEx(n.Name + "##" + n.GetHashCode(), flags, label);
        if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen()) Select(n);
        if (n.Summary != null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(n.Summary);
        }
        if (opened && n.HasChildren)
        {
            foreach (var c in n.GetChildren()) DrawNode(c, depth + 1);
            ImGui.TreePop();
        }
    }

    void Select(AssetNode n)
    {
        _selected = n;
        _bytes = null;
        _image = null;
        _imageUploaded = false;
        _status = "";
        _view.Reset();
        try { _bytes = n.Bytes?.Invoke(); } catch (Exception e) { _status = "bytes: " + e.Message; }
        try { _image = n.Image?.Invoke(); } catch (Exception e) { _status = "image: " + e.Message; }
    }

    void DrawDetail()
    {
        if (_selected == null) { ImGui.TextDisabled("Select a node."); return; }
        var n = _selected;
        ImGui.TextUnformatted(n.Name);
        if (n.Summary != null) ImGui.TextDisabled(n.Summary);
        ImGui.Separator();

        if (n.Details != null && ImGui.BeginTable("details", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
        {
            IEnumerable<(string, string)> rows;
            try { rows = n.Details().ToList(); } catch (Exception e) { rows = [("error", e.Message)]; }
            foreach (var (k, v) in rows)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0); ImGui.TextDisabled(k);
                ImGui.TableSetColumnIndex(1); ImGui.TextWrapped(v);
            }
            ImGui.EndTable();
        }

        if (_bytes != null)
        {
            ImGui.Separator();
            ImGui.Text($"{_bytes.Length} bytes");
            ImGui.SameLine();
            if (ImGui.Button("Export bytes")) Export(n, _bytes);
            if (_status.Length > 0) { ImGui.SameLine(); ImGui.TextDisabled(_status); }
        }
        else if (_status.Length > 0) ImGui.TextDisabled(_status);

        if (_image is { } img)
        {
            ImGui.Separator();
            if (!_imageUploaded)
            {
                _tex = HostWindow.UploadRgba(_tex, img.W, img.H, img.Rgba);
                _texW = img.W; _texH = img.H;
                _imageUploaded = _tex != 0;
            }
            if (_imageUploaded)
            {
                ImGui.Text($"image {img.W}x{img.H}  (wheel = zoom, drag = pan)");
                float h = Math.Max(120f, Math.Min(ImGui.GetContentRegionAvail().Y * 0.6f, img.H * 2f + 8));
                ImGui.BeginChild("img", new Vector2(0, h), ImGuiChildFlags.Border);
                _view.Draw(_tex, _texW, _texH);
                ImGui.EndChild();
            }
        }

        if (_bytes != null)
        {
            ImGui.Separator();
            int rows = Math.Min(_hexRows, (_bytes.Length + 15) / 16);
            ImGui.BeginChild("hex", Vector2.Zero, ImGuiChildFlags.Border);
            var sb = new StringBuilder(80);
            for (int r = 0; r < rows; r++)
            {
                sb.Clear();
                int o = r * 16;
                sb.Append(o.ToString("X6")).Append("  ");
                for (int i = 0; i < 16; i++)
                {
                    if (o + i < _bytes.Length) sb.Append(_bytes[o + i].ToString("X2")).Append(' ');
                    else sb.Append("   ");
                    if (i == 7) sb.Append(' ');
                }
                sb.Append(' ');
                for (int i = 0; i < 16 && o + i < _bytes.Length; i++)
                {
                    byte b = _bytes[o + i];
                    sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
                }
                ImGui.TextUnformatted(sb.ToString());
            }
            if (rows * 16 < _bytes.Length && ImGui.SmallButton($"more ({_bytes.Length - rows * 16} bytes hidden)")) _hexRows += 256;
            ImGui.EndChild();
        }
    }

    void Export(AssetNode n, byte[] bytes)
    {
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "assets_export");
            Directory.CreateDirectory(dir);
            string file = n.ExportName ?? SafeName(n.Name) + ".bin";
            string path = Path.Combine(dir, file);
            File.WriteAllBytes(path, bytes);
            _status = "wrote " + path;
            Console.WriteLine("[assets] exported " + path);
        }
        catch (Exception e) { _status = "export failed: " + e.Message; }
    }

    static string SafeName(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        return sb.ToString();
    }
}
