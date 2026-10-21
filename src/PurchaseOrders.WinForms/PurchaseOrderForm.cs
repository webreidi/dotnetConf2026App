using System.ComponentModel;
using System.Globalization;
using PurchaseOrders.Client;
using PurchaseOrders.Contracts;

namespace PurchaseOrders.WinForms;

public sealed class PurchaseOrderForm : Form
{
    private readonly PurchaseOrdersApiClient _client;

    private readonly ComboBox _supplierCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox _notesBox = new() { Dock = DockStyle.Fill, MaxLength = 500 };
    private readonly DataGridView _linesGrid = new() { Dock = DockStyle.Fill, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
    private readonly Label _totalLabel = new() { AutoSize = true, Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold), Anchor = AnchorStyles.Right };
    private readonly Button _createButton = new() { Text = "Create draft PO", AutoSize = true };
    private readonly Button _clearButton = new() { Text = "Clear", AutoSize = true };
    private readonly Button _refreshButton = new() { Text = "Refresh", AutoSize = true };
    private readonly DataGridView _recentGrid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
    };
    private readonly ToolStripStatusLabel _statusLabel = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };

    public PurchaseOrderForm(PurchaseOrdersApiClient client)
    {
        _client = client;
        _client.Retrying += (_, e) => BeginInvoke(() =>
            SetStatus($"Request {e.Reason}; retrying ({e.Attempt + 1}/{e.MaxAttempts})..."));

        Text = "Purchase Orders — Draft Entry";
        MinimumSize = new Size(820, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);

        BuildLayout();
        ConfigureLinesGrid();

        _createButton.Click += async (_, _) => await CreateDraftAsync();
        _clearButton.Click += (_, _) => ResetForm();
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        Load += async (_, _) => await InitializeAsync();
    }

    private void BuildLayout()
    {
        var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(8) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.Controls.Add(new Label { Text = "Supplier", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        header.Controls.Add(_supplierCombo, 1, 0);
        header.Controls.Add(new Label { Text = "Notes", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        header.Controls.Add(_notesBox, 1, 1);

        var linesGroup = new GroupBox { Text = "Lines", Dock = DockStyle.Fill, Padding = new Padding(8) };
        linesGroup.Controls.Add(_linesGrid);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8, 0, 8, 8) };
        actions.Controls.Add(_createButton);
        actions.Controls.Add(_clearButton);
        actions.Controls.Add(_totalLabel);

        var entryPanel = new Panel { Dock = DockStyle.Fill };
        entryPanel.Controls.Add(linesGroup);
        entryPanel.Controls.Add(actions);
        entryPanel.Controls.Add(header);

        var recentGroup = new GroupBox { Text = "Recent draft orders", Dock = DockStyle.Fill, Padding = new Padding(8) };
        var recentToolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        recentToolbar.Controls.Add(_refreshButton);
        recentGroup.Controls.Add(_recentGrid);
        recentGroup.Controls.Add(recentToolbar);

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 340 };
        split.Panel1.Controls.Add(entryPanel);
        split.Panel2.Controls.Add(recentGroup);

        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_statusLabel);

        Controls.Add(split);
        Controls.Add(statusStrip);
        AcceptButton = _createButton;
    }

    private void ConfigureLinesGrid()
    {
        _linesGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Sku", HeaderText = "SKU", FillWeight = 20 });
        _linesGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Description", HeaderText = "Description", FillWeight = 45 });
        _linesGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Quantity", HeaderText = "Qty", FillWeight = 10, ValueType = typeof(int) });
        _linesGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "UnitPrice", HeaderText = "Unit price", FillWeight = 12, ValueType = typeof(decimal),
            DefaultCellStyle = { Format = "N2" },
        });
        _linesGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "LineTotal", HeaderText = "Line total", FillWeight = 13, ReadOnly = true,
            DefaultCellStyle = { Format = "N2", BackColor = SystemColors.Control },
        });

        _linesGrid.CellValueChanged += (_, e) =>
        {
            if (e.ColumnIndex != _linesGrid.Columns["LineTotal"]!.Index)
            {
                RecalculateTotals();
            }
        };
        _linesGrid.RowsRemoved += (_, _) => RecalculateTotals();
        _linesGrid.DataError += (_, e) =>
        {
            SetStatus($"Invalid value in {_linesGrid.Columns[e.ColumnIndex].HeaderText}.");
            e.ThrowException = false;
        };
        RecalculateTotals();
    }

    private async Task InitializeAsync()
    {
        _recentGrid.DataSource = new BindingList<PurchaseOrderSummaryDto>();
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        // Suppliers may have failed to load at startup; retry so users can recover without restarting.
        if (_supplierCombo.DataSource is null)
        {
            await LoadSuppliersAsync();
        }

        await LoadRecentOrdersAsync();
    }

    private async Task LoadSuppliersAsync()
    {
        await RunBusyAsync("Loading suppliers...", async () =>
        {
            var suppliers = await _client.GetSuppliersAsync();
            _supplierCombo.DisplayMember = nameof(SupplierDto.Name);
            _supplierCombo.ValueMember = nameof(SupplierDto.Code);
            _supplierCombo.DataSource = suppliers.ToList();
            SetStatus($"Connected. {suppliers.Count} active suppliers.");
        });
    }

    private async Task LoadRecentOrdersAsync()
    {
        await RunBusyAsync("Loading recent orders...", async () =>
        {
            var orders = await _client.ListPurchaseOrdersAsync();
            _recentGrid.DataSource = new BindingList<PurchaseOrderSummaryDto>(orders.ToList());
            if (_recentGrid.Columns[nameof(PurchaseOrderSummaryDto.Id)] is { } idColumn)
            {
                idColumn.Visible = false;
            }
            if (_recentGrid.Columns[nameof(PurchaseOrderSummaryDto.Total)] is { } totalColumn)
            {
                totalColumn.DefaultCellStyle.Format = "N2";
            }
            SetStatus($"Loaded {orders.Count} draft orders.");
        });
    }

    private async Task CreateDraftAsync()
    {
        if (_supplierCombo.SelectedValue is not string supplierCode)
        {
            SetStatus("Choose a supplier first.");
            return;
        }

        if (!TryReadLines(out var lines, out var error))
        {
            SetStatus(error);
            return;
        }

        var request = new CreatePurchaseOrderRequest(supplierCode, _notesBox.Text, lines);

        await RunBusyAsync("Creating draft purchase order...", async () =>
        {
            try
            {
                var order = await _client.CreateDraftAsync(request);
                SetStatus($"Draft {order.OrderNumber} created for {order.SupplierName} — total {order.Total:N2}.");
                ResetForm(keepStatus: true);
                await LoadRecentOrdersAsync();
            }
            catch (PurchaseOrderValidationException ex)
            {
                var details = string.Join(Environment.NewLine, ex.Errors.SelectMany(e => e.Value.Select(v => $"• {e.Key}: {v}")));
                MessageBox.Show(this, details, "Please fix the purchase order", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                SetStatus("Validation failed.");
            }
        });
    }

    private bool TryReadLines(out List<PurchaseOrderLineRequest> lines, out string error)
    {
        lines = [];
        error = string.Empty;

        foreach (DataGridViewRow row in _linesGrid.Rows)
        {
            if (row.IsNewRow)
            {
                continue;
            }

            var sku = Convert.ToString(row.Cells["Sku"].Value)?.Trim() ?? string.Empty;
            var description = Convert.ToString(row.Cells["Description"].Value)?.Trim() ?? string.Empty;
            var quantity = row.Cells["Quantity"].Value as int? ?? 0;
            var unitPrice = row.Cells["UnitPrice"].Value as decimal? ?? 0m;

            if (sku.Length == 0 && description.Length == 0 && quantity == 0 && unitPrice == 0)
            {
                continue;
            }

            lines.Add(new PurchaseOrderLineRequest(sku, description, quantity, unitPrice));
        }

        if (lines.Count == 0)
        {
            error = "Add at least one line.";
            return false;
        }

        return true;
    }

    private void RecalculateTotals()
    {
        decimal total = 0;
        var overflowed = false;
        foreach (DataGridViewRow row in _linesGrid.Rows)
        {
            if (row.IsNewRow)
            {
                continue;
            }

            var quantity = row.Cells["Quantity"].Value as int? ?? 0;
            var unitPrice = row.Cells["UnitPrice"].Value as decimal? ?? 0m;
            decimal lineTotal;
            try
            {
                lineTotal = decimal.Round(quantity * unitPrice, 2, MidpointRounding.AwayFromZero);
            }
            catch (OverflowException)
            {
                row.Cells["LineTotal"].Value = null;
                overflowed = true;
                continue;
            }

            row.Cells["LineTotal"].Value = lineTotal;
            if (overflowed)
            {
                continue;
            }

            try
            {
                total = checked(total + lineTotal);
            }
            catch (OverflowException)
            {
                overflowed = true;
            }
        }

        _totalLabel.Text = overflowed
            ? "Total: invalid (amount too large)"
            : string.Create(CultureInfo.CurrentCulture, $"Total: {total:N2}");
    }

    private void ResetForm(bool keepStatus = false)
    {
        _linesGrid.Rows.Clear();
        _notesBox.Clear();
        RecalculateTotals();
        if (!keepStatus)
        {
            SetStatus("Ready.");
        }
    }

    private async Task RunBusyAsync(string message, Func<Task> action)
    {
        UseWaitCursor = true;
        SetInputsEnabled(false);
        SetStatus(message);
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
        {
            SetStatus($"Error: {ex.Message}");
        }
        finally
        {
            SetInputsEnabled(true);
            UseWaitCursor = false;
        }
    }

    // Lock the draft while a request is in flight so edits aren't silently lost when ResetForm runs.
    private void SetInputsEnabled(bool enabled)
    {
        _createButton.Enabled = _refreshButton.Enabled = _clearButton.Enabled = enabled;
        _supplierCombo.Enabled = enabled;
        _notesBox.ReadOnly = !enabled;
        _linesGrid.ReadOnly = !enabled;
        _linesGrid.AllowUserToAddRows = enabled;
        _linesGrid.AllowUserToDeleteRows = enabled;
        if (enabled)
        {
            _linesGrid.Columns["LineTotal"]!.ReadOnly = true;
        }
    }

    private void SetStatus(string message) => _statusLabel.Text = message;
}
