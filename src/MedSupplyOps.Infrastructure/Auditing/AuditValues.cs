using System.Text.Json;

namespace MedSupplyOps.Infrastructure.Auditing;

public static class AuditValues
{
    public const string RequisitionEntity = "Requisition";
    public const string CreateAction = "Create";
    public const string ApproveAction = "Approve";
    public const string RejectAction = "Reject";
    public const string IssueAction = "Issue";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string ToJson<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
}
