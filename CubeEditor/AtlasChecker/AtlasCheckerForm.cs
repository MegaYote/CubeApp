using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace AtlasChecker
{
    /// <summary>
    /// Standalone viewer for the 16x16 tile atlases (terrain.png / items.png).
    ///
    /// CubeEditor already has an AtlasPickerDialog, but it is only reachable from inside the
    /// block-edit modal, runs at a fixed 2x with no coordinate labels, and has no notion of
    /// which tiles are actually referenced by blocks.json/items.json. This one is meant to be
    /// launched directly whenever you need to answer "where does tile 4,12 live and what uses
    /// it?":
    ///
    ///   * every tile is labelled with its col,row
    ///   * completely empty tiles (zero non-transparent pixels) are dimmed, so free atlas space
    ///     is obvious at a glance
    ///   * tiles referenced by the data files are outlined and listed in the side panel
    ///   * the oak door pair is called out explicitly and the view scrolls to it on launch
    /// </summary>
    public sealed class AtlasCheckerForm : Form
    {
        private const int Tile = 16;
        private const int Grid = 16; // 16x16 tiles

        private readonly string _root;
        private readonly Panel _canvasHost;
        private readonly AtlasView _view;
        private readonly ComboBox _cmbAtlas;
        private readonly ComboBox _cmbZoom;
        private readonly CheckBox _chkShowLabels;
        private readonly CheckBox _chkDimEmpty;
        private readonly ListBox _lstUsed;
        private readonly Label _lblInfo;

        public AtlasCheckerForm(string root)
        {
            _root = root;

            Text = "Atlas Coordinate Checker";
            Width = 1180;
            Height = 860;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(900, 600);
            KeyPreview = true;

            _cmbAtlas = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
            _cmbAtlas.Items.AddRange(new object[] { "terrain.png", "items.png" });
            _cmbAtlas.SelectedIndex = 0;

            _cmbZoom = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70 };
            foreach (int z in new[] { 1, 2, 3, 4, 6, 8 }) _cmbZoom.Items.Add(z);
            _cmbZoom.SelectedItem = 4;

            _chkShowLabels = new CheckBox { Text = "Labels", Checked = true, AutoSize = true };
            _chkDimEmpty = new CheckBox { Text = "Dim empty", Checked = true, AutoSize = true };

            var btnDoor = new Button { Text = "Go to door tiles", AutoSize = true };
            btnDoor.Click += (s, e) =>
            {
                _view.ZoomTo(3, 12);
_view.ScrollToTile(12, 4);
            };

            var btnReload = new Button { Text = "Reload data", AutoSize = true };
            btnReload.Click += (s, e) => _view.Reload();

            var topBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 34,
                Padding = new Padding(6, 4, 0, 0),
                WrapContents = false
            };
            topBar.Controls.Add(new Label { Text = "Atlas:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
            topBar.Controls.Add(_cmbAtlas);
            topBar.Controls.Add(new Label { Text = "Zoom:", AutoSize = true, Margin = new Padding(12, 6, 4, 0) });
            topBar.Controls.Add(_cmbZoom);
            topBar.Controls.Add(_chkShowLabels);
            topBar.Controls.Add(_chkDimEmpty);
            topBar.Controls.Add(btnDoor);
            topBar.Controls.Add(btnReload);

            _lstUsed = new ListBox
            {
                Dock = DockStyle.Fill,
                IntegralHeight = false,
                Font = new Font("Consolas", 9f)
            };
            _lblInfo = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 62,
                Padding = new Padding(6, 4, 0, 0),
                Font = new Font("Consolas", 9f)
            };

            var sidePanel = new Panel { Dock = DockStyle.Right, Width = 330, Padding = new Padding(6) };
            sidePanel.Controls.Add(_lstUsed);
            var sideHeader = new Label
            {
                Dock = DockStyle.Top,
                Height = 34,
                Text = "Tiles in use (col,row -> ids)",
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            sidePanel.Controls.Add(sideHeader);
            sidePanel.Controls.Add(_lblInfo);

            _canvasHost = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(32, 32, 32) };
            _view = new AtlasView(_root) { Dock = DockStyle.None };
            _canvasHost.Controls.Add(_view);
            _view.TilePicked += OnTilePicked;

            _cmbAtlas.SelectedIndexChanged += (s, e) => { _view.Load(_cmbAtlas.SelectedItem?.ToString() ?? "terrain.png"); RefreshUsedList(); };
            _cmbZoom.SelectedIndexChanged += (s, e) => { _view.Zoom = Convert.ToInt32(_cmbZoom.SelectedItem ?? 4); };
            _chkShowLabels.CheckedChanged += (s, e) => _view.ShowLabels = _chkShowLabels.Checked;
            _chkDimEmpty.CheckedChanged += (s, e) => _view.DimEmpty = _chkDimEmpty.Checked;
            _lstUsed.SelectedIndexChanged += (s, e) =>
            {
                if (_lstUsed.SelectedItem is TileUse u) _view.ScrollToTile(u.Col, u.Row);
            };

            Controls.Add(_canvasHost);
            Controls.Add(sidePanel);
            Controls.Add(topBar);

            _view.Load("terrain.png");
            _view.Zoom = 4;
            _view.ShowLabels = true;
            _view.DimEmpty = true;

            RefreshUsedList();

            // Land the view on the door tiles - that's the reason this tool usually gets opened.
            Shown += (s, e) => { _view.ZoomTo(3, 12); _view.ScrollToTile(4, 12); };
        }

        private void RefreshUsedList()
        {
            _lstUsed.BeginUpdate();
            _lstUsed.Items.Clear();
            foreach (var u in _view.Uses.OrderBy(u => u.Col).ThenBy(u => u.Row))
            {
                bool isDoor = u.Ids.Any(i => i.Contains("door", StringComparison.OrdinalIgnoreCase));
                _lstUsed.Items.Add(new TileUse(u.Col, u.Row, u.Ids) { IsDoor = isDoor });
            }
            _lstUsed.EndUpdate();
        }

        private void OnTilePicked(int col, int row)
        {
            var uses = _view.Uses.Where(u => u.Col == col && u.Row == row).ToList();
            string users = uses.Count == 0 ? "(nothing references this tile)"
                        : string.Join("  |  ", uses.Select(u => string.Join(", ", u.Ids)));

            bool empty = _view.IsEmptyTile(col, row);
            _lblInfo.Text =
                $"tile ({col},{row})   px ({col * Tile},{row * Tile})-({col * Tile + Tile - 1},{row * Tile + Tile - 1})" +
                $"{(empty ? "   [EMPTY]" : "")}\r\n{users}";
        }
    }

    internal sealed class TileUse
    {
        public int Col { get; }
        public int Row { get; }
        public List<string> Ids { get; }
        public bool IsDoor { get; set; }
        public TileUse(int col, int row, List<string> ids) { Col = col; Row = row; Ids = ids; }
        public override string ToString()
            => $"{Col},{Row}{(IsDoor ? "  <-- DOOR" : "")}".PadRight(16)
             + (Ids.Count > 0 ? string.Join(", ", Ids) : "(free)");
    }

    /// <summary>Scrollable, zoomable, coordinate-labelled atlas renderer.</summary>
    internal sealed class AtlasView : Control
    {
        private const int Tile = 16;
        private const int Grid = 16;

        private static readonly Color EmptyDim = Color.FromArgb(90, 0, 0, 0);
        private static readonly Color UsedLine = Color.FromArgb(150, 120, 200, 255);
        private static readonly Color DoorLine = Color.FromArgb(255, 60, 60);
        private static readonly Color HoverLine = Color.FromArgb(255, 255, 120);

        private readonly string _root;
        private Bitmap? _atlas;
        private Bitmap? _scaled;
        private readonly bool[,] _empty = new bool[Grid, Grid];
        private readonly HashSet<int> _used = new HashSet<int>();
        private readonly List<TileUse> _uses = new();

        private int _hoverCol = -1, _hoverRow = -1;
        private int _selectedCol = -1, _selectedRow = -1;

        /// <summary>Tiles belonging to the oak door pair - called out with a red outline.</summary>
        private static readonly (int Col, int Row)[] DoorTiles = { (12, 4), (12, 3) };

        // Runtime view options. Public fields rather than properties: WinForms treats a settable
        // property on a Control as designer-persisted state and flags it (WFO1000).
        public int Zoom = 4;
        public bool ShowLabels = true;
        public bool DimEmpty = true;
        public IReadOnlyList<TileUse> Uses => _uses;

        public event Action<int, int>? TilePicked;

        public AtlasView(string root)
        {
            _root = root;
            DoubleBuffered = true;
            ResizeRedraw = true;
            SetStyle(ControlStyles.Selectable, true);
        }

        public void Load(string atlasName)
        {
            string path = Path.Combine(_root, "Assets", "Textures", atlasName);
            _atlas?.Dispose();
            _scaled?.Dispose();
            _atlas = null;
            _scaled = null;
            if (!File.Exists(path)) return;

            // Detach from the file so the PNG isn't locked (the game also reads it).
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var tmp = new Bitmap(fs);
            _atlas = new Bitmap(tmp);

            ScanEmptiness();
            Rescale();
            ParseData(atlasName);
            Size = new Size(_atlas.Width * Zoom + 1, _atlas.Height * Zoom + 1);
            Invalidate();
        }

        public void Reload() => Load(_atlasName);

        private string _atlasName = "terrain.png";

        private void Rescale()
        {
            if (_atlas == null) return;
            _scaled?.Dispose();
            int w = _atlas.Width * Zoom, h = _atlas.Height * Zoom;
            _scaled = new Bitmap(w, h);
            using var g = Graphics.FromImage(_scaled);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(_atlas, new Rectangle(0, 0, w, h), new Rectangle(0, 0, _atlas.Width, _atlas.Height), GraphicsUnit.Pixel);
        }

        /// <summary>A tile counts as empty when it has (almost) no non-transparent pixels.</summary>
        private void ScanEmptiness()
        {
            if (_atlas == null) return;
            for (int r = 0; r < Grid; r++)
            for (int c = 0; c < Grid; c++)
            {
                int opaque = 0;
                for (int y = 0; y < Tile; y++)
                for (int x = 0; x < Tile; x++)
                {
                    if (_atlas!.GetPixel(c * Tile + x, r * Tile + y).A > 8) opaque++;
                }
                _empty[c, r] = opaque <= 4;
            }
        }

        public bool IsEmptyTile(int col, int row)
            => col >= 0 && col < Grid && row >= 0 && row < Grid && _empty[col, row];

        /// <summary>
        /// Collects every tile referenced by blocks.json / items.json, in both the Cubuild and
        /// Minecraft data sets, so the panel answers "what uses this tile?" without guesswork.
        /// </summary>
        private void ParseData(string atlasName)
        {
            _used.Clear();
            _uses.Clear();

            string dataDir = atlasName.StartsWith("items", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(_root, "Assets", "Data")
                : Path.Combine(_root, "Assets", "Data");

            var files = new List<(string label, string path)>();
            foreach (string rel in new[]
            {
                Path.Combine("blocks.json"),
                Path.Combine("items.json"),
                Path.Combine("Minecraft", "blocks.json"),
                Path.Combine("Minecraft", "items.json"),
            })
            {
                string p = Path.Combine(dataDir, rel);
                if (File.Exists(p)) files.Add(($"{rel}", p));
            }

            var byTile = new Dictionary<int, TileUse>();

            foreach (var (label, path) in files)
            {
                string json;
                try { json = File.ReadAllText(path); }
                catch { continue; }
                json = StripComments(json);

                JsonNode? root;
                try { root = JsonNode.Parse(json); }
                catch { continue; }
                if (root is not JsonObject obj) continue;

                foreach (string section in new[] { "blocks", "items" })
                {
                    if (!obj.TryGetPropertyValue(section, out var node) || node is not JsonArray arr) continue;
                    foreach (var entry in arr)
                    {
                        if (entry is not JsonObject item) continue;
                        string id = GetStr(item, "id");
                        if (string.IsNullOrEmpty(id)) continue;

                        foreach (string key in new[] { "texture", "top", "bottom", "side", "itemTile" })
                        {
                            string v = GetStr(item, key);
                            if (string.IsNullOrEmpty(v)) continue;
                            var parts = v.Split(',');
                            if (parts.Length != 2) continue;
                            if (!int.TryParse(parts[0].Trim(), out int col) || !int.TryParse(parts[1].Trim(), out int row)) continue;
                            if (col < 0 || col >= Grid || row < 0 || row >= Grid) continue;

                            int key2 = row * Grid + col;
                            _used.Add(key2);
                            if (!byTile.TryGetValue(key2, out var use))
                            {
                                use = new TileUse(col, row, new List<string>());
                                byTile[key2] = use;
                                _uses.Add(use);
                            }
                            string tag = id + (key == "texture" ? "" : $" ({key})");
                            if (!use.Ids.Contains(tag)) use.Ids.Add(tag);
                        }
                    }
                }
            }

            _uses.Sort((a, b) => a.Col != b.Col ? a.Col.CompareTo(b.Col) : a.Row.CompareTo(b.Row));
        }

        private static string StripComments(string json)
        {
            var noLine = Regex.Replace(json, @"(?m)//.*?$", "");
            var noBlock = Regex.Replace(noLine, @"/\*.*?\*/", "", RegexOptions.Singleline);
            return Regex.Replace(noBlock, @",\s*([}\]])", "$1");
        }

        private static string GetStr(JsonObject o, string key)
        {
            if (!o.TryGetPropertyValue(key, out var n) || n == null) return "";
            if (n is JsonValue v)
            {
                try { return v.GetValue<string>() ?? ""; } catch { return v.ToString().Trim('"'); }
            }
            return n.ToString().Trim('"');
        }

        public void ZoomTo(int col, int row)
        {
            // scroll so the tile is comfortably visible; caller usually follows with ScrollToTile
            if (Parent is Panel p)
            {
                int s = Tile * Zoom;
                p.AutoScrollPosition = new Point(
                    Math.Max(0, col * s - p.ClientSize.Width / 2 + s / 2),
                    Math.Max(0, row * s - p.ClientSize.Height / 2 + s / 2));
            }
        }

        public void ScrollToTile(int col, int row)
        {
            if (Parent is not Panel p) return;
            int s = Tile * Zoom;
            p.AutoScrollPosition = new Point(
                Math.Max(0, col * s - p.ClientSize.Width / 2 + s / 2),
                Math.Max(0, row * s - p.ClientSize.Height / 2 + s / 2));
            Invalidate();
        }

        private int ScaledTile => Tile * Zoom;

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int c = Math.Clamp(e.X / ScaledTile, 0, Grid - 1);
            int r = Math.Clamp(e.Y / ScaledTile, 0, Grid - 1);
            if (c != _hoverCol || r != _hoverRow) { _hoverCol = c; _hoverRow = r; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hoverCol = _hoverRow = -1;
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            _selectedCol = Math.Clamp(e.X / ScaledTile, 0, Grid - 1);
            _selectedRow = Math.Clamp(e.Y / ScaledTile, 0, Grid - 1);
            TilePicked?.Invoke(_selectedCol, _selectedRow);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Color.FromArgb(32, 32, 32));
            if (_scaled == null) return;

            int s = ScaledTile;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(_scaled, new Rectangle(0, 0, _scaled.Width, _scaled.Height));

            // Dim empty tiles so free atlas space reads at a glance.
            if (DimEmpty)
            {
                using var dim = new SolidBrush(EmptyDim);
                for (int r = 0; r < Grid; r++)
                for (int c = 0; c < Grid; c++)
                {
                    if (_empty[c, r]) g.FillRectangle(dim, c * s, r * s, s, s);
                }
            }

            // Tile grid.
            using (var pen = new Pen(Color.FromArgb(90, 255, 255, 255)))
            {
                for (int c = 0; c <= Grid; c++) g.DrawLine(pen, c * s, 0, c * s, Grid * s);
                for (int r = 0; r <= Grid; r++) g.DrawLine(pen, 0, r * s, Grid * s, r * s);
            }

            // Outline tiles that are referenced by the data files.
            using (var pen = new Pen(UsedLine))
            {
                foreach (var key in _used)
                {
                    int c = key % Grid, r = key / Grid;
                    g.DrawRectangle(pen, c * s + 1, r * s + 1, s - 2, s - 2);
                }
            }

            // The oak door pair, called out loudly.
            using (var pen = new Pen(DoorLine, 3))
            {
                foreach (var (c, r) in DoorTiles)
                    g.DrawRectangle(pen, c * s + 2, r * s + 2, s - 4, s - 4);
            }

            if (ShowLabels)
            {
                using var font = new Font("Consolas", Math.Max(7f, Math.Min(11f, Zoom * 1.6f)), FontStyle.Bold);
                using var shadow = new SolidBrush(Color.FromArgb(190, 0, 0, 0));
                using var ink = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
                using var doorInk = new SolidBrush(Color.FromArgb(255, 255, 210));
                var fmt = new StringFormat
                {
                    Alignment = StringAlignment.Near,
                    LineAlignment = StringAlignment.Near
                };
                for (int r = 0; r < Grid; r++)
                for (int c = 0; c < Grid; c++)
                {
                    string label = $"{c},{r}";
                    var box = new RectangleF(c * s + 2, r * s + 1, s - 4, s - 4);
                    g.DrawString(label, font, shadow, box, fmt);
                    bool isDoor = Array.Exists(DoorTiles, t => t.Col == c && t.Row == r);
                    g.DrawString(label, font, isDoor ? doorInk : ink, box, fmt);
                }
            }

            // Hover / selection crosshair.
            if (_hoverCol >= 0)
            {
                using var pen = new Pen(HoverLine, 2);
                g.DrawRectangle(pen, _hoverCol * s + 1, _hoverRow * s + 1, s - 2, s - 2);
            }
            if (_selectedCol >= 0)
            {
                using var pen = new Pen(Color.FromArgb(255, 120, 255, 120), 2);
                g.DrawRectangle(pen, _selectedCol * s + 1, _selectedRow * s + 1, s - 2, s - 2);
            }
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            ApplicationConfiguration.Initialize();

            // Default to the CubeApp folder next to this exe, but allow an explicit override.
            string root = Environment.GetEnvironmentVariable("CUBUILD_ROOT") ?? DefaultRoot();
            if (!Directory.Exists(Path.Combine(root, "Assets", "Textures")))
            {
                MessageBox.Show(
                    $"Could not find the Assets\\Textures folder under:\n{root}\n\n" +
                    "Set the CUBUILD_ROOT environment variable to your CubeApp folder and try again.",
                    "Atlas Coordinate Checker", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Application.Run(new AtlasCheckerForm(root));
        }

        private static string DefaultRoot()
        {
            // .../CubeEditor/AtlasChecker/bin/Release/net9.0-windows/  ->  up to CubeApp
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "Assets", "Textures"))
                    && File.Exists(Path.Combine(dir.FullName, "Assets", "Data", "blocks.json")))
                {
                    return dir.FullName;
                }
            }
            return Environment.CurrentDirectory;
        }
    }
}