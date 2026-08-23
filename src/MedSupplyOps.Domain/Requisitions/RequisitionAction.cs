namespace MedSupplyOps.Domain.Requisitions;

/// <summary>可對請領單施加的動作。</summary>
public enum RequisitionAction
{
    Submit = 0,
    Approve = 1,
    Reject = 2,
    Issue = 3,
    Close = 4,
}
