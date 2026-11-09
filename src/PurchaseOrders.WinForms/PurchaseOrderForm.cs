using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using PurchaseOrders.Client;
using PurchaseOrders.Contracts;

namespace PurchaseOrders.WinForms;

public sealed class PurchaseOrderForm : Form
{
    private readonly PurchaseOrdersApiClient _client;
    private Guid _submissionId;
    private string? _submissionPayload;

    private readonly ComboBox _supplierCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox _notesBox = new() { Dock = DockStyle.Fill, MaxLength = 500 };
    private readonly DataGridView _linesGrid = new() { Dock = DockStyle.Fill, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
    private readonly Label _totalLabel = new() { AutoSize = true, Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold), Anchor = AnchorStyles.Right };
    private readonly Button _createButton = new() { Text = "Create draft PO", AutoSize = true };
    private readonly Button _clearButton = new() { Text = "Clear", AutoSize = true };
    private readonly Button _refreshButton = new() { Text = "Refresh", AutoSize = true };
    private readonly Button _generateReportButton = new() { Text = "Generate Duplicate Records Report", AutoSize = true, Enabled = false };
    private readonly DataGridView _recentGrid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = true,
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
        _generateReportButton.Click += async (_, _) => await GenerateReportAsync();
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
        recentToolbar.Controls.Add(_generateReportButton);
        recentToolbar.Controls.Add(new Label
        {
            Text = "Report checks all saved orders; no selection needed.",
            AutoSize = true,
            Margin = new Padding(8, 8, 0, 0),
        });
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
            await LoadRecentOrdersCoreAsync();
        });
    }

    private async Task<int> LoadRecentOrdersCoreAsync()
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
        return orders.Count;
    }

    private async Task GenerateReportAsync()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Save duplicate purchase orders report",
            Filter = "Markdown document (*.md)|*.md",
            DefaultExt = "md",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = "duplicate-purchase-orders.md",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            SetStatus("Report generation canceled.");
            return;
        }

        await RunBusyAsync("Checking all saved orders for duplicates...", async () =>
        {
            try
            {
                var report = await PurchaseOrderReportGenerator.GenerateDuplicatesAsync(_client);
                // Stage beside the destination so a failed write cannot truncate an existing document.
                var temporaryPath = Path.Combine(Path.GetDirectoryName(dialog.FileName)!, $".{Guid.NewGuid():N}.tmp");
                try
                {
                    await File.WriteAllTextAsync(temporaryPath, report);
                    File.Move(temporaryPath, dialog.FileName, overwrite: true);
                }
                finally
                {
                    File.Delete(temporaryPath);
                }
                SetStatus($"Saved duplicate purchase orders report to {dialog.FileName}. Drafts have not been sent to suppliers.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
            {
                SetStatus($"Report generation failed: {ex.Message}");
                MessageBox.Show(this, ex.Message, "Report generation failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        });
    }

    private async Task CreateDraftAsync()
    {
        if (!_createButton.Enabled || !_linesGrid.EndEdit())
        {
            return;
        }

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
        var payload = JsonSerializer.Serialize(request);
        if (!string.Equals(payload, _submissionPayload, StringComparison.Ordinal))
        {
            _submissionId = Guid.NewGuid();
            _submissionPayload = payload;
        }

        await RunBusyAsync("Creating draft purchase order...", async () =>
        {
            PurchaseOrderDto order;
            try
            {
                order = await _client.CreateDraftAsync(_submissionId, request);
            }
            catch (PurchaseOrderValidationException ex)
            {
                var details = string.Join(Environment.NewLine, ex.Errors.SelectMany(e => e.Value.Select(v => $"• {e.Key}: {v}")));
                MessageBox.Show(this, details, "Please fix the purchase order", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                SetStatus("Validation failed; refreshing recent orders...");
                try
                {
                    await LoadRecentOrdersCoreAsync();
                    SetStatus("Validation failed. Recent orders refreshed.");
                }
                catch (Exception refreshException) when (refreshException is HttpRequestException or TimeoutException or System.Text.Json.JsonException or InvalidOperationException)
                {
                    SetStatus($"Validation failed; recent-order refresh failed: {refreshException.Message}");
                }

                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or System.Text.Json.JsonException or InvalidOperationException)
            {
                SetStatus("Submission was not confirmed; refreshing recent orders...");
                var refreshFailure = string.Empty;
                try
                {
                    var count = await LoadRecentOrdersCoreAsync();
                    SetStatus($"Submission was not confirmed. Refreshed {count} recent orders; retry the unchanged form to confirm the same submission.");
                }
                catch (Exception refreshException) when (refreshException is HttpRequestException or TimeoutException or System.Text.Json.JsonException or InvalidOperationException)
                {
                    refreshFailure = $" Recent orders could not be refreshed: {refreshException.Message}";
                    SetStatus($"Submission was not confirmed; recent-order refresh failed: {refreshException.Message}");
                }

                MessageBox.Show(
                    this,
                    $"The purchase order could not be confirmed: {ex.Message}{Environment.NewLine}{Environment.NewLine}The server may still have created it. Retry without changing the form to confirm the same submission. Editing or clearing the form starts a new submission.{refreshFailure}",
                    "Submission not confirmed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            SetStatus($"Draft {order.OrderNumber} created for {order.SupplierName} — total {order.Total:N2}.");
            ResetForm(keepStatus: true);
            await LoadRecentOrdersCoreAsync();
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
        _submissionId = Guid.Empty;
        _submissionPayload = null;
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
        _recentGrid.Enabled = enabled;
        UpdateReportButton();
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

    private void UpdateReportButton() =>
        _generateReportButton.Enabled = _refreshButton.Enabled;

    private void SetStatus(string message) => _statusLabel.Text = message;
}
