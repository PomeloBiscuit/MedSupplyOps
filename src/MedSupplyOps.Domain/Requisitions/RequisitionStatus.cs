namespace MedSupplyOps.Domain.Requisitions;

/// <summary>請領單狀態。對應 FR-304。</summary>
public enum RequisitionStatus
{
    /// <summary>草稿：尚未送出，可自由編輯明細。</summary>
    Draft = 0,

    /// <summary>待審核：已送出，等庫管員處理。</summary>
    PendingApproval = 1,

    /// <summary>已核准：可以發料。</summary>
    Approved = 2,

    /// <summary>已駁回：終態。</summary>
    Rejected = 3,

    /// <summary>已發料：庫存已扣。</summary>
    Issued = 4,

    /// <summary>已結案：終態。</summary>
    Closed = 5,
}
