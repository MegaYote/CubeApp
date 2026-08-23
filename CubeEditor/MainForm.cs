using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace CubeEditor
{
    // ------------------------------------------------------------------------
    // Atlas picker: shows terrain.png or items.png scaled 2x with 16x16 grid
    // ------------------------------------------------------------------------
    public class AtlasPickerDialog : Form
    {
        public int SelectedCol { get; private set; }
        public int SelectedRow { get; private set; }

        private readonly Image _atlas;
        private readonly PictureBox _pb;
        private readonly Label _lblHover;
        private readonly Label _lblSelected;
        private readonly Bitmap _scaled;
        private const int Tile = 16;
        private const int AtlasScale = 2;
        private const int ScaledTile = Tile * AtlasScale;

        public AtlasPickerDialog(Image atlas, int initialCol, int initialRow, string title = "Pick Tile")
        {
            _atlas = atlas;
            SelectedCol = Math.Clamp(initialCol, 0, 15);
            SelectedRow = Math.Clamp(initialRow, 0, 15);
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;

            int w = atlas.Width * AtlasScale;
            int h = atlas.Height * AtlasScale;

            // Create scaled bitmap (nearest neighbour)
            _scaled = new Bitmap(w, h);
            using (var g = Graphics.FromImage(_scaled))
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(atlas, new Rectangle(0, 0, w, h), new Rectangle(0, 0, atlas.Width, atlas.Height), GraphicsUnit.Pixel);
            }

            _pb = new PictureBox
            {
                Image = _scaled,
                Size = new Size(w, h),
                Location = new Point(8, 8),
                BorderStyle = BorderStyle.FixedSingle
            };
            _lblHover = new Label { AutoSize = true, Location = new Point(8, h + 14), Text = "hover: -" };
            _lblSelected = new Label { AutoSize = true, Location = new Point(120, h + 14), Text = $"selected: {SelectedCol},{SelectedRow}" };

            _pb.Paint += Pb_Paint;
            _pb.MouseMove += Pb_MouseMove;
            _pb.MouseClick += Pb_MouseClick;
            _pb.MouseLeave += (s, e) => _lblHover.Text = "hover: -";

            var btnOk = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(w - 170, h + 34), Size = new Size(75, 23) };
            var btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(w - 85, h + 34), Size = new Size(75, 23) };
            btnOk.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };

            ClientSize = new Size(w + 16, h + 65);
            Controls.Add(_pb);
            Controls.Add(_lblHover);
            Controls.Add(_lblSelected);
            Controls.Add(btnOk);
            Controls.Add(btnCancel);
            AcceptButton = btnOk;
            CancelButton = btnCancel;
        }

        private void Pb_Paint(object? sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            using var penGrid = new Pen(Color.FromArgb(120, Color.White), 1);
            using var penSel = new Pen(Color.Red, 2);
            // grid 16x16 tiles
            for (int c = 1; c < 16; c++)
            {
                int x = c * ScaledTile;
                g.DrawLine(penGrid, x, 0, x, _pb.Height);
            }
            for (int r = 1; r < 16; r++)
            {
                int y = r * ScaledTile;
                g.DrawLine(penGrid, 0, y, _pb.Width, y);
            }
            // selected rect
            var sel = new Rectangle(SelectedCol * ScaledTile, SelectedRow * ScaledTile, ScaledTile, ScaledTile);
            g.DrawRectangle(penSel, sel);
            // also semi-transparent fill
            using var br = new SolidBrush(Color.FromArgb(60, Color.Yellow));
            g.FillRectangle(br, sel);
        }

        private void Pb_MouseMove(object? sender, MouseEventArgs e)
        {
            int c = Math.Clamp(e.X / ScaledTile, 0, 15);
            int r = Math.Clamp(e.Y / ScaledTile, 0, 15);
            _lblHover.Text = $"hover: {c},{r}";
        }

        private void Pb_MouseClick(object? sender, MouseEventArgs e)
        {
            int c = Math.Clamp(e.X / ScaledTile, 0, 15);
            int r = Math.Clamp(e.Y / ScaledTile, 0, 15);
            SelectedCol = c;
            SelectedRow = r;
            _lblSelected.Text = $"selected: {SelectedCol},{SelectedRow}";
            _pb.Invalidate();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            // _scaled disposed with PictureBox? Keep alive until close
        }
    }

    // ------------------------------------------------------------------------
    // Main editor
    // ------------------------------------------------------------------------
    public class MainForm : Form
    {
        private readonly string _blocksPath = @"C:\Users\jimza\CubeApp\Assets\Data\blocks.json";
        private readonly string _itemsPath = @"C:\Users\jimza\CubeApp\Assets\Data\items.json";
        private readonly string _terrainPath = @"C:\Users\jimza\CubeApp\Assets\Textures\terrain.png";
        private readonly string _itemsTexPath = @"C:\Users\jimza\CubeApp\Assets\Textures\items.png";

        private Image? _terrainImg;
        private Image? _itemsImg;

        private List<JsonObject> _blocks = new();
        private List<string> _hotbar = new();
        private List<JsonObject> _items = new();

        private bool _suppress = false;
        private int _curBlock = -1;
        private int _curItem = -1;

        // --- Blocks UI ---
        private ListBox _lstBlocks = null!;
        private TextBox _txtBId = null!;
        private TextBox _txtBDisplay = null!;
        private ComboBox _cmbBShape = null!;
        private ComboBox _cmbBToolType = null!;
        private ComboBox _cmbBZombieSpeed = null!;
        private ComboBox _cmbBBase = null!;
        private NumericUpDown _numBHardness = null!;
        private NumericUpDown _numBLight = null!;
        private TextBox _txtBMapColor = null!;
        private Button _btnBMapPick = null!;
        private CheckBox _chkBSolid = null!;
        private CheckBox _chkBOpaque = null!;
        private CheckBox _chkBTransparent = null!;
        private CheckBox _chkBTranslucent = null!;
        private CheckBox _chkBInventory = null!;
        private CheckBox _chkBPlaceable = null!;
        private CheckBox _chkBGravity = null!;
        private CheckBox _chkBToolReq = null!;
        private CheckBox _chkBZombieBreak = null!;

        private class TileSet
        {
            public NumericUpDown Col = null!;
            public NumericUpDown Row = null!;
            public PictureBox Preview = null!;
            public Button Pick = null!;
        }
        private TileSet _tileBTexture = null!;
        private TileSet _tileBTop = null!;
        private TileSet _tileBBottom = null!;
        private TileSet _tileBSide = null!;
        private TileSet _tileBItemTile = null!;

        // --- Items UI ---
        private ListBox _lstItems = null!;
        private TextBox _txtIId = null!;
        private TextBox _txtIDisplay = null!;
        private ComboBox _cmbICategory = null!;
        private ComboBox _cmbIPlacedBlock = null!;
        private ComboBox _cmbIToolType = null!;
        private NumericUpDown _numIStack = null!;
        private NumericUpDown _numIToolLevel = null!;
        private NumericUpDown _numIDurability = null!;
        private NumericUpDown _numIFood = null!;
        private TextBox _txtIMapColor = null!;
        private Button _btnIMapPick = null!;
        private TileSet _tileIItemTile = null!;

        public MainForm()
        {
            Text = "Cube Editor";
            Size = new Size(1050, 750);
            MinimumSize = new Size(900, 650);
            StartPosition = FormStartPosition.CenterScreen;

            BuildUI();
            LoadImages();
            LoadBlocks();
            LoadItems();
            RefreshBlockList();
            RefreshItemList();
            RefreshBaseCombos();
            RefreshPlacedBlockCombos();
        }

        // ====================================================================
        // UI building
        // ====================================================================
        private void BuildUI()
        {
            var tab = new TabControl { Dock = DockStyle.Fill };
            var tabBlocks = new TabPage("Blocks");
            var tabItems = new TabPage("Items");
            tab.TabPages.Add(tabBlocks);
            tab.TabPages.Add(tabItems);
            Controls.Add(tab);

            BuildBlocksTab(tabBlocks);
            BuildItemsTab(tabItems);
        }

        private void BuildBlocksTab(TabPage page)
        {
            var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 280, FixedPanel = FixedPanel.Panel1 };
            page.Controls.Add(split);

            // Left - list fills, buttons docked at top (added after so Fill sits below Top)
            var leftTableB = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
            leftTableB.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            leftTableB.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            split.Panel1.Controls.Add(leftTableB);

            var pnlLeftBtns = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Padding = new Padding(4) };
            leftTableB.Controls.Add(pnlLeftBtns, 0, 0);

            _lstBlocks = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            _lstBlocks.SelectedIndexChanged += (s, e) => OnBlockSelected();
            leftTableB.Controls.Add(_lstBlocks, 0, 1);
            var btnBNew = new Button { Text = "New", Width = 58 };
            var btnBDup = new Button { Text = "Duplicate", Width = 66 };
            var btnBDel = new Button { Text = "Delete", Width = 58 };
            var btnBSave = new Button { Text = "Save Blocks", Width = 80 };
            var btnBReload = new Button { Text = "Reload", Width = 58 };
            btnBNew.Click += (s, e) => BlockNew();
            btnBDup.Click += (s, e) => BlockDuplicate();
            btnBDel.Click += (s, e) => BlockDelete();
            btnBSave.Click += (s, e) => SaveBlocks();
            btnBReload.Click += (s, e) => { LoadBlocks(); RefreshBlockList(); RefreshBaseCombos(); RefreshPlacedBlockCombos(); };
            pnlLeftBtns.Controls.Add(btnBNew);
            pnlLeftBtns.Controls.Add(btnBDup);
            pnlLeftBtns.Controls.Add(btnBDel);
            pnlLeftBtns.Controls.Add(btnBSave);
            pnlLeftBtns.Controls.Add(btnBReload);

            // Right - scrollable
            var right = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(8) };
            split.Panel2.Controls.Add(right);

            int y = 8;
            // warning label
            var lblWarn = new Label { Text = "NOTE: changing id renames the block. Numeric id = list position (NEVER reorder middle).", AutoSize = true, ForeColor = Color.DarkRed, Location = new Point(6, y) };
            right.Controls.Add(lblWarn);
            y += 22;

            // id
            AddLabel(right, "id:", 6, y + 3);
            _txtBId = new TextBox { Location = new Point(130, y), Width = 200 };
            _txtBId.TextChanged += (s, e) => { if (_suppress || _curBlock < 0) return; var v = _txtBId.Text.Trim(); _blocks[_curBlock]["id"] = JsonValue.Create(v); // update listbox item
                if (_curBlock >= 0 && _curBlock < _lstBlocks.Items.Count) { _suppress = true; _lstBlocks.Items[_curBlock] = v; _suppress = false; }
                RefreshBaseCombos(); RefreshPlacedBlockCombos();
            };
            right.Controls.Add(_txtBId);
            var lblNum = new Label { Text = "(numeric id = position)", AutoSize = true, Location = new Point(340, y + 3), ForeColor = Color.Gray };
            right.Controls.Add(lblNum);
            y += 28;

            // displayName
            AddLabel(right, "displayName:", 6, y + 3);
            _txtBDisplay = new TextBox { Location = new Point(130, y), Width = 200 };
            _txtBDisplay.TextChanged += (s, e) => { if (_suppress || _curBlock < 0) return; SetStr(_blocks[_curBlock], "displayName", _txtBDisplay.Text); };
            right.Controls.Add(_txtBDisplay);
            y += 28;

            // tile fields
            _tileBTexture = AddTileField(right, ref y, "texture:", GetTerrainImg, OnBlockTileChanged);
            _tileBTop = AddTileField(right, ref y, "top:", GetTerrainImg, OnBlockTileChanged);
            _tileBBottom = AddTileField(right, ref y, "bottom:", GetTerrainImg, OnBlockTileChanged);
            _tileBSide = AddTileField(right, ref y, "side:", GetTerrainImg, OnBlockTileChanged);
            _tileBItemTile = AddTileField(right, ref y, "itemTile:", GetItemTileImgForBlock, OnBlockTileChanged);

            // shape
            AddLabel(right, "shape:", 6, y + 3);
            _cmbBShape = new ComboBox { Location = new Point(130, y), Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
            _cmbBShape.Items.AddRange(new object[] { "", "cross", "cutout", "glass", "slab", "slab_top", "stairs" });
            _cmbBShape.SelectedIndexChanged += (s, e) => { if (_suppress || _curBlock < 0) return; SetStr(_blocks[_curBlock], "shape", _cmbBShape.Text); };
            right.Controls.Add(_cmbBShape);
            y += 28;

            // checkboxes grid: 3 per row
            int chkY = y;
            _chkBSolid = AddCheck(right, "solid", 6, chkY, (s, e) => { if (_suppress || _curBlock < 0) return; SetBool(_blocks[_curBlock], "solid", _chkBSolid.Checked); });
            _chkBOpaque = AddCheck(right, "opaque", 130, chkY, (s, e) => { if (_suppress || _curBlock < 0) return; SetBool(_blocks[_curBlock], "opaque", _chkBOpaque.Checked); });
            _chkBTransparent = AddCheck(right, "transparent", 250, chkY, (s, e) => { if (_suppress || _curBlock < 0) return; SetBool(_blocks[_curBlock], "transparent", _chkBTransparent.Checked); });
            chkY += 24;
            _chkBTranslucent = AddCheck(right, "translucent", 6, chkY, (s, e) => { if (_suppress || _curBlock < 0) return; SetBool(_blocks[_curBlock], "translucent", _chkBTranslucent.Checked); });
            _chkBInventory = AddCheck(right, "inventory", 130, chkY, (s, e) => { if (_suppress || _curBlock < 0) return; SetBool(_blocks[_curBlock], "inventory", _chkBInventory.Checked); });
            _chkBPlaceable = AddCheck(right, "placeable", 250, chkY, (s, e) => { if (_suppress || _curBlock < 0) return; SetBool(_blocks[_curBlock], "placeable", _chkBPlaceable.Checked); });
            chkY += 24;
            _chkBGravity = AddCheck(right, "gravity", 6, chkY, (s, e) => { if (_suppress || _curBlock < 0) return; SetBool(_blocks[_curBlock], "gravity", _chkBGravity.Checked); });
            _chkBToolReq = AddCheck(right, "toolRequired", 130, chkY, (s, e) => { if (_suppress || _curBlock < 0) return; SetBool(_blocks[_curBlock], "toolRequired", _chkBToolReq.Checked); });
            _chkBZombieBreak = AddCheck(right, "zombieCanBreak", 250, chkY, (s, e) => { if (_suppress || _curBlock < 0) return; SetBool(_blocks[_curBlock], "zombieCanBreak", _chkBZombieBreak.Checked); });
            chkY += 28;
            y = chkY;

            // hardness
            AddLabel(right, "hardness:", 6, y + 3);
            _numBHardness = new NumericUpDown { Location = new Point(130, y), Width = 90, DecimalPlaces = 2, Increment = 0.1m, Minimum = 0, Maximum = 9999 };
            _numBHardness.ValueChanged += (s, e) => { if (_suppress || _curBlock < 0) return; var v = _numBHardness.Value; if (v == 0) _blocks[_curBlock].Remove("hardness"); else _blocks[_curBlock]["hardness"] = JsonValue.Create((double)v); };
            right.Controls.Add(_numBHardness);
            y += 28;

            // lightEmission
            AddLabel(right, "lightEmission:", 6, y + 3);
            _numBLight = new NumericUpDown { Location = new Point(130, y), Width = 90, Minimum = 0, Maximum = 15 };
            _numBLight.ValueChanged += (s, e) => { if (_suppress || _curBlock < 0) return; int v = (int)_numBLight.Value; if (v == 0) _blocks[_curBlock].Remove("lightEmission"); else _blocks[_curBlock]["lightEmission"] = JsonValue.Create(v); };
            right.Controls.Add(_numBLight);
            y += 28;

            // mapColor
            AddLabel(right, "mapColor:", 6, y + 3);
            _txtBMapColor = new TextBox { Location = new Point(130, y), Width = 90 };
            _txtBMapColor.TextChanged += (s, e) => { if (_suppress || _curBlock < 0) return; SetStr(_blocks[_curBlock], "mapColor", _txtBMapColor.Text.Trim()); };
            right.Controls.Add(_txtBMapColor);
            _btnBMapPick = new Button { Text = "...", Location = new Point(225, y), Size = new Size(30, 23) };
            _btnBMapPick.Click += (s, e) => PickMapColor(_txtBMapColor);
            right.Controls.Add(_btnBMapPick);
            var pnlPreviewB = new Panel { Location = new Point(265, y), Size = new Size(23, 23), BorderStyle = BorderStyle.FixedSingle };
            right.Controls.Add(pnlPreviewB);
            _txtBMapColor.Tag = pnlPreviewB;
            _txtBMapColor.TextChanged += (s, e) => UpdateMapColorPreview(_txtBMapColor);
            y += 28;

            // toolType
            AddLabel(right, "toolType:", 6, y + 3);
            _cmbBToolType = new ComboBox { Location = new Point(130, y), Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
            _cmbBToolType.Items.AddRange(new object[] { "", "pickaxe", "axe", "shovel" });
            _cmbBToolType.SelectedIndexChanged += (s, e) => { if (_suppress || _curBlock < 0) return; SetStr(_blocks[_curBlock], "toolType", _cmbBToolType.Text); };
            right.Controls.Add(_cmbBToolType);
            y += 28;

            // zombieBreakSpeed
            AddLabel(right, "zombieBreakSpeed:", 6, y + 3);
            _cmbBZombieSpeed = new ComboBox { Location = new Point(130, y), Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
            _cmbBZombieSpeed.Items.AddRange(new object[] { "", "Slow", "Medium", "Fast" });
            _cmbBZombieSpeed.SelectedIndexChanged += (s, e) => { if (_suppress || _curBlock < 0) return; SetStr(_blocks[_curBlock], "zombieBreakSpeed", _cmbBZombieSpeed.Text); };
            right.Controls.Add(_cmbBZombieSpeed);
            y += 28;

            // base
            AddLabel(right, "base:", 6, y + 3);
            _cmbBBase = new ComboBox { Location = new Point(130, y), Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
            _cmbBBase.SelectedIndexChanged += (s, e) => { if (_suppress || _curBlock < 0) return; SetStr(_blocks[_curBlock], "base", _cmbBBase.Text); };
            right.Controls.Add(_cmbBBase);
            y += 28;

            // bottom spacer
            right.Controls.Add(new Label { Location = new Point(6, y), Height = 30, AutoSize = false });
        }

        private void BuildItemsTab(TabPage page)
        {
            var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 280, FixedPanel = FixedPanel.Panel1 };
            page.Controls.Add(split);

            var leftTableI = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
            leftTableI.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            leftTableI.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            split.Panel1.Controls.Add(leftTableI);

            var pnlLeftBtns = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Padding = new Padding(4) };
            leftTableI.Controls.Add(pnlLeftBtns, 0, 0);

            _lstItems = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            _lstItems.SelectedIndexChanged += (s, e) => OnItemSelected();
            leftTableI.Controls.Add(_lstItems, 0, 1);
            var btnINew = new Button { Text = "New", Width = 58 };
            var btnIDup = new Button { Text = "Duplicate", Width = 66 };
            var btnIDel = new Button { Text = "Delete", Width = 58 };
            var btnISave = new Button { Text = "Save Items", Width = 80 };
            var btnIReload = new Button { Text = "Reload", Width = 58 };
            btnINew.Click += (s, e) => ItemNew();
            btnIDup.Click += (s, e) => ItemDuplicate();
            btnIDel.Click += (s, e) => ItemDelete();
            btnISave.Click += (s, e) => SaveItems();
            btnIReload.Click += (s, e) => { LoadItems(); RefreshItemList(); RefreshPlacedBlockCombos(); };
            pnlLeftBtns.Controls.Add(btnINew);
            pnlLeftBtns.Controls.Add(btnIDup);
            pnlLeftBtns.Controls.Add(btnIDel);
            pnlLeftBtns.Controls.Add(btnISave);
            pnlLeftBtns.Controls.Add(btnIReload);

            var right = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(8) };
            split.Panel2.Controls.Add(right);

            int y = 8;
            AddLabel(right, "id:", 6, y + 3);
            _txtIId = new TextBox { Location = new Point(130, y), Width = 200 };
            _txtIId.TextChanged += (s, e) => { if (_suppress || _curItem < 0) return; var v = _txtIId.Text.Trim(); _items[_curItem]["id"] = JsonValue.Create(v); if (_curItem >= 0 && _curItem < _lstItems.Items.Count) { _suppress = true; _lstItems.Items[_curItem] = v; _suppress = false; } };
            right.Controls.Add(_txtIId);
            y += 28;

            AddLabel(right, "displayName:", 6, y + 3);
            _txtIDisplay = new TextBox { Location = new Point(130, y), Width = 200 };
            _txtIDisplay.TextChanged += (s, e) => { if (_suppress || _curItem < 0) return; SetStr(_items[_curItem], "displayName", _txtIDisplay.Text); };
            right.Controls.Add(_txtIDisplay);
            y += 28;

            AddLabel(right, "category:", 6, y + 3);
            _cmbICategory = new ComboBox { Location = new Point(130, y), Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
            _cmbICategory.Items.AddRange(new object[] { "", "tool", "food", "misc", "gemstone" });
            _cmbICategory.SelectedIndexChanged += (s, e) => { if (_suppress || _curItem < 0) return; SetStr(_items[_curItem], "category", _cmbICategory.Text); };
            right.Controls.Add(_cmbICategory);
            y += 28;

            _tileIItemTile = AddTileField(right, ref y, "itemTile:", GetItemAtlas, OnItemTileChanged);

            AddLabel(right, "stackSize:", 6, y + 3);
            _numIStack = new NumericUpDown { Location = new Point(130, y), Width = 90, Minimum = 1, Maximum = 9999, Value = 64 };
            _numIStack.ValueChanged += (s, e) => { if (_suppress || _curItem < 0) return; _items[_curItem]["stackSize"] = JsonValue.Create((int)_numIStack.Value); };
            right.Controls.Add(_numIStack);
            y += 28;

            AddLabel(right, "placedBlock:", 6, y + 3);
            _cmbIPlacedBlock = new ComboBox { Location = new Point(130, y), Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
            _cmbIPlacedBlock.SelectedIndexChanged += (s, e) => { if (_suppress || _curItem < 0) return; SetStr(_items[_curItem], "placedBlock", _cmbIPlacedBlock.Text); };
            right.Controls.Add(_cmbIPlacedBlock);
            y += 28;

            AddLabel(right, "toolType:", 6, y + 3);
            _cmbIToolType = new ComboBox { Location = new Point(130, y), Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
            _cmbIToolType.Items.AddRange(new object[] { "", "pickaxe", "axe", "shovel" });
            _cmbIToolType.SelectedIndexChanged += (s, e) => { if (_suppress || _curItem < 0) return; SetStr(_items[_curItem], "toolType", _cmbIToolType.Text); };
            right.Controls.Add(_cmbIToolType);
            y += 28;

            AddLabel(right, "toolLevel:", 6, y + 3);
            _numIToolLevel = new NumericUpDown { Location = new Point(130, y), Width = 90, Minimum = 1, Maximum = 3, Value = 1 };
            _numIToolLevel.ValueChanged += (s, e) => { if (_suppress || _curItem < 0) return; int v = (int)_numIToolLevel.Value; // only save if toolType present? save anyway
                if (_items[_curItem].ContainsKey("toolLevel") || _cmbIToolType.Text != "") _items[_curItem]["toolLevel"] = JsonValue.Create(v);
            };
            right.Controls.Add(_numIToolLevel);
            y += 28;

            AddLabel(right, "durability:", 6, y + 3);
            _numIDurability = new NumericUpDown { Location = new Point(130, y), Width = 90, Minimum = 0, Maximum = 99999 };
            _numIDurability.ValueChanged += (s, e) => { if (_suppress || _curItem < 0) return; int v = (int)_numIDurability.Value; if (v == 0) _items[_curItem].Remove("durability"); else _items[_curItem]["durability"] = JsonValue.Create(v); };
            right.Controls.Add(_numIDurability);
            y += 28;

            AddLabel(right, "foodValue:", 6, y + 3);
            _numIFood = new NumericUpDown { Location = new Point(130, y), Width = 90, Minimum = 0, Maximum = 999 };
            _numIFood.ValueChanged += (s, e) => { if (_suppress || _curItem < 0) return; int v = (int)_numIFood.Value; if (v == 0) _items[_curItem].Remove("foodValue"); else _items[_curItem]["foodValue"] = JsonValue.Create(v); };
            right.Controls.Add(_numIFood);
            y += 28;

            AddLabel(right, "mapColor:", 6, y + 3);
            _txtIMapColor = new TextBox { Location = new Point(130, y), Width = 90 };
            _txtIMapColor.TextChanged += (s, e) => { if (_suppress || _curItem < 0) return; SetStr(_items[_curItem], "mapColor", _txtIMapColor.Text.Trim()); };
            right.Controls.Add(_txtIMapColor);
            _btnIMapPick = new Button { Text = "...", Location = new Point(225, y), Size = new Size(30, 23) };
            _btnIMapPick.Click += (s, e) => PickMapColor(_txtIMapColor);
            right.Controls.Add(_btnIMapPick);
            var pnlPreviewI = new Panel { Location = new Point(265, y), Size = new Size(23, 23), BorderStyle = BorderStyle.FixedSingle };
            right.Controls.Add(pnlPreviewI);
            _txtIMapColor.Tag = pnlPreviewI;
            _txtIMapColor.TextChanged += (s, e) => UpdateMapColorPreview(_txtIMapColor);
            y += 28;

            right.Controls.Add(new Label { Location = new Point(6, y), Height = 10 });
        }

        // helpers for UI
        private static void AddLabel(Panel p, string text, int x, int y)
        {
            p.Controls.Add(new Label { Text = text, AutoSize = true, Location = new Point(x, y) });
        }
        private static CheckBox AddCheck(Panel p, string text, int x, int y, EventHandler handler)
        {
            var chk = new CheckBox { Text = text, AutoSize = true, Location = new Point(x, y) };
            chk.CheckedChanged += handler;
            p.Controls.Add(chk);
            return chk;
        }
        private TileSet AddTileField(Panel parent, ref int y, string label, Func<Image?> getAtlas, Action<TileSet> onChanged)
        {
            AddLabel(parent, label, 6, y + 3);
            var col = new NumericUpDown { Location = new Point(130, y), Width = 55, Minimum = 0, Maximum = 15 };
            var row = new NumericUpDown { Location = new Point(190, y), Width = 55, Minimum = 0, Maximum = 15 };
            var prev = new PictureBox { Location = new Point(255, y), Size = new Size(32, 32), BorderStyle = BorderStyle.FixedSingle, SizeMode = PictureBoxSizeMode.StretchImage };
            var btn = new Button { Text = "Pick", Location = new Point(295, y), Size = new Size(45, 23) };
            parent.Controls.Add(col);
            parent.Controls.Add(row);
            parent.Controls.Add(prev);
            parent.Controls.Add(btn);
            var ts = new TileSet { Col = col, Row = row, Preview = prev, Pick = btn };

            col.ValueChanged += (s, e) => { if (_suppress) return; UpdateTilePreview(ts, getAtlas()); onChanged(ts); };
            row.ValueChanged += (s, e) => { if (_suppress) return; UpdateTilePreview(ts, getAtlas()); onChanged(ts); };
            btn.Click += (s, e) =>
            {
                var atlas = getAtlas();
                if (atlas == null) { MessageBox.Show("Atlas image not found: " + (getAtlas == GetTerrainImg ? _terrainPath : _itemsTexPath)); return; }
                int c = (int)col.Value;
                int r = (int)row.Value;
                using var dlg = new AtlasPickerDialog(atlas, c, r, "Pick " + label.TrimEnd(':'));
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _suppress = true;
                    col.Value = dlg.SelectedCol;
                    row.Value = dlg.SelectedRow;
                    _suppress = false;
                    UpdateTilePreview(ts, atlas);
                    onChanged(ts);
                }
            };
            parent.Controls.Add(new Label { Text = "col", Location = new Point(130, y + 20), AutoSize = true, ForeColor = Color.Gray, Font = new Font(Font.FontFamily, 6) });
            parent.Controls.Add(new Label { Text = "row", Location = new Point(190, y + 20), AutoSize = true, ForeColor = Color.Gray, Font = new Font(Font.FontFamily, 6) });
            y += 38;
            return ts;
        }

        // Atlas getters
        private Image? GetTerrainImg() => _terrainImg;
        private Image? GetItemAtlas() => _itemsImg ?? _terrainImg;
        private Image? GetItemTileImgForBlock() => _itemsImg ?? _terrainImg;

        private void UpdateTilePreview(TileSet ts, Image? atlas)
        {
            if (atlas == null) { ts.Preview.Image = null; return; }
            int c = (int)ts.Col.Value;
            int r = (int)ts.Row.Value;
            SetTilePreview(ts.Preview, atlas, c, r);
        }

        private static void SetTilePreview(PictureBox pb, Image atlas, int col, int row)
        {
            try
            {
                int tile = 16;
                col = Math.Clamp(col, 0, 15);
                row = Math.Clamp(row, 0, 15);
                // if atlas smaller than expected, clamp source rect
                int sx = col * tile;
                int sy = row * tile;
                if (sx + tile > atlas.Width) sx = Math.Max(0, atlas.Width - tile);
                if (sy + tile > atlas.Height) sy = Math.Max(0, atlas.Height - tile);
                var bmp = new Bitmap(tile, tile);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.DrawImage(atlas, new Rectangle(0, 0, tile, tile), new Rectangle(sx, sy, tile, tile), GraphicsUnit.Pixel);
                }
                // scale to 32x32 for preview (2x)
                var scaled = new Bitmap(32, 32);
                using (var g2 = Graphics.FromImage(scaled))
                {
                    g2.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g2.DrawImage(bmp, new Rectangle(0, 0, 32, 32));
                }
                bmp.Dispose();
                var old = pb.Image;
                pb.Image = scaled;
                old?.Dispose();
            }
            catch { }
        }

        // ====================================================================
        // JSON helpers
        // ====================================================================
        private static string StripJsonComments(string json)
        {
            // remove // comments (multiline) and /* */ block comments and trailing commas
            var noLine = Regex.Replace(json, @"(?m)//.*?$", "");
            var noBlock = Regex.Replace(noLine, @"/\*.*?\*/", "", RegexOptions.Singleline);
            var noTrailing = Regex.Replace(noBlock, @",\s*([}\]])", "$1");
            return noTrailing;
        }

        private static string GetStr(JsonObject o, string key)
        {
            if (o.TryGetPropertyValue(key, out var n) && n != null)
            {
                if (n is JsonValue v)
                {
                    try { return v.GetValue<string>(); } catch { }
                    // numbers/bool fallback
                    return v.ToString().Trim('"');
                }
                return n.ToString().Trim('"');
            }
            return "";
        }

        private static void SetStr(JsonObject o, string key, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                o.Remove(key);
            else
                o[key] = JsonValue.Create(value);
        }

        private static void SetBool(JsonObject o, string key, bool value)
        {
            o[key] = JsonValue.Create(value);
        }

        private static bool GetBool(JsonObject o, string key, bool def = false)
        {
            if (o.TryGetPropertyValue(key, out var n) && n != null)
            {
                if (n is JsonValue v)
                {
                    try { return v.GetValue<bool>(); } catch { }
                    var s = v.ToString().ToLowerInvariant();
                    if (s == "true") return true;
                    if (s == "false") return false;
                }
                return def;
            }
            return def;
        }

        private static int GetInt(JsonObject o, string key, int def = 0)
        {
            if (o.TryGetPropertyValue(key, out var n) && n != null)
            {
                if (n is JsonValue v)
                {
                    try { return v.GetValue<int>(); } catch { }
                    try { return (int)v.GetValue<double>(); } catch { }
                    if (int.TryParse(v.ToString(), out var iv)) return iv;
                }
            }
            return def;
        }

        private static double GetDouble(JsonObject o, string key, double def = 0)
        {
            if (o.TryGetPropertyValue(key, out var n) && n != null)
            {
                if (n is JsonValue v)
                {
                    try { return v.GetValue<double>(); } catch { }
                    try { return v.GetValue<int>(); } catch { }
                    if (double.TryParse(v.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dv)) return dv;
                }
            }
            return def;
        }

        private static (int c, int r) ParseTile(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return (0, 0);
            var parts = s.Split(',');
            if (parts.Length >= 2 && int.TryParse(parts[0].Trim(), out var c) && int.TryParse(parts[1].Trim(), out var r))
                return (Math.Clamp(c, 0, 15), Math.Clamp(r, 0, 15));
            return (0, 0);
        }

        // ====================================================================
        // Image loading
        // ====================================================================
        private void LoadImages()
        {
            try
            {
                if (File.Exists(_terrainPath))
                    _terrainImg = Image.FromFile(_terrainPath);
                else
                    _terrainImg = null;
            }
            catch { _terrainImg = null; }
            try
            {
                if (File.Exists(_itemsTexPath))
                    _itemsImg = Image.FromFile(_itemsTexPath);
                else
                    _itemsImg = null;
            }
            catch { _itemsImg = null; }
        }

        // ====================================================================
        // Blocks load / save
        // ====================================================================
        private void LoadBlocks()
        {
            try
            {
                if (!File.Exists(_blocksPath))
                {
                    MessageBox.Show("blocks.json not found:\n" + _blocksPath, "Load Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _blocks = new List<JsonObject>();
                    _hotbar = new List<string>();
                    return;
                }
                string raw = File.ReadAllText(_blocksPath);
                string stripped = StripJsonComments(raw);
                var root = JsonNode.Parse(stripped) as JsonObject;
                if (root == null) throw new Exception("Failed to parse blocks.json");
                _hotbar = new List<string>();
                if (root.TryGetPropertyValue("hotbar", out var hb) && hb is JsonArray hbArr)
                {
                    foreach (var x in hbArr) if (x != null) _hotbar.Add(x.GetValue<string>());
                }
                _blocks = new List<JsonObject>();
                if (root.TryGetPropertyValue("blocks", out var bl) && bl is JsonArray blArr)
                {
                    foreach (var x in blArr) if (x is JsonObject jo) _blocks.Add((JsonObject)jo.DeepClone());
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to load blocks.json:\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _blocks = new List<JsonObject>();
            }
        }

        private void LoadItems()
        {
            try
            {
                if (!File.Exists(_itemsPath))
                {
                    MessageBox.Show("items.json not found:\n" + _itemsPath, "Load Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _items = new List<JsonObject>();
                    return;
                }
                string raw = File.ReadAllText(_itemsPath);
                string stripped = StripJsonComments(raw);
                var root = JsonNode.Parse(stripped) as JsonObject;
                if (root == null) throw new Exception("Failed to parse items.json");
                _items = new List<JsonObject>();
                if (root.TryGetPropertyValue("items", out var it) && it is JsonArray arr)
                {
                    foreach (var x in arr) if (x is JsonObject jo) _items.Add((JsonObject)jo.DeepClone());
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to load items.json:\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _items = new List<JsonObject>();
            }
        }

        private void SaveBlocks()
        {
            try
            {
                if (File.Exists(_blocksPath))
                    File.Copy(_blocksPath, _blocksPath + ".bak", true);

                var root = new JsonObject();
                var hbArr = new JsonArray();
                foreach (var h in _hotbar) hbArr.Add(JsonValue.Create(h));
                root["hotbar"] = hbArr;
                var blArr = new JsonArray();
                foreach (var b in _blocks) blArr.Add(b.DeepClone());
                root["blocks"] = blArr;

                var opts = new JsonSerializerOptions { WriteIndented = true };
                string json = root.ToJsonString(opts);
                File.WriteAllText(_blocksPath, json);
                MessageBox.Show("Blocks saved successfully.", "Save", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to save blocks.json:\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void SaveItems()
        {
            try
            {
                if (File.Exists(_itemsPath))
                    File.Copy(_itemsPath, _itemsPath + ".bak", true);

                var root = new JsonObject();
                var arr = new JsonArray();
                foreach (var it in _items) arr.Add(it.DeepClone());
                root["items"] = arr;

                var opts = new JsonSerializerOptions { WriteIndented = true };
                string json = root.ToJsonString(opts);
                File.WriteAllText(_itemsPath, json);
                MessageBox.Show("Items saved successfully.", "Save", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to save items.json:\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ====================================================================
        // List refresh
        // ====================================================================
        private void RefreshBlockList()
        {
            _suppress = true;
            int sel = _lstBlocks.SelectedIndex;
            _lstBlocks.BeginUpdate();
            _lstBlocks.Items.Clear();
            foreach (var b in _blocks) _lstBlocks.Items.Add(GetStr(b, "id"));
            _lstBlocks.EndUpdate();
            if (sel >= 0 && sel < _lstBlocks.Items.Count) _lstBlocks.SelectedIndex = sel;
            else if (_lstBlocks.Items.Count > 0) _lstBlocks.SelectedIndex = 0;
            else { _curBlock = -1; ClearBlockEditor(); }
            _suppress = false;
        }

        private void RefreshItemList()
        {
            _suppress = true;
            int sel = _lstItems.SelectedIndex;
            _lstItems.BeginUpdate();
            _lstItems.Items.Clear();
            foreach (var it in _items) _lstItems.Items.Add(GetStr(it, "id"));
            _lstItems.EndUpdate();
            if (sel >= 0 && sel < _lstItems.Items.Count) _lstItems.SelectedIndex = sel;
            else if (_lstItems.Items.Count > 0) _lstItems.SelectedIndex = 0;
            else { _curItem = -1; ClearItemEditor(); }
            _suppress = false;
        }

        private void RefreshBaseCombos()
        {
            var ids = _blocks.Select(b => GetStr(b, "id")).Where(s => s != "").ToList();
            _suppress = true;
            string cur = _cmbBBase.Text;
            _cmbBBase.Items.Clear();
            _cmbBBase.Items.Add("");
            foreach (var id in ids) _cmbBBase.Items.Add(id);
            // restore if still exists
            int idx = _cmbBBase.Items.IndexOf(cur);
            _cmbBBase.SelectedIndex = idx >= 0 ? idx : 0;
            _suppress = false;

            // placedBlock for items
            RefreshPlacedBlockCombos();
        }

        private void RefreshPlacedBlockCombos()
        {
            var ids = _blocks.Select(b => GetStr(b, "id")).Where(s => s != "").ToList();
            _suppress = true;
            string cur = _cmbIPlacedBlock.Text;
            _cmbIPlacedBlock.Items.Clear();
            _cmbIPlacedBlock.Items.Add("");
            foreach (var id in ids) _cmbIPlacedBlock.Items.Add(id);
            int idx = _cmbIPlacedBlock.Items.IndexOf(cur);
            if (idx < 0 && _curItem >= 0)
            {
                string v = GetStr(_items[_curItem], "placedBlock");
                idx = _cmbIPlacedBlock.Items.IndexOf(v);
            }
            _cmbIPlacedBlock.SelectedIndex = idx >= 0 ? idx : 0;
            _suppress = false;
        }

        private void ClearBlockEditor()
        {
            _suppress = true;
            _txtBId.Text = "";
            _txtBDisplay.Text = "";
            _cmbBShape.SelectedIndex = 0;
            _cmbBToolType.SelectedIndex = 0;
            _cmbBZombieSpeed.SelectedIndex = 0;
            _cmbBBase.SelectedIndex = 0;
            _numBHardness.Value = 0;
            _numBLight.Value = 0;
            _txtBMapColor.Text = "";
            UpdateMapColorPreview(_txtBMapColor);
            _chkBSolid.Checked = false;
            _chkBOpaque.Checked = false;
            _chkBTransparent.Checked = false;
            _chkBTranslucent.Checked = false;
            _chkBInventory.Checked = false;
            _chkBPlaceable.Checked = false;
            _chkBGravity.Checked = false;
            _chkBToolReq.Checked = false;
            _chkBZombieBreak.Checked = false;
            foreach (var ts in new[] { _tileBTexture, _tileBTop, _tileBBottom, _tileBSide, _tileBItemTile })
            {
                if (ts == null) continue;
                ts.Col.Value = 0;
                ts.Row.Value = 0;
                if (_terrainImg != null) SetTilePreview(ts.Preview, _terrainImg, 0, 0);
            }
            _suppress = false;
        }

        private void ClearItemEditor()
        {
            _suppress = true;
            _txtIId.Text = "";
            _txtIDisplay.Text = "";
            _cmbICategory.SelectedIndex = 0;
            _cmbIPlacedBlock.SelectedIndex = 0;
            _cmbIToolType.SelectedIndex = 0;
            _numIStack.Value = 64;
            _numIToolLevel.Value = 1;
            _numIDurability.Value = 0;
            _numIFood.Value = 0;
            _txtIMapColor.Text = "";
            UpdateMapColorPreview(_txtIMapColor);
            if (_tileIItemTile != null) { _tileIItemTile.Col.Value = 0; _tileIItemTile.Row.Value = 0; }
            _suppress = false;
        }

        // ====================================================================
        // Selection handlers
        // ====================================================================
        private void OnBlockSelected()
        {
            if (_suppress) return;
            int idx = _lstBlocks.SelectedIndex;
            if (idx < 0 || idx >= _blocks.Count) return;
            _curBlock = idx;
            var b = _blocks[idx];
            _suppress = true;

            _txtBId.Text = GetStr(b, "id");
            _txtBDisplay.Text = GetStr(b, "displayName");

            // tiles
            void SetTile(TileSet ts, string key, Image? atlas)
            {
                var s = GetStr(b, key);
                var (c, r) = ParseTile(s);
                ts.Col.Value = c;
                ts.Row.Value = r;
                if (atlas != null) SetTilePreview(ts.Preview, atlas, c, r);
                else ts.Preview.Image = null;
            }
            SetTile(_tileBTexture, "texture", _terrainImg);
            SetTile(_tileBTop, "top", _terrainImg);
            SetTile(_tileBBottom, "bottom", _terrainImg);
            SetTile(_tileBSide, "side", _terrainImg);
            SetTile(_tileBItemTile, "itemTile", GetItemTileImgForBlock());

            // shape
            string shape = GetStr(b, "shape");
            int si = _cmbBShape.Items.IndexOf(shape);
            _cmbBShape.SelectedIndex = si >= 0 ? si : 0;

            // checkboxes - missing => default false except inventory/placeable/solid/opaque true? use GetBool defaults
            // Show checked only if explicitly true; otherwise show false. To be more helpful, use GetBool with true defaults for those.
            // Solid/opaque/inventory/placeable default true for most blocks
            // We'll show true if GetBool with appropriate default is true.
            // Use shape-aware? Simpler: treat missing as true for solid/opaque/inventory/placeable
            _chkBSolid.Checked = GetBool(b, "solid", true);
            // opaque default true unless shape defines otherwise, but we approximate true
            _chkBOpaque.Checked = GetBool(b, "opaque", true);
            _chkBTransparent.Checked = GetBool(b, "transparent", false);
            _chkBTranslucent.Checked = GetBool(b, "translucent", false);
            _chkBInventory.Checked = GetBool(b, "inventory", true);
            _chkBPlaceable.Checked = GetBool(b, "placeable", true);
            _chkBGravity.Checked = GetBool(b, "gravity", false);
            _chkBToolReq.Checked = GetBool(b, "toolRequired", false);
            _chkBZombieBreak.Checked = GetBool(b, "zombieCanBreak", false);

            // hardness
            double hd = GetDouble(b, "hardness", 0);
            _numBHardness.Value = (decimal)Math.Clamp(hd, 0, 9999);
            int le = GetInt(b, "lightEmission", 0);
            _numBLight.Value = Math.Clamp(le, 0, 15);

            _txtBMapColor.Text = GetStr(b, "mapColor");
            UpdateMapColorPreview(_txtBMapColor);

            string tt = GetStr(b, "toolType");
            int tti = _cmbBToolType.Items.IndexOf(tt);
            _cmbBToolType.SelectedIndex = tti >= 0 ? tti : 0;

            string zbs = GetStr(b, "zombieBreakSpeed");
            int zbi = _cmbBZombieSpeed.Items.IndexOf(zbs);
            _cmbBZombieSpeed.SelectedIndex = zbi >= 0 ? zbi : 0;

            string bas = GetStr(b, "base");
            int bi = _cmbBBase.Items.IndexOf(bas);
            _cmbBBase.SelectedIndex = bi >= 0 ? bi : 0;

            _suppress = false;
        }

        private void OnItemSelected()
        {
            if (_suppress) return;
            int idx = _lstItems.SelectedIndex;
            if (idx < 0 || idx >= _items.Count) return;
            _curItem = idx;
            var it = _items[idx];
            _suppress = true;

            _txtIId.Text = GetStr(it, "id");
            _txtIDisplay.Text = GetStr(it, "displayName");

            string cat = GetStr(it, "category");
            int ci = _cmbICategory.Items.IndexOf(cat);
            _cmbICategory.SelectedIndex = ci >= 0 ? ci : 0;

            var s = GetStr(it, "itemTile");
            var (c, r) = ParseTile(s);
            _tileIItemTile.Col.Value = c;
            _tileIItemTile.Row.Value = r;
            var atlas = GetItemAtlas();
            if (atlas != null) SetTilePreview(_tileIItemTile.Preview, atlas, c, r);

            int ss = GetInt(it, "stackSize", 64);
            _numIStack.Value = Math.Clamp(ss, 1, 9999);

            string pb = GetStr(it, "placedBlock");
            int pbi = _cmbIPlacedBlock.Items.IndexOf(pb);
            _cmbIPlacedBlock.SelectedIndex = pbi >= 0 ? pbi : 0;

            string tt = GetStr(it, "toolType");
            int tti = _cmbIToolType.Items.IndexOf(tt);
            _cmbIToolType.SelectedIndex = tti >= 0 ? tti : 0;

            int tl = GetInt(it, "toolLevel", 1);
            _numIToolLevel.Value = Math.Clamp(tl, 1, 3);

            int dur = GetInt(it, "durability", 0);
            _numIDurability.Value = Math.Clamp(dur, 0, 99999);

            int fv = GetInt(it, "foodValue", 0);
            _numIFood.Value = Math.Clamp(fv, 0, 999);

            _txtIMapColor.Text = GetStr(it, "mapColor");
            UpdateMapColorPreview(_txtIMapColor);

            _suppress = false;
        }

        // tile change callbacks
        private void OnBlockTileChanged(TileSet ts)
        {
            if (_suppress || _curBlock < 0) return;
            string key = "";
            if (ts == _tileBTexture) key = "texture";
            else if (ts == _tileBTop) key = "top";
            else if (ts == _tileBBottom) key = "bottom";
            else if (ts == _tileBSide) key = "side";
            else if (ts == _tileBItemTile) key = "itemTile";
            else return;
            string val = $"{(int)ts.Col.Value},{(int)ts.Row.Value}";
            _blocks[_curBlock][key] = JsonValue.Create(val);
        }

        private void OnItemTileChanged(TileSet ts)
        {
            if (_suppress || _curItem < 0) return;
            string val = $"{(int)ts.Col.Value},{(int)ts.Row.Value}";
            _items[_curItem]["itemTile"] = JsonValue.Create(val);
        }

        // ====================================================================
        // Map color picker
        // ====================================================================
        private void PickMapColor(TextBox tb)
        {
            using var dlg = new ColorDialog { FullOpen = true };
            string cur = tb.Text.Trim();
            try
            {
                if (!string.IsNullOrEmpty(cur))
                    dlg.Color = ColorTranslator.FromHtml(cur);
            }
            catch { }
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                string hex = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
                tb.Text = hex;
            }
        }

        private static void UpdateMapColorPreview(TextBox tb)
        {
            if (tb.Tag is Panel pnl)
            {
                string s = tb.Text.Trim();
                try
                {
                    if (!string.IsNullOrEmpty(s))
                        pnl.BackColor = ColorTranslator.FromHtml(s);
                    else
                        pnl.BackColor = SystemColors.Control;
                }
                catch { pnl.BackColor = SystemColors.Control; }
            }
        }

        // ====================================================================
        // Block buttons
        // ====================================================================
        private void BlockNew()
        {
            int n = 0;
            var existing = new HashSet<string>(_blocks.Select(b => GetStr(b, "id")));
            while (existing.Contains($"new_block_{n}")) n++;
            string nid = $"new_block_{n}";
            var jo = new JsonObject
            {
                ["id"] = JsonValue.Create(nid),
                ["displayName"] = JsonValue.Create(nid),
                ["texture"] = JsonValue.Create("0,0"),
                ["mapColor"] = JsonValue.Create("#FF00FF"),
                ["hardness"] = JsonValue.Create(1.0)
            };
            _blocks.Add(jo);
            RefreshBlockList();
            RefreshBaseCombos();
            // select new
            _lstBlocks.SelectedIndex = _blocks.Count - 1;
        }

        private void BlockDuplicate()
        {
            int idx = _lstBlocks.SelectedIndex;
            if (idx < 0 || idx >= _blocks.Count) { MessageBox.Show("Select a block to duplicate."); return; }
            var src = _blocks[idx];
            var clone = (JsonObject)src.DeepClone();
            string baseId = GetStr(clone, "id");
            string nid = baseId + "_copy";
            int suffix = 1;
            var existing = new HashSet<string>(_blocks.Select(b => GetStr(b, "id")));
            string candidate = nid;
            while (existing.Contains(candidate))
            {
                candidate = $"{nid}{suffix}";
                suffix++;
            }
            clone["id"] = JsonValue.Create(candidate);
            // displayName copy
            string dn = GetStr(clone, "displayName");
            if (!string.IsNullOrEmpty(dn)) clone["displayName"] = JsonValue.Create(dn + " Copy");
            _blocks.Add(clone);
            RefreshBlockList();
            RefreshBaseCombos();
            _lstBlocks.SelectedIndex = _blocks.Count - 1;
        }

        private void BlockDelete()
        {
            int idx = _lstBlocks.SelectedIndex;
            if (idx < 0 || idx >= _blocks.Count) { MessageBox.Show("Select a block to delete."); return; }
            string id = GetStr(_blocks[idx], "id");
            var res = MessageBox.Show(
                $"Delete block '{id}' at index {idx}?\n\nWARNING: NEVER reorder or remove entries from the middle of the list.\nNumeric id is list position (0=air). Deleting a middle entry will shift all following ids and corrupt world saves.\n\nOnly delete the LAST entry or you risk save corruption.\n\nContinue?",
                "Delete Block", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (res != DialogResult.Yes) return;
            _blocks.RemoveAt(idx);
            RefreshBlockList();
            RefreshBaseCombos();
            RefreshPlacedBlockCombos();
        }

        // ====================================================================
        // Item buttons
        // ====================================================================
        private void ItemNew()
        {
            int n = 0;
            var existing = new HashSet<string>(_items.Select(it => GetStr(it, "id")));
            // also avoid collision with block ids? not required but check
            while (existing.Contains($"new_item_{n}")) n++;
            string nid = $"new_item_{n}";
            var jo = new JsonObject
            {
                ["id"] = JsonValue.Create(nid),
                ["displayName"] = JsonValue.Create(nid),
                ["category"] = JsonValue.Create("misc"),
                ["itemTile"] = JsonValue.Create("0,0"),
                ["stackSize"] = JsonValue.Create(64),
                ["mapColor"] = JsonValue.Create("#FFFFFF")
            };
            _items.Add(jo);
            RefreshItemList();
            _lstItems.SelectedIndex = _items.Count - 1;
        }

        private void ItemDuplicate()
        {
            int idx = _lstItems.SelectedIndex;
            if (idx < 0 || idx >= _items.Count) { MessageBox.Show("Select an item to duplicate."); return; }
            var src = _items[idx];
            var clone = (JsonObject)src.DeepClone();
            string baseId = GetStr(clone, "id");
            string nid = baseId + "_copy";
            int suffix = 1;
            var existing = new HashSet<string>(_items.Select(it => GetStr(it, "id")));
            string candidate = nid;
            while (existing.Contains(candidate))
            {
                candidate = $"{nid}{suffix}";
                suffix++;
            }
            clone["id"] = JsonValue.Create(candidate);
            string dn = GetStr(clone, "displayName");
            if (!string.IsNullOrEmpty(dn)) clone["displayName"] = JsonValue.Create(dn + " Copy");
            _items.Add(clone);
            RefreshItemList();
            _lstItems.SelectedIndex = _items.Count - 1;
        }

        private void ItemDelete()
        {
            int idx = _lstItems.SelectedIndex;
            if (idx < 0 || idx >= _items.Count) { MessageBox.Show("Select an item to delete."); return; }
            string id = GetStr(_items[idx], "id");
            var res = MessageBox.Show($"Delete item '{id}'?", "Delete Item", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (res != DialogResult.Yes) return;
            _items.RemoveAt(idx);
            RefreshItemList();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _terrainImg?.Dispose();
            _itemsImg?.Dispose();
            base.OnFormClosed(e);
        }
    }
}
