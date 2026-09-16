using System.Text.Json;

namespace MedSupplyOps.Infrastructure.Auditing;

public static class AuditValues
{
    public const string ItemEntity = "Item";
    public const string RequisitionEntity = "Requisition";
    public const string StockLotEntity = "StockLot";
    public const string UserEntity = "User";
    public const string CreateAction = "Create";
    public const string UpdateAction = "Update";
    public const string DeleteAction = "Delete";
    public const string ApproveAction = "Approve";
    public const string RejectAction = "Reject";
    public const string IssueAction = "Issue";
    public const string ReceiveAction = "Receive";
    public const string DisableAction = "Disable";
    public const string EnableAction = "Enable";
    public const string ResetPasswordAction = "ResetPassword";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string ToJson<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
}
