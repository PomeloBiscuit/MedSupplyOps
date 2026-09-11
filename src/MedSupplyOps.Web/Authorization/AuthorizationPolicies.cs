namespace MedSupplyOps.Web.Authorization;

public static class AuthorizationPolicies
{
    public const string Authenticated = nameof(Authenticated);
    public const string InventoryRead = nameof(InventoryRead);
    public const string RequisitionRead = nameof(RequisitionRead);
    public const string RequisitionCreate = nameof(RequisitionCreate);
    public const string RequisitionReview = nameof(RequisitionReview);
    public const string RequisitionIssue = nameof(RequisitionIssue);
    public const string FhirRead = nameof(FhirRead);
    public const string ItemManage = nameof(ItemManage);
    public const string StockReceive = nameof(StockReceive);
}

public static class ApplicationRoles
{
    public const string Requester = nameof(Requester);
    public const string Storekeeper = nameof(Storekeeper);
    public const string Administrator = nameof(Administrator);
}
