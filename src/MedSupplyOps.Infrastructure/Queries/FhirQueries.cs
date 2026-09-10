using System.Data.Common;
using Dapper;

namespace MedSupplyOps.Infrastructure.Queries;

/// <summary>
/// FHIR 唯讀介接的資料來源。這裡只回傳一般資料列；FHIR POCO 與序列化留在 Web 介接層。
/// </summary>
public sealed class FhirQueries
{
    private readonly DbConnection _connection;

    public FhirQueries(DbConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    public async Task<FhirSupplyRequestData?> GetSupplyRequestAsync(
        long requisitionLineId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT rl.requisition_line_id AS RequisitionLineId,
                   r.requisition_no AS RequisitionNo,
                   r.status AS RequisitionStatus,
                   i.item_code AS ItemCode,
                   i.item_name AS ItemName,
                   i.specification AS Specification,
                   i.unit_of_measure AS UnitOfMeasure,
                   rl.quantity AS Quantity,
                   r.created_at AS CreatedAt
            FROM requisition_lines rl
            INNER JOIN requisitions r ON r.requisition_id = rl.requisition_id
            INNER JOIN items i ON i.item_id = rl.item_id
            WHERE rl.requisition_line_id = :requisitionLineId
            """;

        await EnsureOpenAsync(cancellationToken);
        var command = new CommandDefinition(sql, new { requisitionLineId }, cancellationToken: cancellationToken);
        var row = await _connection.QuerySingleOrDefaultAsync<FhirSupplyRequestRow>(command);
        return row is null ? null : ToSupplyRequestData(row);
    }

    public async Task<IReadOnlyList<FhirSupplyRequestData>> SearchSupplyRequestsByRequisitionNoAsync(
        string requisitionNo,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT rl.requisition_line_id AS RequisitionLineId,
                   r.requisition_no AS RequisitionNo,
                   r.status AS RequisitionStatus,
                   i.item_code AS ItemCode,
                   i.item_name AS ItemName,
                   i.specification AS Specification,
                   i.unit_of_measure AS UnitOfMeasure,
                   rl.quantity AS Quantity,
                   r.created_at AS CreatedAt
            FROM requisitions r
            INNER JOIN requisition_lines rl ON rl.requisition_id = r.requisition_id
            INNER JOIN items i ON i.item_id = rl.item_id
            WHERE r.requisition_no = :requisitionNo
            ORDER BY rl.line_no, rl.requisition_line_id
            """;

        await EnsureOpenAsync(cancellationToken);
        var command = new CommandDefinition(sql, new { requisitionNo }, cancellationToken: cancellationToken);
        return (await _connection.QueryAsync<FhirSupplyRequestRow>(command))
            .Select(ToSupplyRequestData)
            .ToList();
    }

    public async Task<FhirSupplyDeliveryData?> GetSupplyDeliveryAsync(
        long issueAllocationId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT a.issue_allocation_id AS IssueAllocationId,
                   a.requisition_line_id AS RequisitionLineId,
                   a.quantity AS Quantity,
                   a.expiry_date_at_issue AS ExpiryDateAtIssue,
                   a.issued_at AS IssuedAt,
                   i.item_code AS ItemCode,
                   i.item_name AS ItemName,
                   i.specification AS Specification,
                   i.unit_of_measure AS UnitOfMeasure,
                   l.lot_number AS LotNumber
            FROM issue_allocations a
            INNER JOIN requisition_lines rl ON rl.requisition_line_id = a.requisition_line_id
            INNER JOIN items i ON i.item_id = rl.item_id
            INNER JOIN stock_lots l ON l.stock_lot_id = a.stock_lot_id
            WHERE a.issue_allocation_id = :issueAllocationId
            """;

        await EnsureOpenAsync(cancellationToken);
        var command = new CommandDefinition(sql, new { issueAllocationId }, cancellationToken: cancellationToken);
        var row = await _connection.QuerySingleOrDefaultAsync<FhirSupplyDeliveryRow>(command);
        return row is null ? null : ToSupplyDeliveryData(row);
    }

    public async Task<IReadOnlyList<FhirSupplyDeliveryData>> SearchSupplyDeliveriesBySupplyRequestAsync(
        long requisitionLineId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT a.issue_allocation_id AS IssueAllocationId,
                   a.requisition_line_id AS RequisitionLineId,
                   a.quantity AS Quantity,
                   a.expiry_date_at_issue AS ExpiryDateAtIssue,
                   a.issued_at AS IssuedAt,
                   i.item_code AS ItemCode,
                   i.item_name AS ItemName,
                   i.specification AS Specification,
                   i.unit_of_measure AS UnitOfMeasure,
                   l.lot_number AS LotNumber
            FROM issue_allocations a
            INNER JOIN requisition_lines rl ON rl.requisition_line_id = a.requisition_line_id
            INNER JOIN items i ON i.item_id = rl.item_id
            INNER JOIN stock_lots l ON l.stock_lot_id = a.stock_lot_id
            WHERE a.requisition_line_id = :requisitionLineId
            ORDER BY a.expiry_date_at_issue, l.lot_number, a.issue_allocation_id
            """;

        await EnsureOpenAsync(cancellationToken);
        var command = new CommandDefinition(sql, new { requisitionLineId }, cancellationToken: cancellationToken);
        return (await _connection.QueryAsync<FhirSupplyDeliveryRow>(command))
            .Select(ToSupplyDeliveryData)
            .ToList();
    }

    private static FhirSupplyRequestData ToSupplyRequestData(FhirSupplyRequestRow row)
        => new(
            decimal.ToInt64(row.RequisitionLineId),
            row.RequisitionNo,
            row.RequisitionStatus,
            row.ItemCode,
            row.ItemName,
            row.Specification,
            row.UnitOfMeasure,
            decimal.ToInt32(row.Quantity),
            row.CreatedAt);

    private static FhirSupplyDeliveryData ToSupplyDeliveryData(FhirSupplyDeliveryRow row)
        => new(
            decimal.ToInt64(row.IssueAllocationId),
            decimal.ToInt64(row.RequisitionLineId),
            row.ItemCode,
            row.ItemName,
            row.Specification,
            row.UnitOfMeasure,
            row.LotNumber,
            decimal.ToInt32(row.Quantity),
            DateOnly.FromDateTime(row.ExpiryDateAtIssue),
            row.IssuedAt);

    private async Task EnsureOpenAsync(CancellationToken cancellationToken)
    {
        if (_connection.State != System.Data.ConnectionState.Open)
        {
            await _connection.OpenAsync(cancellationToken);
        }
    }
}

internal sealed class FhirSupplyRequestRow
{
    public decimal RequisitionLineId { get; set; }
    public string RequisitionNo { get; set; } = string.Empty;
    public string RequisitionStatus { get; set; } = string.Empty;
    public string ItemCode { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public string? Specification { get; set; }
    public string UnitOfMeasure { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public DateTime CreatedAt { get; set; }
}

internal sealed class FhirSupplyDeliveryRow
{
    public decimal IssueAllocationId { get; set; }
    public decimal RequisitionLineId { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public string? Specification { get; set; }
    public string UnitOfMeasure { get; set; } = string.Empty;
    public string LotNumber { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public DateTime ExpiryDateAtIssue { get; set; }
    public DateTime IssuedAt { get; set; }
}

public sealed record FhirSupplyRequestData(
    long RequisitionLineId,
    string RequisitionNo,
    string RequisitionStatus,
    string ItemCode,
    string ItemName,
    string? Specification,
    string UnitOfMeasure,
    int Quantity,
    DateTime CreatedAt);

public sealed record FhirSupplyDeliveryData(
    long IssueAllocationId,
    long RequisitionLineId,
    string ItemCode,
    string ItemName,
    string? Specification,
    string UnitOfMeasure,
    string LotNumber,
    int Quantity,
    DateOnly ExpiryDateAtIssue,
    DateTime IssuedAt);
