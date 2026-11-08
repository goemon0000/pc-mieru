using System.Text.Json.Nodes;

namespace PcMieru.Gui;

/// <summary>
/// PC part information as a tree, built from the JSON form of the model so that it shows whatever the model holds
/// (a Value that was not collected shows its status, never a blank). A stand-in until the parts view is designed.
/// </summary>
internal sealed class HardwarePage : PageBase
{
    private readonly TreeView _tree = new() { Name = "hardwareTree", Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
    private readonly Button _refresh = new() { AutoSize = true, Name = "hardwareRefresh" };
    private readonly Label _state = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private bool _loaded;

    public HardwarePage(GuiContext context)
        : base(context, "hardware", "gui.nav.hardware")
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(Px(8)) };
        bar.Controls.Add(_refresh);
        bar.Controls.Add(_state);
        _refresh.Click += (_, _) => Run(() => LoadAsync(invalidate: true));
        Controls.Add(_tree);
        Controls.Add(bar);
        ApplyLanguage();
    }

    public override void OnActivated()
    {
        if (!_loaded)
        {
            Run(() => LoadAsync(invalidate: false));
        }
    }

    public override void ApplyLanguage()
    {
        _refresh.Text = Strings.Get("gui.hw.refresh");
        _tree.AccessibleName = Strings.Get("gui.nav.hardware");
        if (_loaded)
        {
            Run(() => LoadAsync(invalidate: false));
        }
    }

    private async Task LoadAsync(bool invalidate)
    {
        _state.Text = Strings.Get("gui.hw.loading");
        if (invalidate)
        {
            Context.Services.Hardware.Invalidate();
        }

        var info = await Context.Services.Hardware.GetAsync(PcMieru.Core.Hardware.Model.HardwareCategory.All).ConfigureAwait(true);
        var node = PcMieruJson.ToNode(info);
        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        if (node is JsonObject root)
        {
            foreach (var (key, value) in root.Where(p => p.Key is not ("schemaVersion" or "collectedAtUtc")))
            {
                JsonTreeBuilder.Add(_tree.Nodes, key, value);
            }
        }

        foreach (TreeNode top in _tree.Nodes)
        {
            top.Expand();
        }

        _tree.EndUpdate();
        _loaded = true;
        _state.Text = string.Empty;
    }
}

/// <summary>Turns the JSON form of a model into tree nodes. A {status, source, value} object is one Value: shown inline.</summary>
internal static class JsonTreeBuilder
{
    public static void Add(TreeNodeCollection into, string name, JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj when IsValue(obj):
                AddValue(into, name, obj);
                break;
            case JsonObject obj:
                var branch = into.Add(name);
                foreach (var (key, child) in obj)
                {
                    Add(branch.Nodes, key, child);
                }

                break;
            case JsonArray array:
                var list = into.Add(name);
                for (var i = 0; i < array.Count; i++)
                {
                    Add(list.Nodes, "#" + (i + 1), array[i]);
                }

                break;
            default:
                into.Add(name + ": " + (node?.ToString() ?? Fmt.Dash));
                break;
        }
    }

    public static bool IsValue(JsonObject obj) => obj.ContainsKey("status") && obj.ContainsKey("source");

    private static void AddValue(TreeNodeCollection into, string name, JsonObject value)
    {
        var status = value["status"]?.GetValue<string>() ?? "error";
        if (status == "ok" && value["value"] is { } inner)
        {
            if (inner is JsonObject or JsonArray)
            {
                Add(into, name, inner);
            }
            else
            {
                into.Add(name + ": " + inner);
            }

            return;
        }

        into.Add(name + ": " + StatusText(status));
    }

    private static string StatusText(string status) =>
        Enum.TryParse<ValueStatus>(status, ignoreCase: true, out var parsed) ? Strings.Status(parsed) : status;
}
